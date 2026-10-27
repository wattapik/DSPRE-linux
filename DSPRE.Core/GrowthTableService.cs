using System.Buffers.Binary;

namespace DSPRE.Core;

/// <summary>One experience curve: the total experience a Pokémon has at each level, 0 to 100.</summary>
public sealed class GrowthCurve
{
    public List<uint> Experience { get; set; } = [];
}

public sealed class GrowthTables
{
    public List<GrowthCurve> Curves { get; set; } = [];
}

/// <summary>The experience tables (<c>pl_growtbl.narc</c>, Platinum): one 101 x u32 member per growth rate, indexed by the growth rate
/// number in the species' personal data.</summary>
public static class GrowthTableService
{
    public const int Levels = 101;

    public static readonly IReadOnlyList<string> CurveNames =
    [
        "Medium Fast", "Erratic", "Fluctuating", "Medium Slow", "Fast", "Slow", "Medium Fast (unused)", "Medium Fast (unused)"
    ];

    public static string CurveName(int curve) => curve < CurveNames.Count ? CurveNames[curve] : $"Curve {curve}";

    public static string ArchivePath(RomProject project) => Path.Combine(project.FilesPath, "poketool", "personal", "pl_growtbl.narc");

    public static bool IsSupported(RomProject project, RomGameMetadata metadata) =>
        metadata.HeaderFormat == MapHeaderFormat.Platinum && File.Exists(ArchivePath(project));

    public static GrowthTables Load(RomProject project)
    {
        string path = ArchivePath(project);
        var tables = new GrowthTables();
        for (int member = 0, count = NarcFile.MemberCount(path); member < count; member++)
        {
            byte[] data = NarcFile.ReadMember(path, member);
            if (data.Length != Levels * 4) throw new InvalidDataException($"Growth table {member} is {data.Length} bytes; expected {Levels * 4}.");
            var curve = new GrowthCurve();
            for (int level = 0; level < Levels; level++) curve.Experience.Add(BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(level * 4)));
            tables.Curves.Add(curve);
        }

        return tables;
    }

    public static void Save(RomProject project, GrowthTables tables) =>
        EditJournal.Track(project, EditJournalDocuments.GrowthTables, EditJournalDocuments.Keys(), () =>
        {
            string path = ArchivePath(project);
            int count = NarcFile.MemberCount(path);
            if (tables.Curves.Count != count) throw new InvalidDataException($"There are {tables.Curves.Count} curves; the archive has {count}.");
            var replacements = new Dictionary<int, byte[]>();
            for (int member = 0; member < count; member++)
            {
                List<uint> experience = tables.Curves[member].Experience;
                if (experience.Count != Levels) throw new InvalidDataException($"Curve {member} has {experience.Count} levels; it needs {Levels}.");
                var data = new byte[Levels * 4];
                for (int level = 0; level < Levels; level++) BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(level * 4), experience[level]);
                replacements[member] = data;
            }

            NarcFile.ReplaceMembers(path, replacements);
        });

    /// <summary>The experience a standard growth rate (0 medium fast, 1 erratic, 2 fluctuating, 3 medium slow, 4 fast, 5 slow) needs
    /// at a level, by the games' formulas.</summary>
    public static uint Standard(int curve, int level)
    {
        if (level <= 1) return 0;
        long n = level, cube = n * n * n;
        long value = curve switch
        {
            0 or 6 or 7 => cube,
            1 => level switch
            {
                <= 50 => cube * (100 - n) / 50,
                <= 68 => cube * (150 - n) / 100,
                <= 98 => cube * ((1911 - 10 * n) / 3) / 500,
                _ => cube * (160 - n) / 100
            },
            2 => level switch
            {
                <= 15 => cube * ((n + 1) / 3 + 24) / 50,
                <= 35 => cube * (n + 14) / 50,
                _ => cube * (n / 2 + 32) / 50
            },
            3 => 6 * cube / 5 - 15 * n * n + 100 * n - 140,
            4 => 4 * cube / 5,
            5 => 5 * cube / 4,
            _ => throw new ArgumentOutOfRangeException(nameof(curve))
        };
        return (uint)Math.Max(value, 0);
    }
}
