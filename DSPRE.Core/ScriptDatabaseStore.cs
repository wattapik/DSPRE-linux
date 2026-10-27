using System.Text.Json.Nodes;

namespace DSPRE.Core;

/// <summary>The script command databases the legacy bridge parses scripts with (legacy
/// DS_Map/Resources/CustomScrcmdManager.cs and Helpers.InitializeScriptDatabase). They live in the
/// bridge's own data folder, %AppData%\DSPRE\databases - inside the wine prefix off Windows. On first
/// use of a ROM the bridge copies the built-in database for its game into
/// edited_databases\&lt;rom name&gt;\scrcmd_database.json and parses that ROM's scripts with that copy from
/// then on, so replacing it changes how the ROM's scripts are read and written. The bridge notices a
/// replaced database (expanded/scripts/.database_hash) and re-exports every script the next time it
/// runs.</summary>
public static class ScriptDatabaseStore
{
    public const string DatabaseFileName = "scrcmd_database.json";
    private const string ProjectFolderSuffix = "_DSPRE_contents";

    /// <summary>%AppData%\DSPRE as the Windows bridge sees it. Off Windows that is the wine prefix's
    /// (WINEPREFIX, else ~/.wine) user AppData folder, which wine names after the Unix user.</summary>
    public static string BridgeDataDirectory
    {
        get
        {
            if (OperatingSystem.IsWindows()) return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "DSPRE");
            string prefix = Environment.GetEnvironmentVariable("WINEPREFIX") is { Length: > 0 } custom
                ? custom
                : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".wine");
            return Path.Combine(prefix, "drive_c", "users", Environment.UserName, "AppData", "Roaming", "DSPRE");
        }
    }

    public static string DatabasesDirectory => Path.Combine(BridgeDataDirectory, "databases");
    public static string EditedDatabasesDirectory => Path.Combine(DatabasesDirectory, "edited_databases");

    /// <summary>The name the bridge files a project's database under: the project folder's name without
    /// the "_DSPRE_contents" suffix (legacy RomInfo.projectName).</summary>
    public static string ProjectKey(RomProject project)
    {
        string folder = Path.GetFileName(Path.TrimEndingDirectorySeparator(project.RootPath));
        return folder.EndsWith(ProjectFolderSuffix, StringComparison.Ordinal) ? folder[..^ProjectFolderSuffix.Length] : folder;
    }

    public static string DatabasePath(string key) => Path.Combine(EditedDatabasesDirectory, key, DatabaseFileName);

    public static string ProjectDatabasePath(RomProject project) => DatabasePath(ProjectKey(project));

    /// <summary>Every ROM the bridge has a database for.</summary>
    public static List<string> ListKeys() =>
        Directory.Exists(EditedDatabasesDirectory)
            ? Directory.GetDirectories(EditedDatabasesDirectory).Select(Path.GetFileName).OfType<string>().Order(StringComparer.OrdinalIgnoreCase).ToList()
            : [];

    /// <summary>Replaces a ROM's database with a JSON file, which must load as a script command database
    /// first so a wrong file cannot break script parsing.</summary>
    public static void Import(string key, string sourcePath)
    {
        ScriptCommandDatabase database;
        try
        {
            database = ScriptCommandDatabaseService.Load(sourcePath);
        }
        catch (Exception ex)
        {
            throw new InvalidDataException($"{Path.GetFileName(sourcePath)} is not a script command database: {ex.Message}", ex);
        }

        if (database.Commands.Count == 0) throw new InvalidDataException($"{Path.GetFileName(sourcePath)} has no script commands.");
        string target = DatabasePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(sourcePath, target, overwrite: true);
    }

    /// <summary>Replaces a ROM's database with the built-in one for its game, as the bridge would make
    /// it on first use. Returns false when the built-in database is not in the bridge's folder.</summary>
    public static bool ResetToBuiltIn(string key, MapHeaderFormat format)
    {
        string builtIn = Path.Combine(DatabasesDirectory, ScriptCommandDatabaseService.GetFileName(format));
        if (!File.Exists(builtIn)) return false;
        string target = DatabasePath(key);
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.Copy(builtIn, target, overwrite: true);
        return true;
    }

    /// <summary>Downloads the latest built-in databases for every game into the bridge's folder and
    /// <paramref name="appDatabasesDirectory"/> (the Script Commands reference). Databases already copied
    /// for a ROM are left alone, as legacy's database update does. Returns the games it could not get.</summary>
    public static async Task<List<string>> UpdateBuiltInAsync(string appDatabasesDirectory, CancellationToken cancellationToken = default)
    {
        var failed = new List<string>();
        foreach (MapHeaderFormat format in new[] { MapHeaderFormat.DiamondPearl, MapHeaderFormat.Platinum, MapHeaderFormat.HeartGoldSoulSilver })
        {
            try
            {
                CopyToBridge(await ScriptCommandDatabaseService.DownloadAsync(appDatabasesDirectory, format, cancellationToken));
            }
            catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException or System.Text.Json.JsonException)
            {
                StatusLog.Warning("Script databases", $"{ScriptCommandDatabaseService.GetFileName(format)} could not be updated: {ex.Message}");
                failed.Add(ScriptCommandDatabaseService.GetFileName(format));
            }
        }

        return failed;
    }

    /// <summary>Makes a downloaded built-in database the one the bridge copies for new ROMs too.</summary>
    public static void CopyToBridge(string builtInDatabase)
    {
        Directory.CreateDirectory(DatabasesDirectory);
        File.Copy(builtInDatabase, Path.Combine(DatabasesDirectory, Path.GetFileName(builtInDatabase)), overwrite: true);
    }

    private static string BridgeSettingsPath => Path.Combine(BridgeDataDirectory, "userSettings.json");

    /// <summary>Whether the bridge pulls the database repository from GitHub every time it starts (legacy
    /// automaticallyUpdateDBs; on by default).</summary>
    public static bool GetBridgeAutoUpdate()
    {
        try
        {
            return !File.Exists(BridgeSettingsPath) || JsonNode.Parse(File.ReadAllText(BridgeSettingsPath))?["automaticallyUpdateDBs"]?.GetValue<bool>() is not false;
        }
        catch (Exception ex) when (ex is IOException or System.Text.Json.JsonException or InvalidOperationException)
        {
            return true;
        }
    }

    /// <summary>Sets automaticallyUpdateDBs in the bridge's settings, keeping its other settings.</summary>
    public static void SetBridgeAutoUpdate(bool enabled)
    {
        JsonObject settings = File.Exists(BridgeSettingsPath) && JsonNode.Parse(File.ReadAllText(BridgeSettingsPath)) is JsonObject existing ? existing : new JsonObject();
        settings["automaticallyUpdateDBs"] = enabled;
        Directory.CreateDirectory(BridgeDataDirectory);
        File.WriteAllText(BridgeSettingsPath, settings.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }
}
