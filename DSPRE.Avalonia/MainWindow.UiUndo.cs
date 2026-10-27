using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.VisualTree;

namespace DSPRE.Avalonia;

/// <summary>Undo and redo of edits made in the editors' inputs before they are saved: each change the user
/// makes to a text box, drop-down, check box or number is remembered with the value it replaced, and undoing
/// puts that value back, through the input, so the editor reacts as it did to the original edit. Typing in
/// one input is one step. Undo and Redo use these first and the saved edits (EditUndoHistory) after them.
///
/// Only inputs are covered: adding or deleting a list entry, dragging in the 3D view and other edits made
/// with buttons or the mouse are not, and an input's history ends when its editor's changes are saved or
/// discarded, or the document it showed is replaced.</summary>
public partial class MainWindow
{
    private sealed record UiEdit(Control Input, object? Before, object? After, object? DataContext, string Editor, DateTime At);

    // Changes to one input within this long of each other are one step (typing a number or a name).
    private static readonly TimeSpan UiEditGroupWindow = TimeSpan.FromSeconds(1.2);
    private const int UiEditLimit = 200;

    private readonly List<UiEdit> uiUndo = [];
    private readonly List<UiEdit> uiRedo = [];
    private bool applyingUiUndo;

    private void RecordUiEdit(Control input, object? before, string editor)
    {
        object? after = InputValue(input);
        if (Equals(before, after)) return;

        DateTime now = DateTime.UtcNow;
        if (uiUndo.Count > 0 && uiUndo[^1] is { } last && ReferenceEquals(last.Input, input) && ReferenceEquals(last.DataContext, input.DataContext) &&
            now - last.At <= UiEditGroupWindow && uiRedo.Count == 0)
        {
            // Still the same step; if it came back to where it started, there is nothing to undo.
            if (Equals(last.Before, after)) uiUndo.RemoveAt(uiUndo.Count - 1);
            else uiUndo[^1] = last with { After = after, At = now };
        }
        else
        {
            uiUndo.Add(new UiEdit(input, before, after, input.DataContext, editor, now));
            if (uiUndo.Count > UiEditLimit) uiUndo.RemoveAt(0);
        }

        uiRedo.Clear();
        UpdateUndoButtons();
    }

    private void ClearUiUndo()
    {
        uiUndo.Clear();
        uiRedo.Clear();
        UpdateUndoButtons();
    }

    /// <summary>Whether the edit can still be applied: its input is on screen, shows the document it was
    /// made in, and the editor still has the change unsaved.</summary>
    private static bool UiEditIsLive(UiEdit edit, HashSet<string> unsavedEditors) =>
        unsavedEditors.Contains(edit.Editor) && edit.Input.GetVisualRoot() is not null && ReferenceEquals(edit.Input.DataContext, edit.DataContext);

    /// <summary>Drops what can no longer be undone or redone; run when the buttons are brought up to date.</summary>
    private void PruneUiUndo(HashSet<string> unsavedEditors)
    {
        int before = uiUndo.Count + uiRedo.Count;
        uiUndo.RemoveAll(edit => !UiEditIsLive(edit, unsavedEditors));
        // A redo is of an edit that was undone, so its editor may well have nothing unsaved just now.
        uiRedo.RemoveAll(edit => edit.Input.GetVisualRoot() is null || !ReferenceEquals(edit.Input.DataContext, edit.DataContext));
        if (before != uiUndo.Count + uiRedo.Count) UpdateUndoButtons();
    }

    private static string UiEditName(UiEdit edit)
    {
        string name = edit.Input.Name ?? edit.Input.GetType().Name;
        foreach (string suffix in new[] { "Input", "Selector", "CheckBox", "Check", "RadioButton", "TextBox" })
        {
            if (name.EndsWith(suffix, StringComparison.Ordinal) && name.Length > suffix.Length)
            {
                name = name[..^suffix.Length];
                break;
            }
        }

        return string.Concat(name.Select((c, i) => i > 0 && char.IsUpper(c) && !char.IsUpper(name[i - 1]) ? " " + char.ToLowerInvariant(c) : c.ToString()));
    }

    private static void SetInputValue(Control input, object? value)
    {
        switch (input)
        {
            case TextBox box: box.Text = value as string ?? string.Empty; break;
            case ComboBox box: box.SelectedIndex = value is int index ? index : -1; break;
            case ToggleButton toggle: toggle.IsChecked = value as bool?; break;
            case NumericUpDown box: box.Value = value as decimal?; break;
        }
    }

    /// <summary>Undoes (or redoes) the latest edit still possible; false when there was none.</summary>
    private bool StepUiHistory(bool redo, out string description)
    {
        description = string.Empty;
        if (StepPictureHistory(redo, out description)) return true;
        if (StepPermissionHistory(redo, out description)) return true;
        HashSet<string> unsaved = currentProject is null ? [] : EditorsWithUnsavedChanges();
        PruneUiUndo(unsaved);
        List<UiEdit> from = redo ? uiRedo : uiUndo, to = redo ? uiUndo : uiRedo;
        if (from.Count == 0) return false;

        UiEdit edit = from[^1];
        from.RemoveAt(from.Count - 1);
        if (!redo) pictureUndoneLast = false;
        applyingUiUndo = true;
        try
        {
            SetInputValue(edit.Input, redo ? edit.After : edit.Before);
        }
        finally
        {
            applyingUiUndo = false;
        }

        to.Add(edit);
        description = $"{UiEditName(edit)} in the {edit.Editor}";
        UpdateUndoButtons();
        return true;
    }
}
