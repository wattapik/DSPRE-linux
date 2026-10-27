using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Hidden Items tab of the Item Table Editor: the table the game looks a hidden item up in by script ID
/// (<see cref="HiddenItemService"/>), with where each entry is used (the project's hidden item spawnables) and what is wrong
/// with the pairing: entries nothing uses, IDs two entries share, and spawnables whose ID has no entry.</summary>
public partial class MainWindow
{
    private sealed record HiddenItemUseRow(int? HeaderId, string Text)
    {
        public override string ToString() => Text;
    }

    private List<HiddenItemEntry> hiddenItems = [];
    private int hiddenItemCapacity;
    private HiddenItemLayout? hiddenItemLayout;
    private List<HiddenItemUse> hiddenItemUses = [];
    /// <summary>Entry index of each row of the list as filtered.</summary>
    private List<int> hiddenItemShown = [];

    private void LoadHiddenItems()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;

        hiddenItems = [];
        hiddenItemCapacity = 0;
        hiddenItemLayout = null;
        hiddenItemUses = [];
        HiddenItemsFilter.Text = "";

        HiddenItemLayout? layout = HiddenItemService.Locate(currentProject, metadata, out string? problem);
        bool supported = layout is not null;
        HiddenItemsBody.IsVisible = supported;
        HiddenItemsSaveButton.IsEnabled = supported;
        HiddenItemsReloadButton.IsEnabled = supported;
        if (!supported)
        {
            HiddenItemsCount.Text = string.Empty;
            HiddenItemsStatus.Text = problem;
            HiddenItemsProblems.Text = string.Empty;
            return;
        }

        try
        {
            HiddenItemTable table = HiddenItemService.Load(currentProject, metadata);
            hiddenItems = table.Entries;
            hiddenItemEdits.Loaded(hiddenItems, "Hidden items");
            hiddenItemCapacity = table.Capacity;
            hiddenItemLayout = table.Layout;
            hiddenItemUses = HiddenItemUsageService.Find(currentProject, metadata);
            HiddenItemsStatus.Text = $"Hidden items loaded: the table is at ARM9 0x{table.Layout.TableOffset:X} with room for {table.Capacity} entries. Spawnables are read from the saved event files.";
        }
        catch (Exception ex)
        {
            LogUiError("Hidden items could not be loaded", ex);
            HiddenItemsStatus.Text = ex.Message;
        }

        RefreshHiddenItemsList(0);
    }

    private string DescribeHiddenItem(HiddenItemEntry entry, Dictionary<int, int> useCounts, HashSet<ushort> duplicated)
    {
        string name = entry.ItemId < ItemTableItemNames.Count ? ItemTableItemNames[entry.ItemId] : "???";
        string state = duplicated.Contains(entry.ScriptId) ? "  [ID used twice]"
            : useCounts.GetValueOrDefault(entry.ScriptId) is int uses && uses > 0 ? $"  ({uses} on maps)"
            : "  [unused]";
        return $"{HiddenItemUsageService.ScriptBase + entry.ScriptId}: {name} x{entry.Amount}{state}";
    }

    private HiddenItemEntry? SelectedHiddenItem =>
        HiddenItemsList.SelectedIndex >= 0 && HiddenItemsList.SelectedIndex < hiddenItemShown.Count ? hiddenItems[hiddenItemShown[HiddenItemsList.SelectedIndex]] : null;

    private void RefreshHiddenItemsList(int selectEntry)
    {
        Dictionary<int, int> useCounts = hiddenItemUses.GroupBy(use => use.ScriptId).ToDictionary(group => group.Key, group => group.Count());
        HashSet<ushort> duplicated = hiddenItems.GroupBy(entry => entry.ScriptId).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();

        string filter = (HiddenItemsFilter.Text ?? "").Trim();
        hiddenItemShown = [];
        var rows = new List<string>();
        for (int i = 0; i < hiddenItems.Count; i++)
        {
            string row = DescribeHiddenItem(hiddenItems[i], useCounts, duplicated);
            if (filter.Length > 0 && !row.Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
            hiddenItemShown.Add(i);
            rows.Add(row);
        }

        bool wasLoading = loadingItemTableEditor;
        loadingItemTableEditor = true;
        try
        {
            HiddenItemsList.Items.Clear();
            foreach (string row in rows) HiddenItemsList.Items.Add(row);
            int shownIndex = hiddenItemShown.IndexOf(Math.Clamp(selectEntry, 0, Math.Max(0, hiddenItems.Count - 1)));
            HiddenItemsList.SelectedIndex = shownIndex >= 0 ? shownIndex : rows.Count > 0 ? 0 : -1;
        }
        finally
        {
            loadingItemTableEditor = wasLoading;
        }

        HiddenItemsCount.Text = filter.Length > 0 ? $"Showing {rows.Count} of {hiddenItems.Count} entries (room for {hiddenItemCapacity})" : $"Entries: {hiddenItems.Count} / {hiddenItemCapacity}";
        HiddenItemsProblems.Text = DescribeHiddenItemProblems(duplicated);
        ShowSelectedHiddenItem();
    }

    private string DescribeHiddenItemProblems(HashSet<ushort> duplicated)
    {
        var lines = new List<string>();
        var known = hiddenItems.Select(entry => (int)entry.ScriptId).ToHashSet();
        List<IGrouping<int, HiddenItemUse>> orphans = hiddenItemUses.Where(use => !known.Contains(use.ScriptId)).GroupBy(use => use.ScriptId).OrderBy(group => group.Key).ToList();
        if (orphans.Count > 0)
        {
            string ids = string.Join(", ", orphans.Take(8).Select(group => HiddenItemUsageService.ScriptBase + group.Key));
            lines.Add($"{orphans.Sum(group => group.Count())} spawnable(s) use script IDs the table has no entry for ({ids}{(orphans.Count > 8 ? ", ..." : "")}); the game finds nothing there.");
        }

        if (duplicated.Count > 0) lines.Add($"{duplicated.Count} script ID(s) are in the table twice; only the first entry of each is ever found.");
        return string.Join('\n', lines);
    }

    private void ShowSelectedHiddenItem()
    {
        HiddenItemEntry? entry = SelectedHiddenItem;
        bool wasLoading = loadingItemTableEditor;
        loadingItemTableEditor = true;
        try
        {
            HiddenItemSelector.IsEnabled = HiddenItemAmount.IsEnabled = HiddenItemScriptId.IsEnabled = HiddenItemExtra.IsEnabled = entry is not null;
            HiddenItemSelector.SelectedIndex = entry is not null && entry.ItemId < ItemTableItemNames.Count ? entry.ItemId : -1;
            HiddenItemAmount.Value = entry?.Amount ?? 0;
            HiddenItemScriptId.Value = entry?.ScriptId ?? 0;
            HiddenItemExtra.Value = entry?.Extra ?? 0;
            HiddenItemExtraPanel.IsVisible = hiddenItemLayout?.HasExtraByte == true;
            HiddenItemScriptCall.Text = entry is null ? string.Empty : $"Use in a hidden item spawnable: script {HiddenItemUsageService.ScriptBase + entry.ScriptId}";
            ShowHiddenItemUses(entry);
        }
        finally
        {
            loadingItemTableEditor = wasLoading;
        }
    }

    private void ShowHiddenItemUses(HiddenItemEntry? entry)
    {
        if (entry is null || currentProject is null)
        {
            HiddenItemUsesList.ItemsSource = null;
            HiddenItemUsesHeading.Text = "Where it is";
            return;
        }

        List<HiddenItemUse> uses = hiddenItemUses.Where(use => use.ScriptId == entry.ScriptId).ToList();
        HiddenItemUsesHeading.Text = uses.Count == 0 ? "Where it is: no hidden item spawnable uses this ID" : $"Where it is ({uses.Count}) - double-click to open the map";
        HiddenItemUsesList.ItemsSource = uses.Select(use =>
        {
            string place = use.HeaderId is int header
                ? $"Header {header:D3} {currentProject.GetInternalName((ushort)header)} - {FlyWarpLocationOfHeader(header)}"
                : $"Event file {use.EventFileId} (no header uses it)";
            return new HiddenItemUseRow(use.HeaderId, $"{place}, spawnable {use.SpawnableIndex} at ({use.X}, {use.Y})");
        }).ToList();
    }

    private void HiddenItemUses_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (HiddenItemUsesList.SelectedItem is HiddenItemUseRow { HeaderId: int header }) OpenHeaderFromSearch((ushort)header);
    }

    private void HiddenItemsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingItemTableEditor) return;
        ShowSelectedHiddenItem();
    }

    private void HiddenItemsFilter_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (loadingItemTableEditor || hiddenItemLayout is null) return;
        int selected = HiddenItemsList.SelectedIndex >= 0 && HiddenItemsList.SelectedIndex < hiddenItemShown.Count ? hiddenItemShown[HiddenItemsList.SelectedIndex] : 0;
        RefreshHiddenItemsList(selected);
    }

    private void HiddenItemField_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ApplyHiddenItemFields();

    private void HiddenItemField_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e) => ApplyHiddenItemFields();

    private void ApplyHiddenItemFields()
    {
        if (loadingItemTableEditor || SelectedHiddenItem is not HiddenItemEntry entry) return;
        int index = hiddenItems.IndexOf(entry);
        if (HiddenItemSelector.SelectedIndex >= 0) entry.ItemId = (ushort)HiddenItemSelector.SelectedIndex;
        entry.Amount = (byte)Math.Clamp((int)(HiddenItemAmount.Value ?? 0), 0, 255);
        entry.ScriptId = (ushort)Math.Clamp((int)(HiddenItemScriptId.Value ?? 0), 0, ushort.MaxValue);
        entry.Extra = (byte)Math.Clamp((int)(HiddenItemExtra.Value ?? 0), 0, 255);
        RefreshHiddenItemsList(index);
        ReportEdit(hiddenItemEdits, () => hiddenItems, HiddenItemsStatus);
    }

    private void HiddenItemAdd_Click(object? sender, RoutedEventArgs e)
    {
        if (hiddenItems.Count >= hiddenItemCapacity)
        {
            HiddenItemsStatus.Text = $"The table is full: the ROM allows at most {hiddenItemCapacity} entries.";
            return;
        }

        // Prefer an ID a spawnable already points at without an entry, so the new entry fixes it; else the first free one.
        var known = hiddenItems.Select(entry => (int)entry.ScriptId).ToHashSet();
        int orphan = hiddenItemUses.Select(use => use.ScriptId).Where(id => !known.Contains(id) && id <= ushort.MaxValue).DefaultIfEmpty(-1).Min();
        ushort scriptId = orphan >= 0 ? (ushort)orphan : HiddenItemService.FirstFreeScriptId(hiddenItems);

        // Item 1 rather than legacy's item 0: item 0 marks the end of the table when it is read back.
        hiddenItems.Add(new HiddenItemEntry { ItemId = 1, Amount = 1, ScriptId = scriptId, Extra = hiddenItemLayout?.HasExtraByte == true ? (byte)2 : (byte)0 });
        HiddenItemsFilter.Text = "";
        RefreshHiddenItemsList(hiddenItems.Count - 1);
        HiddenItemsList.ScrollIntoView(HiddenItemsList.SelectedIndex);
        ReportEdit(hiddenItemEdits, () => hiddenItems, HiddenItemsStatus);
    }

    private void HiddenItemRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedHiddenItem is not HiddenItemEntry entry) return;
        int index = hiddenItems.IndexOf(entry);
        hiddenItems.RemoveAt(index);
        RefreshHiddenItemsList(index);
        ReportEdit(hiddenItemEdits, () => hiddenItems, HiddenItemsStatus, "Spawnables that referenced the removed script ID need updating by hand.");
    }

    private void ReloadHiddenItems_Click(object? sender, RoutedEventArgs e) => LoadHiddenItems();

    private void SaveHiddenItems_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        try
        {
            HiddenItemService.Save(currentProject, metadata, hiddenItems);
            hiddenItemEdits.Saved(hiddenItems);
            HiddenItemsStatus.Text = $"Hidden items saved ({hiddenItems.Count} / {hiddenItemCapacity}).";
        }
        catch (Exception ex)
        {
            LogUiError("Hidden items could not be saved", ex);
            HiddenItemsStatus.Text = ex.Message;
        }
    }
}
