namespace DSPRE.Core;

/// <summary>A map being built in the Map Creator: its grid and its tileset, kept in a folder of the project's
/// expanded directory as <c>name.pdsmap</c> + <c>name.pdsts</c> + PNG textures, which Pokemon DS Map Studio opens too.
/// These are work files; the project only changes when a built model is applied to a map.</summary>
public sealed class MapCreatorDocument
{
    public string Name { get; }
    public string Folder { get; }
    public MapCreatorTileset Tileset { get; }
    public MapCreatorMap Map { get; }

    /// <summary>The project map (and its texture pack) this document was last built into as a new map, or -1; building again
    /// updates that map's model and pack in place instead of making another map.</summary>
    public int BuiltMapId { get; set; } = -1;
    public int BuiltPackId { get; set; } = -1;
    public int BuiltHeaderId { get; set; } = -1;

    private string BuiltPath => Path.Combine(Folder, "built.txt");

    public bool HasBuiltMap(RomProject project) =>
        BuiltMapId >= 0 && File.Exists(MapFileService.GetPath(project, BuiltMapId)) && (BuiltPackId < 0 || BuiltPackId < TexturePackService.Count(project, TexturePackKind.Map));

    public void RememberBuiltMap(int mapId, int packId, int headerId)
    {
        BuiltMapId = mapId;
        BuiltPackId = packId;
        BuiltHeaderId = headerId;
        File.WriteAllText(BuiltPath, $"{mapId} {packId} {headerId}");
    }

    /// <summary>Puts the built map into a cell of an existing matrix, with the header made for it: the game takes a cell's
    /// area data (and so its textures), music and so on from the matrix's header there, not from the map file.</summary>
    public void PlaceInMatrix(RomProject project, int matrixId, int column, int row)
    {
        if (BuiltMapId < 0) throw new InvalidOperationException("Build the map as a new map first.");
        MatrixDocument matrix = MatrixService.Load(project, matrixId);
        if (column < 0 || row < 0 || column >= matrix.Width || row >= matrix.Height) throw new ArgumentOutOfRangeException(nameof(column), $"Matrix {matrixId} is {matrix.Width} x {matrix.Height} cells.");
        matrix.Maps[row, column] = (ushort)BuiltMapId;
        if (matrix.HasHeadersSection && BuiltHeaderId >= 0) matrix.Headers[row, column] = (ushort)BuiltHeaderId;
        MatrixService.Save(project, matrix);
    }

    /// <summary>Puts a rebuilt model into the map this document made earlier, and its textures into that map's pack.</summary>
    public void UpdateBuiltMap(RomProject project, RomGameMetadata metadata, MapModelImportService.Converted converted)
    {
        MapFileService.ImportSection(project, metadata, BuiltMapId, MapFileSection.Model, converted.NsbmdPath);
        if (converted.NsbtxPath is not null && BuiltPackId >= 0) TexturePackService.Import(project, TexturePackKind.Map, BuiltPackId, converted.NsbtxPath);
    }

    public string MapPath => Path.Combine(Folder, Name + MapCreatorMap.FileExtension);
    public string TilesetPath => Path.Combine(Folder, Name + MapCreatorTileset.FileExtension);

    private MapCreatorDocument(string name, string folder, MapCreatorTileset tileset, MapCreatorMap map)
    {
        Name = name;
        Folder = folder;
        Tileset = tileset;
        Map = map;
        Map.TilesetFile = Name + MapCreatorTileset.FileExtension;
    }

    public static string RootDirectory(RomProject project) => Path.Combine(project.RootPath, "expanded", "map_creator");

    public static IReadOnlyList<string> List(RomProject project)
    {
        string root = RootDirectory(project);
        if (!Directory.Exists(root)) return [];
        return Directory.EnumerateDirectories(root)
            .Select(Path.GetFileName)
            .OfType<string>()
            .Where(name => File.Exists(Path.Combine(root, name, name + MapCreatorMap.FileExtension)))
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public static string SafeName(string name)
    {
        string cleaned = new(name.Trim().Select(c => char.IsLetterOrDigit(c) || c is '_' or '-' ? c : '_').ToArray());
        return cleaned.Length == 0 ? "map" : cleaned;
    }

    public static MapCreatorDocument Create(RomProject project, RomGameMetadata metadata, string name)
    {
        name = SafeName(name);
        string folder = Path.Combine(RootDirectory(project), name);
        if (Directory.Exists(folder)) throw new IOException($"A map named \"{name}\" already exists in the Map Creator.");
        Directory.CreateDirectory(folder);
        var map = new MapCreatorMap { GameIndex = GameIndexOf(metadata) };
        var document = new MapCreatorDocument(name, folder, new MapCreatorTileset(folder), map);
        document.Save();
        return document;
    }

    public static MapCreatorDocument Open(RomProject project, string name)
    {
        string folder = Path.Combine(RootDirectory(project), name);
        string mapPath = Path.Combine(folder, name + MapCreatorMap.FileExtension);
        MapCreatorMap map = MapCreatorMap.Read(mapPath);
        string tilesetPath = Path.Combine(folder, string.IsNullOrEmpty(map.TilesetFile) ? name + MapCreatorTileset.FileExtension : map.TilesetFile);
        MapCreatorTileset tileset = File.Exists(tilesetPath) ? MapCreatorTileset.Read(tilesetPath) : new MapCreatorTileset(folder);
        tileset.FolderPath = folder;
        var document = new MapCreatorDocument(name, folder, tileset, map);
        if (File.Exists(document.BuiltPath) && File.ReadAllText(document.BuiltPath).Split(' ') is [var built, var pack, ..] parts && int.TryParse(built, out int builtId) && int.TryParse(pack, out int packId))
        {
            document.BuiltMapId = builtId;
            document.BuiltPackId = packId;
            document.BuiltHeaderId = parts.Length > 2 && int.TryParse(parts[2], out int headerId) ? headerId : -1;
        }

        return document;
    }

    /// <summary>Copies a Pokemon DS Map Studio map (its first map, with its tileset and textures) into the Map Creator.</summary>
    public static MapCreatorDocument ImportPdsmap(RomProject project, string pdsmapPath, int mapNumber = 0)
    {
        MapCreatorMap map = MapCreatorMap.Read(pdsmapPath, mapNumber);
        string sourceFolder = Path.GetDirectoryName(Path.GetFullPath(pdsmapPath))!;
        string tilesetPath = Path.Combine(sourceFolder, map.TilesetFile);
        if (!File.Exists(tilesetPath)) throw new FileNotFoundException($"The map's tileset \"{map.TilesetFile}\" is not next to it.", tilesetPath);

        string name = SafeName(Path.GetFileNameWithoutExtension(pdsmapPath));
        string unique = name;
        for (int suffix = 2; Directory.Exists(Path.Combine(RootDirectory(project), unique)); suffix++) unique = $"{name}_{suffix}";
        string folder = Path.Combine(RootDirectory(project), unique);
        Directory.CreateDirectory(folder);

        var tileset = new MapCreatorTileset(folder);
        tileset.Merge(MapCreatorTileset.Read(tilesetPath));
        var document = new MapCreatorDocument(unique, folder, tileset, map);
        document.Save();
        return document;
    }

    public void Save()
    {
        Directory.CreateDirectory(Folder);
        Tileset.Write(TilesetPath);
        Map.TilesetFile = Name + MapCreatorTileset.FileExtension;
        Map.Write(MapPath);
    }

    public void Delete()
    {
        if (Directory.Exists(Folder)) Directory.Delete(Folder, true);
    }

    /// <summary>Removes the tiles the map does not use (and textures only they used), renumbering the grid to match.</summary>
    public int RemoveUnusedTiles()
    {
        var used = Map.TilesUsed().Where(index => index < Tileset.Tiles.Count).ToHashSet();
        var lookup = new int[Tileset.Tiles.Count];
        var kept = new List<MapTile>();
        for (int i = 0; i < Tileset.Tiles.Count; i++)
        {
            lookup[i] = used.Contains(i) ? kept.Count : -1;
            if (used.Contains(i)) kept.Add(Tileset.Tiles[i]);
        }

        int removed = Tileset.Tiles.Count - kept.Count;
        Map.Remap(index => index < lookup.Length ? lookup[index] : -1);
        Tileset.Tiles.Clear();
        Tileset.Tiles.AddRange(kept);
        return removed;
    }

    /// <summary>Builds the map's model: the OBJ is written to the document's build folder and converted to an NSBMD
    /// (and NSBTX) there, ready for <see cref="MapModelImportService.Apply"/>.</summary>
    public async Task<MapModelImportService.Converted> BuildAsync(RomProject project, string toolsDirectory, ObjToImd.Options options, CancellationToken cancellationToken = default)
    {
        string build = Path.Combine(Folder, "build");
        if (Directory.Exists(build)) Directory.Delete(build, true);
        MapCreatorComposer.Output output = MapCreatorComposer.WriteObj(Tileset, Map, build, Name);
        return await MapModelImportService.ConvertObjAsync(project, output.ObjPath, toolsDirectory, options, cancellationToken);
    }

    private static int GameIndexOf(RomGameMetadata metadata) => metadata.HeaderFormat switch
    {
        MapHeaderFormat.DiamondPearl => 0,
        MapHeaderFormat.Platinum => 2,
        _ => 3
    };
}
