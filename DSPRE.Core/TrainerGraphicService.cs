using System.Buffers.Binary;
using System.Text;

namespace DSPRE.Core;

/// <summary>Which of the two trainer graphic archives: the front sprites (trfgra) or the back sprites (trbgra) the player and rival show from behind.</summary>
public enum TrainerGraphicSide { Front, Back }

/// <summary>Trainer class front sprites (trfgra) and back sprites (trbgra, Platinum; same five-file layout). Each class has five files (pokeplatinum
/// res/trainers/classes/meson.build, pokemon.c SpriteSystem_SetTrainerClassGraphicsIndex): tiles, palette,
/// cells, animation, and a scrambled 160x80 "scan" copy that the Hall of Fame and similar screens read.
/// The tiles are a run of 80x80 frames, 3200 bytes each, split into OAM pieces; a cell places one frame,
/// and animated classes have cells that place frames at shifted positions or reuse a frame. So the
/// editable image is the frames themselves, stacked vertically, each laid out the way the first cell
/// that uses it draws it - what the decompilation's front.png holds. Each half of the scan is blank or a
/// copy of one frame.</summary>
public static class TrainerGraphicService
{
    public const int FrameSize = 80;
    private const int FrameBytes = FrameSize * FrameSize / 2;
    private const int TileDataOffset = 0x30;
    private const int FilesPerClass = 5;

    public sealed record ImportResult(int Frames, int Colors, int SourceColors, bool ScanUpdated);

    /// <summary>One OAM's tiles: where they are in the tile data and where they sit in their frame.</summary>
    private sealed record Piece(int Frame, int X, int Y, int Width, int Height, int ByteOffset, bool FlipX, bool FlipY);

    private sealed record Layout(byte[] Tiles, int FrameCount, List<Piece> Pieces);

    /// <summary>Diamond/Pearl trainer graphics have no cell files, so their layout is unknown.</summary>
    public static bool IsSupported(RomGameMetadata metadata, TrainerGraphicSide side = TrainerGraphicSide.Front) =>
        side == TrainerGraphicSide.Back ? metadata.HeaderFormat == MapHeaderFormat.Platinum : metadata.HeaderFormat != MapHeaderFormat.DiamondPearl;

    public static int ClassCount(RomProject project, TrainerGraphicSide side = TrainerGraphicSide.Front) =>
        Directory.Exists(project.TrainerGraphicsDirectoryFor(side)) ? Directory.GetFiles(project.TrainerGraphicsDirectoryFor(side)).Length / FilesPerClass : 0;

    private static string FilePath(RomProject project, int trainerClass, int file, TrainerGraphicSide side) =>
        Path.Combine(project.TrainerGraphicsDirectoryFor(side), (trainerClass * FilesPerClass + file).ToString("D4"));

    public static int FrameCount(RomProject project, int trainerClass, TrainerGraphicSide side = TrainerGraphicSide.Front) => ReadLayout(project, trainerClass, side).FrameCount;

    /// <summary>All frames as one 80 x (80 * frames) RGBA image, frame 0 on top.</summary>
    public static byte[] RenderSheet(RomProject project, int trainerClass, TrainerGraphicSide side = TrainerGraphicSide.Front)
    {
        Layout layout = ReadLayout(project, trainerClass, side);
        byte[] indices = DecodeFrames(layout);
        RgbColor[] palette = NdsPalette.Parse(File.ReadAllBytes(FilePath(project, trainerClass, 1, side))).Palettes[0];
        byte[] rgba = new byte[indices.Length * 4];
        for (int i = 0; i < indices.Length; i++)
        {
            if (indices[i] == 0 || indices[i] >= palette.Length) continue;
            rgba[i * 4] = palette[indices[i]].R;
            rgba[i * 4 + 1] = palette[indices[i]].G;
            rgba[i * 4 + 2] = palette[indices[i]].B;
            rgba[i * 4 + 3] = 255;
        }

        return rgba;
    }

    /// <summary>Replaces every frame from an 80 x (80 * frames) image (an 80x80 one for a single-frame
    /// class). Pixels under half opacity become transparent. The colors become the class's palette,
    /// reduced to 15 when there are more, and kept as they are when they already fit it. The scan copy
    /// is rewritten from the new frames.</summary>
    public static ImportResult Import(RomProject project, RomGameMetadata metadata, int trainerClass, string imagePath, TrainerGraphicSide side = TrainerGraphicSide.Front)
    {
        if (!IsSupported(metadata, side)) throw new InvalidOperationException(side == TrainerGraphicSide.Back ? "Trainer back graphics are only supported for Platinum." : "Diamond/Pearl trainer graphics are not supported.");
        string source = Path.GetFullPath(imagePath);
        Layout layout = ReadLayout(project, trainerClass, side);
        byte[] rgba = BattleSpriteService.DecodeImageFileToRgba(source, out int width, out int height);
        if (width != FrameSize || height != FrameSize * layout.FrameCount)
        {
            throw new InvalidDataException($"The image is {width}x{height}; this trainer has {layout.FrameCount} frame{(layout.FrameCount == 1 ? "" : "s")}, so it needs {FrameSize}x{FrameSize * layout.FrameCount} (frames stacked top to bottom).");
        }

        string palettePath = FilePath(project, trainerClass, 1, side);
        byte[] palette = File.ReadAllBytes(palettePath);
        int colorsStart = IndexedImage.PaletteColorsStart(palette, palettePath);
        (ushort[] colors, byte[] indices, int sourceColors, bool keptPalette) = IndexedImage.Quantize(rgba, IndexedImage.ReadColors(palette, colorsStart));

        byte[] oldFrames = DecodeFrames(layout);
        byte[] tiles = layout.Tiles;
        foreach (Piece piece in layout.Pieces)
        {
            byte[] region = new byte[piece.Width * piece.Height];
            for (int y = 0; y < piece.Height; y++)
            for (int x = 0; x < piece.Width; x++)
            {
                int sx = piece.FlipX ? piece.Width - 1 - x : x;
                int sy = piece.FlipY ? piece.Height - 1 - y : y;
                region[sx + sy * piece.Width] = indices[(piece.Frame * FrameSize + piece.Y + y) * FrameSize + piece.X + x];
            }

            IndexedImage.EncodeTiled(region, piece.Width, piece.Height).CopyTo(tiles, TileDataOffset + piece.ByteOffset);
        }

        byte[]? scan = BuildScan(project, metadata, trainerClass, side, oldFrames, indices, layout.FrameCount);
        File.WriteAllBytes(FilePath(project, trainerClass, 0, side), tiles);
        if (!keptPalette)
        {
            IndexedImage.WriteColors(palette, colorsStart, colors);
            File.WriteAllBytes(palettePath, palette);
        }

        if (scan is not null) File.WriteAllBytes(FilePath(project, trainerClass, 4, side), scan);

        if (side == TrainerGraphicSide.Back) EditJournal.Record(project, "ImportTrainerGraphic", ("trainerClass", trainerClass), ("source", source), ("side", "Back"));
        else EditJournal.Record(project, "ImportTrainerGraphic", ("trainerClass", trainerClass), ("source", source));
        return new ImportResult(layout.FrameCount, keptPalette ? sourceColors : colors.Length, sourceColors, scan is not null);
    }

    /// <summary>The scan file rewritten from the new frames, or null when it is missing or not in the
    /// expected format. Each half keeps showing whichever frame it showed before (matched against the old
    /// frames; some animated classes show a later pose on the left), or stays blank. HGSS scrambles the
    /// file from the end, like Diamond/Pearl's Pokémon sprites; Platinum from the start.</summary>
    private static byte[]? BuildScan(RomProject project, RomGameMetadata metadata, int trainerClass, TrainerGraphicSide side, byte[] oldFrames, byte[] newFrames, int frameCount)
    {
        string path = FilePath(project, trainerClass, 4, side);
        if (!File.Exists(path)) return null;
        byte[] file = File.ReadAllBytes(path);
        bool fromEnd = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;
        byte[]? scan = fromEnd ? BattleSpriteService.DecodeSpriteIndicesDiamondPearl(file) : BattleSpriteService.DecodeSpriteIndices(file);
        if (scan is null || file.Length != TileDataOffset + FrameBytes * 2) return null;

        byte[] Frame(byte[] frames, int frame) => frames.AsSpan(frame * FrameSize * FrameSize, FrameSize * FrameSize).ToArray();

        // The cipher always decodes the four pixels at its seed end as 0, so they are not compared.
        int Differences(int offsetX, byte[] frame)
        {
            int count = 0;
            for (int y = 0; y < FrameSize; y++)
            for (int x = 0; x < FrameSize; x++)
            {
                int scanX = offsetX + x;
                if ((y == 0 && scanX < 4) || (y == FrameSize - 1 && scanX >= FrameSize * 2 - 4)) continue;
                if (scan[y * FrameSize * 2 + scanX] != frame[y * FrameSize + x]) count++;
            }

            return count;
        }

        // Closest rather than exact: a hack may have edited the frames without the scan.
        int Shown(int offsetX, int fallback)
        {
            if (Differences(offsetX, new byte[FrameSize * FrameSize]) == 0) return -1;
            (int best, int fewest) = Enumerable.Range(0, frameCount)
                .Select(f => (Frame: f, Count: Differences(offsetX, Frame(oldFrames, f))))
                .MinBy(match => match.Count);
            return fewest <= FrameSize * FrameSize / 20 ? best : fallback;
        }

        int leftFrame = Shown(0, 0);
        int rightFrame = Shown(FrameSize, Math.Min(1, frameCount - 1));
        byte[] combined = new byte[FrameSize * 2 * FrameSize];
        foreach ((int frame, int offsetX) in new[] { (leftFrame, 0), (rightFrame, FrameSize) })
        {
            if (frame < 0) continue;
            byte[] pixels = Frame(newFrames, frame);
            for (int y = 0; y < FrameSize; y++) Array.Copy(pixels, y * FrameSize, combined, y * FrameSize * 2 + offsetX, FrameSize);
        }

        // Keep the file's own header; the encoder's is the Pokémon sprite one.
        byte[] encoded = BattleSpriteService.EncodeSpriteIndices(combined, isDiamondPearl: fromEnd);
        encoded.AsSpan(encoded.Length - FrameBytes * 2).CopyTo(file.AsSpan(TileDataOffset));
        return file;
    }

    private static byte[] DecodeFrames(Layout layout)
    {
        var tiles = NdsTileImage.Parse(layout.Tiles);
        byte[] frames = new byte[FrameSize * FrameSize * layout.FrameCount];
        foreach (Piece piece in layout.Pieces)
        {
            byte[] region = tiles.DecodeRegion(piece.ByteOffset, piece.Width, piece.Height);
            for (int y = 0; y < piece.Height; y++)
            for (int x = 0; x < piece.Width; x++)
            {
                int sx = piece.FlipX ? piece.Width - 1 - x : x;
                int sy = piece.FlipY ? piece.Height - 1 - y : y;
                frames[(piece.Frame * FrameSize + piece.Y + y) * FrameSize + piece.X + x] = region[sx + sy * piece.Width];
            }
        }

        return frames;
    }

    private static Layout ReadLayout(RomProject project, int trainerClass, TrainerGraphicSide side)
    {
        string tilesPath = FilePath(project, trainerClass, 0, side);
        string cellsPath = FilePath(project, trainerClass, 2, side);
        if (!File.Exists(tilesPath) || !File.Exists(cellsPath)) throw new InvalidDataException($"Trainer class {trainerClass} has no graphics files.");

        byte[] tiles = File.ReadAllBytes(tilesPath);
        int size = tiles.Length >= TileDataOffset ? (int)BinaryPrimitives.ReadUInt32LittleEndian(tiles.AsSpan(0x28)) : 0;
        bool valid = Encoding.ASCII.GetString(tiles, 0, Math.Min(4, tiles.Length)) == "RGCN"
            && BinaryPrimitives.ReadUInt32LittleEndian(tiles.AsSpan(0x1C)) == 3
            && (BinaryPrimitives.ReadUInt32LittleEndian(tiles.AsSpan(0x24)) & 0xFF) == 0
            && size > 0 && size % FrameBytes == 0 && TileDataOffset + size <= tiles.Length;
        if (!valid) throw new InvalidDataException($"Trainer class {trainerClass}'s tiles are not 80x80 16-color frames.");

        NdsCellBank cells = NdsCellBank.Parse(File.ReadAllBytes(cellsPath));
        int frameCount = size / FrameBytes;
        var pieces = new List<Piece>();
        var placed = new bool[frameCount];
        foreach (NdsCellBank.Bank bank in cells.Banks)
        {
            var byFrame = bank.Oams
                .Select(oam => (Oam: oam, Offset: (int)((oam.TileOffset << (int)cells.BlockSize) * 0x20 + bank.DataOffset)))
                .GroupBy(entry => entry.Offset / FrameBytes);
            foreach (var group in byFrame)
            {
                int frame = group.Key;
                if (frame >= frameCount || placed[frame]) continue;
                placed[frame] = true;
                int minX = group.Min(entry => entry.Oam.XOffset), minY = group.Min(entry => entry.Oam.YOffset);
                foreach ((NdsCellBank.Oam oam, int offset) in group)
                {
                    if (oam.PaletteIndex != 0) throw new InvalidDataException($"Trainer class {trainerClass} uses more than one palette, which importing does not support.");
                    var piece = new Piece(frame, oam.XOffset - minX, oam.YOffset - minY, oam.Width, oam.Height, offset, oam.FlipX, oam.FlipY);
                    if (piece.X + piece.Width > FrameSize || piece.Y + piece.Height > FrameSize || offset + piece.Width * piece.Height / 2 > (frame + 1) * FrameBytes)
                    {
                        throw new InvalidDataException($"Trainer class {trainerClass}'s frame {frame} is not laid out as an 80x80 frame.");
                    }

                    pieces.Add(piece);
                }
            }
        }

        return new Layout(tiles, frameCount, pieces);
    }
}
