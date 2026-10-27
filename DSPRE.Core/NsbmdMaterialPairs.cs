using System.Buffers.Binary;
using System.Text;

namespace DSPRE.Core;

/// <summary>Which texture each material of an NSBMD model is drawn with, and with which palette. An
/// NSBTX texture pack does not record this - the models that use the pack do, in their MDL0 material
/// block's texture and palette dictionaries (each name lists the materials using it), which is what
/// legacy's NSBMDLoader.ReadMdl0 reads to pair them for rendering.</summary>
public static class NsbmdMaterialPairs
{
    public sealed record Pair(string Texture, string Palette);

    /// <summary>Every texture/palette pair the model's materials use. A material with a texture but no
    /// palette (direct-colour textures) is left out.</summary>
    public static List<Pair> Read(byte[] nsbmd)
    {
        var pairs = new List<Pair>();
        if (nsbmd.Length < 0x14 || Encoding.ASCII.GetString(nsbmd, 0, 4) != "BMD0") return pairs;

        int blockCount = BinaryPrimitives.ReadUInt16LittleEndian(nsbmd.AsSpan(0x0E));
        for (int block = 0; block < blockCount; block++)
        {
            int mdl0 = (int)BinaryPrimitives.ReadUInt32LittleEndian(nsbmd.AsSpan(0x10 + 4 * block));
            if (mdl0 + 8 > nsbmd.Length || Encoding.ASCII.GetString(nsbmd, mdl0, 4) != "MDL0") continue;

            // Model dictionary: its data entries (one 4-byte model offset each) follow the dictionary's
            // 4-byte header, the unknown block (8 bytes + 4 per entry) and the data block header (4 bytes).
            int dictionary = mdl0 + 8;
            int modelCount = nsbmd[dictionary + 1];
            int offsets = dictionary + 4 + 8 + 4 * modelCount + 4;
            for (int model = 0; model < modelCount; model++)
            {
                int modelStart = mdl0 + (int)BinaryPrimitives.ReadUInt32LittleEndian(nsbmd.AsSpan(offsets + 4 * model));
                int materials = modelStart + (int)BinaryPrimitives.ReadUInt32LittleEndian(nsbmd.AsSpan(modelStart + 8));
                Dictionary<int, string> textureOf = ReadMaterialDictionary(nsbmd, materials, materials + BinaryPrimitives.ReadUInt16LittleEndian(nsbmd.AsSpan(materials)));
                Dictionary<int, string> paletteOf = ReadMaterialDictionary(nsbmd, materials, materials + BinaryPrimitives.ReadUInt16LittleEndian(nsbmd.AsSpan(materials + 2)));
                foreach ((int material, string texture) in textureOf)
                {
                    if (paletteOf.TryGetValue(material, out string? palette)) pairs.Add(new Pair(texture, palette));
                }
            }
        }

        return pairs;
    }

    /// <summary>A texture or palette dictionary: for each name, the material ids that use it.</summary>
    private static Dictionary<int, string> ReadMaterialDictionary(byte[] data, int materialBlock, int dictionary)
    {
        var byMaterial = new Dictionary<int, string>();
        int count = data[dictionary + 1];
        int entries = dictionary + 4 + 8 + 4 * count + 4;
        int names = entries + 4 * count;
        for (int i = 0; i < count; i++)
        {
            uint flags = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(entries + 4 * i));
            int list = materialBlock + (int)(flags & 0xFFFF);
            int materialCount = (int)(flags >> 16) & 0xFF;
            string name = Encoding.ASCII.GetString(data, names + 16 * i, 16).TrimEnd('\0', ' ');
            for (int k = 0; k < materialCount; k++) byMaterial[data[list + k]] = name;
        }

        return byMaterial;
    }

    /// <summary>The pairs each map's terrain model uses, by map id. Map texture packs are used by these
    /// models; which pack a map gets comes from its area data.</summary>
    public static Dictionary<int, List<Pair>> ReadMapPairs(RomProject project)
    {
        var byMap = new Dictionary<int, List<Pair>>();
        if (!Directory.Exists(project.MapsDirectory)) return byMap;
        foreach (string path in Directory.GetFiles(project.MapsDirectory))
        {
            if (!int.TryParse(Path.GetFileName(path), out int mapId)) continue;
            byMap[mapId] = TryRead(MapBuildingService.LoadModelData(project, mapId));
        }

        return byMap;
    }

    /// <summary>The pairs every building model uses. Building texture packs are used by these.</summary>
    public static List<Pair> ReadBuildingPairs(RomProject project) =>
        new[] { project.ExteriorBuildingModelsDirectory, project.InteriorBuildingModelsDirectory }
            .Where(Directory.Exists)
            .SelectMany(Directory.GetFiles)
            .SelectMany(path => TryRead(File.ReadAllBytes(path)))
            .ToList();

    private static List<Pair> TryRead(byte[] nsbmd)
    {
        try
        {
            return Read(nsbmd);
        }
        catch (Exception ex) when (ex is ArgumentOutOfRangeException or IndexOutOfRangeException)
        {
            return [];
        }
    }
}
