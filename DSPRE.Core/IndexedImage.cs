using System.Buffers.Binary;
using System.Text;

namespace DSPRE.Core;

/// <summary>Turning an RGBA image into 16-color DS graphics: a palette of up to 15 colors (color 0 is
/// transparent) and 4bpp 8x8 tiles. Shared by the item icon and trainer graphic imports.</summary>
internal static class IndexedImage
{
    public static int PaletteColorsStart(byte[] palette, string path)
    {
        if (palette.Length < 0x28 || Encoding.ASCII.GetString(palette, 0, 4) != "RLCN") throw new InvalidDataException($"Palette file {Path.GetFileName(path)} is not a palette.");
        int colorsStart = 0x18 + (int)BinaryPrimitives.ReadUInt32LittleEndian(palette.AsSpan(0x24));
        if (colorsStart + 32 > palette.Length) throw new InvalidDataException($"Palette file {Path.GetFileName(path)} has fewer than 16 colors.");
        return colorsStart;
    }

    /// <summary>Colors 1-15 of the first palette.</summary>
    public static ushort[] ReadColors(byte[] palette, int colorsStart) =>
        Enumerable.Range(1, 15).Select(c => (ushort)(BinaryPrimitives.ReadUInt16LittleEndian(palette.AsSpan(colorsStart + c * 2)) & 0x7FFF)).ToArray();

    public static void WriteColors(byte[] palette, int colorsStart, ushort[] colors)
    {
        for (int c = 1; c <= 15; c++)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(palette.AsSpan(colorsStart + c * 2), c <= colors.Length ? colors[c - 1] : (ushort)0);
        }
    }

    /// <summary>Raster-order indices of a width x height region as 4bpp 8x8 tiles, row by row (the
    /// inverse of NdsTileImage's de-tiling).</summary>
    public static byte[] EncodeTiled(ReadOnlySpan<byte> indices, int width, int height)
    {
        byte[] output = new byte[width * height / 2];
        int position = 0;
        for (int tileY = 0; tileY < height / 8; tileY++)
        for (int tileX = 0; tileX < width / 8; tileX++)
        for (int y = 0; y < 8; y++)
        for (int x = 0; x < 8; x += 2)
        {
            int pixel = (tileY * 8 + y) * width + tileX * 8 + x;
            output[position++] = (byte)((indices[pixel] & 0x0F) | ((indices[pixel + 1] & 0x0F) << 4));
        }

        return output;
    }

    /// <summary>The palette (without the transparent color 0) and each pixel's index into the full
    /// palette, plus how many distinct colors the image had. When every color is already in
    /// <paramref name="current"/>, that palette is kept as it is, so re-importing an exported icon
    /// changes nothing.</summary>
    public static (ushort[] Colors, byte[] Indices, int SourceColors, bool KeptPalette) Quantize(byte[] rgba, ushort[] current)
    {
        int pixels = rgba.Length / 4;
        var colorOf = new int[pixels];
        var counts = new Dictionary<ushort, int>();
        for (int i = 0; i < pixels; i++)
        {
            colorOf[i] = -1;
            if (rgba[i * 4 + 3] < 128) continue;
            // The inverse of NdsPalette's plain *8 scaling, so an exported icon re-imports exactly.
            ushort color = (ushort)((rgba[i * 4] >> 3) | ((rgba[i * 4 + 1] >> 3) << 5) | ((rgba[i * 4 + 2] >> 3) << 10));
            colorOf[i] = color;
            counts[color] = counts.GetValueOrDefault(color) + 1;
        }

        bool keep = counts.Keys.All(current.Contains);
        ushort[] colors = keep ? current
            : counts.Count <= 15 ? counts.OrderByDescending(pair => pair.Value).Select(pair => pair.Key).ToArray()
            : MedianCut(counts, 15);

        byte[] indices = new byte[pixels];
        var nearest = new Dictionary<int, byte>();
        for (int i = 0; i < pixels; i++)
        {
            if (colorOf[i] < 0) continue;
            if (!nearest.TryGetValue(colorOf[i], out byte index))
            {
                int best = 0, bestDistance = int.MaxValue;
                for (int c = 0; c < colors.Length; c++)
                {
                    int distance = Distance((ushort)colorOf[i], colors[c]);
                    if (distance < bestDistance) (best, bestDistance) = (c, distance);
                }

                nearest[colorOf[i]] = index = (byte)(best + 1);
            }

            indices[i] = index;
        }

        return (colors, indices, counts.Count, keep);
    }

    private static int Channel(ushort color, int channel) => (color >> (channel * 5)) & 31;

    private static int Distance(ushort a, ushort b)
    {
        int distance = 0;
        for (int channel = 0; channel < 3; channel++)
        {
            int d = Channel(a, channel) - Channel(b, channel);
            distance += d * d;
        }

        return distance;
    }

    /// <summary>Splits the colors (weighted by pixel count) into <paramref name="target"/> boxes, always
    /// cutting the box with the widest channel at its weighted median, and averages each box.</summary>
    private static ushort[] MedianCut(Dictionary<ushort, int> counts, int target)
    {
        var boxes = new List<List<KeyValuePair<ushort, int>>> { counts.ToList() };
        while (boxes.Count < target)
        {
            (int box, int channel, int range) = (-1, 0, 0);
            for (int b = 0; b < boxes.Count; b++)
            {
                if (boxes[b].Count < 2) continue;
                for (int c = 0; c < 3; c++)
                {
                    int r = boxes[b].Max(p => Channel(p.Key, c)) - boxes[b].Min(p => Channel(p.Key, c));
                    if (r > range) (box, channel, range) = (b, c, r);
                }
            }

            if (box < 0) break;
            List<KeyValuePair<ushort, int>> sorted = boxes[box].OrderBy(p => Channel(p.Key, channel)).ToList();
            int half = sorted.Sum(p => p.Value) / 2, running = 0, split = 1;
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                running += sorted[i].Value;
                split = i + 1;
                if (running >= half) break;
            }

            boxes[box] = sorted[..split];
            boxes.Add(sorted[split..]);
        }

        return boxes.Select(box =>
        {
            int total = box.Sum(p => p.Value);
            int Average(int c) => (box.Sum(p => Channel(p.Key, c) * p.Value) + total / 2) / total;
            return (ushort)(Average(0) | (Average(1) << 5) | (Average(2) << 10));
        }).Distinct().ToArray();
    }
}
