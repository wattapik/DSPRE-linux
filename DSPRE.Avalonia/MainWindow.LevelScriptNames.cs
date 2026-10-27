using Avalonia.Controls;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The names level.lua gives level scripts (see <see cref="LevelScriptNames"/>), shown beside their
/// numbers and edited from the Level Script Editor.</summary>
public partial class MainWindow
{
    private Dictionary<int, string> levelScriptNames = [];
    private bool showingLevelScriptName;

    private string LevelScriptLabel(int id, string prefix = "Level Script ") =>
        levelScriptNames.TryGetValue(id, out string? name) ? $"{prefix}{id:D4} - {name}" : $"{prefix}{id:D4}";

    /// <summary>The names global.lua and level.lua give scripts, read when a project opens so the header
    /// pane shows them before any script editor has been.</summary>
    private void LoadScriptNamesQuietly()
    {
        try
        {
            RefreshScriptFileNames();
            RefreshLevelScriptNames();
            RefreshTextArchiveNames();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Script names could not be read: {ex.Message}");
        }
    }

    private void RefreshLevelScriptNames()
    {
        if (currentProject is null) return;
        levelScriptNames = LevelScriptNames.Read(currentProject, GetScriptLanguage());
        headerIdCombosStale = true;
        UpdateScriptNameHints();
    }

    private void ShowLevelScriptName(int id)
    {
        showingLevelScriptName = true;
        try
        {
            LevelScriptNameInput.Text = levelScriptNames.GetValueOrDefault(id, "");
        }
        finally
        {
            showingLevelScriptName = false;
        }
    }

    private int? OpenLevelScriptId => currentLevelScript?.Id;

    private bool LevelScriptNameUnsaved() =>
        OpenLevelScriptId is int id && (LevelScriptNameInput.Text ?? "").Trim() != levelScriptNames.GetValueOrDefault(id, "");

    private void LevelScriptNameInput_TextChanged(object? sender, global::Avalonia.Controls.TextChangedEventArgs e)
    {
        if (showingLevelScriptName) return;
        RefreshUnsavedMarkers();
    }

    private void SaveLevelScriptName()
    {
        if (currentProject is null || OpenLevelScriptId is not int id || !LevelScriptNameUnsaved()) return;
        string name = (LevelScriptNameInput.Text ?? "").Trim().Replace("\r", "").Replace("\n", " ");
        try
        {
            LevelScriptNames.Save(currentProject, id, name);
        }
        catch (Exception ex)
        {
            LogUiError("level.lua could not be saved", ex);
            LevelScriptStatus.Text = ex.Message;
            return;
        }

        RefreshLevelScriptNames();
        ShowLevelScriptName(id);
        int selected = LevelScriptSelector.SelectedIndex;
        RunSwitchingDocument(() =>
        {
            for (int i = 0; i < levelScriptIds.Count; i++) LevelScriptSelector.Items[i] = LevelScriptLabel(levelScriptIds[i]);
            LevelScriptSelector.SelectedIndex = selected;
        });
        LevelScriptStatus.Text = name.Length > 0 ? $"Level script {id:D4} is now called \"{name}\" in level.lua." : $"Level script {id:D4} has no name in level.lua.";
        RefreshUnsavedMarkers();
    }
}

public partial class MainWindow
{
    private (ComboBox Selector, TextBlock Count)? FindTarget(object? sender) =>
        ReferenceEquals(sender, ScriptFileFindInput) ? (ScriptFileSelector, ScriptFileFindCount)
        : ReferenceEquals(sender, LevelScriptFindInput) ? (LevelScriptSelector, LevelScriptFindCount)
        : null;

    private static List<int> FileFindMatches(ComboBox selector, string? query)
    {
        query = query?.Trim() ?? "";
        if (query.Length == 0) return [];
        return Enumerable.Range(0, selector.ItemCount)
            .Where(index => selector.Items[index]?.ToString()?.Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            .ToList();
    }

    private void FileFindInput_TextChanged(object? sender, global::Avalonia.Controls.TextChangedEventArgs e)
    {
        if (FindTarget(sender) is not var (selector, count) || sender is not TextBox box) return;
        int matches = FileFindMatches(selector, box.Text).Count;
        count.Text = box.Text?.Trim().Length > 0 ? $"{matches} match{(matches == 1 ? "" : "es")}" : string.Empty;
    }

    /// <summary>Selecting a file loads it, so the search moves the selection only on Enter, to the next
    /// match after the open file, wrapping around.</summary>
    private void FileFindInput_KeyDown(object? sender, global::Avalonia.Input.KeyEventArgs e)
    {
        if (e.Key != global::Avalonia.Input.Key.Enter || FindTarget(sender) is not var (selector, count) || sender is not TextBox box) return;
        e.Handled = true;
        List<int> matches = FileFindMatches(selector, box.Text);
        if (matches.Count == 0)
        {
            count.Text = "no match";
            return;
        }

        int next = matches.FirstOrDefault(index => index > selector.SelectedIndex, matches[0]);
        count.Text = $"{matches.IndexOf(next) + 1} of {matches.Count}";
        selector.SelectedIndex = next;
    }
}
