namespace DSPRE.Core;

public enum MoveSplit : byte
{
    Physical = 0,
    Special,
    Status,
}

public enum ContestCondition : byte
{
    Cool = 0,
    Beautiful,
    Cute,
    Smart,
    Tough,
}

[Flags]
public enum MoveFlags : byte
{
    None = 0,
    Contact = 1 << 0,
    Protect = 1 << 1,
    MagicCoat = 1 << 2,
    Snatch = 1 << 3,
    MirrorMove = 1 << 4,
    KingsRock = 1 << 5,
    KeepHpBar = 1 << 6,
    DelShadow = 1 << 7,
}

/// <summary>Byte-exact port of the legacy MoveData record (DS_Map/ROMFiles/MoveData.cs).</summary>
public sealed class MoveRecord
{
    public ushort BattleEffect;
    public MoveSplit Split;
    public byte Damage;
    public PokemonType MoveType;
    public byte Accuracy;
    public byte Pp;
    public byte SideEffectProbability;
    public ushort Target;
    public sbyte Priority;
    public byte FlagField;
    public byte ContestAppeal;
    public ContestCondition ContestConditionType;
}

public static class MoveDataService
{
    /// <summary>Move target/range bitfield values mapped to the user-friendly name and description the
    /// legacy MoveDataEditor's range picker shows, ported verbatim from MoveData.AttackRangeDescriptions.</summary>
    public static readonly (ushort Value, string Name, string Description)[] AttackRangeDescriptions =
    [
        (0, "Opponent or Ally", "May target one adjacent opponent or ally"),
        (1 << 0, "Varies", "Variable based on move effect"),
        (1 << 1, "One Random Opponent", "Affects a random opponent"),
        (1 << 2, "All Opponents", "Affects all adjacent opponents"),
        (1 << 3, "All Others", "Affects all adjacent opponents and allies"),
        (1 << 4, "User", "Affects the user"),
        (1 << 5, "User Side", "Affects the user's side of the field"),
        (1 << 6, "All Sides", "Affects the user's and opponent's sides of the field"),
        (1 << 7, "Opponent Side", "Affects the opponent's side of the field"),
        (1 << 8, "One Ally", "May target one adjacent ally"),
        (1 << 9, "User or Ally", "May target the user or one adjacent ally"),
        (1 << 10, "One Opponent", "May target any one adjacent opponent"),
    ];

    public static string GetAttackRangeName(ushort targetValue)
    {
        foreach ((ushort value, string name, _) in AttackRangeDescriptions)
        {
            if (value == targetValue) return name;
        }

        var names = new List<string>();
        foreach ((ushort value, string name, _) in AttackRangeDescriptions)
        {
            if (value != 0 && (targetValue & value) != 0) names.Add(name);
        }

        return names.Count > 0 ? string.Join("|", names) : "Unknown";
    }

    public static IReadOnlyList<int> GetIds(RomProject project)
    {
        if (!Directory.Exists(project.MoveDataDirectory)) return [];
        return Directory.GetFiles(project.MoveDataDirectory)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => int.TryParse(name, out _))
            .Select(int.Parse)
            .OrderBy(id => id)
            .ToArray();
    }

    public static int GetFileCount(RomProject project) =>
        Directory.Exists(project.MoveDataDirectory) ? Directory.GetFiles(project.MoveDataDirectory).Length : 0;

    public static MoveRecord Load(RomProject project, int id)
    {
        string path = Path.Combine(project.MoveDataDirectory, id.ToString("D4"));
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);

        return new MoveRecord
        {
            BattleEffect = reader.ReadUInt16(),
            Split = (MoveSplit)reader.ReadByte(),
            Damage = reader.ReadByte(),
            MoveType = (PokemonType)reader.ReadByte(),
            Accuracy = reader.ReadByte(),
            Pp = reader.ReadByte(),
            SideEffectProbability = reader.ReadByte(),
            Target = reader.ReadUInt16(),
            Priority = reader.ReadSByte(),
            FlagField = reader.ReadByte(),
            ContestAppeal = reader.ReadByte(),
            ContestConditionType = (ContestCondition)reader.ReadByte(),
        };
    }

    public static void Save(RomProject project, int id, MoveRecord move) =>
        EditJournal.Track(project, EditJournalDocuments.MoveData, EditJournalDocuments.Keys(("move", id)), () => SaveUntracked(project, id, move));

    private static void SaveUntracked(RomProject project, int id, MoveRecord move)
    {
        Directory.CreateDirectory(project.MoveDataDirectory);
        string path = Path.Combine(project.MoveDataDirectory, id.ToString("D4"));

        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream))
        {
            writer.Write(move.BattleEffect);
            writer.Write((byte)move.Split);
            writer.Write(move.Damage);
            writer.Write((byte)move.MoveType);
            writer.Write(move.Accuracy);
            writer.Write(move.Pp);
            writer.Write(move.SideEffectProbability);
            writer.Write(move.Target);
            writer.Write(move.Priority);
            writer.Write(move.FlagField);
            writer.Write(move.ContestAppeal);
            writer.Write((byte)move.ContestConditionType);
            writer.Write((ushort)0); // Filler, matches legacy ToByteArray padding to 16 bytes.
        }

        File.WriteAllBytes(path, stream.ToArray());
    }

    /// <summary>Move-name archive is the same one TrainerService/PersonalDataService already resolve for
    /// attack names; move descriptions live in the immediately preceding archive (names_id - 1) in every
    /// game version, per RomInfo.SetMoveTextNumbers.</summary>
    public static int GetMoveDescriptionsArchiveId(RomGameMetadata metadata) => TrainerService.GetAttackNamesArchiveId(metadata) - 1;
}
