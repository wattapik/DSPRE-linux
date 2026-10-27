using System.Globalization;
using System.Numerics;
using SharpGLTF.Schema2;
using SharpGLTF.Validation;

namespace DSPRE.Core;

/// <summary>Cuts the terrain of an existing map into tiles. The map's model (as apicula's GLB, 32 x 32 cells of 16
/// units centred on the origin) is clipped along the cell borders, and what lies inside each cell becomes a one-cell
/// tile at the cell's own coordinates, so tiles of any map can be put back together on the map creator's grid.
/// Cells with the same geometry and textures share one tile.</summary>
public static class MapCreatorTileCutter
{
    private const float CellSize = 16f;
    private const float Quantum = 4096f;

    /// <param name="Cells">The tile (an index into the tileset) that each cell of the map was cut into.</param>
    public sealed record Result(int TilesAdded, int CellsCut, IReadOnlyDictionary<(int Column, int Row), int> Cells);

    /// <summary>Converts the map's terrain with apicula and cuts it into <paramref name="tileset"/>, which is not saved.</summary>
    public static async Task<Result> CutMapAsync(RomProject project, RomGameMetadata metadata, string toolsDirectory, int mapId, int texturePackId, MapCreatorTileset tileset, string namePrefix)
    {
        MapFileParts parts = MapFileService.Load(project, metadata, mapId);
        string texturePath = TexturePackService.GetPath(project, TexturePackKind.Map, texturePackId);
        byte[]? pack = texturePackId >= 0 && File.Exists(texturePath) ? await File.ReadAllBytesAsync(texturePath) : null;
        string output = Path.Combine(project.RootPath, "expanded", "map_creator_cut");
        if (Directory.Exists(output)) Directory.Delete(output, true);
        try
        {
            await MapFileService.ConvertModelAsync(project, metadata, toolsDirectory, parts with { Buildings = [] }, pack, output, glb: true);
            string glb = Directory.EnumerateFiles(output, "*.glb", SearchOption.AllDirectories).FirstOrDefault()
                ?? throw new FileNotFoundException("apicula produced no GLB for this map's model.");
            return CutGlb(glb, tileset, namePrefix);
        }
        finally
        {
            if (Directory.Exists(output)) Directory.Delete(output, true);
        }
    }

    public static Result CutGlb(string glbPath, MapCreatorTileset tileset, string namePrefix)
    {
        ModelRoot model = ModelRoot.Load(glbPath, new ReadSettings { Validation = ValidationMode.TryFix });
        var cells = new Dictionary<(int Column, int Row), List<TilePolygon>>();
        var materialByImage = new Dictionary<int, int>();

        foreach (Mesh mesh in model.LogicalMeshes)
        {
            foreach (MeshPrimitive primitive in mesh.Primitives)
            {
                Accessor? positionAccessor = primitive.GetVertexAccessor("POSITION");
                if (positionAccessor is null) continue;
                IList<Vector3> positions = positionAccessor.AsVector3Array();
                IList<Vector2>? uvs = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                IList<Vector4>? colors = primitive.GetVertexAccessor("COLOR_0")?.AsColorArray(1f);
                Vector4 factor = primitive.Material?.FindChannel("BaseColor")?.Parameter ?? Vector4.One;
                int material = MaterialFor(tileset, primitive.Material, materialByImage);

                foreach ((int a, int b, int c) in primitive.GetTriangleIndices())
                {
                    TileVertex Vertex(int i)
                    {
                        Vector3 p = positions[i];
                        Vector2 uv = uvs is not null ? uvs[i] : Vector2.Zero;
                        Vector4 color = (colors is not null ? colors[i] : Vector4.One) * factor;
                        // glTF is y-up with v running top-down; Studio is z-up with v running bottom-up, and north is -z.
                        return new TileVertex(p.X / CellSize, -p.Z / CellSize, p.Y / CellSize, uv.X, 1f - uv.Y, 0f, 0f, 1f, Math.Clamp(color.X, 0f, 1f), Math.Clamp(color.Y, 0f, 1f), Math.Clamp(color.Z, 0f, 1f));
                    }

                    ClipIntoCells([Vertex(a), Vertex(b), Vertex(c)], material, cells);
                }
            }
        }

        var existing = new Dictionary<string, int>();
        for (int i = 0; i < tileset.Tiles.Count; i++) existing.TryAdd(Signature(tileset.Tiles[i].Polygons), i);

        int added = 0;
        var placement = new Dictionary<(int Column, int Row), int>();
        foreach (((int column, int row), List<TilePolygon> polygons) in cells.OrderBy(pair => pair.Key.Row).ThenBy(pair => pair.Key.Column))
        {
            string signature = Signature(polygons);
            if (existing.TryGetValue(signature, out int known))
            {
                placement[(column, row)] = known;
                continue;
            }

            placement[(column, row)] = tileset.Tiles.Count;
            existing[signature] = tileset.Tiles.Count;
            tileset.Tiles.Add(new MapTile { Name = $"{namePrefix}_{column:D2}_{row:D2}", Polygons = polygons });
            added++;
        }

        return new Result(added, cells.Count, placement);
    }

    private static int MaterialFor(MapCreatorTileset tileset, Material? material, Dictionary<int, int> materialByImage)
    {
        Image? image = material?.FindChannel("BaseColor")?.Texture?.PrimaryImage;
        int key = image?.LogicalIndex ?? -1;
        if (materialByImage.TryGetValue(key, out int existing)) return existing;

        // A model material is "<texture>_lm<number>"; the tile keeps the texture's own name, since that is what a map animation matches.
        string name = SafeName(LightMapSuffix.Replace(material?.Name ?? "untextured", "")) + ".png";
        byte[]? png = null;
        if (image?.Content.Content is ReadOnlyMemory<byte> content && image.Content.IsPng) png = content.ToArray();
        TilesetMaterial template = TilesetMaterial.ForImage(name);
        // Untextured materials keep a plain white texture so every tile polygon has an image to refer to.
        png ??= PlainWhitePng();
        return materialByImage[key] = tileset.AddMaterialImage(template, name, png);
    }

    private static readonly System.Text.RegularExpressions.Regex LightMapSuffix = new(@"_lm\d+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private static string SafeName(string name)
    {
        var characters = name.Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray();
        return characters.Length == 0 ? "material" : new string(characters);
    }

    private static byte[] PlainWhitePng()
    {
        const int size = 8;
        var raw = new byte[size * (1 + size * 4)];
        for (int y = 0; y < size; y++)
        {
            Array.Fill(raw, (byte)255, y * (1 + size * 4) + 1, size * 4);
        }

        var png = new MemoryStream();
        png.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
        var header = new byte[13];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header, size);
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(header.AsSpan(4), size);
        header[8] = 8;
        header[9] = 6;
        WriteChunk(png, "IHDR", header);

        var compressed = new MemoryStream();
        using (var zlib = new System.IO.Compression.ZLibStream(compressed, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true)) zlib.Write(raw);
        WriteChunk(png, "IDAT", compressed.ToArray());
        WriteChunk(png, "IEND", []);
        return png.ToArray();
    }

    private static void WriteChunk(Stream stream, string type, byte[] data)
    {
        var length = new byte[4];
        System.Buffers.Binary.BinaryPrimitives.WriteInt32BigEndian(length, data.Length);
        stream.Write(length);
        byte[] body = [.. System.Text.Encoding.ASCII.GetBytes(type), .. data];
        stream.Write(body);
        uint crc = 0xFFFFFFFF;
        foreach (byte b in body)
        {
            crc ^= b;
            for (int bit = 0; bit < 8; bit++) crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
        }

        System.Buffers.Binary.BinaryPrimitives.WriteUInt32BigEndian(length, ~crc);
        stream.Write(length);
    }

    private static void ClipIntoCells(TileVertex[] triangle, int material, Dictionary<(int, int), List<TilePolygon>> cells)
    {
        int half = MapCreatorMap.Columns / 2;
        float minX = triangle.Min(v => v.X), maxX = triangle.Max(v => v.X), minY = triangle.Min(v => v.Y), maxY = triangle.Max(v => v.Y);
        int firstColumn = Math.Max((int)MathF.Floor(minX) + half, 0), lastColumn = Math.Min((int)MathF.Ceiling(maxX) + half - 1, MapCreatorMap.Columns - 1);
        int firstRow = Math.Max((int)MathF.Floor(minY) + half, 0), lastRow = Math.Min((int)MathF.Ceiling(maxY) + half - 1, MapCreatorMap.Rows - 1);

        for (int column = firstColumn; column <= lastColumn; column++)
        {
            for (int row = firstRow; row <= lastRow; row++)
            {
                float left = column - half, bottom = row - half;
                List<TileVertex> polygon = [.. triangle];
                polygon = Clip(polygon, v => v.X - left, 0);
                polygon = Clip(polygon, v => left + 1 - v.X, 0);
                polygon = Clip(polygon, v => v.Y - bottom, 0);
                polygon = Clip(polygon, v => bottom + 1 - v.Y, 0);
                if (polygon.Count < 3 || Area(polygon) < 1e-7f) continue;

                if (!cells.TryGetValue((column, row), out List<TilePolygon>? list)) cells[(column, row)] = list = [];
                TileVertex Local(TileVertex v) => Quantize(v with { X = Math.Clamp(v.X - left, 0f, 1f), Y = Math.Clamp(v.Y - bottom, 0f, 1f) });
                for (int i = 1; i < polygon.Count - 1; i++)
                {
                    list.Add(new TilePolygon(material, [Local(polygon[0]), Local(polygon[i]), Local(polygon[i + 1])]));
                }
            }
        }
    }

    // Sutherland-Hodgman against one half-plane: keeps the part where distance(v) >= limit.
    private static List<TileVertex> Clip(List<TileVertex> polygon, Func<TileVertex, float> distance, float limit)
    {
        var result = new List<TileVertex>(polygon.Count + 1);
        for (int i = 0; i < polygon.Count; i++)
        {
            TileVertex current = polygon[i], next = polygon[(i + 1) % polygon.Count];
            float dc = distance(current) - limit, dn = distance(next) - limit;
            if (dc >= 0) result.Add(current);
            if ((dc >= 0) != (dn >= 0))
            {
                float t = dc / (dc - dn);
                result.Add(Lerp(current, next, t));
            }
        }

        return result;
    }

    private static TileVertex Lerp(TileVertex a, TileVertex b, float t) => new(
        a.X + (b.X - a.X) * t, a.Y + (b.Y - a.Y) * t, a.Z + (b.Z - a.Z) * t, a.U + (b.U - a.U) * t, a.V + (b.V - a.V) * t,
        a.NormalX + (b.NormalX - a.NormalX) * t, a.NormalY + (b.NormalY - a.NormalY) * t, a.NormalZ + (b.NormalZ - a.NormalZ) * t,
        a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t);

    private static float Area(List<TileVertex> polygon)
    {
        float area = 0f;
        for (int i = 0; i < polygon.Count; i++)
        {
            TileVertex a = polygon[i], b = polygon[(i + 1) % polygon.Count];
            area += a.X * b.Y - b.X * a.Y;
        }

        return MathF.Abs(area) / 2f;
    }

    private static float Q(float value) => MathF.Round(value * Quantum) / Quantum;

    private static TileVertex Quantize(TileVertex v) => v with { X = Q(v.X), Y = Q(v.Y), Z = Q(v.Z), U = Q(v.U), V = Q(v.V), R = MathF.Round(v.R * 255f) / 255f, G = MathF.Round(v.G * 255f) / 255f, B = MathF.Round(v.B * 255f) / 255f };

    // Two cells are the same tile when they draw the same polygons: whichever corner a polygon starts at, in whatever order,
    // within rounding, and with the texture offset by whole repeats (cells further along a repeating texture).
    private static string Signature(List<TilePolygon> polygons)
    {
        const float Step = 256f;
        float minU = polygons.Count == 0 ? 0f : polygons.Min(polygon => polygon.Vertices.Min(v => v.U));
        float minV = polygons.Count == 0 ? 0f : polygons.Min(polygon => polygon.Vertices.Min(v => v.V));
        float shiftU = MathF.Floor(minU), shiftV = MathF.Floor(minV);
        long R(float value) => (long)MathF.Round(value * Step);

        return string.Join('|', polygons.Select(polygon =>
        {
            string[] corners = polygon.Vertices
                .Select(v => string.Create(CultureInfo.InvariantCulture, $"{R(v.X)},{R(v.Y)},{R(v.Z)},{R(v.U - shiftU)},{R(v.V - shiftV)},{(int)MathF.Round(v.R * 31f)},{(int)MathF.Round(v.G * 31f)},{(int)MathF.Round(v.B * 31f)}"))
                .ToArray();
            int start = Array.IndexOf(corners, corners.Min(StringComparer.Ordinal));
            return $"{polygon.Material}:" + string.Join(';', corners.Skip(start).Concat(corners.Take(start)));
        }).Order(StringComparer.Ordinal));
    }
}
