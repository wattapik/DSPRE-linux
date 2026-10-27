using Avalonia.Interactivity;
using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using DSPRE.Core;
using System.IO;

namespace DSPRE.Avalonia;

/// <summary>The Map Model Editor: a loader that shows an NSBMD map model, either the terrain model of a
/// project map or a file the user picks, optionally with a map texture pack applied. Viewing only; the
/// model is converted to GLB by apicula through the bridge, as the map exports do.</summary>
public partial class MainWindow
{
    private bool loadingMapModelEditor;
    private string? mapModelFile;
    private int mapModelLoads;
    private int mapModelNewPackId = -1;
    private MapModelImportService.Result? lastAppliedImport;
    private bool loadingMapModelAnimation;
    private DispatcherTimer? mapModelAnimationTimer;
    private bool loadingMapModelPack;
    // Texture pack chosen for a map but not yet given to it (see MapTextureAssignmentService), by map id.
    private readonly Dictionary<int, int> mapModelPendingPacks = [];
    // Likewise the animation (an area data dynamic textures value, 65535 for none) chosen for a map.
    private readonly Dictionary<int, int> mapModelPendingAnimations = [];

    private void ConfigureMapModelEditor()
    {
        if (currentProject is null) return;
        loadingMapModelEditor = true;
        try
        {
            int previousTexture = MapModelTextureSelector.SelectedIndex;
            MapModelTextureSelector.Items.Clear();
            MapModelTextureSelector.Items.Add("Embedded textures");
            int packCount = TexturePackService.Count(currentProject, TexturePackKind.Map);
            for (int i = 0; i < packCount; i++) MapModelTextureSelector.Items.Add($"Map Texture Pack [{i:D2}]");
            MapModelTextureSelector.SelectedIndex = previousTexture >= 0 && previousTexture <= packCount ? previousTexture : 0;

            MapModelList.Items.Clear();
            foreach (MapViewEntry map in mapEntries) MapModelList.Items.Add($"[{map.Id:D4}] {map.Name}");
            RefreshMapModelPackPane(selectCurrent: true);
        }
        finally
        {
            loadingMapModelEditor = false;
        }

        ConfigureMapModelAnimations();
    }

    private void MapModelList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingMapModelEditor || MapModelList.SelectedIndex < 0) return;
        mapModelFile = null;
        RefreshMapModelPackPane(selectCurrent: true);
        // A map is shown with the textures it really has: the pack its area data names.
        if (SelectedMapAreaData() is { } area && area.MapTileset + 1 < MapModelTextureSelector.Items.Count)
        {
            loadingMapModelEditor = true;
            MapModelTextureSelector.SelectedIndex = area.MapTileset + 1;
            loadingMapModelEditor = false;
        }

        SelectMapModelAnimationFor(SelectedMapAreaData());
        _ = LoadMapModelPreviewAsync();
    }

    private void MapModelTextureSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingMapModelEditor) return;
        _ = LoadMapModelPreviewAsync();
    }

    private void MapModelResetView_Click(object? sender, RoutedEventArgs e) => MapModelViewer.ResetView();

    private async void MapModelOpenFile_Click(object? sender, RoutedEventArgs e)
    {
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Open map model (NSBMD or OBJ)",
            SuggestedStartLocation = await ImportStartFolderAsync(),
            FileTypeFilter = [new FilePickerFileType("Map model") { Patterns = ["*.nsbmd", "*.obj", "*"] }]
        });
        if (files.Count != 1) return;

        mapModelFile = files[0].Path.LocalPath;
        loadingMapModelEditor = true;
        MapModelList.SelectedIndex = -1;
        loadingMapModelEditor = false;
        await LoadMapModelPreviewAsync();
    }

    private async void MapModelImportObj_Click(object? sender, RoutedEventArgs e)
    {
        if (MapModelList.SelectedIndex < 0 || MapModelList.SelectedIndex >= mapEntries.Count)
        {
            MapModelStatus.Text = "Select the map whose model the OBJ should replace.";
            return;
        }

        await ImportObjAsync(mapEntries[MapModelList.SelectedIndex].Id);
    }

    private async void MapModelImportObjAsNewMap_Click(object? sender, RoutedEventArgs e) => await ImportObjAsync(null);

    private async Task ImportObjAsync(int? existingMapId)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        if (existingMapId is null && !await EnsureCanAddMapsAsync()) return;
        string? path = await PickFileAsync("Pick the OBJ model (its .mtl and textures must be beside it)", "OBJ model", "*.obj");
        if (path is null) return;
        string target = existingMapId is int existing ? $"replacing map {existing:D4}'s model" : "as a new map";
        ObjToImd.Options? options = await new ObjImportOptionsDialog($"{Path.GetFileName(path)} ({target})").ShowDialog<ObjToImd.Options?>(this);
        if (options is null) return;

        MapModelImportService.Converted converted;
        BeginBlockingLoad("Converting OBJ...", showImmediately: true);
        try
        {
            converted = await MapModelImportService.ConvertObjAsync(currentProject, path, ToolsDirectory, options);
        }
        catch (Exception ex)
        {
            LogUiError("OBJ model could not be converted", ex);
            MapModelStatus.Text = ex.Message;
            return;
        }
        finally
        {
            EndBlockingLoad();
        }

        mapModelPendingImports.RemoveAll(pending => existingMapId is not null && pending.MapId == existingMapId);
        var import = new PendingMapImport(existingMapId, converted);
        mapModelPendingImports.Add(import);

        string reduced = converted.TriangleFraction < 1f ? $" Triangles reduced to {converted.TriangleFraction:P0}." : string.Empty;
        string fit = converted.FitsLimit
            ? string.Empty
            : $" The model is {converted.ModelSize:N0} bytes - over {MapFileService.ModelCriticalSize:N0}, which crashes the game; import again with a smaller model or texture size.";
        MapModelStatus.Text = $"{Path.GetFileName(path)} converted ({converted.ModelSize:N0} bytes) {target}; it is not in the project yet. Save Model or Save All adds it.{reduced}{fit}";

        // The converted model is previewed with the textures it was converted with.
        mapModelFile = converted.NsbmdPath;
        loadingMapModelEditor = true;
        try
        {
            if (existingMapId is int replaced)
            {
                int index = mapEntries.FindIndex(entry => entry.Id == replaced);
                if (index >= 0) MapModelList.SelectedIndex = index;
            }
            else
            {
                MapModelList.SelectedIndex = -1;
            }

            MapModelTextureSelector.SelectedIndex = 0;
        }
        finally
        {
            loadingMapModelEditor = false;
        }

        RefreshUnsavedMarkers();
        await LoadMapModelPreviewAsync();
    }

    /// <summary>A new map needs a header, which needs the Dynamic Header Allocation patch; tells the user when the project lacks it.</summary>
    private async Task<bool> EnsureCanAddMapsAsync()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return false;
        if (NewMapService.CanAddMaps(currentProject, metadata)) return true;
        await new ChoiceDialog("Adding a map is not possible yet", NewMapService.RequirementMessage, ("ok", "OK")).ShowDialog<string?>(this);
        return false;
    }

    private sealed record PendingMapImport(int? MapId, MapModelImportService.Converted Converted);

    // OBJ models converted but not yet put into the project: replacing a map's model, or making a new map.
    private readonly List<PendingMapImport> mapModelPendingImports = [];

    /// <summary>Puts a converted OBJ into the project (see <see cref="MapModelImportService.Apply"/>), then
    /// gives the map the texture pack made from it.</summary>
    private void ApplyMapModelImport(PendingMapImport import)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        MapModelImportService.Result result;
        try
        {
            result = MapModelImportService.Apply(currentProject, metadata, import.MapId, import.Converted, ToolsDirectory);
        }
        catch (Exception ex)
        {
            LogUiError("OBJ model could not be added to the project", ex);
            MapModelStatus.Text = ex.Message;
            return;
        }

        mapModelPendingImports.Remove(import);
        lastAppliedImport = result;
        if (mapModelFile == import.Converted.NsbmdPath) mapModelFile = null;
        InvalidateCachedMapModels([(ushort)result.MapId]);
        if (result.TexturePackId >= 0) mapModelNewPackId = result.TexturePackId;

        if (import.MapId is null)
        {
            RefreshHeaderLinkLists();
            if (result.HeaderId >= 0) RebuildHeaderList((ushort)result.HeaderId);
            ConfigureMapEditor(autoSelectFirst: false);
            ConfigureMapModelEditor();
            // The matrix editor's map and header choices were filled before this map and header existed.
            matrixCellMapFilledFor = -1;
            MatrixCellHeader.Items.Clear();
            if (currentMatrix is not null) RefreshMatrixPane();
        }

        string textures = result.TexturePackId >= 0 ? $" Textures: Map Texture Pack [{result.TexturePackId:D2}]." : string.Empty;
        if (import.MapId is int replaced)
        {
            if (result.TexturePackId >= 0 && mapAreaData.TryGetValue(replaced, out AreaDataRecord? area) && area.MapTileset != result.TexturePackId)
            {
                mapModelPendingPacks[replaced] = result.TexturePackId;
                ApplyMapModelChanges(replaced);
            }
            else
            {
                RefreshMapEditorAfterModelChange([replaced]);
            }

            MapModelStatus.Text = $"Map {replaced:D4}'s model replaced ({result.ModelSize:N0} bytes).{textures}";
        }
        else
        {
            string wiring = result.Wired is { } wired
                ? $"header {wired.HeaderId:D4} (area data {wired.AreaDataId}, matrix {wired.MatrixId}, script file {wired.ScriptFileId}, level script {wired.LevelScriptId}, event file {wired.EventFileId}, text archive {wired.TextArchiveId} and wild encounter file {wired.EncounterFileId}, all linked)"
                : $"area data {result.AreaDataId} and matrix {result.MatrixId}";
            MapModelStatus.Text = $"New map {result.MapId:D4} made with {wiring}.{textures} Merge texture packs later in the Texture Editor if maps should share one.";
        }

        int index = mapEntries.FindIndex(entry => entry.Id == result.MapId);
        if (index >= 0)
        {
            loadingMapModelEditor = true;
            MapModelList.SelectedIndex = index;
            loadingMapModelEditor = false;
        }

        loadingMapModelEditor = true;
        try
        {
            int packCount = TexturePackService.Count(currentProject, TexturePackKind.Map);
            while (MapModelTextureSelector.Items.Count <= packCount) MapModelTextureSelector.Items.Add($"Map Texture Pack [{MapModelTextureSelector.Items.Count - 1:D2}]");
            if (mapModelNewPackId >= 0) MapModelTextureSelector.SelectedIndex = mapModelNewPackId + 1;
        }
        finally
        {
            loadingMapModelEditor = false;
        }

        RefreshMapModelPackPane(selectCurrent: true);
        RefreshUnsavedMarkers();
        _ = LoadMapModelPreviewAsync();
    }

    #region Texture animation

    private void ConfigureMapModelAnimations()
    {
        RomGameMetadata? game = null;
        bool supported = currentProject is not null && currentProject.TryGetGameMetadata(out game) && game is not null &&
            DynamicTextureService.IsSupported(currentProject, game);
        MapModelAnimationBar.IsVisible = supported;
        loadingMapModelAnimation = true;
        try
        {
            MapModelAnimationSelector.Items.Clear();
            MapModelAnimationSelector.Items.Add("None");
            if (supported)
            {
                for (int type = 0; type < DynamicTextureService.Count(currentProject!, game!); type++)
                {
                    string name = DynamicTextureService.Load(currentProject!, game!, type)?.Name ?? string.Empty;
                    MapModelAnimationSelector.Items.Add($"Set {type}: {name}");
                }
            }

            MapModelAnimationSelector.SelectedIndex = 0;
        }
        finally
        {
            loadingMapModelAnimation = false;
        }

        SelectMapModelAnimationFor(SelectedMapAreaData());
    }

    /// <summary>Shows the animation the map's area data selects, as the game would.</summary>
    private void SelectMapModelAnimationFor(AreaDataRecord? area)
    {
        if (!MapModelAnimationBar.IsVisible) return;
        int type = area?.DynamicTextureType ?? DynamicTextureService.None;
        if (MapModelList.SelectedIndex >= 0 && MapModelList.SelectedIndex < mapEntries.Count &&
            mapModelPendingAnimations.TryGetValue(mapEntries[MapModelList.SelectedIndex].Id, out int pending)) type = pending;
        int index = type + 1 < MapModelAnimationSelector.Items.Count && type != DynamicTextureService.None ? type + 1 : 0;
        loadingMapModelAnimation = true;
        MapModelAnimationSelector.SelectedIndex = index;
        loadingMapModelAnimation = false;
        ShowMapModelAnimation();
    }

    private void MapModelAnimationSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingMapModelAnimation) return;
        if (SelectedMapAreaData() is { } area && MapModelAnimationSelector.SelectedIndex >= 0)
        {
            int mapId = mapEntries[MapModelList.SelectedIndex].Id;
            int type = MapModelAnimationSelector.SelectedIndex == 0 ? DynamicTextureService.None : MapModelAnimationSelector.SelectedIndex - 1;
            if (type == area.DynamicTextureType) mapModelPendingAnimations.Remove(mapId);
            else mapModelPendingAnimations[mapId] = type;
            RefreshUnsavedMarkers();
        }

        ShowMapModelAnimation();
    }

    private void ShowMapModelAnimation()
    {
        TextureAnimation? animation = null;
        int type = MapModelAnimationSelector.SelectedIndex - 1;
        if (type >= 0 && currentProject is not null && currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata is not null)
        {
            try
            {
                animation = DynamicTextureService.Load(currentProject, metadata, type);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                LogUiError("Texture animation could not be read", ex);
                MapModelStatus.Text = $"The texture animation could not be read: {ex.Message}";
            }
        }

        MapModelViewer.SetTextureAnimation(animation);
        MapModelAnimationPlayButton.Content = "Pause";
        MapModelAnimationPlayButton.IsEnabled = MapModelAnimationSlider.IsEnabled = animation is not null;
        loadingMapModelAnimation = true;
        MapModelAnimationSlider.Maximum = Math.Max((animation?.FrameCount ?? 1) - 1, 1);
        MapModelAnimationSlider.Value = 0;
        loadingMapModelAnimation = false;
        MapModelAnimationFrame.Text = animation is null ? string.Empty : $"frame 0 / {animation.FrameCount}";
        if (animation is null)
        {
            mapModelAnimationTimer?.Stop();
            return;
        }

        if (mapModelAnimationTimer is null)
        {
            mapModelAnimationTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(100) };
            mapModelAnimationTimer.Tick += (_, _) => UpdateMapModelAnimationReadout();
        }

        mapModelAnimationTimer.Start();
    }

    private void UpdateMapModelAnimationReadout()
    {
        if (!MapModelEditorPanel.IsVisible || MapModelViewer.AnimationPaused) return;
        double frame = MapModelViewer.AnimationFrame;
        loadingMapModelAnimation = true;
        MapModelAnimationSlider.Value = frame;
        loadingMapModelAnimation = false;
        MapModelAnimationFrame.Text = $"frame {(int)frame} / {(int)MapModelAnimationSlider.Maximum + 1}";
    }

    private void MapModelAnimationPlay_Click(object? sender, RoutedEventArgs e)
    {
        MapModelViewer.AnimationPaused = !MapModelViewer.AnimationPaused;
        MapModelAnimationPlayButton.Content = MapModelViewer.AnimationPaused ? "Play" : "Pause";
    }

    private void MapModelAnimationSlider_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (loadingMapModelAnimation || Math.Abs(e.NewValue - MapModelViewer.AnimationFrame) < 3) return;
        MapModelViewer.AnimationPaused = true;
        MapModelViewer.AnimationFrame = e.NewValue;
        MapModelAnimationPlayButton.Content = "Play";
        MapModelAnimationFrame.Text = $"frame {(int)e.NewValue} / {(int)MapModelAnimationSlider.Maximum + 1}";
    }

    #endregion

    private AreaDataRecord? SelectedMapAreaData() =>
        MapModelList.SelectedIndex >= 0 && MapModelList.SelectedIndex < mapEntries.Count &&
        mapAreaData.TryGetValue(mapEntries[MapModelList.SelectedIndex].Id, out AreaDataRecord? record) ? record : null;

    /// <summary>Shows which map texture pack the selected map uses and lets another be picked. A pack made
    /// by an OBJ import is marked "new" and is what the pane offers first after the import.</summary>
    private void RefreshMapModelPackPane(bool selectCurrent)
    {
        if (currentProject is null) return;
        AreaDataRecord? area = SelectedMapAreaData();
        int packCount = TexturePackService.Count(currentProject, TexturePackKind.Map);
        loadingMapModelPack = true;
        try
        {
            MapModelPackSelector.Items.Clear();
            for (int i = 0; i < packCount; i++) MapModelPackSelector.Items.Add($"Map Texture Pack [{i:D2}]{(i == mapModelNewPackId ? " - new, from the import" : string.Empty)}");
            if (area is null)
            {
                MapModelPackInfo.Text = MapModelList.SelectedIndex < 0 ? "Select a map to see which texture pack it uses." : "This map has no area data, so no texture pack is assigned.";
                MapModelPackTextures.Items.Clear();
                return;
            }

            int sharedWith = mapAreaData.Where(entry => entry.Value.Id == area.Id).Select(entry => entry.Key).Distinct().Count() - 1;
            MapModelPackInfo.Text = $"Area data {area.Id} gives this map Map Texture Pack [{area.MapTileset:D2}]" +
                (sharedWith > 0 ? $", and {sharedWith} other map{(sharedWith == 1 ? "" : "s")} share it." : ".");
            int selected = mapModelPendingPacks.TryGetValue(mapEntries[MapModelList.SelectedIndex].Id, out int pending) ? pending : area.MapTileset;
            MapModelPackSelector.SelectedIndex = selected < packCount ? selected : -1;
        }
        finally
        {
            loadingMapModelPack = false;
        }

        ShowMapModelPackContents();
    }

    private void ShowMapModelPackContents()
    {
        MapModelPackTextures.Items.Clear();
        AreaDataRecord? area = SelectedMapAreaData();
        int pack = MapModelPackSelector.SelectedIndex;
        if (currentProject is null || pack < 0) return;

        try
        {
            foreach (string name in NsbtxPaletteLayout.ReadTextureNames(File.ReadAllBytes(TexturePackService.GetPath(currentProject, TexturePackKind.Map, pack))))
            {
                MapModelPackTextures.Items.Add(name);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            MapModelPackTextures.Items.Add($"(unreadable: {ex.Message})");
        }
    }

    private void MapModelPackSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingMapModelPack) return;
        if (SelectedMapAreaData() is { } area && MapModelPackSelector.SelectedIndex >= 0)
        {
            int mapId = mapEntries[MapModelList.SelectedIndex].Id;
            if (MapModelPackSelector.SelectedIndex == area.MapTileset) mapModelPendingPacks.Remove(mapId);
            else mapModelPendingPacks[mapId] = MapModelPackSelector.SelectedIndex;
            RefreshUnsavedMarkers();
        }

        ShowMapModelPackContents();
        if (MapModelPackSelector.SelectedIndex >= 0)
        {
            loadingMapModelEditor = true;
            MapModelTextureSelector.SelectedIndex = MapModelPackSelector.SelectedIndex + 1;
            loadingMapModelEditor = false;
            _ = LoadMapModelPreviewAsync();
        }
    }

    /// <summary>Gives the map its pending pack and animation through a new area data (see
    /// <see cref="MapTextureAssignmentService"/>). What Save All runs for pending changes.</summary>
    private void ApplyMapModelChanges(int mapId)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        int? pack = mapModelPendingPacks.TryGetValue(mapId, out int pendingPack) ? pendingPack : null;
        int? animation = mapModelPendingAnimations.TryGetValue(mapId, out int pendingAnimation) ? pendingAnimation : null;
        if (pack is null && animation is null) return;
        List<int> affected;

        try
        {
            MapTextureAssignmentService.Result result = MapTextureAssignmentService.AssignAreaSettings(currentProject, metadata, mapId, pack, animation);
            mapModelPendingPacks.Remove(mapId);
            mapModelPendingAnimations.Remove(mapId);
            mapAreaData = AreaDataService.ResolveMapAreaData(currentProject, headerStore!.Load, currentProject.GetHeaderCount(), IsHeartGoldSoulSilver());
            areaDataRecords.Clear();
            affected = new[] { mapId }.Concat(result.OtherMaps).ToList();
            InvalidateCachedMapModels(affected.Select(id => (ushort)id));
            string what = string.Join(" and ", new[] { pack is int p ? $"Map Texture Pack [{p:D2}]" : null, animation is int a ? (a == DynamicTextureService.None ? "no texture animation" : $"texture animation set {a}") : null }.OfType<string>());
            string others = result.OtherMaps.Count > 0
                ? $" Map{(result.OtherMaps.Count == 1 ? "" : "s")} {string.Join(", ", result.OtherMaps.Take(8).Select(id => id.ToString("D4")))}{(result.OtherMaps.Count > 8 ? ", ..." : "")} share header {result.HeaderId:D4} with it and use the new area data too."
                : string.Empty;
            MapModelStatus.Text = $"Map {mapId:D4} now uses {what} through a new area data {result.AreaDataId} on header {result.HeaderId:D4}; no existing area data was changed.{others}";
        }
        catch (Exception ex)
        {
            LogUiError("Map area settings could not be assigned", ex);
            MapModelStatus.Text = ex.Message;
            return;
        }

        if (MapModelEditorPanel.IsVisible)
        {
            RefreshMapModelPackPane(selectCurrent: true);
            SelectMapModelAnimationFor(SelectedMapAreaData());
        }

        RefreshMapEditorAfterModelChange(affected);
        RefreshUnsavedMarkers();
    }

    /// <summary>The map views draw from converted models that the change just invalidated: the Single Map view
    /// reloads when it shows an affected map, and the multi-map views redraw when they are next shown.</summary>
    private void RefreshMapEditorAfterModelChange(IReadOnlyCollection<int> affectedMaps)
    {
        ResetMapViewHeaders();
        if (MapSelector.SelectedIndex >= 0 && MapSelector.SelectedIndex < mapEntries.Count &&
            affectedMaps.Contains(mapEntries[MapSelector.SelectedIndex].Id))
        {
            LoadSelectedMap();
        }
    }

    private void MapModelSave_Click(object? sender, RoutedEventArgs e)
    {
        int? mapId = MapModelList.SelectedIndex >= 0 && MapModelList.SelectedIndex < mapEntries.Count ? mapEntries[MapModelList.SelectedIndex].Id : null;
        List<PendingMapImport> imports = mapModelPendingImports
            .Where(import => (import.MapId is not null && import.MapId == mapId) || (import.MapId is null && mapModelFile == import.Converted.NsbmdPath))
            .ToList();
        bool pending = mapId is int id && (mapModelPendingPacks.ContainsKey(id) || mapModelPendingAnimations.ContainsKey(id));
        if (imports.Count == 0 && !pending)
        {
            MapModelStatus.Text = mapId is int none ? $"Map {none:D4} has no unsaved model changes." : "Nothing to save.";
            return;
        }

        foreach (PendingMapImport import in imports) ApplyMapModelImport(import);
        if (mapId is int remaining && (mapModelPendingPacks.ContainsKey(remaining) || mapModelPendingAnimations.ContainsKey(remaining))) ApplyMapModelChanges(remaining);
    }

    private void CollectUnsavedMapModelChanges(List<UnsavedChange> changes)
    {
        foreach (PendingMapImport import in mapModelPendingImports.ToList())
        {
            string target = import.MapId is int id ? $"replaces the model of map {id:D4}" : "makes a new map";
            changes.Add(new UnsavedChange("Map Model Editor", "Map model import", [$"{import.Converted.Name}.obj {target} ({import.Converted.ModelSize:N0} bytes)"], () => ApplyMapModelImport(import), OpenDocument: false));
        }

        foreach (int mapId in mapModelPendingPacks.Keys.Concat(mapModelPendingAnimations.Keys).Distinct().Order().ToList())
        {
            if (!mapAreaData.TryGetValue(mapId, out AreaDataRecord? area))
            {
                mapModelPendingPacks.Remove(mapId);
                mapModelPendingAnimations.Remove(mapId);
                continue;
            }

            var details = new List<string>();
            if (mapModelPendingPacks.TryGetValue(mapId, out int pack))
            {
                if (area.MapTileset == pack) mapModelPendingPacks.Remove(mapId);
                else details.Add($"Map {mapId:D4}: Map Texture Pack [{area.MapTileset:D2}] -> [{pack:D2}] (through a new area data)");
            }

            if (mapModelPendingAnimations.TryGetValue(mapId, out int animation))
            {
                if (area.DynamicTextureType == animation) mapModelPendingAnimations.Remove(mapId);
                else details.Add($"Map {mapId:D4}: texture animation {DescribeAnimation(area.DynamicTextureType)} -> {DescribeAnimation(animation)} (through a new area data)");
            }

            if (details.Count > 0) changes.Add(new UnsavedChange("Map Model Editor", "Map texture pack and animation", details, () => ApplyMapModelChanges(mapId), OpenDocument: false));
        }
    }

    private static string DescribeAnimation(int type) => type == DynamicTextureService.None ? "none" : $"set {type}";

    private async Task LoadMapModelPreviewAsync()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;

        byte[] model;
        string label;
        try
        {
            if (mapModelFile is not null && mapModelFile.EndsWith(".obj", StringComparison.OrdinalIgnoreCase))
            {
                ShowObjModel(mapModelFile);
                return;
            }

            if (mapModelFile is not null)
            {
                model = await File.ReadAllBytesAsync(mapModelFile);
                label = Path.GetFileName(mapModelFile);
            }
            else if (MapModelList.SelectedIndex >= 0 && MapModelList.SelectedIndex < mapEntries.Count)
            {
                MapViewEntry map = mapEntries[MapModelList.SelectedIndex];
                model = MapFileService.Load(currentProject, metadata, map.Id).Model;
                label = $"map {map.Id:D4}";
            }
            else
            {
                return;
            }
        }
        catch (Exception ex)
        {
            LogUiError("Map model could not be read", ex);
            MapModelStatus.Text = ex.Message;
            return;
        }

        if (!NsbmdTextureTools.IsNsbmd(model))
        {
            MapModelStatus.Text = $"{label} is not an NSBMD model.";
            MapModelViewerPlaceholder.Text = "That file is not an NSBMD model.";
            MapModelViewerPlaceholder.IsVisible = true;
            return;
        }

        int texturePackId = MapModelTextureSelector.SelectedIndex - 1;
        string texturePath = TexturePackService.GetPath(currentProject, TexturePackKind.Map, texturePackId);
        byte[]? pack = texturePackId >= 0 && File.Exists(texturePath) ? await File.ReadAllBytesAsync(texturePath) : null;
        int load = ++mapModelLoads;
        string output = Path.Combine(currentProject.RootPath, "expanded", "map_model_preview");

        BeginBlockingLoad("Loading map model...", showImmediately: false);
        try
        {
            if (Directory.Exists(output)) Directory.Delete(output, true);
            await MapFileService.ConvertModelAsync(currentProject, metadata, ToolsDirectory,
                new MapFileParts(null, [], [], model, []), pack, output, glb: true);
            if (load != mapModelLoads) return;

            string? glb = Directory.EnumerateFiles(output, "*.glb", SearchOption.AllDirectories).FirstOrDefault();
            if (glb is null) throw new FileNotFoundException("apicula produced no GLB for this model.");
            MapModelViewer.LoadGlb(glb);
            MapModelViewer.ResetView();
            MapModelViewerPlaceholder.IsVisible = false;
            string packLabel = texturePackId < 0 ? "embedded textures" : $"map texture pack {texturePackId:D2}";
            MapModelStatus.Text = $"Loaded {label} ({MapModelViewer.TriangleCount:N0} triangles, {packLabel}).";
        }
        catch (Exception ex)
        {
            LogUiError("Map model could not be loaded", ex);
            MapModelStatus.Text = ex.Message;
            MapModelViewerPlaceholder.Text = "Map model could not be loaded.";
            MapModelViewerPlaceholder.IsVisible = true;
        }
        finally
        {
            EndBlockingLoad();
        }
    }

    private void ShowObjModel(string path)
    {
        try
        {
            ObjModelConverter.Result result = ObjModelConverter.ToGlb(path, zUp: true);
            string output = Path.Combine(currentProject!.RootPath, "expanded", "map_model_preview");
            if (Directory.Exists(output)) Directory.Delete(output, true);
            Directory.CreateDirectory(output);
            string glb = Path.Combine(output, "model.glb");
            File.WriteAllBytes(glb, result.Glb);
            MapModelViewer.LoadGlb(glb);
            MapModelViewer.ResetView();
            MapModelViewerPlaceholder.IsVisible = false;
            MapModelStatus.Text = $"Loaded {Path.GetFileName(path)} ({result.Triangles:N0} triangles, shaded, with textures from its .mtl when found).";
        }
        catch (Exception ex)
        {
            LogUiError("OBJ model could not be loaded", ex);
            MapModelStatus.Text = ex.Message;
            MapModelViewerPlaceholder.Text = "That OBJ could not be loaded.";
            MapModelViewerPlaceholder.IsVisible = true;
        }
    }
}
