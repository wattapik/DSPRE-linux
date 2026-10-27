namespace DSPRE.Core;

/// <summary>One row of the HGSS conditional music table: while Flag is set, HeaderId's own music is
/// overridden with MusicId.</summary>
public sealed class ConditionalMusicEntry
{
    public ushort HeaderId { get; set; }
    public ushort Flag { get; set; }
    public ushort MusicId { get; set; }
}

/// <summary>A battle "effect combo": a VS. intro animation paired with a battle theme.</summary>
public sealed class BattleEffectCombo
{
    public ushort VsGraphic { get; set; }
    public ushort BattleMusic { get; set; }
}

/// <summary>Links a trainer class or a wild Pokemon (SubjectId) to an effect combo. Stored as one
/// u16: the low 10 bits are the subject, the high 6 the combo.</summary>
public sealed class BattleEffectLink
{
    public const int MaxSubjectId = 1023;
    public const int MaxComboId = 63;

    public ushort SubjectId { get; set; }
    public ushort ComboId { get; set; }
}

public enum BattleTableKind
{
    ConditionalMusic,
    EffectCombos,
    VsTrainer,
    VsPokemon
}

/// <summary>The Core counterpart of the legacy DS_Map/Editors/TableEditor.cs. Every table is found
/// through a pointer stored in the ARM9 (offsets from RomInfo.SetConditionalMusicTableOffsetToRAMAddress
/// and SetBattleEffectsData) and edited in place; entry counts are fixed by the game code, so rows can
/// be changed but not added or removed.</summary>
public static class BattleTableService
{
    private const uint Arm9RamAddress = 0x02000000;
    private const uint SynthOverlayRamAddress = 0x023C8000;
    private const int PlatinumComboCount = 35;

    private sealed record TableLocation(long Offset, int Count);

    public static bool IsSupported(RomGameMetadata metadata, BattleTableKind kind) => metadata.HeaderFormat switch
    {
        MapHeaderFormat.HeartGoldSoulSilver => true,
        MapHeaderFormat.Platinum => kind == BattleTableKind.EffectCombos,
        _ => false
    };

    public static List<ConditionalMusicEntry> LoadConditionalMusic(RomProject project, RomGameMetadata metadata)
    {
        byte[] arm9 = File.ReadAllBytes(project.Arm9Path);
        TableLocation table = Locate(arm9, metadata, BattleTableKind.ConditionalMusic);
        var entries = new List<ConditionalMusicEntry>(table.Count);
        for (int i = 0; i < table.Count; i++)
        {
            int offset = (int)table.Offset + i * 6;
            entries.Add(new ConditionalMusicEntry
            {
                HeaderId = BitConverter.ToUInt16(arm9, offset),
                Flag = BitConverter.ToUInt16(arm9, offset + 2),
                MusicId = BitConverter.ToUInt16(arm9, offset + 4)
            });
        }

        return entries;
    }

    public static void SaveConditionalMusic(RomProject project, RomGameMetadata metadata, List<ConditionalMusicEntry> entries) =>
        EditJournal.Track(project, EditJournalDocuments.ConditionalMusic, EditJournalDocuments.Keys(), () =>
            Write(project, metadata, BattleTableKind.ConditionalMusic, entries.Count, 6, (writer, i) =>
            {
                writer.Write(entries[i].HeaderId);
                writer.Write(entries[i].Flag);
                writer.Write(entries[i].MusicId);
            }));

    public static List<BattleEffectCombo> LoadEffectCombos(RomProject project, RomGameMetadata metadata)
    {
        byte[] arm9 = File.ReadAllBytes(project.Arm9Path);
        TableLocation table = Locate(arm9, metadata, BattleTableKind.EffectCombos);
        var combos = new List<BattleEffectCombo>(table.Count);
        for (int i = 0; i < table.Count; i++)
        {
            int offset = (int)table.Offset + i * 4;
            combos.Add(new BattleEffectCombo
            {
                VsGraphic = BitConverter.ToUInt16(arm9, offset),
                BattleMusic = BitConverter.ToUInt16(arm9, offset + 2)
            });
        }

        return combos;
    }

    public static void SaveEffectCombos(RomProject project, RomGameMetadata metadata, List<BattleEffectCombo> combos) =>
        EditJournal.Track(project, EditJournalDocuments.BattleEffectCombos, EditJournalDocuments.Keys(), () =>
            Write(project, metadata, BattleTableKind.EffectCombos, combos.Count, 4, (writer, i) =>
            {
                writer.Write(combos[i].VsGraphic);
                writer.Write(combos[i].BattleMusic);
            }));

    public static List<BattleEffectLink> LoadLinks(RomProject project, RomGameMetadata metadata, BattleTableKind kind)
    {
        RequireLinkKind(kind);
        byte[] arm9 = File.ReadAllBytes(project.Arm9Path);
        TableLocation table = Locate(arm9, metadata, kind);
        var links = new List<BattleEffectLink>(table.Count);
        for (int i = 0; i < table.Count; i++)
        {
            ushort entry = BitConverter.ToUInt16(arm9, (int)table.Offset + i * 2);
            links.Add(new BattleEffectLink { SubjectId = (ushort)(entry & BattleEffectLink.MaxSubjectId), ComboId = (ushort)(entry >> 10) });
        }

        return links;
    }

    public static void SaveLinks(RomProject project, RomGameMetadata metadata, BattleTableKind kind, List<BattleEffectLink> links)
    {
        RequireLinkKind(kind);
        foreach (BattleEffectLink link in links)
        {
            if (link.SubjectId > BattleEffectLink.MaxSubjectId || link.ComboId > BattleEffectLink.MaxComboId)
            {
                throw new InvalidDataException(
                    $"Entry {link.SubjectId} / combo {link.ComboId} does not fit: IDs go up to {BattleEffectLink.MaxSubjectId} and combos up to {BattleEffectLink.MaxComboId}.");
            }
        }

        JournalDocument document = kind == BattleTableKind.VsTrainer ? EditJournalDocuments.VsTrainerEffects : EditJournalDocuments.VsPokemonEffects;
        EditJournal.Track(project, document, EditJournalDocuments.Keys(), () =>
            Write(project, metadata, kind, links.Count, 2, (writer, i) =>
                writer.Write((ushort)(links[i].SubjectId | (links[i].ComboId << 10)))));
    }

    private static void RequireLinkKind(BattleTableKind kind)
    {
        if (kind is not (BattleTableKind.VsTrainer or BattleTableKind.VsPokemon)) throw new ArgumentOutOfRangeException(nameof(kind));
    }

    private static void Write(RomProject project, RomGameMetadata metadata, BattleTableKind kind, int count, int entrySize, Action<BinaryWriter, int> writeEntry)
    {
        TableLocation table = Locate(File.ReadAllBytes(project.Arm9Path), metadata, kind);
        if (count != table.Count)
        {
            throw new InvalidDataException($"This table holds exactly {table.Count} entries; {count} were supplied.");
        }

        using FileStream stream = new(project.Arm9Path, FileMode.Open, FileAccess.Write);
        using BinaryWriter writer = new(stream);
        for (int i = 0; i < count; i++)
        {
            stream.Position = table.Offset + (long)i * entrySize;
            writeEntry(writer, i);
        }
    }

    private static TableLocation Locate(byte[] arm9, RomGameMetadata metadata, BattleTableKind kind)
    {
        if (!IsSupported(metadata, kind)) throw new NotSupportedException("This table does not exist in this game.");

        bool hgss = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;
        bool japanese = metadata.Language == RomLanguage.Japanese;
        // Spanish HeartGold's pointers all sit 8 bytes lower than every other western HGSS build's.
        int shift = hgss && metadata.Language == RomLanguage.Spanish && metadata.GameCode.StartsWith("IPK", StringComparison.OrdinalIgnoreCase) ? -8 : 0;

        (int pointerOffset, int countOffsetFromPointer, int entrySize) = kind switch
        {
            BattleTableKind.ConditionalMusic => ((japanese ? 0x66238 : 0x667D8) + shift, -8, 6),
            BattleTableKind.VsPokemon => ((japanese ? 0x5136C : 0x518D4) + shift, -0xA, 2),
            BattleTableKind.VsTrainer => ((japanese ? 0x51328 : 0x51890) + shift, -0xA, 2),
            BattleTableKind.EffectCombos when hgss => ((japanese ? 0x51260 : 0x517C8) + shift, -0x1E, 4),
            BattleTableKind.EffectCombos => (metadata.Language switch
            {
                RomLanguage.English => 0x51BE0,
                RomLanguage.Japanese => 0x514C0,
                _ => 0x51C84
            }, 0, 4),
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };

        if (pointerOffset + 4 > arm9.Length) throw new InvalidDataException("The ARM9 is smaller than expected; it may still be compressed.");

        uint ramAddress = BitConverter.ToUInt32(arm9, pointerOffset);
        if (ramAddress >= SynthOverlayRamAddress)
        {
            throw new NotSupportedException(
                "This table has been repointed into the expanded ARM9 file by the legacy Patch Toolbox, which this editor cannot edit yet.");
        }

        if (ramAddress < Arm9RamAddress) throw new InvalidDataException("The table pointer in the ARM9 is not a valid address.");

        int count = countOffsetFromPointer == 0 ? PlatinumComboCount : arm9[pointerOffset + countOffsetFromPointer];
        long offset = ramAddress - Arm9RamAddress;
        if (offset + (long)count * entrySize > arm9.Length) throw new InvalidDataException("The table does not fit inside the ARM9.");
        return new TableLocation(offset, count);
    }
}
