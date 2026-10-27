using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Media;
using DSPRE.Core;
using System.Diagnostics;

namespace DSPRE.Avalonia;

/// <summary>The Character Map page: legacy's Charmap Manager (DS_Map/Resources/CharMaps/CharMapManagerForm.cs).</summary>
public partial class MainWindow
{
    private static readonly IBrush CharMapWarningBrush = new SolidColorBrush(Color.Parse("#E8B75F"));
    private static readonly IBrush CharMapInfoBrush = new SolidColorBrush(Color.Parse("#AAB7C4"));
    private CharMap? charMap;
    private bool charMapIsCustom;
    private bool charMapDirty;

    private sealed record CharMapRow(ushort Code, string Character, string? Alias)
    {
        public override string ToString() => Alias is null ? $"0x{Code:X4}  <->  {Character}" : $"0x{Code:X4}  <-   {Alias}   (alias)";
    }

    private sealed record CharMapAliasRow(ushort Code, string Character, string Alias)
    {
        public override string ToString() => $"{Alias}  ->  {Character}  (0x{Code:X4})";
    }

    private void LoadCharMapPage()
    {
        // Coming back to the page keeps unsaved alias edits.
        if (charMap is null || !charMapDirty) LoadCharMapFromDisk();
        RefreshCharMapView();
    }

    private void LoadCharMapFromDisk()
    {
        charMapIsCustom = File.Exists(CharMapStore.CustomPath);
        string path = charMapIsCustom ? CharMapStore.CustomPath : CharMapStore.DefaultPath(ToolsDirectory);
        charMapDirty = false;
        try
        {
            charMap = File.Exists(path) ? CharMapStore.Load(path) : null;
            CharMapStatus.Text = charMap is null ? $"No character map found at {path}." : "";
        }
        catch (Exception ex)
        {
            LogUiError("The character map could not be read", ex);
            charMap = null;
            CharMapStatus.Text = $"{path} could not be read: {ex.Message}";
        }
    }

    private void RefreshCharMapView(ushort? selectCode = null, string? selectAlias = null)
    {
        string defaultPath = CharMapStore.DefaultPath(ToolsDirectory);
        string defaultVersion = File.Exists(defaultPath) ? CharMapStore.GetVersion(defaultPath).ToString() : "missing";
        bool outdated = charMapIsCustom && CharMapStore.IsCustomOutdated(ToolsDirectory);
        CharMapSourceText.Text = charMapIsCustom
            ? $"Using your custom character map (version {charMap?.Metadata.Version}) for all text; the default map is version {defaultVersion}." +
              (outdated ? " Your map was made from an older default map: use Rebase on Default to pick up the default's changes." : "") +
              (charMapDirty ? "  Unsaved changes." : "")
            : $"Using the default character map (version {defaultVersion}), which is read-only. Create a custom map to add aliases.";
        CharMapSourceText.Foreground = outdated || charMapDirty ? CharMapWarningBrush : CharMapInfoBrush;

        CharMapCreateButton.IsEnabled = File.Exists(defaultPath);
        CharMapSaveButton.IsEnabled = charMapIsCustom && charMapDirty;
        CharMapReloadButton.IsEnabled = charMapIsCustom;
        CharMapRebaseButton.IsEnabled = charMapIsCustom && charMap is not null && File.Exists(defaultPath);
        CharMapDeleteButton.IsEnabled = charMapIsCustom;
        CharMapOpenFileButton.IsEnabled = charMapIsCustom;
        CharMapAddAliasButton.IsEnabled = charMapIsCustom && charMap is not null;
        CharMapRemoveAliasButton.IsEnabled = charMapIsCustom && charMap is not null;
        CharMapAliasBox.IsEnabled = charMapIsCustom && charMap is not null;

        RefreshCharMapList(selectCode);
        var aliasRows = new List<CharMapAliasRow>();
        if (charMap is not null)
        {
            foreach (ushort code in charMap.GetAllCodes().Order())
            {
                CharMapEntry entry = charMap.GetEntry(code)!;
                foreach (string alias in entry.Aliases ?? []) aliasRows.Add(new CharMapAliasRow(code, entry.Character, alias));
            }
        }

        CharMapAliasList.ItemsSource = aliasRows;
        if (selectAlias is not null) CharMapAliasList.SelectedItem = aliasRows.FirstOrDefault(row => row.Alias == selectAlias);
    }

    private void RefreshCharMapList(ushort? selectCode = null)
    {
        var rows = new List<CharMapRow>();
        if (charMap is not null)
        {
            string search = (CharMapSearchBox.Text ?? "").Trim();
            bool byCode = search.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            foreach (ushort code in charMap.GetAllCodes().Order())
            {
                CharMapEntry entry = charMap.GetEntry(code)!;
                bool codeMatches = byCode && $"0x{code:X4}".Contains(search, StringComparison.OrdinalIgnoreCase);
                if (search.Length == 0 || codeMatches || (!byCode && entry.Character.Contains(search))) rows.Add(new CharMapRow(code, entry.Character, null));
                foreach (string alias in entry.Aliases ?? [])
                {
                    if (search.Length == 0 || codeMatches || (!byCode && (alias.Contains(search) || entry.Character.Contains(search)))) rows.Add(new CharMapRow(code, entry.Character, alias));
                }
            }
        }

        CharMapList.ItemsSource = rows;
        if (selectCode is ushort selected) CharMapList.SelectedItem = rows.FirstOrDefault(row => row.Code == selected && row.Alias is null);
        ShowSelectedCharMapCode();
    }

    private void CharMapSearch_TextChanged(object? sender, TextChangedEventArgs e) => RefreshCharMapList((CharMapList.SelectedItem as CharMapRow)?.Code);

    private void CharMapList_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ShowSelectedCharMapCode();

    private void ShowSelectedCharMapCode()
    {
        if (CharMapList.SelectedItem is not CharMapRow row || charMap?.GetEntry(row.Code) is not CharMapEntry entry)
        {
            CharMapSelectedText.Text = "Pick a character on the left.";
            return;
        }

        string aliases = entry.Aliases is { Count: > 0 } list ? string.Join("  ", list) : "none";
        CharMapSelectedText.Text = $"0x{row.Code:X4}   {entry.Character}\nAliases: {aliases}";
    }

    private async void CharMapList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (CharMapList.SelectedItem is not CharMapRow row || Clipboard is null) return;
        string text = row.Alias ?? row.Character;
        await Clipboard.SetTextAsync(text);
        CharMapStatus.Text = $"Copied {text}.";
    }

    private void MarkCharMapDirty(ushort? selectCode = null, string? selectAlias = null)
    {
        charMapDirty = true;
        RefreshCharMapView(selectCode, selectAlias);
    }

    private async void CharMapAddAlias_Click(object? sender, RoutedEventArgs e)
    {
        if (charMap is null || !charMapIsCustom) return;
        if (CharMapList.SelectedItem is not CharMapRow row)
        {
            CharMapStatus.Text = "Pick the character the alias stands for first.";
            return;
        }

        string alias = (CharMapAliasBox.Text ?? "").Trim();
        if (alias.Length == 0)
        {
            CharMapStatus.Text = "Type the alias first.";
            return;
        }

        if (alias.Length == 1)
        {
            string? choice = await new ChoiceDialog("Single Character Alias",
                $"An alias that is a single character without brackets can be mistaken for ordinary text when encoding. Use [{alias}] instead?",
                ("bracket", $"Use [{alias}]"), ("plain", $"Keep {alias}"), ("cancel", "Cancel")).ShowDialog<string?>(this);
            if (choice is null or "cancel") return;
            if (choice == "bracket") alias = $"[{alias}]";
        }
        else if (!(alias.StartsWith('[') && alias.EndsWith(']')))
        {
            alias = $"[{alias}]";
        }

        if (charMap.FindCode(alias) is ushort existing)
        {
            CharMapStatus.Text = $"{alias} is already a character or alias of 0x{existing:X4}.";
            return;
        }

        charMap.GetEntry(row.Code)!.AddAlias(alias);
        CharMapAliasBox.Text = "";
        MarkCharMapDirty(row.Code, alias);
        CharMapStatus.Text = $"Added {alias} for 0x{row.Code:X4}. Save to use it.";
    }

    private void CharMapRemoveAlias_Click(object? sender, RoutedEventArgs e)
    {
        if (charMap is null || !charMapIsCustom) return;
        if (CharMapAliasList.SelectedItem is not CharMapAliasRow row)
        {
            CharMapStatus.Text = "Pick an alias in the list first.";
            return;
        }

        if (charMap.GetEntry(row.Code)?.RemoveAlias(row.Alias) != true) return;
        MarkCharMapDirty((CharMapList.SelectedItem as CharMapRow)?.Code);
        CharMapStatus.Text = $"Removed {row.Alias}. Save to apply.";
    }

    private void CharMapSave_Click(object? sender, RoutedEventArgs e)
    {
        if (charMap is null || !charMapIsCustom) return;
        try
        {
            CharMapStore.SaveCustom(charMap);
            charMapDirty = false;
            RefreshCharMapView((CharMapList.SelectedItem as CharMapRow)?.Code);
            CharMapStatus.Text = $"Saved {CharMapStore.CustomPath}.";
        }
        catch (Exception ex)
        {
            LogUiError("The character map could not be saved", ex);
            CharMapStatus.Text = $"The character map could not be saved: {ex.Message}";
        }
    }

    private async Task<bool> ConfirmDiscardCharMapAsync() =>
        !charMapDirty || await ConfirmAsync("Unsaved Changes", "The custom character map has unsaved changes. Discard them?", "Discard");

    private async void CharMapReload_Click(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmDiscardCharMapAsync()) return;
        LoadCharMapFromDisk();
        RefreshCharMapView();
        if (charMap is not null) CharMapStatus.Text = "Reloaded.";
    }

    private async void CharMapCreate_Click(object? sender, RoutedEventArgs e)
    {
        if (charMapIsCustom && !await ConfirmAsync("Create Custom Map", "Replace your custom character map with a fresh copy of the default one? Your aliases and changes are lost.", "Replace")) return;
        try
        {
            CharMapStore.CreateCustom(ToolsDirectory);
        }
        catch (Exception ex)
        {
            LogUiError("The custom character map could not be created", ex);
            CharMapStatus.Text = $"The custom character map could not be created: {ex.Message}";
            return;
        }

        LoadCharMapFromDisk();
        RefreshCharMapView();
        CharMapStatus.Text = $"Created {CharMapStore.CustomPath} from the default map; it is used for all text from now on.";
    }

    private async void CharMapDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmAsync("Delete Custom Map", "Delete your custom character map? Text is read and written with the default map again. This cannot be undone.", "Delete")) return;
        try
        {
            CharMapStore.DeleteCustom();
        }
        catch (Exception ex)
        {
            LogUiError("The custom character map could not be deleted", ex);
            CharMapStatus.Text = $"The custom character map could not be deleted: {ex.Message}";
            return;
        }

        LoadCharMapFromDisk();
        RefreshCharMapView();
        CharMapStatus.Text = "Custom character map deleted; the default map is used again.";
    }

    private void CharMapOpenFile_Click(object? sender, RoutedEventArgs e)
    {
        if (!File.Exists(CharMapStore.CustomPath)) return;
        try
        {
            string opener = OperatingSystem.IsWindows() ? "explorer" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            Process.Start(new ProcessStartInfo(opener) { ArgumentList = { CharMapStore.CustomPath }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            CharMapStatus.Text = $"The file could not be opened: {ex.Message}";
        }
    }

    private async void CharMapRebase_Click(object? sender, RoutedEventArgs e)
    {
        if (charMap is null || !charMapIsCustom) return;
        if (!CharMapStore.IsCustomOutdated(ToolsDirectory) &&
            !await ConfirmAsync("Rebase Character Map", "Your custom map is already up to date with the default map. Rebase it anyway?", "Rebase"))
        {
            return;
        }

        string? choice = await new ChoiceDialog("Rebase Character Map",
            "Your custom map is merged into the current default map. When both give a code different characters:\n\n" +
            "Prefer Custom keeps your character and both maps' aliases.\n" +
            "Prefer Base keeps the default's character and adds your aliases.\n" +
            "Replace Base keeps only your map, taking the default's version number.",
            ("custom", "Prefer Custom"), ("base", "Prefer Base"), ("replace", "Replace Base"), ("cancel", "Cancel")).ShowDialog<string?>(this);
        CharMapMergeStrategy strategy;
        switch (choice)
        {
            case "custom": strategy = CharMapMergeStrategy.PreferCustom; break;
            case "base": strategy = CharMapMergeStrategy.PreferBase; break;
            case "replace": strategy = CharMapMergeStrategy.ReplaceBase; break;
            default: return;
        }

        CharMapMergeResult result;
        try
        {
            result = CharMapStore.Merge(CharMapStore.Load(CharMapStore.DefaultPath(ToolsDirectory)), charMap, strategy);
        }
        catch (Exception ex)
        {
            LogUiError("The character map could not be rebased", ex);
            CharMapStatus.Text = $"The character map could not be rebased: {ex.Message}";
            return;
        }

        charMap = result.MergedMap;
        MarkCharMapDirty();
        CharMapStatus.Text = "Rebased onto the default map. Save to keep it.";
        await ShowCharMapMergeResultAsync(result);
    }

    private async Task ShowCharMapMergeResultAsync(CharMapMergeResult result)
    {
        var text = new TextBox
        {
            Text = result.Summary + (result.Conflicts.Count > 0 ? "\n\nConflicts:\n" + string.Join("\n", result.Conflicts) : ""),
            IsReadOnly = true,
            AcceptsReturn = true,
            TextWrapping = TextWrapping.NoWrap,
            FontFamily = new FontFamily("monospace")
        };
        var close = new Button { Content = "Close", Padding = new global::Avalonia.Thickness(14, 6), HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Right };
        var layout = new DockPanel { Margin = new global::Avalonia.Thickness(14) };
        DockPanel.SetDock(close, Dock.Bottom);
        layout.Children.Add(close);
        layout.Children.Add(text);
        var window = new Window { Title = "Rebase Result", Width = 640, Height = 420, Content = layout, WindowStartupLocation = WindowStartupLocation.CenterOwner };
        close.Click += (_, _) => window.Close();
        await window.ShowDialog(this);
    }
}
