using System.Buffers.Binary;
using System.Text;
using StbImageSharp;

namespace DSPRE.Core;

/// <summary>One corner of a tile polygon. Positions are in Pokemon DS Map Studio's space: x east and y north in
/// whole tiles from the tile's own corner, z up; texture coordinates follow OBJ (v runs bottom-up).</summary>
public readonly record struct TileVertex(float X, float Y, float Z, float U, float V, float NormalX = 0f, float NormalY = 0f, float NormalZ = 1f, float R = 1f, float G = 1f, float B = 1f);

/// <summary>A triangle or quad of a tile, drawn with one of the tileset's materials.</summary>
public sealed record TilePolygon(int Material, TileVertex[] Vertices)
{
    public bool IsQuad => Vertices.Length == 4;
}

public sealed class TilesetMaterial
{
    public string ImageName = string.Empty;
    public string MaterialName = string.Empty;
    public string PaletteName = string.Empty;
    public string TextureName = string.Empty;
    public bool Fog = true, BothFaces, UniformNormals = true, AlwaysInclude;
    public bool Light0 = true, Light1, Light2, Light3;
    public int[] Diffuse = [25, 25, 25], Ambient = [31, 31, 31], Specular = [0, 0, 0], Emission = [0, 0, 0];
    public bool RenderBorder, VertexColors;
    public int Alpha = 31, TexGenMode, TilingU, TilingV, ColorFormat = 1;

    public static TilesetMaterial ForImage(string imageName)
    {
        string name = Path.GetFileNameWithoutExtension(imageName);
        return new TilesetMaterial { ImageName = imageName, MaterialName = name, TextureName = name, PaletteName = name + "_pl" };
    }

    public TilesetMaterial Clone()
    {
        var copy = (TilesetMaterial)MemberwiseClone();
        copy.Diffuse = (int[])Diffuse.Clone();
        copy.Ambient = (int[])Ambient.Clone();
        copy.Specular = (int[])Specular.Clone();
        copy.Emission = (int[])Emission.Clone();
        return copy;
    }
}

/// <summary>A piece of map that goes into one grid cell, or Width x Height cells. A tileable tile is stretched
/// over a run of identical neighbours instead of repeated, so floors and walls stay a few big polygons.</summary>
public sealed class MapTile
{
    public string Name = string.Empty;
    public int Width = 1, Height = 1;
    public bool XTileable, YTileable, UTileable, VTileable, GlobalTextureMapping;
    public float GlobalTextureScale = 1f;
    public float XOffset, YOffset, ZOffset;
    public List<TilePolygon> Polygons = [];

    public MapTile Clone() => new()
    {
        Name = Name, Width = Width, Height = Height, XTileable = XTileable, YTileable = YTileable, UTileable = UTileable, VTileable = VTileable,
        GlobalTextureMapping = GlobalTextureMapping, GlobalTextureScale = GlobalTextureScale, XOffset = XOffset, YOffset = YOffset, ZOffset = ZOffset,
        Polygons = Polygons.Select(polygon => polygon with { Vertices = (TileVertex[])polygon.Vertices.Clone() }).ToList()
    };
}

/// <summary>The decoded pixels of a tileset texture.</summary>
public sealed record TextureImage(int Width, int Height, byte[] Rgba);

/// <summary>A palette of <see cref="MapTile"/>s and the materials (texture images) they use, kept in a folder
/// as a Pokemon DS Map Studio .pdsts file next to its PNG textures, so a tileset can be opened by either program.</summary>
public sealed class MapCreatorTileset
{
    public const string FileExtension = ".pdsts";

    public List<TilesetMaterial> Materials { get; } = [];
    public List<MapTile> Tiles { get; } = [];
    public List<int[][]> SmartGrids { get; } = [];
    public string FolderPath { get; set; }

    private readonly Dictionary<string, TextureImage?> images = new(StringComparer.OrdinalIgnoreCase);

    public MapCreatorTileset(string folderPath) => FolderPath = folderPath;

    public TextureImage? GetImage(int material)
    {
        if (material < 0 || material >= Materials.Count) return null;
        string name = Materials[material].ImageName;
        if (images.TryGetValue(name, out TextureImage? cached)) return cached;
        string path = Path.Combine(FolderPath, name);
        TextureImage? image = null;
        if (File.Exists(path))
        {
            try
            {
                ImageResult decoded = ImageResult.FromMemory(File.ReadAllBytes(path), ColorComponents.RedGreenBlueAlpha);
                image = new TextureImage(decoded.Width, decoded.Height, decoded.Data);
            }
            catch (InvalidOperationException)
            {
            }
        }

        images[name] = image;
        return image;
    }

    /// <summary>The tilesets shipped with a Pokemon DS Map Studio checkout (src/main/resources/tilesets), found by looking
    /// for it beside the program or in a parent folder, as (label like "Platinum / Tileset 7", path).</summary>
    public static IReadOnlyList<(string Label, string Path)> FindBundled()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string root = Path.Combine(directory.FullName, "Pokemon-DS-Map-Studio", "src", "main", "resources", "tilesets");
            if (!Directory.Exists(root)) continue;
            return Directory.EnumerateFiles(root, "*" + FileExtension, SearchOption.AllDirectories)
                .Select(path => (Label: Path.GetRelativePath(root, Path.GetDirectoryName(path)!).Replace(Path.DirectorySeparatorChar, '/').Replace('_', ' ').Replace(" - ", " ... "), Path: path))
                .Select(item => (Label: item.Label.Replace("/", "  /  ").Replace(" ... ", " - "), item.Path))
                .OrderBy(item => item.Label, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return [];
    }

    public void ForgetImages() => images.Clear();

    /// <summary>The material for a texture PNG of the tileset's folder, added when the tileset has none for it yet.</summary>
    public int MaterialForImage(string imageName)
    {
        int index = Materials.FindIndex(material => string.Equals(material.ImageName, imageName, StringComparison.OrdinalIgnoreCase));
        if (index >= 0) return index;
        Materials.Add(TilesetMaterial.ForImage(imageName));
        return Materials.Count - 1;
    }

    /// <summary>Copies the tiles of another tileset into this one, along with the textures they use. Returns the index of the first tile added.</summary>
    public int Merge(MapCreatorTileset other)
    {
        int first = Tiles.Count;
        var materialMap = new Dictionary<int, int>();
        foreach (MapTile source in other.Tiles)
        {
            MapTile tile = source.Clone();
            for (int i = 0; i < tile.Polygons.Count; i++)
            {
                int material = tile.Polygons[i].Material;
                if (!materialMap.TryGetValue(material, out int mapped))
                {
                    mapped = materialMap[material] = AddMaterialFrom(other, material);
                }

                tile.Polygons[i] = tile.Polygons[i] with { Material = mapped };
            }

            Tiles.Add(tile);
        }

        return first;
    }

    private int AddMaterialFrom(MapCreatorTileset other, int material)
    {
        TilesetMaterial source = other.Materials[material];
        string sourcePath = Path.Combine(other.FolderPath, source.ImageName);
        return AddMaterialImage(source, Path.GetFileName(source.ImageName), File.Exists(sourcePath) ? File.ReadAllBytes(sourcePath) : null);
    }

    /// <summary>Adds a material using the given texture file contents and returns its index. The same name with the
    /// same pixels is the same material; a clash with different pixels is renamed.</summary>
    public int AddMaterialImage(TilesetMaterial template, string name, byte[]? png)
    {
        for (int attempt = 0; ; attempt++)
        {
            string candidate = attempt == 0 ? name : $"{Path.GetFileNameWithoutExtension(name)}_{attempt}{Path.GetExtension(name)}";
            string candidatePath = Path.Combine(FolderPath, candidate);
            int existing = Materials.FindIndex(material => string.Equals(material.ImageName, candidate, StringComparison.OrdinalIgnoreCase));
            bool onDisk = File.Exists(candidatePath);
            bool sameFile = onDisk && png is not null && File.ReadAllBytes(candidatePath).AsSpan().SequenceEqual(png);
            if (existing >= 0 && (sameFile || png is null)) return existing;
            if (existing >= 0 || (onDisk && !sameFile && png is not null)) continue;

            Directory.CreateDirectory(FolderPath);
            if (png is not null) File.WriteAllBytes(candidatePath, png);
            images.Remove(candidate);
            TilesetMaterial copy = template.Clone();
            copy.ImageName = candidate;
            string stem = Path.GetFileNameWithoutExtension(candidate);
            copy.MaterialName = stem;
            copy.TextureName = stem;
            copy.PaletteName = stem + "_pl";
            Materials.Add(copy);
            return Materials.Count - 1;
        }
    }

    #region .pdsts reading

    private const byte TagTile = 0, TagImageName = 1, TagPaletteNameImd = 2, TagTextureNameImd = 3, TagMaterialName = 4, TagMaterialStart = 9, TagMaterialEnd = 10;
    private const byte TagWidth = 11, TagHeight = 12, TagXTileable = 13, TagYTileable = 14, TagVCoords = 15, TagTCoords = 16, TagFindQuads = 17, TagFindTris = 18;
    private const byte TagTextureIds = 19, TagObjName = 21, TagNCoords = 22, TagZOffset = 23, TagTexOffsetsQuad = 24, TagTexOffsetsTri = 25, TagSmartGrid = 26;
    private const byte TagGlobalMapping = 27, TagGlobalTexScale = 28, TagFog = 30, TagBothFace = 31, TagNormalOrient = 32, TagAlpha = 33, TagTexGenMode = 34;
    private const byte TagIncludeInImd = 35, TagUTileable = 36, TagVTileable = 37, TagXOffset = 38, TagYOffset = 39, TagTilingU = 40, TagTilingV = 41, TagColorFormat = 42;
    private const byte TagLight0 = 43, TagLight1 = 44, TagLight2 = 45, TagLight3 = 46, TagRenderBorder = 47, TagVertexColors = 48, TagColors = 49;
    private const byte TagFindQuadsExtended = 50, TagFindTrisExtended = 51, TagDiffuse = 53, TagAmbient = 54, TagSpecular = 55, TagEmission = 56;

    private sealed class FaceIndices(bool quad)
    {
        public readonly int[] V = new int[quad ? 4 : 3], T = new int[quad ? 4 : 3], N = new int[quad ? 4 : 3], C = new int[quad ? 4 : 3];
    }

    private sealed class RawTile
    {
        public MapTile Tile = new();
        public List<float> V = [], T = [], N = [], C = [];
        public List<FaceIndices> Quads = [], Tris = [];
        public List<int> TextureIds = [], OffsetsQuad = [], OffsetsTri = [];
    }

    public static MapCreatorTileset Read(string path)
    {
        var tileset = new MapCreatorTileset(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = new BufferedStream(File.OpenRead(path));
        var reader = new TilesetReader(stream);
        TilesetMaterial? material = null;
        RawTile? raw = null;

        void Finish()
        {
            if (raw is not null) tileset.Tiles.Add(Build(raw));
        }

        int tag;
        while ((tag = stream.ReadByte()) != -1)
        {
            switch ((byte)tag)
            {
                case TagMaterialStart: reader.IntElement(); material = new TilesetMaterial(); break;
                case TagMaterialEnd: reader.IntElement(); tileset.Materials.Add(material!); break;
                case TagImageName: material!.ImageName = reader.String(); break;
                case TagMaterialName: material!.MaterialName = reader.String(); break;
                case TagPaletteNameImd: material!.PaletteName = reader.String(); break;
                case TagTextureNameImd: material!.TextureName = reader.String(); break;
                case TagFog: material!.Fog = reader.Bool(); break;
                case TagBothFace: material!.BothFaces = reader.Bool(); break;
                case TagNormalOrient: material!.UniformNormals = reader.Bool(); break;
                case TagIncludeInImd: material!.AlwaysInclude = reader.Bool(); break;
                case TagLight0: material!.Light0 = reader.Bool(); break;
                case TagLight1: material!.Light1 = reader.Bool(); break;
                case TagLight2: material!.Light2 = reader.Bool(); break;
                case TagLight3: material!.Light3 = reader.Bool(); break;
                case TagDiffuse: material!.Diffuse = reader.SignedBytes(); break;
                case TagAmbient: material!.Ambient = reader.SignedBytes(); break;
                case TagSpecular: material!.Specular = reader.SignedBytes(); break;
                case TagEmission: material!.Emission = reader.SignedBytes(); break;
                case TagRenderBorder: material!.RenderBorder = reader.Bool(); break;
                case TagVertexColors: material!.VertexColors = reader.Bool(); break;
                case TagAlpha: material!.Alpha = reader.IntElement(); break;
                case TagTexGenMode: material!.TexGenMode = reader.IntElement(); break;
                case TagTilingU: material!.TilingU = reader.IntElement(); break;
                case TagTilingV: material!.TilingV = reader.IntElement(); break;
                case TagColorFormat: material!.ColorFormat = reader.IntElement(); break;
                case TagSmartGrid: tileset.SmartGrids.Add(reader.IntMatrix()); break;
                case TagTile: Finish(); reader.IntElement(); raw = new RawTile(); break;
                case TagWidth: raw!.Tile.Width = reader.IntElement(); break;
                case TagHeight: raw!.Tile.Height = reader.IntElement(); break;
                case TagXTileable: raw!.Tile.XTileable = reader.Bool(); break;
                case TagYTileable: raw!.Tile.YTileable = reader.Bool(); break;
                case TagUTileable: raw!.Tile.UTileable = reader.Bool(); break;
                case TagVTileable: raw!.Tile.VTileable = reader.Bool(); break;
                case TagGlobalMapping: raw!.Tile.GlobalTextureMapping = reader.Bool(); break;
                case TagGlobalTexScale: raw!.Tile.GlobalTextureScale = reader.FloatElement(); break;
                case TagXOffset: raw!.Tile.XOffset = reader.FloatElement(); break;
                case TagYOffset: raw!.Tile.YOffset = reader.FloatElement(); break;
                case TagZOffset: raw!.Tile.ZOffset = reader.FloatElement(); break;
                case TagVCoords: raw!.V = reader.Floats(); break;
                case TagTCoords: raw!.T = reader.Floats(); break;
                case TagNCoords: raw!.N = reader.Floats(); break;
                case TagColors: raw!.C = reader.Floats(); break;
                case TagFindQuads: raw!.Quads = reader.Faces(true, extended: false); break;
                case TagFindTris: raw!.Tris = reader.Faces(false, extended: false); break;
                case TagFindQuadsExtended: raw!.Quads = reader.Faces(true, extended: true); break;
                case TagFindTrisExtended: raw!.Tris = reader.Faces(false, extended: true); break;
                case TagTextureIds: raw!.TextureIds = reader.Ints(); break;
                case TagTexOffsetsQuad: raw!.OffsetsQuad = reader.Ints(); break;
                case TagTexOffsetsTri: raw!.OffsetsTri = reader.Ints(); break;
                case TagObjName: raw!.Tile.Name = reader.String(); break;
                default: throw new InvalidDataException($"Unknown tag {tag} in the tileset (is this a Pokemon DS Map Studio .pdsts file?).");
            }
        }

        Finish();
        return tileset;
    }

    private static MapTile Build(RawTile raw)
    {
        MapTile tile = raw.Tile;
        if (raw.C.Count == 0) raw.C = [1f, 1f, 1f];

        TileVertex Corner(FaceIndices face, int corner)
        {
            // Indices are 1-based and, for normals and colours, may be absent (0 or less).
            int v = (face.V[corner] - 1) * 3, t = (face.T[corner] - 1) * 2, n = (face.N[corner] - 1) * 3, c = (face.C[corner] - 1) * 3;
            float[] normal = n >= 0 && n + 2 < raw.N.Count ? [raw.N[n], raw.N[n + 1], raw.N[n + 2]] : [0f, 0f, 1f];
            float[] color = c >= 0 && c + 2 < raw.C.Count ? [raw.C[c], raw.C[c + 1], raw.C[c + 2]] : [1f, 1f, 1f];
            if (v < 0 || v + 2 >= raw.V.Count) throw new InvalidDataException($"Tile {tile.Name} refers to a vertex that does not exist.");
            float u = 0f, w = 0f;
            if (t >= 0 && t + 1 < raw.T.Count)
            {
                u = raw.T[t];
                w = raw.T[t + 1];
            }

            return new TileVertex(raw.V[v], raw.V[v + 1], raw.V[v + 2], u, w, normal[0], normal[1], normal[2], color[0], color[1], color[2]);
        }

        void Group(List<FaceIndices> faces, List<int> offsets)
        {
            for (int group = 0; group < raw.TextureIds.Count; group++)
            {
                if (group >= offsets.Count) break;
                int start = offsets[group];
                int end = group < raw.TextureIds.Count - 1 && group + 1 < offsets.Count ? offsets[group + 1] : faces.Count;
                for (int i = start; i < end && i < faces.Count; i++)
                {
                    tile.Polygons.Add(new TilePolygon(raw.TextureIds[group], Enumerable.Range(0, faces[i].V.Length).Select(corner => Corner(faces[i], corner)).ToArray()));
                }
            }
        }

        Group(raw.Quads, raw.OffsetsQuad);
        Group(raw.Tris, raw.OffsetsTri);
        return tile;
    }

    private sealed class TilesetReader(Stream stream)
    {
        public int Int()
        {
            Span<byte> data = stackalloc byte[4];
            stream.ReadExactly(data);
            return BinaryPrimitives.ReadInt32BigEndian(data);
        }

        public float Float() => BitConverter.Int32BitsToSingle(Int());
        public int IntElement() { Int(); return Int(); }
        public float FloatElement() { Int(); return Float(); }
        public bool Bool() { Int(); return Int() == 1; }

        public string String()
        {
            byte[] data = new byte[Int()];
            stream.ReadExactly(data);
            return Encoding.UTF8.GetString(data);
        }

        public int[] SignedBytes()
        {
            byte[] data = new byte[Int()];
            stream.ReadExactly(data);
            return data.Select(value => (int)(sbyte)value).ToArray();
        }

        public List<int> Ints() => Enumerable.Range(0, Count()).Select(_ => Int()).ToList();
        public List<float> Floats() => Enumerable.Range(0, Count()).Select(_ => Float()).ToList();

        public int[][] IntMatrix()
        {
            int rows = Count();
            var matrix = new int[rows][];
            for (int i = 0; i < rows; i++) matrix[i] = Ints().ToArray();
            return matrix;
        }

        public List<FaceIndices> Faces(bool quad, bool extended)
        {
            int count = Count();
            var faces = new List<FaceIndices>(count);
            for (int i = 0; i < count; i++)
            {
                var face = new FaceIndices(quad);
                foreach (int[] array in new[] { face.V, face.T, face.N }) for (int j = 0; j < array.Length; j++) array[j] = Int();
                for (int j = 0; j < face.C.Length; j++) face.C[j] = extended ? Int() : 1;
                faces.Add(face);
            }

            return faces;
        }

        private int Count()
        {
            int count = Int();
            if (count < 0 || count > 50_000_000) throw new InvalidDataException("The tileset file is damaged.");
            return count;
        }
    }

    #endregion

    #region .pdsts writing

    public void Write(string path)
    {
        using var stream = new BufferedStream(File.Create(path));
        var w = new TilesetWriter(stream);

        for (int i = 0; i < Materials.Count; i++)
        {
            TilesetMaterial m = Materials[i];
            w.IntElement(TagMaterialStart, i);
            w.StringElement(TagImageName, m.ImageName);
            w.StringElement(TagMaterialName, m.MaterialName);
            w.StringElement(TagPaletteNameImd, m.PaletteName);
            w.StringElement(TagTextureNameImd, m.TextureName);
            w.BoolElement(TagFog, m.Fog);
            w.BoolElement(TagBothFace, m.BothFaces);
            w.BoolElement(TagNormalOrient, m.UniformNormals);
            w.BoolElement(TagIncludeInImd, m.AlwaysInclude);
            w.BoolElement(TagLight0, m.Light0);
            w.BytesElement(TagDiffuse, m.Diffuse);
            w.BoolElement(TagLight1, m.Light1);
            w.BytesElement(TagAmbient, m.Ambient);
            w.BoolElement(TagLight2, m.Light2);
            w.BytesElement(TagSpecular, m.Specular);
            w.BoolElement(TagLight3, m.Light3);
            w.BytesElement(TagEmission, m.Emission);
            w.BoolElement(TagRenderBorder, m.RenderBorder);
            w.BoolElement(TagVertexColors, m.VertexColors);
            w.IntElement(TagAlpha, m.Alpha);
            w.IntElement(TagTexGenMode, m.TexGenMode);
            w.IntElement(TagTilingU, m.TilingU);
            w.IntElement(TagTilingV, m.TilingV);
            w.IntElement(TagColorFormat, m.ColorFormat);
            w.IntElement(TagMaterialEnd, i);
        }

        if (SmartGrids.Count == 0) SmartGrids.Add(DefaultSmartGrid());
        foreach (int[][] grid in SmartGrids)
        {
            w.Tag(TagSmartGrid);
            w.Int(grid.Length);
            foreach (int[] row in grid)
            {
                w.Int(row.Length);
                foreach (int value in row) w.Int(value);
            }
        }

        for (int i = 0; i < Tiles.Count; i++)
        {
            MapTile tile = Tiles[i];
            w.IntElement(TagTile, i);
            w.IntElement(TagWidth, tile.Width);
            w.IntElement(TagHeight, tile.Height);
            w.BoolElement(TagXTileable, tile.XTileable);
            w.BoolElement(TagYTileable, tile.YTileable);
            w.BoolElement(TagUTileable, tile.UTileable);
            w.BoolElement(TagVTileable, tile.VTileable);
            w.BoolElement(TagGlobalMapping, tile.GlobalTextureMapping);
            w.FloatElement(TagGlobalTexScale, tile.GlobalTextureScale);
            w.FloatElement(TagXOffset, tile.XOffset);
            w.FloatElement(TagYOffset, tile.YOffset);
            w.FloatElement(TagZOffset, tile.ZOffset);
            WriteGeometry(w, tile);
            w.StringElement(TagObjName, tile.Name);
        }
    }

    // Studio's empty smart-drawing grid (5 x 3 slots, none assigned), which it expects at least one of.
    private static int[][] DefaultSmartGrid() => Enumerable.Range(0, 5).Select(_ => Enumerable.Repeat(-1, 3).ToArray()).ToArray();

    private static void WriteGeometry(TilesetWriter w, MapTile tile)
    {
        var positions = new Dictionary<(float, float, float), int>();
        var uvs = new Dictionary<(float, float), int>();
        var normals = new Dictionary<(float, float, float), int>();
        var colors = new Dictionary<(float, float, float), int>();
        var v = new List<float>(); var t = new List<float>(); var n = new List<float>(); var c = new List<float>();

        static int Slot<TKey>(Dictionary<TKey, int> lookup, TKey key, Action add) where TKey : notnull
        {
            if (!lookup.TryGetValue(key, out int index))
            {
                index = lookup.Count;
                lookup[key] = index;
                add();
            }

            return index + 1;
        }

        int[] Indices(TileVertex vertex) =>
        [
            Slot(positions, (vertex.X, vertex.Y, vertex.Z), () => v.AddRange([vertex.X, vertex.Y, vertex.Z])),
            Slot(uvs, (vertex.U, vertex.V), () => t.AddRange([vertex.U, vertex.V])),
            Slot(normals, (vertex.NormalX, vertex.NormalY, vertex.NormalZ), () => n.AddRange([vertex.NormalX, vertex.NormalY, vertex.NormalZ])),
            Slot(colors, (vertex.R, vertex.G, vertex.B), () => c.AddRange([vertex.R, vertex.G, vertex.B]))
        ];

        var materials = tile.Polygons.Select(polygon => polygon.Material).Distinct().ToList();
        var quads = new List<int[][]>(); var tris = new List<int[][]>();
        var offsetsQuad = new List<int>(); var offsetsTri = new List<int>();
        foreach (int material in materials)
        {
            offsetsQuad.Add(quads.Count);
            offsetsTri.Add(tris.Count);
            foreach (TilePolygon polygon in tile.Polygons.Where(polygon => polygon.Material == material))
            {
                (polygon.IsQuad ? quads : tris).Add(polygon.Vertices.Select(Indices).ToArray());
            }
        }

        w.FloatsElement(TagVCoords, v);
        w.FloatsElement(TagTCoords, t);
        w.FloatsElement(TagNCoords, n);
        w.FloatsElement(TagColors, c);
        w.FacesElement(TagFindQuadsExtended, quads);
        w.FacesElement(TagFindTrisExtended, tris);
        w.Tag(TagTextureIds);
        w.Int(materials.Count);
        foreach (int material in materials) w.Int(material);
        w.IntsElement(TagTexOffsetsQuad, offsetsQuad);
        w.IntsElement(TagTexOffsetsTri, offsetsTri);
    }

    private sealed class TilesetWriter(Stream stream)
    {
        public void Tag(byte tag) => stream.WriteByte(tag);

        public void Int(int value)
        {
            Span<byte> data = stackalloc byte[4];
            BinaryPrimitives.WriteInt32BigEndian(data, value);
            stream.Write(data);
        }

        public void Float(float value) => Int(BitConverter.SingleToInt32Bits(value));
        public void IntElement(byte tag, int value) { Tag(tag); Int(1); Int(value); }
        public void BoolElement(byte tag, bool value) => IntElement(tag, value ? 1 : 0);
        public void FloatElement(byte tag, float value) { Tag(tag); Int(1); Float(value); }

        public void StringElement(byte tag, string value)
        {
            byte[] data = Encoding.UTF8.GetBytes(value);
            Tag(tag);
            Int(data.Length);
            stream.Write(data);
        }

        public void BytesElement(byte tag, int[] values)
        {
            Tag(tag);
            Int(values.Length);
            foreach (int value in values) stream.WriteByte((byte)value);
        }

        public void IntsElement(byte tag, List<int> values)
        {
            Tag(tag);
            Int(values.Count);
            foreach (int value in values) Int(value);
        }

        public void FloatsElement(byte tag, List<float> values)
        {
            Tag(tag);
            Int(values.Count);
            foreach (float value in values) Float(value);
        }

        public void FacesElement(byte tag, List<int[][]> faces)
        {
            Tag(tag);
            Int(faces.Count);
            foreach (int[][] face in faces)
            {
                // Per face: all position indices, then texture, then normal, then colour indices.
                for (int kind = 0; kind < 4; kind++)
                {
                    foreach (int[] corner in face) Int(corner[kind]);
                }
            }
        }
    }

    #endregion
}
