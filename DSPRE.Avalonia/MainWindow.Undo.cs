using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The top bar's Undo and Redo: they step back and forward through the saves made since the project
/// was opened (see EditUndoHistory). Every editor is opened afresh afterwards, since each holds its own copy
/// of the documents that were just rewritten.</summary>
public partial class MainWindow
{
    private EditUndoHistory? undoHistory;

    private void HookUndoHistory()
    {
        if (undoHistory is not null) undoHistory.Changed -= OnUndoHistoryChanged;
        undoHistory = EditJournal.Active?.History;
        if (undoHistory is not null) undoHistory.Changed += OnUndoHistoryChanged;
        UpdateUndoButtons();
    }

    private void OnUndoHistoryChanged() => Dispatcher.UIThread.Post(UpdateUndoButtons);

    private void UpdateUndoButtons()
    {
        UndoButton.IsEnabled = uiUndo.Count > 0 || permissionUndo.Count > 0 || PictureUndoCount > 0 || undoHistory?.CanUndo == true;
        RedoButton.IsEnabled = uiRedo.Count > 0 || permissionRedo.Count > 0 || PictureRedoCount > 0 || undoHistory?.CanRedo == true;
        ToolTip.SetTip(UndoButton, permissionUndo.Count > 0 && permissionUndo[^1].MapId == permissionsMapId && (uiUndo.Count == 0 || uiUndo[^1].At <= LatestPermissionEdit) ? "Undo the last painting in the Permissions tab"
            : PictureUndoCount > 0 && (uiUndo.Count == 0 || uiUndo[^1].At <= LatestPictureEdit) ? "Undo the last change to the Town Map picture in the Pokedex Map Editor"
            : uiUndo.Count > 0 ? $"Undo the last change: {UiEditName(uiUndo[^1])} in the {uiUndo[^1].Editor}"
            : undoHistory?.UndoDescription is string undo ? $"Undo the last save: {undo}" : "Nothing to undo");
        ToolTip.SetTip(RedoButton, permissionRedo.Count > 0 && permissionUndoneLast ? "Redo the last painting in the Permissions tab"
            : PictureRedoCount > 0 && (pictureUndoneLast || uiRedo.Count == 0) ? "Redo the change to the Town Map picture in the Pokedex Map Editor"
            : uiRedo.Count > 0 ? $"Redo the change to {UiEditName(uiRedo[^1])} in the {uiRedo[^1].Editor}"
            : undoHistory?.RedoDescription is string redo ? $"Redo: {redo}" : "Nothing to redo");
    }

    private async void Undo_Click(object? sender, RoutedEventArgs e) => await StepHistoryAsync(redo: false);

    private async void Redo_Click(object? sender, RoutedEventArgs e) => await StepHistoryAsync(redo: true);

    private async Task StepHistoryAsync(bool redo)
    {
        if (currentProject is null) return;
        // Edits not saved yet come first; the buttons' own steps never reload anything.
        if (StepUiHistory(redo, out string uiStep))
        {
            StatusText.Text = $"{(redo ? "Redid" : "Undid")} the change to {uiStep}.";
            RefreshUnsavedMarkers();
            return;
        }

        if (undoHistory is null || EditJournal.Active is not { } journal) return;
        if (!(redo ? undoHistory.CanRedo : undoHistory.CanUndo)) return;
        // Everything open is reloaded from the project, which would replace edits not saved yet.
        if (!await ConfirmLeavingProjectAsync(redo ? "redoing" : "undoing")) return;

        int editor = EditorList.SelectedIndex;
        try
        {
            string? done = redo ? undoHistory.Redo(journal.Context) : undoHistory.Undo(journal.Context);
            sharedMapBuildings.Clear();
            sharedMapSavedBuildings.Clear();
            ShowProjectStatus(currentProject);
            EditorList.SelectedIndex = editor;
            StatusText.Text = done is null ? "Nothing to " + (redo ? "redo." : "undo.") : $"{(redo ? "Redid" : "Undid")}: {done}.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            LogUiError(redo ? "Redo failed" : "Undo failed", ex);
            StatusText.Text = $"{(redo ? "Redo" : "Undo")} failed: {ex.Message}";
        }
    }
}
