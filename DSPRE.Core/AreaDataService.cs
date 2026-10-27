namespace DSPRE.Core;

public sealed class AreaDataRecord
{
    public const byte TypeIndoor = 0;
    public const byte TypeOutdoor = 1;

    public int Id { get; set; }
    public ushort BuildingsTileset { get; set; }
    public ushort MapTileset { get; set; }
    public ushort DynamicTextureType { get; set; }
    public ushort Unknown1 { get; set; }
    public byte AreaType { get; set; } = TypeOutdoor;
    public ushort LightType { get; set; }

    public override string ToString() => $"AreaData File {Id}";
}

public static class AreaDataService
{
    public static IReadOnlyList<int> GetIds(RomProject project)
    {
        if (!Directory.Exists(project.AreaDataDirectory))
        {
            return [];
        }

        return Directory.GetFiles(project.AreaDataDirectory)
            .Select(path => int.TryParse(Path.GetFileNameWithoutExtension(path), out int id) ? id : -1)
            .Where(id => id >= 0)
            .OrderBy(id => id)
            .ToArray();
    }

    public static string GetPath(RomProject project, int id) => Path.Combine(project.AreaDataDirectory, $"{id:D4}");

    public static AreaDataRecord Load(RomProject project, int id, bool isHeartGoldSoulSilver)
    {
        byte[] data = File.ReadAllBytes(GetPath(project, id));
        var record = new AreaDataRecord { Id = id };
        if (data.Length < 8)
        {
            return record;
        }

        record.BuildingsTileset = BitConverter.ToUInt16(data, 0);
        record.MapTileset = BitConverter.ToUInt16(data, 2);
        if (isHeartGoldSoulSilver)
        {
            record.DynamicTextureType = BitConverter.ToUInt16(data, 4);
            record.AreaType = data[6];
            record.LightType = data[7];
        }
        else
        {
            record.Unknown1 = BitConverter.ToUInt16(data, 4);
            record.LightType = BitConverter.ToUInt16(data, 6);
        }

        return record;
    }

    public static byte[] ToByteArray(AreaDataRecord record, bool isHeartGoldSoulSilver)
    {
        byte[] data = new byte[8];
        BitConverter.TryWriteBytes(data.AsSpan(0), record.BuildingsTileset);
        BitConverter.TryWriteBytes(data.AsSpan(2), record.MapTileset);
        if (isHeartGoldSoulSilver)
        {
            BitConverter.TryWriteBytes(data.AsSpan(4), record.DynamicTextureType);
            data[6] = record.AreaType;
            data[7] = (byte)record.LightType;
        }
        else
        {
            BitConverter.TryWriteBytes(data.AsSpan(4), record.Unknown1);
            BitConverter.TryWriteBytes(data.AsSpan(6), record.LightType);
        }

        return data;
    }

    public static void Save(RomProject project, AreaDataRecord record, bool isHeartGoldSoulSilver) =>
        EditJournal.Track(project, EditJournalDocuments.AreaData, EditJournalDocuments.Keys(("areaData", record.Id)), () => SaveUntracked(project, record, isHeartGoldSoulSilver));

    private static void SaveUntracked(RomProject project, AreaDataRecord record, bool isHeartGoldSoulSilver)
    {
        Directory.CreateDirectory(project.AreaDataDirectory);
        File.WriteAllBytes(GetPath(project, record.Id), ToByteArray(record, isHeartGoldSoulSilver));
    }

    /// <summary>The original editor clones file 0000 when appending a new AreaData slot.</summary>
    public static int Add(RomProject project)
    {
        Directory.CreateDirectory(project.AreaDataDirectory);
        int newId = GetIds(project).Count;
        string source = GetPath(project, 0);
        string destination = GetPath(project, newId);
        if (File.Exists(source))
        {
            File.Copy(source, destination, false);
        }
        else
        {
            File.WriteAllBytes(destination, new byte[8]);
        }

        EditJournal.Record(project, "CreateAreaData", ("areaData", newId));
        return newId;
    }

    /// <summary>Replaces an AreaData file wholesale with an external binary file.</summary>
    public static void Import(RomProject project, int id, string sourcePath)
    {
        string fullSource = Path.GetFullPath(sourcePath);
        File.Copy(fullSource, GetPath(project, id), true);
        EditJournal.Record(project, "ImportAreaData", ("areaData", id), ("source", fullSource));
    }

    public static bool RemoveLast(RomProject project)
    {
        IReadOnlyList<int> ids = GetIds(project);
        if (ids.Count <= 1)
        {
            return false;
        }

        File.Delete(GetPath(project, ids[^1]));
        EditJournal.Record(project, "DeleteLastAreaData", ("areaData", ids[^1]));
        return true;
    }

    /// <summary>
    /// Maps each map file to the AreaData record its header selects, mirroring exactly how the legacy
    /// Event Editor resolves it (DS_Map/Editors/EventEditor.cs's DisplayEventMap): for each header,
    /// walk its own Matrix (header.MatrixId). If that matrix carries a header section, each cell's
    /// map ID is owned by whichever header sits at that same cell (matrix.headers[y,x]) - a matrix can
    /// place multiple headers' maps next to each other for seamless connections, so the cell's own
    /// header (not the matrix's "owning" header) is authoritative there. If the matrix has no header
    /// section at all, it's an unambiguous solo matrix (typically a single interior/dungeon map with no
    /// neighbors to distinguish) and every map in it belongs to the *originating* header directly -
    /// legacy falls back to exactly this (its own already-known areaDataID) in that case rather than
    /// reading a per-cell header that doesn't exist. A prior version of this method skipped matrices
    /// lacking a header section entirely, which meant most interior/dungeon maps - never referenced
    /// from any matrix that *does* have header data - resolved to nothing.
    /// </summary>
    public static IReadOnlyDictionary<int, AreaDataRecord> ResolveMapAreaData(
        RomProject project,
        Func<ushort, MapHeaderRecord> getHeader,
        int headerCount,
        bool isHeartGoldSoulSilver)
    {
        var areaDataById = new Dictionary<byte, AreaDataRecord?>();
        AreaDataRecord? LoadCached(byte areaDataId)
        {
            if (!areaDataById.TryGetValue(areaDataId, out AreaDataRecord? record))
            {
                try
                {
                    record = Load(project, areaDataId, isHeartGoldSoulSilver);
                }
                catch (IOException)
                {
                    record = null;
                }

                areaDataById[areaDataId] = record;
            }

            return record;
        }

        var result = new Dictionary<int, AreaDataRecord>();
        if (!Directory.Exists(project.MatricesDirectory))
        {
            return result;
        }

        for (ushort headerId = 0; headerId < headerCount; headerId++)
        {
            MapHeaderRecord header = getHeader(headerId);
            string matrixPath = Path.Combine(project.MatricesDirectory, $"{header.MatrixId:D4}");
            if (!File.Exists(matrixPath))
            {
                continue;
            }

            (bool hasHeaders, ushort[]? cellHeaders, ushort[] cellMaps)? matrix;
            try
            {
                matrix = ReadMatrixCells(matrixPath);
            }
            catch (IOException)
            {
                continue;
            }

            if (matrix is not { } loaded)
            {
                continue;
            }

            AreaDataRecord? ownRecord = null;
            for (int cell = 0; cell < loaded.cellMaps.Length; cell++)
            {
                ushort mapId = loaded.cellMaps[cell];
                if (mapId == 0xFFFF || result.ContainsKey(mapId))
                {
                    continue;
                }

                AreaDataRecord? record;
                if (loaded.hasHeaders)
                {
                    ushort cellHeaderId = loaded.cellHeaders![cell];
                    record = cellHeaderId < headerCount ? LoadCached(getHeader(cellHeaderId).AreaDataId) : null;
                }
                else
                {
                    record = ownRecord ??= LoadCached(header.AreaDataId);
                }

                if (record is not null)
                {
                    result[mapId] = record;
                }
            }
        }

        return result;
    }

    private static (bool HasHeaders, ushort[]? CellHeaders, ushort[] CellMaps) ReadMatrixCells(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new BinaryReader(stream);
        byte width = reader.ReadByte();
        byte height = reader.ReadByte();
        bool hasHeaders = reader.ReadBoolean();
        bool hasHeights = reader.ReadBoolean();
        byte nameLength = reader.ReadByte();
        reader.ReadBytes(nameLength);

        int cellCount = width * height;
        ushort[]? cellHeaders = null;
        if (hasHeaders)
        {
            cellHeaders = new ushort[cellCount];
            for (int i = 0; i < cellCount; i++)
            {
                cellHeaders[i] = reader.ReadUInt16();
            }
        }

        if (hasHeights)
        {
            reader.ReadBytes(cellCount);
        }

        ushort[] cellMaps = new ushort[cellCount];
        for (int i = 0; i < cellCount; i++)
        {
            cellMaps[i] = reader.ReadUInt16();
        }

        return (hasHeaders, cellHeaders, cellMaps);
    }

    /// <summary>The header whose area data a map is given, found the way <see cref="ResolveMapAreaData"/>
    /// does: the first header, in order, whose matrix places the map (through the cell's own header
    /// when the matrix has them). Null when no header reaches the map.</summary>
    public static ushort? FindHeaderForMap(RomProject project, Func<ushort, MapHeaderRecord> getHeader, int headerCount, int mapId)
    {
        for (ushort headerId = 0; headerId < headerCount; headerId++)
        {
            string matrixPath = Path.Combine(project.MatricesDirectory, $"{getHeader(headerId).MatrixId:D4}");
            if (!File.Exists(matrixPath)) continue;

            (bool hasHeaders, ushort[]? cellHeaders, ushort[] cellMaps) matrix;
            try
            {
                matrix = ReadMatrixCells(matrixPath);
            }
            catch (IOException)
            {
                continue;
            }

            int cell = Array.IndexOf(matrix.cellMaps, (ushort)mapId);
            if (cell < 0) continue;
            if (!matrix.hasHeaders) return headerId;
            if (matrix.cellHeaders![cell] < headerCount) return matrix.cellHeaders[cell];
        }

        return null;
    }
}
