using DSPRE;
using DSPRE.ROMFiles;
using LibNDSFormats.NSBMD;
using LibNDSFormats.NSBTX;
using NSMBe4.NSBMD;
using System.Drawing;
using System.Drawing.Imaging;
using System.Text.Json;

string[] validCommands = ["rebuild", "export", "export-map", "export-maps", "list-valid-buildings", "export-overworld-sprite", "export-overworld-sprites", "export-building", "export-buildings", "toolbox-status", "toolbox-apply", "create-script", "convert-model", "export-script-docs"];
if (args.Length < 3 || !validCommands.Any(command => args[0].Equals(command, StringComparison.OrdinalIgnoreCase)))
{
    Console.Error.WriteLine("Usage: DSPRE.ScriptTool.exe rebuild|export <game-code> <project-directory> | export-map <game-code> <project-directory> <map-id> <output-directory> [texture-pack-id] [building-texture-pack-id] [map-file] [--interior-buildings] | export-maps <game-code> <project-directory> <jobs-file: lines of map-id output-directory texture-pack-id building-texture-pack-id interior(0|1)> | list-valid-buildings <game-code> <project-directory> <interior:true|false> [building-texture-pack-id] | export-overworld-sprite <game-code> <project-directory> <overlay-table-entry> <orientation> <output-png-path> | export-overworld-sprites <game-code> <project-directory> <output-directory> <entry:orientation,entry:orientation,...> | export-building <game-code> <project-directory> <interior:true|false> <model-id> <output-directory> [building-texture-pack-id] | export-buildings <game-code> <project-directory> <interior:true|false> <output-root> <output-subfolder> <building-texture-pack-id> <model-id,model-id,...> | toolbox-status <game-code> <project-directory> | create-script <game-code> <project-directory> <script-id> | convert-model <game-code> <project-directory> <nsbmd-path> <output-directory> <dae|glb> | toolbox-apply <game-code> <project-directory> <patch-id> [synthetic-overlay-offset-hex] | export-script-docs <game-code> <project-directory> <output-directory>");
    return 2;
}

try
{
    string gameCode = args[1];
    string projectDirectory = Path.GetFullPath(args[2]);
    AppLogger.Initialize(null);
    SettingsManager.Load();
    DSPRE.Program.SetupDatabase();
    _ = new RomInfo(gameCode, projectDirectory);
    bool success;
    if (args[0].Equals("export-map", StringComparison.OrdinalIgnoreCase))
    {
        // HGSS indoor maps place models from the interior building set (see ExportMapScene).
        bool interiorBuildings = args.Contains("--interior-buildings");
        const string reusePrefix = "--reuse-from=";
        string? reuseDirectory = args.FirstOrDefault(arg => arg.StartsWith(reusePrefix, StringComparison.Ordinal))?[reusePrefix.Length..];
        args = args.Where(arg => arg != "--interior-buildings" && !arg.StartsWith(reusePrefix, StringComparison.Ordinal)).ToArray();
        if (args.Length < 5 || !int.TryParse(args[3], out int mapId)) return 2;
        int texturePackId = args.Length >= 6 && int.TryParse(args[5], out int requestedPack)
            ? requestedPack
            : (RomInfo.gameFamily == RomInfo.GameFamilies.HGSS ? 2 : 6);
        int buildingTexturePackId = args.Length >= 7 && int.TryParse(args[6], out int requestedBuildingPack)
            ? requestedBuildingPack
            : 0;
        // An optional map file to render instead of the project's own copy: the editor's preview of
        // building edits that are not saved yet.
        string? mapFilePath = args.Length >= 8 ? Path.GetFullPath(args[7]) : null;
        success = ExportMapScene(mapId, Path.GetFullPath(args[4]), texturePackId, buildingTexturePackId, interiorBuildings, mapFilePath, reuseDirectory);
    }
    else if (args[0].Equals("export-maps", StringComparison.OrdinalIgnoreCase))
    {
        // One launch for many maps: starting this bridge under wine costs several times what converting
        // one map does. Fields are tab-separated, since the output path may contain spaces.
        if (args.Length < 4) return 2;
        success = true;
        foreach (string line in File.ReadAllLines(args[3]).Where(line => line.Trim().Length > 0))
        {
            string[] fields = line.Split('\t');
            if (fields.Length != 5 || !int.TryParse(fields[0], out int mapId) || !int.TryParse(fields[2], out int texturePackId) ||
                !int.TryParse(fields[3], out int buildingTexturePackId)) return 2;
            bool converted;
            try
            {
                converted = ExportMapScene(mapId, Path.GetFullPath(fields[1]), texturePackId, buildingTexturePackId, fields[4] == "1");
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"Map {mapId}: {ex.Message}");
                converted = false;
            }

            Console.WriteLine($"{(converted ? "converted" : "failed")} {mapId}");
            success &= converted;
        }
    }
    else if (args[0].Equals("list-valid-buildings", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 4 || !bool.TryParse(args[3], out bool interior)) return 2;
        int buildingTexturePackId = args.Length >= 5 && int.TryParse(args[4], out int requestedBuildingPack)
            ? requestedBuildingPack
            : -1;
        success = ListValidBuildingModels(interior, buildingTexturePackId);
    }
    else if (args[0].Equals("export-building", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 6 || !bool.TryParse(args[3], out bool interior) || !int.TryParse(args[4], out int modelId)) return 2;
        int buildingTexturePackId = args.Length >= 7 && int.TryParse(args[6], out int requestedBuildingPack)
            ? requestedBuildingPack
            : -1;
        success = ExportBuildingScene(interior, modelId, Path.GetFullPath(args[5]), buildingTexturePackId);
    }
    else if (args[0].Equals("export-buildings", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 8 || !bool.TryParse(args[3], out bool interior) || !int.TryParse(args[6], out int buildingTexturePackId)) return 2;
        int[] modelIds = args[7].Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray();
        success = ExportBuildingScenes(interior, modelIds, Path.GetFullPath(args[4]), args[5], buildingTexturePackId);
    }
    else if (args[0].Equals("export-overworld-sprite", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 6 || !int.TryParse(args[3], out int overlayTableEntry) || !int.TryParse(args[4], out int orientation)) return 2;
        success = ExportOverworldSprite(overlayTableEntry, orientation, Path.GetFullPath(args[5]));
    }
    else if (args[0].Equals("convert-model", StringComparison.OrdinalIgnoreCase))
    {
        // Legacy ModelUtils.ModelToDAE/ModelToGLB: apicula's own conversion of one model, DAE by default.
        if (args.Length < 6) return 2;
        string apicula = Path.Combine(AppContext.BaseDirectory, "..", "apicula.exe");
        string format = args[5].Equals("glb", StringComparison.OrdinalIgnoreCase) ? " -f glb" : string.Empty;
        var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = apicula,
            Arguments = $"convert \"{Path.GetFullPath(args[3])}\"{format} --overwrite --output \"{Path.GetFullPath(args[4])}\"",
            WorkingDirectory = Path.GetDirectoryName(apicula) ?? Environment.CurrentDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true
        })!;
        string errors = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0) Console.Error.WriteLine(errors);
        success = process.ExitCode == 0;
    }
    else if (args[0].Equals("create-script", StringComparison.OrdinalIgnoreCase))
    {
        // A new script file with one empty script, as legacy ScriptEditor.AddScriptFile makes it: binary
        // and plaintext both.
        if (args.Length < 4 || !int.TryParse(args[3], out int scriptId)) return 2;
        DSUtils.TryUnpackNarcs([RomInfo.DirNames.scripts]);
        var script = new ScriptFile(scriptLines: ["Script 1:", "End"], functionLines: null, actionLines: null, scriptId);
        success = script.SaveToFileDefaultDir(scriptId, showSuccessMessage: false);
    }
    else if (args[0].Equals("export-script-docs", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 4) return 2;
        success = ExportScriptDocs(Path.GetFullPath(args[3]));
    }
    else if (args[0].Equals("toolbox-status", StringComparison.OrdinalIgnoreCase))
    {
        success = RomToolbox.PrintStatus();
    }
    else if (args[0].Equals("toolbox-apply", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 4) return 2;
        uint? offset = args.Length >= 5 ? Convert.ToUInt32(args[4], 16) : null;
        try
        {
            success = RomToolbox.Apply(args[3], offset);
        }
        catch (InvalidOperationException ex)
        {
            // A refusal (already applied, prerequisite missing) rather than a failure part-way through.
            Console.WriteLine($"ERROR|{ex.Message}");
            return 1;
        }
    }
    else if (args[0].Equals("export-overworld-sprites", StringComparison.OrdinalIgnoreCase))
    {
        if (args.Length < 5) return 2;
        var requests = new List<(int Entry, int Orientation)>();
        foreach (string pair in args[4].Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] parts = pair.Split(':');
            if (parts.Length != 2 || !int.TryParse(parts[0], out int entry) || !int.TryParse(parts[1], out int orientation)) return 2;
            requests.Add((entry, orientation));
        }

        success = ExportOverworldSprites(requests, Path.GetFullPath(args[3]));
    }
    else
    {
        success = args[0].Equals("export", StringComparison.OrdinalIgnoreCase)
            ? ScriptFile.ExportAllScripts(suppressErrors: true)
            : ScriptFile.BuildRequiredBins();
    }
    return success ? 0 : 1;
}
catch (Exception ex)
{
    Console.Error.WriteLine(ex);
    return 1;
}

// A rendered map is the terrain model plus zero or more "Building" instances placed on top of it
// (see the DS Pokemon Hacking Wiki's mapping guide) - each Building record only stores a model ID,
// position, rotation and per-instance scale, so its own NSBMD has to be located, textured and
// converted to glTF independently of the terrain, then the placement recorded in a manifest for the
// viewer to assemble. This mirrors DS_Map/Rendering/LegacyMap3DRenderer.cs, which is the legacy
// WinForms Map Editor's OpenGL renderer and the reference this transform math was ported from.
/// <summary>Legacy DocTool.ExportScriptsToDocs: each script file with scripts, as plaintext of its
/// scripts and functions only, into &lt;output&gt;/NNNN.txt. Level scripts and empty files are skipped.</summary>
static bool ExportScriptDocs(string outputDirectory)
{
    Directory.CreateDirectory(outputDirectory);
    DSUtils.TryUnpackNarcs([RomInfo.DirNames.scripts, RomInfo.DirNames.textArchives]);
    DSPRE.Resources.ScriptDatabase.InitializePokemonNames();
    DSPRE.Resources.ScriptDatabase.InitializeItemNames();
    DSPRE.Resources.ScriptDatabase.InitializeMoveNames();
    DSPRE.Resources.ScriptDatabase.InitializeTrainerNames();
    int exported = 0;
    int failed = 0;
    for (int i = 0; i < Filesystem.GetScriptCount(); i++)
    {
        try
        {
            var file = new ScriptFile(i, readFunctions: true, readActions: false);
            if (file.isLevelScript || file.hasNoScripts) continue;
            file.WritePlainTextFile(Path.Combine(outputDirectory, $"{i:D4}.txt"), includeActions: false);
            exported++;
        }
        catch (Exception ex)
        {
            failed++;
            Console.Error.WriteLine($"Script {i:D4}: {ex.Message}");
        }
    }

    Console.WriteLine($"EXPORTED|{exported}|{failed}");
    return true;
}

static bool ExportMapScene(int mapId, string outputDirectory, int texturePackId, int buildingTexturePackId, bool interiorBuildings, string? mapFilePath = null, string? reuseDirectory = null)
{
    outputDirectory = Path.GetFullPath(outputDirectory);
    Directory.CreateDirectory(outputDirectory);

    // Unlike the maps/matrices/scripts NARCs, DSPRE.Core's extraction pipeline never unpacks the
    // building model NARCs - only the legacy WinForms Map Editor did, on demand, when its own tab
    // was opened (DS_Map/Editors/MapEditor.cs, SetupMapEditor). A ROM project opened purely through
    // the new Avalonia app therefore has no unpacked/exteriorBuildingModels directory at all, so
    // every building below would fail to resolve. TryUnpackNarcs is idempotent (skips a directory
    // that already has files), so it is safe to call unconditionally here.
    DSUtils.TryUnpackNarcs([RomInfo.DirNames.exteriorBuildingModels, RomInfo.DirNames.buildingTextures]);
    if (RomInfo.gameFamily == RomInfo.GameFamilies.HGSS)
    {
        DSUtils.TryUnpackNarcs([RomInfo.DirNames.interiorBuildingModels]);
    }

    var map = mapFilePath is null
        ? new MapFile(mapId, RomInfo.gameFamily, discardMoveperms: true, showMessages: false)
        : new MapFile(mapFilePath, RomInfo.gameFamily, discardMoveperms: true, showMessages: false);
    if (map.mapModelData is null || map.mapModelData.Length == 0)
    {
        return false;
    }

    byte[] terrainData = map.mapModelData;
    if (texturePackId >= 0)
    {
        string texturePath = Path.Combine(RomInfo.gameDirs[RomInfo.DirNames.mapTextures].unpackedDir, texturePackId.ToString("D4"));
        if (File.Exists(texturePath))
        {
            terrainData = NSBUtils.BuildNSBMDwithTextures(terrainData, File.ReadAllBytes(texturePath));
        }
        else
        {
            Console.Error.WriteLine($"Map texture pack {texturePackId:D4} was not found at '{texturePath}'. Exporting untextured model.");
        }
    }


    // NSBUtils.BuildNSBMDwithTextures does not validate that a model's materials actually resolve
    // against the texture pack it is combined with - it just concatenates the two blocks - so using
    // the wrong pack silently produces garbled colors on whichever materials don't happen to match
    // by name (a partial, "vaguely right but with random wrong-colored lines" result), rather than
    // an outright error. buildingTexturePackId is resolved by the caller per-map from AreaData's
    // BuildingsTileset field, the same way the terrain's own texture pack already is.
    byte[]? buildingTextureBytes = null;
    if (buildingTexturePackId >= 0)
    {
        string buildingTexturePath = Filesystem.GetBuildingTexturePath(buildingTexturePackId);
        if (File.Exists(buildingTexturePath))
        {
            buildingTextureBytes = File.ReadAllBytes(buildingTexturePath);
        }
        else
        {
            Console.Error.WriteLine($"Building texture pack {buildingTexturePackId:D4} was not found at '{buildingTexturePath}'. Exporting untextured buildings.");
        }
    }

    // Each model is one apicula run, and apicula start-up dominates the export, so the terrain and
    // every distinct building convert side by side. Models that fall back to dmybox00 are resolved
    // first so the same output folder is never written by two conversions at once.
    var effectiveModels = new Dictionary<uint, (uint Id, byte[] Data)?>();
    foreach (Building building in map.buildings ?? [])
    {
        if (!effectiveModels.ContainsKey(building.modelID))
        {
            effectiveModels[building.modelID] = PrepareBuildingModel(building.modelID, buildingTextureBytes, interiorBuildings);
        }
    }

    var conversions = new List<(string Name, byte[] Data)> { ("terrain", terrainData) };
    conversions.AddRange(effectiveModels.Values
        .Where(model => model is not null)
        .Select(model => model!.Value)
        .DistinctBy(model => model.Id)
        .Select(model => ($"building_{model.Id:D4}", model.Data)));
    var converted = new System.Collections.Concurrent.ConcurrentDictionary<string, string?>();
    Parallel.ForEach(conversions, new ParallelOptions { MaxDegreeOfParallelism = 4 }, conversion =>
    {
        if (reuseDirectory is not null &&
            (TryReuseConvertedModel(outputDirectory, outputDirectory, conversion.Name, out string? reusedPath) ||
             TryReuseConvertedModel(reuseDirectory, outputDirectory, conversion.Name, out reusedPath)))
        {
            converted[conversion.Name] = reusedPath;
        }
        else if (ConvertNsbmdToGlb(conversion.Data, outputDirectory, conversion.Name, out string? relativePath, out string? error))
        {
            converted[conversion.Name] = relativePath;
        }
        else
        {
            Console.Error.WriteLine(conversion.Name == "terrain" ? error : $"{conversion.Name} failed to convert: {error}");
            converted[conversion.Name] = null;
        }
    });

    if (converted.GetValueOrDefault("terrain") is not string terrainRelativePath)
    {
        return false;
    }

    var manifest = new SceneManifest { Terrain = terrainRelativePath };
    foreach (Building building in map.buildings ?? [])
    {
        // A building that cannot be drawn keeps an empty slot, so part N is always the map's building N:
        // the editor moves, selects and highlights parts by building index.
        if (effectiveModels[building.modelID] is not { } model || converted.GetValueOrDefault($"building_{model.Id:D4}") is not string relativePath)
        {
            manifest.Parts.Add(new ScenePartManifest());
            continue;
        }

        // Position is stored as a whole grid-cell part plus a 1/65536th fraction. Grid cells are 16
        // world units, empirically confirmed against test/TRAGEDY_DSPRE_contents (Twinleaf Town):
        // terrain.glb's own vertex bounds span ~552 units across the documented 32-cell-wide grid
        // (552/32 ~= 17.25, i.e. ~16 after accounting for terrain slightly overhanging the collision
        // grid's edge cells), and a *16 scale places this map's two starter-house buildings at a
        // symmetric +-88 units - consistent with their placement being mirrored across the town's
        // central path. (An earlier version of this divided by 4 instead, algebraically derived from
        // the legacy OpenGL renderer's own glScalef/glTranslatef calls in LegacyMap3DRenderer.cs; that
        // derivation assumed apicula bakes each model's NSBMD modelScale the same way that fixed-
        // function pipeline does, which turned out not to hold - it produced positions clustered
        // within +-1.5 units of each other against a 552-unit terrain, which is what was reported as
        // "every building loads on top of each other in the center".)
        float fullX = building.xPosition + building.xFraction / 65536f;
        float fullY = building.yPosition + building.yFraction / 65536f;
        float fullZ = building.zPosition + building.zFraction / 65536f;
        const float unitsPerGridCell = 16f;

        // width/height/length default to 16, which is the neutral "don't resize the model" value.
        manifest.Parts.Add(new ScenePartManifest
        {
            Model = relativePath,
            Position = [fullX * unitsPerGridCell, fullY * unitsPerGridCell, fullZ * unitsPerGridCell],
            RotationDegrees = [Building.U16ToDeg(building.xRotation), Building.U16ToDeg(building.yRotation), Building.U16ToDeg(building.zRotation)],
            Scale = [building.width / 16f, building.height / 16f, building.length / 16f]
        });
    }

    File.WriteAllText(
        Path.Combine(outputDirectory, "scene.json"),
        JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));
    return true;
}

// An unsaved-building preview only changes which building models are placed, so every model an earlier
// export of the same map (same texture packs) already converted is copied across instead of re-run
// through apicula - which is what made adding one building re-convert the whole map.
static bool TryReuseConvertedModel(string reuseDirectory, string outputDirectory, string modelName, out string? relativeGlbPath)
{
    relativeGlbPath = null;
    string source = Path.Combine(reuseDirectory, modelName);
    string? glb = Directory.Exists(source) ? Directory.GetFiles(source, "*.glb").FirstOrDefault() : null;
    if (glb is null) return false;

    string destination = Path.Combine(outputDirectory, modelName);
    if (!string.Equals(Path.GetFullPath(source), Path.GetFullPath(destination), StringComparison.OrdinalIgnoreCase))
    {
        Directory.CreateDirectory(destination);
        foreach (string file in Directory.GetFiles(source))
        {
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        }
    }

    relativeGlbPath = $"{modelName}/{Path.GetFileName(glb)}";
    return true;
}

// Prints one building model ID per line (to stdout) for every model in the interior/exterior set that
// would actually render as itself with buildingTexturePackId assigned - i.e. every model
// ModelHasMatchingTexture accepts, the same check ExportMapScene uses to decide whether to fall back
// to dmybox00. buildingTexturePackId < 0 means "no pack selected", in which case every model is
// printed (untextured rendering never substitutes dmybox00 - see PrepareBuildingModel).
static bool ListValidBuildingModels(bool interior, int buildingTexturePackId)
{
    DSUtils.TryUnpackNarcs([interior ? RomInfo.DirNames.interiorBuildingModels : RomInfo.DirNames.exteriorBuildingModels, RomInfo.DirNames.buildingTextures]);

    string modelsDir = Filesystem.GetBuildingModelsDirPath(interior);
    if (string.IsNullOrWhiteSpace(modelsDir) || !Directory.Exists(modelsDir))
    {
        // Not an error: DP/Pt has no interior building model set at all, for instance.
        return true;
    }

    byte[]? buildingTextureBytes = null;
    if (buildingTexturePackId >= 0)
    {
        string buildingTexturePath = Filesystem.GetBuildingTexturePath(buildingTexturePackId);
        if (File.Exists(buildingTexturePath))
        {
            buildingTextureBytes = File.ReadAllBytes(buildingTexturePath);
        }
        else
        {
            Console.Error.WriteLine($"Building texture pack {buildingTexturePackId:D4} was not found at '{buildingTexturePath}'; treating every model as valid.");
        }
    }

    int count = Directory.GetFiles(modelsDir).Length;
    for (int id = 0; id < count; id++)
    {
        string path = Filesystem.GetBuildingModelPath(interior, id);
        if (!File.Exists(path))
        {
            continue;
        }

        bool valid = buildingTextureBytes is null || ModelHasMatchingTexture(File.ReadAllBytes(path), buildingTextureBytes);
        if (valid)
        {
            // NSBMDLoader.LoadNSBMD (invoked inside ModelHasMatchingTexture) writes its own verbose
            // debug logging straight to stdout for every model it parses, so a bare "Console.WriteLine
            // (id)" would be unrecoverably mixed in with that noise. This prefix lets the caller pick
            // just this tool's actual output back out with a simple StartsWith filter.
            Console.WriteLine($"VALIDMODEL:{id}");
        }
    }

    return true;
}

// Many building previews in one process: starting this tool (under Wine, on Linux) costs far more
// than converting a model does, so the palette's thumbnails are converted in batches rather than one
// launch each. Each model lands in <outputRoot>/<id:D4>/<outputSubfolder>. A model that fails is
// reported and skipped; the caller sees which ones worked by which scene.json files exist.
static bool ExportBuildingScenes(bool interior, int[] modelIds, string outputRoot, string outputSubfolder, int buildingTexturePackId)
{
    DSUtils.TryUnpackNarcs([interior ? RomInfo.DirNames.interiorBuildingModels : RomInfo.DirNames.exteriorBuildingModels, RomInfo.DirNames.buildingTextures]);
    Parallel.ForEach(modelIds, new ParallelOptions { MaxDegreeOfParallelism = 4 }, modelId =>
    {
        try
        {
            ExportBuildingScene(interior, modelId, Path.Combine(outputRoot, modelId.ToString("D4"), outputSubfolder), buildingTexturePackId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Building model {modelId} failed to convert: {ex.Message}");
        }
    });
    return true;
}

// The Building Editor's preview: one building model on its own, written as a scene manifest whose
// "terrain" is the model itself so the viewer needs no separate single-model path. Unlike
// PrepareBuildingModel this never substitutes dmybox00 for a model the pack doesn't match - the
// editor is showing this specific file, so a mismatched pack should visibly render untextured.
static bool ExportBuildingScene(bool interior, int modelId, string outputDirectory, int buildingTexturePackId)
{
    Directory.CreateDirectory(outputDirectory);
    DSUtils.TryUnpackNarcs([interior ? RomInfo.DirNames.interiorBuildingModels : RomInfo.DirNames.exteriorBuildingModels, RomInfo.DirNames.buildingTextures]);

    string modelPath = Filesystem.GetBuildingModelPath(interior, modelId);
    if (!File.Exists(modelPath))
    {
        Console.Error.WriteLine($"Building model {modelId} was not found at '{modelPath}'.");
        return false;
    }

    byte[] buildingData = File.ReadAllBytes(modelPath);
    if (buildingTexturePackId >= 0)
    {
        string buildingTexturePath = Filesystem.GetBuildingTexturePath(buildingTexturePackId);
        // Same guard as PrepareBuildingModel: attaching a second TEX0 block to a model that
        // already embeds one corrupts the NSBMD header's block count.
        bool alreadyTextured = buildingData.Length > 0xF && BitConverter.ToInt16(buildingData, 0xE) > 1;
        if (!File.Exists(buildingTexturePath))
        {
            Console.Error.WriteLine($"Building texture pack {buildingTexturePackId:D4} was not found at '{buildingTexturePath}'. Exporting the model as stored.");
        }
        else if (!alreadyTextured)
        {
            buildingData = NSBUtils.BuildNSBMDwithTextures(buildingData, File.ReadAllBytes(buildingTexturePath));
        }
    }

    if (!ConvertNsbmdToGlb(buildingData, outputDirectory, "model", out string? relativePath, out string? error))
    {
        Console.Error.WriteLine(error);
        return false;
    }

    // Written under a temporary name first: the palette polls for scene.json while a batch runs and
    // must never see it half-written.
    string scenePath = Path.Combine(outputDirectory, "scene.json");
    File.WriteAllText(
        scenePath + ".tmp",
        JsonSerializer.Serialize(new SceneManifest { Terrain = relativePath! }, new JsonSerializerOptions { WriteIndented = true }));
    File.Move(scenePath + ".tmp", scenePath, overwrite: true);
    return true;
}

// Exports the same in-game overworld sprite image DS_Map/Editors/EventEditor.cs's GetOverworldImage
// renders into its 2D Event Editor, headlessly, as a standalone PNG DSPRE.Avalonia can load as a GL
// texture. RomInfo.OverworldTable/ow3DSpriteDict are never populated by the bootstrap at the top of
// this file (only EventEditor.cs and DocTool.cs load them, on demand, for the same reason) so this
// loads them itself first, in the same order EventEditor.cs's own OnLoad does (SetOWtable populates
// where the raw sprite table lives in the ROM before ReadOWTable can parse it out).
static bool ExportOverworldSprite(int overlayTableEntry, int orientation, string outputPngPath)
{
    LoadOverworldTables();
    string? fallbackIcon = WriteOverworldSprite(overlayTableEntry, orientation, outputPngPath);
    if (fallbackIcon is not null) Console.WriteLine($"ICON:{fallbackIcon}");
    return true;
}

// Every sprite a map needs in one run: starting the bridge under Wine costs far more than exporting a
// sprite, so one call per sprite made a map's events trickle in one by one. Each sprite is written as
// <entry:D4>_<orientation>.png, and a sprite with no flat image prints "ICON:<entry>:<orientation>:<name>".
static bool ExportOverworldSprites(IReadOnlyList<(int Entry, int Orientation)> requests, string outputDirectory)
{
    LoadOverworldTables();
    foreach ((int entry, int orientation) in requests)
    {
        string? fallbackIcon = WriteOverworldSprite(entry, orientation, Path.Combine(outputDirectory, $"{entry:D4}_{orientation}.png"));
        if (fallbackIcon is not null) Console.WriteLine($"ICON:{entry}:{orientation}:{fallbackIcon}");
    }

    return true;
}

const int HgssFriendEntry = 101;
const int HgssLyraEntry = 97;

static void LoadOverworldTables()
{
    DSUtils.TryUnpackNarcs([RomInfo.DirNames.OWSprites]);
    RomInfo.SetOWtable();
    RomInfo.Set3DOverworldsDict();
    RomInfo.ReadOWTable();
}

/// <summary>Writes the sprite's PNG, or returns the name of the bundled icon to show instead.</summary>
static string? WriteOverworldSprite(int overlayTableEntry, int orientation, string outputPngPath)
{
    string? outputDirectory = Path.GetDirectoryName(outputPngPath);
    if (!string.IsNullOrEmpty(outputDirectory)) Directory.CreateDirectory(outputDirectory);

    // A handful of overlayTableEntry IDs (91-96, 101-116) are 3D in-engine models with no flat sprite
    // at all (see RomInfo.Set3DOverworldsDict) - EventEditor.cs substitutes one of a handful of bundled
    // PNGs (sign variants, or a generic bounding box) for these instead of attempting an NSBTX read.
    // Those PNGs live as DSPRE.Avalonia Content assets already (Resources/Event Icons), not anywhere
    // this headless tool can reach (DSPRE.Properties.Resources is internal to DS_Map's own assembly) -
    // so this reports the icon's *name* instead of writing outputPngPath, and the caller
    // (RomExtractionService.ExportOverworldSprite(s)Async) resolves that name against its own bundled copy.
    if (RomInfo.gameFamily == RomInfo.GameFamilies.HGSS)
    {
        // ow3DSpriteDict's numbers are Diamond/Pearl/Platinum's. In HGSS, 101 is the friend whose look
        // follows the player's gender (Lyra or Ethan) and has no table entry; it is drawn as Lyra (97),
        // the friend when playing as Ethan, as Platinum's is drawn as Dawn.
        if (overlayTableEntry == HgssFriendEntry) overlayTableEntry = HgssLyraEntry;
    }
    else if (RomInfo.ow3DSpriteDict.TryGetValue((uint)overlayTableEntry, out string? namedIcon))
    {
        return namedIcon;
    }

    if (!RomInfo.OverworldTable.TryGetValue((uint)overlayTableEntry, out (uint spriteID, ushort properties) entry))
    {
        return "overworld";
    }

    try
    {
        using FileStream stream = new(
            Path.Combine(RomInfo.gameDirs[RomInfo.DirNames.OWSprites].unpackedDir, entry.spriteID.ToString("D4")),
            FileMode.Open);
        var nsbtx = new NSBTX_File(stream);
        using Bitmap sprite = PickOverworldFrame(nsbtx, orientation);
        sprite.Save(outputPngPath, ImageFormat.Png);
        return null;
    }
    catch
    {
        // Sprite ID resolved but the NSBTX file itself couldn't be read/decoded - same fallback
        // EventEditor.cs's own catch block uses.
        return "overworldUnreadable";
    }
}

// Ported 1:1 from DS_Map/Editors/EventEditor.cs's GetOverworldImage (the NSBTX-reading half only -
// the ow3DSpriteDict/OverworldTable lookups happen in WriteOverworldSprite above instead, since their
// fallback is now a named bundled icon rather than a loaded Bitmap). The frame-slot picked per
// orientation is not a documented format - these are the exact slot indices legacy's own renderer uses
// for each observed sprite-sheet frame count (2/4/8/16/32ish frames), reverse-engineered by whoever
// wrote EventEditor.cs originally; kept unchanged rather than re-derived.
static Bitmap PickOverworldFrame(NSBTX_File nsbtx, int orientation)
{
    if (nsbtx.texInfo.num_objs <= 1) return nsbtx.GetBitmap(0, 0).bmp;
    if (nsbtx.texInfo.num_objs <= 4)
    {
        return orientation switch
        {
            0 => nsbtx.GetBitmap(0, 0).bmp,
            1 => nsbtx.GetBitmap(1, 0).bmp,
            2 => nsbtx.GetBitmap(2, 0).bmp,
            _ => nsbtx.GetBitmap(3, 0).bmp
        };
    }
    if (nsbtx.texInfo.num_objs <= 8)
    {
        return orientation switch
        {
            0 => nsbtx.GetBitmap(0, 0).bmp,
            1 => nsbtx.GetBitmap(2, 0).bmp,
            2 => nsbtx.GetBitmap(4, 0).bmp,
            _ => nsbtx.GetBitmap(6, 0).bmp
        };
    }
    if (nsbtx.texInfo.num_objs <= 16)
    {
        return orientation switch
        {
            0 => nsbtx.GetBitmap(0, 0).bmp,
            1 => nsbtx.GetBitmap(11, 0).bmp,
            2 => nsbtx.GetBitmap(2, 0).bmp,
            _ => nsbtx.GetBitmap(4, 0).bmp
        };
    }
    return orientation switch
    {
        0 => nsbtx.GetBitmap(0, 0).bmp,
        1 => nsbtx.GetBitmap(27, 0).bmp,
        2 => nsbtx.GetBitmap(2, 0).bmp,
        _ => nsbtx.GetBitmap(4, 0).bmp
    };
}

static (uint Id, byte[] Data)? PrepareBuildingModel(uint modelID, byte[]? buildingTextureBytes, bool interior)
{
    byte[]? buildingData = LoadBuildingModelData(modelID, interior);
    if (buildingData is null)
    {
        Console.Error.WriteLine($"Building model {modelID} could not be found in the {(interior ? "interior" : "exterior")} building models directory; omitting it from the scene.");
        return null;
    }

    if (buildingTextureBytes is not null)
    {
        // NSBUtils.BuildNSBMDwithTextures never validates that a model's materials actually resolve
        // against the pack it's handed - see the comment above its call site in ExportMapScene - so a
        // building whose model needs texture names this pack doesn't have renders untextured (flat
        // grey) instead of erroring. The real game does not show that grey model at all: it falls
        // back to rendering building model 0 ("dmybox00") with its own texture instead. Replicate
        // that here rather than silently exporting a grey model DSPRE's viewer has no way to flag.
        if (modelID != 0 && !ModelHasMatchingTexture(buildingData, buildingTextureBytes))
        {
            byte[]? fallbackData = LoadBuildingModelData(0, interior);
            if (fallbackData is not null)
            {
                buildingData = fallbackData;
                modelID = 0;
            }
        }

        // Some building models (dmybox00 among them) already carry an embedded texture; blindly
        // attaching a second TEX0 block on top would corrupt the block count in the NSBMD header.
        // This mirrors the exact same check MapFile.LoadMapModel already does for the terrain model.
        bool alreadyTextured = buildingData.Length > 0xF && BitConverter.ToInt16(buildingData, 0xE) > 1;
        buildingData = alreadyTextured ? buildingData : NSBUtils.BuildNSBMDwithTextures(buildingData, buildingTextureBytes);
    }

    return (modelID, buildingData);
}

/// <summary>True if at least one of the model's textured materials names a texture that is actually
/// present in buildingTextureBytes. A model with no textured materials at all (nothing to mismatch)
/// or a texture pack that fails to parse both count as "matching" - fail open, since the point is
/// only to catch a genuine name mismatch, not to second-guess unrelated data.</summary>
static bool ModelHasMatchingTexture(byte[] buildingData, byte[] buildingTextureBytes)
{
    try
    {
        NSBMD model = NSBMDLoader.LoadNSBMD(new MemoryStream(buildingData));
        // Materials' texname is left empty until this runs (see RegenerateTexturesWithLegacyDecoder's
        // own call to it above) - without it, every material below reads as untextured and the check
        // always short-circuits to "matches", silently never triggering the fallback at all.
        model.MatchTextures();
        if (model.models is null || model.models.Length == 0)
        {
            return true;
        }

        var neededNames = model.models[0].Materials
            .Where(material => material.format != 0 && !string.IsNullOrEmpty(material.texname))
            .Select(material => material.texname)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (neededNames.Count == 0)
        {
            return true;
        }

        NSBTXLoader.LoadNsbtx(new MemoryStream(buildingTextureBytes), out List<LibNDSFormats.NSBMD.NSBMDTexture> availableTextures, out _);
        return neededNames.Overlaps(availableTextures.Select(texture => texture.texname));
    }
    catch (Exception ex) when (ex is IOException or EndOfStreamException or ArgumentException)
    {
        return true;
    }
}

// The legacy Map Editor lets the user manually toggle between the interior/exterior building model
// sets (interiorbldRadioButton/exteriorbldRadioButton in MapEditor.cs) because it has no data-driven
// way to know which a given building belongs to either; it defaults to exterior. This does the same,
// falling back to interior only on HGSS (the only game family with an interior building set at all)
// when exterior does not have that model ID.
/// <summary>An HGSS map uses the interior building set when its area data says it is indoors, else the
/// exterior one; the two sets reuse the same model numbers for different buildings. This is the choice
/// the legacy Event Editor makes (EventEditor.cs, areaData.areaType == 0); DP and Platinum place
/// exterior models everywhere.</summary>
static byte[]? LoadBuildingModelData(uint modelID, bool interior)
{
    string path = Filesystem.GetBuildingModelPath(interior && RomInfo.gameFamily == RomInfo.GameFamilies.HGSS, (int)modelID);
    return File.Exists(path) ? File.ReadAllBytes(path) : null;
}

// Runs apicula in a per-call scratch subdirectory, then moves its ENTIRE output - the .glb plus any
// side-car files it wrote (apicula does not always embed textures in the binary glTF buffer; it can
// write them out as separate PNGs referenced by relative URI, e.g. "t1_door1.png") - into a subfolder
// of outputDirectory named after modelName. Every model gets its own subfolder rather than sharing
// outputDirectory directly, for two reasons: converting several models into the same directory must
// never delete or collide with a sibling's output (the original single-model version of this tool
// wiped every *.glb in outputDirectory before each conversion), and two different models' side-car
// files could otherwise collide on the same name.
static bool ConvertNsbmdToGlb(byte[] nsbmdBytes, string outputDirectory, string modelName, out string? relativeGlbPath, out string? error)
{
    relativeGlbPath = null;
    error = null;
    string scratchDirectory = Path.Combine(outputDirectory, $".dspre-scratch-{Guid.NewGuid():N}");
    Directory.CreateDirectory(scratchDirectory);
    string tempNsbmdPath = Path.Combine(scratchDirectory, "model.nsbmd");
    try
    {
        File.WriteAllBytes(tempNsbmdPath, nsbmdBytes);

        string apicula = Path.Combine(AppContext.BaseDirectory, "..", "apicula.exe");
        if (!File.Exists(apicula))
        {
            error = $"Apicula was not found at '{apicula}'.";
            return false;
        }

        var process = new System.Diagnostics.Process
        {
            StartInfo = new System.Diagnostics.ProcessStartInfo
            {
                FileName = apicula,
                Arguments = $"convert \"{tempNsbmdPath}\" -f glb --overwrite --output \"{scratchDirectory}\"",
                WorkingDirectory = Path.GetDirectoryName(apicula) ?? Environment.CurrentDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            }
        };
        process.Start();
        string standardOutput = process.StandardOutput.ReadToEnd();
        string standardError = process.StandardError.ReadToEnd();
        process.WaitForExit();
        if (process.ExitCode != 0)
        {
            error = $"Apicula exited with code {process.ExitCode}.\n{standardOutput}\n{standardError}";
            return false;
        }

        string[] produced = Directory.GetFiles(scratchDirectory, "*", SearchOption.AllDirectories);
        string? generatedGlbName = produced
            .Where(file => string.Equals(Path.GetExtension(file), ".glb", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .FirstOrDefault();
        if (generatedGlbName is null)
        {
            error = $"Apicula completed but did not create a GLB.\n{standardOutput}\n{standardError}";
            return false;
        }

        string modelDirectory = Path.Combine(outputDirectory, modelName);
        Directory.CreateDirectory(modelDirectory);
        foreach (string file in produced)
        {
            File.Copy(file, Path.Combine(modelDirectory, Path.GetFileName(file)), overwrite: true);
        }

        // The legacy NSBMD loader/decoder was never written for concurrent use; only apicula itself
        // runs in parallel during a batch export.
        lock (ToolLocks.LegacyDecoder)
        {
            RegenerateTexturesWithLegacyDecoder(nsbmdBytes, modelDirectory);
        }

        // Always a forward-slash path: this is read back on Linux by DSPRE.Avalonia, but this tool
        // itself runs under Wine, where Path.Combine would otherwise join with a backslash.
        relativeGlbPath = $"{modelName}/{generatedGlbName}";
        return true;
    }
    finally
    {
        if (Directory.Exists(scratchDirectory))
        {
            Directory.Delete(scratchDirectory, recursive: true);
        }
    }
}

// apicula's own decode of the DS-specific texture formats - particularly the 4x4-texel compressed
// format, which has 4 different per-block interpolation modes - does not always match the legacy
// WinForms Map Editor's renderer pixel-for-pixel; that showed up as "vaguely right colors with
// random grey/green lines" on building textures. DSPRE already has a decoder proven correct for
// every DS texture format (NSBMDGlRenderer.DecodeMaterialToBitmap, extracted from the exact code
// that has rendered these textures correctly in the WinForms Map Editor for years), so this
// regenerates every texture apicula produced using that decoder instead, overwriting apicula's PNGs
// in place by matching filenames. MapGlView never has to know the difference: it just reads
// whatever PNG file sits next to the glb it was given.
static void RegenerateTexturesWithLegacyDecoder(byte[] nsbmdBytes, string modelDirectory)
{
    try
    {
        NSBMD model = NSBMDLoader.LoadNSBMD(new MemoryStream(nsbmdBytes));
        model.MatchTextures();
        if (model.models is null || model.models.Length == 0)
        {
            return;
        }

        var renderer = new NSBMDGlRenderer();
        foreach (NSBMDMaterial material in model.models[0].Materials)
        {
            if (material.format == 0 || string.IsNullOrEmpty(material.texname))
            {
                continue;
            }

            string pngPath = Path.Combine(modelDirectory, $"{material.texname}.png");
            if (!File.Exists(pngPath))
            {
                // apicula did not produce a PNG under this name - do not introduce a file the glb
                // never actually references.
                continue;
            }

            using System.Drawing.Bitmap bitmap = renderer.DecodeMaterialToBitmap(material);
            bitmap.Save(pngPath, System.Drawing.Imaging.ImageFormat.Png);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"Could not regenerate textures with the legacy decoder for '{modelDirectory}': {ex.Message}");
    }
}

static class ToolLocks
{
    public static readonly object LegacyDecoder = new();
}

sealed class SceneManifest
{
    /// <summary>2: one part per building, including empty ones (see ExportMapScene). MapModelCache
    /// re-converts scenes older than this.</summary>
    public int Version { get; set; } = 2;
    public string Terrain { get; set; } = "";
    public List<ScenePartManifest> Parts { get; set; } = [];
}

sealed class ScenePartManifest
{
    public string Model { get; set; } = "";
    public float[] Position { get; set; } = new float[3];
    public float[] RotationDegrees { get; set; } = new float[3];
    public float[] Scale { get; set; } = new float[3];
}
