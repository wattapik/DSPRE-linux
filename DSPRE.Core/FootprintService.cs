namespace DSPRE.Core;

/// <summary>One species' Pokédex footprint as 16 rows of 16 characters: <c>.</c> see-through, <c>k</c> the footprint's black.</summary>
public sealed class Footprint
{
    public List<string> Rows { get; set; } = [];
}

public sealed class FootprintSet
{
    public List<Footprint> Footprints { get; set; } = [];
}

/// <summary>Pokédex footprints (<c>poketool/pokefoot/pokefoot.narc</c>, Platinum). Member 0 is the shared palette, 1 and 2 the shared
/// animation and cell bank, and member <c>species + 3</c> an LZ77-compressed 4-bit tile file (species 0 is a placeholder). The cell bank
/// draws every footprint the same way: a 16x8 piece from tiles 4 and 5 above a 16x8 piece from tiles 0 and 1. The other four tiles
/// are fill the game does not show, so they are left as they are. A pixel is 0 (see-through) or an ink: palette entry 4 or entry 1, both
/// black in the file's palette. A footprint uses one of them throughout (1 for most of Treecko to Rayquaza, 4 for the rest), and an edit keeps
/// the one the species has.</summary>
public static class FootprintService
{
    public const int Size = 16;
    private const int FirstMember = 3;
    private const int TileFileSize = 0x130;
    private const int TileDataOffset = 0x30;
    private const int TileBytes = 32;
    private const byte InkA = 4, InkB = 1;

    // Tile of the top band's left half, the top band's right half, the bottom band's left half, the bottom band's right half.
    private static readonly int[] BandTiles = [4, 5, 0, 1];

    public static string ArchivePath(RomProject project) => Path.Combine(project.FilesPath, "poketool", "pokefoot", "pokefoot.narc");

    public static bool IsSupported(RomProject project, RomGameMetadata metadata) =>
        metadata.HeaderFormat == MapHeaderFormat.Platinum && File.Exists(ArchivePath(project));

    /// <summary>Species 0 to the last one the archive has.</summary>
    public static FootprintSet Load(RomProject project)
    {
        string path = ArchivePath(project);
        var set = new FootprintSet();
        for (int member = FirstMember, count = NarcFile.MemberCount(path); member < count; member++) set.Footprints.Add(Read(Tiles(path, member), member));
        return set;
    }

    /// <summary>The color the footprints are drawn in (palette entry 4).</summary>
    public static RgbColor Color(RomProject project) => NdsPalette.Parse(NarcFile.ReadMember(ArchivePath(project), 0)).Palettes[0][InkA];

    public static void Save(RomProject project, FootprintSet set) =>
        EditJournal.Track(project, EditJournalDocuments.Footprints, EditJournalDocuments.Keys(), () =>
        {
            string path = ArchivePath(project);
            int count = NarcFile.MemberCount(path) - FirstMember;
            if (set.Footprints.Count != count) throw new InvalidDataException($"There are {set.Footprints.Count} footprints; the archive has {count}.");
            var replacements = new Dictionary<int, byte[]>();
            for (int species = 0; species < count; species++)
            {
                int member = species + FirstMember;
                byte[] original = NarcFile.ReadMember(path, member);
                byte[] tiles = Tiles(path, member);
                Footprint wanted = set.Footprints[species];
                if (Validate(wanted, species).SequenceEqual(Read(tiles, member).Rows)) continue;

                byte[] changed = (byte[])tiles.Clone();
                Write(changed, wanted.Rows, InkOf(tiles, species));
                replacements[member] = Lz10.IsCompressed(original) ? Lz10.Compress(changed) : changed;
            }

            if (replacements.Count > 0) NarcFile.ReplaceMembers(path, replacements);
        });

    private static IReadOnlyList<string> Validate(Footprint footprint, int species)
    {
        if (footprint.Rows.Count != Size || footprint.Rows.Any(row => row.Length != Size || row.Any(c => c is not ('.' or 'k'))))
        {
            throw new InvalidDataException($"Footprint {species} must be {Size} rows of {Size} characters from '.' and 'k'.");
        }

        return footprint.Rows;
    }

    private static byte[] Tiles(string path, int member)
    {
        byte[] data = NarcFile.ReadMember(path, member);
        if (Lz10.IsCompressed(data)) data = Lz10.Decompress(data);
        if (data.Length != TileFileSize || data[0] != 'R' || data[1] != 'G' || data[2] != 'C' || data[3] != 'N')
        {
            throw new InvalidDataException($"Footprint member {member} is not a 16x16 tile file.");
        }

        return data;
    }

    private static int Pixel(byte[] tiles, int tile, int x, int y)
    {
        byte value = tiles[TileDataOffset + tile * TileBytes + y * 4 + x / 2];
        return (value >> (x % 2 * 4)) & 15;
    }

    private static Footprint Read(byte[] tiles, int member)
    {
        var footprint = new Footprint();
        for (int y = 0; y < Size; y++)
        {
            var row = new char[Size];
            for (int x = 0; x < Size; x++)
            {
                int tile = BandTiles[(y / 8) * 2 + x / 8];
                int pixel = Pixel(tiles, tile, x % 8, y % 8);
                row[x] = pixel switch { 0 => '.', InkA or InkB => 'k', _ => throw new InvalidDataException($"Footprint member {member} uses palette entry {pixel}, which it should not.") };
            }

            footprint.Rows.Add(new string(row));
        }

        return footprint;
    }

    /// <summary>The ink the species' shown tiles use; a blank one uses what its neighbours in the list do.</summary>
    private static byte InkOf(byte[] tiles, int species)
    {
        foreach (int tile in BandTiles)
        {
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    if (Pixel(tiles, tile, x, y) is int pixel and not 0) return (byte)pixel;
                }
            }
        }

        return species is >= 252 and <= 386 ? InkB : InkA;
    }

    private static void Write(byte[] tiles, IReadOnlyList<string> rows, byte ink)
    {
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                byte value = rows[y][x] == 'k' ? ink : (byte)0;
                int at = TileDataOffset + BandTiles[(y / 8) * 2 + x / 8] * TileBytes + (y % 8) * 4 + (x % 8) / 2;
                int shift = x % 2 * 4;
                tiles[at] = (byte)((tiles[at] & ~(15 << shift)) | (value << shift));
            }
        }
    }

    /// <summary>The footprint as a 16x16 RGBA image: its pixels in <paramref name="color"/>, the rest see-through.</summary>
    public static byte[] ToRgba(Footprint footprint, RgbColor color)
    {
        var rgba = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                if (footprint.Rows[y][x] == '.') continue;
                int at = (y * Size + x) * 4;
                rgba[at] = color.R;
                rgba[at + 1] = color.G;
                rgba[at + 2] = color.B;
                rgba[at + 3] = 255;
            }
        }

        return rgba;
    }

    /// <summary>A 16x16 RGBA image as a footprint: a pixel of half opacity or more is part of the footprint, whatever its color.</summary>
    public static Footprint FromRgba(byte[] rgba, int width, int height)
    {
        if (width != Size || height != Size) throw new InvalidDataException($"The image is {width}x{height}; a footprint is {Size}x{Size}.");
        var footprint = new Footprint();
        for (int y = 0; y < Size; y++)
        {
            var row = new char[Size];
            for (int x = 0; x < Size; x++)
            {
                int at = (y * Size + x) * 4;
                row[x] = rgba[at + 3] < 128 ? '.' : 'k';
            }

            footprint.Rows.Add(new string(row));
        }

        return footprint;
    }
}
