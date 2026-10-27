using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DSPRE.Core;
using System.Diagnostics;

namespace DSPRE.Avalonia;

/// <summary>Project Overview's Export Docs: legacy's documentation export (DS_Map/DocTool.cs).</summary>
public partial class MainWindow
{
    private async void ExportDocs_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "Folder to export the documentation into",
            AllowMultiple = false,
            SuggestedStartLocation = await StartFolderAsync(appSettings.RomExportFolder)
        });
        if (folders.Count != 1) return;
        string folder = folders[0].Path.LocalPath;
        RomProject project = currentProject;

        ExportDocsButton.IsEnabled = false;
        BeginBlockingLoad();
        try
        {
            ExportDocsStatus.Text = "Exporting the data...";
            List<string> written = await Task.Run(() => DocExportService.Export(project, metadata, folder));
            ExportDocsStatus.Text = "Exporting the scripts...";
            (int scripts, int failed) = await DocExportService.ExportScriptsAsync(project, metadata, ToolsDirectory, folder);
            ExportDocsStatus.Text = $"Exported {written.Count} documents and {scripts} scripts to {folder}." +
                (failed > 0 ? $" {failed} script file(s) could not be read (see the status log)." : "");
            StatusLog.Info("Export Docs", ExportDocsStatus.Text);
        }
        catch (Exception ex)
        {
            LogUiError("The documentation could not be exported", ex);
            ExportDocsStatus.Text = $"The documentation could not be exported: {ex.Message}";
            return;
        }
        finally
        {
            EndBlockingLoad();
            ExportDocsButton.IsEnabled = true;
        }

        if (await ConfirmAsync("Export Docs", $"{ExportDocsStatus.Text}\n\nOpen the folder?", "Open Folder"))
        {
            try
            {
                string opener = OperatingSystem.IsWindows() ? "explorer" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
                Process.Start(new ProcessStartInfo(opener) { ArgumentList = { folder }, UseShellExecute = false });
            }
            catch (Exception ex)
            {
                StatusLog.Warning("Export Docs", $"The folder could not be opened: {ex.Message}");
            }
        }
    }
}
