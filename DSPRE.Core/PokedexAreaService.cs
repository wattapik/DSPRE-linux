using System.Buffers.Binary;

namespace DSPRE.Core;

/// <summary>The areas one species is listed in on the Pokédex's area map, per category (see <see cref="PokedexAreaService.Categories"/>).
/// The numbers are 1-based and name a place in the table of the category's group.</summary>
public sealed class AreaList
{
    public List<int> Areas { get; set; } = [];
}

public sealed class SpeciesAreas
{
    public List<AreaList> Lists { get; set; } = [];
}

/// <summary>Index 0 is the placeholder species and is not read: it is empty here and left as it is in the file.</summary>
public sealed class PokedexAreas
{
    public List<SpeciesAreas> Species { get; set; } = [];
}

/// <summary>Pokédex area lists (Platinum, <c>application/zukanlist/zkn_data/zukan_enc_platinum.narc</c>). Members 1 and 3 are tables of the
/// map header each place stands for: member 1 the 21 special places (mines, caves, lakes, the Great Marsh...), member 3 the 50 routes and
/// cities (entry 0 of each is unused); members 0 and 2 hold where they are drawn on the map. After four tables, the lists are ten blocks of
/// 495 members, member <c>4 + block * 495 + species</c>, each a run of area numbers ended by 0. Blocks 0 to 4 use the special places and
/// 5 to 9 the routes, each in the order morning, day, night, Honey Tree, Poké Radar - checked against the games' encounter tables
/// (the lists match the morning, day, night and radar encounters, and the Honey Tree lists hold exactly the Honey Tree species).</summary>
public static class PokedexAreaService
{
    public const int CategoryCount = 10;
    public const int VanillaSpecialPlaceCount = 21;
    public const int VanillaRoutePlaceCount = 50;
    private const int BlockStride = 495;
    private const int FirstListMember = 4;
    private const int SpeciesCount = 494;

    public static readonly IReadOnlyList<string> Categories =
    [
        "Special places: morning", "Special places: day", "Special places: night", "Special places: Honey Tree and Great Marsh", "Special places: Poké Radar",
        "Routes and cities: morning", "Routes and cities: day", "Routes and cities: night", "Routes and cities: Honey Tree", "Routes and cities: Poké Radar"
    ];

    public static bool UsesSpecialPlaces(int category) => category < 5;

    /// <summary>How many special places and routes the archive's place tables have (the original game has 21 and 50).</summary>
    public sealed record PlaceCounts(int Specials, int Routes)
    {
        public int For(int category) => UsesSpecialPlaces(category) ? Specials : Routes;
    }

    private static PlaceCounts CountsOf(byte[][] members)
    {
        if (members.Length < 4 || members[1].Length % 4 != 0 || members[3].Length % 4 != 0 || members[1].Length < 8 || members[3].Length < 8) throw new InvalidDataException("The Pokédex area place tables are damaged.");
        return new PlaceCounts(members[1].Length / 4 - 1, members[3].Length / 4 - 1);
    }

    public static PlaceCounts Counts(RomProject project) => CountsOf(NarcFile.ReadAllMembers(ArchivePath(project)));

    /// <summary>A species' saved list that names a place beyond the given counts (so the map's tables cannot shrink to them), or null.</summary>
    public static string? FindListedBeyond(RomProject project, int specials, int routes)
    {
        byte[][] members = NarcFile.ReadAllMembers(ArchivePath(project));
        var counts = new PlaceCounts(specials, routes);
        for (int species = 1; species < SpeciesCount; species++)
        {
            for (int category = 0; category < CategoryCount; category++)
            {
                byte[] data = members[Member(category, species)];
                for (int at = 0; at + 4 <= data.Length; at += 4)
                {
                    uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
                    if (value == 0) break;
                    if (value > counts.For(category)) return $"species {species} lists area {value} ({Categories[category]})";
                }
            }
        }

        return null;
    }

    public static string ArchivePath(RomProject project) => Path.Combine(project.FilesPath, "application", "zukanlist", "zkn_data", "zukan_enc_platinum.narc");

    public static bool IsSupported(RomProject project, RomGameMetadata metadata) =>
        metadata.HeaderFormat == MapHeaderFormat.Platinum && File.Exists(ArchivePath(project));

    private static int Member(int category, int species) => FirstListMember + category * BlockStride + species;

    public static PokedexAreas Load(RomProject project)
    {
        string path = ArchivePath(project);
        byte[][] members = NarcFile.ReadAllMembers(path);
        if (members.Length < Member(CategoryCount - 1, SpeciesCount - 1) + 1) throw new InvalidDataException("The Pokédex area archive is smaller than expected.");
        PlaceCounts counts = CountsOf(members);
        var areas = new PokedexAreas();
        for (int species = 0; species < SpeciesCount; species++)
        {
            var entry = new SpeciesAreas();
            for (int category = 0; category < CategoryCount; category++)
            {
                entry.Lists.Add(species == 0 ? new AreaList() : Parse(members[Member(category, species)], category, species, counts.For(category)));
            }

            areas.Species.Add(entry);
        }

        return areas;
    }

    private static AreaList Parse(byte[] data, int category, int species, int maxArea)
    {
        var list = new AreaList();
        if (data.Length < 4 || data.Length % 4 != 0) throw new InvalidDataException($"The area list of species {species} ({Categories[category]}) is damaged.");
        for (int at = 0; at + 4 <= data.Length; at += 4)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(at));
            if (value == 0) return list;
            if (value > maxArea) throw new InvalidDataException($"Species {species} ({Categories[category]}) lists area {value}, which does not exist.");
            list.Areas.Add((int)value);
        }

        throw new InvalidDataException($"The area list of species {species} ({Categories[category]}) has no end.");
    }

    /// <summary>The place each area number stands for, from the table's map header: area 1 first. Null names are returned as "Area n".</summary>
    public static IReadOnlyList<string> PlaceNames(RomProject project, RomGameMetadata metadata, bool specialPlaces)
    {
        string path = ArchivePath(project);
        byte[] table = NarcFile.ReadMember(path, specialPlaces ? 1 : 3);
        IMapHeaderStore? store = MapHeaderStoreFactory.Create(project, metadata);
        IReadOnlyList<HeaderOption> locations = project.GetLocationNames(metadata);
        int count = table.Length / 4 - 1;
        var names = new List<string>();
        for (int area = 1; area <= count; area++)
        {
            string name = $"Area {area}";
            if (store is not null && area * 4 + 4 <= table.Length)
            {
                uint header = BinaryPrimitives.ReadUInt32LittleEndian(table.AsSpan(area * 4));
                if (header < store.Count)
                {
                    ushort locationId = store.Load((ushort)header).LocationNameId;
                    if (locationId < locations.Count) name = locations[locationId].Label;
                }
            }

            names.Add(name);
        }

        return names;
    }

    public static void Save(RomProject project, PokedexAreas areas) =>
        EditJournal.Track(project, EditJournalDocuments.PokedexAreas, EditJournalDocuments.Keys(), () =>
        {
            string path = ArchivePath(project);
            if (areas.Species.Count != SpeciesCount) throw new InvalidDataException($"There are {areas.Species.Count} species; the archive has {SpeciesCount}.");
            byte[][] current = NarcFile.ReadAllMembers(path);
            PlaceCounts counts = CountsOf(current);
            var replacements = new Dictionary<int, byte[]>();
            for (int species = 1; species < SpeciesCount; species++)
            {
                SpeciesAreas entry = areas.Species[species];
                if (entry.Lists.Count != CategoryCount) throw new InvalidDataException($"Species {species} has {entry.Lists.Count} lists; it needs {CategoryCount}.");
                for (int category = 0; category < CategoryCount; category++)
                {
                    List<int> list = entry.Lists[category].Areas;
                    if (list.Any(area => area < 1 || area > counts.For(category))) throw new InvalidDataException($"Species {species} ({Categories[category]}) lists an area outside 1 to {counts.For(category)}; the map's places have to be saved first.");
                    var data = new byte[(list.Count + 1) * 4];
                    for (int i = 0; i < list.Count; i++) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(i * 4), (uint)list[i]);
                    int member = Member(category, species);
                    if (!data.AsSpan().SequenceEqual(current[member])) replacements[member] = data;
                }
            }

            if (replacements.Count > 0) NarcFile.ReplaceMembers(path, replacements);
        });
}
