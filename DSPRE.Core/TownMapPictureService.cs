using System.Buffers.Binary;

namespace DSPRE.Core;

/// <summary>Which of the archive's two 512 x 512 Town Map pictures: the route diagram (screen 26: roads and squares for the towns) or the terrain map
/// (screen 25: the land). They share one tile sheet and palette but use different palette banks.</summary>
public enum TownMapView { Routes, Terrain }

/// <summary>A Town Map picture as 512 rows of 512 characters: <c>0</c> see-through and <c>1</c> to <c>F</c> the colors of the picture's palette bank
/// (<see cref="TownMapPictureService.Palette"/>).</summary>
public sealed class TownMapPicture
{
    public List<string> Rows { get; set; } = [];

    public string this[int y] => Rows[y];

    public int Get(int x, int y)
    {
        char c = Rows[y][x];
        return c <= '9' ? c - '0' : (c | 0x20) - 'a' + 10;
    }

    public void Set(int x, int y, int color)
    {
        char[] row = Rows[y].ToCharArray();
        row[x] = "0123456789ABCDEF"[color];
        Rows[y] = new string(row);
    }
}

/// <summary>The Town Map pictures (Platinum, <c>graphic/tmap_gra.narc</c>): member 20 is the 768-tile sheet (4 bits a pixel), members 25 (terrain) and 26
/// (route diagram) the 512 x 512 backgrounds that place tiles (four 32 x 32 blocks of entries one after the other, each entry a tile number, two
/// flip bits and a palette bank), member 2 the palette. Each picture uses one palette bank (0 for the terrain, 1 for the route diagram); the tile
/// sheet and palette are shared with the archive's other screens, so tiles they use are never changed - a changed 8 x 8 block reuses a tile
/// (flipped if need be) that already has its pixels or takes a tile no other screen uses.</summary>
public static class TownMapPictureService
{
    public const int Size = PokedexMapService.MapSize;
    private const int SheetMember = 20, PaletteMember = 2;
    private const int TileDataOffset = 0x30, ScreenDataOffset = 0x24, TileBytes = 32, TileCount = 768, BlocksAcross = Size / 8;
    private static readonly int[] AllScreens = [22, 23, 24, 25, 26, 27, 28, 29];

    private static int ScreenMember(TownMapView view) => view == TownMapView.Terrain ? 25 : 26;

    /// <summary>The palette bank the picture is drawn with.</summary>
    public static int PictureBank(TownMapView view) => view == TownMapView.Terrain ? 0 : 1;

    private static IEnumerable<int> OtherScreens(TownMapView view) => AllScreens.Where(screen => screen != ScreenMember(view));

    private static string ArchivePath(RomProject project) => Path.Combine(project.FilesPath, "graphic", "tmap_gra.narc");

    public static bool IsSupported(RomProject project, RomGameMetadata metadata) =>
        metadata.HeaderFormat == MapHeaderFormat.Platinum && File.Exists(ArchivePath(project));

    private static byte[] Member(string path, int member, out bool compressed)
    {
        byte[] data = NarcFile.ReadMember(path, member);
        compressed = Lz10.IsCompressed(data);
        return compressed ? Lz10.Decompress(data) : data;
    }

    /// <summary>The 16 colors of the picture's palette bank (entry 0 is the see-through one).</summary>
    public static RgbColor[] Palette(RomProject project, TownMapView view = TownMapView.Routes) => NdsPalette.Parse(Member(ArchivePath(project), PaletteMember, out _)).Palettes[PictureBank(view)];

    private static int EntryAt(byte[] screen, int tileX, int tileY)
    {
        int block = tileY / 32 * 2 + tileX / 32, within = tileY % 32 * 32 + tileX % 32;
        return BinaryPrimitives.ReadUInt16LittleEndian(screen.AsSpan(ScreenDataOffset + (block * 1024 + within) * 2));
    }

    private static void SetEntry(byte[] screen, int tileX, int tileY, int entry)
    {
        int block = tileY / 32 * 2 + tileX / 32, within = tileY % 32 * 32 + tileX % 32;
        BinaryPrimitives.WriteUInt16LittleEndian(screen.AsSpan(ScreenDataOffset + (block * 1024 + within) * 2), (ushort)entry);
    }

    private static int TilePixel(byte[] sheet, int tile, int x, int y) => sheet[TileDataOffset + tile * TileBytes + y * 4 + x / 2] >> (x % 2 * 4) & 15;

    /// <summary>The picture as palette indexes, row by row: 0 where see-through, 1 to 15 for the bank's colors.</summary>
    private static byte[] Decode(byte[] sheet, byte[] screen, int pictureBank)
    {
        var pixels = new byte[Size * Size];
        for (int tileY = 0; tileY < BlocksAcross; tileY++)
        {
            for (int tileX = 0; tileX < BlocksAcross; tileX++)
            {
                int entry = EntryAt(screen, tileX, tileY);
                int tile = entry & 0x3FF, bank = entry >> 12;
                bool flipX = (entry & 0x400) != 0, flipY = (entry & 0x800) != 0;
                for (int y = 0; y < 8; y++)
                {
                    for (int x = 0; x < 8; x++)
                    {
                        int value = TilePixel(sheet, tile, flipX ? 7 - x : x, flipY ? 7 - y : y);
                        if (value != 0 && bank != pictureBank) throw new InvalidDataException($"The Town Map picture uses palette bank {bank} at tile ({tileX}, {tileY}); only bank {pictureBank} is supported.");
                        pixels[(tileY * 8 + y) * Size + tileX * 8 + x] = (byte)value;
                    }
                }
            }
        }

        return pixels;
    }

    public static TownMapPicture Load(RomProject project, TownMapView view = TownMapView.Routes)
    {
        string path = ArchivePath(project);
        byte[] pixels = Decode(Member(path, SheetMember, out _), Member(path, ScreenMember(view), out _), PictureBank(view));
        var picture = new TownMapPicture();
        for (int y = 0; y < Size; y++) picture.Rows.Add(string.Create(Size, (pixels, y), (span, state) => { for (int x = 0; x < Size; x++) span[x] = "0123456789ABCDEF"[state.pixels[state.y * Size + x]]; }));
        return picture;
    }

    private static byte[] ToPixels(TownMapPicture picture)
    {
        if (picture.Rows.Count != Size || picture.Rows.Any(row => row.Length != Size || row.Any(c => !Uri.IsHexDigit(c)))) throw new InvalidDataException($"The Town Map picture must be {Size} rows of {Size} hexadecimal digits.");
        var pixels = new byte[Size * Size];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++) pixels[y * Size + x] = (byte)Convert.ToInt32(picture.Rows[y][x].ToString(), 16);
        }

        return pixels;
    }

    private static byte[] TileOf(byte[] pixels, int tileX, int tileY, bool flipX, bool flipY)
    {
        var tile = new byte[TileBytes];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int value = pixels[(tileY * 8 + (flipY ? 7 - y : y)) * Size + tileX * 8 + (flipX ? 7 - x : x)];
                tile[y * 4 + x / 2] |= (byte)(value << (x % 2 * 4));
            }
        }

        return tile;
    }

    private static byte[] Flipped(byte[] tile, bool flipX, bool flipY)
    {
        var result = new byte[TileBytes];
        for (int y = 0; y < 8; y++)
        {
            for (int x = 0; x < 8; x++)
            {
                int value = tile[(flipY ? 7 - y : y) * 4 + (flipX ? 7 - x : x) / 2] >> ((flipX ? 7 - x : x) % 2 * 4) & 15;
                result[y * 4 + x / 2] |= (byte)(value << (x % 2 * 4));
            }
        }

        return result;
    }

    /// <summary>How the picture would be written: the new tile sheet and screen, and how many tiles it needs that are not already there
    /// against how many the archive has free for it.</summary>
    private sealed record Plan(byte[] Sheet, byte[] Screen, bool SheetCompressed, bool ScreenCompressed, int NewTilesNeeded, int FreeTiles, bool Changed)
    {
        public bool Fits => NewTilesNeeded <= FreeTiles;
    }

    private static Plan MakePlan(string path, TownMapPicture picture, TownMapView view)
    {
        byte[] sheet = Member(path, SheetMember, out bool sheetCompressed), screen = Member(path, ScreenMember(view), out bool screenCompressed);
        int bank = PictureBank(view);
        byte[] wanted = ToPixels(picture), current = Decode(sheet, screen, bank);

        // Tiles other screens use are never touched.
        var pinned = new HashSet<int>();
        foreach (int other in OtherScreens(view))
        {
            byte[] data = Member(path, other, out _);
            for (int at = ScreenDataOffset; at + 2 <= data.Length; at += 2) pinned.Add(BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(at)) & 0x3FF);
        }

        var changed = new List<(int X, int Y)>();
        for (int tileY = 0; tileY < BlocksAcross; tileY++)
        {
            for (int tileX = 0; tileX < BlocksAcross; tileX++)
            {
                if (BlockDiffers(wanted, current, tileX, tileY)) changed.Add((tileX, tileY));
                else pinned.Add(EntryAt(screen, tileX, tileY) & 0x3FF);
            }
        }

        // Every pinned tile, in each of its four orientations, is something a changed block can reuse as it is.
        var reusable = new Dictionary<string, (int Tile, bool FlipX, bool FlipY)>();
        void Offer(int tile)
        {
            byte[] bytes = sheet.AsSpan(TileDataOffset + tile * TileBytes, TileBytes).ToArray();
            foreach ((bool flipX, bool flipY) in new[] { (false, false), (true, false), (false, true), (true, true) })
            {
                reusable.TryAdd(Convert.ToHexString(Flipped(bytes, flipX, flipY)), (tile, flipX, flipY));
            }
        }

        foreach (int tile in pinned) Offer(tile);
        var free = new Queue<int>(Enumerable.Range(0, TileCount).Where(tile => !pinned.Contains(tile)));
        int freeTiles = free.Count;
        int blank = pinned.Where(tile => sheet.AsSpan(TileDataOffset + tile * TileBytes, TileBytes).ToArray().All(b => b == 0)).DefaultIfEmpty(-1).First();
        byte[] newSheet = (byte[])sheet.Clone(), newScreen = (byte[])screen.Clone();
        int needed = 0;
        var overflow = new HashSet<string>();
        foreach ((int tileX, int tileY) in changed)
        {
            byte[] tile = TileOf(wanted, tileX, tileY, false, false);
            if (tile.All(b => b == 0) && blank >= 0)
            {
                SetEntry(newScreen, tileX, tileY, blank);
                continue;
            }

            string key = Convert.ToHexString(tile);
            if (reusable.TryGetValue(key, out var found))
            {
                SetEntry(newScreen, tileX, tileY, found.Tile | (found.FlipX ? 0x400 : 0) | (found.FlipY ? 0x800 : 0) | bank << 12);
            }
            else if (free.Count > 0)
            {
                int slot = free.Dequeue();
                needed++;
                tile.CopyTo(newSheet, TileDataOffset + slot * TileBytes);
                reusable[key] = (slot, false, false);
                SetEntry(newScreen, tileX, tileY, slot | bank << 12);
            }
            else if (overflow.Add(key))
            {
                // Out of room: keep counting what is missing, but write nothing for it.
                needed++;
            }
        }

        return new Plan(newSheet, newScreen, sheetCompressed, screenCompressed, needed, freeTiles, changed.Count > 0);
    }

    /// <summary>How many tiles the picture would need that are not already in the archive, and how many the archive has free for them; the
    /// picture can be saved when the first is no more than the second.</summary>
    public static (int Needed, int Free) TileBudget(RomProject project, TownMapPicture picture, TownMapView view = TownMapView.Routes)
    {
        Plan plan = MakePlan(ArchivePath(project), picture, view);
        return (plan.NewTilesNeeded, plan.FreeTiles);
    }

    /// <summary>Writes the picture: blocks that did not change keep their tile, the others reuse or take a tile.</summary>
    public static void Save(RomProject project, TownMapPicture picture, TownMapView view = TownMapView.Routes) =>
        EditJournal.Track(project, EditJournalDocuments.TownMapPicture, view == TownMapView.Terrain ? EditJournalDocuments.Keys(("view", "Terrain")) : EditJournalDocuments.Keys(), () =>
        {
            string path = ArchivePath(project);
            Plan plan = MakePlan(path, picture, view);
            if (!plan.Changed) return;
            if (!plan.Fits) throw new InvalidDataException($"The picture needs {plan.NewTilesNeeded} new 8 x 8 tiles but only {plan.FreeTiles} are free. Undo some of the changes (or use fewer different details and colors).");

            NarcFile.ReplaceMembers(path, new Dictionary<int, byte[]>
            {
                [SheetMember] = plan.SheetCompressed ? Lz10.Compress(plan.Sheet) : plan.Sheet,
                [ScreenMember(view)] = plan.ScreenCompressed ? Lz10.Compress(plan.Screen) : plan.Screen
            });
        });

    private static bool BlockDiffers(byte[] wanted, byte[] current, int tileX, int tileY)
    {
        for (int y = 0; y < 8; y++)
        {
            int at = (tileY * 8 + y) * Size + tileX * 8;
            if (!wanted.AsSpan(at, 8).SequenceEqual(current.AsSpan(at, 8))) return true;
        }

        return false;
    }

    /// <summary>The picture as a 512 x 512 RGBA image in the bank's colors (see-through where 0).</summary>
    public static byte[] ToRgba(TownMapPicture picture, RgbColor[] palette)
    {
        var rgba = new byte[Size * Size * 4];
        for (int y = 0; y < Size; y++)
        {
            for (int x = 0; x < Size; x++)
            {
                int color = picture.Get(x, y);
                if (color == 0) continue;
                int at = (y * Size + x) * 4;
                rgba[at] = palette[color].R;
                rgba[at + 1] = palette[color].G;
                rgba[at + 2] = palette[color].B;
                rgba[at + 3] = 255;
            }
        }

        return rgba;
    }

    /// <summary>A 512 x 512 RGBA image as a picture: under half opacity is see-through, any other pixel takes the closest of the bank's colors.</summary>
    public static TownMapPicture FromRgba(byte[] rgba, int width, int height, RgbColor[] palette)
    {
        if (width != Size || height != Size) throw new InvalidDataException($"The image is {width}x{height}; the picture is {Size}x{Size}.");
        var picture = new TownMapPicture();
        var nearest = new Dictionary<int, int>();
        for (int y = 0; y < Size; y++)
        {
            var row = new char[Size];
            for (int x = 0; x < Size; x++)
            {
                int at = (y * Size + x) * 4;
                if (rgba[at + 3] < 128)
                {
                    row[x] = '0';
                    continue;
                }

                int key = rgba[at] << 16 | rgba[at + 1] << 8 | rgba[at + 2];
                if (!nearest.TryGetValue(key, out int index))
                {
                    long best = long.MaxValue;
                    for (int i = 1; i < palette.Length; i++)
                    {
                        long distance = Square(rgba[at] - palette[i].R) + Square(rgba[at + 1] - palette[i].G) + Square(rgba[at + 2] - palette[i].B);
                        if (distance < best)
                        {
                            best = distance;
                            index = i;
                        }
                    }

                    nearest[key] = index;
                }

                row[x] = "0123456789ABCDEF"[index];
            }

            picture.Rows.Add(new string(row));
        }

        return picture;
    }

    private static long Square(int value) => (long)value * value;
}
