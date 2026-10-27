using Avalonia.Controls;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The names text.lua gives text archives (see <see cref="TextArchiveNames"/>), shown beside their
/// numbers and edited from the Text Editor.</summary>
public partial class MainWindow
{
    private Dictionary<int, string> textArchiveNames = [];
    private bool showingTextArchiveName;

    private string TextArchiveLabel(int id, string prefix = "Text Archive ") =>
        textArchiveNames.TryGetValue(id, out string? name) ? $"{prefix}{id:D4} - {name}" : $"{prefix}{id:D4}";

    private void RefreshTextArchiveNames()
    {
        if (currentProject is null) return;
        textArchiveNames = TextArchiveNames.Read(currentProject, GetScriptLanguage());
        headerIdCombosStale = true;
    }

    private void ShowTextArchiveName(int id)
    {
        showingTextArchiveName = true;
        try
        {
            TextArchiveNameInput.Text = textArchiveNames.GetValueOrDefault(id, "");
        }
        finally
        {
            showingTextArchiveName = false;
        }
    }

    private bool TextArchiveNameUnsaved() =>
        currentTextArchive is not null && (TextArchiveNameInput.Text ?? "").Trim() != textArchiveNames.GetValueOrDefault(currentTextArchive.Id, "");

    private void TextArchiveNameInput_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (showingTextArchiveName) return;
        RefreshUnsavedMarkers();
    }

    private void SaveTextArchiveName()
    {
        if (currentProject is null || currentTextArchive is null || !TextArchiveNameUnsaved()) return;
        int id = currentTextArchive.Id;
        string name = (TextArchiveNameInput.Text ?? "").Trim().Replace("\r", "").Replace("\n", " ");
        try
        {
            TextArchiveNames.Save(currentProject, id, name);
        }
        catch (Exception ex)
        {
            LogUiError("text.lua could not be saved", ex);
            TextEditorStatus.Text = ex.Message;
            return;
        }

        RefreshTextArchiveNames();
        ShowTextArchiveName(id);
        int selected = TextArchiveSelector.SelectedIndex;
        RunSwitchingDocument(() =>
        {
            for (int i = 0; i < textArchiveIds.Count; i++) TextArchiveSelector.Items[i] = TextArchiveLabel(textArchiveIds[i]);
            TextArchiveSelector.SelectedIndex = selected;
        });
        TextEditorStatus.Text = name.Length > 0 ? $"Text archive {id:D4} is now called \"{name}\" in text.lua." : $"Text archive {id:D4} has no name in text.lua.";
        RefreshUnsavedMarkers();
    }
}
