namespace DSPRE.Core;

public sealed record LearnsetEntry(byte Level, ushort Move);

public sealed class LearnsetFile(int id, List<LearnsetEntry> entries)
{
    public int Id { get; } = id;
    public List<LearnsetEntry> Entries { get; } = entries;
}

/// <summary>Byte-exact port of the legacy LearnsetData format (DS_Map/ROMFiles/LearnsetData.cs): one
/// ushort per entry, move packed into the low 9 bits and level into the high 7, terminated by the raw
/// sentinel 0xFFFF. Format is identical across DP/Pt/HGSS - only the source NARC path differs.</summary>
public static class LearnsetService
{
    private const int BitsMove = 9;
    private const int BitsLevel = 7;
    private const int MoveMask = (1 << BitsMove) - 1;
    private const int LevelMask = (1 << BitsLevel) - 1;
    private const ushort Sentinel = 0xFFFF;

    /// <summary>Not a hard cap - the vanilla games never exceed this many level-up moves per species, so
    /// the legacy editor shows a soft warning (dismissible) above it rather than blocking the save.</summary>
    public const int VanillaLimit = 20;

    public static IReadOnlyList<int> GetIds(RomProject project)
    {
        if (!Directory.Exists(project.LearnsetsDirectory)) return [];
        return Directory.GetFiles(project.LearnsetsDirectory)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => int.TryParse(name, out _))
            .Select(int.Parse)
            .OrderBy(id => id)
            .ToArray();
    }

    public static LearnsetFile Load(RomProject project, int id)
    {
        string path = Path.Combine(project.LearnsetsDirectory, id.ToString("D4"));
        byte[] data = File.ReadAllBytes(path);
        var entries = new List<LearnsetEntry>(Math.Max(0, data.Length / 2 - 1));

        for (int offset = 0; offset + 2 <= data.Length; offset += 2)
        {
            ushort raw = BitConverter.ToUInt16(data, offset);
            if (raw == Sentinel) break;

            // File order is kept (not re-sorted), like legacy LearnsetData: the order of same-level moves
            // is user-editable and matters in game. Exact duplicates are dropped, as legacy's UniqueList does.
            var entry = new LearnsetEntry((byte)((raw >> BitsMove) & LevelMask), (ushort)(raw & MoveMask));
            if (!entries.Contains(entry)) entries.Add(entry);
        }

        return new LearnsetFile(id, entries);
    }

    public static void Save(RomProject project, LearnsetFile file) =>
        EditJournal.Track(project, EditJournalDocuments.Learnset, EditJournalDocuments.Keys(("learnset", file.Id)), () => SaveUntracked(project, file));

    private static void SaveUntracked(RomProject project, LearnsetFile file)
    {
        Directory.CreateDirectory(project.LearnsetsDirectory);
        string path = Path.Combine(project.LearnsetsDirectory, file.Id.ToString("D4"));

        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream))
        {
            foreach (LearnsetEntry entry in file.Entries)
            {
                ushort packed = (ushort)((entry.Move & MoveMask) | ((entry.Level & LevelMask) << BitsMove));
                writer.Write(packed);
            }

            writer.Write(Sentinel);
            writer.Write((ushort)0); // Legacy writer always emits this trailing zero word after the sentinel.
        }

        File.WriteAllBytes(path, stream.ToArray());
    }

    /// <summary>Inserts in (level, then move) sorted order, matching the legacy editor's
    /// InsertEntrySafe. Returns false without modifying the list if an entry with the same (level, move)
    /// already exists - the legacy UniqueList silently drops such duplicates.</summary>
    public static bool InsertSorted(List<LearnsetEntry> entries, LearnsetEntry entry)
    {
        if (entries.Any(e => e.Level == entry.Level && e.Move == entry.Move))
        {
            return false;
        }

        int index = entries.FindIndex(e => e.Level > entry.Level || (e.Level == entry.Level && e.Move > entry.Move));
        entries.Insert(index < 0 ? entries.Count : index, entry);
        return true;
    }
}
