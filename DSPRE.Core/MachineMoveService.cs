namespace DSPRE.Core;

/// <summary>One TM/HM: the move it teaches and the palette its bag icon is drawn with.</summary>
public sealed class MachineEntry
{
    public ushort MoveId { get; set; }
    public ushort PaletteId { get; set; }
}

/// <summary>The Core counterpart of the legacy DS_Map/Editors/TMEditor.cs. The move table is 100 u16s
/// in the ARM9 (92 TMs, then 8 HMs); each machine's palette is the palette field of its own row in the
/// ARM9 item table, machines being items 328 onwards.</summary>
public static class MachineMoveService
{
    public const int FirstMachineItemId = 328;
    private const int ItemTableEntrySize = 8;
    private const int ItemTablePaletteField = 4;

    // Item-icon palette per move type, in PokemonType order. The IDs are not sequential or ordered by
    // type in the ROM; the Unknown ("???") type has no palette of its own and uses Normal's.
    private static readonly Dictionary<PokemonType, ushort> TypePalettes = new()
    {
        [PokemonType.Normal] = 402,
        [PokemonType.Fighting] = 398,
        [PokemonType.Flying] = 413,
        [PokemonType.Poison] = 403,
        [PokemonType.Ground] = 410,
        [PokemonType.Rock] = 412,
        [PokemonType.Bug] = 610,
        [PokemonType.Ghost] = 411,
        [PokemonType.Steel] = 408,
        [PokemonType.Fire] = 406,
        [PokemonType.Water] = 400,
        [PokemonType.Grass] = 405,
        [PokemonType.Electric] = 409,
        [PokemonType.Psychic] = 401,
        [PokemonType.Ice] = 404,
        [PokemonType.Dragon] = 399,
        [PokemonType.Dark] = 407
    };

    public static ushort PaletteForType(PokemonType type) =>
        TypePalettes.TryGetValue(type, out ushort palette) ? palette : TypePalettes[PokemonType.Normal];

    /// <summary>Null for a palette that is not one of the type palettes (a custom one).</summary>
    public static PokemonType? TypeForPalette(ushort paletteId)
    {
        foreach ((PokemonType type, ushort palette) in TypePalettes)
        {
            if (palette == paletteId) return type;
        }

        return null;
    }

    public static List<MachineEntry> Load(RomProject project, RomGameMetadata metadata)
    {
        (int movesOffset, long itemTableOffset) = GetOffsets(metadata);
        byte[] arm9 = File.ReadAllBytes(project.Arm9Path);
        RequireFits(arm9.Length, movesOffset, itemTableOffset);

        var machines = new List<MachineEntry>(PersonalDataService.MachineCount);
        for (int i = 0; i < PersonalDataService.MachineCount; i++)
        {
            machines.Add(new MachineEntry
            {
                MoveId = BitConverter.ToUInt16(arm9, movesOffset + i * 2),
                PaletteId = BitConverter.ToUInt16(arm9, (int)PaletteOffset(itemTableOffset, i))
            });
        }

        return machines;
    }

    public static void Save(RomProject project, RomGameMetadata metadata, List<MachineEntry> machines) =>
        EditJournal.Track(project, EditJournalDocuments.MachineMoves, EditJournalDocuments.Keys(), () =>
        {
            if (machines.Count != PersonalDataService.MachineCount)
            {
                throw new InvalidDataException($"There are exactly {PersonalDataService.MachineCount} TMs/HMs; {machines.Count} were supplied.");
            }

            (int movesOffset, long itemTableOffset) = GetOffsets(metadata);
            using FileStream stream = new(project.Arm9Path, FileMode.Open, FileAccess.Write);
            RequireFits(stream.Length, movesOffset, itemTableOffset);
            using BinaryWriter writer = new(stream);
            for (int i = 0; i < machines.Count; i++)
            {
                stream.Position = movesOffset + i * 2;
                writer.Write(machines[i].MoveId);
                stream.Position = PaletteOffset(itemTableOffset, i);
                writer.Write(machines[i].PaletteId);
            }
        });

    private static long PaletteOffset(long itemTableOffset, int machineIndex) =>
        itemTableOffset + (FirstMachineItemId + machineIndex) * ItemTableEntrySize + ItemTablePaletteField;

    private static (int MovesOffset, long ItemTableOffset) GetOffsets(RomGameMetadata metadata)
    {
        int movesOffset = PersonalDataService.GetMachineMoveArm9Offset(metadata);
        long itemTableOffset = ItemDataService.GetItemTableArm9Offset(metadata);
        if (movesOffset < 0 || itemTableOffset < 0) throw new NotSupportedException("The TM/HM tables' location is not known for this game version.");
        return (movesOffset, itemTableOffset);
    }

    private static void RequireFits(long arm9Length, int movesOffset, long itemTableOffset)
    {
        if (movesOffset + PersonalDataService.MachineCount * 2 > arm9Length ||
            PaletteOffset(itemTableOffset, PersonalDataService.MachineCount - 1) + 2 > arm9Length)
        {
            throw new InvalidDataException("The ARM9 is smaller than expected; it may still be compressed.");
        }
    }
}
