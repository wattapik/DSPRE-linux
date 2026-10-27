using System.Buffers.Binary;

namespace DSPRE.Core;

/// <summary>One step of a trainer graphic's animation: which cell of the class's cell bank is shown, for
/// how many ticks, and how far it is shifted (only for sequences whose frames carry a shift).</summary>
public sealed class TrainerAnimationFrame
{
    public ushort Cell { get; set; }
    public ushort Duration { get; set; } = 1;
    public short XShift { get; set; }
    public short YShift { get; set; }
    /// <summary>The frame's user attribute word, kept as the file had it.</summary>
    public uint Attribute { get; set; }
}

/// <summary>A run of frames. The game picks sequences by number (the first is the still shown when the
/// trainer appears, the next ones the animations), so sequences cannot be added or removed.</summary>
public sealed class TrainerAnimationSequence
{
    /// <summary>1 plays once, 2 loops, 3 and 4 are the same backwards (the cell animation play modes).</summary>
    public uint PlayMode { get; set; } = 1;
    public ushort LoopStart { get; set; }
    /// <summary>How its frames are stored: <see cref="TrainerAnimationService.TypeCellOnly"/> (a cell and nothing else)
    /// or <see cref="TrainerAnimationService.TypeCellShift"/> (a cell and a shift).</summary>
    public uint Type { get; set; } = TrainerAnimationService.TypeCellOnly;
    public uint Attribute { get; set; }
    public List<TrainerAnimationFrame> Frames { get; set; } = [];

    public bool HasShift => Type == TrainerAnimationService.TypeCellShift;
}

/// <summary>The frame sequences of a trainer class graphic (the RNAN "cell animation" file, file 3 of its
/// five). Layout, from the files and the cell animation format: after the 16-byte header, a KNBA block of
/// a 24-byte header, the sequence table (16 bytes each: frame count, loop start, type, play mode, offset of
/// its first frame), the frame table (8 bytes: offset of its data, duration, 0xBEEF) and the frame data
/// (a cell index, or a cell index and a shift), then a UAAT block of per-sequence and per-frame attribute
/// words; the file ends with label blocks that name the sequences and are kept as they were.</summary>
public sealed class TrainerAnimation
{
    public List<TrainerAnimationSequence> Sequences { get; set; } = [];
}

public static class TrainerAnimationService
{
    public const uint TypeCellOnly = 0x10000;
    public const uint TypeCellShift = 0x10002;
    private const int FileIndex = 3;
    private const int FilesPerClass = 5;
    private const int KnbaBase = 0x18;
    private const int HeaderSize = 0x18;

    public static bool IsSupported(RomGameMetadata metadata, TrainerGraphicSide side = TrainerGraphicSide.Front) => TrainerGraphicService.IsSupported(metadata, side);

    private static string FilePath(RomProject project, int trainerClass, TrainerGraphicSide side) =>
        Path.Combine(project.TrainerGraphicsDirectoryFor(side), (trainerClass * FilesPerClass + FileIndex).ToString("D4"));

    public static TrainerAnimation Load(RomProject project, int trainerClass, TrainerGraphicSide side = TrainerGraphicSide.Front)
    {
        string path = FilePath(project, trainerClass, side);
        if (!File.Exists(path)) throw new InvalidDataException($"Trainer class {trainerClass} has no animation file.");
        return Parse(File.ReadAllBytes(path), trainerClass);
    }

    public static void Save(RomProject project, int trainerClass, TrainerAnimation animation, TrainerGraphicSide side = TrainerGraphicSide.Front) =>
        EditJournal.Track(project, EditJournalDocuments.TrainerAnimation, side == TrainerGraphicSide.Back ? EditJournalDocuments.Keys(("trainerClass", trainerClass), ("side", "Back")) : EditJournalDocuments.Keys(("trainerClass", trainerClass)), () =>
        {
            string path = FilePath(project, trainerClass, side);
            File.WriteAllBytes(path, Serialize(File.ReadAllBytes(path), animation));
        });

    public static TrainerAnimation Parse(byte[] file, int trainerClass = -1)
    {
        string name = trainerClass >= 0 ? $"Trainer class {trainerClass}'s animation" : "The animation";
        if (file.Length < KnbaBase + HeaderSize || !file.AsSpan(0, 4).SequenceEqual("RNAN"u8) || !file.AsSpan(0x10, 4).SequenceEqual("KNBA"u8))
        {
            throw new InvalidDataException($"{name} is not a cell animation (RNAN) file.");
        }

        int sequenceCount = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(KnbaBase));
        int frameCount = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(KnbaBase + 2));
        int sequenceTable = KnbaBase + (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(KnbaBase + 4));
        int frameTable = KnbaBase + (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(KnbaBase + 8));
        int data = KnbaBase + (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(KnbaBase + 12));
        int attributes = KnbaBase + (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(KnbaBase + 20));
        if (attributes + 0x10 > file.Length || !file.AsSpan(attributes, 4).SequenceEqual("TAAU"u8) || sequenceTable + 16 * sequenceCount > file.Length || frameTable + 8 * frameCount > file.Length)
        {
            throw new InvalidDataException($"{name} is laid out in a way this editor does not understand.");
        }

        int attributeBase = attributes + 8;
        uint Word(int offset) => offset + 4 <= file.Length ? BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(offset)) : 0;

        var animation = new TrainerAnimation();
        for (int s = 0; s < sequenceCount; s++)
        {
            int at = sequenceTable + 16 * s;
            int count = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at));
            var sequence = new TrainerAnimationSequence
            {
                LoopStart = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(at + 2)),
                Type = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 4)),
                PlayMode = BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 8))
            };
            if (sequence.Type is not (TypeCellOnly or TypeCellShift)) throw new InvalidDataException($"{name} has a sequence of a kind (0x{sequence.Type:X}) this editor does not handle.");

            int first = (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(at + 12)) / 8;
            int entry = attributes + 0x10 + 12 * s;
            sequence.Attribute = Word(attributeBase + (int)Word(entry + 4));
            int framePointers = attributeBase + (int)Word(entry + 8);
            for (int f = 0; f < count; f++)
            {
                int frameAt = frameTable + 8 * (first + f);
                if (first + f >= frameCount) throw new InvalidDataException($"{name} has a sequence running past its frame table.");
                int record = data + (int)BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(frameAt));
                var frame = new TrainerAnimationFrame
                {
                    Cell = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(record)),
                    Duration = BinaryPrimitives.ReadUInt16LittleEndian(file.AsSpan(frameAt + 4)),
                    Attribute = Word(attributeBase + (int)Word(framePointers + 4 * f))
                };
                if (sequence.HasShift)
                {
                    frame.XShift = BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(record + 4));
                    frame.YShift = BinaryPrimitives.ReadInt16LittleEndian(file.AsSpan(record + 6));
                }

                sequence.Frames.Add(frame);
            }

            animation.Sequences.Add(sequence);
        }

        return animation;
    }

    /// <summary>The file with its sequences' frames replaced. <paramref name="original"/> supplies what is not
    /// edited: the header and the label blocks at the end.</summary>
    public static byte[] Serialize(byte[] original, TrainerAnimation animation)
    {
        int sequenceCount = BinaryPrimitives.ReadUInt16LittleEndian(original.AsSpan(KnbaBase));
        if (animation.Sequences.Count != sequenceCount) throw new InvalidDataException($"The animation has {sequenceCount} sequences, which cannot change.");
        int oldAttributes = KnbaBase + (int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(KnbaBase + 20));
        int oldAttributesSize = (int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(oldAttributes + 4));
        int tailStart = KnbaBase - 8 + (int)BinaryPrimitives.ReadUInt32LittleEndian(original.AsSpan(0x14));
        if (animation.Sequences.Any(sequence => sequence.Frames.Count == 0)) throw new InvalidDataException("Every sequence needs at least one frame.");
        int frameCount = animation.Sequences.Sum(sequence => sequence.Frames.Count);
        if (frameCount > ushort.MaxValue) throw new InvalidDataException("There are too many frames.");

        // Frame data, shared between frames that need the same bytes as the game's own files do.
        var data = new List<byte>();
        var offsets = new Dictionary<string, int>();
        int DataOffset(TrainerAnimationSequence sequence, TrainerAnimationFrame frame)
        {
            byte[] record = new byte[sequence.HasShift ? 8 : 4];
            BinaryPrimitives.WriteUInt16LittleEndian(record, frame.Cell);
            BinaryPrimitives.WriteUInt16LittleEndian(record.AsSpan(2), sequence.HasShift ? (ushort)0xBEEF : (ushort)0xCCCC);
            if (sequence.HasShift)
            {
                BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(4), frame.XShift);
                BinaryPrimitives.WriteInt16LittleEndian(record.AsSpan(6), frame.YShift);
            }

            string key = Convert.ToHexString(record);
            if (!offsets.TryGetValue(key, out int offset))
            {
                offsets[key] = offset = data.Count;
                data.AddRange(record);
            }

            return offset;
        }

        byte[] sequences = new byte[16 * sequenceCount];
        byte[] frames = new byte[8 * frameCount];
        int frameIndex = 0;
        for (int s = 0; s < sequenceCount; s++)
        {
            TrainerAnimationSequence sequence = animation.Sequences[s];
            Span<byte> entry = sequences.AsSpan(16 * s, 16);
            BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)sequence.Frames.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], sequence.LoopStart);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], sequence.Type);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], sequence.PlayMode);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[12..], (uint)(8 * frameIndex));
            foreach (TrainerAnimationFrame frame in sequence.Frames)
            {
                Span<byte> frameEntry = frames.AsSpan(8 * frameIndex, 8);
                BinaryPrimitives.WriteUInt32LittleEndian(frameEntry, (uint)DataOffset(sequence, frame));
                BinaryPrimitives.WriteUInt16LittleEndian(frameEntry[4..], frame.Duration);
                BinaryPrimitives.WriteUInt16LittleEndian(frameEntry[6..], 0xBEEF);
                frameIndex++;
            }
        }

        int attributesSize = 0x10 + 12 * sequenceCount + 4 * frameCount + 4 * sequenceCount + 4 * frameCount;
        int sequenceTable = HeaderSize, frameTable = sequenceTable + sequences.Length, dataStart = frameTable + frames.Length;
        int attributesStart = dataStart + data.Count;
        byte[] knba = new byte[attributesStart + attributesSize];
        original.AsSpan(KnbaBase, HeaderSize).CopyTo(knba);
        BinaryPrimitives.WriteUInt16LittleEndian(knba.AsSpan(2), (ushort)frameCount);
        BinaryPrimitives.WriteUInt32LittleEndian(knba.AsSpan(4), (uint)sequenceTable);
        BinaryPrimitives.WriteUInt32LittleEndian(knba.AsSpan(8), (uint)frameTable);
        BinaryPrimitives.WriteUInt32LittleEndian(knba.AsSpan(12), (uint)dataStart);
        BinaryPrimitives.WriteUInt32LittleEndian(knba.AsSpan(20), (uint)attributesStart);
        sequences.CopyTo(knba, sequenceTable);
        frames.CopyTo(knba, frameTable);
        data.CopyTo(knba, dataStart);

        // The attribute block: a table of each sequence's frame count and where its attribute word and
        // frame list are, the frame lists, then the sequence words and the frame words.
        Span<byte> attributes = knba.AsSpan(attributesStart);
        "TAAU"u8.CopyTo(attributes);
        BinaryPrimitives.WriteUInt32LittleEndian(attributes[4..], (uint)attributesSize);
        BinaryPrimitives.WriteUInt16LittleEndian(attributes[8..], (ushort)sequenceCount);
        BinaryPrimitives.WriteUInt16LittleEndian(attributes[10..], 1);
        BinaryPrimitives.WriteUInt32LittleEndian(attributes[12..], 8);
        int framePointers = 0x10 + 12 * sequenceCount;
        int sequenceWords = framePointers + 4 * frameCount;
        int frameWords = sequenceWords + 4 * sequenceCount;
        int frameNumber = 0;
        for (int s = 0; s < sequenceCount; s++)
        {
            TrainerAnimationSequence sequence = animation.Sequences[s];
            Span<byte> entry = attributes.Slice(0x10 + 12 * s, 12);
            BinaryPrimitives.WriteUInt16LittleEndian(entry, (ushort)sequence.Frames.Count);
            BinaryPrimitives.WriteUInt16LittleEndian(entry[2..], 0xBEEF);
            BinaryPrimitives.WriteUInt32LittleEndian(entry[4..], (uint)(sequenceWords + 4 * s - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(entry[8..], (uint)(framePointers + 4 * frameNumber - 8));
            BinaryPrimitives.WriteUInt32LittleEndian(attributes[(sequenceWords + 4 * s)..], sequence.Attribute);
            foreach (TrainerAnimationFrame frame in sequence.Frames)
            {
                BinaryPrimitives.WriteUInt32LittleEndian(attributes[(framePointers + 4 * frameNumber)..], (uint)(frameWords + 4 * frameNumber - 8));
                BinaryPrimitives.WriteUInt32LittleEndian(attributes[(frameWords + 4 * frameNumber)..], frame.Attribute);
                frameNumber++;
            }
        }

        byte[] tail = original.AsSpan(tailStart).ToArray();
        byte[] file = new byte[0x10 + 8 + knba.Length + tail.Length];
        original.AsSpan(0, 0x18).CopyTo(file);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(0x14), (uint)(8 + knba.Length));
        knba.CopyTo(file, 0x18);
        tail.CopyTo(file, 0x18 + knba.Length);
        BinaryPrimitives.WriteUInt32LittleEndian(file.AsSpan(8), (uint)file.Length);
        return file;
    }
}
