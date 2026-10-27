using System.Buffers.Binary;

namespace DSPRE.Core;

public sealed class WildEncounterRow
{
    internal WildEncounterRow(string category, int slot, int species, int minLevel, int maxLevel)
    {
        Category = category;
        Slot = slot;
        Species = species;
        MinLevel = minLevel;
        MaxLevel = maxLevel;
    }

    public string Category { get; }
    public int Slot { get; }
    public int Species { get; set; }
    public bool IsCategoryStart => Slot == 0;
    public int MinLevel { get; set; }
    public int MaxLevel { get; set; }
    /// <summary>Walking slots have one level (shown as the minimum); day/night/radar/dual-slot
    /// replacements have none and use the walking slot's.</summary>
    public bool HasMinLevel => MinLevelOffset >= 0;
    /// <summary>The slot's chance, fixed by the game rather than stored in the file; null for slots that
    /// stand in for others (time of day, radar, swarms...).</summary>
    public int? RatePercent { get; init; }
    /// <summary>4 bytes in Diamond/Pearl/Platinum, 2 in HeartGold/SoulSilver.</summary>
    internal int SpeciesSize { get; init; } = 4;
    public bool HasMaxLevel => MaxLevelOffset >= 0;
    internal int SpeciesOffset { get; init; }
    internal int MinLevelOffset { get; init; } = -1;
    internal int MaxLevelOffset { get; init; } = -1;
}

public sealed class WildEncounterFile
{
    private readonly byte[] originalData;

    private readonly IReadOnlyDictionary<string, int> rateOffsets;

    internal WildEncounterFile(int id, byte[] data, IReadOnlyList<WildEncounterRow> rows, IReadOnlyDictionary<string, int> rateOffsets)
    {
        Id = id;
        originalData = data;
        Rows = rows.ToList();
        this.rateOffsets = rateOffsets;
        Rates = rateOffsets.ToDictionary(pair => pair.Key, pair => (int)data[pair.Value]);
    }

    public int Id { get; }
    public List<WildEncounterRow> Rows { get; }
    public Dictionary<string, int> Rates { get; }

    public byte[] ToByteArray()
    {
        byte[] data = (byte[])originalData.Clone();
        foreach (WildEncounterRow row in Rows)
        {
            if (row.SpeciesSize == 2) WriteUInt16(data, row.SpeciesOffset, (ushort)Math.Clamp(row.Species, 0, ushort.MaxValue));
            else WriteUInt32(data, row.SpeciesOffset, (uint)Math.Clamp(row.Species, 0, ushort.MaxValue));
            if (row.MinLevelOffset >= 0) data[row.MinLevelOffset] = (byte)Math.Clamp(row.MinLevel, 0, 255);
            if (row.MaxLevelOffset >= 0) data[row.MaxLevelOffset] = (byte)Math.Clamp(row.MaxLevel, 0, 255);
        }

        foreach ((string category, int offset) in rateOffsets)
        {
            if (Rates.TryGetValue(category, out int rate) && offset < data.Length)
            {
                data[offset] = (byte)Math.Clamp(rate, 0, 255);
            }
        }

        return data;
    }

    private static void WriteUInt16(byte[] data, int offset, ushort value)
    {
        if (offset >= 0 && offset + 2 <= data.Length)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(offset, 2), value);
        }
    }

    private static void WriteUInt32(byte[] data, int offset, uint value)
    {
        if (offset >= 0 && offset + 4 <= data.Length)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(offset, 4), value);
        }
    }
}

public static class WildEncounterService
{
    /// <summary>Per-slot encounter probabilities, fixed by the DPPt game engine rather than stored in
    /// the ROM. Day/Night/Poké Radar/dual-slot categories are time- or condition-gated ALTERNATES for
    /// specific Walking slots (not additional independent slots), so they share that slot's rate -
    /// verified against the legacy WinForms WildEditorDPPt, whose designer file hard-codes the same
    /// percentages into control names/labels (e.g. walkingTwentyFirstComboBox, "10%" labels next to the
    /// morning/day/night trio, radarFirst/Second sharing the same row as the 10% walking slots).</summary>
    public static readonly IReadOnlyDictionary<string, int[]> SlotRatePercentages = new Dictionary<string, int[]>
    {
        ["Walking"] = [20, 20, 10, 10, 10, 10, 5, 5, 4, 4, 1, 1],
        ["Swarm"] = [20, 20],
        ["Day"] = [10, 10],
        ["Night"] = [10, 10],
        ["Poké Radar"] = [10, 10, 1, 1],
        ["Ruby Dual Slot"] = [4, 4],
        ["Sapphire Dual Slot"] = [4, 4],
        ["Emerald Dual Slot"] = [4, 4],
        ["FireRed Dual Slot"] = [4, 4],
        ["LeafGreen Dual Slot"] = [4, 4],
        ["Surf"] = [60, 30, 5, 4, 1],
        ["Old Rod"] = [60, 30, 5, 4, 1],
        ["Good Rod"] = [40, 40, 15, 4, 1],
        ["Super Rod"] = [40, 40, 15, 4, 1],
    };

    public static int? GetSlotRatePercent(string category, int slot)
    {
        if (SlotRatePercentages.TryGetValue(category, out int[]? rates) && slot >= 0 && slot < rates.Length)
        {
            return rates[slot];
        }
        return null;
    }

    public static IReadOnlyList<int> GetFileIds(RomProject project)
    {
        if (!Directory.Exists(project.EncountersDirectory)) return [];
        return Directory.GetFiles(project.EncountersDirectory)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(name => int.TryParse(name, out _))
            .Select(int.Parse)
            .OrderBy(id => id)
            .ToArray();
    }

    private static readonly IReadOnlyDictionary<string, int> DpPtRateOffsets = new Dictionary<string, int>
    {
        ["Walking"] = 0, ["Surf"] = 204, ["Old Rod"] = 292, ["Good Rod"] = 336, ["Super Rod"] = 380
    };

    private static readonly IReadOnlyDictionary<string, int> HgssRateOffsets = new Dictionary<string, int>
    {
        ["Walking"] = 0, ["Surf"] = 1, ["Rock Smash"] = 2, ["Old Rod"] = 3, ["Good Rod"] = 4, ["Super Rod"] = 5
    };

    /// <summary>HeartGold/SoulSilver slot chances (legacy WildEditorHGSS's labels): their rods differ
    /// from Diamond/Pearl/Platinum's.</summary>
    private static readonly IReadOnlyDictionary<string, int[]> HgssSlotRates = new Dictionary<string, int[]>
    {
        ["Morning"] = [20, 20, 10, 10, 10, 10, 5, 5, 4, 4, 1, 1],
        ["Day"] = [20, 20, 10, 10, 10, 10, 5, 5, 4, 4, 1, 1],
        ["Night"] = [20, 20, 10, 10, 10, 10, 5, 5, 4, 4, 1, 1],
        ["Surf"] = [60, 30, 5, 4, 1],
        ["Rock Smash"] = [90, 10],
        ["Old Rod"] = [40, 30, 15, 10, 5],
        ["Good Rod"] = [40, 30, 15, 10, 5],
        ["Super Rod"] = [40, 30, 15, 10, 5],
    };

    public static WildEncounterFile Load(RomProject project, int id)
    {
        string path = Path.Combine(project.EncountersDirectory, $"{id:D4}");
        byte[] data = File.ReadAllBytes(path);
        if (project.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata?.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver)
        {
            return LoadHgss(id, data);
        }

        if (data.Length < 424)
        {
            throw new InvalidDataException($"Encounter file {id:D4} is only {data.Length} bytes; expected a DPPt encounter file.");
        }

        var rows = new List<WildEncounterRow>();
        AddFixedRows(rows, data, "Walking", 4, 12, 8, LevelLayout.Single);
        // Layout after the walking slots (legacy EncounterFileDPPt): swarms 100, day 108, night 116,
        // radar 124, form data 140, unown table 160, dual slots 164.
        AddSpeciesRows(rows, data, "Day", 108, 2, 4);
        AddSpeciesRows(rows, data, "Night", 116, 2, 4);
        AddSpeciesRows(rows, data, "Poké Radar", 124, 4, 4);
        AddSpeciesRows(rows, data, "Ruby Dual Slot", 164, 2, 4);
        AddSpeciesRows(rows, data, "Sapphire Dual Slot", 172, 2, 4);
        AddSpeciesRows(rows, data, "Emerald Dual Slot", 180, 2, 4);
        AddSpeciesRows(rows, data, "FireRed Dual Slot", 188, 2, 4);
        AddSpeciesRows(rows, data, "LeafGreen Dual Slot", 196, 2, 4);
        AddFixedRows(rows, data, "Surf", 208, 5, 8, LevelLayout.MaxThenMin);
        AddFixedRows(rows, data, "Old Rod", 296, 5, 8, LevelLayout.MaxThenMin);
        AddFixedRows(rows, data, "Good Rod", 340, 5, 8, LevelLayout.MaxThenMin);
        AddFixedRows(rows, data, "Super Rod", 384, 5, 8, LevelLayout.MaxThenMin);
        // Added after the rest so the journal's row indices of older entries keep pointing at the same slots.
        AddSpeciesRows(rows, data, "Swarm", 100, 2, 4);
        return new WildEncounterFile(id, data, rows, DpPtRateOffsets);
    }

    /// <summary>Legacy EncounterFileHGSS: six rate bytes, the 12 grass slots' levels (shared by morning,
    /// day and night), then u16 species for each time of day, radio, surf/rock smash/rods (min and max
    /// level bytes first) and the four swarm slots.</summary>
    private static WildEncounterFile LoadHgss(int id, byte[] data)
    {
        if (data.Length < 196)
        {
            throw new InvalidDataException($"Encounter file {id:D4} is only {data.Length} bytes; expected a HeartGold/SoulSilver encounter file.");
        }

        var rows = new List<WildEncounterRow>();
        void Species(string category, int start, int count, Func<int, int>? levelOffset = null)
        {
            for (int i = 0; i < count; i++)
            {
                int offset = start + 2 * i;
                int levelAt = levelOffset?.Invoke(i) ?? -1;
                rows.Add(new WildEncounterRow(category, i, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)), levelAt >= 0 ? data[levelAt] : 0, 0)
                {
                    SpeciesOffset = offset, SpeciesSize = 2, MinLevelOffset = levelAt, RatePercent = HgssRate(category, i)
                });
            }
        }

        void Levelled(string category, int start, int count)
        {
            for (int i = 0; i < count; i++)
            {
                int offset = start + 4 * i;
                rows.Add(new WildEncounterRow(category, i, BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2)), data[offset], data[offset + 1])
                {
                    SpeciesOffset = offset + 2, SpeciesSize = 2, MinLevelOffset = offset, MaxLevelOffset = offset + 1, RatePercent = HgssRate(category, i)
                });
            }
        }

        // The grass levels sit on the morning rows only: one byte serves all three times of day.
        Species("Morning", 20, 12, i => 8 + i);
        Species("Day", 44, 12);
        Species("Night", 68, 12);
        Species("Hoenn Sound", 92, 2);
        Species("Sinnoh Sound", 96, 2);
        Species("Grass Swarm", 188, 1);
        Levelled("Rock Smash", 120, 2);
        Levelled("Surf", 100, 5);
        Species("Surf Swarm", 190, 1);
        Levelled("Old Rod", 128, 5);
        Levelled("Good Rod", 148, 5);
        Levelled("Super Rod", 168, 5);
        Species("Night Fishing", 192, 1);
        Species("Rod Swarm", 194, 1);
        return new WildEncounterFile(id, data, rows, HgssRateOffsets);
    }

    private static int? HgssRate(string category, int slot) =>
        HgssSlotRates.TryGetValue(category, out int[]? rates) && slot < rates.Length ? rates[slot] : null;

    public static void Save(RomProject project, WildEncounterFile file) =>
        EditJournal.Track(project, EditJournalDocuments.WildEncounters, EditJournalDocuments.Keys(("encounterFile", file.Id)), () => SaveUntracked(project, file));

    private static void SaveUntracked(RomProject project, WildEncounterFile file)
    {
        Directory.CreateDirectory(project.EncountersDirectory);
        File.WriteAllBytes(Path.Combine(project.EncountersDirectory, $"{file.Id:D4}"), file.ToByteArray());
    }

    /// <summary>Adds an encounter file that never triggers an encounter (every slot rate and species 0), the size of the existing ones, and returns its number.</summary>
    public static int AddBlank(RomProject project)
    {
        IReadOnlyList<int> ids = GetFileIds(project);
        int length = ids.Count > 0 ? (int)new FileInfo(Path.Combine(project.EncountersDirectory, $"{ids[0]:D4}")).Length : throw new InvalidOperationException("The project has no wild encounter files to size a new one from.");
        int id = ids.Max() + 1;
        File.WriteAllBytes(Path.Combine(project.EncountersDirectory, $"{id:D4}"), new byte[length]);
        EditJournal.Record(project, "CreateEncounterFile", ("encounterFile", id));
        return id;
    }

    public static string? FindArchivePath(RomProject project, RomGameMetadata metadata)
    {
        string[] candidates = metadata.HeaderFormat switch
        {
            MapHeaderFormat.Platinum => [Path.Combine(project.FilesPath, "fielddata", "encountdata", "pl_enc_data.narc")],
            MapHeaderFormat.DiamondPearl => [Path.Combine(project.FilesPath, "fielddata", "encountdata", "d_enc_data.narc"), Path.Combine(project.FilesPath, "fielddata", "encountdata", "p_enc_data.narc")],
            MapHeaderFormat.HeartGoldSoulSilver => [Path.Combine(project.FilesPath, "a", "0", "3", "7"), Path.Combine(project.FilesPath, "a", "1", "3", "6")],
            _ => []
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static void AddSpeciesRows(List<WildEncounterRow> rows, byte[] data, string category, int start, int count, int stride)
    {
        for (int i = 0; i < count; i++)
        {
            int speciesOffset = start + i * stride;
            rows.Add(new WildEncounterRow(category, i, (int)ReadUInt32(data, speciesOffset), 0, 0) { SpeciesOffset = speciesOffset, RatePercent = GetSlotRatePercent(category, i) });
        }
    }

    /// <summary>How a slot stores its level before the u32 species: walking slots a u32 level, water
    /// slots a max then a min level byte and two bytes of padding (legacy EncounterFileDPPt).</summary>
    private enum LevelLayout
    {
        Single,
        MaxThenMin
    }

    private static void AddFixedRows(List<WildEncounterRow> rows, byte[] data, string category, int start, int count, int stride, LevelLayout levels)
    {
        for (int i = 0; i < count; i++)
        {
            int offset = start + i * stride;
            int minOffset = levels == LevelLayout.Single ? offset : offset + 1;
            int maxOffset = levels == LevelLayout.Single ? -1 : offset;
            int speciesOffset = offset + 4;
            rows.Add(new WildEncounterRow(category, i, (int)ReadUInt32(data, speciesOffset), data[minOffset], maxOffset >= 0 ? data[maxOffset] : 0)
            {
                SpeciesOffset = speciesOffset, MinLevelOffset = minOffset, MaxLevelOffset = maxOffset, RatePercent = GetSlotRatePercent(category, i)
            });
        }
    }

    private static uint ReadUInt32(byte[] data, int offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
}