using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>Saving a text archive whose names scripts refer to by constant (trainers, Pokémon, items,
/// moves) also renames those constants in the scripts - see ScriptConstantSync.</summary>
public partial class MainWindow
{
    /// <summary>Saves the archive and updates the scripts. Returns a note for the status line, empty when
    /// no script needed changing.</summary>
    private string SaveTextArchiveKeepingScripts(TextArchiveData archive)
    {
        if (currentProject is null) return "";
        if (currentProject.TryGetGameMetadata(out RomGameMetadata? placeMetadata) && placeMetadata is not null && archive.Id == LocationNameService.ArchiveId(placeMetadata))
        {
            TextArchiveService.Save(currentProject, archive);
            RefreshHeaderNames();
            return "";
        }

        if (!currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null || !ScriptConstantSync.Tracks(metadata, archive.Id))
        {
            TextArchiveService.Save(currentProject, archive);
            return "";
        }

        List<string> before = TextArchiveService.Load(currentProject, archive.Id, metadata).Messages.ToList();
        TextArchiveService.Save(currentProject, archive);
        List<ScriptConstantSync.Rename> renames = ScriptConstantSync.FindRenames(metadata, archive.Id, before, archive.Messages);
        if (renames.Count == 0) return "";

        List<int> changed = ScriptConstantSync.UpdateScripts(currentProject, renames);
        // The open script's boxes may hold unsaved edits, so they get the same renames rather than a reload.
        loadingScript = true;
        try
        {
            ScriptsTextBox.Text = ScriptConstantSync.Apply(ScriptsTextBox.Text ?? "", renames);
            FunctionsTextBox.Text = ScriptConstantSync.Apply(FunctionsTextBox.Text ?? "", renames);
            LuaTextBox.Text = ScriptConstantSync.Apply(LuaTextBox.Text ?? "", renames);
        }
        finally
        {
            loadingScript = false;
        }

        if (changed.Count == 0) return "";
        string note = $"Renamed {string.Join(", ", renames.Take(3).Select(r => r.Description))}{(renames.Count > 3 ? $" and {renames.Count - 3} more" : "")} in script file(s) {string.Join(", ", changed.Take(8).Select(id => id.ToString("D4")))}{(changed.Count > 8 ? "..." : "")}.";
        StatusLog.Info("Scripts", note);
        return " " + note;
    }
}
