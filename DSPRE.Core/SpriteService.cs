namespace DSPRE.Core;

/// <summary>
/// Composes decoded NCGR tiles + NCLR palette(s) + one NCER bank into an RGBA pixel buffer, and the
/// two DSPRE-specific call sites that need this: party Pokemon species icons and trainer class sprites.
/// Ported from the legacy renderer's Actions.Get_Image(Bank,...) composition loop (Ekona/Images/Actions.cs)
/// and its two callers, DSUtils.GetPokePic and TrainerEditor.LoadTrainerClassPic/UpdateTrainerClassPic.
/// </summary>
public static class SpriteService
{
    /// <summary>
    /// Composites one NCER bank (= one animation frame/cell) onto a canvasWidth x canvasHeight RGBA
    /// buffer (top-left origin), returning canvasWidth*canvasHeight*4 bytes. OAM position offsets are
    /// relative to the canvas CENTER, matching the NDS hardware/legacy renderer convention. A pixel
    /// whose decoded palette index is 0 is left fully transparent (alpha 0) rather than color-keyed
    /// after the fact, which is simpler than the legacy Bitmap.MakeTransparent approach and behaviorally
    /// equivalent unless a palette contains a duplicate of its own index-0 color.
    /// </summary>
    public static byte[] Compose(NdsTileImage tiles, RgbColor[][] palettes, NdsCellBank cells, int bankIndex, int canvasWidth, int canvasHeight)
    {
        byte[] rgba = new byte[Math.Max(canvasWidth, 0) * Math.Max(canvasHeight, 0) * 4];
        if (bankIndex < 0 || bankIndex >= cells.Banks.Length || canvasWidth <= 0 || canvasHeight <= 0)
        {
            return rgba;
        }

        NdsCellBank.Bank bank = cells.Banks[bankIndex];
        foreach (NdsCellBank.Oam oam in bank.Oams)
        {
            if (oam.Width <= 0 || oam.Height <= 0)
            {
                continue;
            }

            // 0x20 = 32 bytes = one 8x8 4bpp NDS tile, the hardware tile-addressing granularity used
            // regardless of this image's actual bit depth.
            uint shiftedTileOffset = oam.TileOffset << (int)cells.BlockSize;
            int byteOffset = (int)(shiftedTileOffset * 0x20 + bank.DataOffset);
            byte[] pixelIndices = tiles.DecodeRegion(byteOffset, oam.Width, oam.Height);

            int paletteIndex = oam.PaletteIndex;
            if (paletteIndex < 0 || paletteIndex >= palettes.Length)
            {
                paletteIndex = 0;
            }
            RgbColor[] palette = palettes.Length > 0 ? palettes[paletteIndex] : [];

            for (int y = 0; y < oam.Height; y++)
            {
                int canvasY = canvasHeight / 2 + oam.YOffset + y;
                if (canvasY < 0 || canvasY >= canvasHeight)
                {
                    continue;
                }

                for (int x = 0; x < oam.Width; x++)
                {
                    int canvasX = canvasWidth / 2 + oam.XOffset + x;
                    if (canvasX < 0 || canvasX >= canvasWidth)
                    {
                        continue;
                    }

                    // Flip is applied by sampling from the mirrored source position, equivalent to
                    // decoding normally and then flipping the finished sub-image.
                    int sx = oam.FlipX ? oam.Width - 1 - x : x;
                    int sy = oam.FlipY ? oam.Height - 1 - y : y;
                    byte paletteEntryIndex = pixelIndices[sx + sy * oam.Width];
                    if (paletteEntryIndex == 0 || paletteEntryIndex >= palette.Length)
                    {
                        continue;
                    }

                    RgbColor color = palette[paletteEntryIndex];
                    int rgbaOffset = (canvasX + canvasY * canvasWidth) * 4;
                    rgba[rgbaOffset] = color.R;
                    rgba[rgbaOffset + 1] = color.G;
                    rgba[rgbaOffset + 2] = color.B;
                    rgba[rgbaOffset + 3] = 255;
                }
            }
        }

        return rgba;
    }

    /// <summary>Renders a party Pokemon's species icon (mon_icon NARC, always bank/frame 0). Returns
    /// null if the ROM's icon data or ARM9 palette table couldn't be resolved (e.g. an ARM9-expansion
    /// ROM hack patch DSPRE itself applies is not accounted for here - only the standard/vanilla ARM9
    /// layout is supported for now).</summary>
    public static byte[]? GetSpeciesIconRgba(RomProject project, RomGameMetadata metadata, int species, int width, int height)
    {
        try
        {
            (string paletteDigits, bool fiveDigits) = ResolveMonIconDigits(project);
            string palettePath = Path.Combine(project.MonIconsDirectory, paletteDigits);
            if (!File.Exists(palettePath))
            {
                return null;
            }

            NdsPalette palette = NdsPalette.Parse(File.ReadAllBytes(palettePath));

            // Species icons all share materials from ONE NCLR file; the correct sub-palette for this
            // specific species is selected by overwriting sub-palette slot 0 with it (matching
            // DSUtils.GetPokePic exactly), since the icon's own OAMs always reference palette slot 0.
            RgbColor[][] palettes = (RgbColor[][])palette.Palettes.Clone();
            int paletteId = ResolveSpeciesPaletteIndex(project, metadata, species);
            if (paletteId > 0 && paletteId < palettes.Length)
            {
                palettes[0] = palettes[paletteId];
            }

            int spriteFileId = species + 7;
            string tilesPath = Path.Combine(project.MonIconsDirectory, spriteFileId.ToString(fiveDigits ? "D5" : "D4"));
            if (!File.Exists(tilesPath))
            {
                return null;
            }
            NdsTileImage tiles = NdsTileImage.Parse(File.ReadAllBytes(tilesPath));

            string cellsPath = Path.Combine(project.MonIconsDirectory, 2.ToString(fiveDigits ? "D5" : "D4"));
            if (!File.Exists(cellsPath))
            {
                return null;
            }
            NdsCellBank cells = NdsCellBank.Parse(File.ReadAllBytes(cellsPath));

            return Compose(tiles, palettes, cells, 0, width, height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static (string Digits, bool FiveDigits) ResolveMonIconDigits(RomProject project)
    {
        string fourDigit = Path.Combine(project.MonIconsDirectory, "0000");
        return File.Exists(fourDigit) ? ("0000", false) : ("00000", true);
    }

    /// <summary>A per-species byte, read from a table whose RAM address is itself read from a fixed,
    /// game-version/language-specific ARM9 offset (one level of pointer indirection). Only the
    /// standard (non-overlay, non-ARM9-expanded) ROM layout is handled; DSPRE's own "expand ARM9
    /// memory" ROM Toolbox patch and the HGSS overlay-129 variant used by some ROM hacks are not
    /// accounted for here.</summary>
    private static int ResolveSpeciesPaletteIndex(RomProject project, RomGameMetadata metadata, int species)
    {
        long fileOffset = IconPaletteTableOffset(project, metadata, species);
        return fileOffset >= 0 ? File.ReadAllBytes(project.Arm9Path)[fileOffset] : 0;
    }

    /// <summary>Where in arm9.bin the icon's palette number is, or -1 when the table cannot be found.</summary>
    internal static long IconPaletteTableOffset(RomProject project, RomGameMetadata metadata, int iconId)
    {
        long tableOffsetInArm9 = ResolveMonIconPalTableArm9Offset(metadata);
        if (tableOffsetInArm9 < 0 || !File.Exists(project.Arm9Path)) return -1;

        long arm9Length = new FileInfo(project.Arm9Path).Length;
        if (tableOffsetInArm9 + 4 > arm9Length) return -1;
        using var stream = File.OpenRead(project.Arm9Path);
        using var reader = new BinaryReader(stream);
        stream.Position = tableOffsetInArm9;
        uint tableRamAddress = reader.ReadUInt32();
        const uint arm9RamBase = 0x02000000;
        long fileOffset = tableRamAddress - arm9RamBase + iconId;
        return fileOffset >= 0 && fileOffset < arm9Length ? fileOffset : -1;
    }

    internal static (string Digits, bool FiveDigits) MonIconDigits(RomProject project) => ResolveMonIconDigits(project);

    private static long ResolveMonIconPalTableArm9Offset(RomGameMetadata metadata)
    {
        if (metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver
            && metadata.Language == RomLanguage.Spanish
            && metadata.GameCode.StartsWith("IPK", StringComparison.OrdinalIgnoreCase))
        {
            return 0x74400; // HeartGold Spanish is the one language/version combo with its own offset.
        }

        return (metadata.HeaderFormat, metadata.Language) switch
        {
            (MapHeaderFormat.DiamondPearl, RomLanguage.English) => 0x6B838,
            (MapHeaderFormat.DiamondPearl, RomLanguage.Italian) => 0x6B874,
            (MapHeaderFormat.DiamondPearl, RomLanguage.German) => 0x6B894,
            (MapHeaderFormat.DiamondPearl, RomLanguage.French) => 0x6B894,
            (MapHeaderFormat.DiamondPearl, RomLanguage.Spanish) => 0x6B894,
            (MapHeaderFormat.DiamondPearl, RomLanguage.Japanese) => 0x6FDEC,
            (MapHeaderFormat.Platinum, RomLanguage.English) => 0x79F80,
            (MapHeaderFormat.Platinum, RomLanguage.Italian) => 0x7A020,
            (MapHeaderFormat.Platinum, RomLanguage.German) => 0x7A020,
            (MapHeaderFormat.Platinum, RomLanguage.French) => 0x7A020,
            (MapHeaderFormat.Platinum, RomLanguage.Spanish) => 0x7A020,
            (MapHeaderFormat.Platinum, RomLanguage.Japanese) => 0x79858,
            (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.English) => 0x74408,
            (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.Italian) => 0x74408,
            (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.French) => 0x74408,
            (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.German) => 0x74408,
            (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.Spanish) => 0x74408,
            (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.Japanese) => 0x73EA0,
            _ => -1
        };
    }

    /// <summary>Loads a trainer class's sprite data and reports its animation frame count (0 for
    /// Diamond/Pearl, which - matching the legacy renderer exactly - has no NCER cell bank for trainer
    /// graphics at all, so no trainer class sprite can be rendered on those games).</summary>
    public static bool TryLoadTrainerClassSprite(RomProject project, RomGameMetadata metadata, int trainerClassId, out NdsTileImage? tiles, out RgbColor[][]? palettes, out NdsCellBank? cells, out int frameCount, TrainerGraphicSide side = TrainerGraphicSide.Front)
    {
        tiles = null;
        palettes = null;
        cells = null;
        frameCount = 0;

        try
        {
            int paletteFileId = trainerClassId * 5 + 1;
            string palettePath = Path.Combine(project.TrainerGraphicsDirectoryFor(side), paletteFileId.ToString("D4"));
            int tilesFileId = trainerClassId * 5;
            string tilesPath = Path.Combine(project.TrainerGraphicsDirectoryFor(side), tilesFileId.ToString("D4"));
            if (!File.Exists(palettePath) || !File.Exists(tilesPath))
            {
                return false;
            }

            palettes = NdsPalette.Parse(File.ReadAllBytes(palettePath)).Palettes;
            tiles = NdsTileImage.Parse(File.ReadAllBytes(tilesPath));

            if (metadata.HeaderFormat == MapHeaderFormat.DiamondPearl)
            {
                return true; // Static-only on DP; frameCount stays 0, caller should not try to animate.
            }

            int cellsFileId = trainerClassId * 5 + 2;
            string cellsPath = Path.Combine(project.TrainerGraphicsDirectoryFor(side), cellsFileId.ToString("D4"));
            if (!File.Exists(cellsPath))
            {
                return false;
            }

            cells = NdsCellBank.Parse(File.ReadAllBytes(cellsPath));
            frameCount = cells.Banks.Length;
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static byte[]? GetTrainerClassSpriteRgba(NdsTileImage tiles, RgbColor[][] palettes, NdsCellBank cells, int frame, int width, int height)
    {
        try
        {
            return Compose(tiles, palettes, cells, frame, width, height);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>Item icons (item_icon.narc) are standard NCGR/NCLR/NCER, unlike the bespoke scrambled
    /// format battle sprites use - every item icon/palette shares one common cell bank file, always ID 1
    /// (0001), matching the legacy ItemEditor.SetUpIcon exactly.</summary>
    public static byte[]? GetItemIconRgba(RomProject project, int iconId, int paletteId, int width = 32, int height = 32)
    {
        try
        {
            string tilesPath = Path.Combine(project.ItemIconsDirectory, iconId.ToString("D4"));
            string palettePath = Path.Combine(project.ItemIconsDirectory, paletteId.ToString("D4"));
            string cellsPath = Path.Combine(project.ItemIconsDirectory, 1.ToString("D4"));
            if (!File.Exists(tilesPath) || !File.Exists(palettePath) || !File.Exists(cellsPath))
            {
                return null;
            }

            NdsTileImage tiles = NdsTileImage.Parse(File.ReadAllBytes(tilesPath));
            RgbColor[][] palettes = NdsPalette.Parse(File.ReadAllBytes(palettePath)).Palettes;
            NdsCellBank cells = NdsCellBank.Parse(File.ReadAllBytes(cellsPath));

            return Compose(tiles, palettes, cells, 0, width, height);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
