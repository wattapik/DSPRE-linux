using System.Buffers.Binary;

namespace DSPRE.Core;

/// <summary>A route or city of the Pokédex area map: the map header it stands for and the cells it covers. The cells are the
/// <see cref="Rows"/> ('#' covered, '.' not) of a <see cref="Width"/> x <see cref="Height"/> box whose top-left cell is at
/// (<see cref="X"/>, <see cref="Y"/>).</summary>
public sealed class RoutePlace
{
    public uint Header { get; set; }
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; } = 1;
    public int Height { get; set; } = 1;
    public List<string> Rows { get; set; } = ["."];

    public bool Covers(int x, int y)
    {
        int column = x - X, row = y - Y;
        return column >= 0 && column < Width && row >= 0 && row < Height && Rows[row][column] == '#';
    }

    public IEnumerable<(int X, int Y)> Cells()
    {
        for (int row = 0; row < Height; row++)
        {
            for (int column = 0; column < Width; column++)
            {
                if (Rows[row][column] == '#') yield return (X + column, Y + row);
            }
        }
    }

    /// <summary>Covers or uncovers one cell, growing or shrinking the box to fit. False (and nothing changed) when the box would
    /// need more than <see cref="PokedexMapService.MaxCells"/> cells or leave the map.</summary>
    public bool SetCell(int x, int y, bool covered)
    {
        var cells = Cells().ToHashSet();
        if (covered) cells.Add((x, y));
        else cells.Remove((x, y));
        if (cells.Count == 0)
        {
            (Width, Height, Rows) = (1, 1, ["."]);
            return true;
        }

        int left = cells.Min(cell => cell.X), top = cells.Min(cell => cell.Y), right = cells.Max(cell => cell.X), bottom = cells.Max(cell => cell.Y);
        int width = right - left + 1, height = bottom - top + 1;
        if (left < 0 || top < 0 || right > byte.MaxValue || bottom > byte.MaxValue || width * height > PokedexMapService.MaxCells) return false;
        (X, Y, Width, Height) = (left, top, width, height);
        Rows = Enumerable.Range(0, height).Select(row => new string(Enumerable.Range(0, width).Select(column => cells.Contains((left + column, top + row)) ? '#' : '.').ToArray())).ToList();
        return true;
    }

    /// <summary>Moves the whole place; false when it would leave the map.</summary>
    public bool MoveBy(int dx, int dy)
    {
        if (X + dx < 0 || Y + dy < 0 || X + dx + Width - 1 > byte.MaxValue || Y + dy + Height - 1 > byte.MaxValue) return false;
        (X, Y) = (X + dx, Y + dy);
        return true;
    }
}

/// <summary>A special place of the Pokédex area map (a mine, cave, lake...): the map header it stands for and where its marker is.</summary>
public sealed class SpecialPlace
{
    public uint Header { get; set; }
    public int X { get; set; }
    public int Y { get; set; }

    /// <summary>The marker's third byte: 1 on Mt. Coronet and 0 on every other place.</summary>
    public int Flag { get; set; }
}

/// <summary>Area number <c>n</c> is <c>Routes[n - 1]</c> or <c>Specials[n - 1]</c>. The original game has 21 special places and 50 routes;
/// more can be added at the end, but whether the game reads them was not checked.</summary>
public sealed class PokedexAreaMap
{
    public List<RoutePlace> Routes { get; set; } = [];
    public List<SpecialPlace> Specials { get; set; } = [];
}

/// <summary>The places of the Pokédex area map (Platinum, members 0 to 3 of <c>zukan_enc_platinum.narc</c>): member 0 holds the special
/// places' markers (x, y, flag, 0 per u32), member 1 their map headers, member 2 the routes and cities (36 bytes each: x, y, width,
/// height, then width x height cell flags) and member 3 their map headers; entry 0 of each is unused and left as it is. Cells and markers
/// are 16 pixels square on the Town Map picture (<c>graphic/tmap_gra.narc</c>, rendered by <see cref="RenderTownMap"/>) with its top-left
/// at (<see cref="OriginX"/>, <see cref="OriginY"/>) - found by laying every cell on the picture, which they then all land on.</summary>
public static class PokedexMapService
{
    public const int CellSize = 16;
    public const int OriginX = 16;
    public const int OriginY = 8;
    public const int MapSize = 512;
    public const int MaxCells = 32;

    /// <summary>The most special places, and the most routes, the editor will write.</summary>
    public const int MaxPlaces = 255;
    private const int RecordSize = 36;

    public static bool IsSupported(RomProject project, RomGameMetadata metadata) =>
        PokedexAreaService.IsSupported(project, metadata) && File.Exists(TownMapPath(project));

    private static string TownMapPath(RomProject project) => Path.Combine(project.FilesPath, "graphic", "tmap_gra.narc");

    public static PokedexAreaMap Load(RomProject project)
    {
        string path = PokedexAreaService.ArchivePath(project);
        byte[] markers = NarcFile.ReadMember(path, 0), specialHeaders = NarcFile.ReadMember(path, 1), routes = NarcFile.ReadMember(path, 2), routeHeaders = NarcFile.ReadMember(path, 3);
        if (markers.Length % 4 != 0 || markers.Length < 8 || specialHeaders.Length != markers.Length) throw new InvalidDataException("The special place tables are damaged.");
        int specialCount = markers.Length / 4 - 1, routeCount = routeHeaders.Length / 4 - 1;
        if (routeHeaders.Length % 4 != 0 || routeCount < 1 || routes.Length != (routeCount + 1) * RecordSize) throw new InvalidDataException("The route tables are damaged.");

        var map = new PokedexAreaMap();
        for (int area = 1; area <= specialCount; area++)
        {
            uint marker = BinaryPrimitives.ReadUInt32LittleEndian(markers.AsSpan(area * 4));
            if (marker >> 24 != 0) throw new InvalidDataException($"Special place {area} has an unexpected marker.");
            map.Specials.Add(new SpecialPlace { Header = BinaryPrimitives.ReadUInt32LittleEndian(specialHeaders.AsSpan(area * 4)), X = (int)(marker & 0xFF), Y = (int)(marker >> 8 & 0xFF), Flag = (int)(marker >> 16 & 0xFF) });
        }

        for (int area = 1; area <= routeCount; area++)
        {
            ReadOnlySpan<byte> record = routes.AsSpan(area * RecordSize, RecordSize);
            int width = record[2], height = record[3];
            if (width < 1 || height < 1 || width * height > MaxCells) throw new InvalidDataException($"Route {area} is {width} x {height} cells.");
            var place = new RoutePlace { Header = BinaryPrimitives.ReadUInt32LittleEndian(routeHeaders.AsSpan(area * 4)), X = record[0], Y = record[1], Width = width, Height = height, Rows = [] };
            for (int row = 0; row < height; row++)
            {
                var cells = new char[width];
                for (int column = 0; column < width; column++)
                {
                    byte cell = record[4 + row * width + column];
                    if (cell > 1) throw new InvalidDataException($"Route {area} has a cell of {cell}; cells are 0 or 1.");
                    cells[column] = cell == 1 ? '#' : '.';
                }

                place.Rows.Add(new string(cells));
            }

            map.Routes.Add(place);
        }

        return map;
    }

    public static void Save(RomProject project, PokedexAreaMap map) =>
        EditJournal.Track(project, EditJournalDocuments.PokedexAreaMap, EditJournalDocuments.Keys(), () =>
        {
            string path = PokedexAreaService.ArchivePath(project);
            if (map.Specials.Count is < 1 or > MaxPlaces || map.Routes.Count is < 1 or > MaxPlaces) throw new InvalidDataException($"The map needs 1 to {MaxPlaces} special places and 1 to {MaxPlaces} routes.");
            if (PokedexAreaService.FindListedBeyond(project, map.Specials.Count, map.Routes.Count) is { } stranded) throw new InvalidDataException($"A place is still in use: {stranded}.");
            byte[] oldMarkers = NarcFile.ReadMember(path, 0), oldSpecialHeaders = NarcFile.ReadMember(path, 1), oldRoutes = NarcFile.ReadMember(path, 2), oldRouteHeaders = NarcFile.ReadMember(path, 3);
            byte[][] before = [oldMarkers, oldSpecialHeaders, oldRoutes, oldRouteHeaders];

            // Entry 0 of each table is unused and kept as it is; the places follow it.
            byte[] markers = new byte[(map.Specials.Count + 1) * 4], specialHeaders = new byte[markers.Length];
            byte[] routes = new byte[(map.Routes.Count + 1) * RecordSize], routeHeaders = new byte[(map.Routes.Count + 1) * 4];
            oldMarkers.AsSpan(0, 4).CopyTo(markers);
            oldSpecialHeaders.AsSpan(0, 4).CopyTo(specialHeaders);
            oldRoutes.AsSpan(0, RecordSize).CopyTo(routes);
            oldRouteHeaders.AsSpan(0, 4).CopyTo(routeHeaders);

            for (int area = 1; area <= map.Specials.Count; area++)
            {
                SpecialPlace place = map.Specials[area - 1];
                if (place.X is < 0 or > 255 || place.Y is < 0 or > 255 || place.Flag is < 0 or > 255) throw new InvalidDataException($"Special place {area} is outside the map.");
                BinaryPrimitives.WriteUInt32LittleEndian(markers.AsSpan(area * 4), (uint)(place.X | place.Y << 8 | place.Flag << 16));
                BinaryPrimitives.WriteUInt32LittleEndian(specialHeaders.AsSpan(area * 4), place.Header);
            }

            for (int area = 1; area <= map.Routes.Count; area++)
            {
                RoutePlace place = map.Routes[area - 1];
                if (place.Width < 1 || place.Height < 1 || place.Width * place.Height > MaxCells) throw new InvalidDataException($"Route {area} must cover a box of 1 to {MaxCells} cells.");
                if (place.X is < 0 or > 255 || place.Y is < 0 or > 255) throw new InvalidDataException($"Route {area} is outside the map.");
                if (place.Rows.Count != place.Height || place.Rows.Any(row => row.Length != place.Width || row.Any(c => c is not ('#' or '.')))) throw new InvalidDataException($"Route {area}'s cells do not match its {place.Width} x {place.Height} box.");
                Span<byte> record = routes.AsSpan(area * RecordSize, RecordSize);
                record[0] = (byte)place.X;
                record[1] = (byte)place.Y;
                record[2] = (byte)place.Width;
                record[3] = (byte)place.Height;
                for (int row = 0; row < place.Height; row++)
                {
                    for (int column = 0; column < place.Width; column++) record[4 + row * place.Width + column] = (byte)(place.Rows[row][column] == '#' ? 1 : 0);
                }

                BinaryPrimitives.WriteUInt32LittleEndian(routeHeaders.AsSpan(area * 4), place.Header);
            }

            byte[][] after = [markers, specialHeaders, routes, routeHeaders];
            var replacements = new Dictionary<int, byte[]>();
            for (int member = 0; member < 4; member++)
            {
                if (!before[member].AsSpan().SequenceEqual(after[member])) replacements[member] = after[member];
            }

            if (replacements.Count > 0) NarcFile.ReplaceMembers(path, replacements);
        });

    /// <summary>The Town Map picture the cells lie on, 512 x 512 RGBA (the parts outside the land and routes see-through).</summary>
    public static byte[] RenderTownMap(RomProject project)
    {
        string path = TownMapPath(project);
        byte[] Member(int index)
        {
            byte[] data = NarcFile.ReadMember(path, index);
            return Lz10.IsCompressed(data) ? Lz10.Decompress(data) : data;
        }

        byte[] tiles = Member(20), screen = Member(26);
        RgbColor[][] palettes = NdsPalette.Parse(Member(2)).Palettes;
        const int tileData = 0x30, screenData = 0x24;
        int entries = (screen.Length - screenData) / 2;
        var rgba = new byte[MapSize * MapSize * 4];
        for (int n = 0; n < entries; n++)
        {
            // A 512 x 512 background is four 32 x 32 blocks one after the other.
            int block = n / 1024, within = n % 1024;
            int originX = (block % 2 * 32 + within % 32) * 8, originY = (block / 2 * 32 + within / 32) * 8;
            ushort entry = BinaryPrimitives.ReadUInt16LittleEndian(screen.AsSpan(screenData + n * 2));
            int tile = entry & 0x3FF, bank = entry >> 12;
            bool flipX = (entry & 0x400) != 0, flipY = (entry & 0x800) != 0;
            RgbColor[] palette = palettes[Math.Min(bank, palettes.Length - 1)];
            for (int y = 0; y < 8; y++)
            {
                for (int x = 0; x < 8; x++)
                {
                    int sx = flipX ? 7 - x : x, sy = flipY ? 7 - y : y;
                    int at = tileData + tile * 32 + sy * 4 + sx / 2;
                    int value = at < tiles.Length ? tiles[at] >> (sx % 2 * 4) & 15 : 0;
                    if (value == 0 || value >= palette.Length) continue;
                    int pixel = ((originY + y) * MapSize + originX + x) * 4;
                    rgba[pixel] = palette[value].R;
                    rgba[pixel + 1] = palette[value].G;
                    rgba[pixel + 2] = palette[value].B;
                    rgba[pixel + 3] = 255;
                }
            }
        }

        return rgba;
    }
}
