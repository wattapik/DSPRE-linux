using System.Buffers.Binary;
using System.Text;

namespace DSPRE.Core;

/// <summary>hzla's PlatPatches "overworld sprites" expansion (github.com/hzla/PlatPatches,
/// src/patches/overworld-sprites.js), Platinum only: once applied, the overworld tables live in the
/// synthetic overlay with room for custom entries. DSPRE never applies the patch; on a patched ROM it
/// reads the moved tables and adds/deletes custom entries within the reserved capacity. Ported from
/// legacy DS_Map/OverworldSpriteTableExpansion.cs.
///
/// Layout in the synthetic overlay file: the marker "OWTBLXPANDV1", u32 version at +0x10, capacity at
/// +0x14, used custom count at +0x18, then four tables back to back from +0x20 - renderer behaviour
/// (8-byte rows), render properties (8), texture association (8; entry id -> sprite file) and animation
/// (16) - each reserved at (original rows + capacity + 1) rows. A row's first u32 is its entry id; 0xFFFF
/// ends a table.</summary>
public static class OverworldTableExpansion
{
    private const string Marker = "OWTBLXPANDV1";
    private const int HeaderSize = 0x20;
    private const uint Terminator = 0xFFFF;
    private static readonly int[] EntrySizes = [8, 8, 8, 16];
    public const int RenderPropertiesTable = 1;
    public const int TextureTable = 2;
    private const int PlatinumSyntheticOverlayFile = 9;

    public sealed record Table(long Start, int EntrySize, int OriginalRows);

    public sealed record Layout(string Path, long MarkerOffset, uint Capacity, uint Used, IReadOnlyList<Table> Tables)
    {
        public int Rows(int table) => Tables[table].OriginalRows + (int)Used;
    }

    public static string SyntheticOverlayPath(RomProject project) =>
        System.IO.Path.Combine(project.SyntheticOverlayDirectory, PlatinumSyntheticOverlayFile.ToString("D4"));

    /// <summary>The expansion's layout, or null when the ROM is not Platinum or not patched. Unpacks the
    /// synthetic overlay first when that has not happened yet.</summary>
    public static Layout? Detect(RomProject project, RomGameMetadata metadata)
    {
        if (metadata.HeaderFormat != MapHeaderFormat.Platinum) return null;
        string archive = project.SyntheticOverlayArchivePath(metadata);
        if (!Directory.Exists(project.SyntheticOverlayDirectory) && File.Exists(archive)) NarcExtractor.Extract(archive, project.SyntheticOverlayDirectory);
        string path = SyntheticOverlayPath(project);
        if (!File.Exists(path)) return null;

        byte[] data = File.ReadAllBytes(path);
        int marker = data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(Marker));
        if (marker < 0 || marker + HeaderSize > data.Length) return null;

        uint capacity = U32(data, marker + 0x14);
        uint used = U32(data, marker + 0x18);
        var tables = new List<Table>();
        long cursor = marker + HeaderSize;
        foreach (int entrySize in EntrySizes)
        {
            int terminatorRow = FindTerminator(data, cursor, entrySize);
            int originalRows = terminatorRow - (int)used;
            if (terminatorRow < 0 || originalRows < 0) return null;
            tables.Add(new Table(cursor, entrySize, originalRows));
            cursor += (long)(originalRows + capacity + 1) * entrySize;
        }

        return new Layout(path, marker, capacity, used, tables);
    }

    /// <summary>Entry id -> sprite file, in table order.</summary>
    public static List<(uint EntryId, uint SpriteId)> ReadTextureTable(Layout layout)
    {
        byte[] data = File.ReadAllBytes(layout.Path);
        Table table = layout.Tables[TextureTable];
        return Enumerable.Range(0, layout.Rows(TextureTable))
            .Select(row => table.Start + (long)row * table.EntrySize)
            .Select(offset => (U32(data, offset), U32(data, offset + 4)))
            .ToList();
    }

    /// <summary>Offset of the entry's render-properties row (key, then the render-state word), or -1.</summary>
    public static long FindRenderPropertiesRow(Layout layout, byte[] data, uint entryId)
    {
        int row = FindRow(data, layout, RenderPropertiesTable, entryId);
        return row < 0 ? -1 : layout.Tables[RenderPropertiesTable].Start + (long)row * layout.Tables[RenderPropertiesTable].EntrySize;
    }

    public static bool IsCustom(Layout layout, uint entryId)
    {
        int row = FindRow(File.ReadAllBytes(layout.Path), layout, TextureTable, entryId);
        return row >= layout.Tables[TextureTable].OriginalRows;
    }

    /// <summary>Why an id cannot be an overworld entry (ids the game reserves), or null.</summary>
    public static string? CheckEntryId(uint id) => id switch
    {
        > 0xFFFF => $"0x{id:X} is outside the 16-bit entry id range.",
        0xFFFF => "0xFFFF ends the table.",
        0x64 or (>= 0x65 and <= 0x74) => $"0x{id:X} is reserved for field graphics.",
        >= 0x1000 and <= 0x10C0 => $"0x{id:X} is reserved for berry growth.",
        0x2000 => "0x2000 is reserved.",
        _ => null
    };

    /// <summary>The first free, allowed id above the highest one in use.</summary>
    public static uint? SuggestEntryId(Layout layout)
    {
        var used = ReadTextureTable(layout).Select(entry => entry.EntryId).ToHashSet();
        if (used.Count == 0) return null;
        for (uint candidate = used.Max() + 1; candidate < 0xFFFF; candidate++)
        {
            if (!used.Contains(candidate) && CheckEntryId(candidate) is null) return candidate;
        }

        return null;
    }

    /// <summary>One past the highest numbered sprite file: a fresh slot for a new entry's own art.</summary>
    public static uint NextSpriteSlot(RomProject project) => (uint)HeaderFileService.NextFileId(project.OverworldSpritesDirectory);

    /// <summary>Adds a custom entry showing <paramref name="spriteId"/>, its behaviour, render properties
    /// and animation copied from <paramref name="cloneFrom"/>. With <paramref name="copySpriteFrom"/>,
    /// <paramref name="spriteId"/> is a new sprite file made as a copy of that one (a size/palette
    /// template the caller then imports an image into), so no existing entry's art is touched.</summary>
    public static void Add(RomProject project, RomGameMetadata metadata, uint entryId, uint spriteId, uint cloneFrom, uint? copySpriteFrom = null)
    {
        Layout layout = Detect(project, metadata) ?? throw new InvalidOperationException("The overworld sprite expansion patch is not applied to this ROM.");
        if (layout.Used >= layout.Capacity) throw new InvalidOperationException($"No free custom slots left ({layout.Used}/{layout.Capacity} used).");
        if (CheckEntryId(entryId) is string problem) throw new InvalidOperationException(problem);
        if (CheckEntryId(cloneFrom) is string cloneProblem) throw new InvalidOperationException("Clone source: " + cloneProblem);

        byte[] data = File.ReadAllBytes(layout.Path);
        for (int table = 0; table < 4; table++)
        {
            if (FindRow(data, layout, table, entryId) >= 0) throw new InvalidOperationException($"Entry 0x{entryId:X} already exists.");
        }

        var cloneRows = new int[4];
        foreach (int table in new[] { 0, 1, 3 })
        {
            cloneRows[table] = FindRow(data, layout, table, cloneFrom);
            if (cloneRows[table] < 0) throw new InvalidOperationException($"Clone source 0x{cloneFrom:X} is missing from table {table}.");
        }

        if (copySpriteFrom is uint template)
        {
            File.Copy(OverworldSpriteService.GetSpritePath(project, template), OverworldSpriteService.GetSpritePath(project, spriteId), overwrite: false);
        }

        for (int table = 0; table < 4; table++)
        {
            Table t = layout.Tables[table];
            long insert = t.Start + (long)layout.Rows(table) * t.EntrySize;
            byte[] row = table == TextureTable ? new byte[8] : data.AsSpan((int)(t.Start + (long)cloneRows[table] * t.EntrySize), t.EntrySize).ToArray();
            BinaryPrimitives.WriteUInt32LittleEndian(row, entryId);
            if (table == TextureTable) BinaryPrimitives.WriteUInt32LittleEndian(row.AsSpan(4), spriteId);
            row.CopyTo(data, insert);
            Array.Clear(data, (int)insert + t.EntrySize, t.EntrySize);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan((int)insert + t.EntrySize), Terminator);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan((int)layout.MarkerOffset + 0x18), layout.Used + 1);
        File.WriteAllBytes(layout.Path, data);
        EditJournal.Record(project, "AddOverworldEntry", ("entry", (int)entryId), ("sprite", (int)spriteId), ("cloneFrom", (int)cloneFrom),
            ("copySpriteFrom", copySpriteFrom is uint copied ? (int)copied : null));
    }

    /// <summary>Removes a custom entry from all four tables, closing the gap. Original entries cannot be
    /// deleted.</summary>
    public static void Delete(RomProject project, RomGameMetadata metadata, uint entryId)
    {
        Layout layout = Detect(project, metadata) ?? throw new InvalidOperationException("The overworld sprite expansion patch is not applied to this ROM.");
        byte[] data = File.ReadAllBytes(layout.Path);
        int textureRow = FindRow(data, layout, TextureTable, entryId);
        if (textureRow < 0) throw new InvalidOperationException($"Entry 0x{entryId:X} was not found.");
        if (textureRow < layout.Tables[TextureTable].OriginalRows) throw new InvalidOperationException("Original overworld entries cannot be deleted, only custom ones.");

        for (int table = 0; table < 4; table++)
        {
            int row = FindRow(data, layout, table, entryId);
            if (row < 0) continue;
            Table t = layout.Tables[table];
            long from = t.Start + (long)(row + 1) * t.EntrySize;
            long through = t.Start + (long)(layout.Rows(table) + 1) * t.EntrySize;
            // Every later row, terminator included, moves back one.
            Array.Copy(data, from, data, from - t.EntrySize, through - from);
            Array.Clear(data, (int)(through - t.EntrySize), t.EntrySize);
        }

        BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan((int)layout.MarkerOffset + 0x18), layout.Used - 1);
        File.WriteAllBytes(layout.Path, data);
        EditJournal.Record(project, "DeleteOverworldEntry", ("entry", (int)entryId));
    }

    private static int FindRow(byte[] data, Layout layout, int table, uint key)
    {
        Table t = layout.Tables[table];
        for (int row = 0; row <= layout.Rows(table); row++)
        {
            long offset = t.Start + (long)row * t.EntrySize;
            if (offset + 4 > data.Length) break;
            uint value = U32(data, offset);
            if (value == key) return row;
            if (value == Terminator) break;
        }

        return -1;
    }

    private static int FindTerminator(byte[] data, long start, int entrySize)
    {
        for (int row = 0; row < 4096; row++)
        {
            long offset = start + (long)row * entrySize;
            if (offset + 4 > data.Length) return -1;
            if (U32(data, offset) == Terminator) return row;
        }

        return -1;
    }

    private static uint U32(byte[] data, long offset) => BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan((int)offset));
}
