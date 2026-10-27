using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Header Manager: every header with the matrix and area data it uses and the maps it reaches, the maps no header
/// reaches, and the actions that join them (point a header at another matrix, give it a map, add or remove headers). Changes go
/// through <see cref="MapHeaderAssignmentService"/> and the header store at once, like the Map Editor's "Header of map" dropdown.</summary>
public partial class MainWindow
{
    private readonly ListBox headerManagerList = new() { SelectionMode = SelectionMode.Single };
    private readonly TextBox headerManagerFilter = new() { Watermark = "Search headers..." };
    private readonly TextBlock headerManagerTitle = new() { FontWeight = global::Avalonia.Media.FontWeight.SemiBold, FontSize = 16 };
    private readonly TextBlock headerManagerSummary = new() { Foreground = ExtraMutedBrush, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
    private readonly TextBox headerManagerName = new() { MaxLength = 16, Width = 200 };
    private readonly CheckBox headerManagerKanto = new() { Content = "Kanto flag" };
    private sealed class ManagedField(string label, Func<MapHeaderRecord, int> get, Func<MapHeaderRecord, int, MapHeaderRecord> set, int max, Func<ComboBox>? source, bool hgssOnly)
    {
        public string Label { get; } = label;
        public Func<MapHeaderRecord, int> Get { get; } = get;
        public Func<MapHeaderRecord, int, MapHeaderRecord> Set { get; } = set;
        public int Max { get; } = max;
        public Func<ComboBox>? Source { get; } = source;
        public bool HgssOnly { get; } = hgssOnly;
        public NumericUpDown? Box { get; init; }
        public ComboBox? Combo { get; init; }
        public Control Host { get; set; } = null!;
    }

    private readonly List<ManagedField> headerManagerFields = [];
    private readonly Dictionary<ushort, (MapHeaderRecord Record, string Name)> headerManagerPending = [];
    private readonly TextBlock headerManagerDirty = new() { Foreground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#FFC857")), FontSize = 11 };
    private readonly ListBox headerManagerMaps = new() { Height = 170 };
    private readonly ListBox headerManagerUnreached = new() { Height = 170 };
    private readonly TextBlock headerManagerStatus = new() { Foreground = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#65D8C7")), TextWrapping = global::Avalonia.Media.TextWrapping.Wrap };
    private bool loadingHeaderManager;

    private static Border Card(string title, string? note, params Control[] children)
    {
        var stack = new StackPanel { Spacing = 8 };
        stack.Children.Add(new TextBlock { Text = title, FontWeight = global::Avalonia.Media.FontWeight.SemiBold, FontSize = 14 });
        if (note is not null) stack.Children.Add(new TextBlock { Text = note, Foreground = ExtraMutedBrush, FontSize = 11, TextWrapping = global::Avalonia.Media.TextWrapping.Wrap });
        foreach (Control child in children) stack.Children.Add(child);
        return new Border
        {
            Background = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#1B252E")),
            BorderBrush = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#33414D")),
            BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4), Padding = new Thickness(12), Child = stack
        };
    }

    /// <summary>A field the Map Editor's Header pane also has as a list (script file names, music titles, location names...) takes that
    /// list's items, so the two show the same names; the rest are numbers.</summary>
    private void AddHeaderManagerField(string label, Func<MapHeaderRecord, int> get, Func<MapHeaderRecord, int, MapHeaderRecord> set, int max, Func<ComboBox>? source = null, bool hgssOnly = false)
    {
        NumericUpDown? box = null;
        ComboBox? combo = null;
        if (source is null)
        {
            box = new NumericUpDown { Minimum = 0, Maximum = max, Increment = 1, FormatString = "0", Width = 130 };
            box.ValueChanged += (_, _) => HeaderManagerFieldEdited();
        }
        else
        {
            combo = new ComboBox { Width = 330, MaxDropDownHeight = 360 };
            combo.SelectionChanged += (_, _) => HeaderManagerFieldEdited();
        }

        var field = new ManagedField(label, get, set, max, source, hgssOnly) { Box = box, Combo = combo };
        Control editor = (Control?)box ?? combo!;
        if (label == "Location name")
        {
            editor = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 6,
                Children = { combo!, ActionButton("New Name...", "Add a new location name (a new message in the location names text archive) and pick it", async (_, _) => { if (await PromptAddLocationNameAsync() is int id) SelectManagedChoice(combo!, id); }) }
            };
        }

        field.Host = new StackPanel { Margin = new Thickness(0, 0, 12, 8), Children = { new TextBlock { Text = label, Foreground = ExtraMutedBrush, FontSize = 11 }, editor } };
        headerManagerFields.Add(field);
    }

    private void FillHeaderManagerCombos()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        FillHeaderIdCombos();
        bool hgss = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;
        loadingManagedFields = true;
        try
        {
            foreach (ManagedField field in headerManagerFields)
            {
                field.Host.IsVisible = !field.HgssOnly || hgss;
                if (field.Combo is null || field.Source is null) continue;
                field.Combo.Items.Clear();
                if (field.Label == "Wild encounters") field.Combo.Items.Add(new IdChoice(NoWildEncounters, "No wild encounters"));
                foreach (object item in field.Source().Items.Cast<object>().Where(item => item is IdChoice or HeaderOption)) field.Combo.Items.Add(item);
            }

            headerManagerKanto.IsVisible = hgss;
        }
        finally
        {
            loadingManagedFields = false;
        }
    }

    private static void SelectManagedChoice(ComboBox combo, int id)
    {
        object? found = combo.Items.FirstOrDefault(item => item is IdChoice choice ? choice.Id == id : item is HeaderOption option && option.Id == id);
        if (found is null)
        {
            found = combo.Items.OfType<HeaderOption>().Any() ? new HeaderOption((ushort)id, "Unknown ID") : new IdChoice(id, $"{id} (not in the project)");
            combo.Items.Add(found);
        }

        combo.SelectedItem = found;
    }

    private static int ManagedChoiceId(ComboBox combo, int fallback) => combo.SelectedItem switch
    {
        IdChoice choice => choice.Id,
        HeaderOption option => option.Id,
        _ => fallback
    };

    private int ManagedFieldValue(ManagedField field, int fallback) =>
        field.Combo is { } combo ? ManagedChoiceId(combo, fallback) : (int)Math.Clamp(field.Box!.Value ?? 0, 0, field.Max);

    private Control BuildHeaderManagerPanel()
    {
        headerManagerList.SelectionChanged += (_, _) => ShowManagedHeader();
        headerManagerFilter.TextChanged += (_, _) => FillHeaderManagerList(SelectedManagedHeaderId());
        headerManagerMaps.DoubleTapped += (_, _) => OpenManagedMap(headerManagerMaps);
        headerManagerUnreached.DoubleTapped += (_, _) => OpenManagedMap(headerManagerUnreached);
        headerManagerName.TextChanged += (_, _) => HeaderManagerFieldEdited();
        headerManagerKanto.IsCheckedChanged += (_, _) => HeaderManagerFieldEdited();

        AddHeaderManagerField("Area data", r => r.AreaDataId, (r, v) => r with { AreaDataId = (byte)v }, 255, () => AreaDataInput);
        AddHeaderManagerField("Matrix", r => r.MatrixId, (r, v) => r with { MatrixId = (ushort)v }, ushort.MaxValue, () => MatrixInput);
        AddHeaderManagerField("Script file", r => r.ScriptFileId, (r, v) => r with { ScriptFileId = (ushort)v }, ushort.MaxValue, () => ScriptInput);
        AddHeaderManagerField("Level script", r => r.LevelScriptId, (r, v) => r with { LevelScriptId = (ushort)v }, ushort.MaxValue, () => LevelScriptInput);
        AddHeaderManagerField("Text archive", r => r.TextArchiveId, (r, v) => r with { TextArchiveId = (ushort)v }, ushort.MaxValue, () => TextArchiveInput);
        AddHeaderManagerField("Event file", r => r.EventFileId, (r, v) => r with { EventFileId = (ushort)v }, ushort.MaxValue, () => EventInput);
        AddHeaderManagerField("Wild encounters", r => r.WildPokemonId, (r, v) => r with { WildPokemonId = (ushort)v }, ushort.MaxValue, () => WildPokemonInput);
        AddHeaderManagerField("Music (day)", r => r.MusicDayId, (r, v) => r with { MusicDayId = (ushort)v }, ushort.MaxValue, () => MusicDayInput);
        AddHeaderManagerField("Music (night)", r => r.MusicNightId, (r, v) => r with { MusicNightId = (ushort)v }, ushort.MaxValue, () => MusicNightInput);
        AddHeaderManagerField("Location name", r => r.LocationNameId, (r, v) => r with { LocationNameId = (ushort)v }, ushort.MaxValue, () => LocationInput);
        AddHeaderManagerField("Location specifier", r => r.LocationSpecifier, (r, v) => r with { LocationSpecifier = (byte)v }, 255, () => LocationSpecifierInput);
        AddHeaderManagerField("Area icon", r => r.AreaIcon, (r, v) => r with { AreaIcon = (byte)v }, 255, () => AreaIconInput);
        AddHeaderManagerField("Weather", r => r.WeatherId, (r, v) => r with { WeatherId = (byte)v }, 255, () => WeatherInput);
        AddHeaderManagerField("Camera angle", r => r.CameraAngleId, (r, v) => r with { CameraAngleId = (byte)v }, 255, () => CameraInput);
        AddHeaderManagerField("Battle background", r => r.BattleBackground, (r, v) => r with { BattleBackground = (byte)v }, 255);
        AddHeaderManagerField("Flags", r => r.Flags, (r, v) => r with { Flags = (byte)v }, 255);
        AddHeaderManagerField("Follow mode", r => r.FollowMode, (r, v) => r with { FollowMode = (byte)v }, 255, hgssOnly: true);
        AddHeaderManagerField("Location type", r => r.LocationType, (r, v) => r with { LocationType = (byte)v }, 255, hgssOnly: true);
        AddHeaderManagerField("World map X", r => r.WorldMapX, (r, v) => r with { WorldMapX = (byte)v }, 255, hgssOnly: true);
        AddHeaderManagerField("World map Y", r => r.WorldMapY, (r, v) => r with { WorldMapY = (byte)v }, 255, hgssOnly: true);
        AddHeaderManagerField("Unknown 0", r => r.Unknown0, (r, v) => r with { Unknown0 = (byte)v }, 255, hgssOnly: true);
        AddHeaderManagerField("Unknown 1", r => r.Unknown1, (r, v) => r with { Unknown1 = (byte)v }, 255);

        var values = new WrapPanel { Orientation = Orientation.Horizontal };
        values.Children.Add(new StackPanel { Margin = new Thickness(0, 0, 12, 8), Children = { new TextBlock { Text = "Internal name", Foreground = ExtraMutedBrush, FontSize = 11 }, headerManagerName } });
        foreach (ManagedField field in headerManagerFields) values.Children.Add(field.Host);
        values.Children.Add(new StackPanel { Margin = new Thickness(0, 16, 12, 8), Children = { headerManagerKanto } });

        Border header = Card("Selected header", null,
            headerManagerTitle,
            headerManagerSummary,
            values,
            new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    ActionButton("Save Header", "Write these values to the project", (_, _) => SaveManagedHeader()),
                    ActionButton("Revert", "Put back the values the project has", (_, _) => RevertManagedHeader()),
                    ActionButton("Open in Map Editor", "Show this header in the Map Editor", (_, _) => OpenManagedHeaderInMapEditor()),
                    headerManagerDirty
                }
            },
            new TextBlock { Text = "Maps this header reaches (double-click to open one)", FontWeight = global::Avalonia.Media.FontWeight.SemiBold },
            headerManagerMaps);

        var divider = new StackPanel
        {
            Spacing = 4, Margin = new Thickness(0, 10),
            Children =
            {
                new Border { Height = 3, Background = new global::Avalonia.Media.SolidColorBrush(global::Avalonia.Media.Color.Parse("#65D8C7")), CornerRadius = new CornerRadius(2) },
                new TextBlock { Text = "WHOLE PROJECT - the headers above are not involved until you use a button below", Foreground = ExtraMutedBrush, FontSize = 11 }
            }
        };

        Border project = Card("Maps no header reaches",
            "A map here has no area data in the Map Editor, so it draws untextured. Giving it to a header puts it under that header (the one picked in the list on the left) and points the header at the map's matrix.",
            headerManagerUnreached,
            new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 8,
                Children =
                {
                    ActionButton("Give Selected Map To Header", "Make the selected map belong to the header picked on the left", (_, _) => GiveMapToManagedHeader()),
                    ActionButton("Add Header", "Add a copy of header 0 named NEWMAP (needs the Dynamic Header Allocation patch)", (_, _) => AddHeader_Click(null, new global::Avalonia.Interactivity.RoutedEventArgs())),
                    ActionButton("Remove Last Header", "Delete the last header (needs the Dynamic Header Allocation patch)", (_, _) => RemoveLastHeader_Click(null, new global::Avalonia.Interactivity.RoutedEventArgs()))
                }
            });

        var content = new StackPanel { Spacing = 8, Children = { header, divider, project } };
        return EditorPage(SideList("Headers", headerManagerFilter, headerManagerList), new ScrollViewer { Content = content, Padding = new Thickness(0, 0, 12, 0) }, headerManagerStatus);
    }

    private ushort? SelectedManagedHeaderId() => headerManagerList.SelectedItem is HeaderListEntry entry ? entry.Id : null;

    private void ConfigureHeaderManager()
    {
        if (!TryGetGame(out _, out _) || headerStore is null) return;
        EnsureMapEditorDataLoaded();
        mapLocationNameIds = null;
        matrixLocationNameIds = null;
        EnsureLocationLookupsLoaded();
        FillHeaderManagerCombos();
        FillHeaderManagerList(SelectedManagedHeaderId() ?? 0);
    }

    private void FillHeaderManagerList(ushort? keep)
    {
        loadingHeaderManager = true;
        try
        {
            string filter = headerManagerFilter.Text?.Trim() ?? string.Empty;
            headerManagerList.Items.Clear();
            foreach (HeaderListEntry entry in allHeaders)
            {
                if (filter.Length > 0 && !entry.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase)) continue;
                headerManagerList.Items.Add(entry);
            }

            headerManagerList.SelectedItem = headerManagerList.Items.OfType<HeaderListEntry>().FirstOrDefault(entry => entry.Id == keep) ?? headerManagerList.Items.OfType<HeaderListEntry>().FirstOrDefault();
        }
        finally
        {
            loadingHeaderManager = false;
        }

        ShowManagedHeader();
    }

    private void ShowManagedHeader()
    {
        if (loadingHeaderManager) return;
        headerManagerUnreached.Items.Clear();
        foreach (MapViewEntry map in mapEntries.Where(map => !mapHeaderIds.ContainsKey(map.Id))) headerManagerUnreached.Items.Add(map);

        headerManagerMaps.Items.Clear();
        if (headerStore is null || SelectedManagedHeaderId() is not ushort id)
        {
            headerManagerTitle.Text = "Pick a header";
            headerManagerSummary.Text = string.Empty;
            return;
        }

        MapHeaderRecord header = headerStore.Load(id);
        HeaderListEntry entry = allHeaders[id];
        headerManagerTitle.Text = $"{id:D4} - {entry.InternalName}";
        foreach (MapViewEntry map in mapEntries.Where(map => mapHeaderIds.TryGetValue(map.Id, out ushort owner) && owner == id)) headerManagerMaps.Items.Add(map);
        string pack = mapAreaData.Values.FirstOrDefault(area => area.Id == header.AreaDataId) is { } area ? $", map texture pack {area.MapTileset:D2}" : string.Empty;
        headerManagerSummary.Text = $"Location: {entry.LocationName}. Matrix {header.MatrixId:D4}, area data {header.AreaDataId:D3}{pack}, event file {header.EventFileId}, script file {header.ScriptFileId}. Reaches {headerManagerMaps.Items.Count} map(s); {headerManagerUnreached.Items.Count} map(s) in the project have no header.";
        LoadManagedHeaderFields(id, header);
    }

    private bool loadingManagedFields;

    private void LoadManagedHeaderFields(ushort id, MapHeaderRecord saved)
    {
        (MapHeaderRecord record, string name) = headerManagerPending.TryGetValue(id, out var pending) ? pending : (saved, currentProject!.GetInternalName(id));
        loadingManagedFields = true;
        try
        {
            headerManagerName.Text = name;
            foreach (ManagedField field in headerManagerFields)
            {
                if (field.Combo is { } combo) SelectManagedChoice(combo, field.Get(record));
                else field.Box!.Value = field.Get(record);
            }
            headerManagerKanto.IsChecked = record.KantoFlag;
        }
        finally
        {
            loadingManagedFields = false;
        }

        headerManagerDirty.Text = headerManagerPending.ContainsKey(id) ? "Unsaved changes" : string.Empty;
    }

    private (MapHeaderRecord Record, string Name) ReadManagedHeaderFields(MapHeaderRecord saved)
    {
        MapHeaderRecord record = saved with { KantoFlag = headerManagerKanto.IsChecked == true };
        foreach (ManagedField field in headerManagerFields) record = field.Set(record, ManagedFieldValue(field, field.Get(saved)));
        return (record, headerManagerName.Text ?? string.Empty);
    }

    private void HeaderManagerFieldEdited()
    {
        if (loadingManagedFields || loadingHeaderManager || headerStore is null || currentProject is null || SelectedManagedHeaderId() is not ushort id) return;
        MapHeaderRecord saved = headerStore.Load(id);
        (MapHeaderRecord record, string name) = ReadManagedHeaderFields(saved);
        if (record == saved && name == currentProject.GetInternalName(id)) headerManagerPending.Remove(id);
        else headerManagerPending[id] = (record, name);
        headerManagerDirty.Text = headerManagerPending.ContainsKey(id) ? "Unsaved changes" : string.Empty;
        RefreshUnsavedMarkers();
    }

    private void RevertManagedHeader()
    {
        if (headerStore is null || SelectedManagedHeaderId() is not ushort id) return;
        headerManagerPending.Remove(id);
        LoadManagedHeaderFields(id, headerStore.Load(id));
        RefreshUnsavedMarkers();
    }

    private void SaveManagedHeader() => SaveManagedHeaders(SelectedManagedHeaderId() is ushort id ? [id] : []);

    private void SaveManagedHeaders(IEnumerable<ushort> ids)
    {
        if (currentProject is null || headerStore is null) return;
        if (!SaveOpenHeaderIfAny()) return;
        var changedAreaData = new List<ushort>();
        ushort last = 0;
        try
        {
            foreach (ushort id in ids.Where(headerManagerPending.ContainsKey).ToList())
            {
                (MapHeaderRecord record, string name) = headerManagerPending[id];
                if (!MatrixService.GetIds(currentProject).Contains(record.MatrixId)) throw new InvalidOperationException($"Header {id:D4}: there is no matrix {record.MatrixId:D4}.");
                MapHeaderRecord before = headerStore.Load(id);
                headerStore.Save(record);
                currentProject.SaveInternalName(id, name);
                headerManagerPending.Remove(id);
                if (before.AreaDataId != record.AreaDataId || before.MatrixId != record.MatrixId) changedAreaData.Add(record.AreaDataId);
                last = id;
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            LogUiError("The header could not be saved", ex);
            headerManagerStatus.Text = $"The header could not be saved: {ex.Message}";
            return;
        }

        ReloadAfterHeaderManagerChange(SelectedManagedHeaderId() ?? last);
        foreach (ushort areaData in changedAreaData.Distinct()) RedrawMapViewsUsingAreaData(areaData);
        headerManagerStatus.Text = "Header saved.";
    }

    private void GiveMapToManagedHeader()
    {
        if (currentProject is null || headerStore is null || SelectedManagedHeaderId() is not ushort id) return;
        if (headerManagerUnreached.SelectedItem is not MapViewEntry map)
        {
            headerManagerStatus.Text = "Pick a map from the list of maps no header reaches.";
            return;
        }

        if (!SaveOpenHeaderIfAny()) return;
        try
        {
            MapHeaderAssignmentService.Result result = MapHeaderAssignmentService.Assign(currentProject, headerStore, map.Id, id);
            ReloadAfterHeaderManagerChange(id);
            headerManagerStatus.Text = $"Map {map.Id:D4} now belongs to header {id:D4} (matrix {result.MatrixId:D4}{(result.MatrixChanged ? $"; the header's matrix was {result.PreviousMatrixId:D4}" : string.Empty)}).";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            LogUiError("The map could not be given to the header", ex);
            headerManagerStatus.Text = $"The map could not be given to the header: {ex.Message}";
        }
    }

    private void ReloadAfterHeaderManagerChange(ushort selectId)
    {
        foreach (ushort id in headerManagerPending.Keys.ToList())
        {
            (MapHeaderRecord record, string name) = headerManagerPending[id];
            headerManagerPending[id] = (record with { MatrixId = headerStore!.Load(id).MatrixId }, name);
        }

        syncingHeaderFromMap = true;
        try
        {
            RebuildHeaderList(selectId);
        }
        finally
        {
            syncingHeaderFromMap = false;
        }

        RefreshMapHeaderLookups();
        EnsureLocationLookupsLoaded();
        FillHeaderManagerList(selectId);
        RefreshUnsavedMarkers();
    }

    private void OpenManagedHeaderInMapEditor()
    {
        if (SelectedManagedHeaderId() is not ushort id) return;
        SelectEditor("Map Editor");
        SelectHeaderFromSearch(id);
    }

    private void OpenManagedMap(ListBox list)
    {
        if (list.SelectedItem is not MapViewEntry map) return;
        int index = mapEntries.FindIndex(entry => entry.Id == map.Id);
        if (index < 0) return;
        SelectEditor("Map Editor");
        MapModeTabs.SelectedItem = SingleMapTab;
        MapSelector.SelectedIndex = index;
    }
}
