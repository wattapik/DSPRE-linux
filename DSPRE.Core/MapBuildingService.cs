namespace DSPRE.Core;

/// <summary>One placed Building instance on a map: byte-for-byte equivalent of the legacy
/// DS_Map/ROMFiles/MapFile.cs "Building" class. Position/rotation are split into a whole part and a
/// fractional part exactly like the ROM format stores them (see MapBuildingService for the exact
/// byte layout); callers that just want a single float should use the WholePlusFraction helpers.</summary>
public sealed class BuildingRecord
{
    public uint ModelId { get; set; }
    public short XPosition { get; set; }
    public short YPosition { get; set; } = 1;
    public short ZPosition { get; set; }
    public ushort XFraction { get; set; }
    public ushort YFraction { get; set; }
    public ushort ZFraction { get; set; }
    public ushort XRotation { get; set; }
    public ushort YRotation { get; set; }
    public ushort ZRotation { get; set; }
    public uint Width { get; set; } = 16;
    public uint Height { get; set; } = 16;
    public uint Length { get; set; } = 16;

    public static ushort DegreesToU16(float degrees) => (ushort)(degrees * 65536f / 360f);
    public static float U16ToDegrees(ushort value) => value * 360f / 65536f;

    public BuildingRecord Clone() => new()
    {
        ModelId = ModelId,
        XPosition = XPosition,
        // Matches legacy's "duplicate" constructor (Building(Building toCopy)): nudges the copy one
        // whole unit over on Y so it doesn't render exactly on top of the original.
        YPosition = (short)(YPosition + 1),
        ZPosition = ZPosition,
        XFraction = XFraction,
        YFraction = YFraction,
        ZFraction = ZFraction,
        XRotation = XRotation,
        YRotation = YRotation,
        ZRotation = ZRotation,
        Width = Width,
        Height = Height,
        Length = Length
    };

    /// <summary>An exact, value-preserving copy for snapshotting state to compare against later (e.g.
    /// detecting unsaved edits) - unlike Clone(), which deliberately nudges YPosition for the "Duplicate
    /// Building" UI action and would make every snapshot look different from its source.</summary>
    public BuildingRecord Snapshot() => new()
    {
        ModelId = ModelId,
        XPosition = XPosition,
        YPosition = YPosition,
        ZPosition = ZPosition,
        XFraction = XFraction,
        YFraction = YFraction,
        ZFraction = ZFraction,
        XRotation = XRotation,
        YRotation = YRotation,
        ZRotation = ZRotation,
        Width = Width,
        Height = Height,
        Length = Length
    };
}

/// <summary>Reads/writes the "Buildings" section of a map's raw combined file (permissions + buildings
/// + NSBMD terrain model + BDHC collision mesh, plus an HGSS-only background-sound prefix) - see the
/// byte-layout comment at the top of DS_Map/ROMFiles/MapFile.cs, which this mirrors exactly. Every
/// other section is treated as an opaque blob and round-tripped byte-for-byte unchanged, so this
/// service never needs to understand the NSBMD/BDHC formats at all.</summary>
public static class MapBuildingService
{
    private const int BuildingRecordSize = 48;

    public static List<BuildingRecord> Load(RomProject project, int mapId)
    {
        string path = GetMapFilePath(project, mapId);
        if (!File.Exists(path)) return [];

        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);
        MapFileSections sections = ReadSections(reader, IsHgss(project));
        return ParseBuildings(sections.BuildingsData);
    }

    /// <summary>The map's terrain model (NSBMD) as stored in its map file.</summary>
    public static byte[] LoadModelData(RomProject project, int mapId)
    {
        string path = GetMapFilePath(project, mapId);
        if (!File.Exists(path)) return [];

        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);
        return ReadSections(reader, IsHgss(project)).NsbmdData;
    }

    public static void Save(RomProject project, int mapId, IReadOnlyList<BuildingRecord> buildings) =>
        EditJournal.Track(project, EditJournalDocuments.MapBuilding, EditJournalDocuments.Keys(("map", mapId)), () => SaveUntracked(project, mapId, buildings));

    private static void SaveUntracked(RomProject project, int mapId, IReadOnlyList<BuildingRecord> buildings)
    {
        string path = GetMapFilePath(project, mapId);
        if (!File.Exists(path)) return;
        WriteMapFile(project, mapId, buildings, path);
    }

    /// <summary>Writes the map's file with the given buildings to <paramref name="destinationPath"/>
    /// instead of over the project's copy - how the map editor renders building edits that are not
    /// saved yet. Not journaled: nothing in the project changes.</summary>
    public static void WritePreviewMapFile(RomProject project, int mapId, IReadOnlyList<BuildingRecord> buildings, string destinationPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(destinationPath)!);
        WriteMapFile(project, mapId, buildings, destinationPath);
    }

    private static void WriteMapFile(RomProject project, int mapId, IReadOnlyList<BuildingRecord> buildings, string destinationPath)
    {
        MapFileSections sections;
        using (FileStream readStream = File.OpenRead(GetMapFilePath(project, mapId)))
        using (BinaryReader reader = new(readStream))
        {
            sections = ReadSections(reader, IsHgss(project));
        }

        byte[] newBuildingsData = WriteBuildings(buildings);

        using FileStream writeStream = new(destinationPath, FileMode.Create, FileAccess.Write);
        using BinaryWriter writer = new(writeStream);
        writer.Write(sections.PermissionsData.Length);
        writer.Write(newBuildingsData.Length);
        writer.Write(sections.NsbmdData.Length);
        writer.Write(sections.BdhcData.Length);
        if (sections.BgsData is not null) writer.Write(sections.BgsData);
        writer.Write(sections.PermissionsData);
        writer.Write(newBuildingsData);
        writer.Write(sections.NsbmdData);
        writer.Write(sections.BdhcData);
    }

    /// <summary>Building model IDs+names for the exterior or (HGSS-only) interior model list, matching
    /// legacy's MapEditor.GetBuildingsList: each model file's own embedded name sits 16 bytes long at
    /// offset 0x38 of the raw (un-embedded-texture) NSBMD file.</summary>
    public static IReadOnlyList<(int Id, string Name)> GetBuildingModels(RomProject project, bool interior)
    {
        string directory = interior ? project.InteriorBuildingModelsDirectory : project.ExteriorBuildingModelsDirectory;
        if (!Directory.Exists(directory)) return [];

        int count = Directory.GetFiles(directory).Length;
        var models = new List<(int, string)>(count);
        for (int i = 0; i < count; i++)
        {
            string path = Path.Combine(directory, i.ToString("D4"));
            string name = string.Empty;
            try
            {
                using FileStream stream = File.OpenRead(path);
                using BinaryReader reader = new(stream);
                stream.Position = 0x38;
                name = System.Text.Encoding.UTF8.GetString(reader.ReadBytes(16)).TrimEnd('\0', ' ');
            }
            catch (IOException) { }
            models.Add((i, name));
        }

        return models;
    }

    private static string GetMapFilePath(RomProject project, int mapId) => Path.Combine(project.MapsDirectory, mapId.ToString("D4"));

    private static bool IsHgss(RomProject project) =>
        project.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata is not null && metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;

    private sealed record MapFileSections(byte[]? BgsData, byte[] PermissionsData, byte[] BuildingsData, byte[] NsbmdData, byte[] BdhcData);

    private static MapFileSections ReadSections(BinaryReader reader, bool hgss)
    {
        int permissionsLength = reader.ReadInt32();
        int buildingsLength = reader.ReadInt32();
        int nsbmdLength = reader.ReadInt32();
        int bdhcLength = reader.ReadInt32();

        byte[]? bgsData = null;
        if (hgss)
        {
            ushort bgsSignature = reader.ReadUInt16();
            ushort bgsDataLength = reader.ReadUInt16();
            reader.BaseStream.Position -= 4;
            bgsData = bgsSignature == 0x1234 ? reader.ReadBytes(bgsDataLength + 4) : reader.ReadBytes(4);
        }

        byte[] permissionsData = reader.ReadBytes(permissionsLength);
        byte[] buildingsData = reader.ReadBytes(buildingsLength);
        byte[] nsbmdData = reader.ReadBytes(nsbmdLength);
        byte[] bdhcData = reader.ReadBytes(bdhcLength);
        return new MapFileSections(bgsData, permissionsData, buildingsData, nsbmdData, bdhcData);
    }

    /// <summary>A map's buildings section (0x30 bytes a building) - also what legacy exports as a
    /// buildings file.</summary>
    public static List<BuildingRecord> ParseBuildings(byte[] data)
    {
        var buildings = new List<BuildingRecord>(data.Length / BuildingRecordSize);
        using MemoryStream stream = new(data);
        using BinaryReader reader = new(stream);
        int count = data.Length / BuildingRecordSize;
        for (int i = 0; i < count; i++)
        {
            long recordStart = stream.Position;
            var building = new BuildingRecord
            {
                ModelId = reader.ReadUInt32(),
                XFraction = reader.ReadUInt16(),
                XPosition = reader.ReadInt16(),
                YFraction = reader.ReadUInt16(),
                YPosition = reader.ReadInt16(),
                ZFraction = reader.ReadUInt16(),
                ZPosition = reader.ReadInt16(),
                XRotation = reader.ReadUInt16()
            };
            stream.Position += 2;
            building.YRotation = reader.ReadUInt16();
            stream.Position += 2;
            building.ZRotation = reader.ReadUInt16();
            stream.Position += 2;
            stream.Position += 1;
            building.Width = reader.ReadUInt16();
            stream.Position += 2;
            building.Height = reader.ReadUInt16();
            stream.Position += 2;
            building.Length = reader.ReadUInt16();

            stream.Position = recordStart + BuildingRecordSize;
            buildings.Add(building);
        }

        return buildings;
    }

    public static byte[] WriteBuildings(IReadOnlyList<BuildingRecord> buildings)
    {
        using MemoryStream stream = new(BuildingRecordSize * buildings.Count);
        using BinaryWriter writer = new(stream);
        foreach (BuildingRecord building in buildings)
        {
            writer.Write(building.ModelId);
            writer.Write(building.XFraction);
            writer.Write(building.XPosition);
            writer.Write(building.YFraction);
            writer.Write(building.YPosition);
            writer.Write(building.ZFraction);
            writer.Write(building.ZPosition);

            writer.Write((int)building.XRotation);
            writer.Write((int)building.YRotation);
            writer.Write((int)building.ZRotation);

            writer.Write((byte)0);

            writer.Write(building.Width);
            writer.Write(building.Height);
            writer.Write(building.Length);

            writer.Write(new byte[0x7]);
        }

        return stream.ToArray();
    }
}
