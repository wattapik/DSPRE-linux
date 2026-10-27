using System.Buffers.Binary;

namespace DSPRE.Core;

/// <summary>Edits the notes of a sequence file (SSEQ) in place. A note is the command <c>key velocity length</c>; changing a length that needs
/// more bytes than the old one moves everything after it, so the jump, call and track-start targets that lie beyond are rewritten.</summary>
public static class SequenceEditor
{
    public const int MaxLength = 65535;

    /// <param name="Offset">The note to edit; for a new note, the existing note it is placed relative to.</param>
    /// <param name="Key">The stored key (the sounding key minus the track's transpose).</param>
    /// <param name="Waits">Whether the note's track waits for notes; a deleted note on such a track becomes a rest of the same length.</param>
    /// <param name="Shift">Ticks to move the note later (or earlier when negative), leaving every other note where it is; for a new note, where it starts relative to the note at <paramref name="Offset"/>.</param>
    /// <param name="Program">The instrument to play the note with (null keeps it); <paramref name="CurrentProgram"/> is what it plays with now, which the track gets back after the note.</param>
    /// <param name="Create">Adds a note instead of editing one.</param>
    public sealed record NoteOp(int Offset, int Key, int Velocity, int Length, bool Delete = false, bool Waits = true, int Shift = 0, int? Program = null, int CurrentProgram = 0, bool Create = false);

    /// <param name="Map">Where an offset of the old data sits in the new data.</param>
    /// <param name="NoteOffset">Where the edited or new note sits in the new data.</param>
    public sealed record Result(byte[] File, Func<int, int> Map, int NoteOffset);

    /// <summary>Where the sequence data starts in the file.</summary>
    public static int PayloadStart(byte[] sseq)
    {
        if (sseq.Length < 0x1C || sseq[0] != 'S' || sseq[1] != 'S' || sseq[2] != 'E' || sseq[3] != 'Q') throw new InvalidDataException("Not a sequence (SSEQ).");
        return (int)BinaryPrimitives.ReadUInt32LittleEndian(sseq.AsSpan(0x18));
    }

    public static byte[] Apply(byte[] sseq, NoteOp op) => ApplyDetailed(sseq, op).File;

    public static Result ApplyDetailed(byte[] sseq, NoteOp op)
    {
        int start = PayloadStart(sseq);
        byte[] data = sseq.AsSpan(start).ToArray();
        if (op.Offset < 0 || op.Offset + 3 > data.Length || data[op.Offset] >= 0x80) throw new InvalidDataException($"There is no note at offset {op.Offset}.");
        Func<int, int> map = offset => offset;
        int note = op.Offset;

        if (op.Delete)
        {
            RefuseIfConditional(data, op.Offset);
            int width = VariableWidth(data, op.Offset + 2);
            data = op.Waits
                ? Splice(data, op.Offset, 2 + width, [0x80, .. EncodeVariable(ReadVariable(data, op.Offset + 2), width + 1)])
                : Splice(data, op.Offset, 2 + width, []);
            return new Result(Assemble(sseq, start, data), map, -1);
        }

        if (op.Key is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(op), "A key is 0 to 127.");
        if (op.Velocity is < 0 or > 127) throw new ArgumentOutOfRangeException(nameof(op), "A velocity is 0 to 127.");
        if (op.Length is < 1 or > MaxLength) throw new ArgumentOutOfRangeException(nameof(op), $"A length is 1 to {MaxLength} ticks.");

        if (op.Create)
        {
            byte[] bytes = [(byte)op.Key, (byte)op.Velocity, .. EncodeVariable(op.Length, MinimumWidth(op.Length))];
            (data, note, map) = Relocate(data, op.Offset, op.Shift, bytes, op.Waits, op.Waits ? op.Length : 0, moving: false);
        }
        else
        {
            int width = VariableWidth(data, op.Offset + 2);
            int lengthAt = op.Offset + 2;
            data[op.Offset] = (byte)op.Key;
            data[op.Offset + 1] = (byte)op.Velocity;
            byte[] length = EncodeVariable(op.Length, Math.Max(width, MinimumWidth(op.Length)));
            data = length.Length == width ? Overwrite(data, lengthAt, length) : Splice(data, lengthAt, width, length);
            int grown = length.Length - width;
            if (grown != 0) map = offset => offset >= lengthAt + width ? offset + grown : offset;
            if (op.Shift != 0)
            {
                int noteWidth = VariableWidth(data, op.Offset + 2);
                byte[] bytes = data.AsSpan(op.Offset, 2 + noteWidth).ToArray();
                (data, note, Func<int, int> moved) = Relocate(data, op.Offset, op.Shift, bytes, op.Waits, op.Waits ? op.Length : 0, moving: true);
                Func<int, int> before = map;
                map = offset => moved(before(offset));
            }
        }

        if (op.Program is { } program && program != op.CurrentProgram)
        {
            if (program < 0) throw new ArgumentOutOfRangeException(nameof(op), "An instrument number is 0 or more.");
            int noteBefore = note;
            (data, note, int inserted, int restored) = WrapProgram(data, note, program, op.CurrentProgram);
            Func<int, int> earlier = map;
            map = offset => earlier(offset) is int mapped && mapped > noteBefore ? mapped + inserted + restored : mapped;
        }

        return new Result(Assemble(sseq, start, data), map, note);
    }

    private static void RefuseIfConditional(byte[] data, int note)
    {
        SortedDictionary<int, int> commands = CommandStarts(data);
        if (commands.Any(command => command.Key + command.Value == note && data[command.Key] == 0xA2))
        {
            throw new NotSupportedException("This note only plays under a condition (an 'if' in front of it), which the editor cannot keep when it changes the note's place or instrument.");
        }
    }

    private static (byte[] Data, int Note, int Inserted, int Restored) WrapProgram(byte[] data, int note, int program, int restore)
    {
        RefuseIfConditional(data, note);
        int noteLength = 2 + VariableWidth(data, note + 2);
        var commands = CommandStarts(data);
        int before = commands.Keys.Where(position => position < note && position + commands[position] == note).DefaultIfEmpty(-1).Max();
        int after = note + noteLength;
        if (before >= 0 && data[before] == 0x81 && after < data.Length && data[after] == 0x81 && !IsTarget(data, note) && !IsTarget(data, after))
        {
            byte[] replacement = [0x81, .. EncodeVariable(program, MinimumWidth(program))];
            int change = replacement.Length - commands[before];
            return (Splice(data, before, commands[before], replacement), note + change, change, 0);
        }

        byte[] restoreBytes = [0x81, .. EncodeVariable(restore, MinimumWidth(restore))];
        data = Splice(data, after, 0, restoreBytes);
        byte[] programBytes = [0x81, .. EncodeVariable(program, MinimumWidth(program))];
        data = Splice(data, note, 0, programBytes, keepTargetsAtStart: true);
        return (data, note + programBytes.Length, programBytes.Length, restoreBytes.Length);
    }

    private static bool IsTarget(byte[] data, int position) =>
        FindOffsetOperands(data).Any(operand => (data[operand] | (data[operand + 1] << 8) | (data[operand + 2] << 16)) == position);

    private sealed class Item
    {
        public bool IsRest;
        public bool IsNote;
        public int Time;
        public byte[] Bytes = [];
    }

    /// <summary>Commands that make a stretch of a track something this editor will not move a note across (they leave the straight line,
    /// change the note-wait setting, or carry a target of their own).</summary>
    private static bool Stops(byte command) => command is 0x93 or 0x94 or 0x95 or 0xA0 or 0xA1 or 0xA2 or 0xC7 or 0xD4 or 0xFC or 0xFD or 0xFE or 0xFF;

    private static Item ReadItem(byte[] data, int position, int length, bool waits)
    {
        byte command = data[position];
        if (Stops(command)) throw new NotSupportedException("The note cannot be moved that far: the track loops, jumps, calls or changes its note-wait setting in between.");
        var bytes = data.AsSpan(position, length).ToArray();
        if (command == 0x80) return new Item { IsRest = true, Time = ReadVariable(data, position + 1), Bytes = bytes };
        if (command < 0x80) return new Item { IsNote = true, Time = waits ? ReadVariable(data, position + 2) : 0, Bytes = bytes };
        return new Item { Bytes = bytes };
    }

    /// <summary>Puts a note at <paramref name="shift"/> ticks from the note at <paramref name="anchor"/> (taking it out of there first when
    /// <paramref name="moving"/>) and keeps every other note at its time: the rests around it are split and shortened to make room.
    /// A note on a track that waits for notes needs rests where it goes, since it then holds the track for its whole length.</summary>
    private static (byte[] Data, int Note, Func<int, int> Map) Relocate(byte[] data, int anchor, int shift, byte[] noteBytes, bool waits, int duration, bool moving)
    {
        SortedDictionary<int, int> commands = CommandStarts(data);
        if (!commands.TryGetValue(anchor, out int anchorLength)) throw new NotSupportedException("This note is not on a track the editor can follow.");
        RefuseIfConditional(data, anchor);
        var items = new List<Item>();
        int regionStart = anchor, regionEnd;
        int target;

        if (shift >= 0)
        {
            if (moving && waits) items.Add(new Item { IsRest = true, Time = duration });
            int position = moving ? anchor + anchorLength : anchor;
            int total = items.Sum(item => item.Time);
            target = shift;
            int need = shift + duration;
            while (total < need)
            {
                if (!commands.TryGetValue(position, out int length)) throw new NotSupportedException("The note cannot be moved that far: the track ends first.");
                Item item = ReadItem(data, position, length, waits);
                items.Add(item);
                total += item.Time;
                position += length;
            }

            regionEnd = position;
            if (!moving && items.Count == 0) regionEnd = anchor;
        }
        else
        {
            int position = anchor, total = 0;
            var before = new List<Item>();
            while (total < -shift)
            {
                int previous = commands.Keys.Where(start => start < position && start + commands[start] == position).DefaultIfEmpty(-1).Max();
                if (previous < 0) throw new NotSupportedException("The note cannot be moved that far: the track starts first.");
                Item item = ReadItem(data, previous, commands[previous], waits);
                before.Insert(0, item);
                total += item.Time;
                position = previous;
            }

            regionStart = position;
            target = total + shift;
            items.InsertRange(0, before);
            if (moving)
            {
                if (waits) items.Add(new Item { IsRest = true, Time = duration });
                regionEnd = anchor + anchorLength;
            }
            else
            {
                items.Add(ReadItem(data, anchor, anchorLength, waits));
                regionEnd = anchor + anchorLength;
            }
        }

        foreach (int operand in FindOffsetOperands(data))
        {
            int destination = data[operand] | (data[operand + 1] << 8) | (data[operand + 2] << 16);
            if (destination > regionStart && destination < regionEnd) throw new NotSupportedException("The note cannot be moved that far: something jumps into the stretch it would cross.");
        }

        int time = 0, index = 0;
        for (; index < items.Count; index++)
        {
            Item item = items[index];
            if (time == target) break;
            if (item.Time > 0 && time + item.Time > target)
            {
                if (!item.IsRest) throw new NotSupportedException("That time is taken by another note on this track, which plays one note at a time.");
                int first = target - time;
                items[index] = new Item { IsRest = true, Time = first };
                items.Insert(index + 1, new Item { IsRest = true, Time = item.Time - first });
                index++;
                time = target;
                break;
            }

            time += item.Time;
        }

        if (time != target) throw new NotSupportedException("The note cannot be placed there.");
        var placed = new Item { IsNote = true, Time = duration, Bytes = noteBytes };
        items.Insert(index, placed);
        int remaining = duration;
        for (int next = index + 1; remaining > 0; )
        {
            if (next >= items.Count || !items[next].IsRest) throw new NotSupportedException("That time is taken by another note on this track, which plays one note at a time.");
            int take = Math.Min(remaining, items[next].Time);
            items[next].Time -= take;
            remaining -= take;
            if (items[next].Time == 0) items.RemoveAt(next);
            else next++;
        }

        var encoded = new List<byte>();
        int placedAt = -1;
        for (int i = 0; i < items.Count; i++)
        {
            Item item = items[i];
            if (item.IsRest)
            {
                int rest = item.Time;
                while (i + 1 < items.Count && items[i + 1].IsRest) rest += items[++i].Time;
                if (rest > 0) encoded.AddRange([0x80, .. EncodeVariable(rest, MinimumWidth(rest))]);
                continue;
            }

            if (ReferenceEquals(item, placed)) placedAt = encoded.Count;
            encoded.AddRange(item.Bytes);
        }

        byte[] result = Splice(data, regionStart, regionEnd - regionStart, [.. encoded], keepTargetsAtStart: true);
        int delta = encoded.Count - (regionEnd - regionStart);
        Func<int, int> map = offset => offset >= regionEnd ? offset + delta : offset > regionStart ? regionStart : offset;
        return (result, regionStart + placedAt, map);
    }

    public static SortedDictionary<int, int> CommandStarts(byte[] data)
    {
        var starts = new SortedDictionary<int, int>();
        var pending = new Stack<int>();
        pending.Push(0);
        while (pending.Count > 0)
        {
            int position = pending.Pop();
            bool conditional = false;
            while (position >= 0 && position < data.Length && !starts.ContainsKey(position))
            {
                byte command = data[position];
                int length = CommandLength(data, position);
                if (position + length > data.Length) break;
                starts[position] = length;
                if (command is 0x93 or 0x94 or 0x95)
                {
                    int operand = position + length - 3;
                    pending.Push(data[operand] | (data[operand + 1] << 8) | (data[operand + 2] << 16));
                }

                position += length;
                bool ends = !conditional && command is 0x94 or 0xFD or 0xFF;
                if (ends) break;
                conditional = command == 0xA2;
            }
        }

        return starts;
    }

    private static byte[] Overwrite(byte[] data, int at, byte[] bytes)
    {
        bytes.CopyTo(data, at);
        return data;
    }

    private static byte[] Assemble(byte[] original, int start, byte[] data)
    {
        int total = start + data.Length;
        byte[] result = new byte[(total + 3) / 4 * 4];
        original.AsSpan(0, start).CopyTo(result);
        data.CopyTo(result, start);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x08), (uint)result.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(result.AsSpan(0x14), (uint)(result.Length - 0x10));
        return result;
    }

    /// <summary>Replaces <paramref name="remove"/> bytes at <paramref name="at"/> with <paramref name="insert"/> and repoints every jump, call and
    /// track start that aims beyond that place.</summary>
    private static byte[] Splice(byte[] data, int at, int remove, byte[] insert, bool keepTargetsAtStart = false)
    {
        int delta = insert.Length - remove;
        List<int> operands = delta == 0 ? [] : FindOffsetOperands(data);
        byte[] result = new byte[data.Length + delta];
        data.AsSpan(0, at).CopyTo(result);
        insert.CopyTo(result, at);
        data.AsSpan(at + remove).CopyTo(result.AsSpan(at + insert.Length));

        foreach (int operand in operands)
        {
            if (operand >= at && operand < at + remove) continue;
            int target = data[operand] | (data[operand + 1] << 8) | (data[operand + 2] << 16);
            int moved = keepTargetsAtStart && target == at ? target : target >= at + remove ? target + delta : target > at ? at : target;
            int position = operand >= at + remove ? operand + delta : operand;
            if (moved >= 1 << 24) throw new InvalidDataException("The sequence is too large to edit.");
            result[position] = (byte)moved;
            result[position + 1] = (byte)(moved >> 8);
            result[position + 2] = (byte)(moved >> 16);
        }

        return result;
    }

    /// <summary>Where the three-byte targets of open-track, jump and call commands sit. Reads the data as commands from the start (and from
    /// each target), so a command it cannot size stops the edit rather than risking a wrong target.</summary>
    public static List<int> FindOffsetOperands(byte[] data) =>
        [.. CommandStarts(data).Where(command => data[command.Key] is 0x93 or 0x94 or 0x95).Select(command => command.Key + command.Value - 3)];

    private static int CommandLength(byte[] data, int position)
    {
        byte command = data[position];
        int length = command switch
        {
            < 0x80 => 2 + VariableWidth(data, position + 2),
            0x80 or 0x81 => 1 + VariableWidth(data, position + 1),
            0x93 => 5,
            0x94 or 0x95 => 4,
            0xA2 => 1,
            >= 0xB0 and <= 0xBD => 4,
            >= 0xC0 and <= 0xD6 => 2,
            0xE0 or 0xE1 or 0xE3 or 0xFE => 3,
            0xFC or 0xFD or 0xFF => 1,
            _ => throw new NotSupportedException($"The sequence uses a command (0x{command:X2} at {position}) that cannot be resized safely.")
        };
        return length;
    }

    private static int VariableWidth(byte[] data, int position)
    {
        int width = 1;
        while (position + width - 1 < data.Length && (data[position + width - 1] & 0x80) != 0) width++;
        if (position + width > data.Length || width > 4) throw new InvalidDataException("A length in the sequence is cut off.");
        return width;
    }

    public static int ReadVariable(byte[] data, int position)
    {
        int value = 0;
        byte b;
        do
        {
            b = data[position++];
            value = (value << 7) | (b & 0x7F);
        }
        while ((b & 0x80) != 0);
        return value;
    }

    private static int MinimumWidth(int value) => value < 0x80 ? 1 : value < 0x4000 ? 2 : value < 0x200000 ? 3 : 4;

    /// <summary>A value as the sequence's variable-length number, padded with leading zero groups to <paramref name="width"/> bytes.</summary>
    private static byte[] EncodeVariable(int value, int width)
    {
        var bytes = new byte[width];
        for (int i = 0; i < width; i++)
        {
            bytes[width - 1 - i] = (byte)((value >> (7 * i)) & 0x7F | (i > 0 ? 0x80 : 0));
        }

        return bytes;
    }
}
