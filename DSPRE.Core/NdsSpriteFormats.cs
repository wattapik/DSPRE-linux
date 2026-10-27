namespace DSPRE.Core;

/// <summary>
/// Pure byte-array decoders for the three Nintendo DS graphics formats DSPRE's sprite previews are
/// built from: NCLR (palette), NCGR (tile/character pixel data), NCER (cell bank / OAM placement).
/// This is a from-scratch port of the decode math in the legacy WinForms app's Ekona/Images library
/// (NCLR.cs, NCGR.cs, NCER.cs, Actions.cs), which is Windows-only (System.Drawing) and not referenced
/// by DSPRE.Avalonia. Every byte offset and bit-shift here was cross-checked against that source
/// rather than re-derived from format documentation, since a small mistake here reads as "wrong/garbled
/// sprite" with no other symptom to debug from.
/// </summary>
public readonly record struct RgbColor(byte R, byte G, byte B);

public sealed class NdsPalette
{
    public RgbColor[][] Palettes { get; }

    private NdsPalette(RgbColor[][] palettes) => Palettes = palettes;

    /// <summary>Parses an NCLR file. One NCLR can hold several sequential sub-palettes (16 or 256
    /// colors each) back-to-back - e.g. the species icon palette file has one sub-palette per icon
    /// "family" and the correct one is selected per-species via a separate ARM9 table lookup.</summary>
    public static NdsPalette Parse(byte[] data)
    {
        if (data.Length < 0x28)
        {
            return new NdsPalette([new RgbColor[16]]);
        }

        uint sectionLength = BitConverter.ToUInt32(data, 0x14);
        ushort depth = BitConverter.ToUInt16(data, 0x18);
        uint palLength = BitConverter.ToUInt32(data, 0x20);
        if (palLength == 0 || palLength > sectionLength)
        {
            palLength = sectionLength > 0x18 ? sectionLength - 0x18 : 0;
        }

        uint colorsStartOffset = BitConverter.ToUInt32(data, 0x24);
        int numColors = depth == 3 ? 16 : 256; // 3 = colors16 (4bpp), 4 = colors256 (8bpp)
        if (palLength / 2 < (uint)numColors)
        {
            numColors = (int)(palLength / 2);
        }
        if (numColors <= 0)
        {
            numColors = 16;
        }

        int paletteCount = (int)(palLength / (uint)(numColors * 2));
        if (paletteCount <= 0)
        {
            paletteCount = 1;
        }

        int colorsStart = 0x18 + (int)colorsStartOffset;
        var palettes = new RgbColor[paletteCount][];
        for (int p = 0; p < paletteCount; p++)
        {
            palettes[p] = new RgbColor[numColors];
            for (int c = 0; c < numColors; c++)
            {
                int offset = colorsStart + (p * numColors + c) * 2;
                if (offset + 1 >= data.Length)
                {
                    continue;
                }

                ushort bgr = BitConverter.ToUInt16(data, offset);
                // Despite the name, the packed bit layout (LSB first) is R(5) G(5) B(5). Scaling is a
                // plain *8 (not bit-replicated 5->8 expansion) to match the legacy renderer exactly.
                byte r = (byte)((bgr & 0x001F) * 8);
                byte g = (byte)(((bgr & 0x03E0) >> 5) * 8);
                byte b = (byte)(((bgr & 0x7C00) >> 10) * 8);
                palettes[p][c] = new RgbColor(r, g, b);
            }
        }

        return new NdsPalette(palettes);
    }
}

public sealed class NdsTileImage
{
    private readonly byte[] rawTiles;

    public int Bpp { get; }
    private bool NeedsDetile { get; }

    private NdsTileImage(byte[] rawTiles, int bpp, bool needsDetile)
    {
        this.rawTiles = rawTiles;
        Bpp = bpp;
        NeedsDetile = needsDetile;
    }

    public static NdsTileImage Parse(byte[] data)
    {
        if (data.Length < 0x30)
        {
            return new NdsTileImage([], 4, true);
        }

        uint depth = BitConverter.ToUInt32(data, 0x1C);
        uint tiledFlag = BitConverter.ToUInt32(data, 0x24);
        uint sizeTileData = BitConverter.ToUInt32(data, 0x28);

        int available = Math.Max(0, data.Length - 0x30);
        int length = (int)Math.Min(sizeTileData, (uint)available);
        byte[] raw = new byte[length];
        Array.Copy(data, 0x30, raw, 0, length);

        int bpp = depth == 4 ? 8 : 4; // 4 = colors256 (8bpp), anything else treated as colors16 (4bpp)
        // tiledFlag low byte 0 means the data is stored as consecutive 8x8 tile blocks and must be
        // de-tiled into raster order before pixel (x,y) lookups are meaningful; nonzero means it is
        // already a flat raster bitmap.
        bool needsDetile = (tiledFlag & 0xFF) == 0;
        return new NdsTileImage(raw, bpp, needsDetile);
    }

    /// <summary>
    /// Decodes one width x height region starting at a BYTE offset into the shared tile blob, returning
    /// one palette-index byte per pixel in raster order. This mirrors the legacy renderer's per-OAM
    /// decode exactly: each OAM addresses its own tiles by byte offset into one shared blob (not by
    /// (x,y) position), which is how several sprites/icons packed into one NCGR sheet are told apart.
    /// </summary>
    public byte[] DecodeRegion(int byteOffset, int width, int height)
    {
        const int tileSize = 8;
        int needed = width * height * Bpp / 8;
        byte[] slice = new byte[needed];
        if (byteOffset >= 0 && byteOffset < rawTiles.Length)
        {
            int copyLength = Math.Min(needed, rawTiles.Length - byteOffset);
            if (copyLength > 0)
            {
                Array.Copy(rawTiles, byteOffset, slice, 0, copyLength);
            }
        }

        byte[] raster = NeedsDetile ? LinealToHorizontal(slice, width, height, Bpp, tileSize) : slice;

        var indices = new byte[width * height];
        int pos = 0;
        for (int h = 0; h < height; h++)
        {
            for (int w = 0; w < width; w++)
            {
                indices[w + h * width] = Bpp == 4 ? Read4Bpp(raster, ref pos) : Read8Bpp(raster, ref pos);
            }
        }

        return indices;
    }

    private static byte Read4Bpp(byte[] data, ref int pos)
    {
        int byteIndex = pos / 2;
        byte value = 0;
        if (byteIndex < data.Length)
        {
            value = (pos % 2 == 0) ? (byte)(data[byteIndex] & 0x0F) : (byte)((data[byteIndex] >> 4) & 0x0F);
        }
        pos++;
        return value;
    }

    private static byte Read8Bpp(byte[] data, ref int pos)
    {
        byte value = pos < data.Length ? data[pos] : (byte)0;
        pos++;
        return value;
    }

    /// <summary>Reorders tile-block-order bytes (tiles enumerated row-major, 8x8 blocks) into full
    /// raster/scanline order. Exact port of Ekona's Actions.LinealToHorizontal.</summary>
    private static byte[] LinealToHorizontal(byte[] lineal, int width, int height, int bpp, int tileSize)
    {
        byte[] horizontal = new byte[lineal.Length];
        int tileWidth = tileSize * bpp / 8;
        int tilesX = width / tileSize;
        int tilesY = height / tileSize;

        int pos = 0;
        for (int ht = 0; ht < tilesY; ht++)
        {
            for (int wt = 0; wt < tilesX; wt++)
            {
                for (int h = 0; h < tileSize; h++)
                {
                    for (int w = 0; w < tileWidth; w++)
                    {
                        int destIndex = (w + h * tileWidth * tilesX) + wt * tileWidth + ht * tilesX * tileSize * tileWidth;
                        if (destIndex >= lineal.Length || pos >= lineal.Length)
                        {
                            continue;
                        }

                        horizontal[destIndex] = lineal[pos++];
                    }
                }
            }
        }

        return horizontal;
    }
}

public sealed class NdsCellBank
{
    public sealed record Oam(int XOffset, int YOffset, int Width, int Height, uint TileOffset, int PaletteIndex, bool FlipX, bool FlipY, int Priority, int CellIndex);

    public sealed record Bank(Oam[] Oams, uint DataOffset);

    public Bank[] Banks { get; }
    public uint BlockSize { get; }

    private NdsCellBank(Bank[] banks, uint blockSize)
    {
        Banks = banks;
        BlockSize = blockSize;
    }

    public static NdsCellBank Parse(byte[] data)
    {
        if (data.Length < 0x30)
        {
            return new NdsCellBank([], 0);
        }

        ushort headerSize = BitConverter.ToUInt16(data, 0x0C);
        ushort nBanks = BitConverter.ToUInt16(data, 0x18);
        ushort tBank = BitConverter.ToUInt16(data, 0x1A);
        uint bankDataOffset = BitConverter.ToUInt32(data, 0x1C);
        uint blockSize = BitConverter.ToUInt32(data, 0x20) & 0xFF;
        uint partitionDataOffset = BitConverter.ToUInt32(data, 0x24);

        int bankRecordSize = tBank == 0 ? 8 : 16;
        int bankRecordsStart = headerSize + (int)bankDataOffset + 8;
        int cellDataStart = bankRecordsStart + nBanks * bankRecordSize;

        // Only present for VRAM-partitioned sprites (uncommon; species icons/trainer sprites don't use
        // this), so DataOffset is 0 for the ordinary case below.
        var partitionOffsets = new uint[nBanks];
        if (partitionDataOffset != 0 && headerSize + (int)partitionDataOffset + 16 <= data.Length)
        {
            int fieldsStart = headerSize + (int)partitionDataOffset + 8;
            uint firstPartitionDataOffset = BitConverter.ToUInt32(data, fieldsStart + 4);
            int pos = fieldsStart + (int)firstPartitionDataOffset;
            for (int i = 0; i < nBanks && pos + 8 <= data.Length; i++)
            {
                partitionOffsets[i] = BitConverter.ToUInt32(data, pos);
                pos += 8;
            }
        }

        var banks = new Bank[nBanks];
        int recordPos = bankRecordsStart;
        for (int i = 0; i < nBanks; i++)
        {
            if (recordPos + 8 > data.Length)
            {
                banks[i] = new Bank([], 0);
                continue;
            }

            ushort nCells = BitConverter.ToUInt16(data, recordPos);
            uint cellOffset = BitConverter.ToUInt32(data, recordPos + 4);

            int oamsStart = cellDataStart + (int)cellOffset;
            var oams = new List<Oam>(nCells);
            for (int c = 0; c < nCells; c++)
            {
                int oamPos = oamsStart + c * 6;
                if (oamPos + 6 > data.Length)
                {
                    break;
                }

                ushort obj0 = BitConverter.ToUInt16(data, oamPos);
                ushort obj1 = BitConverter.ToUInt16(data, oamPos + 2);
                ushort obj2 = BitConverter.ToUInt16(data, oamPos + 4);
                oams.Add(DecodeOam(obj0, obj1, obj2, c));
            }

            // Highest priority value draws first (bottom layer); ties broken by highest original cell
            // index drawing first, so cell 0 (typically the "main" part) ends up on top. Matches
            // NCER.Comparision_Cell in the legacy renderer.
            Oam[] sorted = oams.ToArray();
            Array.Sort(sorted, (a, b) => b.Priority != a.Priority ? b.Priority.CompareTo(a.Priority) : b.CellIndex.CompareTo(a.CellIndex));

            banks[i] = new Bank(sorted, partitionDataOffset != 0 ? partitionOffsets[i] : 0);
            recordPos += bankRecordSize;
        }

        return new NdsCellBank(banks, blockSize);
    }

    private static Oam DecodeOam(ushort obj0, ushort obj1, ushort obj2, int cellIndex)
    {
        sbyte yOffset = (sbyte)(obj0 & 0xFF);
        int rsFlag = (obj0 >> 8) & 1;
        int shape = (obj0 >> 14) & 3;

        int rawXOffset = obj1 & 0x01FF;
        int xOffset = rawXOffset >= 0x100 ? rawXOffset - 0x200 : rawXOffset;
        int size = (obj1 >> 14) & 3;
        bool flipX = false;
        bool flipY = false;
        if (rsFlag == 0)
        {
            flipX = ((obj1 >> 12) & 1) != 0;
            flipY = ((obj1 >> 13) & 1) != 0;
        }

        uint tileOffset = (uint)(obj2 & 0x03FF);
        int priority = (obj2 >> 10) & 3;
        int paletteIndex = (obj2 >> 12) & 0xF;

        (int width, int height) = GetOamSize(shape, size);
        return new Oam(xOffset, yOffset, width, height, tileOffset, paletteIndex, flipX, flipY, priority, cellIndex);
    }

    private static (int Width, int Height) GetOamSize(int shape, int size) => (shape, size) switch
    {
        (0, 0) => (8, 8),
        (0, 1) => (16, 16),
        (0, 2) => (32, 32),
        (0, 3) => (64, 64),
        (1, 0) => (16, 8),
        (1, 1) => (32, 8),
        (1, 2) => (32, 16),
        (1, 3) => (64, 32),
        (2, 0) => (8, 16),
        (2, 1) => (8, 32),
        (2, 2) => (16, 32),
        (2, 3) => (32, 64),
        _ => (0, 0)
    };
}
