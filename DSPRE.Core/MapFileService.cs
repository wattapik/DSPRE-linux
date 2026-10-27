using System.Buffers.Binary;

namespace DSPRE.Core;

/// <summary>A map file's parts. Background sounds only exist in HeartGold/SoulSilver map files.</summary>
public sealed record MapFileParts(byte[]? SoundPlates, byte[] Permissions, byte[] Buildings, byte[] Model, byte[] Terrain);

public enum MapFileSection
{
    Model,
    Terrain,
    SoundPlates
}

/// <summary>Whole-file and per-section operations on map files (unpacked/maps): exporting, replacing,
/// adding and removing - legacy DS_Map/Editors/MapEditor.cs's map BIN, NSBMD, BDHC and BGS buttons.
/// Buildings and permissions are imported into their editors instead, so they are saved and journaled
/// like any other edit there. Every change here is journaled as its own call.</summary>
public static class MapFileService
{
    /// <summary>Legacy MapFile.blankBGS: the background-sound signature with no entries.</summary>
    public static readonly byte[] BlankSoundPlates = [0x34, 0x12, 0x00, 0x00];
    private const ushort SoundPlatesSignature = 0x1234;

    /// <summary>Above this, legacy warns a map model may misbehave in game (MapEditor.MAPMODEL_CRITICALSIZE).</summary>
    public const int ModelCriticalSize = 61000;

    public static string GetPath(RomProject project, int mapId) => Path.Combine(project.MapsDirectory, mapId.ToString("D4"));

    public static bool IsHgss(RomGameMetadata metadata) => metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;

    public static MapFileParts Load(RomProject project, RomGameMetadata metadata, int mapId) =>
        Parse(File.ReadAllBytes(GetPath(project, mapId)), IsHgss(metadata));

    /// <summary>Splits a map file. Throws when the section lengths do not add up to the file, or an HGSS
    /// file's background-sound block has no signature - what legacy reports as a corrupted map.</summary>
    public static MapFileParts Parse(byte[] data, bool hgss)
    {
        if (data.Length < 16) throw new InvalidDataException("Too short to be a map file.");
        int permissions = BinaryPrimitives.ReadInt32LittleEndian(data);
        int buildings = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(4));
        int model = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(8));
        int terrain = BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(12));
        int offset = 16;

        byte[]? soundPlates = null;
        if (hgss)
        {
            if (data.Length < offset + 4 || BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)) != SoundPlatesSignature)
            {
                throw new InvalidDataException("The background sound section is missing or corrupted.");
            }

            int soundLength = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset + 2)) + 4;
            soundPlates = Take(data, ref offset, soundLength);
        }

        if (permissions < 0 || buildings < 0 || model < 0 || terrain < 0 || (long)offset + permissions + buildings + model + terrain != data.Length)
        {
            throw new InvalidDataException("The section lengths do not match the file's size.");
        }

        return new MapFileParts(soundPlates, Take(data, ref offset, permissions), Take(data, ref offset, buildings), Take(data, ref offset, model), Take(data, ref offset, terrain));
    }

    private static byte[] Take(byte[] data, ref int offset, int length)
    {
        if (offset + length > data.Length) throw new InvalidDataException("A section runs past the end of the file.");
        byte[] part = data.AsSpan(offset, length).ToArray();
        offset += length;
        return part;
    }

    /// <summary>The map file the game expects: section lengths, the background sounds on HGSS, then the
    /// sections.</summary>
    public static byte[] Serialize(MapFileParts parts, bool hgss)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(parts.Permissions.Length);
        writer.Write(parts.Buildings.Length);
        writer.Write(parts.Model.Length);
        writer.Write(parts.Terrain.Length);
        if (hgss) writer.Write(parts.SoundPlates ?? BlankSoundPlates);
        writer.Write(parts.Permissions);
        writer.Write(parts.Buildings);
        writer.Write(parts.Model);
        writer.Write(parts.Terrain);
        writer.Flush();
        return stream.ToArray();
    }

    public static byte[] GetSection(MapFileParts parts, MapFileSection section) => section switch
    {
        MapFileSection.Model => parts.Model,
        MapFileSection.Terrain => parts.Terrain,
        _ => parts.SoundPlates ?? BlankSoundPlates
    };

    /// <summary>Replaces one section with a file's contents; journaled with the file's path, so a replay
    /// reads it from there again.</summary>
    public static void ImportSection(RomProject project, RomGameMetadata metadata, int mapId, MapFileSection section, string sourcePath)
    {
        string source = Path.GetFullPath(sourcePath);
        byte[] data = File.ReadAllBytes(source);
        if (section == MapFileSection.Model && !NsbmdTextureTools.IsNsbmd(data)) throw new InvalidDataException("The file is not an NSBMD model.");
        if (section == MapFileSection.Model && NsbmdTextureTools.HasTextures(data)) data = NsbmdTextureTools.WithoutTextures(data);
        if (section == MapFileSection.SoundPlates && (data.Length < 4 || BinaryPrimitives.ReadUInt16LittleEndian(data) != SoundPlatesSignature))
        {
            throw new InvalidDataException("The file is not background sound data (it does not start with the 0x1234 signature).");
        }

        WriteSection(project, metadata, mapId, section, data);
        EditJournal.Record(project, "ImportMapSection", ("map", mapId), ("section", section.ToString()), ("source", source));
    }

    /// <summary>Resets the map's background sounds to none (legacy's "Blank" BGS button).</summary>
    public static void BlankSoundPlatesOf(RomProject project, RomGameMetadata metadata, int mapId)
    {
        WriteSection(project, metadata, mapId, MapFileSection.SoundPlates, BlankSoundPlates);
        EditJournal.Record(project, "BlankMapSoundPlates", ("map", mapId));
    }

    private static void WriteSection(RomProject project, RomGameMetadata metadata, int mapId, MapFileSection section, byte[] data)
    {
        MapFileParts parts = Load(project, metadata, mapId);
        parts = section switch
        {
            MapFileSection.Model => parts with { Model = data },
            MapFileSection.Terrain => parts with { Terrain = data },
            _ => parts with { SoundPlates = data }
        };
        File.WriteAllBytes(GetPath(project, mapId), Serialize(parts, IsHgss(metadata)));
    }

    /// <summary>Replaces the whole map file. A map file from the other generation layout (Platinum vs
    /// HGSS, which adds the background-sound block) is adapted, as legacy's replaceMapBinButton does.
    /// Returns whether it had to be adapted.</summary>
    public static bool ImportWholeFile(RomProject project, RomGameMetadata metadata, int mapId, string sourcePath)
    {
        string source = Path.GetFullPath(sourcePath);
        byte[] data = File.ReadAllBytes(source);
        bool hgss = IsHgss(metadata);
        bool adapted = false;
        MapFileParts parts;
        try
        {
            parts = Parse(data, hgss);
        }
        catch (InvalidDataException)
        {
            parts = Parse(data, !hgss);
            adapted = true;
        }

        File.WriteAllBytes(GetPath(project, mapId), Serialize(parts, hgss));
        EditJournal.Record(project, "ImportMapFile", ("map", mapId), ("source", source));
        return adapted;
    }

    /// <summary>Appends a copy of map 0000 with its permissions and buildings cleared (legacy's "Add map file"
    /// keeps the buildings, which then stand on top of the new map's own terrain).</summary>
    public static int Add(RomProject project, RomGameMetadata metadata)
    {
        int id = HeaderFileService.NextFileId(project.MapsDirectory);
        MapFileParts template = Load(project, metadata, 0);
        File.WriteAllBytes(GetPath(project, id), Serialize(template with { Permissions = new byte[template.Permissions.Length], Buildings = [] }, IsHgss(metadata)));
        EditJournal.Record(project, "CreateMapFile", ("map", id));
        return id;
    }

    public static bool RemoveLast(RomProject project)
    {
        int last = HeaderFileService.NextFileId(project.MapsDirectory) - 1;
        if (last < 1) return false;
        File.Delete(GetPath(project, last));
        EditJournal.Record(project, "DeleteLastMapFile", ("map", last));
        return true;
    }

    /// <summary>The map's terrain model as an NSBMD file, with the given texture pack embedded when one
    /// is passed (legacy's "embed textures" export).</summary>
    public static byte[] ExportModel(MapFileParts parts, byte[]? texturePack) =>
        texturePack is null ? parts.Model : NsbmdTextureTools.WithTextures(parts.Model, texturePack);

    /// <summary>Converts the map's model, with the given texture pack, to a DAE or GLB scene with apicula
    /// (through the bridge), into <paramref name="outputDirectory"/>.</summary>
    public static async Task ConvertModelAsync(RomProject project, RomGameMetadata metadata, string toolsDirectory, MapFileParts parts, byte[]? texturePack, string outputDirectory, bool glb)
    {
        Directory.CreateDirectory(outputDirectory);
        string temporary = Path.Combine(outputDirectory, $".dspre-{Guid.NewGuid():N}.nsbmd");
        await File.WriteAllBytesAsync(temporary, ExportModel(parts, texturePack));
        try
        {
            ToolResult result = await new RomExtractionService().RunToolboxAsync(project, metadata, toolsDirectory,
                ["convert-model", temporary, outputDirectory, glb ? "glb" : "dae"]);
            if (!result.Succeeded) throw new InvalidOperationException($"apicula could not convert the model: {result.StandardError.Trim()}");
        }
        finally
        {
            File.Delete(temporary);
        }
    }
}
