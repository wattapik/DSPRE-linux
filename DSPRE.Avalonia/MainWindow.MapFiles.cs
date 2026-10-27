using Avalonia.Interactivity;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Single Map view's map file actions (the Map File menu, and the Buildings and Permissions
/// panes' import/export) - legacy MapEditor's map BIN, NSBMD, BDHC, BGS, buildings and permissions
/// buttons. File-level changes go through MapFileService (journaled there); buildings and permissions
/// are imported into their editors and saved with Save Map like any other edit.</summary>
public partial class MainWindow
{
    private const string MapFilePattern = "*.bin";
    private const string BuildingsFilePattern = "*.bld";
    private const string PermissionsFilePattern = "*.per";
    private const string TerrainFilePattern = "*.bdhc";
    private const string SoundPlatesFilePattern = "*.bgs";

    private bool TryGetSelectedMap(out RomProject project, out RomGameMetadata metadata, out MapViewEntry map)
    {
        project = currentProject!;
        metadata = null!;
        map = null!;
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? found) || found is null) return false;
        if (MapSelector.SelectedIndex < 0 || MapSelector.SelectedIndex >= mapEntries.Count) return false;
        metadata = found;
        map = mapEntries[MapSelector.SelectedIndex];
        return true;
    }

    private string MapFileBaseName(MapViewEntry map) => $"{map.Id:D4} {map.Name}".Trim();

    private void MapFileMenu_Opening(object? sender, EventArgs e)
    {
        bool hgss = currentProject is not null && currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata is not null && MapFileService.IsHgss(metadata);
        MapSoundPlatesSeparator.IsVisible = ExportSoundPlatesItem.IsVisible = ImportSoundPlatesItem.IsVisible = BlankSoundPlatesItem.IsVisible = hgss;
    }

    private async Task ExportBytesAsync(string title, string fileName, string typeName, string pattern, byte[] data)
    {
        string? output = await PickSavePathAsync(title, fileName, typeName, pattern);
        if (output is null) return;
        await File.WriteAllBytesAsync(output, data);
        StatusText.Text = $"Saved {Path.GetFileName(output)} ({data.Length:N0} bytes).";
    }

    private async void ExportMapFile_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedMap(out RomProject project, out _, out MapViewEntry map)) return;
        await ExportBytesAsync("Save the map file as", MapFileBaseName(map) + ".bin", "Map file", MapFilePattern, await File.ReadAllBytesAsync(MapFileService.GetPath(project, map.Id)));
    }

    /// <summary>Reloads the selected map after its file changed underneath the editors: they drop their
    /// copies of its buildings so everything is read from the new file.</summary>
    private async Task ReloadSelectedMapFromDiskAsync(MapViewEntry map)
    {
        sharedMapBuildings.Remove((ushort)map.Id);
        sharedMapSavedBuildings.Remove((ushort)map.Id);
        ForgetPermissions(map.Id);
        InvalidateCachedMapModels([(ushort)map.Id]);
        await SelectMapAsync();
    }

    private async void ReplaceMapFile_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedMap(out RomProject project, out RomGameMetadata metadata, out MapViewEntry map)) return;
        string? path = await PickFileAsync("Pick the map file to use", "Map file", MapFilePattern);
        if (path is null) return;
        if (!await ConfirmAsync("Replace map file", $"Replace map {map.Id:D4} with {Path.GetFileName(path)}? Its buildings, permissions, model and terrain all change, and unsaved edits to them are lost.", "Replace")) return;

        BeginBlockingLoad("Replacing map file...");
        try
        {
            bool adapted = MapFileService.ImportWholeFile(project, metadata, map.Id, path);
            await ReloadSelectedMapFromDiskAsync(map);
            StatusText.Text = adapted
                ? $"Map {map.Id:D4} replaced with {Path.GetFileName(path)}, converted from the other game's map layout."
                : $"Map {map.Id:D4} replaced with {Path.GetFileName(path)}.";
        }
        catch (InvalidDataException ex)
        {
            StatusText.Text = $"{Path.GetFileName(path)} is not a usable map file: {ex.Message}";
        }
        finally
        {
            EndBlockingLoad();
        }
    }

    private async void AddMapFile_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedMap(out RomProject project, out RomGameMetadata metadata, out _)) return;
        if (!await EnsureCanAddMapsAsync()) return;
        BeginBlockingLoad("Adding the map...");
        try
        {
            int id = MapFileService.Add(project, metadata);
            NewMapService.Wired wired = await NewMapService.WireAsync(project, metadata, ToolsDirectory, id, $"map_{id:D4}", texturePackId: -1);
            RefreshHeaderLinkLists();
            RebuildHeaderList((ushort)wired.HeaderId);
            ConfigureMapEditor(autoSelectFirst: false);
            int index = mapEntries.FindIndex(entry => entry.Id == id);
            if (index >= 0) MapSelector.SelectedIndex = index;
            StatusText.Text = $"Added map {id:D4}, a copy of map 0000 with no move permissions, with header {wired.HeaderId:D4} (area data {wired.AreaDataId}, matrix {wired.MatrixId}, script file {wired.ScriptFileId}, " +
                $"level script {wired.LevelScriptId}, event file {wired.EventFileId}, text archive {wired.TextArchiveId} and wild encounter file {wired.EncounterFileId}, all linked).";
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            LogUiError("The map could not be added", ex);
            StatusText.Text = $"The map could not be added: {ex.Message}";
        }
        finally
        {
            EndBlockingLoad();
        }
    }

    private async void RemoveLastMapFile_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null) return;
        int last = HeaderFileService.NextFileId(currentProject.MapsDirectory) - 1;
        if (last < 1)
        {
            StatusText.Text = "At least one map file must stay.";
            return;
        }

        if (!await ConfirmAsync("Remove last map file", $"Delete map file {last:D4}? Matrices that place it will point at a missing map.", "Delete")) return;
        MapFileService.RemoveLast(currentProject);
        sharedMapBuildings.Remove((ushort)last);
        sharedMapSavedBuildings.Remove((ushort)last);
        ForgetPermissions(last);
        ConfigureMapEditor(autoSelectFirst: false);
        if (mapEntries.Count > 0) MapSelector.SelectedIndex = Math.Min(Math.Max(0, MapSelector.SelectedIndex), mapEntries.Count - 1);
        StatusText.Text = $"Removed map file {last:D4}.";
    }

    private async void DeleteMapFile_Click(object? sender, RoutedEventArgs e)
    {
        if (TryGetSelectedMap(out _, out _, out MapViewEntry map)) await DeleteMapAsync(map.Id, text => StatusText.Text = text);
    }

    private async void MapModelDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (MapModelList.SelectedIndex < 0 || MapModelList.SelectedIndex >= mapEntries.Count)
        {
            MapModelStatus.Text = "Select the map to delete.";
            return;
        }

        await DeleteMapAsync(mapEntries[MapModelList.SelectedIndex].Id, text => MapModelStatus.Text = text);
    }

    private async Task DeleteMapAsync(int mapId, Action<string> report)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        MapDeletionService.Report plan;
        try
        {
            plan = MapDeletionService.Plan(currentProject, metadata, mapId);
        }
        catch (InvalidOperationException ex)
        {
            report(ex.Message);
            return;
        }

        string what = plan.Removed
            ? "The map file is removed (it is the last one, so no other map changes number)."
            : "The map file becomes a small placeholder with no permissions or buildings, so every other map keeps its number (the game's code may name them).";
        string? choice = await new ChoiceDialog($"Delete map {mapId:D4}?",
            $"{what} The {plan.SquaresEmptied} matrix square(s) that place it are emptied. Its header, area data and events are kept. The edit journal records the deletion, and the project is opened again afterwards. Save any other edits first.",
            ("delete", "Delete"), ("cancel", "Cancel")).ShowDialog<string?>(this);
        if (choice != "delete") return;
        if (!await ConfirmLeavingProjectAsync("deleting the map")) return;

        BeginBlockingLoad($"Deleting map {mapId:D4}...");
        try
        {
            await Task.Run(() => MapDeletionService.Delete(currentProject, metadata, mapId));
            ReportLoadProgress("Reloading the project...", null);
            await PrepareAndShowProjectAsync(currentProject);
            report($"Deleted map {mapId:D4}.");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException)
        {
            LogUiError("The map could not be deleted", ex);
            report($"The map could not be deleted: {ex.Message}");
        }
        finally
        {
            EndBlockingLoad();
        }
    }

    private byte[]? SelectedMapTexturePack(RomProject project)
    {
        int pack = MapTextureSelector.SelectedIndex - 1;
        string path = TexturePackService.GetPath(project, TexturePackKind.Map, pack);
        return pack >= 0 && File.Exists(path) ? File.ReadAllBytes(path) : null;
    }

    private async void ExportMapModel_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedMap(out RomProject project, out RomGameMetadata metadata, out MapViewEntry map)) return;
        await ExportBytesAsync("Save the map model as", MapFileBaseName(map) + ".nsbmd", "NSBMD model", "*.nsbmd",
            MapFileService.ExportModel(MapFileService.Load(project, metadata, map.Id), null));
    }

    private async void ExportMapModelTextured_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedMap(out RomProject project, out RomGameMetadata metadata, out MapViewEntry map)) return;
        byte[]? pack = SelectedMapTexturePack(project);
        if (pack is null)
        {
            StatusText.Text = "Pick a map texture pack above first: its textures are embedded in the exported model.";
            return;
        }

        await ExportBytesAsync("Save the textured map model as", MapFileBaseName(map) + "_textured.nsbmd", "NSBMD model", "*.nsbmd",
            MapFileService.ExportModel(MapFileService.Load(project, metadata, map.Id), pack));
    }

    private async void ImportMapModel_Click(object? sender, RoutedEventArgs e) =>
        await ImportMapSectionAsync(MapFileSection.Model, "Pick the NSBMD model", "NSBMD model", "*.nsbmd");

    private async void ImportMapTerrain_Click(object? sender, RoutedEventArgs e) =>
        await ImportMapSectionAsync(MapFileSection.Terrain, "Pick the collision terrain", "BDHC terrain", TerrainFilePattern);

    private async void ImportMapSoundPlates_Click(object? sender, RoutedEventArgs e) =>
        await ImportMapSectionAsync(MapFileSection.SoundPlates, "Pick the background sounds", "Background sounds", SoundPlatesFilePattern);

    private async Task ImportMapSectionAsync(MapFileSection section, string title, string typeName, string pattern)
    {
        if (!TryGetSelectedMap(out RomProject project, out RomGameMetadata metadata, out MapViewEntry map)) return;
        string? path = await PickFileAsync(title, typeName, pattern);
        if (path is null) return;
        try
        {
            MapFileService.ImportSection(project, metadata, map.Id, section, path);
            InvalidateCachedMapModels([(ushort)map.Id]);
            if (section == MapFileSection.Model) LoadSelectedMap();
            int size = MapFileService.GetSection(MapFileService.Load(project, metadata, map.Id), section).Length;
            StatusText.Text = section == MapFileSection.Model && size > MapFileService.ModelCriticalSize
                ? $"Model imported, but it is {size:N0} bytes - over {MapFileService.ModelCriticalSize:N0}, which may misbehave in game."
                : $"{typeName} of map {map.Id:D4} replaced with {Path.GetFileName(path)} ({size:N0} bytes).";
        }
        catch (InvalidDataException ex)
        {
            StatusText.Text = ex.Message;
        }
    }

    private async void ExportMapTerrain_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedMap(out RomProject project, out RomGameMetadata metadata, out MapViewEntry map)) return;
        await ExportBytesAsync("Save the collision terrain as", MapFileBaseName(map) + ".bdhc", "BDHC terrain", TerrainFilePattern,
            MapFileService.Load(project, metadata, map.Id).Terrain);
    }

    private async void ExportMapSoundPlates_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedMap(out RomProject project, out RomGameMetadata metadata, out MapViewEntry map)) return;
        await ExportBytesAsync("Save the background sounds as", MapFileBaseName(map) + ".bgs", "Background sounds", SoundPlatesFilePattern,
            MapFileService.GetSection(MapFileService.Load(project, metadata, map.Id), MapFileSection.SoundPlates));
    }

    private async void BlankMapSoundPlates_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedMap(out RomProject project, out RomGameMetadata metadata, out MapViewEntry map)) return;
        if (!await ConfirmAsync("Remove background sounds", $"Remove every background sound from map {map.Id:D4}?", "Remove")) return;
        MapFileService.BlankSoundPlatesOf(project, metadata, map.Id);
        StatusText.Text = $"Background sounds of map {map.Id:D4} removed.";
    }

    private async void ExportMapGlb_Click(object? sender, RoutedEventArgs e) => await ConvertMapModelAsync(glb: true);
    private async void ExportMapDae_Click(object? sender, RoutedEventArgs e) => await ConvertMapModelAsync(glb: false);

    private async Task ConvertMapModelAsync(bool glb)
    {
        if (!TryGetSelectedMap(out RomProject project, out RomGameMetadata metadata, out MapViewEntry map)) return;
        string? folder = await PickFolderAsync($"Pick where to export the {(glb ? "GLB" : "DAE")} model (a folder named after the map is made there)");
        if (folder is null) return;

        string output = Path.Combine(folder, MapFileBaseName(map));
        BeginBlockingLoad($"Converting map {map.Id:D4} to {(glb ? "GLB" : "DAE")}...");
        try
        {
            await MapFileService.ConvertModelAsync(project, metadata, ToolsDirectory, MapFileService.Load(project, metadata, map.Id), SelectedMapTexturePack(project), output, glb);
            StatusText.Text = $"Map {map.Id:D4} exported as {(glb ? "GLB" : "DAE")} into {output}.";
        }
        catch (Exception ex)
        {
            LogUiError("Map model could not be exported", ex);
            StatusText.Text = $"The model could not be exported: {ex.Message}";
        }
        finally
        {
            EndBlockingLoad();
        }
    }

    private async void ExportBuildings_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedMap(out _, out _, out MapViewEntry map)) return;
        await ExportBytesAsync("Save the buildings as", MapFileBaseName(map) + ".bld", "Buildings file", BuildingsFilePattern, MapBuildingService.WriteBuildings(currentBuildings));
    }

    private async void ImportBuildings_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedMap(out _, out _, out MapViewEntry map)) return;
        string? path = await PickFileAsync("Pick the buildings file", "Buildings file", BuildingsFilePattern);
        if (path is null) return;
        byte[] data = await File.ReadAllBytesAsync(path);
        if (data.Length % 0x30 != 0)
        {
            StatusText.Text = $"{Path.GetFileName(path)} is not a buildings file: buildings take 48 bytes each.";
            return;
        }

        List<BuildingRecord> imported = MapBuildingService.ParseBuildings(data);
        currentBuildings.Clear();
        currentBuildings.AddRange(imported);
        loadingBuildingEditor = true;
        try
        {
            BuildingListBox.SelectedIndex = -1;
            RefreshBuildingListBoxItems();
        }
        finally
        {
            loadingBuildingEditor = false;
        }

        SyncBuildingFieldsToSelectedBuilding();
        PreviewBuildingChanges();
        StatusText.Text = $"Imported {imported.Count} buildings into map {map.Id:D4}. Save Map to keep them.";
    }

    private async void ExportPermissions_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedMap(out _, out _, out MapViewEntry map)) return;
        await ExportBytesAsync("Save the move permissions as", MapFileBaseName(map) + ".per", "Permissions file", PermissionsFilePattern,
            MapPermissionsService.Serialize(currentPermissionTypes, currentPermissionCollisions));
    }

    private async void ImportPermissions_Click(object? sender, RoutedEventArgs e)
    {
        if (!TryGetSelectedMap(out _, out _, out MapViewEntry map)) return;
        string? path = await PickFileAsync("Pick the permissions file", "Permissions file", PermissionsFilePattern);
        if (path is null) return;
        byte[] data = await File.ReadAllBytesAsync(path);
        if (data.Length != MapPermissionsService.GridSize * MapPermissionsService.GridSize * 2)
        {
            StatusText.Text = $"{Path.GetFileName(path)} is not a permissions file: those are exactly 2,048 bytes.";
            return;
        }

        (currentPermissionTypes, currentPermissionCollisions) = MapPermissionsService.Parse(data);
        RefreshPermissionGridColors();
        StatusText.Text = $"Imported move permissions into map {map.Id:D4}. Save Map to keep them.";
    }
}
