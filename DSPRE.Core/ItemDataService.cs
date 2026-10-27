namespace DSPRE.Core;

public enum NaturalGiftType
{
    Normal = 0,
    Fighting = 1,
    Flying = 2,
    Poison = 3,
    Ground = 4,
    Rock = 5,
    Bug = 6,
    Ghost = 7,
    Steel = 8,
    Fire = 10,
    Water = 11,
    Grass = 12,
    Electric = 13,
    Psychic = 14,
    Ice = 15,
    Dragon = 16,
    Dark = 17,
    None = 31
}

public enum FieldUseFunc : byte
{
    Generic = 0,
    HealingItem = 1,
    Dummy1 = 2,
    Dummy2 = 3,
    Bicycle = 4,
    Dummy3 = 5,
    TMHM = 6,
    Mail = 7,
    Berry = 8,
    Dummy4 = 9,
    PalPad = 10,
    Dummy5 = 11,
    Dummy6 = 12,
    Dummy7 = 13,
    Honey = 14,
    Dummy8 = 15,
    OldRod = 16,
    GoodRod = 17,
    SuperRod = 18,
    Generic2 = 19,
    EvoStone = 20,
    EscapeRope = 21,
    Dummy9 = 22,
    ApricornBox = 23,
    BerryPots = 24,
    UnownReport = 25,
    DowsingMchn = 26,
    GbSounds = 27,
    Gracidea = 28,
    VSRecorder = 29
}

public enum BattleUseFunc : byte
{
    NoUse = 0,
    Ball = 1,
    Healing = 2,
    Escape = 3
}

public enum FieldPocket
{
    Items = 0,
    Medicine = 1,
    Balls = 2,
    TmHms = 3,
    Berries = 4,
    Mail = 5,
    BattleItems = 6,
    KeyItems = 7
}

[Flags]
public enum BattlePocket
{
    None = 0,
    PokeBalls = 1 << 0,
    BattleItems = 1 << 1,
    HpRestore = 1 << 2,
    StatusHealers = 1 << 3,
    PpRestore = 1 << 4
}

/// <summary>Ported verbatim from the legacy HoldEffect enum (DS_Map/ROMFiles/ItemData.cs) - the
/// commented-out hg-engine-only values (147+) don't exist in vanilla Gen IV and are omitted.</summary>
public enum HoldEffect
{
    None = 0,
    HpRestore = 1,
    GiratinaBoost = 2,
    DialgaBoost = 3,
    PalkiaBoost = 4,
    PrzRestore = 5,
    SlpRestore = 6,
    PsnRestore = 7,
    BrnRestore = 8,
    FrzRestore = 9,
    PpRestore = 10,
    ConfuseRestore = 11,
    StatusRestore = 12,
    HpPctRestore = 13,
    HpRestoreSpicy = 14,
    HpRestoreDry = 15,
    HpRestoreSweet = 16,
    HpRestoreBitter = 17,
    HpRestoreSour = 18,
    WeakenSeFire = 19,
    WeakenSeWater = 20,
    WeakenSeElectric = 21,
    WeakenSeGrass = 22,
    WeakenSeIce = 23,
    WeakenSeFight = 24,
    WeakenSePoison = 25,
    WeakenSeGround = 26,
    WeakenSeFlying = 27,
    WeakenSePsychic = 28,
    WeakenSeBug = 29,
    WeakenSeRock = 30,
    WeakenSeGhost = 31,
    WeakenSeDragon = 32,
    WeakenSeDark = 33,
    WeakenSeSteel = 34,
    WeakenNormal = 35,
    PinchAtkUp = 36,
    PinchDefUp = 37,
    PinchSpeedUp = 38,
    PinchSpAtkUp = 39,
    PinchSpDefUp = 40,
    PinchCritrateUp = 41,
    PinchRandomUp = 42,
    HpRestoreSe = 43,
    PinchAccUp = 44,
    PinchPriority = 45,
    RecoilPhysical = 46,
    RecoilSpecial = 47,
    AccReduce = 48,
    StatdownRestore = 49,
    EvsUpSpeedDown = 50,
    ExpShare = 51,
    SometimesPriority = 52,
    FriendshipUp = 53,
    HealInfatuation = 54,
    ChoiceAtk = 55,
    SometimesFlinch = 56,
    StrengthenBug = 57,
    MoneyUp = 58,
    EncountersDown = 59,
    LatiSpecial = 60,
    ClamperlSpAtk = 61,
    ClamperlSpDef = 62,
    Flee = 63,
    NoEvolve = 64,
    MaybeEndure = 65,
    ExpUp = 66,
    CritrateUp = 67,
    StrengthenSteel = 68,
    HpRestoreGradual = 69,
    EvolveSeadra = 70,
    PikaSpAtkUp = 71,
    StrengthenGround = 72,
    StrengthenRock = 73,
    StrengthenGrass = 74,
    StrengthenDark = 75,
    StrengthenFight = 76,
    StrengthenElectric = 77,
    StrengthenWater = 78,
    StrengthenFlying = 79,
    StrengthenPoison = 80,
    StrengthenIce = 81,
    StrengthenGhost = 82,
    StrengthenPsychic = 83,
    StrengthenFire = 84,
    StrengthenDragon = 85,
    StrengthenNormal = 86,
    EvolvePorygon = 87,
    HpRestoreOnDmg = 88,
    ChanseyCritrateUp = 89,
    DittoDefUp = 90,
    CuboneAtkUp = 91,
    FarfetchdCritrateUp = 92,
    AccuracyUp = 93,
    PowerUpPhys = 94,
    PowerUpSpec = 95,
    PowerUpSe = 96,
    ExtendScreens = 97,
    HpDrainOnAtk = 98,
    ChargeSkip = 99,
    PsnUser = 100,
    BrnUser = 101,
    DittoSpeedUp = 102,
    Endure = 103,
    AccuracyUpSlower = 104,
    BoostRepeated = 105,
    SpeedDownGrounded = 106,
    PriorityDown = 107,
    ReciprocateInfat = 108,
    HpRestorePsnType = 109,
    ExtendHail = 110,
    ExtendSandstorm = 111,
    ExtendSun = 112,
    ExtendRain = 113,
    ExtendTrapping = 114,
    ChoiceSpeed = 115,
    DmgUserContactXfr = 116,
    LvlupAtkEvUp = 117,
    LvlupDefEvUp = 118,
    LvlupSpAtkEvUp = 119,
    LvlupSpDefEvUp = 120,
    LvlupSpeedEvUp = 121,
    LvlupHpEvUp = 122,
    Switch = 123,
    LeechBoost = 124,
    ChoiceSpAtk = 125,
    ArceusFire = 126,
    ArceusWater = 127,
    ArceusElectric = 128,
    ArceusGrass = 129,
    ArceusIce = 130,
    ArceusFighting = 131,
    ArceusPoison = 132,
    ArceusGround = 133,
    ArceusFlying = 134,
    ArceusPsychic = 135,
    ArceusBug = 136,
    ArceusRock = 137,
    ArceusGhost = 138,
    ArceusDragon = 139,
    ArceusDark = 140,
    ArceusSteel = 141,
    EvolveRhydon = 142,
    EvolveElectabuzz = 143,
    EvolveMagmar = 144,
    EvolvePorygon2 = 145,
    EvolveDusclops = 146,
}

/// <summary>Bit-packed party-use (in-bag "use on Pokemon") parameters, byte-exact port of
/// ItemData.ItemPartyUseParam (DS_Map/ROMFiles/ItemData.cs), 21 bytes total.</summary>
public sealed class ItemPartyUseParam
{
    public bool SlpHeal, PsnHeal, BrnHeal, FrzHeal, PrzHeal, CfsHeal, InfHeal, GuardSpec;
    public bool Revive, ReviveAll, LevelUp, Evolve;
    public sbyte AtkStages, DefStages, SpAtkStages, SpDefStages, SpeedStages, AccuracyStages, CritRateStages;
    public bool PPUps, PPMax, PPRestore, PPRestoreAll, HPRestore;
    public bool EVHp, EVAtk, EVDef, EVSpeed, EVSpAtk, EVSpDef;
    public bool FriendshipLow, FriendshipMid, FriendshipHigh;
    public sbyte EVHpValue, EVAtkValue, EVDefValue, EVSpeedValue, EVSpAtkValue, EVSpDefValue;
    public byte HPRestoreParam, PPRestoreParam;
    public sbyte FriendshipLowValue, FriendshipMidValue, FriendshipHighValue;

    public static ItemPartyUseParam Read(BinaryReader reader)
    {
        var p = new ItemPartyUseParam();

        byte b0 = reader.ReadByte();
        p.SlpHeal = (b0 & (1 << 0)) != 0;
        p.PsnHeal = (b0 & (1 << 1)) != 0;
        p.BrnHeal = (b0 & (1 << 2)) != 0;
        p.FrzHeal = (b0 & (1 << 3)) != 0;
        p.PrzHeal = (b0 & (1 << 4)) != 0;
        p.CfsHeal = (b0 & (1 << 5)) != 0;
        p.InfHeal = (b0 & (1 << 6)) != 0;
        p.GuardSpec = (b0 & (1 << 7)) != 0;

        byte b1 = reader.ReadByte();
        p.Revive = (b1 & (1 << 0)) != 0;
        p.ReviveAll = (b1 & (1 << 1)) != 0;
        p.LevelUp = (b1 & (1 << 2)) != 0;
        p.Evolve = (b1 & (1 << 3)) != 0;
        p.AtkStages = (sbyte)((b1 >> 4) & 0xF);

        byte b2 = reader.ReadByte();
        p.DefStages = (sbyte)(b2 & 0xF);
        p.SpAtkStages = (sbyte)((b2 >> 4) & 0xF);

        byte b3 = reader.ReadByte();
        p.SpDefStages = (sbyte)(b3 & 0xF);
        p.SpeedStages = (sbyte)((b3 >> 4) & 0xF);

        byte b4 = reader.ReadByte();
        p.AccuracyStages = (sbyte)(b4 & 0xF);
        p.CritRateStages = (sbyte)((b4 >> 4) & 0x3);
        p.PPUps = (b4 & (1 << 6)) != 0;
        p.PPMax = (b4 & (1 << 7)) != 0;

        byte b5 = reader.ReadByte();
        p.PPRestore = (b5 & (1 << 0)) != 0;
        p.PPRestoreAll = (b5 & (1 << 1)) != 0;
        p.HPRestore = (b5 & (1 << 2)) != 0;
        p.EVHp = (b5 & (1 << 3)) != 0;
        p.EVAtk = (b5 & (1 << 4)) != 0;
        p.EVDef = (b5 & (1 << 5)) != 0;
        p.EVSpeed = (b5 & (1 << 6)) != 0;
        p.EVSpAtk = (b5 & (1 << 7)) != 0;

        byte b6 = reader.ReadByte();
        p.EVSpDef = (b6 & (1 << 0)) != 0;
        p.FriendshipLow = (b6 & (1 << 1)) != 0;
        p.FriendshipMid = (b6 & (1 << 2)) != 0;
        p.FriendshipHigh = (b6 & (1 << 3)) != 0;

        p.EVHpValue = reader.ReadSByte();
        p.EVAtkValue = reader.ReadSByte();
        p.EVDefValue = reader.ReadSByte();
        p.EVSpeedValue = reader.ReadSByte();
        p.EVSpAtkValue = reader.ReadSByte();
        p.EVSpDefValue = reader.ReadSByte();
        p.HPRestoreParam = reader.ReadByte();
        p.PPRestoreParam = reader.ReadByte();
        p.FriendshipLowValue = reader.ReadSByte();
        p.FriendshipMidValue = reader.ReadSByte();
        p.FriendshipHighValue = reader.ReadSByte();

        reader.BaseStream.Seek(2, SeekOrigin.Current); // padding

        return p;
    }

    public void Write(BinaryWriter writer)
    {
        byte b0 = 0;
        b0 |= (byte)(SlpHeal ? 1 << 0 : 0);
        b0 |= (byte)(PsnHeal ? 1 << 1 : 0);
        b0 |= (byte)(BrnHeal ? 1 << 2 : 0);
        b0 |= (byte)(FrzHeal ? 1 << 3 : 0);
        b0 |= (byte)(PrzHeal ? 1 << 4 : 0);
        b0 |= (byte)(CfsHeal ? 1 << 5 : 0);
        b0 |= (byte)(InfHeal ? 1 << 6 : 0);
        b0 |= (byte)(GuardSpec ? 1 << 7 : 0);
        writer.Write(b0);

        byte b1 = 0;
        b1 |= (byte)(Revive ? 1 << 0 : 0);
        b1 |= (byte)(ReviveAll ? 1 << 1 : 0);
        b1 |= (byte)(LevelUp ? 1 << 2 : 0);
        b1 |= (byte)(Evolve ? 1 << 3 : 0);
        b1 |= (byte)((AtkStages & 0x0F) << 4);
        writer.Write(b1);

        byte b2 = 0;
        b2 |= (byte)(DefStages & 0x0F);
        b2 |= (byte)((SpAtkStages & 0x0F) << 4);
        writer.Write(b2);

        byte b3 = 0;
        b3 |= (byte)(SpDefStages & 0x0F);
        b3 |= (byte)((SpeedStages & 0x0F) << 4);
        writer.Write(b3);

        byte b4 = 0;
        b4 |= (byte)(AccuracyStages & 0x0F);
        b4 |= (byte)((CritRateStages & 0x03) << 4);
        b4 |= (byte)(PPUps ? 1 << 6 : 0);
        b4 |= (byte)(PPMax ? 1 << 7 : 0);
        writer.Write(b4);

        byte b5 = 0;
        b5 |= (byte)(PPRestore ? 1 << 0 : 0);
        b5 |= (byte)(PPRestoreAll ? 1 << 1 : 0);
        b5 |= (byte)(HPRestore ? 1 << 2 : 0);
        b5 |= (byte)(EVHp ? 1 << 3 : 0);
        b5 |= (byte)(EVAtk ? 1 << 4 : 0);
        b5 |= (byte)(EVDef ? 1 << 5 : 0);
        b5 |= (byte)(EVSpeed ? 1 << 6 : 0);
        b5 |= (byte)(EVSpAtk ? 1 << 7 : 0);
        writer.Write(b5);

        byte b6 = 0;
        b6 |= (byte)(EVSpDef ? 1 << 0 : 0);
        b6 |= (byte)(FriendshipLow ? 1 << 1 : 0);
        b6 |= (byte)(FriendshipMid ? 1 << 2 : 0);
        b6 |= (byte)(FriendshipHigh ? 1 << 3 : 0);
        writer.Write(b6);

        writer.Write(EVHpValue);
        writer.Write(EVAtkValue);
        writer.Write(EVDefValue);
        writer.Write(EVSpeedValue);
        writer.Write(EVSpAtkValue);
        writer.Write(EVSpDefValue);
        writer.Write(HPRestoreParam);
        writer.Write(PPRestoreParam);
        writer.Write(FriendshipLowValue);
        writer.Write(FriendshipMidValue);
        writer.Write(FriendshipHighValue);

        writer.Write((byte)0);
        writer.Write((byte)0);
    }
}

/// <summary>Byte-exact port of the legacy ItemData record (DS_Map/ROMFiles/ItemData.cs).</summary>
public sealed class ItemRecord
{
    public ushort Price;
    public HoldEffect HoldEffectValue;
    public byte HoldEffectParam;
    public byte PluckEffect;
    public byte FlingEffect;
    public byte FlingPower;
    public byte NaturalGiftPower;
    public NaturalGiftType NaturalGiftTypeValue;
    public bool PreventToss;
    public bool Selectable;
    public FieldPocket FieldPocketValue;
    public BattlePocket BattlePocketValue;
    public FieldUseFunc FieldUseFuncValue;
    public BattleUseFunc BattleUseFuncValue;
    public byte PartyUse;
    public ItemPartyUseParam PartyUseParam = new();
}

/// <summary>One row of the ARM9 item table: which item-data file, icon (NCGR), and palette (NCLR) file a
/// given item ID uses. Items commonly share a single item-data file (e.g. all "X Attack"-alikes), which
/// is why item DATA is keyed separately from item ID - exactly mirroring the alt-form indirection used
/// elsewhere in this codebase, but implemented as an ARM9 lookup table instead of a hardcoded list.</summary>
public sealed record ItemTableEntry(ushort ItemDataId, ushort ItemIconId, ushort ItemPaletteId, ushort ItemAgbId);

public static class ItemDataService
{
    private const int TableEntrySize = 8;

    /// <summary>The descriptions archive sits just before the names archive in every version (checked on
    /// Platinum 391/392 and HGSS 221/222; legacy DSPRE only knew the HGSS one).</summary>
    public static int GetItemDescriptionsArchiveId(RomGameMetadata metadata) => TrainerService.GetItemNamesArchiveId(metadata) - 1;

    public static IReadOnlyList<int> GetIds(RomProject project)
    {
        if (!Directory.Exists(project.ItemDataDirectory)) return [];
        return Directory.GetFiles(project.ItemDataDirectory)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => int.TryParse(name, out _))
            .Select(int.Parse)
            .OrderBy(id => id)
            .ToArray();
    }

    public static int GetFileCount(RomProject project) =>
        Directory.Exists(project.ItemDataDirectory) ? Directory.GetFiles(project.ItemDataDirectory).Length : 0;

    public static ItemRecord Load(RomProject project, int dataId)
    {
        string path = Path.Combine(project.ItemDataDirectory, dataId.ToString("D4"));
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);

        var item = new ItemRecord
        {
            Price = reader.ReadUInt16(),
            HoldEffectValue = (HoldEffect)reader.ReadByte(),
            HoldEffectParam = reader.ReadByte(),
            PluckEffect = reader.ReadByte(),
            FlingEffect = reader.ReadByte(),
            FlingPower = reader.ReadByte(),
            NaturalGiftPower = reader.ReadByte(),
        };

        ushort bitfield = reader.ReadUInt16();
        item.NaturalGiftTypeValue = (NaturalGiftType)(bitfield & 0b0001_1111);
        item.PreventToss = (bitfield & (1 << 5)) != 0;
        item.Selectable = (bitfield & (1 << 6)) != 0;
        item.FieldPocketValue = (FieldPocket)((bitfield >> 7) & 0b1111);
        item.BattlePocketValue = (BattlePocket)((bitfield >> 11) & 0b11111);

        item.FieldUseFuncValue = (FieldUseFunc)reader.ReadByte();
        item.BattleUseFuncValue = (BattleUseFunc)reader.ReadByte();
        item.PartyUse = reader.ReadByte();
        reader.ReadByte(); // padding

        item.PartyUseParam = ItemPartyUseParam.Read(reader);
        reader.ReadBytes(2); // padding

        return item;
    }

    public static void Save(RomProject project, int dataId, ItemRecord item) =>
        EditJournal.Track(project, EditJournalDocuments.ItemData, EditJournalDocuments.Keys(("itemData", dataId)), () => SaveUntracked(project, dataId, item));

    private static void SaveUntracked(RomProject project, int dataId, ItemRecord item)
    {
        Directory.CreateDirectory(project.ItemDataDirectory);
        string path = Path.Combine(project.ItemDataDirectory, dataId.ToString("D4"));

        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream))
        {
            writer.Write(item.Price);
            writer.Write((byte)item.HoldEffectValue);
            writer.Write(item.HoldEffectParam);
            writer.Write(item.PluckEffect);
            writer.Write(item.FlingEffect);
            writer.Write(item.FlingPower);
            writer.Write(item.NaturalGiftPower);

            ushort bitfield = 0;
            bitfield |= (ushort)((byte)item.NaturalGiftTypeValue & 0b11111);
            if (item.PreventToss) bitfield |= 1 << 5;
            if (item.Selectable) bitfield |= 1 << 6;
            bitfield |= (ushort)(((byte)item.FieldPocketValue & 0b1111) << 7);
            bitfield |= (ushort)(((byte)item.BattlePocketValue & 0b11111) << 11);
            writer.Write(bitfield);

            writer.Write((byte)item.FieldUseFuncValue);
            writer.Write((byte)item.BattleUseFuncValue);
            writer.Write(item.PartyUse);
            writer.Write((byte)0);

            item.PartyUseParam.Write(writer);

            writer.Write((byte)0);
            writer.Write((byte)0);
        }

        File.WriteAllBytes(path, stream.ToArray());
    }

    public static ItemTableEntry ReadTableEntry(RomProject project, RomGameMetadata metadata, int itemId)
    {
        long offset = GetItemTableArm9Offset(metadata) + itemId * TableEntrySize;
        if (offset < 0 || !File.Exists(project.Arm9Path))
        {
            return new ItemTableEntry(0, 0, 0, 0);
        }

        byte[] arm9 = File.ReadAllBytes(project.Arm9Path);
        if (offset + TableEntrySize > arm9.Length)
        {
            return new ItemTableEntry(0, 0, 0, 0);
        }

        ushort itemData = BitConverter.ToUInt16(arm9, (int)offset);
        ushort itemIcon = BitConverter.ToUInt16(arm9, (int)offset + 2);
        ushort itemPalette = BitConverter.ToUInt16(arm9, (int)offset + 4);
        ushort itemAgb = BitConverter.ToUInt16(arm9, (int)offset + 6);
        return new ItemTableEntry(itemData, itemIcon, itemPalette, itemAgb);
    }

    /// <summary>The first <paramref name="count"/> table entries, reading ARM9 once.</summary>
    public static ItemTableEntry[] ReadTableEntries(RomProject project, RomGameMetadata metadata, int count)
    {
        long start = GetItemTableArm9Offset(metadata);
        var entries = new ItemTableEntry[count];
        byte[] arm9 = start >= 0 && File.Exists(project.Arm9Path) ? File.ReadAllBytes(project.Arm9Path) : [];
        for (int i = 0; i < count; i++)
        {
            long offset = start + i * TableEntrySize;
            entries[i] = start < 0 || offset + TableEntrySize > arm9.Length
                ? new ItemTableEntry(0, 0, 0, 0)
                : new ItemTableEntry(BitConverter.ToUInt16(arm9, (int)offset), BitConverter.ToUInt16(arm9, (int)offset + 2),
                    BitConverter.ToUInt16(arm9, (int)offset + 4), BitConverter.ToUInt16(arm9, (int)offset + 6));
        }

        return entries;
    }

    public static void WriteTableEntry(RomProject project, RomGameMetadata metadata, int itemId, ItemTableEntry entry) =>
        EditJournal.Track(project, EditJournalDocuments.ItemTableEntry, EditJournalDocuments.Keys(("item", itemId)), () => WriteTableEntryUntracked(project, metadata, itemId, entry));

    internal static void WriteTableEntryUntracked(RomProject project, RomGameMetadata metadata, int itemId, ItemTableEntry entry)
    {
        long offset = GetItemTableArm9Offset(metadata) + itemId * TableEntrySize;
        if (offset < 0 || !File.Exists(project.Arm9Path)) return;

        using FileStream stream = new(project.Arm9Path, FileMode.Open, FileAccess.Write);
        if (offset + TableEntrySize > stream.Length) return;

        stream.Position = offset;
        using BinaryWriter writer = new(stream);
        writer.Write(entry.ItemDataId);
        writer.Write(entry.ItemIconId);
        writer.Write(entry.ItemPaletteId);
        writer.Write(entry.ItemAgbId);
    }

    /// <summary>Lists the file IDs in the item icons NARC that are NCGR (icon tile) files vs NCLR
    /// (palette) files, by sniffing each file's magic - matching the legacy ItemEditor's
    /// PopulateIconPaletteDropdowns exactly (icon and palette IDs are interleaved in the same NARC, not
    /// two separate ranges).</summary>
    public static (List<int> IconIds, List<int> PaletteIds) ListItemIconFiles(RomProject project)
    {
        var iconIds = new List<int>();
        var paletteIds = new List<int>();
        if (!Directory.Exists(project.ItemIconsDirectory)) return (iconIds, paletteIds);

        foreach (string file in Directory.GetFiles(project.ItemIconsDirectory))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(file), out int id)) continue;

            byte[] header = new byte[4];
            using (FileStream stream = File.OpenRead(file))
            {
                if (stream.Read(header, 0, 4) < 4) continue;
            }

            string magic = System.Text.Encoding.ASCII.GetString(header);
            if (magic == "RGCN") iconIds.Add(id);
            else if (magic == "RLCN") paletteIds.Add(id);
        }

        iconIds.Sort();
        paletteIds.Sort();
        return (iconIds, paletteIds);
    }

    internal static long GetItemTableArm9Offset(RomGameMetadata metadata) => (metadata.HeaderFormat, metadata.Language) switch
    {
        (MapHeaderFormat.DiamondPearl, RomLanguage.English) => 0xF85B4,
        (MapHeaderFormat.DiamondPearl, RomLanguage.Japanese) => 0xFA520,
        (MapHeaderFormat.DiamondPearl, RomLanguage.French) => 0xF85F8,
        (MapHeaderFormat.DiamondPearl, RomLanguage.German) => 0xF85C8,
        (MapHeaderFormat.DiamondPearl, RomLanguage.Italian) => 0xF856C,
        (MapHeaderFormat.DiamondPearl, RomLanguage.Spanish) => 0xF8604,
        (MapHeaderFormat.Platinum, RomLanguage.English) => 0xF0CC4,
        (MapHeaderFormat.Platinum, RomLanguage.Japanese) => 0xF0354,
        (MapHeaderFormat.Platinum, RomLanguage.French) => 0xF0D4C,
        (MapHeaderFormat.Platinum, RomLanguage.German) => 0xF0D1C,
        (MapHeaderFormat.Platinum, RomLanguage.Italian) => 0xF0CE0,
        (MapHeaderFormat.Platinum, RomLanguage.Spanish) => 0xF0D58,
        (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.English) => 0x100194,
        (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.Japanese) => 0xFF914,
        (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.French) => 0x100178,
        (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.German) => 0x100148,
        (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.Italian) => 0x10010C,
        (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.Spanish) => 0x10017C,
        _ => -1
    };
}
