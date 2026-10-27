namespace DSPRE.Core;

/// <summary>One row of the game's overworld table: the entry ID events refer to (an overworld's
/// "overlay table entry") and the sprite file it draws.</summary>
public sealed record OverworldSpriteEntry(uint EntryId, uint SpriteId);

/// <summary>A decoded overworld sprite sheet: every frame stacked in one 16-colour image. Shiny
/// variants exist only for sheets with exactly two 16-colour palettes.</summary>
public sealed record OverworldSpriteSheet(int Width, int Height, byte[] Rgba, int PaletteCount, int ColorCount, bool HasShinyPalette);

/// <summary>How the game draws an overworld (Platinum only): DrawType 0 none / 1 billboard / 2 3D
/// model, ShadowType 0 none / 1 on, FootmarkType 0 none / 1 normal / 2 cycle, ReflectType 0 none / 1 on.</summary>
public sealed class OverworldRenderState
{
    public int DrawType { get; set; }
    public int ShadowType { get; set; }
    public int FootmarkType { get; set; }
    public int ReflectType { get; set; }
}

/// <summary>The Core counterpart of the legacy DS_Map/Editors/BtxEditor (the "Overworld Editor"),
/// RomInfo.SetOWtable/ReadOWTable and DS_Map/LibNDSFormats/BTX0.cs. On a Platinum ROM patched with
/// hzla's overworld table expansion the tables are read from where the patch moved them (see
/// OverworldTableExpansion).</summary>
public static class OverworldSpriteService
{
    private const uint Btx0Magic = 0x30585442;
    private const uint Tex0Magic = 0x30584554;
    private const int Palette16Format = 3;
    private const uint TableTerminator = 0xFFFF;
    private const uint SynthOverlayRamAddress = 0x023C8000;
    private const int HgssFieldExtensionOverlay = 131;
    private const int RenderStateEntrySize = 8;

    private sealed record TableLocation(string Path, long Offset, bool Hgss);

    public static string GetSpritePath(RomProject project, uint spriteId) =>
        Path.Combine(project.OverworldSpritesDirectory, $"{spriteId:D4}");

    public static List<OverworldSpriteEntry> LoadTable(RomProject project, RomGameMetadata metadata)
    {
        if (OverworldTableExpansion.Detect(project, metadata) is { } expanded)
        {
            return OverworldTableExpansion.ReadTextureTable(expanded)
                .Select(entry => new OverworldSpriteEntry(entry.EntryId, entry.SpriteId))
                .OrderBy(entry => entry.EntryId)
                .ToList();
        }

        TableLocation table = LocateTable(project, metadata);
        byte[] data = File.ReadAllBytes(table.Path);
        var entries = new List<OverworldSpriteEntry>();
        long position = table.Offset;
        if (table.Hgss)
        {
            for (; position + 6 <= data.Length; position += 6)
            {
                ushort entryId = BitConverter.ToUInt16(data, (int)position);
                if (entryId == TableTerminator) break;
                entries.Add(new OverworldSpriteEntry(entryId, BitConverter.ToUInt16(data, (int)position + 2)));
            }
        }
        else
        {
            for (; position + 8 <= data.Length; position += 8)
            {
                uint entryId = BitConverter.ToUInt32(data, (int)position);
                if (entryId == TableTerminator) break;
                entries.Add(new OverworldSpriteEntry(entryId, BitConverter.ToUInt32(data, (int)position + 4)));
            }
        }

        return entries.OrderBy(entry => entry.EntryId).ToList();
    }

    /// <summary>Null when the file is not a BTX0 this editor understands (anything but a single
    /// 16-colour texture), which legacy reports as "not supported" too.</summary>
    public static OverworldSpriteSheet? Decode(byte[] btx, int paletteIndex)
    {
        if (!TryReadLayout(btx, out BtxLayout layout)) return null;
        paletteIndex = Math.Clamp(paletteIndex, 0, Math.Max(0, layout.PaletteCount - 1));

        var rgba = new byte[layout.Width * layout.Height * 4];
        int pixel = 0;
        for (int i = layout.ImageOffset; i < layout.PaletteOffset && pixel < layout.Width * layout.Height; i++)
        {
            foreach (int index in new[] { btx[i] & 0xF, btx[i] >> 4 })
            {
                int colorOffset = layout.PaletteOffset + paletteIndex * layout.ColorCount * 2 + index * 2;
                ushort color = index < layout.ColorCount && colorOffset + 2 <= btx.Length ? BitConverter.ToUInt16(btx, colorOffset) : (ushort)0;
                rgba[pixel * 4] = (byte)((color & 0x1F) << 3);
                rgba[pixel * 4 + 1] = (byte)((color & 0x3E0) >> 2);
                rgba[pixel * 4 + 2] = (byte)((color & 0x7C00) >> 7);
                rgba[pixel * 4 + 3] = 255;
                pixel++;
            }
        }

        return new OverworldSpriteSheet(
            layout.Width, layout.Height, rgba, layout.PaletteCount, layout.ColorCount,
            HasShinyPalette: layout.PaletteBytes == 64 && layout.PaletteCount == 2);
    }

    public static OverworldSpriteSheet? LoadSheet(RomProject project, uint spriteId, int paletteIndex)
    {
        string path = GetSpritePath(project, spriteId);
        return File.Exists(path) ? Decode(File.ReadAllBytes(path), paletteIndex) : null;
    }

    /// <summary>Replaces a sprite sheet's pixels and the chosen palette from an image file, which
    /// must be the sheet's exact size and use no more colours than the palette holds. As in legacy,
    /// palette slots are assigned in the order colours first appear, so the top-left pixel's colour
    /// becomes slot 0.</summary>
    public static void ImportImage(RomProject project, uint spriteId, int paletteIndex, string imagePath)
    {
        string fullSource = Path.GetFullPath(imagePath);
        string path = GetSpritePath(project, spriteId);
        byte[] btx = File.ReadAllBytes(path);
        if (!TryReadLayout(btx, out BtxLayout layout)) throw new InvalidDataException("This sprite file is not a supported 16-colour BTX0.");
        if (paletteIndex < 0 || paletteIndex >= Math.Max(1, layout.PaletteCount)) throw new InvalidDataException("This sprite has no such palette.");

        byte[] rgba = BattleSpriteService.DecodeImageFileToRgba(fullSource, out int width, out int height);
        if (width != layout.Width || height != layout.Height)
        {
            throw new InvalidDataException($"Not the same size. Sprite sheet: {layout.Width}x{layout.Height}, image: {width}x{height}.");
        }

        var palette = new List<uint>();
        var indices = new byte[width * height];
        for (int i = 0; i < indices.Length; i++)
        {
            uint color = BitConverter.ToUInt32(rgba, i * 4);
            int index = palette.IndexOf(color);
            if (index < 0)
            {
                index = palette.Count;
                palette.Add(color);
            }

            indices[i] = (byte)Math.Min(index, 255);
        }

        if (palette.Count > layout.ColorCount)
        {
            throw new InvalidDataException($"Too many colours. Sprite sheet: {layout.ColorCount}, image: {palette.Count}.");
        }

        for (int i = 0; i < indices.Length / 2 && layout.ImageOffset + i < layout.PaletteOffset; i++)
        {
            btx[layout.ImageOffset + i] = (byte)(indices[i * 2] | (indices[i * 2 + 1] << 4));
        }

        for (int i = 0; i < palette.Count; i++)
        {
            static int To5Bit(uint channel) => Math.Min(31, (int)Math.Round(channel / 8.0));
            uint color = palette[i];
            int packed = To5Bit(color & 0xFF) | (To5Bit((color >> 8) & 0xFF) << 5) | (To5Bit((color >> 16) & 0xFF) << 10);
            BitConverter.GetBytes((ushort)packed).CopyTo(btx, layout.PaletteOffset + paletteIndex * layout.ColorCount * 2 + i * 2);
        }

        File.WriteAllBytes(path, btx);

        // The map editors' event markers are drawn from PNGs cached from these sprite files.
        string markerCache = Path.Combine(project.RootPath, "expanded", "overworld_sprites");
        if (Directory.Exists(markerCache)) Directory.Delete(markerCache, recursive: true);

        EditJournal.Record(project, "ImportOverworldSpriteImage", ("sprite", (int)spriteId), ("palette", paletteIndex), ("source", fullSource));
    }

    public static bool SupportsRenderState(RomGameMetadata metadata) => metadata.HeaderFormat == MapHeaderFormat.Platinum;

    /// <summary>Null when the entry has no render-state row (or the game is not Platinum).</summary>
    public static OverworldRenderState? LoadRenderState(RomProject project, RomGameMetadata metadata, uint entryId)
    {
        if (!SupportsRenderState(metadata)) return null;
        (byte[] data, long row, _) = LocateRenderStateRow(project, metadata, entryId);
        if (row < 0) return null;

        uint bits = BitConverter.ToUInt32(data, (int)row + 4);
        return new OverworldRenderState
        {
            DrawType = (int)(bits & 0xF),
            ShadowType = (int)((bits >> 4) & 0x3),
            FootmarkType = (int)((bits >> 6) & 0xF),
            ReflectType = (int)((bits >> 10) & 0x3)
        };
    }

    public static void SaveRenderState(RomProject project, RomGameMetadata metadata, uint entryId, OverworldRenderState state) =>
        EditJournal.Track(project, EditJournalDocuments.OverworldRenderState, EditJournalDocuments.Keys(("entry", (int)entryId)), () =>
        {
            if (!SupportsRenderState(metadata)) throw new NotSupportedException("Overworld render state can only be edited in Platinum.");
            (byte[] data, long row, string path) = LocateRenderStateRow(project, metadata, entryId);
            if (row < 0) throw new InvalidDataException($"Overworld entry {entryId} has no render-state row.");

            // Only the four known fields are replaced; any other bits of the word are kept.
            uint bits = BitConverter.ToUInt32(data, (int)row + 4) & ~0xFFFu;
            bits |= ((uint)state.DrawType & 0xF) | (((uint)state.ShadowType & 0x3) << 4) | (((uint)state.FootmarkType & 0xF) << 6) | (((uint)state.ReflectType & 0x3) << 10);
            using FileStream stream = new(path, FileMode.Open, FileAccess.Write);
            stream.Position = row + 4;
            stream.Write(BitConverter.GetBytes(bits));
        });

    /// <summary>The file holding the render-state table, its bytes, and the entry's row in it (or -1).</summary>
    private static (byte[] Data, long Row, string Path) LocateRenderStateRow(RomProject project, RomGameMetadata metadata, uint entryId)
    {
        if (OverworldTableExpansion.Detect(project, metadata) is { } expanded)
        {
            byte[] expandedData = File.ReadAllBytes(expanded.Path);
            return (expandedData, OverworldTableExpansion.FindRenderPropertiesRow(expanded, expandedData, entryId), expanded.Path);
        }

        TableLocation table = LocateTable(project, metadata);
        byte[] data = File.ReadAllBytes(table.Path);
        return (data, FindRenderStateRow(data, table.Offset, entryId), table.Path);
    }

    /// <summary>The render-state table sits directly before the texture table, terminated by its own
    /// 0xFFFF row, so it is walked backwards from there. (Legacy assumes it starts 259 rows back,
    /// which on a real US Platinum ROM is one row late: it misses entry 0 and counts the terminator.)</summary>
    private static long FindRenderStateRow(byte[] data, long textureTableOffset, uint entryId)
    {
        long terminator = textureTableOffset - RenderStateEntrySize;
        if (terminator < 0 || BitConverter.ToUInt32(data, (int)terminator) != TableTerminator) return -1;

        for (long row = terminator - RenderStateEntrySize; row >= 0; row -= RenderStateEntrySize)
        {
            uint key = BitConverter.ToUInt32(data, (int)row);
            if (key == entryId) return row;
            // Keys ascend from 0, so reaching it without a match means the entry is not there.
            if (key == 0 || key > TableTerminator) return -1;
        }

        return -1;
    }

    private static TableLocation LocateTable(RomProject project, RomGameMetadata metadata)
    {
        if (metadata.HeaderFormat != MapHeaderFormat.HeartGoldSoulSilver)
        {
            long offset = (metadata.HeaderFormat, metadata.Language) switch
            {
                (MapHeaderFormat.DiamondPearl, RomLanguage.English) => 0x22BCC,
                (MapHeaderFormat.DiamondPearl, RomLanguage.Japanese) => 0x23BB8,
                (MapHeaderFormat.DiamondPearl, _) => 0x22B84,
                (MapHeaderFormat.Platinum, RomLanguage.Italian) => 0x2BC44,
                (MapHeaderFormat.Platinum, RomLanguage.French or RomLanguage.Spanish) => 0x2BC3C,
                (MapHeaderFormat.Platinum, RomLanguage.German) => 0x2BC50,
                (MapHeaderFormat.Platinum, RomLanguage.Japanese) => 0x2BA24,
                _ => 0x2BC34
            };
            return new TableLocation(RequireOverlay(project, 5, offset + 8), offset, Hgss: false);
        }

        uint pointerRamAddress = metadata.Language switch
        {
            RomLanguage.Italian => 0x021F929C,
            RomLanguage.French or RomLanguage.Spanish => 0x021F931C,
            RomLanguage.German => 0x021F92DC,
            RomLanguage.Japanese => 0x021F86C4,
            _ => 0x021F92FC
        };
        uint overlay1Address = OverlayService.GetRamAddress(project, 1);
        long pointerOffset = pointerRamAddress - overlay1Address;
        string overlay1Path = RequireOverlay(project, 1, pointerOffset + 4);

        uint tableRamAddress;
        using (FileStream stream = File.OpenRead(overlay1Path))
        using (BinaryReader reader = new(stream))
        {
            stream.Position = pointerOffset;
            tableRamAddress = reader.ReadUInt32();
        }

        if (tableRamAddress >> 24 != 0x02) throw new InvalidDataException("The overworld table pointer in overlay 1 is not a valid address.");

        // Same routing as legacy: the HG-Engine field extension overlay, when present, holds the table.
        string extensionPath = OverlayService.GetOverlayPath(project, HgssFieldExtensionOverlay);
        if (File.Exists(extensionPath))
        {
            return new TableLocation(extensionPath, tableRamAddress - OverlayService.GetRamAddress(project, HgssFieldExtensionOverlay), Hgss: true);
        }

        if (tableRamAddress >= SynthOverlayRamAddress)
        {
            throw new NotSupportedException("The overworld table has been moved into the expanded ARM9 file, which this editor cannot read yet.");
        }

        return new TableLocation(overlay1Path, tableRamAddress - overlay1Address, Hgss: true);
    }

    private static string RequireOverlay(RomProject project, int overlayNumber, long neededLength)
    {
        string path = OverlayService.GetOverlayPath(project, overlayNumber);
        if (!File.Exists(path)) throw new FileNotFoundException($"Overlay {overlayNumber} was not found.", path);
        if (neededLength < 0 || neededLength > new FileInfo(path).Length)
        {
            throw new InvalidDataException($"Overlay {overlayNumber} is smaller than expected; it may still be compressed.");
        }

        return path;
    }

    private readonly record struct BtxLayout(int ImageOffset, int PaletteOffset, int PaletteBytes, int PaletteCount, int ColorCount, int Width, int Height);

    private static bool TryReadLayout(byte[] btx, out BtxLayout layout)
    {
        layout = default;
        try
        {
            if (btx.Length < 0x14 || BitConverter.ToUInt32(btx, 0) != Btx0Magic) return false;
            int tex0 = (int)BitConverter.ToUInt32(btx, 16);
            if (BitConverter.ToUInt32(btx, tex0) != Tex0Magic) return false;

            int textureInfo = tex0 + BitConverter.ToUInt16(btx, tex0 + 14);
            int imageOffset = tex0 + (int)BitConverter.ToUInt32(btx, tex0 + 20);
            int paletteBytes = (int)(BitConverter.ToUInt32(btx, tex0 + 48) << 3);
            int paletteInfo = tex0 + (int)BitConverter.ToUInt32(btx, tex0 + 52);
            int paletteOffset = tex0 + (int)BitConverter.ToUInt32(btx, tex0 + 56);

            int textureCount = btx[textureInfo + 1];
            int parameters = BitConverter.ToUInt16(btx, textureInfo + 12 + textureCount * 4 + 6);
            int width = 8 << ((parameters >> 4) & 7);
            if (((parameters >> 10) & 7) != Palette16Format) return false;

            int paletteCount = btx[paletteInfo + 1];
            if (paletteCount == 0 || paletteOffset <= imageOffset || paletteOffset > btx.Length) return false;

            // Same special case as legacy's BTX0.Read for sheets whose palette block is under-declared.
            int colorCount = paletteBytes < 64 && paletteCount >= 2 ? (btx.Length - paletteOffset) / 2 : paletteBytes / paletteCount / 2;
            int height = (paletteOffset - imageOffset) * 2 / width;
            if (colorCount <= 0 || height <= 0) return false;

            layout = new BtxLayout(imageOffset, paletteOffset, paletteBytes, paletteCount, colorCount, width, height);
            return true;
        }
        catch (Exception ex) when (ex is ArgumentException or IndexOutOfRangeException)
        {
            return false;
        }
    }
}
