namespace DSPRE.Core;

public enum PokemonType
{
    Normal = 0,
    Fighting,
    Flying,
    Poison,
    Ground,
    Rock,
    Bug,
    Ghost,
    Steel,
    Unknown,
    Fire,
    Water,
    Grass,
    Electric,
    Psychic,
    Ice,
    Dragon,
    Dark
}

public enum PokemonGrowthCurve
{
    MediumFast = 0,
    Erratic,
    Fluctuating,
    MediumSlow,
    Fast,
    Slow
}

public enum PokemonEggGroup
{
    Unassigned = 0,
    Monster,
    Water1,
    Bug,
    Flying,
    Field,
    Fairy,
    Grass,
    Humanoid,
    Water3,
    Mineral,
    Amorphous,
    Water2,
    Ditto,
    Dragon,
    NoBreed,
}

public enum PokemonDexColor
{
    Red = 0,
    Blue,
    Yellow,
    Green,
    Black,
    Brown,
    Purple,
    Gray,
    White,
    Pink,
    Unspecified
}

/// <summary>Byte-exact port of the legacy PokemonPersonalData record (DS_Map/ROMFiles/PokemonPersonalData.cs).</summary>
public sealed class PersonalDataRecord
{
    public byte BaseHP;
    public byte BaseAtk;
    public byte BaseDef;
    public byte BaseSpeed;
    public byte BaseSpAtk;
    public byte BaseSpDef;

    public PokemonType Type1;
    public PokemonType Type2;

    public byte CatchRate;
    public byte GivenExp;

    public byte EvHP;
    public byte EvAtk;
    public byte EvDef;
    public byte EvSpeed;
    public byte EvSpAtk;
    public byte EvSpDef;

    public ushort Item1;
    public ushort Item2;

    public byte GenderRatio;
    public byte EggSteps;
    public byte BaseFriendship;
    public PokemonGrowthCurve GrowthCurve;

    public byte EggGroup1;
    public byte EggGroup2;
    public byte FirstAbility;
    public byte SecondAbility;

    public byte EscapeRate;
    public PokemonDexColor Color;
    public bool Flip;

    public SortedSet<byte> Machines = [];
}

public static class PersonalDataService
{
    public const int TmsCount = 92;
    public const int HmsCount = 8;
    public const int MachineCount = TmsCount + HmsCount;

    /// <summary>Species with extra personal-data/learnset/evolution/sprite file slots beyond the base
    /// species count - alternate forms (Deoxys, Wormadam, Giratina, Shaymin, Rotom) that occupy their own
    /// NARC entries. Ported verbatim from PokeDatabase.PersonalData.personalExtraFiles: game-version
    /// agnostic, always 11 entries, indexed immediately after the base species range.</summary>
    public sealed record AltFormEntry(int MonId, string Description, int IconId);

    public static readonly IReadOnlyList<AltFormEntry> AltForms =
    [
        new(386, "Attack", 496),
        new(386, "Defense", 497),
        new(386, "Speed", 498),
        new(413, "Sandy", 529),
        new(413, "Trash", 530),
        new(487, "Origin", 533),
        new(492, "Sky", 534),
        new(479, "Heat", 535),
        new(479, "Wash", 536),
        new(479, "Frost", 537),
        new(479, "Fan", 538),
        new(479, "Mow", 539),
    ];

    public static IReadOnlyList<int> GetIds(RomProject project)
    {
        if (!Directory.Exists(project.PersonalDataDirectory)) return [];
        return Directory.GetFiles(project.PersonalDataDirectory)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => int.TryParse(name, out _))
            .Select(int.Parse)
            .OrderBy(id => id)
            .ToArray();
    }

    public static int GetFileCount(RomProject project) =>
        Directory.Exists(project.PersonalDataDirectory) ? Directory.GetFiles(project.PersonalDataDirectory).Length : 0;

    /// <summary>Builds the full species picker list: base species names, then one entry per alt form
    /// ("{baseName} - {description}"), then generic "Extra entry N" placeholders for any remaining file
    /// slots - matching PersonalDataEditor/LearnsetEditor's GetFileNames()/ctor logic exactly.</summary>
    public static string[] BuildFileNames(IReadOnlyList<string> pokemonNames, int totalFileCount)
    {
        var names = new List<string>(totalFileCount);
        names.AddRange(pokemonNames);
        foreach (AltFormEntry entry in AltForms)
        {
            string baseName = entry.MonId < names.Count ? names[entry.MonId] : $"Mon {entry.MonId}";
            names.Add($"{baseName} - {entry.Description}");
        }

        while (names.Count < totalFileCount)
        {
            names.Add($"Extra entry {names.Count}");
        }

        return names.ToArray();
    }

    /// <summary>Resolves the species icon to show for a given personal-data/learnset/evolution file
    /// index: itself if within the base species range, or the alt form's linked icon id if beyond it -
    /// matching PersonalDataEditor.ChangeLoadedFile / LearnsetEditor.UpdatePokePic exactly.</summary>
    public static int ResolveIconSpeciesId(int fileId, int baseSpeciesCount)
    {
        int excess = fileId - baseSpeciesCount;
        if (excess < 0) return fileId;
        return excess < AltForms.Count ? AltForms[excess].IconId : 0;
    }

    public static PersonalDataRecord Load(RomProject project, int id)
    {
        string path = Path.Combine(project.PersonalDataDirectory, id.ToString("D4"));
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);

        var record = new PersonalDataRecord
        {
            BaseHP = reader.ReadByte(),
            BaseAtk = reader.ReadByte(),
            BaseDef = reader.ReadByte(),
            BaseSpeed = reader.ReadByte(),
            BaseSpAtk = reader.ReadByte(),
            BaseSpDef = reader.ReadByte(),
            Type1 = (PokemonType)reader.ReadByte(),
            Type2 = (PokemonType)reader.ReadByte(),
            CatchRate = reader.ReadByte(),
            GivenExp = reader.ReadByte(),
        };

        ushort evData = reader.ReadUInt16();
        record.EvHP = (byte)(evData & 0b11);
        record.EvAtk = (byte)((evData >> 2) & 0b11);
        record.EvDef = (byte)((evData >> 4) & 0b11);
        record.EvSpeed = (byte)((evData >> 6) & 0b11);
        record.EvSpAtk = (byte)((evData >> 8) & 0b11);
        record.EvSpDef = (byte)((evData >> 10) & 0b11);

        record.Item1 = reader.ReadUInt16();
        record.Item2 = reader.ReadUInt16();
        record.GenderRatio = reader.ReadByte();
        record.EggSteps = reader.ReadByte();
        record.BaseFriendship = reader.ReadByte();
        record.GrowthCurve = (PokemonGrowthCurve)reader.ReadByte();
        record.EggGroup1 = reader.ReadByte();
        record.EggGroup2 = reader.ReadByte();
        record.FirstAbility = reader.ReadByte();
        record.SecondAbility = reader.ReadByte();
        record.EscapeRate = reader.ReadByte();

        byte colorAndFlip = reader.ReadByte();
        record.Color = (PokemonDexColor)(colorAndFlip & 0b01111111);
        record.Flip = ((colorAndFlip >> 7) & 0b1) == 1;

        reader.ReadBytes(2); // Alignment

        uint tm1 = reader.ReadUInt32();
        uint tm2 = reader.ReadUInt32();
        uint tm3 = reader.ReadUInt32();
        uint tm4 = reader.ReadUInt32();
        record.Machines = BitFieldToMachines([tm1, tm2, tm3, tm4]);

        return record;
    }

    public static void Save(RomProject project, int id, PersonalDataRecord record) =>
        EditJournal.Track(project, EditJournalDocuments.PersonalData, EditJournalDocuments.Keys(("species", id)), () => SaveUntracked(project, id, record));

    private static void SaveUntracked(RomProject project, int id, PersonalDataRecord record)
    {
        Directory.CreateDirectory(project.PersonalDataDirectory);
        string path = Path.Combine(project.PersonalDataDirectory, id.ToString("D4"));

        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream))
        {
            writer.Write(record.BaseHP);
            writer.Write(record.BaseAtk);
            writer.Write(record.BaseDef);
            writer.Write(record.BaseSpeed);
            writer.Write(record.BaseSpAtk);
            writer.Write(record.BaseSpDef);
            writer.Write((byte)record.Type1);
            writer.Write((byte)record.Type2);
            writer.Write(record.CatchRate);
            writer.Write(record.GivenExp);

            ushort evData = (ushort)((record.EvHP & 0b11) |
                                      ((record.EvAtk & 0b11) << 2) |
                                      ((record.EvDef & 0b11) << 4) |
                                      ((record.EvSpeed & 0b11) << 6) |
                                      ((record.EvSpAtk & 0b11) << 8) |
                                      ((record.EvSpDef & 0b11) << 10));
            writer.Write(evData);

            writer.Write(record.Item1);
            writer.Write(record.Item2);
            writer.Write(record.GenderRatio);
            writer.Write(record.EggSteps);
            writer.Write(record.BaseFriendship);
            writer.Write((byte)record.GrowthCurve);
            writer.Write(record.EggGroup1);
            writer.Write(record.EggGroup2);
            writer.Write(record.FirstAbility);
            writer.Write(record.SecondAbility);
            writer.Write(record.EscapeRate);

            byte colorAndFlip = (byte)(((byte)record.Color & 0b01111111) | (((record.Flip ? 1 : 0) & 0b1) << 7));
            writer.Write(colorAndFlip);

            writer.Write((ushort)0); // Alignment

            uint[] bitfield = MachinesToBitField(record.Machines);
            foreach (uint word in bitfield)
            {
                writer.Write(word);
            }
        }

        File.WriteAllBytes(path, stream.ToArray());
    }

    private static SortedSet<byte> BitFieldToMachines(uint[] bitfield)
    {
        var result = new SortedSet<byte>();
        for (int i = 0; i < bitfield.Length; i++)
        {
            for (int bit = 0; bit < 32; bit++)
            {
                if ((bitfield[i] & (1u << bit)) != 0)
                {
                    result.Add((byte)(i * 32 + bit));
                }
            }
        }

        return result;
    }

    private static uint[] MachinesToBitField(SortedSet<byte> machines)
    {
        uint[] bitfield = new uint[4];
        foreach (byte machine in machines)
        {
            if (machine >= MachineCount) continue;
            bitfield[machine / 32] |= 1u << (machine % 32);
        }

        return bitfield;
    }

    public static int GetTypeNamesArchiveId(RomGameMetadata metadata) => metadata.HeaderFormat switch
    {
        MapHeaderFormat.DiamondPearl => 565,
        MapHeaderFormat.Platinum => 624,
        MapHeaderFormat.HeartGoldSoulSilver => 735,
        _ => 624
    };

    public static string MachineLabelFromIndex(int index) =>
        index < TmsCount ? $"TM{index + 1:00}" : $"HM{index - TmsCount + 1:00}";

    /// <summary>Reads the ARM9 machine-move table (100 x u16, one move ID per TM/HM slot) - used only to
    /// label the compatibility list, never edited from this editor (that's the TM editor's job).</summary>
    public static int[] ReadMachineMoves(RomProject project, RomGameMetadata metadata)
    {
        int[] moves = new int[MachineCount];
        int offset = GetMachineMoveArm9Offset(metadata);
        if (offset < 0 || !File.Exists(project.Arm9Path)) return moves;

        byte[] arm9 = File.ReadAllBytes(project.Arm9Path);
        for (int i = 0; i < moves.Length; i++)
        {
            int byteOffset = offset + i * 2;
            if (byteOffset + 2 > arm9.Length) break;
            moves[i] = BitConverter.ToUInt16(arm9, byteOffset);
        }

        return moves;
    }

    internal static int GetMachineMoveArm9Offset(RomGameMetadata metadata) => (metadata.HeaderFormat, metadata.Language) switch
    {
        (MapHeaderFormat.DiamondPearl, RomLanguage.English) => 0xF84EC,
        (MapHeaderFormat.DiamondPearl, RomLanguage.Japanese) => 0xFA458,
        (MapHeaderFormat.DiamondPearl, RomLanguage.French) => 0xF8530,
        (MapHeaderFormat.DiamondPearl, RomLanguage.German) => 0xF8500,
        (MapHeaderFormat.DiamondPearl, RomLanguage.Italian) => 0xF84A4,
        (MapHeaderFormat.DiamondPearl, RomLanguage.Spanish) => 0xF853C,
        (MapHeaderFormat.Platinum, RomLanguage.English) => 0xF0BFC,
        (MapHeaderFormat.Platinum, RomLanguage.Japanese) => 0xF028C,
        (MapHeaderFormat.Platinum, RomLanguage.French) => 0xF0C84,
        (MapHeaderFormat.Platinum, RomLanguage.German) => 0xF0C54,
        (MapHeaderFormat.Platinum, RomLanguage.Italian) => 0xF0C18,
        (MapHeaderFormat.Platinum, RomLanguage.Spanish) => 0xF0C90,
        (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.English) => 0x1000CC,
        (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.Japanese) => 0xFF84C,
        (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.French) => 0x1000B0,
        (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.German) => 0x100080,
        (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.Italian) => 0x100044,
        (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.Spanish) => 0x1000B4,
        _ => -1
    };

    /// <summary>The hatch-result table (pms.narc, not really a NARC - see RomProject.HatchResultPath):
    /// 2 bytes/species, the species ID a Pokemon's egg hatches into.</summary>
    public static int GetHatchResult(RomProject project, int monId)
    {
        if (monId < 0 || !File.Exists(project.HatchResultPath)) return 0;
        using FileStream stream = File.OpenRead(project.HatchResultPath);
        long offset = monId * 2L;
        if (offset + 2 > stream.Length) return 0;
        stream.Position = offset;
        using BinaryReader reader = new(stream);
        return reader.ReadUInt16();
    }

    public static void SetHatchResult(RomProject project, int monId, int hatchResult) =>
        EditJournal.Track(project, EditJournalDocuments.HatchResult, EditJournalDocuments.Keys(("species", monId)), () => SetHatchResultUntracked(project, monId, hatchResult));

    private static void SetHatchResultUntracked(RomProject project, int monId, int hatchResult)
    {
        if (monId < 0 || !File.Exists(project.HatchResultPath)) return;
        using FileStream stream = new(project.HatchResultPath, FileMode.Open, FileAccess.Write);
        long offset = monId * 2L;
        if (offset + 2 > stream.Length) return;
        stream.Position = offset;
        using BinaryWriter writer = new(stream);
        writer.Write((ushort)hatchResult);
    }

    /// <summary>0-255 gender-ratio byte to the same "100% Male"/"100% Female"/"Gender Unknown"/split
    /// percentage text the legacy editor shows, verbatim (PersonalDataEditor.GetGenderText).</summary>
    public static string GetGenderText(int genderRatio)
    {
        if (genderRatio == 0) return "100% Male";
        if (genderRatio == 254) return "100% Female";
        if (genderRatio == 255) return "Gender Unknown";

        float femaleProb = 100 * ((genderRatio + 1) / 256f);
        return $"{100 - femaleProb}% Male / {femaleProb}% Female";
    }
}
