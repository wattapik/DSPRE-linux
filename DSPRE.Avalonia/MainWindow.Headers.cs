using Avalonia.Interactivity;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>Header file import/export and adding/removing headers, from the Map Editor's Header pane.</summary>
public partial class MainWindow
{
    private const string HeaderFilePattern = "*.dsh";

    /// <summary>Re-reads the header list after the number of headers changed, without the full
    /// ConfigureHeaderStore reset (which would drop other editors' open state).</summary>
    private void RebuildHeaderList(ushort selectId)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        headerStore = MapHeaderStoreFactory.Create(currentProject, metadata);
        if (headerStore is null) return;

        IReadOnlyList<HeaderOption> locations = currentProject.GetLocationNames(metadata);
        allHeaders.Clear();
        for (ushort id = 0; id < headerStore.Count; id++)
        {
            MapHeaderRecord header = headerStore.Load(id);
            string location = locations.FirstOrDefault(option => option.Id == header.LocationNameId)?.Label ?? "Unknown location";
            allHeaders.Add(new HeaderListEntry(id, currentProject.GetInternalName(id), header.LocationNameId, location));
        }

        HeaderEditorInfo.Text = $"{metadata.GameCode} - {metadata.HeaderFormat} - {headerStore.Count} headers";
        // The map/matrix-to-header lookups are rebuilt on next use against the new list.
        mapLocationNameIds = null;
        matrixLocationNameIds = null;
        selectedHeader = null;
        ClearHeaderFilters();
        RefreshHeaderSelector();
        SelectHeaderFromSearch(selectId);
        UpdateHeaderCountButtons();
        if (headerManagerList.IsEffectivelyVisible) ConfigureHeaderManager();
    }

    /// <summary>Asks for a name, adds it to the location names and refreshes the lists that show them. Returns the new location's number.</summary>
    private async Task<int?> PromptAddLocationNameAsync()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return null;
        string? name = await new TextPromptDialog("New location name", $"The name of the new location. It becomes location number {currentProject.GetLocationNames(metadata).Count}, the next free one.").ShowDialog<string?>(this);
        if (name is null) return null;
        try
        {
            int id = LocationNameService.Add(currentProject, metadata, name);
            ushort keep = ParseOption(LocationInput, 0);
            SetOptions(LocationInput, currentProject.GetLocationNames(metadata));
            SelectOption(LocationInput, keep);
            FillHeaderManagerCombos();
            ShowManagedHeader();
            StatusText.Text = $"Added location name {id}: {name.Trim()}.";
            return id;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
        {
            LogUiError("The location name could not be added", ex);
            StatusText.Text = $"The location name could not be added: {ex.Message}";
            return null;
        }
    }

    private async void AddLocationName_Click(object? sender, RoutedEventArgs e)
    {
        if (await PromptAddLocationNameAsync() is int id) SelectOption(LocationInput, (ushort)id);
    }

    /// <summary>After files a header can name (matrices, area data, scripts, text archives, event and encounter files) were added, the
    /// header's lists are filled again from the project; the Header pane refills them when it next shows a header.</summary>
    private void RefreshHeaderLinkLists()
    {
        if (allMapMatrices.Count > 0) ReloadMatrixSelector((MapMatrixSelector.SelectedItem as MatrixListEntry)?.Id ?? 0);
        headerIdCombosStale = true;
    }

    /// <summary>Saves the header the Header pane has open, when it has one (the Header Manager works before that pane was ever shown).</summary>
    private bool SaveOpenHeaderIfAny() => selectedHeader is null || SaveCurrentHeader();

    private bool refreshingHeaderLabels;

    /// <summary>After a header's internal name or place name changed (or the place names themselves were edited), every list that shows
    /// them is written again from the project: the header lists, the maps' names, the matrices' locations, the lists in the Header pane
    /// and the Header Manager. Nothing is reselected, so the open header, map and unsaved edits stay as they are.</summary>
    private void RefreshHeaderNames()
    {
        if (currentProject is null || headerStore is null || allHeaders.Count == 0 || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        IReadOnlyList<HeaderOption> locations = currentProject.GetLocationNames(metadata);
        for (ushort id = 0; id < allHeaders.Count; id++)
        {
            MapHeaderRecord header = headerStore.Load(id);
            string location = locations.FirstOrDefault(option => option.Id == header.LocationNameId)?.Label ?? "Unknown location";
            allHeaders[id] = new HeaderListEntry(id, currentProject.GetInternalName(id), header.LocationNameId, location);
        }

        refreshingHeaderLabels = true;
        try
        {
            RefreshHeaderSelector();
        }
        finally
        {
            refreshingHeaderLabels = false;
        }

        if (selectedHeader is { } open) HeaderSummaryName.Text = $"{open.Id:D4} - {HeaderNameInput.Text}";
        ushort pickedLocation = ParseOption(LocationInput, 0);
        SetOptions(LocationInput, locations.Count > 0 ? locations : [new HeaderOption(0, "Location names not exported")]);
        SelectOption(LocationInput, pickedLocation);

        mapLocationNameIds = null;
        matrixLocationNameIds = null;
        EnsureLocationLookupsLoaded();
        if (mapEntries.Count > 0)
        {
            Dictionary<ushort, string> labels = locations.GroupBy(option => option.Id).ToDictionary(group => group.Key, group => group.First().Label);
            int picked = MapSelector.SelectedIndex;
            loadingMapEditor = true;
            try
            {
                for (int i = 0; i < mapEntries.Count; i++)
                {
                    string? location = mapLocationNameIds!.TryGetValue(mapEntries[i].Id, out ushort nameId) && labels.TryGetValue(nameId, out string? label) && !string.IsNullOrWhiteSpace(label) ? label : null;
                    mapEntries[i] = mapEntries[i] with { LocationName = location };
                    MapSelector.Items[i] = mapEntries[i];
                }

                MapSelector.SelectedIndex = picked;
            }
            finally
            {
                loadingMapEditor = false;
            }
        }

        RefreshHeaderLinkLists();
        if (headerManagerList.IsEffectivelyVisible) ConfigureHeaderManager();
        RefreshMapOwnerHeader();
    }

    private bool refreshingMapOwnerHeader;

    private MapViewEntry? OpenSingleMap =>
        ReferenceEquals(MapModeTabs.SelectedItem, SingleMapTab) && MapSelector.SelectedIndex >= 0 && MapSelector.SelectedIndex < mapEntries.Count ? mapEntries[MapSelector.SelectedIndex] : null;

    /// <summary>Shows which header reaches the open map (the first item stands for none) in the Header pane's dropdown.</summary>
    private void RefreshMapOwnerHeader()
    {
        if (OpenSingleMap is not { } map || headerStore is null)
        {
            MapOwnerHeaderPanel.IsVisible = false;
            return;
        }

        EnsureLocationLookupsLoaded();
        refreshingMapOwnerHeader = true;
        try
        {
            MapOwnerHeaderPanel.IsVisible = true;
            MapOwnerHeaderTitle.Text = $"Header of map {map.Id:D4}";
            MapOwnerHeaderSelector.Items.Clear();
            MapOwnerHeaderSelector.Items.Add("(no header reaches this map)");
            foreach (HeaderListEntry entry in allHeaders) MapOwnerHeaderSelector.Items.Add(entry);
            MapOwnerHeaderSelector.SelectedIndex = mapHeaderIds.TryGetValue(map.Id, out ushort owner) && owner < allHeaders.Count ? owner + 1 : 0;
        }
        finally
        {
            refreshingMapOwnerHeader = false;
        }
    }

    private async void MapOwnerHeaderSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (refreshingMapOwnerHeader || OpenSingleMap is not { } map || currentProject is null || headerStore is null) return;
        if (MapOwnerHeaderSelector.SelectedItem is not HeaderListEntry chosen)
        {
            RefreshMapOwnerHeader();
            return;
        }

        if (mapHeaderIds.TryGetValue(map.Id, out ushort current) && current == chosen.Id) return;
        if (!SaveOpenHeaderIfAny())
        {
            RefreshMapOwnerHeader();
            return;
        }

        int? matrixId = MapHeaderAssignmentService.FindMatrix(currentProject, headerStore, map.Id);
        if (matrixId is null)
        {
            StatusText.Text = $"Map {map.Id:D4} is not in any matrix, so no header can reach it.";
            RefreshMapOwnerHeader();
            return;
        }

        ushort previousMatrix = headerStore.Load(chosen.Id).MatrixId;
        string matrixNote = previousMatrix == matrixId
            ? $"Header {chosen.Id:D3} already uses matrix {matrixId:D4}."
            : $"Header {chosen.Id:D3} uses matrix {previousMatrix:D4} now; it will use matrix {matrixId:D4}, the one holding this map.";
        string? answer = await new ChoiceDialog(
            "Change the map's header",
            $"Make header {chosen.Id:D3} ({chosen.InternalName}) the header of map {map.Id:D4}?\n\n{matrixNote}\nThe map's square in matrix {matrixId:D4} gets header {chosen.Id:D3}.",
            ("assign", "Change Header"), ("cancel", "Cancel")).ShowDialog<string?>(this);
        if (answer != "assign")
        {
            RefreshMapOwnerHeader();
            return;
        }

        try
        {
            MapHeaderAssignmentService.Result result = MapHeaderAssignmentService.Assign(currentProject, headerStore, map.Id, chosen.Id);
            syncingHeaderFromMap = true;
            try
            {
                RebuildHeaderList(chosen.Id);
            }
            finally
            {
                syncingHeaderFromMap = false;
            }

            RefreshMapHeaderLookups();
            LoadSelectedMap();
            RefreshUnsavedMarkers();
            StatusText.Text = $"Map {map.Id:D4} now belongs to header {chosen.Id:D3} (matrix {result.MatrixId:D4}" +
                $"{(result.AddedHeadersSection ? ", which got a header per square" : string.Empty)}{(result.MatrixChanged ? $"; the header's matrix was {result.PreviousMatrixId:D4}" : string.Empty)}).";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or ArgumentException)
        {
            LogUiError("The map's header could not be changed", ex);
            StatusText.Text = $"The map's header could not be changed: {ex.Message}";
        }

        RefreshMapOwnerHeader();
    }

    /// <summary>Draws the Map Editor's open views again with the packs an area data now names: the open single map when it uses the
    /// area data, and the header's maps or whole matrix when the header does. Views not open, or not visited yet, read the area data
    /// when they are shown.</summary>
    private void RedrawMapViewsUsingAreaData(ushort areaDataId)
    {
        if (currentProject is null || headerStore is null || mapEntries.Count == 0) return;
        if (OpenSingleMap is { } map && mapAreaData.TryGetValue(map.Id, out AreaDataRecord? record) && record.Id == areaDataId)
        {
            int mapPack = record.MapTileset + 1, buildingPack = record.BuildingsTileset + 1;
            if (MapTextureSelector.SelectedIndex != mapPack || MapBuildingTextureSelector.SelectedIndex != buildingPack)
            {
                settingMapTexturePacks = true;
                try
                {
                    if (mapPack < MapTextureSelector.ItemCount) MapTextureSelector.SelectedIndex = mapPack;
                    if (buildingPack < MapBuildingTextureSelector.ItemCount) MapBuildingTextureSelector.SelectedIndex = buildingPack;
                }
                finally
                {
                    settingMapTexturePacks = false;
                }

                LoadSelectedMap();
            }
        }

        if (selectedHeader is null || headerStore.Load(selectedHeader.Id).AreaDataId != areaDataId) return;
        MapHeaderRecord header = headerStore.Load(selectedHeader.Id);
        if (IsHeaderMapsView && headerMapsViewHeader == header.Id)
        {
            ApplyHeaderTexturePacks(header, wholeMatrix: false);
            ReloadCurrentOverworldComposite();
        }
        else if (IsWholeMatrixView && wholeMatrixViewHeader == header.Id && shownWholeMatrixId == header.MatrixId)
        {
            ApplyHeaderTexturePacks(header, wholeMatrix: true);
            ShowMatrixComposite(IsEditingMatrix && currentMatrix is not null ? GetMatrixPlacements(currentMatrix) : GetMatrixPlacements(header.MatrixId), header.MatrixId);
        }
    }

    /// <summary>Re-reads what the map views take from the headers: each map's area data (so its textures) and event file.</summary>
    private void RefreshMapHeaderLookups()
    {
        if (currentProject is null || headerStore is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        mapAreaData = AreaDataService.ResolveMapAreaData(currentProject, headerStore.Load, currentProject.GetHeaderCount(), metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver);
        mapEventFileIds = MapEventService.ResolveMapEventFileIds(currentProject, headerStore.Load, currentProject.GetHeaderCount());
    }

    private void UpdateHeaderCountButtons()
    {
        bool canChange = currentProject is not null && currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata is not null
            && HeaderFileService.CanChangeHeaderCount(currentProject, metadata);
        AddHeaderButton.IsEnabled = canChange;
        RemoveLastHeaderButton.IsEnabled = canChange;
        string tip = canChange ? string.Empty : "Needs the Dynamic Header Allocation patch (ROM Toolbox): the ARM9's own header table has a fixed size.";
        ToolTip.SetTip(AddHeaderButton, canChange ? "Add a copy of header 0 named NEWMAP" : tip);
        ToolTip.SetTip(RemoveLastHeaderButton, canChange ? "Delete the last header" : tip);
    }

    private async void ExportHeader_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || selectedHeader is null) return;
        if (!SaveCurrentHeader()) return;

        string name = currentProject.GetInternalName(selectedHeader.Id);
        string? output = await PickSavePathAsync("Save the header as", $"Header {selectedHeader.Id} - {name}.dsh", "DSPRE header", HeaderFilePattern);
        if (output is null) return;
        await File.WriteAllBytesAsync(output, HeaderFileService.Export(selectedHeader, name));
        StatusText.Text = $"Header {selectedHeader.Id:D4} saved to {Path.GetFileName(output)}.";
    }

    private async void ImportHeader_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || headerStore is null || selectedHeader is null) return;
        if (!currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        string? path = await PickFileAsync("Pick a header file", "DSPRE header", HeaderFilePattern);
        if (path is null) return;

        ushort id = selectedHeader.Id;
        if (!await ConfirmAsync("Replace header", $"Replace header {id:D4} ({currentProject.GetInternalName(id)}) with {Path.GetFileName(path)}? Unsaved changes to it are lost.", "Replace")) return;
        try
        {
            HeaderFileService.Import(currentProject, headerStore, metadata, id, path);
            RebuildHeaderList(id);
            StatusText.Text = $"Header {id:D4} replaced with {Path.GetFileName(path)}.";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            StatusText.Text = $"The header could not be imported: {ex.Message}";
        }
    }

    private async void AddHeader_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        if (!SaveCurrentHeader()) return;

        string? files = await new ChoiceDialog(
            "Add header",
            "Add a new header (a copy of header 0 named NEWMAP)?\n\nIt can also get its own blank files: a text archive, a script file, a level script and an event file. " +
            "Removing the header later does not remove them.",
            ("files", "Add With New Files"), ("plain", "Add Header Only"), ("cancel", "Cancel")).ShowDialog<string?>(this);
        if (files is null or "cancel") return;

        BeginBlockingLoad("Adding header...");
        try
        {
            ushort id = (ushort)HeaderFileService.Add(currentProject, metadata);
            string note = string.Empty;
            if (files == "files")
            {
                HeaderFileService.SupportingFiles created = await HeaderFileService.CreateSupportingFilesAsync(currentProject, metadata, ToolsDirectory);
                IMapHeaderStore store = MapHeaderStoreFactory.Create(currentProject, metadata)!;
                store.Save(store.Load(id) with
                {
                    TextArchiveId = (ushort)created.TextArchive,
                    ScriptFileId = (ushort)created.ScriptFile,
                    LevelScriptId = (ushort)created.LevelScript,
                    EventFileId = (ushort)created.EventFile
                });
                note = $" with text archive {created.TextArchive}, script file {created.ScriptFile}, level script {created.LevelScript} and event file {created.EventFile}";
            }

            RebuildHeaderList(id);
            StatusText.Text = $"Added header {id:D4}{note}.";
        }
        catch (Exception ex)
        {
            LogUiError("Header could not be added", ex);
            StatusText.Text = $"The header could not be added: {ex.Message}";
        }
        finally
        {
            EndBlockingLoad();
        }
    }

    private async void RemoveLastHeader_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        int last = currentProject.GetHeaderCount() - 1;
        if (last < 1)
        {
            StatusText.Text = "At least one header must stay.";
            return;
        }

        if (!await ConfirmAsync("Remove last header", $"Delete header {last:D4} ({currentProject.GetInternalName((ushort)last)})? Its text archive, scripts and event file are kept.", "Delete")) return;
        try
        {
            HeaderFileService.RemoveLast(currentProject, metadata);
            ushort keep = selectedHeader is { } current && current.Id < last ? current.Id : (ushort)(last - 1);
            RebuildHeaderList(keep);
            StatusText.Text = $"Removed header {last:D4}.";
        }
        catch (Exception ex)
        {
            LogUiError("Header could not be removed", ex);
            StatusText.Text = $"The header could not be removed: {ex.Message}";
        }
    }
}
