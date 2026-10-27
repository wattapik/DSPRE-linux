using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DSPRE.Core;
using System.Diagnostics;

namespace DSPRE.Avalonia;

/// <summary>The Settings page: legacy's Settings window (folders, the project opened on launch) and its
/// Manage Script Databases window. App update checks are not here: legacy's come from its installer
/// (Velopack), which this app is not distributed with.</summary>
public partial class MainWindow
{
    private AppSettings appSettings = AppSettings.Load();
    private bool loadingSettingsPage;

    private async Task<IStorageFolder?> StartFolderAsync(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder) || !Directory.Exists(folder)) return null;
        try
        {
            return await StorageProvider.TryGetFolderFromPathAsync(folder);
        }
        catch (Exception ex)
        {
            StatusLog.Warning("Settings", $"The file picker could not start in {folder}: {ex.Message}");
            return null;
        }
    }

    private Task<IStorageFolder?> ImportStartFolderAsync() => StartFolderAsync(appSettings.ImportFolder);

    /// <summary>Opens an extracted project folder; false when it is not one.</summary>
    private async Task<bool> OpenProjectFolderAsync(string folder)
    {
        if (!RomProject.TryOpen(folder, out RomProject? project) || project is null) return false;
        ResetOverviewLoadStatus();
        BeginBlockingLoad();
        try
        {
            await PrepareAndShowProjectAsync(project);
        }
        finally
        {
            EndBlockingLoad();
        }

        return true;
    }

    /// <summary>Legacy's "Open Default Rom" on launch, asking first unless told not to.</summary>
    private async Task OpenStartupProjectAsync()
    {
        string folder = appSettings.StartupProject;
        if (string.IsNullOrWhiteSpace(folder) || currentProject is not null) return;
        if (!Directory.Exists(folder))
        {
            StatusText.Text = $"The project set to open on launch was not found: {folder}";
            return;
        }

        if (!appSettings.OpenStartupProjectWithoutAsking)
        {
            string? choice = await new ChoiceDialog("Open Project",
                $"Open {Path.GetFileName(Path.TrimEndingDirectorySeparator(folder))}?\n\n{folder}\n\nThis is the project set to open on launch (Settings).",
                ("open", "Open"), ("always", "Open, and Don't Ask Again"), ("no", "Not Now")).ShowDialog<string?>(this);
            if (choice is not ("open" or "always")) return;
            if (choice == "always")
            {
                appSettings.OpenStartupProjectWithoutAsking = true;
                SaveAppSettings();
            }
        }

        if (!await OpenProjectFolderAsync(folder)) StatusText.Text = $"The project set to open on launch is not a recognized ROM project: {folder}";
    }

    private void SaveAppSettings()
    {
        try
        {
            appSettings.Save();
        }
        catch (Exception ex)
        {
            LogUiError("Settings could not be saved", ex);
        }
    }

    private bool settingsPageLoaded;

    /// <summary>Whether the page's folder boxes or startup option differ from the saved settings.</summary>
    private bool SettingsUnsaved() =>
        settingsPageLoaded &&
        ((SettingsExportFolderBox.Text ?? "").Trim() != appSettings.RomExportFolder
         || (SettingsImportFolderBox.Text ?? "").Trim() != appSettings.ImportFolder
         || (SettingsStartupProjectBox.Text ?? "").Trim() != appSettings.StartupProject
         || (SettingsOpenWithoutAskingCheck.IsChecked == true) != appSettings.OpenStartupProjectWithoutAsking);

    private void LoadSettingsPage()
    {
        settingsPageLoaded = true;
        loadingSettingsPage = true;
        try
        {
            SettingsExportFolderBox.Text = appSettings.RomExportFolder;
            SettingsImportFolderBox.Text = appSettings.ImportFolder;
            SettingsStartupProjectBox.Text = appSettings.StartupProject;
            SettingsOpenWithoutAskingCheck.IsChecked = appSettings.OpenStartupProjectWithoutAsking;
            SettingsUseOpenProjectButton.IsEnabled = currentProject is not null;
            SettingsBridgeAutoUpdateCheck.IsChecked = ScriptDatabaseStore.GetBridgeAutoUpdate();
            SettingsStatus.Text = $"Settings file: {AppSettings.FilePath}";
            RefreshScriptDatabaseList();
        }
        finally
        {
            loadingSettingsPage = false;
        }
    }

    private async void SettingsBrowseExportFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("ROM export folder") is string folder) SettingsExportFolderBox.Text = folder;
    }

    private async void SettingsBrowseImportFolder_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Import folder") is string folder) SettingsImportFolderBox.Text = folder;
    }

    private async void SettingsBrowseStartupProject_Click(object? sender, RoutedEventArgs e)
    {
        if (await PickFolderAsync("Project to open on launch") is not string folder) return;
        if (!RomProject.TryOpen(folder, out _))
        {
            SettingsStatus.Text = "That folder is not a recognized ROM project.";
            return;
        }

        SettingsStartupProjectBox.Text = folder;
    }

    private void SettingsUseOpenProject_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is not null) SettingsStartupProjectBox.Text = currentProject.RootPath;
    }

    private void SettingsClearExportFolder_Click(object? sender, RoutedEventArgs e) => SettingsExportFolderBox.Text = "";
    private void SettingsClearImportFolder_Click(object? sender, RoutedEventArgs e) => SettingsImportFolderBox.Text = "";
    private void SettingsClearStartupProject_Click(object? sender, RoutedEventArgs e) => SettingsStartupProjectBox.Text = "";

    private void SettingsSave_Click(object? sender, RoutedEventArgs e)
    {
        var folders = new[] { SettingsExportFolderBox.Text, SettingsImportFolderBox.Text, SettingsStartupProjectBox.Text }
            .Select(text => (text ?? "").Trim()).ToArray();
        string? missing = folders.FirstOrDefault(folder => folder.Length > 0 && !Directory.Exists(folder));
        if (missing is not null)
        {
            SettingsStatus.Text = $"Not saved: the folder {missing} does not exist.";
            return;
        }

        appSettings.RomExportFolder = folders[0];
        appSettings.ImportFolder = folders[1];
        appSettings.StartupProject = folders[2];
        appSettings.OpenStartupProjectWithoutAsking = SettingsOpenWithoutAskingCheck.IsChecked == true;
        try
        {
            appSettings.Save();
            SettingsStatus.Text = "Settings saved.";
        }
        catch (Exception ex)
        {
            LogUiError("Settings could not be saved", ex);
            SettingsStatus.Text = $"Settings could not be saved: {ex.Message}";
        }
    }

    #region Script databases

    private string? SelectedScriptDatabaseKey => (SettingsDatabaseList.SelectedItem as ScriptDatabaseRow)?.Key;

    private sealed record ScriptDatabaseRow(string Key, bool OpenProject)
    {
        public override string ToString() => OpenProject ? $"{Key}   (the open project)" : Key;
    }

    private void RefreshScriptDatabaseList(string? select = null)
    {
        string? projectKey = currentProject is null ? null : ScriptDatabaseStore.ProjectKey(currentProject);
        List<string> keys = ScriptDatabaseStore.ListKeys();
        // The open project can have its database replaced before the bridge has made one for it.
        if (projectKey is not null && !keys.Contains(projectKey)) keys.Insert(0, projectKey);
        var rows = keys.Select(key => new ScriptDatabaseRow(key, key == projectKey)).OrderByDescending(row => row.OpenProject).ToList();
        SettingsDatabaseList.ItemsSource = rows;
        SettingsDatabaseList.SelectedItem = rows.FirstOrDefault(row => row.Key == (select ?? projectKey)) ?? rows.FirstOrDefault();
        SettingsDatabaseFolderText.Text = $"Folder: {ScriptDatabaseStore.EditedDatabasesDirectory}";
        UpdateScriptDatabaseButtons();
    }

    private void SettingsDatabaseList_SelectionChanged(object? sender, global::Avalonia.Controls.SelectionChangedEventArgs e) => UpdateScriptDatabaseButtons();

    private void UpdateScriptDatabaseButtons()
    {
        string? key = SelectedScriptDatabaseKey;
        SettingsImportDatabaseButton.IsEnabled = key is not null;
        SettingsExportDatabaseButton.IsEnabled = key is not null && File.Exists(ScriptDatabaseStore.DatabasePath(key));
        // The built-in database to reset to depends on the game, which is only known for the open project.
        SettingsResetDatabaseButton.IsEnabled = key is not null && currentProject is not null && key == ScriptDatabaseStore.ProjectKey(currentProject);
    }

    private async void SettingsImportDatabase_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedScriptDatabaseKey is not string key) return;
        if (await PickFileAsync($"Script command database for {key}", "Script command database", "*.json") is not string source) return;
        try
        {
            ScriptDatabaseStore.Import(key, source);
        }
        catch (Exception ex)
        {
            LogUiError("The script database could not be replaced", ex);
            SettingsStatus.Text = ex.Message;
            return;
        }

        RefreshScriptDatabaseList(key);
        SettingsStatus.Text = $"{key}'s script database replaced with {Path.GetFileName(source)}.";
        await OfferScriptReexportAsync(key);
    }

    private async void SettingsResetDatabase_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedScriptDatabaseKey is not string key || currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        if (!await ConfirmAsync("Reset Script Database", $"Replace {key}'s script database with the built-in one for its game? Custom commands in it are lost unless you export it first.", "Reset")) return;
        if (!ScriptDatabaseStore.ResetToBuiltIn(key, metadata.HeaderFormat))
        {
            SettingsStatus.Text = $"The built-in {ScriptCommandDatabaseService.GetFileName(metadata.HeaderFormat)} is not in {ScriptDatabaseStore.DatabasesDirectory}; download the latest built-in databases first.";
            return;
        }

        RefreshScriptDatabaseList(key);
        SettingsStatus.Text = $"{key}'s script database reset to the built-in {ScriptCommandDatabaseService.GetFileName(metadata.HeaderFormat)}.";
        await OfferScriptReexportAsync(key);
    }

    /// <summary>Legacy's "reload and reparse all scripts now": the export bridge sees the database changed
    /// and re-exports every script with it. Only the open project's scripts can be redone now; another
    /// ROM's are redone when it is next opened.</summary>
    private async Task OfferScriptReexportAsync(string key)
    {
        ConfigureScriptCommandDatabase();
        if (currentProject is null || key != ScriptDatabaseStore.ProjectKey(currentProject) || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            SettingsStatus.Text += " Its scripts are re-read with it the next time that ROM is opened.";
            return;
        }

        if (!await ConfirmAsync("Re-read Scripts",
                "Re-read every script with the new database now? Each script's text is rewritten from the script data, so save any script you are editing first. Otherwise this happens the next time the project is opened.",
                "Re-read Now"))
        {
            SettingsStatus.Text += " The scripts are re-read with it the next time the project is opened.";
            return;
        }

        BeginBlockingLoad();
        try
        {
            SettingsStatus.Text = "Re-reading every script with the new database...";
            ToolResult result = await new RomExtractionService().ExportScriptsAsync(currentProject, metadata, ToolsDirectory);
            SettingsStatus.Text = result.Succeeded
                ? "Every script was re-read with the new database."
                : $"Re-reading the scripts failed (exit code {result.ExitCode}): {result.StandardError.Trim()}";
        }
        catch (Exception ex)
        {
            LogUiError("The scripts could not be re-read", ex);
            SettingsStatus.Text = $"The scripts could not be re-read: {ex.Message}";
        }
        finally
        {
            EndBlockingLoad();
        }
    }

    private async void SettingsExportDatabase_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedScriptDatabaseKey is not string key) return;
        if (await PickSavePathAsync("Export script command database", $"{key}_scrcmd_database.json", "JSON", "*.json") is not string target) return;
        try
        {
            File.Copy(ScriptDatabaseStore.DatabasePath(key), target, overwrite: true);
            SettingsStatus.Text = $"Exported {key}'s script database to {target}.";
        }
        catch (Exception ex)
        {
            LogUiError("The script database could not be exported", ex);
            SettingsStatus.Text = $"The script database could not be exported: {ex.Message}";
        }
    }

    private void SettingsOpenDatabaseFolder_Click(object? sender, RoutedEventArgs e)
    {
        string folder = SelectedScriptDatabaseKey is string key && Directory.Exists(Path.GetDirectoryName(ScriptDatabaseStore.DatabasePath(key)))
            ? Path.GetDirectoryName(ScriptDatabaseStore.DatabasePath(key))!
            : ScriptDatabaseStore.EditedDatabasesDirectory;
        try
        {
            Directory.CreateDirectory(folder);
            string opener = OperatingSystem.IsWindows() ? "explorer" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            Process.Start(new ProcessStartInfo(opener) { ArgumentList = { folder }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            SettingsStatus.Text = $"The folder could not be opened: {ex.Message}";
        }
    }

    private async void SettingsUpdateDatabases_Click(object? sender, RoutedEventArgs e)
    {
        SettingsUpdateDatabasesButton.IsEnabled = false;
        SettingsStatus.Text = "Downloading the latest built-in script databases...";
        try
        {
            List<string> failed = await ScriptDatabaseStore.UpdateBuiltInAsync(Path.Combine(AppContext.BaseDirectory, "databases"));
            SettingsStatus.Text = failed.Count == 0
                ? "The built-in script databases are up to date. ROMs with their own copy keep it; Reset to Built-In switches the open one over."
                : $"Could not download {string.Join(", ", failed)} (see the status log); the others are up to date.";
            ConfigureScriptCommandDatabase();
        }
        finally
        {
            SettingsUpdateDatabasesButton.IsEnabled = true;
        }
    }

    private void SettingsBridgeAutoUpdate_Changed(object? sender, RoutedEventArgs e)
    {
        if (loadingSettingsPage) return;
        try
        {
            ScriptDatabaseStore.SetBridgeAutoUpdate(SettingsBridgeAutoUpdateCheck.IsChecked == true);
            SettingsStatus.Text = SettingsBridgeAutoUpdateCheck.IsChecked == true
                ? "The script bridge will update the databases from GitHub each time it runs."
                : "The script bridge will no longer update the databases by itself.";
        }
        catch (Exception ex)
        {
            LogUiError("The script bridge's settings could not be changed", ex);
            SettingsStatus.Text = $"The script bridge's settings could not be changed: {ex.Message}";
        }
    }

    #endregion
}
