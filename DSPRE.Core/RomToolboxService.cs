namespace DSPRE.Core;

/// <summary>One ROM Toolbox patch, as the legacy toolbox dialog (DS_Map/PatchToolboxDialog.cs)
/// describes it.</summary>
public sealed record RomPatch(string Id, string Title, string Description, bool AtSyntheticOverlayOffset = false, bool Beta = false);

public sealed record RomPatchState(string Id, string State, string Reason)
{
    public bool Applied => State == "applied";
    public bool Available => State == "available";
}

public sealed record RomToolboxStatus(IReadOnlyList<RomPatchState> Patches, string? SyntheticOverlayPath, IReadOnlyDictionary<string, (uint Offset, int Length)> Defaults);

public sealed record RomToolboxResult(bool Succeeded, string Message);

/// <summary>The legacy ROM Toolbox's code and data patches, applied through the legacy bridge
/// (DSPRE.ScriptTool's toolbox-status/toolbox-apply), which reuses the legacy patch data and checks.
/// Applying one is journaled as <c>ApplyRomPatch(patch: "...", offset: ...)</c>.</summary>
public static class RomToolboxService
{
    public static readonly IReadOnlyList<RomPatch> Patches =
    [
        new("arm9Expansion", "ARM9 Expansion",
            "Expands the ARM9's usable memory by repurposing a file inside an unused NARC and turning it into a synthetic overlay, in which you can place new code or new data. This patch is necessary to accomplish most ASM edits. Back up arm9.bin first if you do not know what this does - it can break the game."),
        new("bdhcam", "Dynamic Cameras",
            "Adds dynamic BDHC cameras, giving more control over rotation and position of the in-game camera. Trifindo's PDSMS is needed to make BDHCAM files. Requires the ARM9 Expansion."),
        new("buildingRotation", "Building Rotation",
            "Makes the game honour the rotation of buildings set in the Map Editor. Requires the ARM9 Expansion.", AtSyntheticOverlayOffset: true, Beta: true),
        new("sentenceCase", "Pokémon Names in Sentence Case",
            "Converts every Pokémon name to Sentence Case: BULBASAUR, IVYSAUR, VENUSAUR... become Bulbasaur, Ivysaur, Venusaur..."),
        new("standardizeItems", "Standardize Item Numbers",
            "Rearranges the GiveItem scripts to follow the default item index order, so any item can be a ground item in the Events editor. Ground items in every event file are renumbered to match."),
        new("matrixExpansion", "Expand Matrix 0",
            "Allows Matrix 0 (the overworld) to be expanded up to twice its size."),
        new("repointScrcmd", "Move ScrCommands Table",
            "Moves the in-game script command table and count to the synthetic overlay. Does not add commands or edit the script command database. Requires the ARM9 Expansion.", AtSyntheticOverlayOffset: true, Beta: true),
        new("dynamicHeaders", "Dynamic Header Allocation",
            "Makes the ROM load headers dynamically from a NARC instead of the header table in arm9.bin. This patch is necessary to add new headers."),
        new("disableTextureAnimations", "Disable Texture Animations",
            "Sets the Dynamic Textures (texture animation) field of every area data file to 65535 [0xFFFF]."),
        new("expandTrainerNames", "Expand Trainer Names",
            "Lets trainer names be up to 12 characters long (11 usable).")
    ];

    public static async Task<RomToolboxStatus> GetStatusAsync(RomProject project, RomGameMetadata metadata, string toolsDirectory)
    {
        ToolResult result = await new RomExtractionService().RunToolboxAsync(project, metadata, toolsDirectory, ["toolbox-status"]);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException(FirstLine(result.StandardError) ?? $"The ROM Toolbox status check failed (exit code {result.ExitCode}).");
        }

        var states = new List<RomPatchState>();
        var defaults = new Dictionary<string, (uint, int)>();
        string? synthetic = null;
        foreach (string line in result.StandardOutput.Split('\n').Select(line => line.Trim()))
        {
            string[] parts = line.Split('|');
            switch (parts[0])
            {
                case "PATCH" when parts.Length >= 4:
                    states.Add(new RomPatchState(parts[1], parts[2], parts[3]));
                    break;
                case "SYNTH" when parts.Length >= 2:
                    synthetic = FromBridgePath(parts[1]);
                    break;
                case "DEFAULT" when parts.Length >= 4:
                    defaults[parts[1]] = (Convert.ToUInt32(parts[2], 16), int.Parse(parts[3]));
                    break;
            }
        }

        return new RomToolboxStatus(states, synthetic, defaults);
    }

    public static async Task<RomToolboxResult> ApplyAsync(RomProject project, RomGameMetadata metadata, string toolsDirectory, string patchId, uint? offset)
    {
        List<string> arguments = ["toolbox-apply", patchId];
        if (offset is uint value) arguments.Add(value.ToString("X"));
        ToolResult result = await new RomExtractionService().RunToolboxAsync(project, metadata, toolsDirectory, arguments);
        string? refusal = result.StandardOutput.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("ERROR|", StringComparison.Ordinal));
        if (!result.Succeeded)
        {
            return new RomToolboxResult(false, refusal?["ERROR|".Length..] ?? FirstLine(result.StandardError) ?? $"exit code {result.ExitCode}");
        }

        EditJournal.Record(project, "ApplyRomPatch", ("patch", patchId), ("offset", offset is uint at ? at : null));
        string? note = result.StandardOutput.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.StartsWith("NOTE|", StringComparison.Ordinal));
        return new RomToolboxResult(true, note?["NOTE|".Length..] ?? string.Empty);
    }

    /// <summary>What a write of <paramref name="length"/> bytes at <paramref name="offset"/> into the
    /// synthetic overlay would land on - the legacy offset dialog's checks (SyntheticOverlayOffsetDialog).</summary>
    public static (bool Usable, string Text) DescribeSyntheticOverlayRange(string? path, uint offset, int length)
    {
        if (offset % 4 != 0) return (false, "Offset must be 4-byte aligned.");
        if (path is null || !File.Exists(path)) return (false, "The synthetic overlay file was not found.");
        long fileLength = new FileInfo(path).Length;
        if (offset >= fileLength || offset + (long)length > fileLength) return (false, "The range is outside the synthetic overlay file.");

        byte[] bytes = new byte[length];
        using (FileStream stream = File.OpenRead(path))
        {
            stream.Position = offset;
            stream.ReadExactly(bytes);
        }

        return bytes.All(b => b == 0)
            ? (true, "This range is empty.")
            : (true, "This range already contains data; applying will overwrite it.");
    }

    /// <summary>The bridge runs under Wine off Windows, where the project's folders are seen through the
    /// Z: drive.</summary>
    private static string FromBridgePath(string path)
    {
        if (OperatingSystem.IsWindows() || path.Length < 3 || path[1] != ':') return path;
        return path[2..].Replace('\\', '/');
    }

    private static string? FirstLine(string text) => text.Split('\n').Select(line => line.Trim()).FirstOrDefault(line => line.Length > 0);
}
