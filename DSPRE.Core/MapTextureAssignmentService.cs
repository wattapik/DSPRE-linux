namespace DSPRE.Core;

/// <summary>Gives a map another map texture pack and/or texture animation without affecting any other map's:
/// they go into a new area data (a copy of the one the map has, with only those changed), and the header that gave the
/// map its area data is pointed at that copy. Leftover, unused area data is expected; merging them is left
/// to the user.</summary>
public static class MapTextureAssignmentService
{
    /// <param name="HeaderId">The header now using the new area data.</param>
    /// <param name="OtherMaps">Other maps placed by that same header (an overworld matrix's neighbours), which take the new area data too.</param>
    public sealed record Result(int AreaDataId, int HeaderId, IReadOnlyList<int> OtherMaps);

    /// <param name="packId">The map texture pack to use, or null to keep the area data's.</param>
    /// <param name="dynamicTextureType">The area data's dynamic textures value (<see cref="DynamicTextureService.None"/> for no animation), or null to keep it.</param>
    public static Result AssignAreaSettings(RomProject project, RomGameMetadata metadata, int mapId, int? packId, int? dynamicTextureType)
    {
        bool hgss = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;
        IMapHeaderStore store = MapHeaderStoreFactory.Create(project, metadata) ?? throw new InvalidOperationException("The project's headers could not be read.");
        ushort headerId = AreaDataService.FindHeaderForMap(project, store.Load, store.Count, mapId)
            ?? throw new InvalidOperationException($"No header places map {mapId:D4}, so it has no area data to give a texture pack.");
        MapHeaderRecord header = store.Load(headerId);

        AreaDataRecord current = AreaDataService.Load(project, header.AreaDataId, hgss);
        int newId = AreaDataService.Add(project);
        if (newId > byte.MaxValue) throw new InvalidOperationException("A header can only use area data 0-255, and they are all taken.");

        current.Id = newId;
        if (packId is int pack) current.MapTileset = (ushort)pack;
        if (dynamicTextureType is int dynamic) current.DynamicTextureType = (ushort)dynamic;
        AreaDataService.Save(project, current, hgss);
        store.Save(header with { AreaDataId = (byte)newId });

        IReadOnlyDictionary<int, AreaDataRecord> resolved = AreaDataService.ResolveMapAreaData(project, store.Load, store.Count, hgss);
        List<int> others = resolved.Where(entry => entry.Value.Id == newId && entry.Key != mapId).Select(entry => entry.Key).Order().ToList();
        return new Result(newId, headerId, others);
    }
}
