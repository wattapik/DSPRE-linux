namespace DSPRE.Core;

/// <summary>A matrix is drawn with one map texture pack, the one its header's area data names, whichever maps it places. A map that
/// normally belongs to another area data (the border maps of a Map Creator map, say) looks for textures its own pack has and this one
/// lacks, and is drawn with the wrong picture. <see cref="Complete"/> adds those textures and their palettes, taken from the pack each
/// such map normally uses, to the matrix's pack.</summary>
public static class MatrixTexturePackService
{
    public sealed record Completion(int MatrixId, int PackId, IReadOnlyList<string> AddedTextures, IReadOnlyList<string> AddedPalettes, IReadOnlyList<int> MapsServed,
        IReadOnlyList<string> Conflicts, IReadOnlyList<string> StillMissing, string? MergedFile, int PackBytes);

    public static Completion Complete(RomProject project, RomGameMetadata metadata, int matrixId)
    {
        bool hgss = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;
        IMapHeaderStore store = MapHeaderStoreFactory.Create(project, metadata) ?? throw new InvalidOperationException("The project's headers could not be read.");
        ushort? owner = null;
        for (ushort id = 0; id < store.Count && owner is null; id++)
        {
            if (store.Load(id).MatrixId == matrixId) owner = id;
        }

        if (owner is null) throw new InvalidOperationException($"No header uses matrix {matrixId:D4}, so it has no texture pack to complete.");
        AreaDataRecord targetArea = AreaDataService.Load(project, store.Load(owner.Value).AreaDataId, hgss);
        int targetPack = targetArea.MapTileset;
        byte[] pack = File.ReadAllBytes(TexturePackService.GetPath(project, TexturePackKind.Map, targetPack));

        MatrixDocument matrix = MatrixService.Load(project, matrixId);
        var maps = new SortedSet<int>();
        for (int row = 0; row < matrix.Height; row++)
        {
            for (int column = 0; column < matrix.Width; column++)
            {
                if (matrix.Maps[row, column] != MatrixDocument.EmptyMap) maps.Add(matrix.Maps[row, column]);
            }
        }

        IReadOnlyDictionary<int, AreaDataRecord> areaOfMap = AreaDataService.ResolveMapAreaData(project, store.Load, store.Count, hgss);
        var added = new List<string>();
        var addedPalettes = new List<string>();
        var conflicts = new List<string>();
        var served = new List<int>();
        var stillMissing = new List<string>();
        foreach (int map in maps)
        {
            List<NsbmdMaterialPairs.Pair> pairs = NsbmdMaterialPairs.Read(MapBuildingService.LoadModelData(project, map));
            NsbtxMerger.Pack current = NsbtxMerger.Parse(pack);
            List<NsbmdMaterialPairs.Pair> missing = pairs.Where(pair => !current.TextureNames.Contains(pair.Texture) || !current.PaletteNames.Contains(pair.Palette)).ToList();
            if (missing.Count == 0) continue;

            if (!areaOfMap.TryGetValue(map, out AreaDataRecord? area) || area.MapTileset == targetPack)
            {
                stillMissing.AddRange(missing.Select(pair => $"{pair.Texture} (map {map:D4}, no other texture pack to take it from)"));
                continue;
            }

            byte[] source = File.ReadAllBytes(TexturePackService.GetPath(project, TexturePackKind.Map, area.MapTileset));
            NsbtxMerger.Result merged = NsbtxMerger.Add(pack, source, missing.Select(pair => pair.Texture), missing.Select(pair => pair.Palette));
            NsbtxMerger.Pack after = NsbtxMerger.Parse(merged.Data);
            stillMissing.AddRange(missing.Where(pair => !after.TextureNames.Contains(pair.Texture) || !after.PaletteNames.Contains(pair.Palette))
                .Select(pair => $"{pair.Texture} (map {map:D4}, not in its texture pack {area.MapTileset:D2} either)"));
            if (merged.AddedTextures.Count + merged.AddedPalettes.Count > 0) served.Add(map);
            added.AddRange(merged.AddedTextures);
            addedPalettes.AddRange(merged.AddedPalettes);
            conflicts.AddRange(merged.Conflicts);
            pack = merged.Data;
        }

        string? file = null;
        if (added.Count + addedPalettes.Count > 0)
        {
            string directory = Path.Combine(project.RootPath, "expanded", "texture_merges");
            Directory.CreateDirectory(directory);
            file = Path.Combine(directory, $"pack{targetPack:D2}_matrix{matrixId:D4}.nsbtx");
            File.WriteAllBytes(file, pack);
            TexturePackService.Import(project, TexturePackKind.Map, targetPack, file);
        }

        return new Completion(matrixId, targetPack, added, addedPalettes, served, conflicts, stillMissing.Distinct().ToList(), file, pack.Length);
    }
}
