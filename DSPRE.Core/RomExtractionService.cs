namespace DSPRE.Core;

/// <summary>Exactly one of PngPath/FallbackIconName is set - PngPath for a real, freshly-exported
/// in-game sprite, FallbackIconName (a bundled asset name, e.g. "overworld") when the overlayTableEntry
/// has no flat sprite or one couldn't be read. See RomExtractionService.ExportOverworldSpriteAsync.</summary>
public sealed record OverworldSpriteResult(string? PngPath, string? FallbackIconName);

public sealed class RomExtractionService
{
    private readonly IToolRunner toolRunner;

    // Every DSPRE.ScriptTool process opens the same application.log on startup, so two running at
    // once crash the loser with a sharing violation. Callers that are not already sequential (a
    // background thumbnail pass alongside a map load, say) are serialized here.
    private static readonly SemaphoreSlim BridgeGate = new(1, 1);

    private async Task<ToolResult> RunBridgeAsync(string launcher, List<string> arguments, string workingDirectory, CancellationToken cancellationToken)
    {
        await BridgeGate.WaitAsync(cancellationToken);
        try
        {
            return await toolRunner.RunAsync(launcher, arguments, workingDirectory, cancellationToken);
        }
        finally
        {
            BridgeGate.Release();
        }
    }

    public RomExtractionService(IToolRunner? toolRunner = null)
    {
        this.toolRunner = toolRunner ?? new ProcessToolRunner();
    }

    /// <summary>A cheap sanity check of the cartridge header, so a save file or other non-ROM picked
    /// by mistake is reported as such instead of as the extraction tool's own backtrace.</summary>
    public static bool LooksLikeNintendoDsRom(string path)
    {
        const int headerSize = 0x200;
        const int fileNameTableOffsetField = 0x40;
        const int minimumFileNameTableSize = 8;

        using FileStream stream = File.OpenRead(path);
        if (stream.Length < headerSize) return false;

        byte[] header = new byte[headerSize];
        stream.ReadExactly(header);
        uint tableOffset = BitConverter.ToUInt32(header, fileNameTableOffsetField);
        uint tableSize = BitConverter.ToUInt32(header, fileNameTableOffsetField + 4);
        return tableSize >= minimumFileNameTableSize && tableOffset >= headerSize && (long)tableOffset + tableSize <= stream.Length;
    }

    public Task<ToolResult> ExtractAsync(
        string romPath,
        string outputDirectory,
        RomProjectFormat format,
        string toolsDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(romPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsDirectory);

        Directory.CreateDirectory(outputDirectory);
        string toolName = format == RomProjectFormat.DsRom ? "dsrom" : "ndstool";
        (string executable, List<string> launcherArguments) = ResolveTool(toolsDirectory, toolName);

        IEnumerable<string> toolArguments = format == RomProjectFormat.DsRom
            ? ["extract", "-r", romPath, "-o", outputDirectory]
            : BuildNdstoolArguments(romPath, outputDirectory);
        IEnumerable<string> arguments = launcherArguments.Concat(toolArguments);

        return toolRunner.RunAsync(executable, arguments, toolsDirectory, cancellationToken);
    }

    public Task<ToolResult> BuildAsync(
        string projectDirectory,
        string outputRomPath,
        string toolsDirectory,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputRomPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(toolsDirectory);

        string configPath = Path.Combine(projectDirectory, "config.yaml");
        if (!File.Exists(configPath))
        {
            throw new FileNotFoundException("The extracted project does not contain config.yaml.", configPath);
        }

        (string executable, List<string> launcherArguments) = ResolveTool(toolsDirectory, "dsrom");
        IEnumerable<string> arguments = launcherArguments.Concat(["build", "--config", configPath, "--rom", outputRomPath]);
        return toolRunner.RunAsync(executable, arguments, projectDirectory, cancellationToken);
    }

    public async Task<ToolResult> PrepareArm9Async(
        RomProject project,
        string toolsDirectory,
        CancellationToken cancellationToken = default)
    {
        if (!project.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            throw new InvalidDataException("The ROM project's game metadata could not be resolved.");
        }

        if (!File.Exists(project.Arm9Path))
        {
            throw new FileNotFoundException("The ROM project does not contain arm9.bin.", project.Arm9Path);
        }

        long compressionMarkerOffset = metadata.HeaderFormat == MapHeaderFormat.DiamondPearl
            ? 0xB7C
            : 0xBB4;
        using (FileStream stream = File.OpenRead(project.Arm9Path))
        {
            stream.Position = compressionMarkerOffset;
            byte[] marker = new byte[4];
            stream.ReadExactly(marker);
            if (marker.All(value => value == 0))
            {
                return new ToolResult(0, string.Empty, string.Empty);
            }
        }

        if (metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver)
        {
            using FileStream stream = new FileStream(project.Arm9Path, FileMode.Open, FileAccess.Write, FileShare.Read);
            stream.SetLength(stream.Length - 12);
        }

        (string executable, List<string> launcherArguments) = ResolveTool(toolsDirectory, "blz");
        IEnumerable<string> arguments = launcherArguments.Concat(["-d", project.Arm9Path]);
        return await toolRunner.RunAsync(executable, arguments, toolsDirectory, cancellationToken);
    }

    public async Task<ToolResult> PrepareTextArchivesAsync(
        RomProject project,
        RomGameMetadata metadata,
        string toolsDirectory,
        CancellationToken cancellationToken = default)
    {
        string archivePath = metadata.HeaderFormat switch
        {
            MapHeaderFormat.DiamondPearl => Path.Combine(project.FilesPath, "msgdata", "msg.narc"),
            MapHeaderFormat.Platinum => Path.Combine(project.FilesPath, "msgdata", "pl_msg.narc"),
            MapHeaderFormat.HeartGoldSoulSilver => Path.Combine(project.FilesPath, "a", "0", "2", "7"),
            _ => throw new ArgumentOutOfRangeException(nameof(metadata))
        };
        string archiveDirectory = Path.Combine(project.RootPath, "unpacked", "textArchives");
        string expandedDirectory = Path.Combine(project.RootPath, "expanded", "textArchives");
        if (!File.Exists(archivePath))
        {
            throw new FileNotFoundException("The text archive was not found in the extracted project.", archivePath);
        }

        bool alreadyUnpacked = HasFiles(archiveDirectory);
        ExtractIfMissing(archivePath, archiveDirectory);
        Directory.CreateDirectory(expandedDirectory);
        // Decoding is what makes a first open slow; a contents folder that already has every archive
        // decoded (and possibly edited) is left as it is.
        if (alreadyUnpacked && Directory.GetFiles(expandedDirectory, "*.json").Length >= Directory.GetFiles(archiveDirectory).Length)
        {
            return new ToolResult(0, string.Empty, string.Empty);
        }

        string charmapPath = CharMapStore.ActivePath(toolsDirectory);
        (string executable, List<string> launcherArguments) = ResolveTool(toolsDirectory, "chatot");
        IEnumerable<string> arguments = launcherArguments.Concat([
            "decode", "-m", charmapPath, "-d", expandedDirectory, "-a", archiveDirectory,
            "--json", "--lang", LanguageCode(metadata.Language), "--newer"]);
        return await toolRunner.RunAsync(executable, arguments, toolsDirectory, cancellationToken);
    }

    /// <summary>Converts an IMD model to NSBMD (textures included) with Nintendo's g3dcvtr, which DSPRE
    /// cannot ship: g3dcvtr.exe, with xerces-c_2_8.dll beside it, has to be in the Tools folder or in a
    /// folder above the app (so the one kept at the repository root is found). Runs in the IMD's own
    /// folder because g3dcvtr writes its output relative to where it runs.</summary>
    public Task<ToolResult> ConvertImdAsync(string imdPath, string nsbmdFileName, string toolsDirectory, CancellationToken cancellationToken = default)
    {
        string? converter = FindG3dConverter(toolsDirectory);
        if (converter is null)
        {
            throw new FileNotFoundException($"g3dcvtr.exe was not found in {toolsDirectory}. Put your own copy of Nintendo's g3dcvtr.exe, with xerces-c_2_8.dll beside it, there; it cannot be distributed with DSPRE.");
        }

        string workDirectory = Path.GetDirectoryName(Path.GetFullPath(imdPath))!;
        string executable = OperatingSystem.IsWindows() ? converter : "wine";
        string[] launcherArguments = OperatingSystem.IsWindows() ? [] : [converter];
        IEnumerable<string> arguments = launcherArguments.Concat([Path.GetFileName(imdPath), "-eboth", "-o", nsbmdFileName]);
        return toolRunner.RunAsync(executable, arguments, workDirectory, cancellationToken);
    }

    private static string? FindG3dConverter(string toolsDirectory)
    {
        var directories = new List<string> { toolsDirectory };
        for (DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null && directories.Count < 10; directory = directory.Parent)
        {
            directories.Add(directory.FullName);
        }

        return directories.Select(directory => Path.Combine(directory, "g3dcvtr.exe")).FirstOrDefault(File.Exists);
    }

    public async Task<ToolResult> RebuildTextArchivesAsync(
        RomProject project,
        RomGameMetadata metadata,
        string toolsDirectory,
        CancellationToken cancellationToken = default)
    {
        string archiveDirectory = TextArchiveService.GetUnpackedDirectory(project);
        string expandedDirectory = TextArchiveService.GetExpandedDirectory(project);
        Directory.CreateDirectory(archiveDirectory);
        Directory.CreateDirectory(expandedDirectory);

        (string executable, List<string> launcherArguments) = ResolveTool(toolsDirectory, "chatot");
        IEnumerable<string> arguments = launcherArguments.Concat([
            "encode", "-m", CharMapStore.ActivePath(toolsDirectory),
            "-d", expandedDirectory, "-a", archiveDirectory,
            "--json", "--lang", LanguageCode(metadata.Language), "--newer"]);
        return await toolRunner.RunAsync(executable, arguments, toolsDirectory, cancellationToken);
    }

    public void PackTextArchives(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = metadata.HeaderFormat switch
        {
            MapHeaderFormat.DiamondPearl => Path.Combine(project.FilesPath, "msgdata", "msg.narc"),
            MapHeaderFormat.Platinum => Path.Combine(project.FilesPath, "msgdata", "pl_msg.narc"),
            MapHeaderFormat.HeartGoldSoulSilver => Path.Combine(project.FilesPath, "a", "0", "2", "7"),
            _ => throw new ArgumentOutOfRangeException(nameof(metadata))
        };

        if (!Directory.Exists(TextArchiveService.GetUnpackedDirectory(project)))
        {
            throw new DirectoryNotFoundException("The unpacked text archive directory is missing.");
        }

        NarcPacker.Pack(TextArchiveService.GetUnpackedDirectory(project), archivePath);
    }

    public void PrepareDynamicHeaders(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = project.DynamicHeadersArchivePath(metadata);
        if (DynamicHeadersPatch.IsApplied(project, metadata) && File.Exists(archivePath))
        {
            ExtractIfMissing(archivePath, project.DynamicHeadersDirectory);
        }
    }

    public void PrepareMatrices(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver
            ? project.HgssMatricesArchivePath
            : project.MatricesArchivePath;
        if (File.Exists(archivePath))
        {
            ExtractIfMissing(archivePath, project.MatricesDirectory);
        }
    }

    public void PrepareMaps(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = metadata.HeaderFormat switch
        {
            MapHeaderFormat.Platinum => Path.Combine(project.FilesPath, "fielddata", "land_data", "land_data.narc"),
            MapHeaderFormat.DiamondPearl => Path.Combine(project.FilesPath, "fielddata", "land_data", "land_data_release.narc"),
            MapHeaderFormat.HeartGoldSoulSilver => Path.Combine(project.FilesPath, "a", "0", "6", "5"),
            _ => throw new ArgumentOutOfRangeException(nameof(metadata))
        };
        if (File.Exists(archivePath)) ExtractIfMissing(archivePath, project.MapsDirectory);

        string textureArchivePath = metadata.HeaderFormat switch
        {
            MapHeaderFormat.HeartGoldSoulSilver => Path.Combine(project.FilesPath, "a", "0", "4", "4"),
            _ => Path.Combine(project.FilesPath, "fielddata", "areadata", "area_map_tex", "map_tex_set.narc")
        };
        if (File.Exists(textureArchivePath)) ExtractIfMissing(textureArchivePath, project.MapTexturesDirectory);
    }

    /// <summary>Extracts the per-map event files (NPCs, warps, triggers, spawnables) into
    /// project.EventsDirectory - mirrors PrepareMaps's own DP/Pt/HGSS archive-path switch exactly (same
    /// English-release-only simplification: DP's Japanese-vs-release suffix distinction from legacy's
    /// RomInfo.cs is not replicated here, matching land_data's own precedent).</summary>
    public void PrepareEvents(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = metadata.HeaderFormat switch
        {
            MapHeaderFormat.Platinum => Path.Combine(project.FilesPath, "fielddata", "eventdata", "zone_event.narc"),
            MapHeaderFormat.DiamondPearl => Path.Combine(project.FilesPath, "fielddata", "eventdata", "zone_event_release.narc"),
            MapHeaderFormat.HeartGoldSoulSilver => Path.Combine(project.FilesPath, "a", "0", "3", "2"),
            _ => throw new ArgumentOutOfRangeException(nameof(metadata))
        };
        if (File.Exists(archivePath)) ExtractIfMissing(archivePath, project.EventsDirectory);
    }

    /// <summary>Packs project.EventsDirectory back into its archive - the save-side counterpart to
    /// PrepareEvents, using the exact same archive-path switch, so events edited via MapEventService
    /// actually reach the rebuilt ROM instead of only ever updating the unpacked working directory.</summary>
    public void PackEvents(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = metadata.HeaderFormat switch
        {
            MapHeaderFormat.Platinum => Path.Combine(project.FilesPath, "fielddata", "eventdata", "zone_event.narc"),
            MapHeaderFormat.DiamondPearl => Path.Combine(project.FilesPath, "fielddata", "eventdata", "zone_event_release.narc"),
            MapHeaderFormat.HeartGoldSoulSilver => Path.Combine(project.FilesPath, "a", "0", "3", "2"),
            _ => throw new ArgumentOutOfRangeException(nameof(metadata))
        };
        if (Directory.Exists(project.EventsDirectory) && File.Exists(archivePath)) NarcPacker.Pack(project.EventsDirectory, archivePath);
    }

    public void PrepareTilesets(RomProject project, RomGameMetadata metadata)
    {
        foreach ((string archivePath, string directory) in GetTilesetArchives(project, metadata))
        {
            if (File.Exists(archivePath)) ExtractIfMissing(archivePath, directory);
        }
    }

    public void PackTilesets(RomProject project, RomGameMetadata metadata)
    {
        foreach ((string archivePath, string directory) in GetTilesetArchives(project, metadata))
        {
            if (Directory.Exists(directory) && File.Exists(archivePath)) NarcPacker.Pack(directory, archivePath);
        }
    }

    private static IEnumerable<(string ArchivePath, string Directory)> GetTilesetArchives(RomProject project, RomGameMetadata metadata)
    {
        bool hgss = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;

        yield return (hgss
            ? Path.Combine(project.FilesPath, "a", "0", "4", "4")
            : Path.Combine(project.FilesPath, "fielddata", "areadata", "area_map_tex", "map_tex_set.narc"),
            project.MapTexturesDirectory);

        yield return (hgss
            ? Path.Combine(project.FilesPath, "a", "0", "7", "0")
            : Path.Combine(project.FilesPath, "fielddata", "areadata", "area_build_model", "areabm_texset.narc"),
            project.BuildingTexturesDirectory);

        yield return (hgss
            ? Path.Combine(project.FilesPath, "a", "0", "4", "3")
            : Path.Combine(project.FilesPath, "fielddata", "areadata", "area_build_model", "area_build.narc"),
            project.BuildingConfigsDirectory);

        yield return (hgss
            ? Path.Combine(project.FilesPath, "a", "0", "4", "2")
            : Path.Combine(project.FilesPath, "fielddata", "areadata", "area_data.narc"),
            project.AreaDataDirectory);

        yield return (hgss
            ? Path.Combine(project.FilesPath, "a", "0", "4", "0")
            : Path.Combine(project.FilesPath, "fielddata", "build_model", "build_model.narc"),
            project.ExteriorBuildingModelsDirectory);

        // Interior building models only exist in HGSS - DP/Pt buildings have no separate interior.
        if (hgss)
        {
            yield return (Path.Combine(project.FilesPath, "a", "1", "4", "8"), project.InteriorBuildingModelsDirectory);
        }
    }

    public void PackMaps(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = metadata.HeaderFormat switch
        {
            MapHeaderFormat.Platinum => Path.Combine(project.FilesPath, "fielddata", "land_data", "land_data.narc"),
            MapHeaderFormat.DiamondPearl => Path.Combine(project.FilesPath, "fielddata", "land_data", "land_data_release.narc"),
            MapHeaderFormat.HeartGoldSoulSilver => Path.Combine(project.FilesPath, "a", "0", "6", "5"),
            _ => throw new ArgumentOutOfRangeException(nameof(metadata))
        };
        if (Directory.Exists(project.MapsDirectory) && File.Exists(archivePath)) NarcPacker.Pack(project.MapsDirectory, archivePath);
    }

    public void PackMatrices(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver
            ? project.HgssMatricesArchivePath
            : project.MatricesArchivePath;
        if (Directory.Exists(project.MatricesDirectory) && File.Exists(archivePath))
        {
            NarcPacker.Pack(project.MatricesDirectory, archivePath);
        }
    }

    public void PrepareScripts(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = ResolveScriptsArchivePath(project, metadata);
        if (File.Exists(archivePath))
        {
            ExtractIfMissing(archivePath, project.ScriptsDirectory);
        }
        Directory.CreateDirectory(project.ExpandedScriptsDirectory);
    }

    public void PackScripts(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = ResolveScriptsArchivePath(project, metadata);
        if (Directory.Exists(project.ScriptsDirectory) && File.Exists(archivePath))
        {
            NarcPacker.Pack(project.ScriptsDirectory, archivePath);
        }
    }

    private static string ResolveScriptsArchivePath(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = metadata.HeaderFormat switch
        {
            MapHeaderFormat.DiamondPearl => Path.Combine(project.FilesPath, "fielddata", "script", "scr_seq_release.narc"),
            MapHeaderFormat.Platinum => Path.Combine(project.FilesPath, "fielddata", "script", "scr_seq.narc"),
            MapHeaderFormat.HeartGoldSoulSilver => Path.Combine(project.FilesPath, "a", "0", "1", "2"),
            _ => throw new ArgumentOutOfRangeException(nameof(metadata))
        };
        if (!File.Exists(archivePath) && metadata.HeaderFormat == MapHeaderFormat.DiamondPearl)
        {
            archivePath = Path.Combine(project.FilesPath, "fielddata", "script", "scr_seq.narc");
        }

        return archivePath;
    }

    public void PrepareEncounters(RomProject project, RomGameMetadata metadata)
    {
        string? archivePath = WildEncounterService.FindArchivePath(project, metadata);
        if (archivePath is not null)
        {
            ExtractIfMissing(archivePath, project.EncountersDirectory);
        }
    }

    private static string MonIconsArchivePath(RomProject project, RomGameMetadata metadata) => metadata.HeaderFormat switch
    {
        MapHeaderFormat.HeartGoldSoulSilver => Path.Combine(project.FilesPath, "a", "0", "2", "0"),
        MapHeaderFormat.Platinum => Path.Combine(project.FilesPath, "poketool", "icongra", "pl_poke_icon.narc"),
        _ => Path.Combine(project.FilesPath, "poketool", "icongra", "poke_icon.narc")
    };

    /// <summary>Unpacks the NARCs the sprite decoder (SpriteFormats/SpriteService) reads from - species
    /// icons and trainer class graphics.</summary>
    public void PrepareSprites(RomProject project, RomGameMetadata metadata)
    {
        string monIconsArchive = MonIconsArchivePath(project, metadata);
        if (File.Exists(monIconsArchive)) ExtractIfMissing(monIconsArchive, project.MonIconsDirectory);

        string trainerGraphicsArchive = TrainerGraphicsArchivePath(project, metadata);
        if (File.Exists(trainerGraphicsArchive)) ExtractIfMissing(trainerGraphicsArchive, project.TrainerGraphicsDirectory);

        string trainerBackArchive = TrainerBackGraphicsArchivePath(project, metadata);
        if (File.Exists(trainerBackArchive)) ExtractIfMissing(trainerBackArchive, project.TrainerBackGraphicsDirectory);
    }

    public void PrepareTrainers(RomProject project, RomGameMetadata metadata)
    {
        foreach ((string archivePath, string directory) in GetTrainerArchives(project, metadata))
        {
            if (File.Exists(archivePath)) ExtractIfMissing(archivePath, directory);
        }
    }

    public void PackTrainers(RomProject project, RomGameMetadata metadata)
    {
        foreach ((string archivePath, string directory) in GetTrainerArchives(project, metadata))
        {
            if (Directory.Exists(directory) && File.Exists(archivePath)) NarcPacker.Pack(directory, archivePath);
        }
    }

    private static IEnumerable<(string ArchivePath, string Directory)> GetTrainerArchives(RomProject project, RomGameMetadata metadata)
    {
        bool hgss = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;
        yield return (
            hgss ? Path.Combine(project.FilesPath, "a", "0", "5", "5") : Path.Combine(project.FilesPath, "poketool", "trainer", "trdata.narc"),
            project.TrainerPropertiesDirectory);
        yield return (
            hgss ? Path.Combine(project.FilesPath, "a", "0", "5", "6") : Path.Combine(project.FilesPath, "poketool", "trainer", "trpoke.narc"),
            project.TrainerPartyDirectory);
    }

    public void PrepareMoveData(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = metadata.HeaderFormat switch
        {
            MapHeaderFormat.HeartGoldSoulSilver => Path.Combine(project.FilesPath, "a", "0", "1", "1"),
            MapHeaderFormat.Platinum => Path.Combine(project.FilesPath, "poketool", "waza", "pl_waza_tbl.narc"),
            _ => Path.Combine(project.FilesPath, "poketool", "waza", "waza_tbl.narc")
        };
        if (File.Exists(archivePath)) ExtractIfMissing(archivePath, project.MoveDataDirectory);
    }

    public void PackMoveData(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = metadata.HeaderFormat switch
        {
            MapHeaderFormat.HeartGoldSoulSilver => Path.Combine(project.FilesPath, "a", "0", "1", "1"),
            MapHeaderFormat.Platinum => Path.Combine(project.FilesPath, "poketool", "waza", "pl_waza_tbl.narc"),
            _ => Path.Combine(project.FilesPath, "poketool", "waza", "waza_tbl.narc")
        };
        if (Directory.Exists(project.MoveDataDirectory) && File.Exists(archivePath)) NarcPacker.Pack(project.MoveDataDirectory, archivePath);
    }

    public void PrepareTradeData(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = GetTradeDataArchivePath(project, metadata);
        if (File.Exists(archivePath)) ExtractIfMissing(archivePath, project.TradeDataDirectory);
    }

    public void PackTradeData(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = GetTradeDataArchivePath(project, metadata);
        if (Directory.Exists(project.TradeDataDirectory) && File.Exists(archivePath)) NarcPacker.Pack(project.TradeDataDirectory, archivePath);
    }

    /// <summary>DP/Pt keep trade data at a fixed path for English/Japanese, but under a per-language
    /// "resource/{lang}" folder for every other language - the only archive in the game with this
    /// quirk, per the legacy RomInfo.SetNarcDirs. HGSS always uses the numbered a/1/1/2 path regardless
    /// of language.</summary>
    private static string GetTradeDataArchivePath(RomProject project, RomGameMetadata metadata)
    {
        if (metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver)
        {
            return Path.Combine(project.FilesPath, "a", "1", "1", "2");
        }

        if (metadata.Language != RomLanguage.English && metadata.Language != RomLanguage.Japanese)
        {
            string langFolder = metadata.Language switch
            {
                RomLanguage.Italian => "ita",
                RomLanguage.French => "fra",
                RomLanguage.German => "ger",
                RomLanguage.Spanish => "spa",
                _ => string.Empty
            };
            return Path.Combine(project.FilesPath, "resource", langFolder, "pokemon_trade", "fld_trade.narc");
        }

        return Path.Combine(project.FilesPath, "fielddata", "pokemon_trade", "fld_trade.narc");
    }

    public void PrepareItemData(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = GetItemDataArchivePath(project, metadata);
        if (File.Exists(archivePath)) ExtractIfMissing(archivePath, project.ItemDataDirectory);

        string iconsArchivePath = GetItemIconsArchivePath(project, metadata);
        if (File.Exists(iconsArchivePath)) ExtractIfMissing(iconsArchivePath, project.ItemIconsDirectory);
    }

    public void PackItemData(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = GetItemDataArchivePath(project, metadata);
        if (Directory.Exists(project.ItemDataDirectory) && File.Exists(archivePath)) NarcPacker.Pack(project.ItemDataDirectory, archivePath);

        string iconsArchivePath = GetItemIconsArchivePath(project, metadata);
        if (Directory.Exists(project.ItemIconsDirectory) && File.Exists(iconsArchivePath)) NarcPacker.Pack(project.ItemIconsDirectory, iconsArchivePath);
    }

    private static string GetItemIconsArchivePath(RomProject project, RomGameMetadata metadata) => metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver
        ? Path.Combine(project.FilesPath, "a", "0", "1", "8")
        : Path.Combine(project.FilesPath, "itemtool", "itemdata", "item_icon.narc");

    private static string GetItemDataArchivePath(RomProject project, RomGameMetadata metadata) => metadata.HeaderFormat switch
    {
        MapHeaderFormat.HeartGoldSoulSilver => Path.Combine(project.FilesPath, "a", "0", "1", "7"),
        MapHeaderFormat.Platinum => Path.Combine(project.FilesPath, "itemtool", "itemdata", "pl_item_data.narc"),
        _ => Path.Combine(project.FilesPath, "itemtool", "itemdata", "item_data.narc")
    };

    public void PreparePokemonData(RomProject project, RomGameMetadata metadata)
    {
        foreach ((string archivePath, string directory) in GetPokemonDataArchives(project, metadata))
        {
            if (File.Exists(archivePath)) ExtractIfMissing(archivePath, directory);
        }
    }

    public void PackPokemonData(RomProject project, RomGameMetadata metadata)
    {
        foreach ((string archivePath, string directory) in GetPokemonDataArchives(project, metadata))
        {
            if (Directory.Exists(directory) && File.Exists(archivePath)) NarcPacker.Pack(directory, archivePath);
        }
    }

    private static string GetOverworldSpritesArchivePath(RomProject project, RomGameMetadata metadata) =>
        metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver
            ? Path.Combine(project.FilesPath, "a", "0", "8", "1")
            : Path.Combine(project.FilesPath, "data", "mmodel", "mmodel.narc");

    public void PrepareOverworldSprites(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = GetOverworldSpritesArchivePath(project, metadata);
        if (File.Exists(archivePath)) ExtractIfMissing(archivePath, project.OverworldSpritesDirectory);
    }

    public void PackOverworldSprites(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = GetOverworldSpritesArchivePath(project, metadata);
        if (Directory.Exists(project.OverworldSpritesDirectory) && File.Exists(archivePath))
        {
            NarcPacker.Pack(project.OverworldSpritesDirectory, archivePath);
        }
    }

    /// <summary>HGSS keeps its egg moves in a one-file NARC; DP/Pt keep them inside overlay 5 instead,
    /// so there is nothing to unpack for those.</summary>
    public static string? GetEggMovesArchivePath(RomProject project, RomGameMetadata metadata) =>
        metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver ? Path.Combine(project.FilesPath, "a", "2", "2", "9") : null;

    public void PrepareEggMoves(RomProject project, RomGameMetadata metadata)
    {
        string? archivePath = GetEggMovesArchivePath(project, metadata);
        if (archivePath is not null && File.Exists(archivePath)) ExtractIfMissing(archivePath, project.EggMovesDirectory);
    }

    private static IEnumerable<(string ArchivePath, string Directory)> GetPokemonDataArchives(RomProject project, RomGameMetadata metadata)
    {
        yield return (GetPersonalDataArchivePath(project, metadata), project.PersonalDataDirectory);

        string learnsetsArchive = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver
            ? Path.Combine(project.FilesPath, "a", "0", "3", "3")
            : Path.Combine(project.FilesPath, "poketool", "personal", "wotbl.narc");
        yield return (learnsetsArchive, project.LearnsetsDirectory);

        string evolutionsArchive = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver
            ? Path.Combine(project.FilesPath, "a", "0", "3", "4")
            : Path.Combine(project.FilesPath, "poketool", "personal", "evo.narc");
        yield return (evolutionsArchive, project.EvolutionsDirectory);

        if (GetEggMovesArchivePath(project, metadata) is string eggMovesArchive)
        {
            yield return (eggMovesArchive, project.EggMovesDirectory);
        }

        string battleSpritesArchive = metadata.HeaderFormat switch
        {
            MapHeaderFormat.HeartGoldSoulSilver => Path.Combine(project.FilesPath, "a", "0", "0", "4"),
            MapHeaderFormat.Platinum => Path.Combine(project.FilesPath, "poketool", "pokegra", "pl_pokegra.narc"),
            _ => Path.Combine(project.FilesPath, "poketool", "pokegra", "pokegra.narc")
        };
        yield return (battleSpritesArchive, project.PokemonBattleSpritesDirectory);

        string otherBattleSpritesArchive = metadata.HeaderFormat switch
        {
            MapHeaderFormat.HeartGoldSoulSilver => Path.Combine(project.FilesPath, "a", "1", "1", "4"),
            MapHeaderFormat.Platinum => Path.Combine(project.FilesPath, "poketool", "pokegra", "pl_otherpoke.narc"),
            _ => Path.Combine(project.FilesPath, "poketool", "pokegra", "otherpoke.narc")
        };
        yield return (otherBattleSpritesArchive, project.OtherPokemonBattleSpritesDirectory);
    }

    /// <summary>Personal data lives in its own NARC per game version, EXCEPT Pearl, which - unlike every
    /// other archive in the game - uses a sibling "personal_pearl" folder instead of "personal" (the only
    /// DP/Pt archive that differs between Diamond and Pearl specifically, rather than just by family).</summary>
    private static string GetPersonalDataArchivePath(RomProject project, RomGameMetadata metadata)
    {
        if (metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver)
        {
            return Path.Combine(project.FilesPath, "a", "0", "0", "2");
        }

        if (metadata.HeaderFormat == MapHeaderFormat.Platinum)
        {
            return Path.Combine(project.FilesPath, "poketool", "personal", "pl_personal.narc");
        }

        bool isPearl = metadata.GameCode.StartsWith("APA", StringComparison.OrdinalIgnoreCase);
        return Path.Combine(project.FilesPath, "poketool", isPearl ? "personal_pearl" : "personal", "personal.narc");
    }

    public void PrepareSpecialEncounters(RomProject project, RomGameMetadata metadata)
    {
        foreach ((string archivePath, string directory) in GetSpecialEncounterArchives(project, metadata))
        {
            if (File.Exists(archivePath)) ExtractIfMissing(archivePath, directory);
        }
    }

    /// <summary>Repacks the species icons (PokemonIconService) and trainer class graphics
    /// (TrainerGraphicService).</summary>
    public void PackSprites(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = MonIconsArchivePath(project, metadata);
        if (Directory.Exists(project.MonIconsDirectory) && File.Exists(archivePath)) NarcPacker.Pack(project.MonIconsDirectory, archivePath);

        string trainerGraphicsArchive = TrainerGraphicsArchivePath(project, metadata);
        if (Directory.Exists(project.TrainerGraphicsDirectory) && File.Exists(trainerGraphicsArchive)) NarcPacker.Pack(project.TrainerGraphicsDirectory, trainerGraphicsArchive);

        string trainerBackArchive = TrainerBackGraphicsArchivePath(project, metadata);
        if (Directory.Exists(project.TrainerBackGraphicsDirectory) && File.Exists(trainerBackArchive)) NarcPacker.Pack(project.TrainerBackGraphicsDirectory, trainerBackArchive);
    }

    private static string TrainerGraphicsArchivePath(RomProject project, RomGameMetadata metadata) => metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver
        ? Path.Combine(project.FilesPath, "a", "0", "5", "8")
        : Path.Combine(project.FilesPath, "poketool", "trgra", "trfgra.narc");

    private static string TrainerBackGraphicsArchivePath(RomProject project, RomGameMetadata metadata) => metadata.HeaderFormat == MapHeaderFormat.Platinum
        ? Path.Combine(project.FilesPath, "poketool", "trgra", "trbgra.narc")
        : string.Empty;

    public void PackSpecialEncounters(RomProject project, RomGameMetadata metadata)
    {
        foreach ((string archivePath, string directory) in GetSpecialEncounterArchives(project, metadata))
        {
            if (Directory.Exists(directory) && File.Exists(archivePath)) NarcPacker.Pack(directory, archivePath);
        }
    }

    private static IEnumerable<(string ArchivePath, string Directory)> GetSpecialEncounterArchives(RomProject project, RomGameMetadata metadata)
    {
        if (metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver)
        {
            yield return (Path.Combine(project.FilesPath, "a", "2", "3", "0"), project.SafariZoneDirectory);
            yield return (Path.Combine(project.FilesPath, "a", "2", "5", "2"), project.HeadbuttDirectory);
            yield break;
        }

        yield return (Path.Combine(project.FilesPath, "arc", "encdata_ex.narc"), project.ExtendedEncountersDirectory);
    }

    public void PackEncounters(RomProject project, RomGameMetadata metadata)
    {
        string? archivePath = WildEncounterService.FindArchivePath(project, metadata);
        if (archivePath is null || !Directory.Exists(project.EncountersDirectory))
        {
            throw new FileNotFoundException("The wild encounter archive or unpacked directory is missing.");
        }

        NarcPacker.Pack(project.EncountersDirectory, archivePath);
    }

    /// <summary>The synthetic overlay (the file the ARM9 Expansion turns into extra code space) is only
    /// unpacked once a ROM Toolbox patch touches it; when it is, it goes back into its NARC.</summary>
    public void PackSyntheticOverlay(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = project.SyntheticOverlayArchivePath(metadata);
        if (Directory.Exists(project.SyntheticOverlayDirectory) && File.Exists(archivePath))
        {
            NarcPacker.Pack(project.SyntheticOverlayDirectory, archivePath);
        }
    }

    /// <summary>Runs a ROM Toolbox command on the legacy bridge (see DSPRE.ScriptTool/RomToolbox.cs):
    /// "toolbox-status", or "toolbox-apply" with a patch id and optional synthetic-overlay offset.</summary>
    public Task<ToolResult> RunToolboxAsync(
        RomProject project,
        RomGameMetadata metadata,
        string toolsDirectory,
        IReadOnlyList<string> commandArguments,
        CancellationToken cancellationToken = default)
    {
        string bridgePath = Path.Combine(toolsDirectory, "ScriptTool", "DSPRE.ScriptTool.exe");
        if (!File.Exists(bridgePath)) throw new FileNotFoundException("The ROM Toolbox bridge is missing.", bridgePath);
        string launcher = OperatingSystem.IsWindows() ? bridgePath : "wine";
        List<string> arguments = OperatingSystem.IsWindows() ? [] : [bridgePath];
        arguments.Add(commandArguments[0]);
        arguments.Add(metadata.GameCode);
        arguments.Add(project.RootPath);
        arguments.AddRange(commandArguments.Skip(1));
        string workingDirectory = Directory.GetParent(toolsDirectory)?.FullName ?? toolsDirectory;
        return RunBridgeAsync(launcher, arguments, workingDirectory, cancellationToken);
    }

    public void PackDynamicHeaders(RomProject project, RomGameMetadata metadata)
    {
        string archivePath = project.DynamicHeadersArchivePath(metadata);
        if (DynamicHeadersPatch.IsApplied(project, metadata) && Directory.Exists(project.DynamicHeadersDirectory) && File.Exists(archivePath))
        {
            NarcPacker.Pack(project.DynamicHeadersDirectory, archivePath);
        }
    }

    public Task<ToolResult> RebuildScriptsAsync(
        RomProject project,
        RomGameMetadata metadata,
        string toolsDirectory,
        CancellationToken cancellationToken = default)
    {
        string bridgePath = Path.Combine(toolsDirectory, "ScriptTool", "DSPRE.ScriptTool.exe");
        if (!File.Exists(bridgePath))
        {
            throw new FileNotFoundException("The original ScriptFile rebuild bridge is missing.", bridgePath);
        }

        string launcher = OperatingSystem.IsWindows() ? bridgePath : "wine";
        List<string> arguments = OperatingSystem.IsWindows()
            ? ["rebuild", metadata.GameCode, project.RootPath]
            : [bridgePath, "rebuild", metadata.GameCode, project.RootPath];
        string workingDirectory = Directory.GetParent(toolsDirectory)?.FullName ?? toolsDirectory;
        return RunBridgeAsync(launcher, arguments, workingDirectory, cancellationToken);
    }

    public Task<ToolResult> ExportScriptsAsync(
        RomProject project,
        RomGameMetadata metadata,
        string toolsDirectory,
        CancellationToken cancellationToken = default)
    {
        return RunScriptBridgeAsync("export", project, metadata, toolsDirectory, cancellationToken);
    }

    /// <summary>
    /// Converts a map's terrain plus every Building placed on it into a scene: outputDirectory ends up
    /// containing terrain.glb, one building_{modelId}.glb per distinct building model the map uses, and
    /// a scene.json manifest recording each instance's placement, for MapGlView.LoadScene to assemble.
    /// </summary>
    public Task<ToolResult> ExportMapModelAsync(
        RomProject project,
        RomGameMetadata metadata,
        int mapId,
        string outputDirectory,
        string toolsDirectory,
        int texturePackId = -1,
        int buildingTexturePackId = 0,
        CancellationToken cancellationToken = default,
        string? mapFilePath = null,
        bool interiorBuildings = false,
        string? reuseModelsFrom = null)
    {
        string bridgePath = Path.Combine(toolsDirectory, "ScriptTool", "DSPRE.ScriptTool.exe");
        if (!File.Exists(bridgePath)) throw new FileNotFoundException("The original map conversion bridge is missing.", bridgePath);
        string launcher = OperatingSystem.IsWindows() ? bridgePath : "wine";
        List<string> arguments = OperatingSystem.IsWindows()
            ? ["export-map", metadata.GameCode, project.RootPath, mapId.ToString(), outputDirectory, texturePackId.ToString(), buildingTexturePackId.ToString()]
            : [bridgePath, "export-map", metadata.GameCode, project.RootPath, mapId.ToString(), outputDirectory, texturePackId.ToString(), buildingTexturePackId.ToString()];
        if (mapFilePath is not null) arguments.Add(mapFilePath);
        if (interiorBuildings) arguments.Add("--interior-buildings");
        if (reuseModelsFrom is not null) arguments.Add($"--reuse-from={reuseModelsFrom}");
        string workingDirectory = Directory.GetParent(toolsDirectory)?.FullName ?? toolsDirectory;
        return RunBridgeAsync(launcher, arguments, workingDirectory, cancellationToken);
    }

    /// <summary>One map for ExportMapModelsAsync, with the same meaning as ExportMapModelAsync's arguments.</summary>
    public sealed record MapModelJob(int MapId, string OutputDirectory, int TexturePackId, int BuildingTexturePackId, bool InteriorBuildings);

    /// <summary>Converts several maps in one bridge launch, which under wine costs several times what one
    /// map's conversion does. Each map that succeeds has its scene.json written; callers check for it
    /// per map rather than trusting the exit code, which is non-zero when any map failed.</summary>
    public async Task<ToolResult> ExportMapModelsAsync(
        RomProject project,
        RomGameMetadata metadata,
        IReadOnlyList<MapModelJob> jobs,
        string toolsDirectory,
        CancellationToken cancellationToken = default)
    {
        string bridgePath = Path.Combine(toolsDirectory, "ScriptTool", "DSPRE.ScriptTool.exe");
        if (!File.Exists(bridgePath)) throw new FileNotFoundException("The original map conversion bridge is missing.", bridgePath);
        string jobsFile = Path.Combine(Path.GetTempPath(), $"dspre-map-jobs-{Guid.NewGuid():N}.txt");
        await File.WriteAllLinesAsync(jobsFile, jobs.Select(job =>
            $"{job.MapId}\t{job.OutputDirectory}\t{job.TexturePackId}\t{job.BuildingTexturePackId}\t{(job.InteriorBuildings ? 1 : 0)}"), cancellationToken);
        try
        {
            string launcher = OperatingSystem.IsWindows() ? bridgePath : "wine";
            List<string> arguments = ["export-maps", metadata.GameCode, project.RootPath, jobsFile];
            if (!OperatingSystem.IsWindows()) arguments.Insert(0, bridgePath);
            string workingDirectory = Directory.GetParent(toolsDirectory)?.FullName ?? toolsDirectory;
            return await RunBridgeAsync(launcher, arguments, workingDirectory, cancellationToken);
        }
        finally
        {
            File.Delete(jobsFile);
        }
    }

    /// <summary>Converts a single building model into outputDirectory as a scene.json manifest with
    /// the model as its only content, for MapGlView.LoadScene. A negative buildingTexturePackId keeps
    /// whatever textures the model file embeds itself.</summary>
    public Task<ToolResult> ExportBuildingModelAsync(
        RomProject project,
        RomGameMetadata metadata,
        bool interior,
        int modelId,
        string outputDirectory,
        string toolsDirectory,
        int buildingTexturePackId = -1,
        CancellationToken cancellationToken = default)
    {
        string bridgePath = Path.Combine(toolsDirectory, "ScriptTool", "DSPRE.ScriptTool.exe");
        if (!File.Exists(bridgePath)) throw new FileNotFoundException("The original map conversion bridge is missing.", bridgePath);
        string launcher = OperatingSystem.IsWindows() ? bridgePath : "wine";
        List<string> arguments = ["export-building", metadata.GameCode, project.RootPath, interior.ToString(), modelId.ToString(), outputDirectory, buildingTexturePackId.ToString()];
        if (!OperatingSystem.IsWindows()) arguments.Insert(0, bridgePath);
        string workingDirectory = Directory.GetParent(toolsDirectory)?.FullName ?? toolsDirectory;
        return RunBridgeAsync(launcher, arguments, workingDirectory, cancellationToken);
    }

    /// <summary>Converts several building models in one bridge launch, each into
    /// BuildingModelService.GetPreviewDirectory(...)/outputSubfolder with its own scene.json. Models
    /// that fail are simply left without one.</summary>
    public Task<ToolResult> ExportBuildingModelsAsync(
        RomProject project,
        RomGameMetadata metadata,
        bool interior,
        IReadOnlyList<int> modelIds,
        string outputSubfolder,
        string toolsDirectory,
        int buildingTexturePackId = -1,
        CancellationToken cancellationToken = default)
    {
        string bridgePath = Path.Combine(toolsDirectory, "ScriptTool", "DSPRE.ScriptTool.exe");
        if (!File.Exists(bridgePath)) throw new FileNotFoundException("The original map conversion bridge is missing.", bridgePath);
        string launcher = OperatingSystem.IsWindows() ? bridgePath : "wine";
        string outputRoot = Path.GetDirectoryName(BuildingModelService.GetPreviewDirectory(project, interior, 0))!;
        List<string> arguments = ["export-buildings", metadata.GameCode, project.RootPath, interior.ToString(), outputRoot, outputSubfolder, buildingTexturePackId.ToString(), string.Join(',', modelIds)];
        if (!OperatingSystem.IsWindows()) arguments.Insert(0, bridgePath);
        string workingDirectory = Directory.GetParent(toolsDirectory)?.FullName ?? toolsDirectory;
        return RunBridgeAsync(launcher, arguments, workingDirectory, cancellationToken);
    }

    /// <summary>Returns the IDs of every interior/exterior building model that would actually render as
    /// itself with buildingTexturePackId assigned, rather than falling back to dmybox00 the way the
    /// real game does when a model's textures don't resolve against the pack (see
    /// DSPRE.ScriptTool.Program.TryConvertBuildingModel/ModelHasMatchingTexture). Pass -1 for
    /// buildingTexturePackId ("no pack selected") to get every model back unfiltered, since untextured
    /// rendering never substitutes dmybox00 either.</summary>
    public async Task<IReadOnlyList<int>> ListValidBuildingModelsAsync(
        RomProject project,
        RomGameMetadata metadata,
        bool interior,
        string toolsDirectory,
        int buildingTexturePackId = -1,
        CancellationToken cancellationToken = default)
    {
        string bridgePath = Path.Combine(toolsDirectory, "ScriptTool", "DSPRE.ScriptTool.exe");
        if (!File.Exists(bridgePath)) throw new FileNotFoundException("The original map conversion bridge is missing.", bridgePath);
        string launcher = OperatingSystem.IsWindows() ? bridgePath : "wine";
        List<string> arguments = OperatingSystem.IsWindows()
            ? ["list-valid-buildings", metadata.GameCode, project.RootPath, interior.ToString(), buildingTexturePackId.ToString()]
            : [bridgePath, "list-valid-buildings", metadata.GameCode, project.RootPath, interior.ToString(), buildingTexturePackId.ToString()];
        string workingDirectory = Directory.GetParent(toolsDirectory)?.FullName ?? toolsDirectory;
        ToolResult result = await RunBridgeAsync(launcher, arguments, workingDirectory, cancellationToken);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"The building model check failed with exit code {result.ExitCode}: {result.StandardError}");
        }

        const string marker = "VALIDMODEL:";
        var validIds = new List<int>();
        foreach (string line in result.StandardOutput.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(marker, StringComparison.Ordinal) && int.TryParse(trimmed.AsSpan(marker.Length), out int id))
            {
                validIds.Add(id);
            }
        }

        return validIds;
    }

    /// <summary>Exports the actual in-game sprite for one Overworld event's overlayTableEntry+orientation
    /// combination, the same image legacy's Event Editor draws (DS_Map/Editors/EventEditor.cs's
    /// GetOverworldImage) - either as a PNG written to outputPngPath, or, for the entries that have no
    /// flat sprite at all (3D in-engine models) or whose sprite data can't be read, a bundled fallback
    /// icon name the caller resolves against its own local assets instead (see
    /// DSPRE.ScriptTool.Program.ExportOverworldSprite's own comment on why the fallback can't just be
    /// returned as image bytes from inside the bridge).</summary>
    public async Task<OverworldSpriteResult> ExportOverworldSpriteAsync(
        RomProject project,
        RomGameMetadata metadata,
        int overlayTableEntry,
        int orientation,
        string outputPngPath,
        string toolsDirectory,
        CancellationToken cancellationToken = default)
    {
        string bridgePath = Path.Combine(toolsDirectory, "ScriptTool", "DSPRE.ScriptTool.exe");
        if (!File.Exists(bridgePath)) throw new FileNotFoundException("The original overworld sprite export bridge is missing.", bridgePath);
        string launcher = OperatingSystem.IsWindows() ? bridgePath : "wine";
        List<string> arguments = OperatingSystem.IsWindows()
            ? ["export-overworld-sprite", metadata.GameCode, project.RootPath, overlayTableEntry.ToString(), orientation.ToString(), outputPngPath]
            : [bridgePath, "export-overworld-sprite", metadata.GameCode, project.RootPath, overlayTableEntry.ToString(), orientation.ToString(), outputPngPath];
        string workingDirectory = Directory.GetParent(toolsDirectory)?.FullName ?? toolsDirectory;
        ToolResult result = await RunBridgeAsync(launcher, arguments, workingDirectory, cancellationToken);
        if (!result.Succeeded)
        {
            return new OverworldSpriteResult(null, "overworldUnreadable");
        }

        if (File.Exists(outputPngPath))
        {
            return new OverworldSpriteResult(outputPngPath, null);
        }

        const string marker = "ICON:";
        foreach (string line in result.StandardOutput.Split('\n'))
        {
            string trimmed = line.Trim();
            if (trimmed.StartsWith(marker, StringComparison.Ordinal))
            {
                return new OverworldSpriteResult(null, trimmed[marker.Length..]);
            }
        }

        return new OverworldSpriteResult(null, "overworldUnreadable");
    }

    /// <summary>The batch form of ExportOverworldSpriteAsync: every requested sprite in one bridge run,
    /// written to outputDirectory as <c>{entry:D4}_{orientation}.png</c>. A sprite with no flat image
    /// gets a <c>.icon</c> file naming its bundled fallback instead, so TryGetCachedOverworldSprite
    /// answers it next time without the bridge. A failed run is not cached.</summary>
    public async Task<Dictionary<(int Entry, int Orientation), OverworldSpriteResult>> ExportOverworldSpritesAsync(
        RomProject project,
        RomGameMetadata metadata,
        IReadOnlyCollection<(int Entry, int Orientation)> sprites,
        string outputDirectory,
        string toolsDirectory,
        CancellationToken cancellationToken = default)
    {
        var results = new Dictionary<(int Entry, int Orientation), OverworldSpriteResult>();
        if (sprites.Count == 0) return results;

        string bridgePath = Path.Combine(toolsDirectory, "ScriptTool", "DSPRE.ScriptTool.exe");
        if (!File.Exists(bridgePath)) throw new FileNotFoundException("The original overworld sprite export bridge is missing.", bridgePath);
        Directory.CreateDirectory(outputDirectory);
        string list = string.Join(",", sprites.Select(sprite => $"{sprite.Entry}:{sprite.Orientation}"));
        string launcher = OperatingSystem.IsWindows() ? bridgePath : "wine";
        List<string> arguments = OperatingSystem.IsWindows()
            ? ["export-overworld-sprites", metadata.GameCode, project.RootPath, outputDirectory, list]
            : [bridgePath, "export-overworld-sprites", metadata.GameCode, project.RootPath, outputDirectory, list];
        string workingDirectory = Directory.GetParent(toolsDirectory)?.FullName ?? toolsDirectory;
        ToolResult result = await RunBridgeAsync(launcher, arguments, workingDirectory, cancellationToken);

        var fallbacks = new Dictionary<(int, int), string>();
        if (result.Succeeded)
        {
            const string marker = "ICON:";
            foreach (string line in result.StandardOutput.Split('\n'))
            {
                string[] parts = line.Trim() is string trimmed && trimmed.StartsWith(marker, StringComparison.Ordinal)
                    ? trimmed[marker.Length..].Split(':', 3)
                    : [];
                if (parts.Length == 3 && int.TryParse(parts[0], out int entry) && int.TryParse(parts[1], out int orientation))
                {
                    fallbacks[(entry, orientation)] = parts[2];
                }
            }
        }

        foreach ((int entry, int orientation) in sprites)
        {
            string pngPath = OverworldSpritePath(outputDirectory, entry, orientation, ".png");
            if (File.Exists(pngPath))
            {
                results[(entry, orientation)] = new OverworldSpriteResult(pngPath, null);
            }
            else if (fallbacks.TryGetValue((entry, orientation), out string? iconName))
            {
                File.WriteAllText(OverworldSpritePath(outputDirectory, entry, orientation, ".icon"), iconName);
                results[(entry, orientation)] = new OverworldSpriteResult(null, iconName);
            }
            else
            {
                results[(entry, orientation)] = new OverworldSpriteResult(null, "overworldUnreadable");
            }
        }

        return results;
    }

    /// <summary>A sprite ExportOverworldSpritesAsync already exported into outputDirectory, if any.</summary>
    public static OverworldSpriteResult? TryGetCachedOverworldSprite(string outputDirectory, int entry, int orientation)
    {
        string pngPath = OverworldSpritePath(outputDirectory, entry, orientation, ".png");
        if (File.Exists(pngPath)) return new OverworldSpriteResult(pngPath, null);
        string iconPath = OverworldSpritePath(outputDirectory, entry, orientation, ".icon");
        return File.Exists(iconPath) ? new OverworldSpriteResult(null, File.ReadAllText(iconPath).Trim()) : null;
    }

    // Raised when the bridge starts answering some entries differently, so caches made before are
    // dropped: 2 = HeartGold/SoulSilver's entry 101 shows Lyra instead of Platinum's Dawn.
    private const string OverworldSpriteCacheVersion = "2";

    /// <summary>Deletes the overworld sprite cache when an older bridge made it.</summary>
    public static void DiscardStaleOverworldSpriteCache(string outputDirectory)
    {
        string versionPath = Path.Combine(outputDirectory, ".cache-version");
        if (File.Exists(versionPath) && File.ReadAllText(versionPath).Trim() == OverworldSpriteCacheVersion) return;
        if (Directory.Exists(outputDirectory)) Directory.Delete(outputDirectory, recursive: true);
        Directory.CreateDirectory(outputDirectory);
        File.WriteAllText(versionPath, OverworldSpriteCacheVersion);
    }

    private static string OverworldSpritePath(string outputDirectory, int entry, int orientation, string extension) =>
        Path.Combine(outputDirectory, $"{entry:D4}_{orientation}{extension}");

    /// <summary>Unpacks a NARC into the contents folder unless that folder already holds its files.
    /// The unpacked copy is what every editor edits (the NARC is only rebuilt from it when the ROM is
    /// saved), so re-extracting over it on every open would both slow opening down and silently
    /// throw away edits that have not been built into a ROM yet.</summary>
    private static void ExtractIfMissing(string archivePath, string directory)
    {
        if (HasFiles(directory)) return;
        NarcExtractor.Extract(archivePath, directory);
    }

    private static bool HasFiles(string directory) =>
        Directory.Exists(directory) && Directory.EnumerateFiles(directory).Any();

    /// <summary>True when every script file already has its plaintext export, so the (slow) export
    /// bridge only needs to check whether the command database changed.</summary>
    public static bool AreScriptsExported(RomProject project) =>
        HasFiles(project.ScriptsDirectory) &&
        Directory.Exists(project.ExpandedScriptsDirectory) &&
        Directory.GetFiles(project.ExpandedScriptsDirectory, "*.script").Length >= Directory.GetFiles(project.ScriptsDirectory).Length;

    private Task<ToolResult> RunScriptBridgeAsync(
        string operation,
        RomProject project,
        RomGameMetadata metadata,
        string toolsDirectory,
        CancellationToken cancellationToken)
    {
        string bridgePath = Path.Combine(toolsDirectory, "ScriptTool", "DSPRE.ScriptTool.exe");
        if (!File.Exists(bridgePath))
        {
            throw new FileNotFoundException("The original ScriptFile bridge is missing.", bridgePath);
        }

        string launcher = OperatingSystem.IsWindows() ? bridgePath : "wine";
        List<string> arguments = OperatingSystem.IsWindows()
            ? [operation, metadata.GameCode, project.RootPath]
            : [bridgePath, operation, metadata.GameCode, project.RootPath];
        string workingDirectory = Directory.GetParent(toolsDirectory)?.FullName ?? toolsDirectory;
        return RunBridgeAsync(launcher, arguments, workingDirectory, cancellationToken);
    }

    private static string LanguageCode(RomLanguage language) => language switch
    {
        RomLanguage.English => "en_US",
        RomLanguage.Spanish => "es_ES",
        RomLanguage.Italian => "it_IT",
        RomLanguage.French => "fr_FR",
        RomLanguage.German => "de_DE",
        RomLanguage.Japanese => "ja_JP",
        _ => "en_US"
    };

    private static (string executable, List<string> launcherArguments) ResolveTool(string toolsDirectory, string toolName)
    {
        string nativePath = Path.Combine(toolsDirectory, toolName);
        if (OperatingSystem.IsWindows())
        {
            return (Path.Combine(toolsDirectory, $"{toolName}.exe"), []);
        }

        if (File.Exists(nativePath) && IsExecutable(nativePath))
        {
            return (nativePath, []);
        }

        string windowsPath = Path.Combine(toolsDirectory, $"{toolName}.exe");
        if (File.Exists(windowsPath))
        {
            // Wine exposes the Windows tool's files through the same mounted filesystem.
            return ("wine", [windowsPath]);
        }

        return (nativePath, []);
    }

    private static bool IsExecutable(string path)
    {
        if (OperatingSystem.IsWindows())
        {
            return true;
        }

        UnixFileMode mode = File.GetUnixFileMode(path);
        return mode.HasFlag(UnixFileMode.UserExecute) ||
               mode.HasFlag(UnixFileMode.GroupExecute) ||
               mode.HasFlag(UnixFileMode.OtherExecute);
    }

    private static IEnumerable<string> BuildNdstoolArguments(string romPath, string outputDirectory)
    {
        yield return "-x";
        yield return romPath;
        yield return "-9";
        yield return Path.Combine(outputDirectory, "arm9.bin");
        yield return "-7";
        yield return Path.Combine(outputDirectory, "arm7.bin");
        yield return "-y9";
        yield return Path.Combine(outputDirectory, "y9.bin");
        yield return "-y7";
        yield return Path.Combine(outputDirectory, "y7.bin");
        yield return "-d";
        yield return Path.Combine(outputDirectory, "data");
        yield return "-y";
        yield return Path.Combine(outputDirectory, "overlay");
        yield return "-t";
        yield return Path.Combine(outputDirectory, "banner.bin");
        yield return "-h";
        yield return Path.Combine(outputDirectory, "header.bin");
    }

}
