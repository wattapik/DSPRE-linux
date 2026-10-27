using System.Security.Cryptography;

namespace DSPRE.Core;

/// <summary>Tidies the data a map is made of: map files, matrices, area data and the map and building texture packs.
/// <b>Merging</b> finds entries with identical contents and points everything that used them at one copy; <b>removing</b>
/// drops entries nothing refers to. Either way the entries that are left are renumbered without gaps, and every reference
/// (headers to matrices and area data, matrices to maps, area data to texture packs) is rewritten to match. Anything the
/// game's code refers to by a fixed number is not known to this and would then point at the wrong entry.</summary>
public static class MapDataCleanupService
{
    public sealed record KindReport(string Kind, int Total, int Merged, int Unused)
    {
        public int Removed => Merged + Unused;
    }

    public sealed record Report(IReadOnlyList<KindReport> Kinds, IReadOnlyList<string> Notes)
    {
        public int Removed => Kinds.Sum(kind => kind.Removed);
    }

    private sealed class Table
    {
        public required string Kind;
        public required List<int> Ids;
        public int[] Canonical = [];
        public bool[] Referenced = [];
        public bool[] MergedAway = [];
        public int[] NewId = [];

        public int IndexOf(int id) => Ids.BinarySearch(id);
    }

    public static Report Analyze(RomProject project, RomGameMetadata metadata, bool merge, bool remove) => Run(project, metadata, merge, remove, commit: false);

    public static Report Apply(RomProject project, RomGameMetadata metadata, bool merge, bool remove)
    {
        Report report;
        using (EditJournal.Suppress())
        {
            report = Run(project, metadata, merge, remove, commit: true);
        }

        if (report.Removed > 0) EditJournal.Record(project, "CleanUpMapData", ("merge", merge), ("remove", remove));
        return report;
    }

    private static List<int> IdsIn(string directory) => !Directory.Exists(directory)
        ? []
        : Directory.GetFiles(directory).Select(path => Path.GetFileName(path)).Where(name => int.TryParse(name, out _)).Select(int.Parse).Order().ToList();

    private static string Hash(byte[] data) => Convert.ToHexString(SHA1.HashData(data));

    private static int[] Canonicals(IReadOnlyList<string> hashes)
    {
        var first = new Dictionary<string, int>();
        var result = new int[hashes.Count];
        for (int i = 0; i < hashes.Count; i++) result[i] = first.TryAdd(hashes[i], i) ? i : first[hashes[i]];
        return result;
    }

    private static Report Run(RomProject project, RomGameMetadata metadata, bool merge, bool remove, bool commit)
    {
        bool hgss = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;
        IMapHeaderStore store = MapHeaderStoreFactory.Create(project, metadata) ?? throw new InvalidOperationException("This project's headers cannot be read.");
        var notes = new List<string>();

        var headers = Enumerable.Range(0, store.Count).Select(i => store.Load((ushort)i)).ToList();
        var mapPacks = new Table { Kind = "Map texture packs", Ids = IdsIn(project.MapTexturesDirectory) };
        var buildingPacks = new Table { Kind = "Building texture packs", Ids = IdsIn(project.BuildingTexturesDirectory) };
        var areas = new Table { Kind = "Area data", Ids = IdsIn(project.AreaDataDirectory) };
        var maps = new Table { Kind = "Map files", Ids = IdsIn(project.MapsDirectory) };
        var matrices = new Table { Kind = "Matrices", Ids = IdsIn(project.MatricesDirectory) };

        string[] PackHashes(Table table, string directory) => table.Ids.Select(id => Hash(File.ReadAllBytes(Path.Combine(directory, $"{id:D4}")))).ToArray();
        string[] mapPackHashes = PackHashes(mapPacks, project.MapTexturesDirectory);
        string[] buildingPackHashes = PackHashes(buildingPacks, project.BuildingTexturesDirectory);
        string[] mapHashes = maps.Ids.Select(id => Hash(File.ReadAllBytes(MapFileService.GetPath(project, id)))).ToArray();

        // Entries that are the same as an earlier one are redirected to it, in the order things refer to each other.
        mapPacks.Canonical = merge ? Canonicals(mapPackHashes) : Enumerable.Range(0, mapPacks.Ids.Count).ToArray();
        buildingPacks.Canonical = merge ? Canonicals(buildingPackHashes) : Enumerable.Range(0, buildingPacks.Ids.Count).ToArray();
        maps.Canonical = merge ? Canonicals(mapHashes) : Enumerable.Range(0, maps.Ids.Count).ToArray();

        int Redirect(Table table, int id)
        {
            int index = table.IndexOf(id);
            return index < 0 ? id : table.Ids[table.Canonical[index]];
        }

        var areaRecords = areas.Ids.Select(id => AreaDataService.Load(project, id, hgss)).ToList();
        foreach (AreaDataRecord record in areaRecords)
        {
            record.MapTileset = (ushort)Redirect(mapPacks, record.MapTileset);
            record.BuildingsTileset = (ushort)Redirect(buildingPacks, record.BuildingsTileset);
        }

        areas.Canonical = merge ? Canonicals(areaRecords.Select(record => Hash(AreaDataService.ToByteArray(record, hgss))).ToList()) : Enumerable.Range(0, areas.Ids.Count).ToArray();

        var matrixDocuments = matrices.Ids.Select(id => MatrixService.Load(project, id)).ToList();
        foreach (MatrixDocument matrix in matrixDocuments)
        {
            for (int r = 0; r < matrix.Height; r++)
            {
                for (int c = 0; c < matrix.Width; c++)
                {
                    ushort map = matrix.Maps[r, c];
                    if (map != MatrixDocument.EmptyMap) matrix.Maps[r, c] = (ushort)Redirect(maps, map);
                }
            }
        }

        matrices.Canonical = merge ? Canonicals(matrixDocuments.Select(MatrixContentHash).ToList()) : Enumerable.Range(0, matrices.Ids.Count).ToArray();

        foreach (Table table in new[] { mapPacks, buildingPacks, areas, maps, matrices })
        {
            table.MergedAway = table.Canonical.Select((canonical, index) => canonical != index).ToArray();
            table.Referenced = new bool[table.Ids.Count];
        }

        // What is still used, following the references down from the headers.
        var headerArea = new int[headers.Count];
        var headerMatrix = new int[headers.Count];
        for (int i = 0; i < headers.Count; i++)
        {
            headerArea[i] = Redirect(areas, headers[i].AreaDataId);
            headerMatrix[i] = Redirect(matrices, headers[i].MatrixId);
            MarkUsed(areas, headerArea[i]);
            MarkUsed(matrices, headerMatrix[i]);
        }

        foreach (int index in Enumerable.Range(0, areas.Ids.Count).Where(i => areas.Referenced[i]))
        {
            MarkUsed(mapPacks, areaRecords[index].MapTileset);
            MarkUsed(buildingPacks, areaRecords[index].BuildingsTileset);
        }

        foreach (int index in Enumerable.Range(0, matrices.Ids.Count).Where(i => matrices.Referenced[i]))
        {
            MatrixDocument matrix = matrixDocuments[index];
            for (int r = 0; r < matrix.Height; r++)
            {
                for (int c = 0; c < matrix.Width; c++) if (matrix.Maps[r, c] != MatrixDocument.EmptyMap) MarkUsed(maps, matrix.Maps[r, c]);
            }
        }

        static void MarkUsed(Table table, int id)
        {
            int index = table.IndexOf(id);
            if (index >= 0) table.Referenced[index] = true;
        }

        var reports = new List<KindReport>();
        foreach (Table table in new[] { matrices, areas, maps, mapPacks, buildingPacks })
        {
            int merged = table.MergedAway.Count(away => away);
            int unused = remove ? Enumerable.Range(0, table.Ids.Count).Count(i => !table.MergedAway[i] && !table.Referenced[i]) : 0;
            reports.Add(new KindReport(table.Kind, table.Ids.Count, merged, unused));
            table.NewId = new int[table.Ids.Count];
            int next = 0;
            for (int i = 0; i < table.Ids.Count; i++)
            {
                bool dropped = table.MergedAway[i] || (remove && !table.Referenced[i]);
                table.NewId[i] = dropped ? -1 : next++;
            }
        }

        if (remove && (maps.Ids.Count == 0 || matrices.Ids.Count == 0)) notes.Add("A table is empty; nothing was changed.");
        if (!commit) return new Report(reports, notes);

        // New number of an entry, whichever copy it was merged into.
        int Final(Table table, int id)
        {
            int index = table.IndexOf(id);
            if (index < 0) return id;
            int target = table.NewId[table.Canonical[index]];
            return target < 0 ? id : target;
        }

        // Contents are worked out before any file is touched.
        var areaFiles = new List<(int Id, byte[] Data)>();
        for (int i = 0; i < areas.Ids.Count; i++)
        {
            if (areas.NewId[i] < 0) continue;
            AreaDataRecord record = areaRecords[i];
            record.MapTileset = (ushort)Final(mapPacks, record.MapTileset);
            record.BuildingsTileset = (ushort)Final(buildingPacks, record.BuildingsTileset);
            areaFiles.Add((areas.NewId[i], AreaDataService.ToByteArray(record, hgss)));
        }

        var matrixFiles = new List<(int Id, byte[] Data)>();
        for (int i = 0; i < matrices.Ids.Count; i++)
        {
            if (matrices.NewId[i] < 0) continue;
            MatrixDocument matrix = matrixDocuments[i];
            for (int r = 0; r < matrix.Height; r++)
            {
                for (int c = 0; c < matrix.Width; c++) if (matrix.Maps[r, c] != MatrixDocument.EmptyMap) matrix.Maps[r, c] = (ushort)Final(maps, matrix.Maps[r, c]);
            }

            matrixFiles.Add((matrices.NewId[i], matrix.ToByteArray()));
        }

        var headerChanges = new List<MapHeaderRecord>();
        for (int i = 0; i < headers.Count; i++)
        {
            int area = Final(areas, headers[i].AreaDataId), matrix = Final(matrices, headers[i].MatrixId);
            if (area != headers[i].AreaDataId || matrix != headers[i].MatrixId) headerChanges.Add(headers[i] with { AreaDataId = (byte)area, MatrixId = (ushort)matrix });
        }

        List<(int Id, byte[] Data)> Renumbered(Table table, Func<int, string> path)
        {
            var files = new List<(int, byte[])>();
            for (int i = 0; i < table.Ids.Count; i++) if (table.NewId[i] >= 0) files.Add((table.NewId[i], File.ReadAllBytes(path(table.Ids[i]))));
            return files;
        }

        var mapFiles = Renumbered(maps, id => MapFileService.GetPath(project, id));
        var mapPackFiles = Renumbered(mapPacks, id => Path.Combine(project.MapTexturesDirectory, $"{id:D4}"));
        var buildingPackFiles = Renumbered(buildingPacks, id => Path.Combine(project.BuildingTexturesDirectory, $"{id:D4}"));

        Rewrite(project.AreaDataDirectory, areaFiles);
        Rewrite(project.MatricesDirectory, matrixFiles);
        Rewrite(project.MapsDirectory, mapFiles);
        Rewrite(project.MapTexturesDirectory, mapPackFiles);
        Rewrite(project.BuildingTexturesDirectory, buildingPackFiles);
        foreach (MapHeaderRecord header in headerChanges) store.Save(header);

        UpdateMapCreatorDocuments(project, maps, mapPacks, Final);

        // Cached models are named by the numbers that just changed.
        foreach (string cache in new[] { "map_models", "map_preview", "building_models" })
        {
            string path = Path.Combine(project.RootPath, "expanded", cache);
            if (Directory.Exists(path)) Directory.Delete(path, true);
        }

        return new Report(reports, notes);
    }

    private static string MatrixContentHash(MatrixDocument matrix)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(matrix.Width);
        writer.Write(matrix.Height);
        writer.Write(matrix.HasHeadersSection);
        writer.Write(matrix.HasAltitudesSection);
        foreach (ushort value in matrix.Maps) writer.Write(value);
        if (matrix.HasHeadersSection) foreach (ushort value in matrix.Headers) writer.Write(value);
        if (matrix.HasAltitudesSection) foreach (byte value in matrix.Altitudes) writer.Write(value);
        writer.Flush();
        return Hash(stream.ToArray());
    }

    private static void Rewrite(string directory, List<(int Id, byte[] Data)> files)
    {
        Directory.CreateDirectory(directory);
        foreach (string old in Directory.GetFiles(directory).Where(path => int.TryParse(Path.GetFileName(path), out _))) File.Delete(old);
        foreach ((int id, byte[] data) in files) File.WriteAllBytes(Path.Combine(directory, $"{id:D4}"), data);
    }

    /// <summary>Map Creator maps remember the project map and texture pack they were built into.</summary>
    private static void UpdateMapCreatorDocuments(RomProject project, Table maps, Table mapPacks, Func<Table, int, int> final)
    {
        string root = MapCreatorDocument.RootDirectory(project);
        if (!Directory.Exists(root)) return;
        foreach (string file in Directory.GetDirectories(root).Select(directory => Path.Combine(directory, "built.txt")).Where(File.Exists))
        {
            string[] parts = File.ReadAllText(file).Split(' ');
            if (parts.Length < 2 || !int.TryParse(parts[0], out int map) || !int.TryParse(parts[1], out int pack)) continue;
            int mapIndex = maps.IndexOf(map), packIndex = mapPacks.IndexOf(pack);
            if (mapIndex < 0 || maps.NewId[maps.Canonical[mapIndex]] < 0)
            {
                File.Delete(file);
                continue;
            }

            parts[0] = final(maps, map).ToString();
            parts[1] = (packIndex < 0 || mapPacks.NewId[mapPacks.Canonical[packIndex]] < 0 ? -1 : final(mapPacks, pack)).ToString();
            File.WriteAllText(file, string.Join(' ', parts));
        }
    }
}
