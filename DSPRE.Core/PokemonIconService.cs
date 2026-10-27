namespace DSPRE.Core;

/// <summary>The small party/menu icons (mon_icon NARC): each icon file holds two 32x32 frames (the
/// party-screen bounce), 4bpp in 8x8 tiles, frame 2 starting 512 bytes in. Icons have no colors of their
/// own: all of them share a few 16-color palettes, and a byte per icon in an ARM9 table picks one. An
/// imported image is therefore matched to whichever shared palette fits it best; the palettes themselves
/// are left alone, since changing one would recolor every icon using it.</summary>
public static class PokemonIconService
{
    public const int FrameSize = 32;
    private const int FrameBytes = FrameSize * FrameSize / 2;
    private const int TileDataOffset = 0x30;

    public sealed record ImportResult(int Palette, int ExactColors, int OpaquePixels);

    private static string FilePath(RomProject project, int fileId)
    {
        (string _, bool fiveDigits) = SpriteService.MonIconDigits(project);
        return Path.Combine(project.MonIconsDirectory, fileId.ToString(fiveDigits ? "D5" : "D4"));
    }

    /// <summary>Icon file number of a species/alt-form icon id (the first seven files are the shared
    /// palette, cell and animation files).</summary>
    public static string IconPath(RomProject project, int iconId) => FilePath(project, iconId + 7);

    public static RgbColor[][] LoadPalettes(RomProject project) =>
        NdsPalette.Parse(File.ReadAllBytes(FilePath(project, 0))).Palettes;

    /// <summary>Most screens load only the first three shared palettes (pokeplatinum: the party
    /// screen, battle and mail all load 3 from the icon palette file), so an icon using a later one
    /// shows wrong colors there even though the file holds more.</summary>
    public const int PalettesLoadedEverywhere = 3;

    public static int IconCount(RomProject project) =>
        Directory.Exists(project.MonIconsDirectory) ? Math.Max(0, Directory.GetFiles(project.MonIconsDirectory).Length - 7) : 0;

    /// <summary>The icon's palette number, or -1 when the ROM's table cannot be found.</summary>
    public static int GetPalette(RomProject project, RomGameMetadata metadata, int iconId)
    {
        long offset = SpriteService.IconPaletteTableOffset(project, metadata, iconId);
        return offset < 0 ? -1 : File.ReadAllBytes(project.Arm9Path)[offset];
    }

    /// <summary>Every icon's palette number (-1 where the table cannot be found), reading ARM9 once.</summary>
    public static int[] GetPalettes(RomProject project, RomGameMetadata metadata)
    {
        int count = IconCount(project);
        var result = new int[count];
        long start = SpriteService.IconPaletteTableOffset(project, metadata, 0);
        byte[] arm9 = start >= 0 ? File.ReadAllBytes(project.Arm9Path) : [];
        for (int icon = 0; icon < count; icon++) result[icon] = start >= 0 && start + icon < arm9.Length ? arm9[start + icon] : -1;
        return result;
    }

    /// <summary>How many shared palettes icons use: the highest one any icon picks, plus one.</summary>
    public static int UsedPaletteCount(RomProject project, RomGameMetadata metadata) =>
        Math.Min(GetPalettes(project, metadata).DefaultIfEmpty(0).Max() + 1, LoadPalettes(project).Length);

    /// <summary>Points the icon at another shared palette.</summary>
    public static void SetPalette(RomProject project, RomGameMetadata metadata, int iconId, int palette) =>
        EditJournal.Track(project, EditJournalDocuments.IconPaletteChoice, EditJournalDocuments.Keys(("icon", iconId)), () =>
        {
            long offset = SpriteService.IconPaletteTableOffset(project, metadata, iconId);
            if (offset < 0) throw new InvalidOperationException("The icon palette table was not found in this ROM's ARM9.");
            if (palette < 0 || palette >= LoadPalettes(project).Length) throw new ArgumentOutOfRangeException(nameof(palette));
            using var arm9 = new FileStream(project.Arm9Path, FileMode.Open, FileAccess.Write);
            arm9.Position = offset;
            arm9.WriteByte((byte)palette);
        });

    /// <summary>Rewrites one shared palette's 16 colors; every icon using it changes.</summary>
    public static void SaveSharedPalette(RomProject project, int palette, RgbColor[] colors) =>
        EditJournal.Track(project, EditJournalDocuments.IconSharedPalette, EditJournalDocuments.Keys(("palette", palette)), () =>
        {
            string path = FilePath(project, 0);
            byte[] file = File.ReadAllBytes(path);
            int colorsStart = 0x18 + (int)BitConverter.ToUInt32(file, 0x24);
            int offset = colorsStart + palette * 32;
            if (palette < 0 || colors.Length != 16 || offset + 32 > file.Length) throw new InvalidDataException($"The icon palette file has no palette {palette}.");
            for (int c = 0; c < 16; c++)
            {
                // NdsPalette reads channels as value * 8; this is its inverse.
                ushort bgr = (ushort)((colors[c].R >> 3) | ((colors[c].G >> 3) << 5) | ((colors[c].B >> 3) << 10));
                BitConverter.TryWriteBytes(file.AsSpan(offset + c * 2), bgr);
            }

            File.WriteAllBytes(path, file);
        });

    /// <summary>Both frames as one 32x64 RGBA image, frame 1 on top; transparent pixels stay clear.</summary>
    public static byte[] Render(RomProject project, RomGameMetadata metadata, int iconId) =>
        Render(project, iconId, LoadPalettes(project)[Math.Max(0, GetPalette(project, metadata, iconId))]);

    /// <summary>Both frames drawn with the given palette, for previewing palette edits.</summary>
    public static byte[] Render(RomProject project, int iconId, RgbColor[] palette)
    {
        byte[] file = File.ReadAllBytes(IconPath(project, iconId));
        byte[] indices = DecodeFrames(file);
        byte[] rgba = new byte[indices.Length * 4];
        for (int i = 0; i < indices.Length; i++)
        {
            if (indices[i] == 0 || indices[i] >= palette.Length) continue;
            RgbColor color = palette[indices[i]];
            rgba[i * 4] = color.R;
            rgba[i * 4 + 1] = color.G;
            rgba[i * 4 + 2] = color.B;
            rgba[i * 4 + 3] = 255;
        }

        return rgba;
    }

    /// <summary>Replaces an icon with a 32x32 image (used for both frames) or a 32x64 one (frame 1 above
    /// frame 2). Pixels under half opacity become transparent; the rest take the nearest color of the
    /// shared palette that matches the image best, which the icon is then set to use.</summary>
    public static ImportResult Import(RomProject project, RomGameMetadata metadata, int iconId, string imagePath)
    {
        string source = Path.GetFullPath(imagePath);
        byte[] rgba = BattleSpriteService.DecodeImageFileToRgba(source, out int width, out int height);
        if (width != FrameSize || height is not (FrameSize or FrameSize * 2))
        {
            throw new InvalidDataException($"The image is {width}x{height}; an icon is 32x32 (one frame) or 32x64 (two frames stacked).");
        }

        if (height == FrameSize) rgba = [.. rgba, .. rgba];
        long paletteOffset = SpriteService.IconPaletteTableOffset(project, metadata, iconId);
        if (paletteOffset < 0) throw new InvalidOperationException("The icon palette table was not found in this ROM's ARM9, so the icon's colors cannot be set.");

        RgbColor[][] palettes = LoadPalettes(project);
        int candidates = UsedPaletteCount(project, metadata);
        (int best, long bestError) = (0, long.MaxValue);
        for (int p = 0; p < candidates; p++)
        {
            long error = 0;
            for (int i = 0; i < rgba.Length / 4; i++)
            {
                if (rgba[i * 4 + 3] < 128) continue;
                error += Distance(palettes[p], Nearest(palettes[p], rgba, i), rgba, i);
            }

            if (error < bestError) (best, bestError) = (p, error);
        }

        RgbColor[] palette = palettes[best];
        byte[] indices = new byte[rgba.Length / 4];
        int opaque = 0, exact = 0;
        for (int i = 0; i < indices.Length; i++)
        {
            if (rgba[i * 4 + 3] < 128) continue;
            indices[i] = (byte)Nearest(palette, rgba, i);
            opaque++;
            if (Distance(palette, indices[i], rgba, i) == 0) exact++;
        }

        string path = IconPath(project, iconId);
        byte[] file = File.ReadAllBytes(path);
        if (file.Length < TileDataOffset + 2 * FrameBytes) throw new InvalidDataException($"{Path.GetFileName(path)} is not a two-frame icon file.");
        for (int frame = 0; frame < 2; frame++)
        {
            byte[] tiled = EncodeTiled(indices.AsSpan(frame * FrameSize * FrameSize, FrameSize * FrameSize));
            tiled.CopyTo(file, TileDataOffset + frame * FrameBytes);
        }

        File.WriteAllBytes(path, file);
        using (var arm9 = new FileStream(project.Arm9Path, FileMode.Open, FileAccess.Write))
        {
            arm9.Position = paletteOffset;
            arm9.WriteByte((byte)best);
        }

        EditJournal.Record(project, "ImportPokemonIcon", ("icon", iconId), ("source", source));
        return new ImportResult(best, exact, opaque);
    }

    private static int Nearest(RgbColor[] palette, byte[] rgba, int pixel)
    {
        (int best, int bestDistance) = (1, int.MaxValue);
        for (int c = 1; c < palette.Length; c++)
        {
            int distance = Distance(palette, c, rgba, pixel);
            if (distance < bestDistance) (best, bestDistance) = (c, distance);
        }

        return best;
    }

    private static int Distance(RgbColor[] palette, int index, byte[] rgba, int pixel)
    {
        int dr = palette[index].R - rgba[pixel * 4], dg = palette[index].G - rgba[pixel * 4 + 1], db = palette[index].B - rgba[pixel * 4 + 2];
        return dr * dr + dg * dg + db * db;
    }

    /// <summary>Palette indices of both frames, raster order, frame 1 then frame 2.</summary>
    private static byte[] DecodeFrames(byte[] file)
    {
        var tiles = NdsTileImage.Parse(file);
        return [.. tiles.DecodeRegion(0, FrameSize, FrameSize), .. tiles.DecodeRegion(FrameBytes, FrameSize, FrameSize)];
    }

    /// <summary>One 32x32 frame of raster-order indices as 4bpp 8x8 tiles, row by row (the inverse of
    /// NdsTileImage's de-tiling).</summary>
    private static byte[] EncodeTiled(ReadOnlySpan<byte> indices)
    {
        byte[] output = new byte[FrameBytes];
        int position = 0;
        for (int tileY = 0; tileY < FrameSize / 8; tileY++)
        for (int tileX = 0; tileX < FrameSize / 8; tileX++)
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x += 2)
        {
            int pixel = (tileY * 8 + y) * FrameSize + tileX * 8 + x;
            output[position++] = (byte)((indices[pixel] & 0x0F) | ((indices[pixel + 1] & 0x0F) << 4));
        }

        return output;
    }
}
