namespace DSPRE.Core;

/// <summary>One of the game's fonts as the ROM stores it: a 16-byte header (glyph data offset, width table
/// offset, glyph count, maximum width and height, glyph size in 8x8 tiles), then each glyph as 2x2 tiles of
/// 2bpp pixels, then one width byte per glyph. Glyph n draws character code n + 1. The shadow is part of the
/// glyph, so drawing a line is only a matter of placing glyphs one width apart.</summary>
public sealed class GameFont
{
    public const int GlyphSize = 16;
    private const int BytesPerGlyph = GlyphSize * GlyphSize / 4;

    /// <summary>The value of a glyph pixel.</summary>
    public const byte Transparent = 0, Text = 1, Shadow = 2, Background = 3;

    private readonly byte[] data;
    private readonly int glyphOffset;
    private readonly int widthOffset;
    private readonly int glyphCount;

    public GameFont(byte[] data)
    {
        if (data.Length < 16) throw new InvalidDataException("The font is too short to have a header.");
        this.data = data;
        glyphOffset = BitConverter.ToInt32(data, 0);
        widthOffset = BitConverter.ToInt32(data, 4);
        glyphCount = BitConverter.ToInt32(data, 8);
        if (data[14] != 2 || data[15] != 2 || glyphOffset + glyphCount * BytesPerGlyph > data.Length || widthOffset + glyphCount > data.Length)
        {
            throw new InvalidDataException("The font is not laid out as 16x16 glyphs with a width table.");
        }
    }

    public bool Has(ushort code) => code >= 1 && code <= glyphCount;

    public int Width(ushort code) => Has(code) ? data[widthOffset + code - 1] : 0;

    public byte Pixel(ushort code, int x, int y)
    {
        if (!Has(code) || x < 0 || y < 0 || x >= GlyphSize || y >= GlyphSize) return Transparent;
        int tile = (y / 8) * 2 + x / 8;
        int row = glyphOffset + (code - 1) * BytesPerGlyph + tile * 16 + (y % 8) * 2;
        // Each tile row is a little-endian 16-bit word with the leftmost pixel in its top two bits.
        int word = data[row] | data[row + 1] << 8;
        return (byte)((word >> (14 - 2 * (x % 8))) & 3);
    }

    /// <summary>Draws a line of character codes as RGBA, one glyph after another at their widths, with the
    /// text and shadow pixels in the given colors and everything else transparent.</summary>
    public (byte[] Rgba, int Width) RenderLine(IReadOnlyList<ushort> codes, (byte R, byte G, byte B) text, (byte R, byte G, byte B) shadow)
    {
        int width = Math.Max(1, codes.Sum(Width));
        byte[] rgba = new byte[width * GlyphSize * 4];
        int left = 0;
        foreach (ushort code in codes)
        {
            int glyphWidth = Width(code);
            for (int y = 0; y < GlyphSize; y++)
            {
                for (int x = 0; x < glyphWidth && x < GlyphSize; x++)
                {
                    byte value = Pixel(code, x, y);
                    if (value is not (Text or Shadow)) continue;
                    (byte r, byte g, byte b) = value == Text ? text : shadow;
                    int at = (y * width + left + x) * 4;
                    rgba[at] = r;
                    rgba[at + 1] = g;
                    rgba[at + 2] = b;
                    rgba[at + 3] = 255;
                }
            }
            left += glyphWidth;
        }
        return (rgba, width);
    }
}

public static class GameFontService
{
    /// <summary>The NARC of the game's fonts; member 0 is the font menus and descriptions are drawn in.</summary>
    public static string GetArchivePath(RomProject project, RomGameMetadata metadata) => metadata.HeaderFormat switch
    {
        MapHeaderFormat.HeartGoldSoulSilver => Path.Combine(project.FilesPath, "a", "0", "1", "6"),
        MapHeaderFormat.Platinum => Path.Combine(project.FilesPath, "graphic", "pl_font.narc"),
        _ => Path.Combine(project.FilesPath, "graphic", "font.narc"),
    };

    public static GameFont? Load(RomProject project, RomGameMetadata metadata, int font = 0)
    {
        string path = GetArchivePath(project, metadata);
        return File.Exists(path) ? new GameFont(NarcFile.ReadMember(path, font)) : null;
    }
}

/// <summary>Turns message text as the text archives store it into character codes, matching the character
/// map's characters and aliases longest first. Text commands (<c>{STRVAR_1, ...}</c>) and anything the map
/// does not know are left out.</summary>
public sealed class GameTextEncoder
{
    private readonly Dictionary<string, ushort> lookup = new(StringComparer.Ordinal);
    private readonly int longest;

    public GameTextEncoder(CharMap map)
    {
        foreach (ushort code in map.GetAllCodes())
        {
            if (map.GetEntry(code) is not { } entry) continue;
            if (entry.Character.Length > 0) lookup.TryAdd(entry.Character, code);
            foreach (string alias in entry.Aliases ?? []) lookup.TryAdd(alias, code);
        }
        longest = lookup.Count == 0 ? 0 : lookup.Keys.Max(key => key.Length);
    }

    public List<ushort> Encode(string text)
    {
        var codes = new List<ushort>();
        for (int i = 0; i < text.Length;)
        {
            if (text[i] == '{' && text.IndexOf('}', i) is int end and > 0)
            {
                i = end + 1;
                continue;
            }
            int length = Math.Min(longest, text.Length - i);
            while (length > 0 && !lookup.ContainsKey(text.Substring(i, length))) length--;
            if (length == 0)
            {
                i++;
                continue;
            }
            codes.Add(lookup[text.Substring(i, length)]);
            i += length;
        }
        return codes;
    }
}
