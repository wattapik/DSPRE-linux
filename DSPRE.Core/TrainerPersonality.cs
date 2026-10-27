namespace DSPRE.Core;

/// <summary>The personality value (PID) the game generates for a trainer's Pokémon, which fixes its
/// nature and, through the lowest bit, its ability. Port of legacy
/// DS_Map/Editors/TrainerEditor/DVCalculator/DVCalculator.cs (itself after turtleisaac's PokEditor and
/// pret's decompilations). One instance follows one trainer's party in order: on HGSS (and Platinum
/// with the AI backport) a gender or ability override changes the value later party members start
/// from, as in game.</summary>
public sealed class TrainerPersonality
{
    public static readonly IReadOnlyList<string> Natures =
    [
        "Hardy: Neutral", "Lonely: +Atk, -Def", "Brave: +Atk, -Spe", "Adamant: +Atk, -SpA", "Naughty: +Atk, -SpD",
        "Bold: +Def, -Atk", "Docile: Neutral", "Relaxed: +Def, -Spe", "Impish: +Def, -SpA", "Lax: +Def, -SpD",
        "Timid: +Spe, -Atk", "Hasty: +Spe, -Def", "Serious: Neutral", "Jolly: +Spe, -SpA", "Naive: +Spe, -SpD",
        "Modest: +SpA, -Atk", "Mild: +SpA, -Def", "Quiet: +SpA, -Spe", "Bashful: Neutral", "Rash: +SpA, -SpD",
        "Calm: +SpD, -Atk", "Gentle: +SpD, -Def", "Sassy: +SpD, -Spe", "Careful: +SpD, -SpA", "Quirky: Neutral"
    ];

    private readonly bool overridesChangeGenderMod;
    private readonly bool outdatedAiBackport;
    private uint genderMod;

    /// <param name="trainerClassIsMale">The trainer class's gender from the ARM9 table (see
    /// <see cref="ReadTrainerClassGenders"/>); classes past the table count as male.</param>
    public TrainerPersonality(RomProject project, RomGameMetadata metadata, bool trainerClassIsMale)
    {
        (bool aiBackport, bool outdated) = DetectAiBackport(project, metadata);
        overridesChangeGenderMod = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver || aiBackport;
        outdatedAiBackport = outdated;
        genderMod = trainerClassIsMale ? 136u : 120u;
    }

    /// <param name="genderOverride">The low 4 bits of the party entry's gender/ability byte.</param>
    /// <param name="abilityOverride">The high 4 bits of that byte.</param>
    public uint NextPid(int trainerId, int trainerClass, int species, int level, byte baseGenderRatio, int genderOverride, int abilityOverride, byte difficulty)
    {
        uint state = (uint)(trainerId + species + level + difficulty);
        uint random = 0;
        for (int i = 0; i < trainerClass; i++)
        {
            state = 1103515245 * state + 24691;
            random = state >> 16;
        }

        if (overridesChangeGenderMod && (genderOverride > 0 || abilityOverride > 0))
        {
            if (outdatedAiBackport) genderMod = (uint)(ushort)species;
            if (genderOverride == 1) genderMod = baseGenderRatio + 2u;
            else if (genderOverride == 2) genderMod = baseGenderRatio - 2u;
            if (abilityOverride == 1) genderMod &= ~1u;
            else if (abilityOverride == 2) genderMod |= 1u;
        }

        return (random << 8) + genderMod;
    }

    public static int NatureOf(uint pid) => (int)(pid % 100) % 25;

    /// <summary>The nature every DV from 255 down to 0 would give the next party member, without moving
    /// on to the member after it (legacy DVCalculator.getAllNatures).</summary>
    public List<(int Dv, int Iv, int Nature)> AllNatures(int trainerId, int trainerClass, int species, int level, byte baseGenderRatio, int genderOverride, int abilityOverride)
    {
        uint start = genderMod;
        var natures = new List<(int, int, int)>(256);
        for (int dv = 255; dv >= 0; dv--)
        {
            natures.Add((dv, dv * 31 / 255, NatureOf(NextPid(trainerId, trainerClass, species, level, baseGenderRatio, genderOverride, abilityOverride, (byte)dv))));
            genderMod = start;
        }

        return natures;
    }

    /// <summary>Whether party gender and ability flags exist on this ROM: HGSS, or Platinum with the AI
    /// backport (legacy shows them only then).</summary>
    public static bool FlagsApply(RomProject project, RomGameMetadata metadata) =>
        metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver || DetectAiBackport(project, metadata).Backport;

    private static readonly Dictionary<(MapHeaderFormat, RomLanguage), int> ClassGenderTableOffsets = new()
    {
        [(MapHeaderFormat.DiamondPearl, RomLanguage.English)] = 0xF8010,
        [(MapHeaderFormat.DiamondPearl, RomLanguage.Japanese)] = 0xF9F7C,
        [(MapHeaderFormat.DiamondPearl, RomLanguage.French)] = 0xF8054,
        [(MapHeaderFormat.DiamondPearl, RomLanguage.German)] = 0xF8024,
        [(MapHeaderFormat.DiamondPearl, RomLanguage.Italian)] = 0xF7FC8,
        [(MapHeaderFormat.DiamondPearl, RomLanguage.Spanish)] = 0xF8060,
        [(MapHeaderFormat.Platinum, RomLanguage.English)] = 0xF0714,
        [(MapHeaderFormat.Platinum, RomLanguage.Japanese)] = 0xEFDA4,
        [(MapHeaderFormat.Platinum, RomLanguage.French)] = 0xF079C,
        [(MapHeaderFormat.Platinum, RomLanguage.German)] = 0xF076C,
        [(MapHeaderFormat.Platinum, RomLanguage.Italian)] = 0xF0730,
        [(MapHeaderFormat.Platinum, RomLanguage.Spanish)] = 0xF07A8,
        [(MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.English)] = 0xFFB90,
        [(MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.Japanese)] = 0xFF310,
        [(MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.French)] = 0xFFB74,
        [(MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.German)] = 0xFFB44,
        [(MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.Italian)] = 0xFFB08,
        [(MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.Spanish)] = 0xFFB78,
    };

    /// <summary>Each trainer class's gender (true = male) from the ARM9 table; empty when the table's
    /// place is unknown for this ROM.</summary>
    public static bool[] ReadTrainerClassGenders(RomProject project, RomGameMetadata metadata)
    {
        int length = metadata.HeaderFormat switch
        {
            MapHeaderFormat.Platinum => 105,
            MapHeaderFormat.HeartGoldSoulSilver => 128,
            _ => 98
        };
        if (!ClassGenderTableOffsets.TryGetValue((metadata.HeaderFormat, metadata.Language), out int offset) || !File.Exists(project.Arm9Path)) return [];
        byte[] arm9 = File.ReadAllBytes(project.Arm9Path);
        if (offset + length > arm9.Length) return [];
        return arm9.AsSpan(offset, length).ToArray().Select(value => value != 1).ToArray();
    }

    /// <summary>Platinum ROMs with the HGSS trainer AI backport (legacy RomInfo.SetAIBackportEnabled):
    /// the current one, and Lhea's original whose gender handling differs.</summary>
    private static (bool Backport, bool Outdated) DetectAiBackport(RomProject project, RomGameMetadata metadata)
    {
        if (metadata.HeaderFormat != MapHeaderFormat.Platinum || !File.Exists(project.Arm9Path)) return (false, false);
        byte[] arm9 = File.ReadAllBytes(project.Arm9Path);
        bool At(int offset, byte[] expected) => offset + expected.Length <= arm9.Length && arm9.AsSpan(offset, expected.Length).SequenceEqual(expected);
        return (At(0x0793B8, [0xF0, 0xB5, 0x93, 0xB0]), At(0x0795A2, [0x1D, 0x1C, 0x0F, 0x23]));
    }
}
