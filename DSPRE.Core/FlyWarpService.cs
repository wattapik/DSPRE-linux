namespace DSPRE.Core;

/// <summary>One row of the ARM9-resident fly warp table. DP/Pt and HGSS use different byte layouts (see
/// FlyWarpService.Load/Save), so this record is a superset covering both - HGSS-only fields
/// (FlagIdx/IsBlackoutSpawn/IsFlyPoint/HeaderIdUnlockWarp/GlobalXUnlock/GlobalYUnlock) are unused on
/// DP/Pt, and DP/Pt-only fields (IsTeleportPos/UnlockOnMapEntry/UnlockId) are unused on HGSS.</summary>
public sealed class FlyWarpEntry
{
    // "Game Over" / blackout respawn destination.
    public ushort HeaderIdGameOver;
    public int LocalX;
    public int LocalY;

    // Fly warp destination.
    public ushort HeaderIdFly;
    public int GlobalX;
    public int GlobalY;

    // DP/Pt unlock settings.
    public bool IsTeleportPos;
    public bool UnlockOnMapEntry;
    public ushort UnlockId;

    // HGSS unlock/flag settings.
    public byte FlagIdx;
    public bool IsBlackoutSpawn;
    public bool IsFlyPoint;
    public ushort HeaderIdUnlockWarp;
    public int GlobalXUnlock;
    public int GlobalYUnlock;
}

/// <summary>Byte-exact port of the legacy FlyEditor (DS_Map/FlyEditor/FlyEditor.cs): a fixed-size table
/// living directly in ARM9 (no NARC), with a completely different per-row byte layout and row count on
/// DP/Pt (16 bytes/row, 20 rows) vs HGSS (18 bytes/row, 30 rows).</summary>
public static class FlyWarpService
{
    private const int DiamondPearlPlatinumRowSize = 16;
    private const int HeartGoldSoulSilverRowSize = 18;

    public static int GetTableSize(RomGameMetadata metadata) =>
        metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver ? 30 : 20;

    public static List<FlyWarpEntry> Load(RomProject project, RomGameMetadata metadata)
    {
        int tableSize = GetTableSize(metadata);
        var entries = new List<FlyWarpEntry>(tableSize);

        long offset = GetArm9Offset(metadata);
        if (offset < 0 || !File.Exists(project.Arm9Path))
        {
            for (int i = 0; i < tableSize; i++) entries.Add(new FlyWarpEntry());
            return entries;
        }

        byte[] arm9 = File.ReadAllBytes(project.Arm9Path);
        bool isHgss = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;
        int rowSize = isHgss ? HeartGoldSoulSilverRowSize : DiamondPearlPlatinumRowSize;

        for (int i = 0; i < tableSize; i++)
        {
            long rowOffset = offset + i * rowSize;
            if (rowOffset + rowSize > arm9.Length)
            {
                entries.Add(new FlyWarpEntry());
                continue;
            }

            int pos = (int)rowOffset;
            var entry = new FlyWarpEntry();

            if (isHgss)
            {
                entry.FlagIdx = arm9[pos];
                byte flags = arm9[pos + 1];
                entry.IsBlackoutSpawn = (flags & 0x01) != 0;
                entry.IsFlyPoint = (flags & 0x02) != 0;
                entry.HeaderIdGameOver = BitConverter.ToUInt16(arm9, pos + 2);
                entry.LocalX = arm9[pos + 4];
                entry.LocalY = arm9[pos + 5];
                entry.HeaderIdFly = BitConverter.ToUInt16(arm9, pos + 6);
                entry.GlobalX = BitConverter.ToUInt16(arm9, pos + 8);
                entry.GlobalY = BitConverter.ToUInt16(arm9, pos + 10);
                entry.HeaderIdUnlockWarp = BitConverter.ToUInt16(arm9, pos + 12);
                entry.GlobalXUnlock = BitConverter.ToUInt16(arm9, pos + 14);
                entry.GlobalYUnlock = BitConverter.ToUInt16(arm9, pos + 16);
            }
            else
            {
                entry.HeaderIdGameOver = BitConverter.ToUInt16(arm9, pos);
                entry.LocalX = BitConverter.ToUInt16(arm9, pos + 2);
                entry.LocalY = BitConverter.ToUInt16(arm9, pos + 4);
                entry.HeaderIdFly = BitConverter.ToUInt16(arm9, pos + 6);
                entry.GlobalX = BitConverter.ToUInt16(arm9, pos + 8);
                entry.GlobalY = BitConverter.ToUInt16(arm9, pos + 10);
                entry.IsTeleportPos = arm9[pos + 12] == 1;
                entry.UnlockOnMapEntry = arm9[pos + 13] == 1;
                entry.UnlockId = BitConverter.ToUInt16(arm9, pos + 14);
            }

            entries.Add(entry);
        }

        return entries;
    }

    public static void Save(RomProject project, RomGameMetadata metadata, IReadOnlyList<FlyWarpEntry> entries) =>
        EditJournal.Track(project, EditJournalDocuments.FlyWarp, EditJournalDocuments.Keys(), () => SaveUntracked(project, metadata, entries));

    private static void SaveUntracked(RomProject project, RomGameMetadata metadata, IReadOnlyList<FlyWarpEntry> entries)
    {
        long offset = GetArm9Offset(metadata);
        if (offset < 0 || !File.Exists(project.Arm9Path)) return;

        bool isHgss = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;
        int rowSize = isHgss ? HeartGoldSoulSilverRowSize : DiamondPearlPlatinumRowSize;
        int tableSize = GetTableSize(metadata);

        using FileStream stream = new(project.Arm9Path, FileMode.Open, FileAccess.Write);
        if (offset + (long)tableSize * rowSize > stream.Length) return;

        stream.Position = offset;
        using BinaryWriter writer = new(stream);

        for (int i = 0; i < tableSize && i < entries.Count; i++)
        {
            FlyWarpEntry entry = entries[i];
            if (isHgss)
            {
                writer.Write(entry.FlagIdx);
                byte flags = (byte)((entry.IsBlackoutSpawn ? 0x01 : 0x00) | (entry.IsFlyPoint ? 0x02 : 0x00));
                writer.Write(flags);
                writer.Write(entry.HeaderIdGameOver);
                writer.Write((byte)Math.Clamp(entry.LocalX, 0, 255));
                writer.Write((byte)Math.Clamp(entry.LocalY, 0, 255));
                writer.Write(entry.HeaderIdFly);
                writer.Write((ushort)Math.Clamp(entry.GlobalX, 0, ushort.MaxValue));
                writer.Write((ushort)Math.Clamp(entry.GlobalY, 0, ushort.MaxValue));
                writer.Write(entry.HeaderIdUnlockWarp);
                writer.Write((ushort)Math.Clamp(entry.GlobalXUnlock, 0, ushort.MaxValue));
                writer.Write((ushort)Math.Clamp(entry.GlobalYUnlock, 0, ushort.MaxValue));
            }
            else
            {
                writer.Write(entry.HeaderIdGameOver);
                writer.Write((ushort)Math.Clamp(entry.LocalX, 0, ushort.MaxValue));
                writer.Write((ushort)Math.Clamp(entry.LocalY, 0, ushort.MaxValue));
                writer.Write(entry.HeaderIdFly);
                writer.Write((ushort)Math.Clamp(entry.GlobalX, 0, ushort.MaxValue));
                writer.Write((ushort)Math.Clamp(entry.GlobalY, 0, ushort.MaxValue));
                writer.Write(entry.IsTeleportPos ? (byte)1 : (byte)0);
                writer.Write(entry.UnlockOnMapEntry ? (byte)1 : (byte)0);
                writer.Write(entry.UnlockId);
            }
        }
    }

    private static long GetArm9Offset(RomGameMetadata metadata) => (metadata.HeaderFormat, metadata.Language) switch
    {
        (MapHeaderFormat.DiamondPearl, RomLanguage.Japanese) => 0xF41D0,
        (MapHeaderFormat.DiamondPearl, RomLanguage.English) => 0xF2224,
        (MapHeaderFormat.DiamondPearl, RomLanguage.French) => 0xF2264,
        (MapHeaderFormat.DiamondPearl, RomLanguage.German) => 0xF2234,
        (MapHeaderFormat.DiamondPearl, RomLanguage.Italian) => 0xF21D8,
        (MapHeaderFormat.DiamondPearl, RomLanguage.Spanish) => 0xF2270,
        (MapHeaderFormat.Platinum, RomLanguage.Japanese) => 0xE8E88,
        (MapHeaderFormat.Platinum, RomLanguage.English) => 0xE97B4,
        (MapHeaderFormat.Platinum, RomLanguage.French) => 0xE983C,
        (MapHeaderFormat.Platinum, RomLanguage.German) => 0xE980C,
        (MapHeaderFormat.Platinum, RomLanguage.Italian) => 0xE97D0,
        (MapHeaderFormat.Platinum, RomLanguage.Spanish) => 0xE9848,
        (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.Japanese) => 0xF9630,
        (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.English) => 0xF9E80,
        (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.French) => 0xF9E64,
        (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.German) => 0xF9E34,
        (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.Italian) => 0xF9DF8,
        (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.Spanish) => 0xF9E68,
        _ => -1
    };
}
