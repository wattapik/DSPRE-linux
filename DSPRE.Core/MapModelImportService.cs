namespace DSPRE.Core;

/// <summary>Replaces a map's terrain model with one built from a Wavefront OBJ: the OBJ becomes an IMD
/// (<see cref="ObjToImd"/>), g3dcvtr converts that to NSBMD, and the NSBMD goes through the same
/// import as any other model file, so it is journaled and its textures are left to the map's texture pack.
/// A model over the size the game can load (it crashes) is simplified and converted again until it fits.</summary>
public static class MapModelImportService
{
    private const int MaxAttempts = 8;

    /// <param name="NsbmdPath">Where the converted model is kept: next to the OBJ, so the journal's
    /// record of the import can be replayed from it.</param>
    /// <param name="TriangleFraction">Share of the OBJ's triangles that was kept.</param>
    /// <param name="NsbtxPath">The model's own textures as an NSBTX, kept next to the OBJ; null when it has none.</param>
    /// <param name="TexturePackId">The map texture pack made from them, or -1 when none was made.</param>
    /// <param name="MapId">The map the model went into.</param>
    /// <param name="AreaDataId">The area data made for a new map, or -1.</param>
    /// <param name="MatrixId">The matrix made for a new map, or -1.</param>
    /// <param name="HeaderId">The header made for a new map, or -1.</param>
    /// <param name="Wired">Everything made and linked for a new map, or null for a replaced model.</param>
    public sealed record Result(int MapId, string NsbmdPath, int ModelSize, float TriangleFraction, bool FitsLimit, string? NsbtxPath, int TexturePackId,
        int AreaDataId = -1, int MatrixId = -1, int HeaderId = -1, NewMapService.Wired? Wired = null);

    /// <summary>Converts the OBJ to an NSBMD (and its textures to an NSBTX) next to it, without touching the
    /// project, so the result can be previewed and only <see cref="Apply"/>ed when the user saves.</summary>
    public static async Task<Converted> ConvertObjAsync(
        RomProject project, string objPath, string toolsDirectory, ObjToImd.Options? options = null, CancellationToken cancellationToken = default)
    {
        options ??= new ObjToImd.Options();
        string name = Path.GetFileNameWithoutExtension(objPath);
        string workDirectory = Path.Combine(project.RootPath, "expanded", "map_model_import");
        if (Directory.Exists(workDirectory)) Directory.Delete(workDirectory, true);
        Directory.CreateDirectory(workDirectory);
        string imdPath = Path.Combine(workDirectory, name + ".imd");
        string produced = Path.Combine(workDirectory, name + ".nsbmd");

        int size = 0;
        for (int attempt = 0; attempt < MaxAttempts; attempt++)
        {
            if (File.Exists(produced)) File.Delete(produced);
            await File.WriteAllTextAsync(imdPath, ObjToImd.Convert(objPath, options), cancellationToken);
            ToolResult result = await new RomExtractionService().ConvertImdAsync(imdPath, name + ".nsbmd", toolsDirectory, cancellationToken);
            string output = result.StandardError + result.StandardOutput;
            // g3dcvtr refuses outright, rather than writing an oversized model, past 16 bits of vertices.
            if (!result.Succeeded && options.FitModelSize && output.Contains("too big to be packed", StringComparison.Ordinal) && attempt < MaxAttempts - 1)
            {
                options = options with { TriangleFraction = Math.Max(options.TriangleFraction * 0.4f, 0.01f) };
                continue;
            }

            if (!result.Succeeded || !File.Exists(produced))
            {
                string details = string.Join("\n", new[] { result.StandardError, result.StandardOutput }.Where(text => !string.IsNullOrWhiteSpace(text)));
                throw new InvalidOperationException($"g3dcvtr could not convert the model{(details.Length > 0 ? ":\n" + details.Trim() : ".")}");
            }

            byte[] model = File.ReadAllBytes(produced);
            size = (NsbmdTextureTools.HasTextures(model) ? NsbmdTextureTools.WithoutTextures(model) : model).Length;
            if (!options.FitModelSize || size <= MapFileService.ModelCriticalSize) break;

            // Size follows the triangle count closely; aim a little under the limit.
            float shrink = MapFileService.ModelCriticalSize * 0.92f / size;
            options = options with { TriangleFraction = Math.Max(options.TriangleFraction * shrink, 0.01f) };
        }

        string kept = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(objPath))!, name + ".nsbmd");
        File.Copy(produced, kept, overwrite: true);

        // The map keeps only the model; its textures live in a separate pack, so the ones the model was
        // converted with are saved as an NSBTX and become a new map texture pack.
        string? nsbtxPath = null;
        byte[]? textures = NsbmdTextureTools.ExtractTextures(File.ReadAllBytes(produced));
        if (textures is not null)
        {
            nsbtxPath = Path.Combine(Path.GetDirectoryName(kept)!, name + ".nsbtx");
            File.WriteAllBytes(nsbtxPath, textures);
        }

        return new Converted(name, kept, size, options.TriangleFraction, size <= MapFileService.ModelCriticalSize, nsbtxPath);
    }

    public sealed record Converted(string Name, string NsbmdPath, int ModelSize, float TriangleFraction, bool FitsLimit, string? NsbtxPath);

    /// <summary>Puts a converted model into the project: into the given map, or into a new map with its own header, area data,
    /// matrix, script, level script, event file, text archive and wild encounter file (see <see cref="NewMapService"/>; a project
    /// that cannot add headers refuses, before anything is changed).</summary>
    public static Result Apply(RomProject project, RomGameMetadata metadata, int? existingMapId, Converted converted, string toolsDirectory)
    {
        if (existingMapId is null) NewMapService.RequireCanAddMaps(project, metadata);
        string name = converted.Name;
        string kept = converted.NsbmdPath;
        string? nsbtxPath = converted.NsbtxPath;
        int packId = -1;
        int mapId = existingMapId ?? MapFileService.Add(project, metadata);
        MapFileService.ImportSection(project, metadata, mapId, MapFileSection.Model, kept);
        if (nsbtxPath is not null)
        {
            packId = TexturePackService.Add(project, TexturePackKind.Map);
            TexturePackService.Import(project, TexturePackKind.Map, packId, nsbtxPath);
        }

        int areaDataId = -1, matrixId = -1, headerId = -1;
        NewMapService.Wired? wired = null;
        if (existingMapId is null)
        {
            wired = NewMapService.Wire(project, metadata, toolsDirectory, mapId, name, packId);
            (areaDataId, matrixId, headerId) = (wired.AreaDataId, wired.MatrixId, wired.HeaderId);
        }

        return new Result(mapId, kept, converted.ModelSize, converted.TriangleFraction, converted.FitsLimit, nsbtxPath, packId, areaDataId, matrixId, headerId, wired);
    }
}
