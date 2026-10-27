using System.Globalization;
using System.Text;
using System.Text.Json;

namespace DSPRE.Core;

/// <summary>Reads a Wavefront OBJ (what Pokemon DS Map Studio exports maps and tiles as) and writes it as a
/// binary glTF the map viewer can load. Faces are fan-triangulated and shaded by a fixed light so the shape
/// reads without textures; each material's diffuse map (map_Kd in the .mtl next to the OBJ, PNG or JPEG) is
/// embedded when the image file is found.</summary>
public static class ObjModelConverter
{
    public sealed record Result(byte[] Glb, int Vertices, int Triangles);

    public static Result ToGlb(string objPath, bool zUp = false, float scale = 1f) => ToGlb(File.ReadLines(objPath), Path.GetDirectoryName(Path.GetFullPath(objPath)), zUp, scale);

    /// <param name="directory">Where the .mtl and its images are looked for; null loads no textures.</param>
    /// <param name="zUp">The OBJ is z-up with north along +y, as Studio's and what <see cref="ObjToImd"/> expects; it is turned to glTF's y-up, north -z.</param>
    /// <param name="scale">Multiplies positions (Studio's map is one unit per cell; the game's is 16).</param>
    public static Result ToGlb(IEnumerable<string> lines, string? directory = null, bool zUp = false, float scale = 1f)
    {
        var positions = new List<(float X, float Y, float Z)>();
        var uvs = new List<(float U, float V)>();
        var normals = new List<(float X, float Y, float Z)>();
        var vertexLookup = new Dictionary<(int P, int T, int N), uint>();
        var outPositions = new List<float>();
        var outUvs = new List<float>();
        var outNormals = new List<(float X, float Y, float Z)>();
        var indicesByMaterial = new Dictionary<string, List<uint>> { [""] = [] };
        var materialImages = new Dictionary<string, string>();
        var indices = indicesByMaterial[""];

        static float F(string text) => float.Parse(text, CultureInfo.InvariantCulture);

        uint Vertex(string token)
        {
            string[] parts = token.Split('/');
            int p = Resolve(parts[0], positions.Count);
            int t = parts.Length > 1 && parts[1].Length > 0 ? Resolve(parts[1], uvs.Count) : -1;
            int n = parts.Length > 2 && parts[2].Length > 0 ? Resolve(parts[2], normals.Count) : -1;
            if (vertexLookup.TryGetValue((p, t, n), out uint existing)) return existing;
            (float x, float y, float z) = positions[p];
            outPositions.AddRange([x, y, z]);
            (float u, float v) = t >= 0 ? uvs[t] : (0f, 0f);
            // OBJ's V runs bottom-up, glTF's top-down.
            outUvs.AddRange([u, 1f - v]);
            outNormals.Add(n >= 0 ? normals[n] : (0f, 0f, 0f));
            uint id = (uint)(outPositions.Count / 3 - 1);
            vertexLookup[(p, t, n)] = id;
            return id;
        }

        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            string[] f = line.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
            switch (f[0])
            {
                case "mtllib" when f.Length >= 2 && directory is not null:
                    foreach ((string name, string image) in ReadMaterialImages(directory, line[6..].Trim())) materialImages[name] = image;
                    break;
                case "usemtl" when f.Length >= 2:
                    if (!indicesByMaterial.TryGetValue(f[1], out indices!)) indicesByMaterial[f[1]] = indices = [];
                    break;
                case "v" when f.Length >= 4:
                    (float vx, float vy, float vz) = (F(f[1]) * scale, F(f[2]) * scale, F(f[3]) * scale);
                    positions.Add(zUp ? (vx, vz, -vy) : (vx, vy, vz));
                    break;
                case "vt" when f.Length >= 3: uvs.Add((F(f[1]), F(f[2]))); break;
                case "vn" when f.Length >= 4:
                    (float nx, float ny, float nz) = (F(f[1]), F(f[2]), F(f[3]));
                    normals.Add(zUp ? (nx, nz, -ny) : (nx, ny, nz));
                    break;
                case "f" when f.Length >= 4:
                    uint first = Vertex(f[1]);
                    for (int i = 2; i < f.Length - 1; i++)
                    {
                        indices.AddRange([first, Vertex(f[i]), Vertex(f[i + 1])]);
                    }
                    break;
            }
        }

        var primitiveMaterials = indicesByMaterial.Where(pair => pair.Value.Count > 0).ToList();
        if (primitiveMaterials.Count == 0) throw new InvalidDataException("The OBJ file contains no faces.");

        var colors = new float[outNormals.Count * 4];
        for (int i = 0; i < outNormals.Count; i++)
        {
            (float x, float y, float z) = outNormals[i];
            float shade = 1f;
            float length = MathF.Sqrt(x * x + y * y + z * z);
            if (length > 0f) shade = 0.55f + 0.45f * Math.Clamp((x * 0.3f + y * 0.8f + z * 0.5f) / length, 0f, 1f);
            colors[i * 4] = colors[i * 4 + 1] = colors[i * 4 + 2] = shade;
            colors[i * 4 + 3] = 1f;
        }

        float[] min = [float.MaxValue, float.MaxValue, float.MaxValue], max = [float.MinValue, float.MinValue, float.MinValue];
        for (int i = 0; i < outPositions.Count; i++)
        {
            min[i % 3] = Math.Min(min[i % 3], outPositions[i]);
            max[i % 3] = Math.Max(max[i % 3], outPositions[i]);
        }

        var bin = new MemoryStream();
        var writer = new BinaryWriter(bin);
        int Section(IEnumerable<byte[]> items)
        {
            int start = (int)bin.Position;
            foreach (byte[] item in items) writer.Write(item);
            return start;
        }
        int positionOffset = Section(outPositions.Select(BitConverter.GetBytes));
        int uvOffset = Section(outUvs.Select(BitConverter.GetBytes));
        int colorOffset = Section(colors.Select(BitConverter.GetBytes));
        int count = outPositions.Count / 3;

        var accessors = new List<object>
        {
            new { bufferView = 0, componentType = 5126, count, type = "VEC3", min, max },
            new { bufferView = 1, componentType = 5126, count, type = "VEC2" },
            new { bufferView = 2, componentType = 5126, count, type = "VEC4" }
        };
        var bufferViews = new List<object>
        {
            new { buffer = 0, byteOffset = positionOffset, byteLength = count * 12 },
            new { buffer = 0, byteOffset = uvOffset, byteLength = count * 8 },
            new { buffer = 0, byteOffset = colorOffset, byteLength = count * 16 }
        };
        var images = new List<object>();
        var materials = new List<object>();
        var imageIndexByPath = new Dictionary<string, int>();
        var primitives = new List<object>();
        foreach ((string material, List<uint> faces) in primitiveMaterials)
        {
            int indexStart = Section(faces.Select(BitConverter.GetBytes));
            bufferViews.Add(new { buffer = 0, byteOffset = indexStart, byteLength = faces.Count * 4 });
            accessors.Add(new { bufferView = bufferViews.Count - 1, componentType = 5125, count = faces.Count, type = "SCALAR" });
            int? materialIndex = null;
            if (materialImages.TryGetValue(material, out string? imagePath) && File.Exists(imagePath))
            {
                if (!imageIndexByPath.TryGetValue(imagePath, out int imageIndex))
                {
                    byte[] image = File.ReadAllBytes(imagePath);
                    while (bin.Position % 4 != 0) writer.Write((byte)0);
                    int imageStart = (int)bin.Position;
                    writer.Write(image);
                    bufferViews.Add(new { buffer = 0, byteOffset = imageStart, byteLength = image.Length });
                    images.Add(new { bufferView = bufferViews.Count - 1, mimeType = imagePath.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ? "image/png" : "image/jpeg" });
                    imageIndexByPath[imagePath] = imageIndex = images.Count - 1;
                }
                materials.Add(new { pbrMetallicRoughness = new { baseColorTexture = new { index = imageIndex }, metallicFactor = 0, roughnessFactor = 1 } });
                materialIndex = materials.Count - 1;
            }
            primitives.Add(materialIndex is int m
                ? new { attributes = new { POSITION = 0, TEXCOORD_0 = 1, COLOR_0 = 2 }, indices = accessors.Count - 1, mode = 4, material = m }
                : (object)new { attributes = new { POSITION = 0, TEXCOORD_0 = 1, COLOR_0 = 2 }, indices = accessors.Count - 1, mode = 4 });
        }

        byte[] binary = bin.ToArray();
        var gltf = new Dictionary<string, object>
        {
            ["asset"] = new { version = "2.0", generator = "DSPRE" },
            ["scene"] = 0,
            ["scenes"] = new[] { new { nodes = new[] { 0 } } },
            ["nodes"] = new[] { new { mesh = 0 } },
            ["meshes"] = new[] { new { primitives } },
            ["accessors"] = accessors,
            ["bufferViews"] = bufferViews,
            ["buffers"] = new[] { new { byteLength = binary.Length } }
        };
        if (images.Count > 0)
        {
            gltf["images"] = images;
            gltf["textures"] = Enumerable.Range(0, images.Count).Select(i => new { source = i }).ToList();
            gltf["materials"] = materials;
        }

        byte[] json = Pad(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(gltf)), 0x20);
        binary = Pad(binary, 0);
        var glb = new MemoryStream();
        var output = new BinaryWriter(glb);
        output.Write(0x46546C67u);
        output.Write(2u);
        output.Write((uint)(12 + 8 + json.Length + 8 + binary.Length));
        output.Write((uint)json.Length);
        output.Write(0x4E4F534Au);
        output.Write(json);
        output.Write((uint)binary.Length);
        output.Write(0x004E4942u);
        output.Write(binary);
        return new Result(glb.ToArray(), count, primitiveMaterials.Sum(pair => pair.Value.Count) / 3);
    }

    private static IEnumerable<(string Material, string Image)> ReadMaterialImages(string directory, string name)
    {
        string path = Path.Combine(directory, name);
        if (!File.Exists(path)) yield break;
        string? current = null;
        foreach (string raw in File.ReadLines(path))
        {
            string line = raw.Trim();
            if (line.StartsWith("newmtl ", StringComparison.Ordinal)) current = line[7..].Trim();
            else if (current is not null && line.StartsWith("map_Kd ", StringComparison.Ordinal))
            {
                string file = line[7..].Trim().Split(' ')[^1].Replace('\\', '/');
                yield return (current, Path.Combine(directory, file));
            }
        }
    }

    private static int Resolve(string token, int count)
    {
        int index = int.Parse(token, CultureInfo.InvariantCulture);
        int resolved = index < 0 ? count + index : index - 1;
        if (resolved < 0 || resolved >= count) throw new InvalidDataException($"OBJ index {index} is out of range.");
        return resolved;
    }

    private static byte[] Pad(byte[] data, byte fill)
    {
        int padded = (data.Length + 3) & ~3;
        if (padded == data.Length) return data;
        byte[] result = new byte[padded];
        Array.Fill(result, fill);
        data.CopyTo(result, 0);
        return result;
    }
}
