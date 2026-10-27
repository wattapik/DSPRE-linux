namespace DSPRE.Core;

public sealed record ProjectPreparationResult(ToolResult Arm9, ToolResult? TextArchives, ToolResult? ScriptExport, RomGameMetadata? Metadata);

/// <summary>The open (prepare for editing) and save (repack + build) sequences shared by the editor and
/// the headless journal replay, so a replayed ROM goes through exactly the same steps as an edited one.</summary>
public static class RomProjectPipeline
{
    public static async Task<ProjectPreparationResult> PrepareAsync(
        RomProject project, string toolsDirectory, Action<string>? status = null, bool deferCompleteScriptExport = false)
    {
        var service = new RomExtractionService();
        status?.Invoke("Preparing ARM9 for editing...");
        ToolResult arm9 = await service.PrepareArm9Async(project, toolsDirectory);
        if (!arm9.Succeeded || !project.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            return new ProjectPreparationResult(arm9, null, null, null);
        }

        status?.Invoke("Preparing location names...");
        service.PrepareDynamicHeaders(project, metadata);
        ToolResult text = await service.PrepareTextArchivesAsync(project, metadata, toolsDirectory);
        service.PrepareMatrices(project, metadata);
        service.PrepareMaps(project, metadata);
        service.PrepareTilesets(project, metadata);
        service.PrepareEncounters(project, metadata);
        service.PrepareSpecialEncounters(project, metadata);
        service.PrepareTrainers(project, metadata);
        service.PrepareSprites(project, metadata);
        service.PrepareScripts(project, metadata);
        // With every script already exported the bridge only re-checks the command database, which the
        // caller can run in the background; a null ScriptExport means it was left to the caller.
        if (deferCompleteScriptExport && RomExtractionService.AreScriptsExported(project))
        {
            return new ProjectPreparationResult(arm9, text, null, metadata);
        }

        status?.Invoke("Exporting scripts...");
        ToolResult scripts = await service.ExportScriptsAsync(project, metadata, toolsDirectory);
        return new ProjectPreparationResult(arm9, text, scripts, metadata);
    }

    /// <summary>Extracts the archives the editors otherwise only unpack when first opened, so a headless
    /// journal replay can load every document kind.</summary>
    public static void PrepareEditorData(RomProject project, RomGameMetadata metadata)
    {
        var service = new RomExtractionService();
        if (!Directory.Exists(project.EventsDirectory)) service.PrepareEvents(project, metadata);
        if (!Directory.Exists(project.MoveDataDirectory)) service.PrepareMoveData(project, metadata);
        if (!Directory.Exists(project.ItemDataDirectory)) service.PrepareItemData(project, metadata);
        if (!Directory.Exists(project.TradeDataDirectory)) service.PrepareTradeData(project, metadata);
        if (!Directory.Exists(project.PersonalDataDirectory)) service.PreparePokemonData(project, metadata);
    }

    /// <summary>Re-encodes text/scripts, repacks every edited NARC and builds the ROM to
    /// <paramref name="outputRomPath"/> (via a temporary file, so a failed build never clobbers it).
    /// Returns the failing step's result, or the build result.</summary>
    public static async Task<ToolResult> BuildRomAsync(RomProject project, RomGameMetadata metadata, string outputRomPath, string toolsDirectory, Action<string>? status = null)
    {
        var service = new RomExtractionService();
        status?.Invoke("Encoding text archives...");
        ToolResult text = await service.RebuildTextArchivesAsync(project, metadata, toolsDirectory);
        if (!text.Succeeded) return text;

        service.PackTextArchives(project, metadata);
        status?.Invoke("Rebuilding binary scripts...");
        ToolResult scripts = await service.RebuildScriptsAsync(project, metadata, toolsDirectory);
        if (!scripts.Succeeded) return scripts;

        service.PackDynamicHeaders(project, metadata);
        service.PackSyntheticOverlay(project, metadata);
        service.PackMatrices(project, metadata);
        service.PackMaps(project, metadata);
        service.PackTilesets(project, metadata);
        service.PackEvents(project, metadata);
        service.PackScripts(project, metadata);
        service.PackEncounters(project, metadata);
        service.PackSpecialEncounters(project, metadata);
        service.PackTrainers(project, metadata);
        service.PackMoveData(project, metadata);
        service.PackItemData(project, metadata);
        service.PackTradeData(project, metadata);
        service.PackPokemonData(project, metadata);
        service.PackOverworldSprites(project, metadata);
        service.PackSprites(project, metadata);

        status?.Invoke("Building ROM...");
        string temporaryRomPath = outputRomPath + ".dspre-build.tmp";
        try
        {
            ToolResult build = await service.BuildAsync(project.RootPath, temporaryRomPath, toolsDirectory);
            if (build.Succeeded)
            {
                File.Move(temporaryRomPath, outputRomPath, overwrite: true);
                // Saving over the ROM the contents folder came from keeps them paired, so reopening
                // that ROM goes on reusing the folder rather than asking about it.
                if (RomSourceStamp.Read(project.RootPath) is { } stamp &&
                    string.Equals(Path.GetFullPath(stamp.RomPath), Path.GetFullPath(outputRomPath), StringComparison.Ordinal))
                {
                    RomSourceStamp.FromRom(outputRomPath).Write(project.RootPath);
                }
                EditJournal.Comment(project, $"Built ROM {Path.GetFileName(outputRomPath)}");
            }
            return build;
        }
        finally
        {
            if (File.Exists(temporaryRomPath)) File.Delete(temporaryRomPath);
        }
    }
}
