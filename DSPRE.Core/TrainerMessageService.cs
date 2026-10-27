using System.Buffers.Binary;

namespace DSPRE.Core;

/// <summary>One battle line of a trainer: the trainer, when it is said (<see cref="TrainerMessageService.Triggers"/>) and the text.</summary>
public sealed class TrainerMessageEntry
{
    public int Trainer { get; set; }
    public int Trigger { get; set; }
    public string Text { get; set; } = string.Empty;
}

/// <summary>Every trainer battle line, in the order of the game's table. Entry <c>i</c> of the table is message <c>i</c> of the
/// trainer message text archive.</summary>
public sealed class TrainerMessageTable
{
    public List<TrainerMessageEntry> Entries { get; set; } = [];
}

/// <summary>Trainer battle messages (legacy DS_Map/Editors/TrainerEditor/BattleMessage/TrainerMessageEditor.cs). <c>trtbl</c> is a
/// list of (trainer u16, trigger u16) entries with each trainer's entries together; <c>trtblofs</c> holds, per trainer, the byte offset
/// of its first entry (0 when it has none); the texts are one message per entry in a text archive.</summary>
public static class TrainerMessageService
{
    private const int EntrySize = 4;

    public static readonly IReadOnlyDictionary<int, string> Triggers = new Dictionary<int, string>
    {
        [0] = "Pre-battle (overworld)",
        [1] = "On defeat (player wins)",
        [2] = "Post-battle (overworld)",
        [3] = "Pre-double battle (trainer 1)",
        [4] = "Double battle defeat (trainer 1)",
        [5] = "Post-double battle (trainer 1)",
        [6] = "Not enough Pokémon for a double battle (trainer 1)",
        [7] = "Pre-double battle (trainer 2)",
        [8] = "Double battle defeat (trainer 2)",
        [9] = "Post-double battle (trainer 2)",
        [10] = "Not enough Pokémon for a double battle (trainer 2)",
        [11] = "Not morning (unused)",
        [12] = "Not night (unused)",
        [13] = "First damage dealt (unused)",
        [14] = "Active battler at half HP (unused)",
        [15] = "Last battler sent out",
        [16] = "Last battler at half HP",
        [17] = "Before rematch",
        [18] = "Double battle rematch (trainer 1)",
        [19] = "Double battle rematch (trainer 2)",
        [20] = "Victory (trainer wins) - HeartGold/SoulSilver",
        [100] = "Victory (trainer wins) - Diamond/Pearl/Platinum"
    };

    public static string TriggerName(int trigger) => Triggers.TryGetValue(trigger, out string? name) ? name : $"Trigger {trigger}";

    public static int TextArchiveId(RomGameMetadata metadata) => metadata.HeaderFormat switch
    {
        MapHeaderFormat.DiamondPearl => metadata.Language == RomLanguage.Japanese ? 549 : 558,
        MapHeaderFormat.Platinum => 617,
        _ => metadata.Language == RomLanguage.Japanese ? 718 : 728
    };

    private static (string Table, string Offsets) Paths(RomProject project, RomGameMetadata metadata) =>
        metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver
            ? (Path.Combine(project.FilesPath, "a", "0", "5", "7"), Path.Combine(project.FilesPath, "a", "1", "3", "1"))
            : (Path.Combine(project.FilesPath, "poketool", "trmsg", "trtbl.narc"), Path.Combine(project.FilesPath, "poketool", "trmsg", "trtblofs.narc"));

    public static bool IsSupported(RomProject project, RomGameMetadata metadata)
    {
        (string table, string offsets) = Paths(project, metadata);
        return File.Exists(table) && File.Exists(offsets);
    }

    public static TrainerMessageTable Load(RomProject project, RomGameMetadata metadata)
    {
        (string tablePath, _) = Paths(project, metadata);
        byte[] table = NarcFile.ReadMember(tablePath, 0);
        IReadOnlyList<string> messages = TextArchiveService.Load(project, TextArchiveId(metadata), metadata).Messages;
        var result = new TrainerMessageTable();
        for (int i = 0; i + EntrySize <= table.Length; i += EntrySize)
        {
            int index = i / EntrySize;
            result.Entries.Add(new TrainerMessageEntry
            {
                Trainer = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(i)),
                Trigger = BinaryPrimitives.ReadUInt16LittleEndian(table.AsSpan(i + 2)),
                Text = index < messages.Count ? messages[index] : string.Empty
            });
        }

        return result;
    }

    /// <summary>Writes the table and the offsets and rebuilds the message archive. Entries keep their order except that a trainer's
    /// entries are gathered where its first one is, which is all the game needs.</summary>
    public static void Save(RomProject project, RomGameMetadata metadata, TrainerMessageTable table) =>
        EditJournal.Track(project, EditJournalDocuments.TrainerMessages, EditJournalDocuments.Keys(), () => SaveUntracked(project, metadata, table));

    private static void SaveUntracked(RomProject project, RomGameMetadata metadata, TrainerMessageTable table)
    {
        (string tablePath, string offsetsPath) = Paths(project, metadata);
        foreach (TrainerMessageEntry entry in table.Entries)
        {
            if (entry.Trainer is < 0 or > ushort.MaxValue || entry.Trigger is < 0 or > ushort.MaxValue) throw new InvalidDataException("A trainer or trigger number is out of range.");
        }

        List<TrainerMessageEntry> ordered = table.Entries.GroupBy(entry => entry.Trainer).SelectMany(group => group).ToList();
        if (ordered.Count * EntrySize > ushort.MaxValue) throw new InvalidDataException("There are too many messages for the offset table to address.");

        byte[] oldOffsets = NarcFile.ReadMember(offsetsPath, 0);
        int trainerCount = Math.Max(oldOffsets.Length / 2, ordered.Count == 0 ? 0 : ordered.Max(entry => entry.Trainer) + 1);
        var offsets = new byte[trainerCount * 2];
        var tableBytes = new byte[ordered.Count * EntrySize];
        var seen = new HashSet<int>();
        for (int i = 0; i < ordered.Count; i++)
        {
            TrainerMessageEntry entry = ordered[i];
            BinaryPrimitives.WriteUInt16LittleEndian(tableBytes.AsSpan(i * EntrySize), (ushort)entry.Trainer);
            BinaryPrimitives.WriteUInt16LittleEndian(tableBytes.AsSpan(i * EntrySize + 2), (ushort)entry.Trigger);
            if (seen.Add(entry.Trainer)) BinaryPrimitives.WriteUInt16LittleEndian(offsets.AsSpan(entry.Trainer * 2), (ushort)(i * EntrySize));
        }

        TextArchiveData archive = TextArchiveService.Load(project, TextArchiveId(metadata), metadata);
        archive.Messages.Clear();
        archive.Messages.AddRange(ordered.Select(entry => entry.Text));

        NarcFile.ReplaceMembers(tablePath, new Dictionary<int, byte[]> { [0] = tableBytes });
        NarcFile.ReplaceMembers(offsetsPath, new Dictionary<int, byte[]> { [0] = offsets });
        using (EditJournal.Suppress()) TextArchiveService.Save(project, archive);
    }
}
