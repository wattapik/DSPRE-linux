using System.Globalization;
using System.Text;
using System.Xml;
using StbImageSharp;

namespace DSPRE.Core;

/// <summary>Turns a Wavefront OBJ plus its .mtl textures into a Nitro IMD model, the intermediate file
/// Nintendo's g3dcvtr converts to NSBMD. A port of Pokemon DS Map Studio's ImdModel with its default
/// material settings (16-colour palette textures, uniform normals, no vertex colours); faces stay as
/// quads and triangles rather than being stripped, so models come out larger than Studio's.</summary>
public static class ObjToImd
{
    private const int DefaultPosScale = 6;
    private const int PaletteSize = 16;
    private static readonly float[] UniformNormal = [0f, 0.997f, 0f];

    private sealed class Face(bool quad)
    {
        public bool Quad = quad;
        public float[] Positions = new float[quad ? 12 : 9];
        public float[] Uvs = new float[quad ? 8 : 6];
    }

    private sealed class Material(string name, string? imagePath)
    {
        public string Name = name;
        public string? ImagePath = imagePath;
        public bool Textured => ImagePath is not null;
        public int Diffuse = 25;
        public int TextureIndex = -1;
        public int Width, Height;
        public ImageResult? Source;
        public byte[] Rgba = [];
        public List<Face> Faces = [];
        public bool Transparent;
        public List<(int R, int G, int B, int A)> Palette = [];
        public byte[] TextureData = [];
        public List<(string Type, List<StripVertex> Vertices)>? Primitives;
    }

    /// <param name="MaxTextureSize">Largest width or height a texture keeps; larger ones are scaled down.</param>
    /// <param name="ModelScale">Multiplies the model's coordinates before they are fitted to the DS's range.</param>
    /// <param name="FitModelSize">Whether the importer keeps simplifying until the model, without textures, is under the size the game can load.</param>
    /// <param name="TriangleFraction">Share of the triangles kept; below 1 the model is simplified (and ends up all triangles).</param>
    public sealed record Options(int MaxTextureSize = 512, float ModelScale = 1f, float TriangleFraction = 1f, bool FitModelSize = false);

    public static string Convert(string objPath, Options? options = null)
    {
        options ??= new Options();
        string directory = Path.GetDirectoryName(Path.GetFullPath(objPath))!;
        var positions = new List<float[]>();
        var uvs = new List<float[]>();
        var imageByMaterial = new Dictionary<string, string?>();
        var diffuseByMaterial = new Dictionary<string, int>();
        var materials = new Dictionary<string, Material>();
        Material? current = null;

        foreach (string raw in File.ReadLines(objPath))
        {
            string line = raw.Trim();
            string[] f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            if (f.Length == 0) continue;
            switch (f[0])
            {
                case "mtllib":
                    ReadMtl(Path.Combine(directory, line[6..].Trim()), directory, imageByMaterial, diffuseByMaterial);
                    break;
                case "usemtl":
                    current = UseMaterial(materials, imageByMaterial, diffuseByMaterial, line[6..].Trim());
                    break;
                case "v" when f.Length >= 4:
                    positions.Add([P(f[1]), P(f[2]), P(f[3])]);
                    break;
                case "vt" when f.Length >= 3:
                    uvs.Add([P(f[1]), P(f[2])]);
                    break;
                case "f" when f.Length >= 4:
                    current ??= UseMaterial(materials, imageByMaterial, diffuseByMaterial, string.Empty);
                    AddFace(current, f.Skip(1).ToArray(), positions, uvs);
                    break;
            }
        }

        List<Material> used = materials.Values.Where(material => material.Faces.Count > 0).ToList();
        if (used.Count == 0) throw new InvalidDataException("The OBJ contains no faces.");

        if (options.TriangleFraction < 1f)
        {
            foreach (Material material in used) Simplify(material, options.TriangleFraction);
        }

        List<Material> textured = used.Where(material => material.Textured).ToList();
        for (int i = 0; i < textured.Count; i++) textured[i].TextureIndex = i;
        foreach (Material material in textured) LoadTexture(material, Math.Clamp(options.MaxTextureSize, 8, 1024));
        FitTextureBudget(textured);

        float[] min = [float.MaxValue, float.MaxValue, float.MaxValue], max = [float.MinValue, float.MinValue, float.MinValue];
        foreach (Face face in used.SelectMany(material => material.Faces))
        {
            Rotate(face.Positions);
            for (int i = 0; i < face.Positions.Length; i++) face.Positions[i] *= options.ModelScale;
            for (int i = 0; i < face.Positions.Length; i++)
            {
                min[i % 3] = Math.Min(min[i % 3], face.Positions[i]);
                max[i % 3] = Math.Max(max[i % 3], face.Positions[i]);
            }
        }

        int posScale = PosScale(min, max);
        float scaleFactor = 0.25f * MathF.Pow(2, DefaultPosScale - posScale);
        foreach (Material material in used)
        {
            foreach (Face face in material.Faces)
            {
                for (int i = 0; i < face.Positions.Length; i++) face.Positions[i] *= scaleFactor;
                if (!material.Textured) continue;
                FixTextureCoordinates(face.Uvs, material.Width, material.Height);
                for (int i = 0; i < face.Uvs.Length; i += 2)
                {
                    face.Uvs[i] *= material.Width;
                    face.Uvs[i + 1] *= -material.Height;
                }
            }
        }

        float[] boxStart = [min[0], min[1], min[2]];
        float[] boxSize = [max[0] - min[0], max[1] - min[1], max[2] - min[2]];
        int boxScale = PosScale(boxStart, boxSize);
        float boxFactor = 0.25f * MathF.Pow(2, DefaultPosScale - boxScale);
        for (int i = 0; i < 3; i++)
        {
            boxStart[i] *= boxFactor;
            boxSize[i] *= boxFactor;
        }

        foreach (Material material in textured) Quantize(material);

        int quadTotal = used.Sum(material => material.Faces.Count(face => face.Quad));
        int triangleTotal = used.Sum(material => material.Faces.Count(face => !face.Quad));
        int vertexTotal = used.Sum(material => (material.Primitives ??= BuildPrimitives(material)).Sum(primitive => primitive.Vertices.Count));

        var text = new StringWriter(CultureInfo.InvariantCulture);
        using (var xml = XmlWriter.Create(text, new XmlWriterSettings { Indent = true, IndentChars = "    ", Encoding = new UTF8Encoding(false) }))
        {
            xml.WriteStartDocument();
            xml.WriteStartElement("imd");
            xml.WriteAttributeString("version", "1.6.0");

            xml.WriteStartElement("head");
            Element(xml, "create", ("user", "unknown"), ("host", "unknown"), ("date", "2019-01-11T13:52:52"), ("source", "untitled"));
            Element(xml, "title", text: "Model Data for NINTENDO NITRO-System");
            Element(xml, "generator", ("name", "DSPRE"), ("version", "1.0"));
            xml.WriteEndElement();

            xml.WriteStartElement("body");
            Element(xml, "original_create", ("user", "unknown"), ("host", "unknown"), ("date", "2019-01-11T13:52:52"), ("source", "untitled"));
            Element(xml, "original_generator", ("name", "DSPRE"), ("version", "1.0"));
            Element(xml, "model_info", ("pos_scale", posScale), ("scaling_rule", "standard"), ("vertex_style", "direct"), ("magnify", 1),
                ("tool_start_frame", 1), ("tex_matrix_mode", "maya"), ("compress_node", "merge"), ("node_size", "1 1"),
                ("compress_material", "on"), ("material_size", $"{used.Count} {used.Count}"), ("output_texture", "used"),
                ("force_full_weight", "off"), ("use_primitive_strip", "on"));
            Element(xml, "box_test", ("pos_scale", boxScale), ("xyz", Floats(boxStart)), ("whd", Floats(boxSize)));

            // g3dcvtr rejects empty texture arrays, so a model with no textures has none.
            if (textured.Count > 0)
            {
                xml.WriteStartElement("tex_image_array");
                xml.WriteAttributeString("size", textured.Count.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < textured.Count; i++)
                {
                    Material m = textured[i];
                    xml.WriteStartElement("tex_image");
                    Attributes(xml, ("index", i), ("name", m.Name), ("width", m.Width), ("height", m.Height), ("original_width", m.Width),
                        ("original_height", m.Height), ("format", "palette16"), ("color0_mode", m.Transparent ? "transparency" : "color"),
                        ("palette_name", m.Name + "_pl"), ("path", ""));
                    xml.WriteStartElement("bitmap");
                    xml.WriteAttributeString("size", (m.TextureData.Length / 2).ToString(CultureInfo.InvariantCulture));
                    xml.WriteString(Hex(m.TextureData));
                    xml.WriteEndElement();
                    xml.WriteEndElement();
                }
                xml.WriteEndElement();

                xml.WriteStartElement("tex_palette_array");
                xml.WriteAttributeString("size", textured.Count.ToString(CultureInfo.InvariantCulture));
                for (int i = 0; i < textured.Count; i++)
                {
                    byte[] palette = PaletteBytes(textured[i].Palette);
                    xml.WriteStartElement("tex_palette");
                    Attributes(xml, ("index", i), ("name", textured[i].Name + "_pl"), ("color_size", palette.Length / 2));
                    xml.WriteString(Hex(palette));
                    xml.WriteEndElement();
                }
                xml.WriteEndElement();
            }

            xml.WriteStartElement("material_array");
            xml.WriteAttributeString("size", used.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < used.Count; i++)
            {
                Element(xml, "material", ("index", i), ("name", used[i].Name), ("light0", "on"), ("light1", "off"), ("light2", "off"),
                    ("light3", "off"), ("face", "front"), ("alpha", 31), ("wire_mode", "off"), ("polygon_mode", "modulate"),
                    ("polygon_id", 0), ("fog_flag", "on"), ("depth_test_decal", "off"), ("translucent_update_depth", "off"),
                    ("render_1_pixel", "off"), ("far_clipping", "off"), ("diffuse", $"{used[i].Diffuse} {used[i].Diffuse} {used[i].Diffuse}"), ("ambient", "31 31 31"),
                    ("specular", "0 0 0"), ("emission", "0 0 0"), ("shininess_table_flag", "off"), ("tex_image_idx", used[i].TextureIndex),
                    ("tex_palette_idx", used[i].TextureIndex), ("tex_tiling", "repeat repeat"), ("tex_scale", "1.000000 1.000000"),
                    ("tex_rotate", "0.000000"), ("tex_translate", "0.000000 0.000000"), ("tex_gen_mode", "none"));
            }
            xml.WriteEndElement();

            xml.WriteStartElement("matrix_array");
            xml.WriteAttributeString("size", "1");
            Element(xml, "matrix", ("index", 0), ("mtx_weight", 1), ("node_idx", 0));
            xml.WriteEndElement();

            xml.WriteStartElement("polygon_array");
            xml.WriteAttributeString("size", used.Count.ToString(CultureInfo.InvariantCulture));
            for (int i = 0; i < used.Count; i++) WritePolygon(xml, i, used[i]);
            xml.WriteEndElement();

            xml.WriteStartElement("node_array");
            xml.WriteAttributeString("size", "1");
            xml.WriteStartElement("node");
            Attributes(xml, ("index", 0), ("name", "world_root"), ("kind", "mesh"), ("parent", -1), ("child", -1), ("brother_next", -1),
                ("brother_prev", -1), ("draw_mtx", "on"), ("billboard", "off"), ("scale", "1.000000 1.000000 1.000000"),
                ("rotate", "0.000000 0.000000 0.000000"), ("translate", "0.000000 0.000000 0.000000"), ("visibility", "on"),
                ("display_size", used.Count), ("vertex_size", vertexTotal), ("polygon_size", quadTotal + triangleTotal),
                ("triangle_size", triangleTotal), ("quad_size", quadTotal), ("volume_min", "0.000000 0.000000 0.000000"),
                ("volume_max", "0.000000 0.000000 0.000000"), ("volume_r", "0.000000"));
            for (int i = 0; i < used.Count; i++) Element(xml, "display", ("index", i), ("material", i), ("polygon", i), ("priority", 0));
            xml.WriteEndElement();
            xml.WriteEndElement();

            Element(xml, "output_info", ("vertex_size", vertexTotal), ("polygon_size", quadTotal + triangleTotal),
                ("triangle_size", triangleTotal), ("quad_size", quadTotal));
            xml.WriteEndElement();
            xml.WriteEndElement();
        }

        return text.ToString().Replace("encoding=\"utf-16\"", "encoding=\"UTF-8\"");
    }

    private sealed record StripVertex(float[] Position, float[] Uv);

    private static string VertexKey(float[] position, float[] uv) =>
        string.Create(CultureInfo.InvariantCulture, $"{MathF.Round(position[0] * 4096f)},{MathF.Round(position[1] * 4096f)},{MathF.Round(position[2] * 4096f)},{MathF.Round(uv[0] * 64f)},{MathF.Round(uv[1] * 64f)}");

    /// <summary>Chains faces that share an edge (and the texture coordinates along it) into strips, as Studio does, so a shared
    /// corner is sent once; what cannot be chained stays a plain list. Each result is a primitive type and its corners in order.</summary>
    private static List<(string Type, List<StripVertex> Vertices)> BuildPrimitives(Material material)
    {
        var primitives = new List<(string, List<StripVertex>)>();
        foreach (bool quad in new[] { true, false })
        {
            List<Face> faces = material.Faces.Where(face => face.Quad == quad).ToList();
            if (faces.Count == 0) continue;
            int corners = quad ? 4 : 3;
            StripVertex Corner(Face face, int i) => new([face.Positions[i * 3], face.Positions[i * 3 + 1], face.Positions[i * 3 + 2]], [face.Uvs[i * 2], face.Uvs[i * 2 + 1]]);

            // Directed edge (from, to) -> faces holding it and the corner it starts at.
            var byEdge = new Dictionary<(string, string), List<(int Face, int Corner)>>();
            for (int f = 0; f < faces.Count; f++)
            {
                for (int c = 0; c < corners; c++)
                {
                    StripVertex from = Corner(faces[f], c), to = Corner(faces[f], (c + 1) % corners);
                    var key = (VertexKey(from.Position, from.Uv), VertexKey(to.Position, to.Uv));
                    if (!byEdge.TryGetValue(key, out List<(int, int)>? list)) byEdge[key] = list = [];
                    list.Add((f, c));
                }
            }

            var used = new bool[faces.Count];
            var plain = new List<StripVertex>();
            (int Face, int Corner)? Find(StripVertex from, StripVertex to)
            {
                if (!byEdge.TryGetValue((VertexKey(from.Position, from.Uv), VertexKey(to.Position, to.Uv)), out List<(int Face, int Corner)>? list)) return null;
                foreach ((int face, int corner) in list) if (!used[face]) return (face, corner);
                return null;
            }

            for (int start = 0; start < faces.Count; start++)
            {
                if (used[start]) continue;
                used[start] = true;
                var strip = new List<StripVertex>();
                int length = 1;
                if (quad)
                {
                    // Corners 0, 1, 3, 2: the strip's pairs, with the quad drawn as (s0, s1, s3, s2).
                    strip.AddRange([Corner(faces[start], 0), Corner(faces[start], 1), Corner(faces[start], 3), Corner(faces[start], 2)]);
                    while (Find(strip[^2], strip[^1]) is { } next)
                    {
                        used[next.Face] = true;
                        // The neighbour, turned to start at the shared edge, is (s2, s3, a, b); the strip goes on with b, a.
                        strip.Add(Corner(faces[next.Face], (next.Corner + 3) % 4));
                        strip.Add(Corner(faces[next.Face], (next.Corner + 2) % 4));
                        length++;
                    }
                }
                else
                {
                    strip.AddRange([Corner(faces[start], 0), Corner(faces[start], 1), Corner(faces[start], 2)]);
                    while (true)
                    {
                        // After triangle k (k + 3 corners so far) the neighbour holds the edge from s(k+2) to s(k+1) when k is even, the reverse when odd.
                        int k = length - 1;
                        StripVertex x = k % 2 == 0 ? strip[k + 2] : strip[k + 1], y = k % 2 == 0 ? strip[k + 1] : strip[k + 2];
                        if (Find(x, y) is not { } next) break;
                        used[next.Face] = true;
                        strip.Add(Corner(faces[next.Face], (next.Corner + 2) % 3));
                        length++;
                    }
                }

                if (length == 1)
                {
                    // A lone face is cheaper in the plain list.
                    for (int c = 0; c < corners; c++) plain.Add(Corner(faces[start], c));
                }
                else
                {
                    primitives.Add((quad ? "quad_strip" : "triangle_strip", strip));
                }
            }

            if (plain.Count > 0) primitives.Insert(0, (quad ? "quads" : "triangles", plain));
        }

        return primitives;
    }

    private static void WritePolygon(XmlWriter xml, int index, Material material)
    {
        List<(string Type, List<StripVertex> Vertices)> primitives = material.Primitives ??= BuildPrimitives(material);
        int quads = material.Faces.Count(face => face.Quad), triangles = material.Faces.Count - quads;
        int vertices = primitives.Sum(primitive => primitive.Vertices.Count);
        xml.WriteStartElement("polygon");
        Attributes(xml, ("index", index), ("name", "polygon" + index), ("vertex_size", vertices), ("polygon_size", quads + triangles),
            ("triangle_size", triangles), ("quad_size", quads), ("volume_min", "3.000000 3.000000 3.000000"),
            ("volume_max", "3.000000 3.000000 3.000000"), ("volume_r", "3.000000"), ("mtx_prim_size", 1), ("nrm_flag", "on"),
            ("clr_flag", "off"), ("tex_flag", material.Textured ? "on" : "off"));
        xml.WriteStartElement("mtx_prim");
        xml.WriteAttributeString("index", "0");
        xml.WriteStartElement("mtx_list");
        xml.WriteAttributeString("size", "1");
        xml.WriteString("0");
        xml.WriteEndElement();

        xml.WriteStartElement("primitive_array");
        xml.WriteAttributeString("size", primitives.Count.ToString(CultureInfo.InvariantCulture));
        float[] lastNormal = [0f, 0f, 0f];
        int primitive = 0;
        foreach ((string type, List<StripVertex> list) in primitives)
        {
            xml.WriteStartElement("primitive");
            Attributes(xml, ("index", primitive), ("type", type), ("vertex_size", list.Count));
            if (primitive == 0) Element(xml, "mtx", ("idx", 0));
            int group = type == "quads" ? 4 : type == "triangles" ? 3 : int.MaxValue;
            float[]? previous = null;
            for (int v = 0; v < list.Count; v++)
            {
                // In a plain list every face starts afresh; a strip runs on from corner to corner.
                if (v % group == 0) previous = null;
                WriteVertex(xml, list[v], material.Textured, ref lastNormal, ref previous);
            }

            xml.WriteEndElement();
            primitive++;
        }

        xml.WriteEndElement();
        xml.WriteEndElement();
        xml.WriteEndElement();
    }

    private static void WriteVertex(XmlWriter xml, StripVertex vertex, bool textured, ref float[] lastNormal, ref float[]? previous)
    {
        if (textured) Element(xml, "tex", ("st", Floats([vertex.Uv[0], vertex.Uv[1]])));
        if (!SameNormal(lastNormal, UniformNormal))
        {
            Element(xml, "nrm", ("xyz", Floats(UniformNormal)));
            lastNormal = UniformNormal;
        }

        float[] p = vertex.Position;
        if (previous is null) Element(xml, "pos_xyz", ("xyz", Floats(p)));
        else if (Same(previous[0], p[0])) Element(xml, "pos_yz", ("yz", Floats([p[1], p[2]])));
        else if (Same(previous[1], p[1])) Element(xml, "pos_xz", ("xz", Floats([p[0], p[2]])));
        else if (Same(previous[2], p[2])) Element(xml, "pos_xy", ("xy", Floats([p[0], p[1]])));
        else if (Math.Abs(previous[0] - p[0]) < 0.124f && Math.Abs(previous[1] - p[1]) < 0.124f && Math.Abs(previous[2] - p[2]) < 0.124f)
        {
            Element(xml, "pos_diff", ("xyz", Floats([p[0] - previous[0], p[1] - previous[1], p[2] - previous[2]])));
        }
        else Element(xml, "pos_xyz", ("xyz", Floats(p)));
        previous = p;
    }

    private static bool Same(float a, float b) => Math.Abs(a - b) < 0.0001f;

    private static bool SameNormal(float[] a, float[] b) => a.Zip(b).All(pair => Math.Abs(pair.First - pair.Second) <= 0.001f);

    private static float P(string text) => float.Parse(text, CultureInfo.InvariantCulture);

    // A material with no map_Kd is left untextured, drawn in its diffuse (Kd) colour.
    private static Material UseMaterial(Dictionary<string, Material> materials, Dictionary<string, string?> imageByMaterial, Dictionary<string, int> diffuseByMaterial, string name)
    {
        imageByMaterial.TryGetValue(name, out string? image);
        string key = image ?? "untextured:" + name;
        if (!materials.TryGetValue(key, out Material? material))
        {
            materials[key] = material = new Material(image is null ? (name.Length > 0 ? name : "untextured") : Path.GetFileNameWithoutExtension(image), image);
            if (image is null && diffuseByMaterial.TryGetValue(name, out int diffuse)) material.Diffuse = diffuse;
        }

        return material;
    }

    private static void ReadMtl(string path, string directory, Dictionary<string, string?> imageByMaterial, Dictionary<string, int> diffuseByMaterial)
    {
        if (!File.Exists(path)) throw new FileNotFoundException("The .mtl file named by the OBJ was not found.", path);
        string? name = null;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith("newmtl ", StringComparison.Ordinal))
            {
                name = line[7..].Trim();
                imageByMaterial[name] = null;
            }
            else if (name is not null && line.StartsWith("map_Kd ", StringComparison.Ordinal))
            {
                string image = Path.Combine(directory, line[7..].Trim().Replace('\\', '/'));
                if (!File.Exists(image)) throw new FileNotFoundException($"Texture \"{Path.GetFileName(image)}\" is not next to the OBJ.", image);
                imageByMaterial[name] = image;
            }
            else if (name is not null && line.StartsWith("Kd ", StringComparison.Ordinal))
            {
                string[] channels = line[3..].Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
                if (channels.Length >= 3) diffuseByMaterial[name] = (int)Math.Round(Math.Clamp((P(channels[0]) + P(channels[1]) + P(channels[2])) / 3f, 0f, 1f) * 31f);
            }
        }
    }

    private static void AddFace(Material material, string[] corners, List<float[]> positions, List<float[]> uvs)
    {
        (float[] P, float[] T) Corner(string token)
        {
            string[] parts = token.Split('/');
            int v = Index(parts[0], positions.Count);
            if (parts.Length < 2 || parts[1].Length == 0)
            {
                if (material.Textured) throw new InvalidDataException("The OBJ has textured faces without texture coordinates.");
                return (positions[v], [0f, 0f]);
            }

            return (positions[v], uvs[Index(parts[1], uvs.Count)]);
        }

        // Anything over four corners is fanned into triangles.
        List<int[]> shapes = corners.Length == 4 ? [[0, 1, 2, 3]] : Enumerable.Range(1, corners.Length - 2).Select(i => new[] { 0, i, i + 1 }).ToList();
        foreach (int[] shape in shapes)
        {
            var face = new Face(shape.Length == 4);
            for (int i = 0; i < shape.Length; i++)
            {
                (float[] p, float[] t) = Corner(corners[shape[i]]);
                Array.Copy(p, 0, face.Positions, i * 3, 3);
                Array.Copy(t, 0, face.Uvs, i * 2, 2);
            }

            material.Faces.Add(face);
        }
    }

    private static int Index(string token, int count)
    {
        int index = int.Parse(token, CultureInfo.InvariantCulture);
        int resolved = index < 0 ? count + index : index - 1;
        if (resolved < 0 || resolved >= count) throw new InvalidDataException($"OBJ index {index} is out of range.");
        return resolved;
    }

    private static void Rotate(float[] coordinates)
    {
        for (int i = 0; i < coordinates.Length; i += 3)
        {
            float y = coordinates[i + 1];
            coordinates[i + 1] = coordinates[i + 2];
            coordinates[i + 2] = -y;
        }
    }

    private static void Simplify(Material material, float fraction)
    {
        var triangles = new List<Face>();
        foreach (Face face in material.Faces)
        {
            if (!face.Quad) triangles.Add(face);
            else foreach (int[] corners in new[] { new[] { 0, 1, 2 }, new[] { 0, 2, 3 } })
            {
                var triangle = new Face(false);
                for (int i = 0; i < 3; i++)
                {
                    Array.Copy(face.Positions, corners[i] * 3, triangle.Positions, i * 3, 3);
                    Array.Copy(face.Uvs, corners[i] * 2, triangle.Uvs, i * 2, 2);
                }

                triangles.Add(triangle);
            }
        }

        float[] positions = triangles.SelectMany(face => face.Positions).ToArray();
        float[] uvs = triangles.SelectMany(face => face.Uvs).ToArray();
        (positions, uvs) = MeshDecimator.Reduce(positions, uvs, Math.Max(1, (int)(triangles.Count * fraction)));
        material.Faces = Enumerable.Range(0, positions.Length / 9).Select(t =>
        {
            var face = new Face(false);
            Array.Copy(positions, t * 9, face.Positions, 0, 9);
            Array.Copy(uvs, t * 6, face.Uvs, 0, 6);
            return face;
        }).ToList();
    }

    private static int PosScale(float[] a, float[] b)
    {
        float largest = Math.Max(a.Max(Math.Abs), b.Max(Math.Abs)) * 0.25f * (1 << DefaultPosScale);
        double scale = Math.Ceiling(Math.Log2(largest / 7.9f));
        return double.IsFinite(scale) ? Math.Clamp((int)scale, 0, 31) : DefaultPosScale;
    }

    // DS texture coordinates are 16-bit fixed point, so each polygon's are shifted by whole texture
    // repeats towards zero and clamped, as Studio does.
    private static void FixTextureCoordinates(float[] uvs, int width, int height)
    {
        float minU = float.MaxValue, minV = float.MaxValue, maxU = float.MinValue, maxV = float.MinValue;
        for (int i = 0; i < uvs.Length; i += 2)
        {
            minU = Math.Min(minU, uvs[i]);
            maxU = Math.Max(maxU, uvs[i]);
            minV = Math.Min(minV, uvs[i + 1]);
            maxV = Math.Max(maxV, uvs[i + 1]);
        }

        float deltaU = MathF.Floor(-((maxU + minU) / 2) / width + 0.5f) * width;
        float deltaV = MathF.Floor(-((maxV + minV) / 2) / height + 0.5f) * height;
        for (int i = 0; i < uvs.Length; i += 2)
        {
            uvs[i] = Math.Clamp(uvs[i] + deltaU, -2030f / width, 2030f / width);
            uvs[i + 1] = Math.Clamp(uvs[i + 1] + deltaV, -2030f / height, 2030f / height);
        }
    }

    // g3dcvtr stores the textures' total size in 16 bits of 8-byte units, so at 4 bits a pixel they must
    // stay under 512 KiB together (and the DS has only that much texture memory); the margin leaves room
    // for the map's own textures in the same bank.
    private const int TextureBudgetBytes = 256 * 1024;

    private static void LoadTexture(Material material, int maxSize)
    {
        material.Source = ImageResult.FromMemory(File.ReadAllBytes(material.ImagePath), ColorComponents.RedGreenBlueAlpha);
        material.Width = Math.Min(NearestPowerOfTwo(material.Source.Width), maxSize);
        material.Height = Math.Min(NearestPowerOfTwo(material.Source.Height), maxSize);
    }

    private static void FitTextureBudget(List<Material> materials)
    {
        while (materials.Sum(material => material.Width * material.Height / 2) > TextureBudgetBytes)
        {
            Material largest = materials.OrderByDescending(material => material.Width * material.Height).First();
            if (largest.Width * largest.Height <= 64) break;
            if (largest.Width >= largest.Height) largest.Width /= 2;
            else largest.Height /= 2;
        }

        foreach (Material material in materials)
        {
            ImageResult image = material.Source!;
            material.Rgba = new byte[material.Width * material.Height * 4];
            for (int y = 0; y < material.Height; y++)
            {
                for (int x = 0; x < material.Width; x++)
                {
                    int source = ((y * image.Height / material.Height) * image.Width + x * image.Width / material.Width) * 4;
                    Array.Copy(image.Data, source, material.Rgba, (y * material.Width + x) * 4, 4);
                }
            }
        }
    }

    internal static int NearestPowerOfTwo(int size) => Math.Clamp(1 << (int)Math.Round(Math.Log2(Math.Max(size, 1))), 8, 1024);

    private static (int R, int G, int B, int A) DsColor(byte[] rgba, int offset)
    {
        int a = rgba[offset + 3] / 8 * 8;
        return a < 20 ? (0, 0, 0, a) : (rgba[offset] / 8 * 8, rgba[offset + 1] / 8 * 8, rgba[offset + 2] / 8 * 8, a);
    }

    private static void Quantize(Material material)
    {
        var counts = new Dictionary<(int, int, int, int), int>();
        for (int i = 0; i < material.Rgba.Length; i += 4)
        {
            (int, int, int, int) color = DsColor(material.Rgba, i);
            counts[color] = counts.GetValueOrDefault(color) + 1;
        }

        List<(int R, int G, int B, int A)> palette = counts.Count > PaletteSize
            ? MedianCut(counts.Select(pair => (pair.Key, pair.Value)).ToList(), PaletteSize)
            : counts.Keys.Select(key => (key.Item1, key.Item2, key.Item3, key.Item4)).ToList();
        while (palette.Count < PaletteSize) palette.Add((0, 0, 0, 255));

        int transparent = palette.FindIndex(color => color.A < 20);
        material.Transparent = material.Rgba.Where((_, i) => i % 4 == 3).Any(alpha => alpha / 8 * 8 < 20);
        if (transparent > 0) (palette[0], palette[transparent]) = (palette[transparent], palette[0]);
        material.Palette = palette;

        var indices = new int[material.Width * material.Height];
        var cache = new Dictionary<(int, int, int, int), int>();
        for (int i = 0; i < indices.Length; i++)
        {
            (int R, int G, int B, int A) color = DsColor(material.Rgba, i * 4);
            if (!cache.TryGetValue(color, out int best))
            {
                int bestDistance = int.MaxValue;
                for (int p = 0; p < palette.Count; p++)
                {
                    int distance = Square(color.R - palette[p].R) + Square(color.G - palette[p].G) + Square(color.B - palette[p].B) + Square(color.A - palette[p].A);
                    if (distance < bestDistance) (best, bestDistance) = (p, distance);
                }

                cache[color] = best;
            }

            indices[i] = best;
        }

        // 4 bits per pixel, low nibble first, with each pair of bytes swapped as the IMD format has it.
        byte[] data = new byte[indices.Length / 2];
        for (int i = 0; i < data.Length; i++)
        {
            data[i + 1 - (i % 2) * 2] = (byte)((indices[i * 2] & 0xF) | ((indices[i * 2 + 1] & 0xF) << 4));
        }

        material.TextureData = data;
    }

    private static int Square(int value) => value * value;

    private static List<(int R, int G, int B, int A)> MedianCut(List<((int R, int G, int B, int A) Color, int Count)> colors, int target)
    {
        var boxes = new List<List<((int R, int G, int B, int A) Color, int Count)>> { colors };
        while (boxes.Count < target)
        {
            List<((int R, int G, int B, int A) Color, int Count)>? widest = null;
            int widestRange = 0, widestChannel = 0;
            foreach (var box in boxes.Where(box => box.Count > 1))
            {
                for (int channel = 0; channel < 4; channel++)
                {
                    int range = box.Max(entry => Channel(entry.Color, channel)) - box.Min(entry => Channel(entry.Color, channel));
                    if (range > widestRange) (widest, widestRange, widestChannel) = (box, range, channel);
                }
            }

            if (widest is null) break;
            var sorted = widest.OrderBy(entry => Channel(entry.Color, widestChannel)).ToList();
            int half = sorted.Sum(entry => entry.Count) / 2, running = 0, split = 1;
            for (int i = 0; i < sorted.Count - 1; i++)
            {
                running += sorted[i].Count;
                split = i + 1;
                if (running >= half) break;
            }

            boxes.Remove(widest);
            boxes.Add(sorted.Take(split).ToList());
            boxes.Add(sorted.Skip(split).ToList());
        }

        return boxes.Select(box =>
        {
            long total = box.Sum(entry => (long)entry.Count);
            int Average(int channel) => (int)(box.Sum(entry => (long)Channel(entry.Color, channel) * entry.Count) / total) / 8 * 8;
            return (Average(0), Average(1), Average(2), Average(3));
        }).ToList();
    }

    private static int Channel((int R, int G, int B, int A) color, int channel) => channel switch { 0 => color.R, 1 => color.G, 2 => color.B, _ => color.A };

    private static byte[] PaletteBytes(List<(int R, int G, int B, int A)> palette)
    {
        byte[] data = new byte[palette.Count * 2];
        for (int i = 0; i < palette.Count; i++)
        {
            (int r, int g, int b, _) = palette[i];
            data[i * 2] = (byte)(((b >> 3) << 2) | ((g >> 6) & 3));
            data[i * 2 + 1] = (byte)((((g >> 3) << 5) & 0xE0) | (r >> 3));
        }

        return data;
    }

    private static string Hex(byte[] data)
    {
        var text = new StringBuilder();
        for (int i = 0; i < data.Length; i++)
        {
            if (i % 2 == 0) text.Append(' ');
            text.Append(data[i].ToString("x2", CultureInfo.InvariantCulture));
        }

        return text.ToString();
    }

    private static string Floats(float[] values) =>
        string.Join(' ', values.Select(value => (MathF.Floor(value * 100000f + 0.5f) / 100000f).ToString("F6", CultureInfo.InvariantCulture)));

    private static void Attributes(XmlWriter xml, params (string Name, object Value)[] attributes)
    {
        foreach ((string name, object value) in attributes) xml.WriteAttributeString(name, System.Convert.ToString(value, CultureInfo.InvariantCulture));
    }

    private static void Element(XmlWriter xml, string name, params (string Name, object Value)[] attributes) => Element(xml, name, null, attributes);

    private static void Element(XmlWriter xml, string name, string? text = null, params (string Name, object Value)[] attributes)
    {
        xml.WriteStartElement(name);
        Attributes(xml, attributes);
        if (text is not null) xml.WriteString(text);
        xml.WriteEndElement();
    }
}
