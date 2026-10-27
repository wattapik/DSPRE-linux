using System.Globalization;
using System.Text;

namespace DSPRE.Core;

/// <summary>Builds a map's model from its grid of tiles the way Pokemon DS Map Studio's OBJ export does: every
/// tile is placed in its cell at its height, tileable tiles are stretched over runs of identical neighbours, and
/// the result is written as a Wavefront OBJ with an .mtl and the PNG textures it uses. That OBJ then goes through the
/// same conversion as any other (<see cref="MapModelImportService"/>).</summary>
public static class MapCreatorComposer
{
    /// <param name="Polygons">Quads count once, though they are two triangles to the game.</param>
    public sealed record Output(string ObjPath, int Polygons, int Triangles, int Materials);

    public sealed record Composition(List<TilePolygon> Polygons, int Triangles)
    {
        public int Materials => Polygons.Select(polygon => polygon.Material).Distinct().Count();
    }

    public static Output WriteObj(MapCreatorTileset tileset, MapCreatorMap map, string folder, string name, float tileUpscale = 1f, bool compact = true)
    {
        Composition composition = Compose(tileset, map, tileUpscale, compact);
        if (composition.Polygons.Count == 0) throw new InvalidOperationException("The map has no tiles on it yet.");

        Directory.CreateDirectory(folder);
        string objPath = Path.Combine(folder, name + ".obj");
        string mtlName = name + ".mtl";
        var obj = new StringBuilder();
        var mtl = new StringBuilder();
        obj.Append("mtllib ").Append(mtlName).Append("\no map\n");

        var positions = new Dictionary<(float, float, float), int>();
        var uvs = new Dictionary<(float, float), int>();
        var positionLines = new StringBuilder();
        var uvLines = new StringBuilder();
        static string F(float value) => value.ToString("0.######", CultureInfo.InvariantCulture);

        var corners = new List<int[]>(composition.Polygons.Count);
        foreach (TilePolygon polygon in composition.Polygons)
        {
            int[] indices = new int[polygon.Vertices.Length * 2];
            for (int i = 0; i < polygon.Vertices.Length; i++)
            {
                TileVertex v = polygon.Vertices[i];
                if (!positions.TryGetValue((v.X, v.Y, v.Z), out int p))
                {
                    p = positions[(v.X, v.Y, v.Z)] = positions.Count + 1;
                    positionLines.Append("v ").Append(F(v.X)).Append(' ').Append(F(v.Y)).Append(' ').Append(F(v.Z)).Append('\n');
                }

                if (!uvs.TryGetValue((v.U, v.V), out int t))
                {
                    t = uvs[(v.U, v.V)] = uvs.Count + 1;
                    uvLines.Append("vt ").Append(F(v.U)).Append(' ').Append(F(v.V)).Append('\n');
                }

                indices[i * 2] = p;
                indices[i * 2 + 1] = t;
            }

            corners.Add(indices);
        }

        obj.Append(positionLines).Append(uvLines);
        var names = new Dictionary<int, string>();
        var usedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (int material in composition.Polygons.Select(polygon => polygon.Material).Distinct().OrderBy(index => index))
        {
            string image = tileset.Materials[material].ImageName;
            string stem = Path.GetFileNameWithoutExtension(image).Replace(' ', '_');
            string unique = stem;
            for (int suffix = 1; !usedNames.Add(unique); suffix++) unique = $"{stem}_{suffix}";
            names[material] = unique;
            mtl.Append("newmtl ").Append(unique).Append("\nmap_Kd ").Append(Path.GetFileName(image)).Append("\n\n");
            string source = Path.Combine(tileset.FolderPath, image);
            if (File.Exists(source)) File.Copy(source, Path.Combine(folder, Path.GetFileName(image)), overwrite: true);
        }

        foreach (IGrouping<int, int> group in Enumerable.Range(0, composition.Polygons.Count).GroupBy(i => composition.Polygons[i].Material).OrderBy(group => group.Key))
        {
            obj.Append("usemtl ").Append(names[group.Key]).Append("\ns off\n");
            foreach (int i in group)
            {
                obj.Append('f');
                int[] indices = corners[i];
                for (int c = 0; c < indices.Length; c += 2) obj.Append(' ').Append(indices[c]).Append('/').Append(indices[c + 1]);
                obj.Append('\n');
            }
        }

        File.WriteAllText(objPath, obj.ToString());
        File.WriteAllText(Path.Combine(folder, mtlName), mtl.ToString());
        return new Output(objPath, composition.Polygons.Count, composition.Triangles, names.Count);
    }

    /// <param name="compact">Fuse the cells' polygons into fewer, as the converter needs; off, every cell is drawn as it is (the previews).</param>
    public static Composition Compose(MapCreatorTileset tileset, MapCreatorMap map, float tileUpscale = 1f, bool compact = true)
    {
        var polygons = new List<TilePolygon>();
        for (int layer = 0; layer < MapCreatorMap.Layers; layer++)
        {
            for (int c = 0; c < MapCreatorMap.Columns; c++)
            {
                for (int r = 0; r < MapCreatorMap.Rows; r++) Place(tileset, map, layer, c, r, tileUpscale, polygons);
            }
        }

        if (compact) polygons = MapCreatorPolygonMerger.Merge(polygons, material => TextureSize(tileset, material));
        return new Composition(polygons, polygons.Sum(polygon => polygon.IsQuad ? 2 : 1));
    }

    private static void Place(MapCreatorTileset tileset, MapCreatorMap map, int layer, int c, int r, float scale, List<TilePolygon> output)
    {
        int index = map.Tiles[layer][c, r];
        if (index < 0 || index >= tileset.Tiles.Count) return;

        MapTile tile = tileset.Tiles[index].Clone();
        MapGlobally(tile, c, r);
        if (scale != 1f) ScaleAboutCenter(tile, scale);
        Displace(tile, c - MapCreatorMap.Columns / 2, r - MapCreatorMap.Rows / 2, map.Heights[layer][c, r]);
        Offset(tile);
        output.AddRange(tile.Polygons);
    }

    /// <summary>The size the converter will give the material's texture (0 when it has none), which decides how far its coordinates may run.</summary>
    private static (int Width, int Height) TextureSize(MapCreatorTileset tileset, int material) =>
        tileset.GetImage(material) is { } image ? (ObjToImd.NearestPowerOfTwo(image.Width), ObjToImd.NearestPowerOfTwo(image.Height)) : (0, 0);

    // Textures that follow the world position rather than the tile, so neighbouring tiles continue each other's pattern.
    private static void MapGlobally(MapTile tile, int column, int row)
    {
        if (!tile.GlobalTextureMapping) return;
        Transform(tile, v => v with { U = (v.X + column) * tile.GlobalTextureScale, V = (v.Y + row) * tile.GlobalTextureScale });
    }

    private static void ScaleAboutCenter(MapTile tile, float scale)
    {
        TileVertex[] all = tile.Polygons.SelectMany(polygon => polygon.Vertices).ToArray();
        if (all.Length == 0) return;
        float cx = all.Average(v => v.X), cy = all.Average(v => v.Y), cz = all.Average(v => v.Z);
        Transform(tile, v => v with { X = (v.X - cx) * scale + cx, Y = (v.Y - cy) * scale + cy, Z = (v.Z - cz) * scale + cz });
    }

    private static void Displace(MapTile tile, int column, int row, int height) =>
        Transform(tile, v => v with { X = v.X + column, Y = v.Y + row, Z = v.Z + height });

    private static void Offset(MapTile tile)
    {
        if (tile.XOffset == 0f && tile.YOffset == 0f && tile.ZOffset == 0f) return;
        Transform(tile, v => v with { X = v.X + tile.XOffset, Y = v.Y + tile.YOffset, Z = v.Z + tile.ZOffset });
    }

    private static void Transform(MapTile tile, Func<TileVertex, TileVertex> change)
    {
        for (int i = 0; i < tile.Polygons.Count; i++)
        {
            TileVertex[] vertices = tile.Polygons[i].Vertices;
            for (int j = 0; j < vertices.Length; j++) vertices[j] = change(vertices[j]);
        }
    }
}
