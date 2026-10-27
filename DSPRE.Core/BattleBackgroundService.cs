namespace DSPRE.Core;

/// <summary>The battle background graphics archive (<c>battle/graphic/pl_batt_bg.narc</c> in Platinum, <c>batt_bg.narc</c> in Diamond/Pearl): an
/// LZ77-compressed NCGR (tiles), NSCR (screen: which tile goes where) or NCLR (palette) in every member. Which tiles, screen and
/// palette make up a given background is decided by the game's code, so they are listed and combined by hand here.</summary>
public sealed class BattleBackgroundArchive
{
    public enum MemberKind { Tiles, Screen, Palette, Other }

    public sealed record Member(int Index, MemberKind Kind, string Description, byte[] Data);

    /// <summary>A scenic backdrop: a sheet of 8-bit tiles, laid out by one shared screen, with a palette each for day, evening and night.</summary>
    public sealed record Backdrop(int Number, int Tiles, int Screen, int[] Palettes);

    public IReadOnlyList<Member> Members { get; }

    /// <summary>The backdrops found in the archive. Every 8-bit tile sheet is one, drawn through the archive's one large screen (which refers to all
    /// 1024 tiles of a sheet) with three consecutive 256-colour palettes, in the order of the sheets (day, evening, night).</summary>
    public IReadOnlyList<Backdrop> Backdrops { get; }

    /// <summary>A copy of the archive with some members' (uncompressed) contents replaced.</summary>
    public BattleBackgroundArchive WithMembers(IReadOnlyDictionary<int, byte[]> replacements) =>
        new(Members.Select(member => replacements.TryGetValue(member.Index, out byte[]? data) ? Describe(member.Index, data) : member).ToList());

    private BattleBackgroundArchive(List<Member> members)
    {
        Members = members;
        List<Member> sheets = members.Where(member => member.Kind == MemberKind.Tiles && TileDepth(member.Data) == 8 && BitConverter.ToUInt32(member.Data, 0x28) / 64 >= 1024).ToList();
        List<Member> palettes = members.Where(member => member.Kind == MemberKind.Palette && NdsPalette.Parse(member.Data) is { Palettes.Length: 1 } palette && palette.Palettes[0].Length == 256).ToList();
        Member? screen = members.FirstOrDefault(member => member.Kind == MemberKind.Screen && ReadScreen(member.Data).Entries.Max(entry => entry & 0x3FF) >= 1023);
        var backdrops = new List<Backdrop>();
        if (screen is not null && palettes.Count >= sheets.Count * 3)
        {
            for (int i = 0; i < sheets.Count; i++) backdrops.Add(new Backdrop(i + 1, sheets[i].Index, screen.Index, [palettes[3 * i].Index, palettes[3 * i + 1].Index, palettes[3 * i + 2].Index]));
        }

        Backdrops = backdrops;
    }

    public static string? FindArchive(RomProject project, RomGameMetadata metadata)
    {
        string folder = Path.Combine(project.FilesPath, "battle", "graphic");
        string[] names = metadata.HeaderFormat switch
        {
            MapHeaderFormat.Platinum => ["pl_batt_bg.narc"],
            MapHeaderFormat.DiamondPearl => ["batt_bg.narc"],
            _ => []
        };
        return names.Select(name => Path.Combine(folder, name)).FirstOrDefault(File.Exists);
    }

    public static bool IsSupported(RomProject project, RomGameMetadata metadata) => FindArchive(project, metadata) is not null;

    public static BattleBackgroundArchive Load(RomProject project, RomGameMetadata metadata)
    {
        string path = FindArchive(project, metadata) ?? throw new FileNotFoundException("This game has no battle background archive.");
        int count = NarcFile.MemberCount(path);
        var members = new List<Member>();
        for (int i = 0; i < count; i++)
        {
            byte[] data = NarcFile.ReadMember(path, i);
            try
            {
                if (Lz10.IsCompressed(data)) data = Lz10.Decompress(data);
            }
            catch (InvalidDataException)
            {
            }

            members.Add(Describe(i, data));
        }

        return new BattleBackgroundArchive(members);
    }

    private static Member Describe(int index, byte[] data)
    {
        string magic = data.Length >= 4 ? System.Text.Encoding.ASCII.GetString(data, 0, 4) : string.Empty;
        switch (magic)
        {
            case "RGCN" when data.Length >= 0x30:
            {
                int bpp = TileDepth(data);
                int tiles = (int)BitConverter.ToUInt32(data, 0x28) / (bpp * 8);
                return new Member(index, MemberKind.Tiles, $"{index:D3}  tiles: {tiles} at {bpp} bits", data);
            }
            case "RCSN" when data.Length >= 0x24:
            {
                (int width, int height, ushort[] entries) = ReadScreen(data);
                return new Member(index, MemberKind.Screen, $"{index:D3}  screen: {width} x {height}, tiles up to {entries.Max(entry => entry & 0x3FF)}", data);
            }
            case "RLCN" when data.Length >= 0x28:
            {
                NdsPalette palette = NdsPalette.Parse(data);
                return new Member(index, MemberKind.Palette, $"{index:D3}  palette: {palette.Palettes.Length} x {palette.Palettes[0].Length} colors", data);
            }
            default:
                return new Member(index, MemberKind.Other, $"{index:D3}  ({magic.Trim('\0')}, {data.Length:N0} bytes)", data);
        }
    }

    private static int TileDepth(byte[] ncgr) => BitConverter.ToUInt32(ncgr, 0x1C) == 4 ? 8 : 4;

    private static (int Width, int Height, ushort[] Entries) ReadScreen(byte[] nscr)
    {
        int width = BitConverter.ToUInt16(nscr, 0x18), height = BitConverter.ToUInt16(nscr, 0x1A);
        int length = (int)Math.Min(BitConverter.ToUInt32(nscr, 0x20), (uint)(nscr.Length - 0x24)) / 2;
        var entries = new ushort[length];
        for (int i = 0; i < length; i++) entries[i] = BitConverter.ToUInt16(nscr, 0x24 + 2 * i);
        return (width, height, entries.Length == 0 ? [0] : entries);
    }

    /// <summary>The tiles member that most likely belongs to a screen: the closest one before it holding enough tiles.</summary>
    public int SuggestTiles(int screenIndex)
    {
        if (Members[screenIndex].Kind != MemberKind.Screen) return -1;
        int highest = ReadScreen(Members[screenIndex].Data).Entries.Max(entry => entry & 0x3FF);
        for (int i = screenIndex - 1; i >= 0; i--)
        {
            Member member = Members[i];
            if (member.Kind == MemberKind.Tiles && BitConverter.ToUInt32(member.Data, 0x28) / (TileDepth(member.Data) * 8) > highest) return i;
        }

        return -1;
    }

    /// <summary>Draws tiles, laid out by a screen, in a palette. Without a screen the tiles are laid out in rows of 32.</summary>
    public TextureImage Render(int tilesIndex, int screenIndex, int paletteIndex)
    {
        byte[] ncgr = Members[tilesIndex].Data;
        int bpp = TileDepth(ncgr);
        int tileBytes = bpp * 8;
        byte[] data = ncgr.AsSpan(0x30, (int)Math.Min(BitConverter.ToUInt32(ncgr, 0x28), (uint)(ncgr.Length - 0x30))).ToArray();
        NdsPalette? palette = paletteIndex >= 0 && Members[paletteIndex].Kind == MemberKind.Palette ? NdsPalette.Parse(Members[paletteIndex].Data) : null;

        int width, height;
        ushort[] entries;
        if (screenIndex >= 0 && Members[screenIndex].Kind == MemberKind.Screen)
        {
            (width, height, entries) = ReadScreen(Members[screenIndex].Data);
        }
        else
        {
            int tiles = data.Length / tileBytes, columns = 32;
            width = columns * 8;
            height = (tiles + columns - 1) / columns * 8;
            entries = Enumerable.Range(0, tiles).Select(i => (ushort)i).ToArray();
        }

        int tilesAcross = Math.Max(width / 8, 1);
        var rgba = new byte[width * height * 4];
        for (int n = 0; n < entries.Length; n++)
        {
            int tx = n % tilesAcross * 8, ty = n / tilesAcross * 8;
            if (ty >= height) break;
            int tile = entries[n] & 0x3FF;
            bool flipX = (entries[n] & 0x400) != 0, flipY = (entries[n] & 0x800) != 0;
            int sub = entries[n] >> 12;
            RgbColor[] colors = palette is null ? [] : palette.Palettes[bpp == 4 ? Math.Min(sub, palette.Palettes.Length - 1) : 0];
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    int sx = flipX ? 7 - x : x, sy = flipY ? 7 - y : y;
                    int offset = tile * tileBytes + (bpp == 8 ? sy * 8 + sx : sy * 4 + sx / 2);
                    if (offset >= data.Length) continue;
                    int index = bpp == 8 ? data[offset] : (sx & 1) == 0 ? data[offset] & 15 : data[offset] >> 4;
                    int at = ((ty + y) * width + tx + x) * 4;
                    if (colors.Length == 0)
                    {
                        // No palette: greys, so the shapes can still be told apart.
                        byte grey = (byte)(bpp == 8 ? index : index * 17);
                        rgba[at] = rgba[at + 1] = rgba[at + 2] = grey;
                    }
                    else if (index < colors.Length)
                    {
                        rgba[at] = colors[index].R;
                        rgba[at + 1] = colors[index].G;
                        rgba[at + 2] = colors[index].B;
                    }

                    rgba[at + 3] = 255;
                }
            }
        }

        return new TextureImage(width, height, rgba);
    }
}

/// <summary>Replaces a backdrop's picture with PNG images. The backdrop's tile sheet is shared by its three palettes (day, evening and night), so
/// the images are taken together: each pixel's colours in the images form one entry of a common 256-colour palette set (the first colour,
/// which is see-through, stays as it is), reduced by median cut when there are more than fit.</summary>
public static class BattleBackdropImporter
{
    /// <param name="Replacements">The new (uncompressed) contents of the members that change: the tile sheet and the three palettes.</param>
    public sealed record Result(Dictionary<int, byte[]> Replacements, int Colors, bool Reduced, int TilesWritten);

    /// <param name="images">One PNG path per time of day (day, evening, night); a missing time uses the first image that is given.</param>
    public static Result Build(BattleBackgroundArchive archive, BattleBackgroundArchive.Backdrop backdrop, string?[] images)
    {
        if (images.All(string.IsNullOrEmpty)) throw new InvalidOperationException("No image was given.");
        byte[] screen = archive.Members[backdrop.Screen].Data, sheet = archive.Members[backdrop.Tiles].Data;
        int width = BitConverter.ToUInt16(screen, 0x18), height = BitConverter.ToUInt16(screen, 0x1A);
        int entryCount = (int)Math.Min(BitConverter.ToUInt32(screen, 0x20), (uint)(screen.Length - 0x24)) / 2;

        // Decode each given image; a time without one takes the first.
        var decoded = new StbImageSharp.ImageResult?[3];
        for (int time = 0; time < 3; time++)
        {
            if (string.IsNullOrEmpty(images[time])) continue;
            StbImageSharp.ImageResult image = StbImageSharp.ImageResult.FromMemory(File.ReadAllBytes(images[time]!), StbImageSharp.ColorComponents.RedGreenBlueAlpha);
            if (image.Width != width || image.Height != height) throw new InvalidDataException($"{Path.GetFileName(images[time])} is {image.Width} x {image.Height}; the backdrop is {width} x {height}.");
            decoded[time] = image;
        }

        StbImageSharp.ImageResult first = decoded.First(image => image is not null)!;
        for (int time = 0; time < 3; time++) decoded[time] ??= first;

        // Which pixels of the pictures each tile takes (the first place the screen puts it), and how it is turned there.
        int tilesAcross = width / 8;
        var tileSource = new Dictionary<int, (int X, int Y, bool FlipX, bool FlipY)>();
        for (int n = 0; n < entryCount; n++)
        {
            int entry = BitConverter.ToUInt16(screen, 0x24 + 2 * n), tile = entry & 0x3FF;
            if (tile == 0 || tileSource.ContainsKey(tile)) continue;
            tileSource[tile] = (n % tilesAcross * 8, n / tilesAcross * 8, (entry & 0x400) != 0, (entry & 0x800) != 0);
        }

        // One colour entry per distinct combination of the three images' colours (15-bit each).
        int Color(StbImageSharp.ImageResult image, int x, int y)
        {
            int at = (y * image.Width + x) * 4;
            return (image.Data[at] >> 3) | ((image.Data[at + 1] >> 3) << 5) | ((image.Data[at + 2] >> 3) << 10);
        }

        var counts = new Dictionary<(int, int, int), int>();
        foreach ((int tile, (int x, int y, bool _, bool _)) in tileSource)
        {
            for (int py = 0; py < 8; py++)
            {
                for (int px = 0; px < 8; px++)
                {
                    var key = (Color(decoded[0]!, x + px, y + py), Color(decoded[1]!, x + px, y + py), Color(decoded[2]!, x + px, y + py));
                    counts[key] = counts.GetValueOrDefault(key) + 1;
                }
            }
        }

        bool reduced = counts.Count > 255;
        List<(int, int, int)> palette = reduced ? MedianCut(counts, 255) : counts.Keys.ToList();
        var lookup = new Dictionary<(int, int, int), int>();
        int IndexOf((int, int, int) key)
        {
            if (lookup.TryGetValue(key, out int index)) return index;
            int best = 0;
            long bestDistance = long.MaxValue;
            for (int i = 0; i < palette.Count; i++)
            {
                long distance = Distance(palette[i], key);
                if (distance < bestDistance)
                {
                    bestDistance = distance;
                    best = i;
                }
            }

            return lookup[key] = best + 1;
        }

        byte[] newSheet = (byte[])sheet.Clone();
        foreach ((int tile, (int x, int y, bool flipX, bool flipY)) in tileSource)
        {
            for (int py = 0; py < 8; py++)
            {
                for (int px = 0; px < 8; px++)
                {
                    int sx = flipX ? 7 - px : px, sy = flipY ? 7 - py : py;
                    var key = (Color(decoded[0]!, x + sx, y + sy), Color(decoded[1]!, x + sx, y + sy), Color(decoded[2]!, x + sx, y + sy));
                    int offset = 0x30 + tile * 64 + py * 8 + px;
                    if (offset < newSheet.Length) newSheet[offset] = (byte)IndexOf(key);
                }
            }
        }

        var replacements = new Dictionary<int, byte[]> { [backdrop.Tiles] = newSheet };
        for (int time = 0; time < 3; time++)
        {
            byte[] member = (byte[])archive.Members[backdrop.Palettes[time]].Data.Clone();
            int colorsAt = 0x18 + (int)BitConverter.ToUInt32(member, 0x24);
            for (int i = 0; i < palette.Count && colorsAt + 2 * (i + 2) <= member.Length; i++)
            {
                int color = time == 0 ? palette[i].Item1 : time == 1 ? palette[i].Item2 : palette[i].Item3;
                BitConverter.TryWriteBytes(member.AsSpan(colorsAt + 2 * (i + 1)), (ushort)color);
            }

            for (int i = palette.Count + 1; i < 256 && colorsAt + 2 * (i + 1) <= member.Length; i++) BitConverter.TryWriteBytes(member.AsSpan(colorsAt + 2 * i), (ushort)0);
            replacements[backdrop.Palettes[time]] = member;
        }

        return new Result(replacements, palette.Count + 1, reduced, tileSource.Count);
    }

    private static long Distance((int, int, int) a, (int, int, int) b) => Squared(a.Item1, b.Item1) + Squared(a.Item2, b.Item2) + Squared(a.Item3, b.Item3);

    private static long Squared(int a, int b)
    {
        long r = ((a & 31) - (b & 31)), g = (((a >> 5) & 31) - ((b >> 5) & 31)), bl = (((a >> 10) & 31) - ((b >> 10) & 31));
        return r * r + g * g + bl * bl;
    }

    private static List<(int, int, int)> MedianCut(Dictionary<(int, int, int), int> counts, int size)
    {
        var boxes = new List<List<((int, int, int) Key, int Count)>> { counts.Select(pair => (pair.Key, pair.Value)).ToList() };
        while (boxes.Count < size)
        {
            // Split the box with the widest spread, along its widest channel (of the nine), at the weighted middle.
            int pick = -1, channel = 0, spread = 0;
            for (int b = 0; b < boxes.Count; b++)
            {
                if (boxes[b].Count < 2) continue;
                for (int c = 0; c < 9; c++)
                {
                    int low = int.MaxValue, high = int.MinValue;
                    foreach (var entry in boxes[b])
                    {
                        int value = Channel(entry.Key, c);
                        low = Math.Min(low, value);
                        high = Math.Max(high, value);
                    }

                    if (high - low > spread)
                    {
                        spread = high - low;
                        pick = b;
                        channel = c;
                    }
                }
            }

            if (pick < 0) break;
            List<((int, int, int) Key, int Count)> box = boxes[pick].OrderBy(entry => Channel(entry.Key, channel)).ToList();
            long half = box.Sum(entry => (long)entry.Count) / 2, running = 0;
            int cut = 1;
            for (int i = 0; i < box.Count - 1; i++)
            {
                running += box[i].Count;
                cut = i + 1;
                if (running >= half) break;
            }

            boxes[pick] = box.Take(cut).ToList();
            boxes.Add(box.Skip(cut).ToList());
        }

        return boxes.Select(box =>
        {
            long total = box.Sum(entry => (long)entry.Count);
            int[] mean = new int[9];
            for (int c = 0; c < 9; c++) mean[c] = (int)Math.Round(box.Sum(entry => (double)Channel(entry.Key, c) * entry.Count) / total);
            return (mean[0] | (mean[1] << 5) | (mean[2] << 10), mean[3] | (mean[4] << 5) | (mean[5] << 10), mean[6] | (mean[7] << 5) | (mean[8] << 10));
        }).ToList();
    }

    // Channel 0-2 red, green, blue of the day colour; 3-5 evening; 6-8 night.
    private static int Channel((int, int, int) key, int channel)
    {
        int color = channel / 3 == 0 ? key.Item1 : channel / 3 == 1 ? key.Item2 : key.Item3;
        return (color >> (channel % 3 * 5)) & 31;
    }

    /// <summary>Writes the replaced members into the project's archive, compressed only where the original member was (the game reads the Platinum palettes raw, so compressing them freezes it).</summary>
    public static void Save(RomProject project, RomGameMetadata metadata, int backdropNumber, string?[] images)
    {
        string path = BattleBackgroundArchive.FindArchive(project, metadata) ?? throw new FileNotFoundException("This game has no battle background archive.");
        BattleBackgroundArchive archive = BattleBackgroundArchive.Load(project, metadata);
        if (backdropNumber < 1 || backdropNumber > archive.Backdrops.Count) throw new InvalidOperationException($"There is no backdrop {backdropNumber}.");
        Result result = Build(archive, archive.Backdrops[backdropNumber - 1], images);
        NarcFile.ReplaceMembers(path, result.Replacements.ToDictionary(pair => pair.Key, pair => archive.Members[pair.Key].Kind != BattleBackgroundArchive.MemberKind.Palette && Lz10.IsCompressed(NarcFile.ReadMember(path, pair.Key)) ? Lz10.Compress(pair.Value) : pair.Value));
        EditJournal.Record(project, "ImportBattleBackdrop", ("backdrop", backdropNumber), ("day", images[0] is null ? null : Path.GetFullPath(images[0]!)), ("evening", images[1] is null ? null : Path.GetFullPath(images[1]!)), ("night", images[2] is null ? null : Path.GetFullPath(images[2]!)));
    }
}
