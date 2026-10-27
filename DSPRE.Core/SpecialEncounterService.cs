namespace DSPRE.Core;

// ---------------------------------------------------------------- Honey Tree

public sealed class HoneyTreeEncounter
{
    public ushort Species { get; set; }
    public ushort Padding { get; set; }
}

public sealed class HoneyTreeGroup(string name, string description)
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public List<HoneyTreeEncounter> Encounters { get; } = [];
}

// ---------------------------------------------------------------- Great Marsh

public sealed class GreatMarshEncounter
{
    public ushort Species { get; set; }
    public ushort Padding { get; set; }
}

public sealed class GreatMarshGroup(string name, string description)
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public List<GreatMarshEncounter> Encounters { get; } = [];
}

// ---------------------------------------------------------------- Bug Contest

public sealed class BugContestEncounter
{
    public ushort Species { get; set; }
    public byte MinLevel { get; set; } = 1;
    public byte MaxLevel { get; set; } = 1;
    public byte Rate { get; set; }
    public byte Score { get; set; }
    public ushort Dummy { get; set; }
}

public sealed class BugContestSet(string name, string description)
{
    public string Name { get; } = name;
    public string Description { get; } = description;
    public List<BugContestEncounter> Encounters { get; } = [];
}

// ---------------------------------------------------------------- Headbutt

public sealed class HeadbuttEncounter
{
    public ushort PokemonId { get; set; }
    public byte MinLevel { get; set; }
    public byte MaxLevel { get; set; }
}

public sealed class HeadbuttTree
{
    public ushort GlobalX { get; set; }
    public ushort GlobalY { get; set; }
}

public sealed class HeadbuttTreeGroup
{
    public const int TreeCount = 6;
    public List<HeadbuttTree> Trees { get; } = [];

    public static HeadbuttTreeGroup Empty()
    {
        var group = new HeadbuttTreeGroup();
        for (int i = 0; i < TreeCount; i++) group.Trees.Add(new HeadbuttTree());
        return group;
    }
}

public sealed class HeadbuttEncounterFile(int id)
{
    public const int NormalEncountersCount = 12;
    public const int SpecialEncountersCount = 6;

    public int Id { get; set; } = id;
    public List<HeadbuttEncounter> NormalEncounters { get; } = [];
    public List<HeadbuttEncounter> SpecialEncounters { get; } = [];
    public List<HeadbuttTreeGroup> NormalTreeGroups { get; } = [];
    public List<HeadbuttTreeGroup> SpecialTreeGroups { get; } = [];
}

// ---------------------------------------------------------------- Safari Zone

public sealed class SafariZoneEncounter
{
    public ushort PokemonId { get; set; }
    public byte Level { get; set; } = 1;
}

public sealed class SafariZoneObjectRequirement
{
    public static readonly Dictionary<int, string> ObjectTypes = new()
    {
        { 0, "No Requirement" },
        { 1, "Plains" },
        { 2, "Forest" },
        { 3, "Peak" },
        { 4, "Waterside" }
    };

    public byte TypeId { get; set; }
    public byte Quantity { get; set; }
}

public sealed class SafariZoneEncounterGroup
{
    public const int EncounterSlots = 10;

    public byte ObjectSlots { get; set; }
    public List<SafariZoneEncounter> MorningEncounters { get; } = [];
    public List<SafariZoneEncounter> DayEncounters { get; } = [];
    public List<SafariZoneEncounter> NightEncounters { get; } = [];
    public List<SafariZoneEncounter> MorningEncountersObject { get; } = [];
    public List<SafariZoneEncounter> DayEncountersObject { get; } = [];
    public List<SafariZoneEncounter> NightEncountersObject { get; } = [];
    public List<SafariZoneObjectRequirement> ObjectRequirements { get; } = [];
    public List<SafariZoneObjectRequirement> OptionalObjectRequirements { get; } = [];

    public void Read(BinaryReader reader)
    {
        ReadSlots(reader, MorningEncounters, EncounterSlots);
        ReadSlots(reader, DayEncounters, EncounterSlots);
        ReadSlots(reader, NightEncounters, EncounterSlots);
        ReadSlots(reader, MorningEncountersObject, ObjectSlots);
        ReadSlots(reader, DayEncountersObject, ObjectSlots);
        ReadSlots(reader, NightEncountersObject, ObjectSlots);

        for (int i = 0; i < ObjectSlots; i++)
        {
            ObjectRequirements.Add(ReadRequirement(reader));
            OptionalObjectRequirements.Add(ReadRequirement(reader));
        }
    }

    public void Write(BinaryWriter writer)
    {
        WriteSlots(writer, MorningEncounters);
        WriteSlots(writer, DayEncounters);
        WriteSlots(writer, NightEncounters);
        WriteSlots(writer, MorningEncountersObject);
        WriteSlots(writer, DayEncountersObject);
        WriteSlots(writer, NightEncountersObject);

        for (int i = 0; i < ObjectSlots; i++)
        {
            WriteRequirement(writer, ObjectRequirements[i]);
            WriteRequirement(writer, OptionalObjectRequirements[i]);
        }
    }

    private static void ReadSlots(BinaryReader reader, List<SafariZoneEncounter> target, int count)
    {
        for (int i = 0; i < count; i++)
        {
            target.Add(new SafariZoneEncounter { PokemonId = reader.ReadUInt16(), Level = reader.ReadByte() });
            reader.ReadByte();
        }
    }

    private static void WriteSlots(BinaryWriter writer, List<SafariZoneEncounter> source)
    {
        foreach (SafariZoneEncounter encounter in source)
        {
            writer.Write(encounter.PokemonId);
            writer.Write(encounter.Level);
            writer.Write((byte)0);
        }
    }

    private static SafariZoneObjectRequirement ReadRequirement(BinaryReader reader) =>
        new() { TypeId = reader.ReadByte(), Quantity = reader.ReadByte() };

    private static void WriteRequirement(BinaryWriter writer, SafariZoneObjectRequirement requirement)
    {
        writer.Write(requirement.TypeId);
        writer.Write(requirement.Quantity);
    }
}

public sealed class SafariZoneEncounterFile(int id)
{
    public static readonly Dictionary<int, string> Names = new()
    {
        { 0, "Plains" }, { 1, "Meadow" }, { 2, "Savannah" }, { 3, "Peak" },
        { 4, "Rocky Beach" }, { 5, "Wetland" }, { 6, "Forest" }, { 7, "Swamp" },
        { 8, "Marshland" }, { 9, "Wasteland" }, { 10, "Mountain" }, { 11, "Desert" }
    };

    public int Id { get; set; } = id;
    public SafariZoneEncounterGroup Grass { get; } = new();
    public SafariZoneEncounterGroup Surf { get; } = new();
    public SafariZoneEncounterGroup OldRod { get; } = new();
    public SafariZoneEncounterGroup GoodRod { get; } = new();
    public SafariZoneEncounterGroup SuperRod { get; } = new();

    public IEnumerable<(string Name, SafariZoneEncounterGroup Group)> AllGroups()
    {
        yield return ("Grass", Grass);
        yield return ("Surf", Surf);
        yield return ("Old Rod", OldRod);
        yield return ("Good Rod", GoodRod);
        yield return ("Super Rod", SuperRod);
    }
}

// ---------------------------------------------------------------- Service

public static class SpecialEncounterService
{
    public const int HoneyTreeSlotsPerGroup = 6;
    public const int HoneyTreeGroupCount = 3;
    public const int GreatMarshSlotsPerGroup = 32;
    public const int GreatMarshGroupCount = 2;
    public const int BugContestEntrySize = 8;
    public const int BugContestEncountersPerSet = 10;
    public const int BugContestSetCount = 4;

    public static readonly int[] HoneyTreeSlotRates = [40, 20, 20, 10, 5, 5];

    // Diamond and Platinum use files 2-4; Pearl uses 5-7.
    public static int[] GetHoneyTreeFileIndices(RomGameMetadata metadata) =>
        metadata.GameCode.StartsWith("APA", StringComparison.OrdinalIgnoreCase) ? [5, 6, 7] : [2, 3, 4];

    public static readonly int[] GreatMarshFileIndices = [9, 10];

    public static bool SupportsHoneyTree(RomGameMetadata metadata) =>
        metadata.HeaderFormat is MapHeaderFormat.DiamondPearl or MapHeaderFormat.Platinum;

    public static bool SupportsGreatMarsh(RomGameMetadata metadata) => SupportsHoneyTree(metadata);

    public static bool SupportsHgssFeatures(RomGameMetadata metadata) =>
        metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;

    // ------------------------------------------------------------ Honey Tree

    public static List<HoneyTreeGroup> LoadHoneyTree(RomProject project, RomGameMetadata metadata)
    {
        List<HoneyTreeGroup> groups =
        [
            new("Group A (Common)", "Most common group (70% on normal trees, 20% on Munchlax trees)."),
            new("Group B (Uncommon)", "Uncommon group (20% on normal trees, 70% on Munchlax trees)."),
            new("Group C (Munchlax)", "Rare group containing Munchlax (1% on Munchlax trees only).")
        ];

        int[] indices = GetHoneyTreeFileIndices(metadata);
        for (int group = 0; group < HoneyTreeGroupCount; group++)
        {
            string path = Path.Combine(project.ExtendedEncountersDirectory, $"{indices[group]:D4}");
            ReadFixedSlots(path, groups[group].Encounters, HoneyTreeSlotsPerGroup,
                reader => new HoneyTreeEncounter { Species = reader.ReadUInt16(), Padding = reader.ReadUInt16() },
                () => new HoneyTreeEncounter(), 4);
        }

        return groups;
    }

    public static void SaveHoneyTree(RomProject project, RomGameMetadata metadata, List<HoneyTreeGroup> groups) =>
        EditJournal.Track(project, EditJournalDocuments.HoneyTree, EditJournalDocuments.Keys(), () => SaveHoneyTreeUntracked(project, metadata, groups));

    private static void SaveHoneyTreeUntracked(RomProject project, RomGameMetadata metadata, List<HoneyTreeGroup> groups)
    {
        Directory.CreateDirectory(project.ExtendedEncountersDirectory);
        int[] indices = GetHoneyTreeFileIndices(metadata);
        for (int group = 0; group < groups.Count && group < indices.Length; group++)
        {
            File.WriteAllBytes(
                Path.Combine(project.ExtendedEncountersDirectory, $"{indices[group]:D4}"),
                HoneyTreeGroupToBytes(groups[group]));
        }
    }

    public static byte[] HoneyTreeGroupToBytes(HoneyTreeGroup group)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        foreach (HoneyTreeEncounter encounter in group.Encounters)
        {
            writer.Write(encounter.Species);
            writer.Write(encounter.Padding);
        }

        return stream.ToArray();
    }

    // ------------------------------------------------------------ Great Marsh

    public static List<GreatMarshGroup> LoadGreatMarsh(RomProject project)
    {
        List<GreatMarshGroup> groups =
        [
            new("Post-National Dex", "Pokemon available after obtaining the National Pokedex."),
            new("Pre-National Dex", "Pokemon available before obtaining the National Pokedex.")
        ];

        for (int group = 0; group < GreatMarshGroupCount; group++)
        {
            string path = Path.Combine(project.ExtendedEncountersDirectory, $"{GreatMarshFileIndices[group]:D4}");
            ReadFixedSlots(path, groups[group].Encounters, GreatMarshSlotsPerGroup,
                reader => new GreatMarshEncounter { Species = reader.ReadUInt16(), Padding = reader.ReadUInt16() },
                () => new GreatMarshEncounter(), 4);
        }

        return groups;
    }

    public static void SaveGreatMarsh(RomProject project, List<GreatMarshGroup> groups) =>
        EditJournal.Track(project, EditJournalDocuments.GreatMarsh, EditJournalDocuments.Keys(), () => SaveGreatMarshUntracked(project, groups));

    private static void SaveGreatMarshUntracked(RomProject project, List<GreatMarshGroup> groups)
    {
        Directory.CreateDirectory(project.ExtendedEncountersDirectory);
        for (int group = 0; group < groups.Count && group < GreatMarshFileIndices.Length; group++)
        {
            File.WriteAllBytes(
                Path.Combine(project.ExtendedEncountersDirectory, $"{GreatMarshFileIndices[group]:D4}"),
                GreatMarshGroupToBytes(groups[group]));
        }
    }

    public static byte[] GreatMarshGroupToBytes(GreatMarshGroup group)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        foreach (GreatMarshEncounter encounter in group.Encounters)
        {
            writer.Write(encounter.Species);
            writer.Write(encounter.Padding);
        }

        return stream.ToArray();
    }

    // ------------------------------------------------------------ Bug Contest

    public static string GetBugContestPath(RomProject project) =>
        Path.Combine(project.FilesPath, "data", "mushi", "mushi_encount.bin");

    public static List<BugContestSet> LoadBugContest(RomProject project)
    {
        List<BugContestSet> sets =
        [
            new("Set 0: No National Dex / Sun-Mon", "Used without the National Dex, or with it on Sunday/Monday."),
            new("Set 1: Nat Dex - Tue/Wed", "Used with the National Dex on Tuesday/Wednesday."),
            new("Set 2: Nat Dex - Thu/Fri", "Used with the National Dex on Thursday/Friday."),
            new("Set 3: Nat Dex - Saturday", "Used with the National Dex on Saturday only.")
        ];

        string path = GetBugContestPath(project);
        if (File.Exists(path))
        {
            using FileStream stream = File.OpenRead(path);
            using BinaryReader reader = new(stream);
            int entryIndex = 0;
            while (stream.Position + BugContestEntrySize <= stream.Length &&
                   entryIndex < BugContestSetCount * BugContestEncountersPerSet)
            {
                sets[entryIndex / BugContestEncountersPerSet].Encounters.Add(new BugContestEncounter
                {
                    Species = reader.ReadUInt16(),
                    MinLevel = reader.ReadByte(),
                    MaxLevel = reader.ReadByte(),
                    Rate = reader.ReadByte(),
                    Score = reader.ReadByte(),
                    Dummy = reader.ReadUInt16()
                });
                entryIndex++;
            }
        }

        foreach (BugContestSet set in sets)
        {
            while (set.Encounters.Count < BugContestEncountersPerSet)
            {
                set.Encounters.Add(new BugContestEncounter());
            }
        }

        return sets;
    }

    public static byte[] BugContestToBytes(List<BugContestSet> sets)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        foreach (BugContestSet set in sets)
        {
            foreach (BugContestEncounter encounter in set.Encounters)
            {
                writer.Write(encounter.Species);
                writer.Write(encounter.MinLevel);
                writer.Write(encounter.MaxLevel);
                writer.Write(encounter.Rate);
                writer.Write(encounter.Score);
                writer.Write(encounter.Dummy);
            }
        }

        return stream.ToArray();
    }

    public static void SaveBugContest(RomProject project, List<BugContestSet> sets) =>
        EditJournal.Track(project, EditJournalDocuments.BugContest, EditJournalDocuments.Keys(), () => SaveBugContestUntracked(project, sets));

    private static void SaveBugContestUntracked(RomProject project, List<BugContestSet> sets)
    {
        string path = GetBugContestPath(project);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, BugContestToBytes(sets));
    }

    // ------------------------------------------------------------ Headbutt

    public static IReadOnlyList<int> GetHeadbuttIds(RomProject project) => GetNumericFileIds(project.HeadbuttDirectory);

    public static HeadbuttEncounterFile LoadHeadbutt(RomProject project, int id) =>
        LoadHeadbuttPath(Path.Combine(project.HeadbuttDirectory, $"{id:D4}"), id);

    public static HeadbuttEncounterFile LoadHeadbuttPath(string path, int id)
    {
        var file = new HeadbuttEncounterFile(id);
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);

        if (stream.Length < 4)
        {
            FillHeadbuttDefaults(file);
            return file;
        }

        byte normalTreeGroupsCount = reader.ReadByte();
        reader.ReadByte();
        byte specialTreeGroupsCount = reader.ReadByte();
        reader.ReadByte();

        if (normalTreeGroupsCount == 0 && specialTreeGroupsCount == 0)
        {
            FillHeadbuttDefaults(file);
            return file;
        }

        for (int i = 0; i < HeadbuttEncounterFile.NormalEncountersCount; i++)
        {
            file.NormalEncounters.Add(ReadHeadbuttEncounter(reader));
        }

        for (int i = 0; i < HeadbuttEncounterFile.SpecialEncountersCount; i++)
        {
            file.SpecialEncounters.Add(ReadHeadbuttEncounter(reader));
        }

        for (int i = 0; i < normalTreeGroupsCount; i++)
        {
            file.NormalTreeGroups.Add(ReadTreeGroup(reader));
        }

        for (int i = 0; i < specialTreeGroupsCount; i++)
        {
            file.SpecialTreeGroups.Add(ReadTreeGroup(reader));
        }

        return file;
    }

    public static byte[] HeadbuttToBytes(HeadbuttEncounterFile file)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        writer.Write((ushort)file.NormalTreeGroups.Count);
        writer.Write((ushort)file.SpecialTreeGroups.Count);

        foreach (HeadbuttEncounter encounter in file.NormalEncounters.Concat(file.SpecialEncounters))
        {
            writer.Write(encounter.PokemonId);
            writer.Write(encounter.MinLevel);
            writer.Write(encounter.MaxLevel);
        }

        foreach (HeadbuttTreeGroup group in file.NormalTreeGroups.Concat(file.SpecialTreeGroups))
        {
            foreach (HeadbuttTree tree in group.Trees)
            {
                writer.Write(tree.GlobalX);
                writer.Write(tree.GlobalY);
            }
        }

        return stream.ToArray();
    }

    public static void SaveHeadbutt(RomProject project, HeadbuttEncounterFile file) =>
        EditJournal.Track(project, EditJournalDocuments.Headbutt, EditJournalDocuments.Keys(("headbutt", file.Id)), () => SaveHeadbuttUntracked(project, file));

    private static void SaveHeadbuttUntracked(RomProject project, HeadbuttEncounterFile file)
    {
        Directory.CreateDirectory(project.HeadbuttDirectory);
        File.WriteAllBytes(Path.Combine(project.HeadbuttDirectory, $"{file.Id:D4}"), HeadbuttToBytes(file));
    }

    private static void FillHeadbuttDefaults(HeadbuttEncounterFile file)
    {
        for (int i = 0; i < HeadbuttEncounterFile.NormalEncountersCount; i++) file.NormalEncounters.Add(new HeadbuttEncounter());
        for (int i = 0; i < HeadbuttEncounterFile.SpecialEncountersCount; i++) file.SpecialEncounters.Add(new HeadbuttEncounter());
    }

    private static HeadbuttEncounter ReadHeadbuttEncounter(BinaryReader reader) => new()
    {
        PokemonId = reader.ReadUInt16(),
        MinLevel = reader.ReadByte(),
        MaxLevel = reader.ReadByte()
    };

    private static HeadbuttTreeGroup ReadTreeGroup(BinaryReader reader)
    {
        var group = new HeadbuttTreeGroup();
        for (int i = 0; i < HeadbuttTreeGroup.TreeCount; i++)
        {
            group.Trees.Add(new HeadbuttTree { GlobalX = reader.ReadUInt16(), GlobalY = reader.ReadUInt16() });
        }

        return group;
    }

    // ------------------------------------------------------------ Safari Zone

    public static IReadOnlyList<int> GetSafariZoneIds(RomProject project) => GetNumericFileIds(project.SafariZoneDirectory);

    public static SafariZoneEncounterFile LoadSafariZone(RomProject project, int id) =>
        LoadSafariZonePath(Path.Combine(project.SafariZoneDirectory, $"{id:D4}"), id);

    public static SafariZoneEncounterFile LoadSafariZonePath(string path, int id)
    {
        var file = new SafariZoneEncounterFile(id);
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);
        if (stream.Length < 5)
        {
            return file;
        }

        foreach ((_, SafariZoneEncounterGroup group) in file.AllGroups())
        {
            group.ObjectSlots = reader.ReadByte();
        }

        reader.ReadByte();
        reader.ReadByte();
        reader.ReadByte();

        foreach ((_, SafariZoneEncounterGroup group) in file.AllGroups())
        {
            group.Read(reader);
        }

        return file;
    }

    public static byte[] SafariZoneToBytes(SafariZoneEncounterFile file)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);
        foreach ((_, SafariZoneEncounterGroup group) in file.AllGroups())
        {
            writer.Write(group.ObjectSlots);
        }

        writer.Write((byte)0);
        writer.Write((byte)0);
        writer.Write((byte)0);

        foreach ((_, SafariZoneEncounterGroup group) in file.AllGroups())
        {
            group.Write(writer);
        }

        return stream.ToArray();
    }

    public static void SaveSafariZone(RomProject project, SafariZoneEncounterFile file) =>
        EditJournal.Track(project, EditJournalDocuments.SafariZone, EditJournalDocuments.Keys(("safariZone", file.Id)), () => SaveSafariZoneUntracked(project, file));

    private static void SaveSafariZoneUntracked(RomProject project, SafariZoneEncounterFile file)
    {
        Directory.CreateDirectory(project.SafariZoneDirectory);
        File.WriteAllBytes(Path.Combine(project.SafariZoneDirectory, $"{file.Id:D4}"), SafariZoneToBytes(file));
    }

    // ------------------------------------------------------------ Helpers

    private static void ReadFixedSlots<T>(string path, List<T> target, int slots, Func<BinaryReader, T> read, Func<T> createEmpty, int entrySize)
    {
        target.Clear();
        if (File.Exists(path))
        {
            using FileStream stream = File.OpenRead(path);
            using BinaryReader reader = new(stream);
            for (int slot = 0; slot < slots && stream.Position + entrySize <= stream.Length; slot++)
            {
                target.Add(read(reader));
            }
        }

        while (target.Count < slots)
        {
            target.Add(createEmpty());
        }
    }

    private static IReadOnlyList<int> GetNumericFileIds(string directory)
    {
        if (!Directory.Exists(directory))
        {
            return [];
        }

        return Directory.GetFiles(directory)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => int.TryParse(name, out _))
            .Select(int.Parse!)
            .OrderBy(id => id)
            .ToArray();
    }
}
