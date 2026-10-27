namespace DSPRE.Core;

/// <summary>Reads/writes the "Permissions" section of a map's raw combined file - the same file
/// MapBuildingService edits the Buildings section of. See the byte-layout comment at the top of
/// DS_Map/ROMFiles/MapFile.cs: the permissions section is a flat 32x32 grid of (type, collision) byte
/// pairs in row-major order (2048 bytes total), sitting immediately after the optional HGSS-only
/// background-sound prefix and before the Buildings section. Every other section is treated as an
/// opaque blob and round-tripped byte-for-byte unchanged, mirroring MapBuildingService.</summary>
public static class MapPermissionsService
{
    public const int GridSize = 32;

    public static (byte[,] Types, byte[,] Collisions) Load(RomProject project, int mapId)
    {
        string path = GetMapFilePath(project, mapId);
        var types = new byte[GridSize, GridSize];
        var collisions = new byte[GridSize, GridSize];
        if (!File.Exists(path)) return (types, collisions);

        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);
        MapFileSections sections = ReadSections(reader, IsHgss(project));
        return Parse(sections.PermissionsData);
    }

    /// <summary>A map's permissions section: a type byte then a collision byte for each of the 32x32
    /// cells, row by row - also what legacy exports as a permissions file. A short section leaves the rest
    /// zero.</summary>
    public static (byte[,] Types, byte[,] Collisions) Parse(byte[] data)
    {
        var types = new byte[GridSize, GridSize];
        var collisions = new byte[GridSize, GridSize];
        using MemoryStream permStream = new(data);
        using BinaryReader permReader = new(permStream);
        for (int i = 0; i < GridSize; i++)
        {
            for (int j = 0; j < GridSize; j++)
            {
                if (permStream.Position + 2 > permStream.Length) return (types, collisions);
                types[i, j] = permReader.ReadByte();
                collisions[i, j] = permReader.ReadByte();
            }
        }

        return (types, collisions);
    }

    public static byte[] Serialize(byte[,] types, byte[,] collisions)
    {
        byte[] data = new byte[GridSize * GridSize * 2];
        for (int i = 0, offset = 0; i < GridSize; i++)
        {
            for (int j = 0; j < GridSize; j++, offset += 2)
            {
                data[offset] = types[i, j];
                data[offset + 1] = collisions[i, j];
            }
        }

        return data;
    }

    public static void Save(RomProject project, int mapId, byte[,] types, byte[,] collisions) =>
        EditJournal.Track(project, EditJournalDocuments.MapPermissions, EditJournalDocuments.Keys(("map", mapId)), () => SaveUntracked(project, mapId, types, collisions));

    private static void SaveUntracked(RomProject project, int mapId, byte[,] types, byte[,] collisions)
    {
        string path = GetMapFilePath(project, mapId);
        if (!File.Exists(path)) return;

        MapFileSections sections;
        using (FileStream readStream = File.OpenRead(path))
        using (BinaryReader reader = new(readStream))
        {
            sections = ReadSections(reader, IsHgss(project));
        }

        using MemoryStream newPermStream = new(GridSize * GridSize * 2);
        using (BinaryWriter permWriter = new(newPermStream))
        {
            for (int i = 0; i < GridSize; i++)
            {
                for (int j = 0; j < GridSize; j++)
                {
                    permWriter.Write(types[i, j]);
                    permWriter.Write(collisions[i, j]);
                }
            }
        }

        byte[] newPermissionsData = newPermStream.ToArray();

        using FileStream writeStream = new(path, FileMode.Create, FileAccess.Write);
        using BinaryWriter writer = new(writeStream);
        writer.Write(newPermissionsData.Length);
        writer.Write(sections.BuildingsData.Length);
        writer.Write(sections.NsbmdData.Length);
        writer.Write(sections.BdhcData.Length);
        if (sections.BgsData is not null) writer.Write(sections.BgsData);
        writer.Write(newPermissionsData);
        writer.Write(sections.BuildingsData);
        writer.Write(sections.NsbmdData);
        writer.Write(sections.BdhcData);
    }

    private static string GetMapFilePath(RomProject project, int mapId) => Path.Combine(project.MapsDirectory, mapId.ToString("D4"));

    private static bool IsHgss(RomProject project) =>
        project.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata is not null && metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;

    // Mirrors MapBuildingService's private MapFileSections/ReadSections exactly (same file format) -
    // kept as an independent copy rather than a shared dependency so each service stays a single,
    // self-contained, independently-verifiable unit, consistent with this codebase's one-service-
    // per-feature convention.
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
}
