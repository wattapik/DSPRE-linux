using System.Buffers.Binary;
using System.Text;

namespace DSPRE.Core;

/// <summary>Item icons (item_icon.narc): an item's ARM9 table entry names an NCGR (one 32x32 4bpp
/// frame, 8x8 tiles in row order, matching the single 32x32 cell all item icons share) and an NCLR in the
/// same NARC. Unlike Pokémon icons each item has its own palette file, so an imported image brings its
/// own colors: up to 15 plus transparency. Many items share an icon or palette file (every TM shares the
/// disc, recolored per type by its palette), so an import can either overwrite the item's files in place
/// or append new files to the NARC for this item alone.</summary>
public static class ItemIconService
{
    public const int Size = 32;
    private const int TileBytes = Size * Size / 2;
    private const int TileDataOffset = 0x30;

    public sealed record ImportResult(int IconId, int PaletteId, int Colors, int SourceColors);

    /// <summary>The other items (below <paramref name="itemCount"/>) using this item's icon file and
    /// palette file.</summary>
    public static (List<int> IconSharers, List<int> PaletteSharers) FindSharers(RomProject project, RomGameMetadata metadata, int itemId, int itemCount)
    {
        ItemTableEntry[] entries = ItemDataService.ReadTableEntries(project, metadata, Math.Max(itemCount, itemId + 1));
        ItemTableEntry entry = entries[itemId];
        var icons = new List<int>();
        var palettes = new List<int>();
        for (int i = 0; i < itemCount; i++)
        {
            if (i == itemId) continue;
            if (entries[i].ItemIconId == entry.ItemIconId) icons.Add(i);
            if (entries[i].ItemPaletteId == entry.ItemPaletteId) palettes.Add(i);
        }

        return (icons, palettes);
    }

    /// <summary>Replaces the item's icon with a 32x32 image. Pixels under half opacity become transparent.
    /// The image's colors (at the DS's 5 bits per channel) become the palette; more than 15 are reduced
    /// to 15 by median cut. With <paramref name="newFiles"/> the item gets a new icon and palette file
    /// appended to the NARC, starting from copies of its current ones, and its table entry is pointed at
    /// them; otherwise its current files are overwritten.</summary>
    public static ImportResult Import(RomProject project, RomGameMetadata metadata, int itemId, string imagePath, bool newFiles)
    {
        string source = Path.GetFullPath(imagePath);
        byte[] rgba = BattleSpriteService.DecodeImageFileToRgba(source, out int width, out int height);
        if (width != Size || height != Size) throw new InvalidDataException($"The image is {width}x{height}; an item icon is 32x32.");

        if (ItemDataService.GetItemTableArm9Offset(metadata) < 0) throw new InvalidOperationException("This ROM's item table location is not known.");
        ItemTableEntry entry = ItemDataService.ReadTableEntry(project, metadata, itemId);
        string iconPath = FilePath(project, entry.ItemIconId);
        string palettePath = FilePath(project, entry.ItemPaletteId);
        byte[] icon = File.ReadAllBytes(iconPath);
        byte[] palette = File.ReadAllBytes(palettePath);
        int colorsStart = IndexedImage.PaletteColorsStart(palette, palettePath);
        ValidateIcon(icon, iconPath);

        ushort[] current = IndexedImage.ReadColors(palette, colorsStart);
        (ushort[] colors, byte[] indices, int sourceColors, bool keptPalette) = IndexedImage.Quantize(rgba, current);
        IndexedImage.EncodeTiled(indices, Size, Size).CopyTo(icon, TileDataOffset);
        if (!keptPalette) IndexedImage.WriteColors(palette, colorsStart, colors);

        if (newFiles)
        {
            int next = Directory.GetFiles(project.ItemIconsDirectory)
                .Select(Path.GetFileName)
                .Select(name => int.TryParse(name, out int id) ? id : -1)
                .Max() + 1;
            entry = entry with { ItemIconId = (ushort)next, ItemPaletteId = (ushort)(next + 1) };
            iconPath = FilePath(project, next);
            palettePath = FilePath(project, next + 1);
        }

        File.WriteAllBytes(iconPath, icon);
        File.WriteAllBytes(palettePath, palette);
        if (newFiles) ItemDataService.WriteTableEntryUntracked(project, metadata, itemId, entry);

        EditJournal.Record(project, "ImportItemIcon", ("item", itemId), ("source", source), ("newFiles", newFiles));
        return new ImportResult(entry.ItemIconId, entry.ItemPaletteId, keptPalette ? sourceColors : colors.Length, sourceColors);
    }

    private static string FilePath(RomProject project, int fileId) => Path.Combine(project.ItemIconsDirectory, fileId.ToString("D4"));

    private static void ValidateIcon(byte[] icon, string path)
    {
        bool valid = icon.Length >= TileDataOffset + TileBytes
            && Encoding.ASCII.GetString(icon, 0, 4) == "RGCN"
            && BinaryPrimitives.ReadUInt32LittleEndian(icon.AsSpan(0x1C)) == 3
            && BinaryPrimitives.ReadUInt32LittleEndian(icon.AsSpan(0x28)) == TileBytes
            && (BinaryPrimitives.ReadUInt32LittleEndian(icon.AsSpan(0x24)) & 0xFF) == 0;
        if (!valid) throw new InvalidDataException($"Icon file {Path.GetFileName(path)} is not a tiled 32x32 16-color image.");
    }

}
