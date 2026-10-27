namespace DSPRE.Core;

public enum EvolutionMethod : short
{
    None = 0,
    Friendship220 = 1,
    Friendship220_Day = 2,
    Friendship220_Night = 3,
    LevelingUp = 4,
    Trade = 5,
    Trade_HeldItem = 6,
    Item = 7,
    Atk_Greater_Def = 8,
    Atk_Equal_Def = 9,
    Def_Greater_Atk = 10,
    Personality1 = 11,
    Personality2 = 12,
    FreeSpaceCheck = 13,
    Shedinja = 14,
    BeautyThreshold = 15,
    ItemMale = 16,
    ItemFemale = 17,
    HeldItem_Day = 18,
    HeldItem_Night = 19,
    KnowsMove = 20,
    PartyPokemonPresence = 21,
    LevelingUp_Male = 22,
    LevelingUp_Female = 23,
    Loc_MtCoronet = 24,
    Loc_EternaForest = 25,
    Loc_Route217 = 26,
}

public enum EvolutionParamMeaning
{
    Ignored,
    FromLevel,
    ItemName,
    MoveName,
    PokemonName,
    BeautyValue,
}

public sealed class EvolutionSlot
{
    public EvolutionMethod Method { get; set; }
    public short Param { get; set; }
    public short Target { get; set; }

    /// <summary>Matches EvolutionData.isValid() exactly: the three leveling methods only require a
    /// 1-100 param (target is ignored for them); every other non-None method requires Target &gt; 0.</summary>
    public bool IsValid()
    {
        if (Method == EvolutionMethod.None) return false;
        if (Method is EvolutionMethod.LevelingUp or EvolutionMethod.LevelingUp_Male or EvolutionMethod.LevelingUp_Female)
        {
            return Param > 0 && Param <= 100;
        }

        return Target > 0;
    }
}

public sealed class EvolutionFile(int id, EvolutionSlot[] slots)
{
    public const int SlotCount = 7;
    public int Id { get; } = id;
    public EvolutionSlot[] Slots { get; } = slots;
}

/// <summary>Byte-exact port of the legacy EvolutionFile format (DS_Map/ROMFiles/EvolutionFile.cs): 7
/// fixed slots of 6 bytes each (short method, short param, short target), little-endian. Format is
/// identical across DP/Pt/HGSS - only the source NARC path differs.</summary>
public static class EvolutionService
{
    /// <summary>Method -&gt; what the Param field means, driving the target/param UI. Ported verbatim
    /// from EvolutionFile.evoDescriptions.</summary>
    public static readonly IReadOnlyDictionary<EvolutionMethod, EvolutionParamMeaning> ParamMeanings = new Dictionary<EvolutionMethod, EvolutionParamMeaning>
    {
        [EvolutionMethod.None] = EvolutionParamMeaning.Ignored,
        [EvolutionMethod.Friendship220] = EvolutionParamMeaning.Ignored,
        [EvolutionMethod.Friendship220_Day] = EvolutionParamMeaning.Ignored,
        [EvolutionMethod.Friendship220_Night] = EvolutionParamMeaning.Ignored,
        [EvolutionMethod.LevelingUp] = EvolutionParamMeaning.FromLevel,
        [EvolutionMethod.Trade] = EvolutionParamMeaning.Ignored,
        [EvolutionMethod.Trade_HeldItem] = EvolutionParamMeaning.ItemName,
        [EvolutionMethod.Item] = EvolutionParamMeaning.ItemName,
        [EvolutionMethod.Atk_Greater_Def] = EvolutionParamMeaning.FromLevel,
        [EvolutionMethod.Atk_Equal_Def] = EvolutionParamMeaning.FromLevel,
        [EvolutionMethod.Def_Greater_Atk] = EvolutionParamMeaning.FromLevel,
        [EvolutionMethod.Personality1] = EvolutionParamMeaning.FromLevel,
        [EvolutionMethod.Personality2] = EvolutionParamMeaning.FromLevel,
        [EvolutionMethod.FreeSpaceCheck] = EvolutionParamMeaning.FromLevel,
        [EvolutionMethod.Shedinja] = EvolutionParamMeaning.FromLevel,
        [EvolutionMethod.BeautyThreshold] = EvolutionParamMeaning.BeautyValue,
        [EvolutionMethod.ItemMale] = EvolutionParamMeaning.ItemName,
        [EvolutionMethod.ItemFemale] = EvolutionParamMeaning.ItemName,
        [EvolutionMethod.HeldItem_Day] = EvolutionParamMeaning.ItemName,
        [EvolutionMethod.HeldItem_Night] = EvolutionParamMeaning.ItemName,
        [EvolutionMethod.KnowsMove] = EvolutionParamMeaning.MoveName,
        [EvolutionMethod.PartyPokemonPresence] = EvolutionParamMeaning.PokemonName,
        [EvolutionMethod.LevelingUp_Male] = EvolutionParamMeaning.FromLevel,
        [EvolutionMethod.LevelingUp_Female] = EvolutionParamMeaning.FromLevel,
        [EvolutionMethod.Loc_MtCoronet] = EvolutionParamMeaning.Ignored,
        [EvolutionMethod.Loc_EternaForest] = EvolutionParamMeaning.Ignored,
        [EvolutionMethod.Loc_Route217] = EvolutionParamMeaning.Ignored,
    };

    public static IReadOnlyList<int> GetIds(RomProject project)
    {
        if (!Directory.Exists(project.EvolutionsDirectory)) return [];
        return Directory.GetFiles(project.EvolutionsDirectory)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => int.TryParse(name, out _))
            .Select(int.Parse)
            .OrderBy(id => id)
            .ToArray();
    }

    public static EvolutionFile Load(RomProject project, int id)
    {
        string path = Path.Combine(project.EvolutionsDirectory, id.ToString("D4"));
        byte[] data = File.ReadAllBytes(path);
        var slots = new EvolutionSlot[EvolutionFile.SlotCount];

        using MemoryStream stream = new(data);
        using BinaryReader reader = new(stream);
        for (int i = 0; i < EvolutionFile.SlotCount; i++)
        {
            slots[i] = stream.Position + 6 <= stream.Length
                ? new EvolutionSlot { Method = (EvolutionMethod)reader.ReadInt16(), Param = reader.ReadInt16(), Target = reader.ReadInt16() }
                : new EvolutionSlot { Method = EvolutionMethod.None, Param = 0, Target = 0 };
        }

        return new EvolutionFile(id, slots);
    }

    public static void Save(RomProject project, EvolutionFile file) =>
        EditJournal.Track(project, EditJournalDocuments.Evolution, EditJournalDocuments.Keys(("evolution", file.Id)), () => SaveUntracked(project, file));

    private static void SaveUntracked(RomProject project, EvolutionFile file)
    {
        Directory.CreateDirectory(project.EvolutionsDirectory);
        string path = Path.Combine(project.EvolutionsDirectory, file.Id.ToString("D4"));

        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream))
        {
            foreach (EvolutionSlot slot in file.Slots)
            {
                if (!slot.IsValid()) continue;
                writer.Write((short)slot.Method);
                writer.Write(slot.Param);
                writer.Write(slot.Target);
            }

            // Legacy writer pads the compacted (invalid slots dropped) stream up to 7*6+2 = 44 bytes.
            const int minSize = EvolutionFile.SlotCount * 6 + 2;
            if (stream.Length < minSize)
            {
                stream.SetLength(minSize);
            }
        }

        File.WriteAllBytes(path, stream.ToArray());
    }
}
