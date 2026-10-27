namespace DSPRE.Core;

/// <summary>Decodes the battle sprite format used by pokegra.narc/otherpoke.narc (poketool/pokegra) - a
/// bespoke, non-standard pseudo-NCGR/NCLR pair distinct from the NCGR/NCLR/NCER pipeline in
/// NdsSpriteFormats.cs: a fixed 48-byte header followed by 3200 raw pixel ushorts that are obfuscated
/// with a linear-congruential XOR cipher, and a fixed 40-byte header followed by 16 plain BGR555 colors.
/// Ported from the legacy PokemonSpriteEditor.MakeImage/SetPal (view/decode path only - PNG
/// import/replace is not ported here, matching the same "images as a follow-up" scoping used for the
/// Trainer Editor).</summary>
public static class BattleSpriteService
{
    public const int SpriteWidth = 160;
    public const int SpriteHeight = 80;
    private const int PixelHeaderSize = 48;
    private const int PixelWordCount = 3200; // 160*80/4 pixels per byte-aligned 4bpp ushort (4 pixels/ushort)
    private const int PaletteHeaderSize = 40;
    private const int PaletteColorCount = 16;

    /// <summary>Decodes a 6448-byte sprite file to one palette-index byte per pixel (160x80, row-major).
    /// Returns null if the data doesn't match the expected fixed layout.</summary>
    public static byte[]? DecodeSpriteIndices(byte[] data)
    {
        if (data.Length < PixelHeaderSize + PixelWordCount * 2) return null;

        ushort[] words = new ushort[PixelWordCount];
        for (int i = 0; i < PixelWordCount; i++)
        {
            int offset = PixelHeaderSize + i * 2;
            words[i] = BitConverter.ToUInt16(data, offset);
        }

        // DP uses a reverse-order cipher (seeded from the last word, walking backward); Platinum/HGSS
        // seed from the first word and walk forward. Since this NARC's own path already differs per
        // game family (see RomExtractionService), callers pick DecodeSpriteIndices vs the DP overload
        // via the isDiamondPearl flag on the composing method below.
        DescrambleForward(words);

        byte[] indices = new byte[SpriteWidth * SpriteHeight];
        for (int i = 0; i < PixelWordCount; i++)
        {
            indices[i * 4] = (byte)(words[i] & 0xF);
            indices[i * 4 + 1] = (byte)((words[i] >> 4) & 0xF);
            indices[i * 4 + 2] = (byte)((words[i] >> 8) & 0xF);
            indices[i * 4 + 3] = (byte)((words[i] >> 12) & 0xF);
        }

        return indices;
    }

    public static byte[]? DecodeSpriteIndicesDiamondPearl(byte[] data)
    {
        if (data.Length < PixelHeaderSize + PixelWordCount * 2) return null;

        ushort[] words = new ushort[PixelWordCount];
        for (int i = 0; i < PixelWordCount; i++)
        {
            int offset = PixelHeaderSize + i * 2;
            words[i] = BitConverter.ToUInt16(data, offset);
        }

        DescrambleBackward(words);

        byte[] indices = new byte[SpriteWidth * SpriteHeight];
        for (int i = 0; i < PixelWordCount; i++)
        {
            indices[i * 4] = (byte)(words[i] & 0xF);
            indices[i * 4 + 1] = (byte)((words[i] >> 4) & 0xF);
            indices[i * 4 + 2] = (byte)((words[i] >> 8) & 0xF);
            indices[i * 4 + 3] = (byte)((words[i] >> 12) & 0xF);
        }

        return indices;
    }

    private static void DescrambleForward(ushort[] words)
    {
        uint seed = words[0];
        for (int i = 0; i < words.Length; i++)
        {
            unchecked
            {
                words[i] = (ushort)(words[i] ^ (ushort)(seed & 0xFFFF));
                seed = seed * 1103515245 + 24691;
            }
        }
    }

    private static void DescrambleBackward(ushort[] words)
    {
        uint seed = words[^1];
        for (int i = words.Length - 1; i >= 0; i--)
        {
            unchecked
            {
                words[i] = (ushort)(words[i] ^ (ushort)(seed & 0xFFFF));
                seed = seed * 1103515245 + 24691;
            }
        }
    }

    /// <summary>Decodes a 72-byte palette file to 16 plain BGR555 colors (no scrambling, unlike the
    /// pixel data).</summary>
    public static RgbColor[]? DecodePalette(byte[] data)
    {
        if (data.Length < PaletteHeaderSize + PaletteColorCount * 2) return null;

        var colors = new RgbColor[PaletteColorCount];
        for (int i = 0; i < PaletteColorCount; i++)
        {
            ushort bgr = BitConverter.ToUInt16(data, PaletteHeaderSize + i * 2);
            colors[i] = new RgbColor(
                (byte)((bgr & 0x1F) << 3),
                (byte)(((bgr >> 5) & 0x1F) << 3),
                (byte)(((bgr >> 10) & 0x1F) << 3));
        }

        return colors;
    }

    /// <summary>Composites decoded palette indices + a palette into an RGBA buffer, treating palette
    /// index 0 as transparent (consistent with every other sprite decoder in this codebase).</summary>
    public static byte[] ComposeRgba(byte[] indices, RgbColor[] palette)
    {
        byte[] rgba = new byte[SpriteWidth * SpriteHeight * 4];
        for (int i = 0; i < indices.Length; i++)
        {
            byte index = indices[i];
            if (index == 0 || index >= palette.Length) continue;

            RgbColor color = palette[index];
            int offset = i * 4;
            rgba[offset] = color.R;
            rgba[offset + 1] = color.G;
            rgba[offset + 2] = color.B;
            rgba[offset + 3] = 255;
        }

        return rgba;
    }

    // Fixed literal headers the legacy editor writes ahead of the pixel/palette payload - a truncated,
    // hand-authored NCGR/NCLR (single CHAR/PLTT chunk, no CPOS) rather than anything built through a
    // general-purpose NCGR/NCLR writer. Ported byte-for-byte from PokemonSpriteEditor.SaveBin/SavePal.
    private static readonly byte[] SpriteFileHeader =
    [
        82, 71, 67, 78, 255, 254, 0, 1, 48, 25, 0, 0, 16, 0, 1, 0,
        82, 65, 72, 67, 32, 25, 0, 0, 10, 0, 20, 0, 3, 0, 0, 0,
        0, 0, 0, 0, 1, 0, 0, 0, 0, 25, 0, 0, 24, 0, 0, 0
    ];

    private static readonly byte[] PaletteFileHeader =
    [
        82, 76, 67, 78, 255, 254, 0, 1, 72, 0, 0, 0, 16, 0, 1, 0,
        84, 84, 76, 80, 56, 0, 0, 0, 4, 0, 10, 0, 0, 0, 0, 0,
        32, 0, 0, 0, 16, 0, 0, 0
    ];

    /// <summary>Inverse of DecodeSpriteIndices/DecodeSpriteIndicesDiamondPearl. Platinum/HGSS reseed the
    /// forward cipher at 0 (not from the plain pixel value, unlike decode); DP reseeds from a checksum
    /// (31315 + the sum of all 3200 plain words) walking backward, a genuinely different construction
    /// from DP's own decode (which seeds from the last raw word) - both are reproduced exactly as the
    /// legacy writer does, not "corrected" to be symmetric with their decode counterpart.
    /// <para>Because of that asymmetry, the seed chain a subsequent decode will derive only matches the
    /// one used here if the word decode always destroys anyway (word 0 for the forward cipher, the last
    /// word for DP's backward one - each XORs itself with its own seed source and so always decodes to 0
    /// regardless of what's written) is actually written as 0. This method forces those 4 pixels to index
    /// 0 for that reason: not to hide data loss (that pixel data is unrecoverable on decode either way,
    /// by construction of the cipher) but to keep every *other* pixel's seed chain consistent between
    /// this encoder and any decoder that reads the file back.</para></summary>
    public static byte[] EncodeSpriteIndices(byte[] indices, bool isDiamondPearl)
    {
        byte[] adjusted = (byte[])indices.Clone();
        if (!isDiamondPearl)
        {
            adjusted[0] = adjusted[1] = adjusted[2] = adjusted[3] = 0;
        }
        else
        {
            adjusted[^1] = adjusted[^2] = adjusted[^3] = adjusted[^4] = 0;
        }

        ushort[] words = new ushort[PixelWordCount];
        for (int i = 0; i < PixelWordCount; i++)
        {
            byte a = adjusted[i * 4];
            byte b = adjusted[i * 4 + 1];
            byte c = adjusted[i * 4 + 2];
            byte d = adjusted[i * 4 + 3];
            words[i] = (ushort)((a & 0xF) | ((b & 0xF) << 4) | ((c & 0xF) << 8) | ((d & 0xF) << 12));
        }

        if (!isDiamondPearl)
        {
            uint seed = 0;
            for (int i = 0; i < words.Length; i++)
            {
                unchecked
                {
                    words[i] = (ushort)(words[i] ^ (ushort)(seed & 0xFFFF));
                    seed = seed * 1103515245 + 24691;
                }
            }
        }
        else
        {
            uint seed = 31315;
            for (int i = words.Length - 1; i >= 0; i--) seed += words[i];
            for (int i = words.Length - 1; i >= 0; i--)
            {
                unchecked
                {
                    words[i] = (ushort)(words[i] ^ (ushort)(seed & 0xFFFF));
                    seed = seed * 1103515245 + 24691;
                }
            }
        }

        byte[] data = new byte[SpriteFileHeader.Length + PixelWordCount * 2];
        SpriteFileHeader.CopyTo(data, 0);
        for (int i = 0; i < words.Length; i++)
        {
            BitConverter.GetBytes(words[i]).CopyTo(data, SpriteFileHeader.Length + i * 2);
        }

        return data;
    }

    public static byte[] EncodePalette(RgbColor[] palette)
    {
        byte[] data = new byte[PaletteFileHeader.Length + PaletteColorCount * 2];
        PaletteFileHeader.CopyTo(data, 0);
        for (int i = 0; i < PaletteColorCount; i++)
        {
            RgbColor color = i < palette.Length ? palette[i] : new RgbColor(0, 0, 0);
            ushort bgr = (ushort)(((color.R >> 3) & 0x1F) | (((color.G >> 3) & 0x1F) << 5) | (((color.B >> 3) & 0x1F) << 10));
            BitConverter.GetBytes(bgr).CopyTo(data, PaletteFileHeader.Length + i * 2);
        }

        return data;
    }

    /// <summary>Reduces an RGBA image to at most 16 palette entries: transparent pixels map to index 0
    /// (reserved, matching how every sprite decoder in this codebase treats index 0), and opaque colors
    /// are assigned new palette slots up to the limit, then nearest-matched (by squared RGB distance) to
    /// an existing slot once the palette is full. This is a fresh, portable implementation - the legacy
    /// editor instead round-tripped through System.Drawing's native 8bpp/4bpp bitmap conversion, which
    /// has no cross-platform equivalent available in DSPRE.Core.</summary>
    public static (byte[] Indices, RgbColor[] Palette) QuantizeTo16Colors(byte[] rgba, int width, int height)
    {
        var palette = new List<RgbColor> { new(0, 0, 0) };
        byte[] indices = new byte[width * height];

        for (int p = 0; p < indices.Length; p++)
        {
            int offset = p * 4;
            byte a = rgba[offset + 3];
            if (a < 128)
            {
                indices[p] = 0;
                continue;
            }

            var color = new RgbColor(rgba[offset], rgba[offset + 1], rgba[offset + 2]);
            int existing = palette.IndexOf(color);
            if (existing >= 0)
            {
                indices[p] = (byte)existing;
            }
            else if (palette.Count < PaletteColorCount)
            {
                palette.Add(color);
                indices[p] = (byte)(palette.Count - 1);
            }
            else
            {
                indices[p] = (byte)NearestPaletteIndex(palette, color);
            }
        }

        return (indices, palette.ToArray());
    }

    private static int NearestPaletteIndex(IReadOnlyList<RgbColor> palette, RgbColor color)
    {
        int best = 1;
        int bestDistance = int.MaxValue;
        for (int i = 1; i < palette.Count; i++)
        {
            RgbColor candidate = palette[i];
            int dr = candidate.R - color.R;
            int dg = candidate.G - color.G;
            int db = candidate.B - color.B;
            int distance = dr * dr + dg * dg + db * db;
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = i;
            }
        }

        return best;
    }

    /// <summary>Quantizes against a fixed, pre-existing palette (nearest RGB match) instead of building a
    /// new one - used when replacing one of a species' 4 sprite slots, since all 4 share a single normal
    /// palette and a single shiny palette in the ROM format; rebuilding the palette from just the newly
    /// imported image would shift the other 3 untouched slots' colors.</summary>
    public static byte[] QuantizeToExistingPalette(byte[] rgba, int width, int height, RgbColor[] palette)
    {
        byte[] indices = new byte[width * height];
        for (int p = 0; p < indices.Length; p++)
        {
            int offset = p * 4;
            byte a = rgba[offset + 3];
            if (a < 128)
            {
                indices[p] = 0;
                continue;
            }

            var color = new RgbColor(rgba[offset], rgba[offset + 1], rgba[offset + 2]);
            indices[p] = (byte)NearestPaletteIndex(palette, color);
        }

        return indices;
    }

    /// <summary>Normalizes an arbitrary imported image to the 160x80 canvas every battle sprite file
    /// uses. A native 160x80 image is used as-is; an 80x80 image is placed in both halves side by side,
    /// matching the duplicated-halves layout the legacy decoder itself produces (see DecodeSpriteIndices
    /// doc). Any other size is rejected rather than guessing at padding/cropping - the legacy tool's own
    /// resize rules for odd sizes are ambiguous even reading its source, so this deliberately asks for a
    /// well-formed image instead of risking silently wrong pixel offsets.</summary>
    public static bool TryNormalizeToCanvas(byte[] rgba, int width, int height, out byte[] normalizedRgba)
    {
        if (width == SpriteWidth && height == SpriteHeight)
        {
            normalizedRgba = rgba;
            return true;
        }

        if (width == SpriteHeight && height == SpriteHeight) // 80x80
        {
            normalizedRgba = new byte[SpriteWidth * SpriteHeight * 4];
            for (int y = 0; y < height; y++)
            {
                int srcRowOffset = y * width * 4;
                int dstRowOffset = y * SpriteWidth * 4;
                Array.Copy(rgba, srcRowOffset, normalizedRgba, dstRowOffset, width * 4);
                Array.Copy(rgba, srcRowOffset, normalizedRgba, dstRowOffset + width * 4, width * 4);
            }

            return true;
        }

        normalizedRgba = [];
        return false;
    }

    /// <summary>Decodes a PNG/BMP/GIF file to straight (non-premultiplied) RGBA. Lives in Core, rather than
    /// using the UI toolkit's decoder, so a journaled sprite import replays to identical pixels.</summary>
    public static byte[] DecodeImageFileToRgba(string path, out int width, out int height)
    {
        using FileStream stream = File.OpenRead(path);
        StbImageSharp.ImageResult image = StbImageSharp.ImageResult.FromStream(stream, StbImageSharp.ColorComponents.RedGreenBlueAlpha);
        width = image.Width;
        height = image.Height;
        return image.Data;
    }

    /// <summary>Converts an image file to sprite indices: matched against <paramref name="palette"/> when the
    /// species already has one (so the other slots sharing it keep their colors), otherwise quantized to a
    /// new 16-color palette, which is returned.</summary>
    public static (byte[] Indices, RgbColor[] Palette) ImportSpriteImage(string imagePath, RgbColor[]? palette)
    {
        byte[] rgba = DecodeImageFileToRgba(imagePath, out int width, out int height);
        if (!TryNormalizeToCanvas(rgba, width, height, out byte[] normalized))
        {
            throw new InvalidDataException($"Image must be 160x80 or 80x80 (got {width}x{height}).");
        }

        return palette is not null
            ? (QuantizeToExistingPalette(normalized, SpriteWidth, SpriteHeight, palette), palette)
            : QuantizeTo16Colors(normalized, SpriteWidth, SpriteHeight);
    }

    public static RgbColor[]? LoadBattlePalette(RomProject project, int speciesFileId, bool shiny)
    {
        string path = Path.Combine(project.PokemonBattleSpritesDirectory, (speciesFileId * 6 + (shiny ? 5 : 4)).ToString("D4"));
        return File.Exists(path) ? DecodePalette(File.ReadAllBytes(path)) : null;
    }

    /// <summary>Writes one sprite slot. Pixel data is too large to journal usefully, so a change is
    /// journaled as the image file it was imported from (<paramref name="sourceImage"/>) and the palette
    /// it was matched against; a change with no known source image is flagged as not replayable.</summary>
    public static void SaveBattleSprite(RomProject project, int speciesFileId, BattleSpriteSlot slot, byte[] indices, bool isDiamondPearl,
        string? sourceImage = null, bool shinyPalette = false)
    {
        Directory.CreateDirectory(project.PokemonBattleSpritesDirectory);
        int baseOffset = speciesFileId * 6;
        string path = Path.Combine(project.PokemonBattleSpritesDirectory, (baseOffset + (int)slot).ToString("D4"));
        byte[] encoded = EncodeSpriteIndices(indices, isDiamondPearl);
        byte[]? previous = File.Exists(path) ? File.ReadAllBytes(path) : null;
        File.WriteAllBytes(path, encoded);
        if (previous is not null && previous.AsSpan().SequenceEqual(encoded)) return;

        if (sourceImage is null)
        {
            EditJournal.Comment(project, $"WARNING: battle sprite {speciesFileId} {slot} changed without a source image; this edit is not replayable.");
            return;
        }

        EditJournal.Record(project, "ImportBattleSprite",
            ("species", speciesFileId), ("slot", slot.ToString()), ("shiny", shinyPalette), ("source", Path.GetFullPath(sourceImage)));
    }

    public static void SaveBattlePalette(RomProject project, int speciesFileId, bool shiny, RgbColor[] palette) =>
        EditJournal.Track(project, EditJournalDocuments.BattlePalette, EditJournalDocuments.Keys(("species", speciesFileId), ("shiny", shiny)), () => SaveBattlePaletteUntracked(project, speciesFileId, shiny, palette));

    private static void SaveBattlePaletteUntracked(RomProject project, int speciesFileId, bool shiny, RgbColor[] palette)
    {
        Directory.CreateDirectory(project.PokemonBattleSpritesDirectory);
        int baseOffset = speciesFileId * 6;
        string path = Path.Combine(project.PokemonBattleSpritesDirectory, (baseOffset + (shiny ? 5 : 4)).ToString("D4"));
        File.WriteAllBytes(path, EncodePalette(palette));
    }

    public enum BattleSpriteSlot
    {
        FemaleBack = 0,
        MaleBack = 1,
        FemaleFront = 2,
        MaleFront = 3,
    }

    /// <summary>Loads and composes one of the 4 sprite slots (back/front x male/female) for a species,
    /// tinted with either the normal or shiny palette (slots 4/5 in the same NARC entry group).</summary>
    public static byte[]? GetBattleSpriteRgba(RomProject project, RomGameMetadata metadata, int speciesFileId, BattleSpriteSlot slot, bool shiny)
    {
        int baseOffset = speciesFileId * 6;
        string spritePath = Path.Combine(project.PokemonBattleSpritesDirectory, (baseOffset + (int)slot).ToString("D4"));
        string palettePath = Path.Combine(project.PokemonBattleSpritesDirectory, (baseOffset + (shiny ? 5 : 4)).ToString("D4"));
        if (!File.Exists(spritePath) || !File.Exists(palettePath)) return null;

        byte[] spriteData = File.ReadAllBytes(spritePath);
        byte[]? indices = metadata.HeaderFormat == MapHeaderFormat.DiamondPearl
            ? DecodeSpriteIndicesDiamondPearl(spriteData)
            : DecodeSpriteIndices(spriteData);
        RgbColor[]? palette = DecodePalette(File.ReadAllBytes(palettePath));
        if (indices is null || palette is null) return null;

        return ComposeRgba(indices, palette);
    }
}
