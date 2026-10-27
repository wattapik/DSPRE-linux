using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;

namespace DSPRE.Avalonia;

/// <summary>Keeps unsaved edits from being lost or forgotten. Every editor's unsaved changes are gathered by
/// CollectUnsavedChanges; from that, the sidebar marks each editor that has some with "!", reopening such an
/// editor shows its edits as they were instead of reloading from disk, moving an editor to another document
/// asks first, and closing the window asks first.</summary>
public partial class MainWindow
{
    private DispatcherTimer? unsavedMarkerTimer;
    // Set while a document selector is moved back (or on) by ConfirmDocumentSwitchAsync itself, so the
    // selector's handler doesn't treat that as the user picking a document.
    private bool switchingDocument;
    private bool closeConfirmed;

    private void StartUnsavedMarkers()
    {
        unsavedMarkerTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        unsavedMarkerTimer.Tick += (_, _) => RefreshUnsavedMarkers();
        unsavedMarkerTimer.Start();
    }

    private HashSet<string> EditorsWithUnsavedChanges() => CollectUnsavedChanges().Select(change => change.Editor).ToHashSet();

    private void RefreshUnsavedMarkers()
    {
        if (blockingLoadCount > 0) return;
        HashSet<string> editors = currentProject is null ? [] : EditorsWithUnsavedChanges();
        foreach (ListBoxItem item in EditorList.Items.OfType<ListBoxItem>())
        {
            bool unsaved = item.Content is string title && editors.Contains(title);
            item.Classes.Set("unsaved", unsaved);
            ToolTip.SetTip(item, unsaved ? "Has unsaved changes" : null);
        }
        RefreshInputMarks(editors);
        PruneUiUndo(editors);
    }

    /// <summary>Saves each change with its editor's own Save, then returns what is still unsaved: a save
    /// can refuse (a value that doesn't validate), and that must not pass for saved.</summary>
    private List<UnsavedChange> SaveChanges(IEnumerable<UnsavedChange> changes)
    {
        foreach (UnsavedChange change in changes)
        {
            try
            {
                change.Save();
            }
            catch (Exception ex)
            {
                LogUiError($"{change.Area} could not be saved", ex);
            }
        }
        RefreshUnsavedMarkers();
        return CollectUnsavedChanges();
    }

    private bool HasOpenDocumentChanges(string editor, Func<UnsavedChange, bool>? which = null) =>
        CollectUnsavedChanges().Any(change => change.Editor == editor && change.OpenDocument && (which?.Invoke(change) ?? true));

    /// <summary>Whether an editor may load a document: asking first when it would replace an open one with
    /// unsaved changes (see ConfirmLeavingDocumentAsync), and never reloading the open one over its own
    /// unsaved changes. <paramref name="sameDocument"/>: the one asked for is the one already open.</summary>
    private async Task<bool> ShouldLoadDocumentAsync(bool sameDocument, string editor, Action revert, Func<UnsavedChange, bool>? which = null, Action? discard = null)
    {
        if (sameDocument) return !HasOpenDocumentChanges(editor, which);
        return await ConfirmLeavingDocumentAsync(editor, which, revert, discard);
    }

    /// <summary>Before an editor replaces the document it has open (another trainer, script file,
    /// encounter file...): when that document has unsaved changes, asks whether to save them, discard them
    /// or stay. <paramref name="which"/> narrows the editor's changes to those the new document replaces;
    /// <paramref name="revert"/> puts a selector the user already moved back while asking, and
    /// <paramref name="discard"/> drops edits kept anywhere else in memory. True to go ahead; false to stay,
    /// including when the save was refused.</summary>
    private async Task<bool> ConfirmLeavingDocumentAsync(string editor, Func<UnsavedChange, bool>? which = null, Action? revert = null, Action? discard = null)
    {
        bool Leaving(UnsavedChange change) => change.Editor == editor && change.OpenDocument && (which?.Invoke(change) ?? true);
        List<UnsavedChange> changes = CollectUnsavedChanges().Where(Leaving).ToList();
        if (changes.Count == 0) return true;

        if (revert is not null) RunSwitchingDocument(revert);
        string? choice = await new ChoiceDialog(
            "Unsaved changes",
            $"{DescribeUnsavedAreas(changes)} {(changes.Count == 1 ? "has" : "have")} changes that are not saved ({string.Join("; ", changes.SelectMany(change => change.Details).Take(3))}).",
            ("save", "Save"), ("discard", "Discard Changes"), ("cancel", "Cancel")).ShowDialog<string?>(this);
        if (choice == "save")
        {
            if (SaveChanges(changes).Any(Leaving)) return false;
        }
        else if (choice == "discard")
        {
            discard?.Invoke();
        }
        else
        {
            return false;
        }

        RefreshUnsavedMarkers();
        return true;
    }

    private void RunSwitchingDocument(Action action)
    {
        switchingDocument = true;
        ClearUiUndo();
        try
        {
            action();
        }
        finally
        {
            switchingDocument = false;
        }
    }

    /// <summary>Selects the item of <paramref name="selector"/> that <paramref name="matches"/>.</summary>
    private static void SelectWhere(SelectingItemsControl selector, Func<object?, bool> matches)
    {
        for (int i = 0; i < selector.ItemCount; i++)
        {
            if (matches(selector.Items[i]))
            {
                selector.SelectedIndex = i;
                return;
            }
        }
    }

    protected override async void OnClosing(WindowClosingEventArgs e)
    {
        base.OnClosing(e);
        if (closeConfirmed || e.Cancel || currentProject is null) return;
        List<UnsavedChange> changes = CollectUnsavedChanges();
        if (changes.Count == 0) return;

        e.Cancel = true;
        string? choice = await new ChoiceDialog(
            "Unsaved changes",
            $"{DescribeUnsavedAreas(changes)} {(changes.Count == 1 ? "has" : "have")} unsaved changes. Save them before closing?",
            ("save", "Save All"), ("discard", "Close Without Saving"), ("cancel", "Cancel")).ShowDialog<string?>(this);
        if (choice == "save")
        {
            List<UnsavedChange> remaining = SaveChanges(changes);
            if (remaining.Count > 0)
            {
                StatusText.Text = $"Not closed: {DescribeUnsavedAreas(remaining)} could not be saved.";
                return;
            }
        }
        else if (choice != "discard")
        {
            return;
        }

        closeConfirmed = true;
        // Closing this window again after the close was cancelled for the question does not always end the
        // program when another window (the Status Log) is open, so the program is shut down instead, which
        // closes every window.
        if (global::Avalonia.Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop) desktop.Shutdown();
        else Close();
    }

    private static string DescribeUnsavedAreas(IReadOnlyList<UnsavedChange> changes) =>
        changes.Count <= 4
            ? string.Join(", ", changes.Select(change => change.Area))
            : $"{string.Join(", ", changes.Take(3).Select(change => change.Area))} and {changes.Count - 3} more";
}
