namespace DSPRE.Core;

/// <summary>Everything a new map needs to work in the game, made together and linked: its own area data (with the texture pack, when it
/// has one), a one-map matrix, a header that points at all of them, and the header's own text archive, script file, level script, event
/// file and wild encounter file. Without the Dynamic Header Allocation patch a header cannot be added, so neither can a map.</summary>
public static class NewMapService
{
    public const string RequirementMessage =
        "A new map needs its own header, and headers can only be added after the Dynamic Header Allocation patch (ROM Toolbox) has been applied to this project. " +
        "Apply the patch first, then add the map.";

    public sealed record Wired(int MapId, int AreaDataId, int MatrixId, int HeaderId, int TextArchiveId, int ScriptFileId, int LevelScriptId, int EventFileId, int EncounterFileId);

    public static bool CanAddMaps(RomProject project, RomGameMetadata metadata) => HeaderFileService.CanChangeHeaderCount(project, metadata);

    public static void RequireCanAddMaps(RomProject project, RomGameMetadata metadata)
    {
        if (!CanAddMaps(project, metadata)) throw new InvalidOperationException(RequirementMessage);
    }

    /// <summary>Makes and links everything for the map <paramref name="mapId"/> (which must already exist). <paramref name="texturePackId"/> is the
    /// map texture pack its area data uses, or -1 to leave the new area data's default.</summary>
    public static async Task<Wired> WireAsync(RomProject project, RomGameMetadata metadata, string toolsDirectory, int mapId, string name, int texturePackId, CancellationToken cancellationToken = default)
    {
        RequireCanAddMaps(project, metadata);
        bool hgss = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;

        int areaDataId = AreaDataService.Add(project);
        if (areaDataId > byte.MaxValue) throw new InvalidOperationException("A header can only name area data up to 255, and the project has no room for another.");
        if (texturePackId >= 0)
        {
            AreaDataRecord area = AreaDataService.Load(project, areaDataId, hgss);
            area.MapTileset = (ushort)texturePackId;
            AreaDataService.Save(project, area, hgss);
        }

        int matrixId = MatrixService.Add(project);
        MatrixDocument matrix = MatrixService.Load(project, matrixId);
        string matrixName = name.Length > 16 ? name[..16] : name;
        MatrixService.Save(project, new MatrixDocument(matrixId, matrix.Path, [], matrixName, false, false, new ushort[1, 1] { { (ushort)mapId } }, new ushort[1, 1], new byte[1, 1]));

        int headerId = HeaderFileService.Add(project, metadata);
        cancellationToken.ThrowIfCancellationRequested();
        HeaderFileService.SupportingFiles files = await HeaderFileService.CreateSupportingFilesAsync(project, metadata, toolsDirectory);
        int encounterId = WildEncounterService.AddBlank(project);

        // Made after the header exists, as a store only knows the headers present when it was made.
        IMapHeaderStore store = MapHeaderStoreFactory.Create(project, metadata)!;
        store.Save(store.Load((ushort)headerId) with
        {
            AreaDataId = (byte)areaDataId,
            MatrixId = (ushort)matrixId,
            TextArchiveId = (ushort)files.TextArchive,
            ScriptFileId = (ushort)files.ScriptFile,
            LevelScriptId = (ushort)files.LevelScript,
            EventFileId = (ushort)files.EventFile,
            WildPokemonId = (ushort)encounterId
        });
        project.SaveInternalName((ushort)headerId, name.Length > 16 ? name[..16] : name);
        return new Wired(mapId, areaDataId, matrixId, headerId, files.TextArchive, files.ScriptFile, files.LevelScript, files.EventFile, encounterId);
    }

    /// <summary>The same for callers that cannot await (it runs on the thread pool, so a UI thread waiting here does not deadlock).</summary>
    public static Wired Wire(RomProject project, RomGameMetadata metadata, string toolsDirectory, int mapId, string name, int texturePackId) =>
        Task.Run(() => WireAsync(project, metadata, toolsDirectory, mapId, name, texturePackId)).GetAwaiter().GetResult();
}
