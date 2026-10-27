using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DSPRE.Core;
using System.IO;
using System.Runtime.InteropServices;

namespace DSPRE.Avalonia;

/// <summary>The Map Creator: builds a map's terrain from tiles placed on a 32 x 32 grid, as Pokemon DS Map Studio
/// does. Tiles come from Studio tilesets or are cut from the project's own maps. The finished map becomes an OBJ,
/// then goes through the same staged conversion as any imported model (see <see cref="MapModelImportService"/>).</summary>
public partial class MainWindow
{
    private const int CreatorThumbnailPixels = 32;
    private const int CreatorUndoLimit = 100;

    private MapCreatorDocument? creatorDocument;
    private bool creatorDirty;
    private bool loadingMapCreator;
    private int creatorSelectedTile = -1;
    private readonly Stack<MapCreatorMap> creatorUndo = new();
    private readonly Stack<MapCreatorMap> creatorRedo = new();
    private readonly Dictionary<int, Bitmap?> creatorTileBitmaps = [];
    private MapCreatorMap? creatorStrokeStart;
    private bool creatorStrokeChanged;
    private (int Column, int Row)? creatorStrokeAnchor;
    private (int Column, int Row)? creatorLastPlaced;
    private bool creatorGridWired;

    private enum CreatorTool { Paint, Erase, Fill, Pick, Height }

    private CreatorTool ActiveCreatorTool =>
        MapCreatorToolErase.IsChecked == true ? CreatorTool.Erase
        : MapCreatorToolFill.IsChecked == true ? CreatorTool.Fill
        : MapCreatorToolPick.IsChecked == true ? CreatorTool.Pick
        : MapCreatorToolHeight.IsChecked == true ? CreatorTool.Height
        : CreatorTool.Paint;

    private void ConfigureMapCreator()
    {
        if (currentProject is null) return;
        WireMapCreatorGrid();
        loadingMapCreator = true;
        try
        {
            if (MapCreatorLayerSelector.Items.Count == 0)
            {
                for (int layer = 1; layer <= MapCreatorMap.Layers; layer++) MapCreatorLayerSelector.Items.Add(layer);
                MapCreatorLayerSelector.SelectedIndex = 0;
            }

            MapCreatorTargetMap.Items.Clear();
            foreach (MapViewEntry map in mapEntries) MapCreatorTargetMap.Items.Add($"[{map.Id:D4}] {map.Name}");

            FillCreatorDocumentList(creatorDocument?.Name);
        }
        finally
        {
            loadingMapCreator = false;
        }

        if (creatorDocument is null && MapCreatorDocSelector.Items.Count > 0)
        {
            MapCreatorDocSelector.SelectedIndex = 0;
        }
        else
        {
            RefreshMapCreatorView();
        }
    }

    private void FillCreatorDocumentList(string? select)
    {
        IReadOnlyList<string> names = MapCreatorDocument.List(currentProject!);
        // Rebuilding the list from inside the selector's own SelectionChanged crashes Avalonia's selection model, so an unchanged list is left alone.
        if (!MapCreatorDocSelector.Items.Cast<object>().SequenceEqual(names))
        {
            MapCreatorDocSelector.Items.Clear();
            foreach (string name in names) MapCreatorDocSelector.Items.Add(name);
        }

        int index = select is null ? -1 : MapCreatorDocSelector.Items.IndexOf(select);
        if (MapCreatorDocSelector.SelectedIndex != index) MapCreatorDocSelector.SelectedIndex = index;
    }

    private void WireMapCreatorGrid()
    {
        if (creatorGridWired) return;
        creatorGridWired = true;
        MapCreatorGrid.TileImage = CreatorTileBitmap;
        MapCreatorGrid.LayerImage = CreatorLayerBitmap;
        MapCreatorGrid.TileSize = index => creatorDocument is { } document && index < document.Tileset.Tiles.Count
            ? (Math.Max(document.Tileset.Tiles[index].Width, 1), Math.Max(document.Tileset.Tiles[index].Height, 1))
            : (1, 1);
        MapCreatorGrid.StrokeStarted += _ =>
        {
            if (creatorDocument is null) return;
            creatorStrokeStart = creatorDocument.Map.Clone();
            creatorStrokeChanged = false;
            creatorStrokeAnchor = null;
            creatorLastPlaced = null;
        };
        MapCreatorGrid.CellTouched += CreatorCellTouched;
        MapCreatorGrid.StrokeEnded += CreatorStrokeEnded;
        MapCreatorGrid.HoverChanged += (column, row) =>
        {
            if (creatorDocument is null || column < 0) return;
            int layer = ActiveCreatorLayer;
            int tile = creatorDocument.Map.Tiles[layer][column, row];
            MapCreatorStatus.Text = $"Cell {column}, {row} (layer {layer + 1}): "
                + (tile >= 0 && tile < creatorDocument.Tileset.Tiles.Count ? $"tile {tile} \"{creatorDocument.Tileset.Tiles[tile].Name}\", height {creatorDocument.Map.Heights[layer][column, row]}" : "empty");
        };
    }

    private int ActiveCreatorLayer => Math.Clamp(MapCreatorLayerSelector.SelectedIndex, 0, MapCreatorMap.Layers - 1);

    private void ResetMapCreator()
    {
        creatorDocument = null;
        creatorDirty = false;
        creatorSelectedTile = -1;
        creatorStrokeStart = null;
        creatorUndo.Clear();
        creatorRedo.Clear();
        creatorTileBitmaps.Clear();
        MapCreatorGrid.Map = null;
        MapCreatorGrid.SelectedTile = -1;
        loadingMapCreator = true;
        try
        {
            MapCreatorDocSelector.Items.Clear();
            MapCreatorTileList.Items.Clear();
            MapCreatorTargetMap.Items.Clear();
        }
        finally
        {
            loadingMapCreator = false;
        }

        MapCreatorStatus.Text = "Make or open a map to start.";
        MapCreatorStats.Text = string.Empty;
        MapCreatorTileInfo.Text = "Select a tile.";
        MapCreatorViewerPlaceholder.IsVisible = true;
        MapCreatorGrid.InvalidateVisual();
    }

    #region Documents

    private async void MapCreatorDocSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingMapCreator || currentProject is null) return;
        string? name = MapCreatorDocSelector.SelectedItem as string;
        if (name is null || name == creatorDocument?.Name) return;

        if (creatorDirty && creatorDocument is not null)
        {
            string? choice = await new ChoiceDialog("Unsaved map", $"\"{creatorDocument.Name}\" has unsaved changes.",
                ("save", "Save"), ("discard", "Discard"), ("cancel", "Cancel")).ShowDialog<string?>(this);
            if (choice is not ("save" or "discard"))
            {
                loadingMapCreator = true;
                MapCreatorDocSelector.SelectedItem = creatorDocument.Name;
                loadingMapCreator = false;
                return;
            }

            if (choice == "save") SaveMapCreator();
        }

        OpenCreatorDocument(() => MapCreatorDocument.Open(currentProject, name));
    }

    private void OpenCreatorDocument(Func<MapCreatorDocument> open)
    {
        try
        {
            creatorDocument = open();
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FormatException or IndexOutOfRangeException)
        {
            LogUiError("Map Creator map could not be opened", ex);
            MapCreatorStatus.Text = $"That map could not be opened: {ex.Message}";
            return;
        }

        creatorDirty = false;
        creatorSelectedTile = -1;
        creatorUndo.Clear();
        creatorRedo.Clear();
        creatorTileBitmaps.Clear();
        loadingMapCreator = true;
        try
        {
            FillCreatorDocumentList(creatorDocument.Name);
        }
        finally
        {
            loadingMapCreator = false;
        }

        MapCreatorViewerPlaceholder.Text = "Refresh the preview to see the map in 3D.";
        MapCreatorViewerPlaceholder.IsVisible = true;
        RefreshMapCreatorView();
        RefreshUnsavedMarkers();
        MapCreatorStatus.Text = $"Opened \"{creatorDocument.Name}\".";
    }

    private async void MapCreatorNew_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        if (!await ConfirmLeaveCreatorDocumentAsync()) return;
        string? name = await new TextPromptDialog("New map", "Name of the new map (it becomes the model's name)", "new_map").ShowDialog<string?>(this);
        if (name is null) return;
        OpenCreatorDocument(() => MapCreatorDocument.Create(currentProject, metadata, name));
    }

    private async void MapCreatorImportPdsmap_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null) return;
        if (!await ConfirmLeaveCreatorDocumentAsync()) return;
        string? path = await PickFileAsync("Pick a Pokemon DS Map Studio map (its tileset and textures must be beside it)", "Studio map", "*.pdsmap");
        if (path is null) return;
        int maps = MapCreatorMap.CountMaps(path);
        OpenCreatorDocument(() => MapCreatorDocument.ImportPdsmap(currentProject, path));
        if (maps > 1) MapCreatorStatus.Text += $" The file holds {maps} maps; the first was copied.";
    }

    /// <summary>True to go on: the open map has no unsaved changes, or they were saved or given up.</summary>
    private async Task<bool> ConfirmLeaveCreatorDocumentAsync()
    {
        if (!creatorDirty || creatorDocument is null) return true;
        string? choice = await new ChoiceDialog("Unsaved map", $"\"{creatorDocument.Name}\" has unsaved changes.",
            ("save", "Save"), ("discard", "Discard"), ("cancel", "Cancel")).ShowDialog<string?>(this);
        if (choice == "save") SaveMapCreator();
        return choice is "save" or "discard";
    }

    private void MapCreatorSave_Click(object? sender, RoutedEventArgs e) => SaveMapCreator();

    private void SaveMapCreator()
    {
        if (creatorDocument is null) return;
        try
        {
            creatorDocument.Save();
        }
        catch (IOException ex)
        {
            LogUiError("Map Creator map could not be saved", ex);
            MapCreatorStatus.Text = $"The map could not be saved: {ex.Message}";
            return;
        }

        creatorDirty = false;
        MapCreatorStatus.Text = $"Saved \"{creatorDocument.Name}\" in {creatorDocument.Folder}.";
        RefreshUnsavedMarkers();
    }

    private async void MapCreatorDelete_Click(object? sender, RoutedEventArgs e)
    {
        if (creatorDocument is null) return;
        string? choice = await new ChoiceDialog("Delete map", $"Delete \"{creatorDocument.Name}\" and its tileset from the Map Creator? Maps already built into the project are not affected.",
            ("delete", "Delete"), ("cancel", "Cancel")).ShowDialog<string?>(this);
        if (choice != "delete") return;
        try
        {
            creatorDocument.Delete();
        }
        catch (IOException ex)
        {
            MapCreatorStatus.Text = ex.Message;
            return;
        }

        ResetMapCreator();
        ConfigureMapCreator();
        RefreshUnsavedMarkers();
    }

    private void CollectUnsavedMapCreatorChanges(List<UnsavedChange> changes)
    {
        if (creatorDocument is null || !creatorDirty) return;
        changes.Add(new UnsavedChange("Map Creator", "Map Creator map", [$"\"{creatorDocument.Name}\": tiles placed on the grid or in the tileset"], SaveMapCreator, OpenDocument: true));
    }

    private void MarkCreatorDirty()
    {
        if (creatorDirty) return;
        creatorDirty = true;
        RefreshUnsavedMarkers();
    }

    #endregion

    #region Tiles

    private Bitmap? CreatorTileBitmap(int index)
    {
        if (creatorDocument is null || index < 0 || index >= creatorDocument.Tileset.Tiles.Count) return null;
        if (creatorTileBitmaps.TryGetValue(index, out Bitmap? cached)) return cached;
        TextureImage image = MapCreatorThumbnails.Render(creatorDocument.Tileset, creatorDocument.Tileset.Tiles[index], CreatorThumbnailPixels);
        WriteableBitmap bitmap = ToBitmap(image);
        creatorTileBitmaps[index] = bitmap;
        return bitmap;
    }

    private static WriteableBitmap ToBitmap(TextureImage image)
    {
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using ILockedFramebuffer buffer = bitmap.Lock();
        for (int y = 0; y < image.Height; y++) Marshal.Copy(image.Rgba, y * image.Width * 4, buffer.Address + y * buffer.RowBytes, image.Width * 4);
        return bitmap;
    }

    private readonly Dictionary<int, Bitmap?> creatorLayerBitmaps = [];

    private void InvalidateCreatorLayers() => creatorLayerBitmaps.Clear();

    /// <summary>A layer as the finished map shows it from above, so tiles that reach over a cell border, or are stretched over
    /// several cells, look as they will. Null while a stroke is changing the grid; the grid then draws tile by tile.</summary>
    private Bitmap? CreatorLayerBitmap(int layer)
    {
        if (creatorDocument is null || (creatorStrokeStart is not null && creatorStrokeChanged)) return null;
        if (creatorLayerBitmaps.TryGetValue(layer, out Bitmap? cached)) return cached;
        Bitmap? bitmap = null;
        try
        {
            var single = new MapCreatorMap();
            Array.Copy(creatorDocument.Map.Tiles[layer], single.Tiles[0], single.Tiles[0].Length);
            Array.Copy(creatorDocument.Map.Heights[layer], single.Heights[0], single.Heights[0].Length);
            if (!single.IsEmpty)
            {
                int half = MapCreatorMap.Columns / 2;
                MapCreatorComposer.Composition composition = MapCreatorComposer.Compose(creatorDocument.Tileset, single, compact: false);
                var whole = new MapTile
                {
                    Width = MapCreatorMap.Columns, Height = MapCreatorMap.Rows,
                    Polygons = composition.Polygons.Select(polygon => polygon with { Vertices = polygon.Vertices.Select(v => v with { X = v.X + half, Y = v.Y + half }).ToArray() }).ToList()
                };
                bitmap = ToBitmap(MapCreatorThumbnails.Render(creatorDocument.Tileset, whole, 20));
            }
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidOperationException or IndexOutOfRangeException)
        {
            LogUiError("Map Creator layer could not be drawn", ex);
        }

        creatorLayerBitmaps[layer] = bitmap;
        return bitmap;
    }

    private void RefreshMapCreatorView()
    {
        InvalidateCreatorLayers();
        loadingMapCreator = true;
        try
        {
            MapCreatorTileList.Items.Clear();
            if (creatorDocument is not null)
            {
                for (int i = 0; i < creatorDocument.Tileset.Tiles.Count; i++) MapCreatorTileList.Items.Add(CreatorPaletteItem(i));
                if (creatorSelectedTile >= 0 && creatorSelectedTile < creatorDocument.Tileset.Tiles.Count) MapCreatorTileList.SelectedIndex = creatorSelectedTile;
                else creatorSelectedTile = -1;
            }
        }
        finally
        {
            loadingMapCreator = false;
        }

        MapCreatorGrid.Map = creatorDocument?.Map;
        MapCreatorGrid.ActiveLayer = ActiveCreatorLayer;
        MapCreatorGrid.ShowOtherLayers = MapCreatorShowOtherLayers.IsChecked == true;
        MapCreatorGrid.SelectedTile = creatorSelectedTile;
        MapCreatorTileCount.Text = creatorDocument is null ? "Tiles" : $"Tiles ({creatorDocument.Tileset.Tiles.Count})";
        ShowCreatorTileProperties();
        UpdateCreatorStats();
        MapCreatorGrid.InvalidateVisual();
    }

    private Control CreatorPaletteItem(int index)
    {
        MapTile tile = creatorDocument!.Tileset.Tiles[index];
        var image = new Image { Source = CreatorTileBitmap(index), Width = 40, Height = 40, Stretch = Stretch.Uniform };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.MediumQuality);
        ToolTip.SetTip(image, $"{index}: {tile.Name}{(tile.Width > 1 || tile.Height > 1 ? $" ({tile.Width} x {tile.Height} cells)" : string.Empty)}");
        return image;
    }

    private void MapCreatorTileList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingMapCreator) return;
        creatorSelectedTile = MapCreatorTileList.SelectedIndex;
        MapCreatorGrid.SelectedTile = creatorSelectedTile;
        ShowCreatorTileProperties();
        MapCreatorGrid.InvalidateVisual();
    }

    private void ShowCreatorTileProperties()
    {
        loadingMapCreator = true;
        try
        {
            bool has = creatorDocument is not null && creatorSelectedTile >= 0 && creatorSelectedTile < creatorDocument.Tileset.Tiles.Count;
            foreach (Control control in new Control[] { MapCreatorTileWidth, MapCreatorTileHeight, MapCreatorTileXTileable, MapCreatorTileYTileable, MapCreatorTileUTileable, MapCreatorTileVTileable }) control.IsEnabled = has;
            if (!has)
            {
                MapCreatorTileInfo.Text = "Select a tile.";
                return;
            }

            MapTile tile = creatorDocument!.Tileset.Tiles[creatorSelectedTile];
            MapCreatorTileInfo.Text = $"Tile {creatorSelectedTile}: {tile.Name} - {tile.Polygons.Count:N0} polygons";
            MapCreatorTileWidth.Value = tile.Width;
            MapCreatorTileHeight.Value = tile.Height;
            MapCreatorTileXTileable.IsChecked = tile.XTileable;
            MapCreatorTileYTileable.IsChecked = tile.YTileable;
            MapCreatorTileUTileable.IsChecked = tile.UTileable;
            MapCreatorTileVTileable.IsChecked = tile.VTileable;
        }
        finally
        {
            loadingMapCreator = false;
        }
    }

    private void MapCreatorTileValue_Changed(object? sender, NumericUpDownValueChangedEventArgs e) => ApplyCreatorTileProperties();

    private void MapCreatorTileProperty_Changed(object? sender, RoutedEventArgs e) => ApplyCreatorTileProperties();

    private void ApplyCreatorTileProperties()
    {
        if (loadingMapCreator || creatorDocument is null || creatorSelectedTile < 0 || creatorSelectedTile >= creatorDocument.Tileset.Tiles.Count) return;
        MapTile tile = creatorDocument.Tileset.Tiles[creatorSelectedTile];
        int width = (int)(MapCreatorTileWidth.Value ?? 1), height = (int)(MapCreatorTileHeight.Value ?? 1);
        bool resized = width != tile.Width || height != tile.Height;
        tile.Width = Math.Max(width, 1);
        tile.Height = Math.Max(height, 1);
        tile.XTileable = MapCreatorTileXTileable.IsChecked == true;
        tile.YTileable = MapCreatorTileYTileable.IsChecked == true;
        tile.UTileable = MapCreatorTileUTileable.IsChecked == true;
        tile.VTileable = MapCreatorTileVTileable.IsChecked == true;
        InvalidateCreatorLayers();
        MarkCreatorDirty();
        if (resized)
        {
            creatorTileBitmaps.Remove(creatorSelectedTile);
            int selected = creatorSelectedTile;
            RefreshMapCreatorView();
            loadingMapCreator = true;
            MapCreatorTileList.SelectedIndex = selected;
            loadingMapCreator = false;
        }

        UpdateCreatorStats();
    }

    private async void MapCreatorAddTileset_Click(object? sender, RoutedEventArgs e)
    {
        if (creatorDocument is null)
        {
            MapCreatorStatus.Text = "Make or open a map first.";
            return;
        }

        string? path = await new MapCreatorTilesetPickerDialog(MapCreatorTileset.FindBundled()).ShowDialog<string?>(this);
        if (path == string.Empty) path = await PickFileAsync("Pick a Pokemon DS Map Studio tileset (its textures must be beside it)", "Studio tileset", "*.pdsts");
        if (path is null) return;
        try
        {
            MapCreatorTileset source = MapCreatorTileset.Read(path);
            int first = creatorDocument.Tileset.Merge(source);
            MarkCreatorDirty();
            RefreshMapCreatorView();
            MapCreatorStatus.Text = $"Added {source.Tiles.Count} tiles from {Path.GetFileName(path)} (tiles {first} to {creatorDocument.Tileset.Tiles.Count - 1}).";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
        {
            LogUiError("Tileset could not be read", ex);
            MapCreatorStatus.Text = $"That tileset could not be read: {ex.Message}";
        }
    }

    private async void MapCreatorCutMap_Click(object? sender, RoutedEventArgs e)
    {
        if (creatorDocument is null || currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            MapCreatorStatus.Text = "Make or open a map first.";
            return;
        }

        var labels = mapEntries.Select(map => $"[{map.Id:D4}] {map.Name}").ToList();
        var packs = mapEntries.Select(entry => mapAreaData.TryGetValue(entry.Id, out AreaDataRecord? record) ? (int)record.MapTileset : -1).ToList();
        int packCount = TexturePackService.Count(currentProject, TexturePackKind.Map);
        MapCreatorCutDialog.Choice? choice = await new MapCreatorCutDialog(labels, packs, packCount, 0).ShowDialog<MapCreatorCutDialog.Choice?>(this);
        if (choice is null || choice.MapIndex < 0 || choice.MapIndex >= mapEntries.Count) return;

        MapViewEntry map = mapEntries[choice.MapIndex];
        MapCreatorMap before = creatorDocument.Map.Clone();
        MapCreatorTileCutter.Result result;
        BeginBlockingLoad($"Cutting map {map.Id:D4} into tiles...", showImmediately: true);
        try
        {
            result = await MapCreatorTileCutter.CutMapAsync(currentProject, metadata, ToolsDirectory, map.Id, choice.TexturePack, creatorDocument.Tileset, $"m{map.Id:D4}");
        }
        catch (Exception ex)
        {
            LogUiError("Map could not be cut into tiles", ex);
            MapCreatorStatus.Text = $"The map could not be cut: {ex.Message}";
            return;
        }
        finally
        {
            EndBlockingLoad();
        }

        if (choice.PlaceOnGrid)
        {
            PushCreatorUndo(before);
            int layer = ActiveCreatorLayer;
            for (int c = 0; c < MapCreatorMap.Columns; c++)
            {
                for (int r = 0; r < MapCreatorMap.Rows; r++)
                {
                    creatorDocument.Map.Tiles[layer][c, r] = result.Cells.TryGetValue((c, r), out int tile) ? tile : -1;
                    creatorDocument.Map.Heights[layer][c, r] = 0;
                }
            }
        }

        MarkCreatorDirty();
        RefreshMapCreatorView();
        MapCreatorStatus.Text = $"Cut map {map.Id:D4} into {result.TilesAdded} new tiles from {result.CellsCut} cells{(choice.PlaceOnGrid ? $" and placed it on layer {ActiveCreatorLayer + 1}" : string.Empty)}.";
    }

    private void MapCreatorRemoveUnused_Click(object? sender, RoutedEventArgs e)
    {
        if (creatorDocument is null) return;
        PushCreatorUndo(creatorDocument.Map.Clone());
        int removed = creatorDocument.RemoveUnusedTiles();
        creatorTileBitmaps.Clear();
        creatorSelectedTile = -1;
        if (removed > 0) MarkCreatorDirty();
        RefreshMapCreatorView();
        MapCreatorStatus.Text = removed == 0 ? "Every tile is used." : $"Removed {removed} unused tiles. Undo restores the grid, not the tiles.";
    }

    #endregion

    #region Editing the grid

    private void MapCreatorLayerSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingMapCreator) return;
        MapCreatorGrid.ActiveLayer = ActiveCreatorLayer;
        MapCreatorGrid.InvalidateVisual();
    }

    private void MapCreatorShowOtherLayers_Changed(object? sender, RoutedEventArgs e)
    {
        MapCreatorGrid.ShowOtherLayers = MapCreatorShowOtherLayers.IsChecked == true;
        MapCreatorGrid.InvalidateVisual();
    }

    private void CreatorCellTouched(int column, int row, bool secondary)
    {
        if (creatorDocument is null) return;
        MapCreatorMap map = creatorDocument.Map;
        int layer = ActiveCreatorLayer;
        int height = (int)(MapCreatorHeight.Value ?? 0);
        CreatorTool tool = secondary ? CreatorTool.Erase : ActiveCreatorTool;

        switch (tool)
        {
            case CreatorTool.Pick:
            {
                int tile = map.Tiles[layer][column, row];
                if (tile >= 0 && tile < creatorDocument.Tileset.Tiles.Count)
                {
                    loadingMapCreator = true;
                    MapCreatorTileList.SelectedIndex = creatorSelectedTile = tile;
                    MapCreatorHeight.Value = map.Heights[layer][column, row];
                    MapCreatorGrid.SelectedTile = tile;
                    loadingMapCreator = false;
                    ShowCreatorTileProperties();
                    MapCreatorToolPaint.IsChecked = true;
                }

                break;
            }
            case CreatorTool.Erase:
                if (map.Tiles[layer][column, row] >= 0)
                {
                    map.Tiles[layer][column, row] = -1;
                    creatorStrokeChanged = true;
                }

                break;
            case CreatorTool.Height:
                if (map.Heights[layer][column, row] != height)
                {
                    map.Heights[layer][column, row] = height;
                    creatorStrokeChanged = true;
                }

                break;
            case CreatorTool.Fill:
                if (creatorSelectedTile >= 0) creatorStrokeChanged |= FillCreatorCells(map, layer, column, row, creatorSelectedTile, height);
                break;
            default:
                if (creatorSelectedTile >= 0) PaintCreatorTile(map, layer, column, row, height);
                break;
        }

        MapCreatorGrid.InvalidateVisual();
    }

    /// <summary>A tile of several cells dragged across the grid lands on a lattice of its own size anchored at the first cell of the
    /// stroke, so the copies sit side by side instead of overlapping at every cell the pointer passes.</summary>
    private void PaintCreatorTile(MapCreatorMap map, int layer, int column, int row, int height)
    {
        MapTile definition = creatorDocument!.Tileset.Tiles[creatorSelectedTile];
        int width = Math.Max(definition.Width, 1), tall = Math.Max(definition.Height, 1);
        (int anchorColumn, int anchorRow) = creatorStrokeAnchor ??= (column, row);
        int snappedColumn = anchorColumn + (int)Math.Floor((column - anchorColumn) / (double)width) * width;
        int snappedRow = anchorRow + (int)Math.Floor((row - anchorRow) / (double)tall) * tall;
        if (snappedColumn < 0 || snappedRow < 0 || snappedColumn >= MapCreatorMap.Columns || snappedRow >= MapCreatorMap.Rows) return;
        if (creatorLastPlaced == (snappedColumn, snappedRow)) return;
        creatorLastPlaced = (snappedColumn, snappedRow);
        creatorStrokeChanged |= PlaceCreatorTile(map, layer, snappedColumn, snappedRow, creatorSelectedTile, height);
    }

    private bool PlaceCreatorTile(MapCreatorMap map, int layer, int column, int row, int tile, int height)
    {
        MapTile definition = creatorDocument!.Tileset.Tiles[tile];
        int width = Math.Max(definition.Width, 1), tall = Math.Max(definition.Height, 1);
        bool changed = map.Tiles[layer][column, row] != tile || map.Heights[layer][column, row] != height;
        map.Tiles[layer][column, row] = tile;
        map.Heights[layer][column, row] = height;

        // A tile covering several cells takes the others' tiles with it, as its own polygons stand there.
        for (int c = column; c < Math.Min(column + width, MapCreatorMap.Columns); c++)
        {
            for (int r = row; r < Math.Min(row + tall, MapCreatorMap.Rows); r++)
            {
                if ((c != column || r != row) && map.Tiles[layer][c, r] >= 0)
                {
                    map.Tiles[layer][c, r] = -1;
                    changed = true;
                }
            }
        }

        return changed;
    }

    private bool FillCreatorCells(MapCreatorMap map, int layer, int column, int row, int tile, int height)
    {
        int target = map.Tiles[layer][column, row];
        if (target == tile && map.Heights[layer][column, row] == height) return false;
        var pending = new Queue<(int C, int R)>();
        var seen = new bool[MapCreatorMap.Columns, MapCreatorMap.Rows];
        pending.Enqueue((column, row));
        seen[column, row] = true;
        while (pending.Count > 0)
        {
            (int c, int r) = pending.Dequeue();
            map.Tiles[layer][c, r] = tile;
            map.Heights[layer][c, r] = height;
            foreach ((int dc, int dr) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
            {
                int nc = c + dc, nr = r + dr;
                if (nc < 0 || nr < 0 || nc >= MapCreatorMap.Columns || nr >= MapCreatorMap.Rows || seen[nc, nr] || map.Tiles[layer][nc, nr] != target) continue;
                seen[nc, nr] = true;
                pending.Enqueue((nc, nr));
            }
        }

        return true;
    }

    private void CreatorStrokeEnded()
    {
        if (creatorDocument is null || creatorStrokeStart is null) return;
        if (creatorStrokeChanged)
        {
            InvalidateCreatorLayers();
            PushCreatorUndo(creatorStrokeStart);
            MarkCreatorDirty();
            UpdateCreatorStats();
        }

        creatorStrokeStart = null;
        creatorStrokeChanged = false;
        MapCreatorGrid.InvalidateVisual();
    }

    private void PushCreatorUndo(MapCreatorMap snapshot)
    {
        creatorUndo.Push(snapshot);
        creatorRedo.Clear();
        if (creatorUndo.Count > CreatorUndoLimit)
        {
            MapCreatorMap[] kept = creatorUndo.Take(CreatorUndoLimit).Reverse().ToArray();
            creatorUndo.Clear();
            foreach (MapCreatorMap map in kept) creatorUndo.Push(map);
        }
    }

    private void MapCreatorUndo_Click(object? sender, RoutedEventArgs e) => StepCreatorHistory(creatorUndo, creatorRedo);

    private void MapCreatorRedo_Click(object? sender, RoutedEventArgs e) => StepCreatorHistory(creatorRedo, creatorUndo);

    private void StepCreatorHistory(Stack<MapCreatorMap> from, Stack<MapCreatorMap> to)
    {
        if (creatorDocument is null || from.Count == 0) return;
        to.Push(creatorDocument.Map.Clone());
        MapCreatorMap snapshot = from.Pop();
        for (int layer = 0; layer < MapCreatorMap.Layers; layer++)
        {
            Array.Copy(snapshot.Tiles[layer], creatorDocument.Map.Tiles[layer], snapshot.Tiles[layer].Length);
            Array.Copy(snapshot.Heights[layer], creatorDocument.Map.Heights[layer], snapshot.Heights[layer].Length);
        }

        InvalidateCreatorLayers();
        MarkCreatorDirty();
        UpdateCreatorStats();
        MapCreatorGrid.InvalidateVisual();
    }

    private void MapCreatorClearLayer_Click(object? sender, RoutedEventArgs e)
    {
        if (creatorDocument is null) return;
        PushCreatorUndo(creatorDocument.Map.Clone());
        int layer = ActiveCreatorLayer;
        for (int c = 0; c < MapCreatorMap.Columns; c++)
        {
            for (int r = 0; r < MapCreatorMap.Rows; r++)
            {
                creatorDocument.Map.Tiles[layer][c, r] = -1;
                creatorDocument.Map.Heights[layer][c, r] = 0;
            }
        }

        InvalidateCreatorLayers();
        MarkCreatorDirty();
        UpdateCreatorStats();
        MapCreatorGrid.InvalidateVisual();
    }

    #endregion

    #region Preview and build

    private void UpdateCreatorStats()
    {
        if (creatorDocument is null)
        {
            MapCreatorStats.Text = string.Empty;
            return;
        }

        MapCreatorMap map = creatorDocument.Map;
        int cells = map.Tiles.Sum(layer => layer.Cast<int>().Count(value => value >= 0));
        MapCreatorStats.Text = map.IsEmpty
            ? "The grid is empty."
            : $"{cells:N0} cells filled with {map.TilesUsed().Count()} different tiles. The converter simplifies the model if it is over what the game can load.";
    }

    private void MapCreatorResetView_Click(object? sender, RoutedEventArgs e) => MapCreatorViewer.ResetView();

    private void MapCreatorRefreshPreview_Click(object? sender, RoutedEventArgs e) => RefreshCreatorPreview();

    private void RefreshCreatorPreview()
    {
        if (creatorDocument is null) return;
        if (creatorDocument.Map.IsEmpty)
        {
            MapCreatorStatus.Text = "Place some tiles first.";
            return;
        }

        try
        {
            string folder = Path.Combine(creatorDocument.Folder, "preview");
            if (Directory.Exists(folder)) Directory.Delete(folder, true);
            MapCreatorComposer.Output output = MapCreatorComposer.WriteObj(creatorDocument.Tileset, creatorDocument.Map, folder, creatorDocument.Name, compact: false);
            ObjModelConverter.Result model = ObjModelConverter.ToGlb(output.ObjPath, zUp: true, scale: 16f);
            string glb = Path.Combine(folder, "model.glb");
            File.WriteAllBytes(glb, model.Glb);
            MapCreatorViewer.LoadGlb(glb);
            MapCreatorViewer.ResetView();
            MapCreatorViewerPlaceholder.IsVisible = false;
            MapCreatorStatus.Text = $"Preview: {output.Triangles:N0} triangles, {output.Materials} textures (before the converter simplifies it).";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException)
        {
            LogUiError("Map Creator preview failed", ex);
            MapCreatorStatus.Text = ex.Message;
        }
    }

    private async void MapCreatorBuildNew_Click(object? sender, RoutedEventArgs e) => await BuildCreatorMapAsync(null);

    private async void MapCreatorPlaceInMatrix_Click(object? sender, RoutedEventArgs e)
    {
        if (creatorDocument is null || currentProject is null) return;
        if (!creatorDocument.HasBuiltMap(currentProject))
        {
            MapCreatorStatus.Text = "Build this map as a new map first.";
            return;
        }

        MatrixPlacementDialog.Choice? choice = await new MatrixPlacementDialog($"map {creatorDocument.BuiltMapId:D4}", MatrixService.GetIds(currentProject).Count).ShowDialog<MatrixPlacementDialog.Choice?>(this);
        if (choice is null) return;
        try
        {
            creatorDocument.PlaceInMatrix(currentProject, choice.Matrix, choice.Column, choice.Row);
        }
        catch (Exception ex) when (ex is IOException or ArgumentOutOfRangeException or InvalidOperationException)
        {
            MapCreatorStatus.Text = ex.Message;
            return;
        }

        if (currentMatrix?.Id == choice.Matrix) LoadMatrixForEditing(choice.Matrix);
        string header = creatorDocument.BuiltHeaderId >= 0 ? $" and header {creatorDocument.BuiltHeaderId:D4}" : " (no header was made for it, so the cell keeps its old header)";
        MapCreatorStatus.Text = $"Map {creatorDocument.BuiltMapId:D4}{header} placed in matrix {choice.Matrix} at column {choice.Column}, row {choice.Row}.";
    }

    private async void MapCreatorUpdateBuilt_Click(object? sender, RoutedEventArgs e) => await BuildCreatorMapAsync(null, updateBuilt: true);

    private async void MapCreatorBuildInto_Click(object? sender, RoutedEventArgs e)
    {
        if (MapCreatorTargetMap.SelectedIndex < 0 || MapCreatorTargetMap.SelectedIndex >= mapEntries.Count)
        {
            MapCreatorStatus.Text = "Pick the map whose model should be replaced.";
            return;
        }

        await BuildCreatorMapAsync(mapEntries[MapCreatorTargetMap.SelectedIndex].Id);
    }

    private async Task BuildCreatorMapAsync(int? existingMapId, bool updateBuilt = false)
    {
        if (creatorDocument is null || currentProject is null) return;
        if (creatorDocument.Map.IsEmpty)
        {
            MapCreatorStatus.Text = "Place some tiles first.";
            return;
        }

        if (updateBuilt && !creatorDocument.HasBuiltMap(currentProject))
        {
            MapCreatorStatus.Text = "This map has not been built into the project yet (or its map is gone); use Build As New Map.";
            return;
        }

        if (!updateBuilt && existingMapId is null && !await EnsureCanAddMapsAsync()) return;
        string target = updateBuilt ? $"updating map {creatorDocument.BuiltMapId:D4}" : existingMapId is int id ? $"replacing map {id:D4}'s model" : "as a new map";
        ObjToImd.Options? options = await new ObjImportOptionsDialog($"{creatorDocument.Name} ({target})").ShowDialog<ObjToImd.Options?>(this);
        if (options is null) return;

        SaveMapCreator();
        MapModelImportService.Converted converted;
        BeginBlockingLoad("Building the map model...", showImmediately: true);
        try
        {
            converted = await creatorDocument.BuildAsync(currentProject, ToolsDirectory, options);
        }
        catch (Exception ex)
        {
            LogUiError("Map Creator map could not be built", ex);
            MapCreatorStatus.Text = ex.Message;
            return;
        }
        finally
        {
            EndBlockingLoad();
        }

        if (updateBuilt && currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata is not null)
        {
            creatorDocument.UpdateBuiltMap(currentProject, metadata, converted);
            InvalidateCachedMapModels([(ushort)creatorDocument.BuiltMapId]);
            RefreshMapEditorAfterModelChange([creatorDocument.BuiltMapId]);
            ConfigureMapModelEditor();
            RefreshUnsavedMarkers();
            MapCreatorStatus.Text = $"Map {creatorDocument.BuiltMapId:D4} updated ({converted.ModelSize:N0} bytes) with its model and Map Texture Pack [{creatorDocument.BuiltPackId:D2}].";
            return;
        }

        var import = new PendingMapImport(existingMapId, converted);
        mapModelPendingImports.RemoveAll(pending => existingMapId is not null ? pending.MapId == existingMapId : pending.MapId is null && pending.Converted.NsbmdPath == converted.NsbmdPath);
        // A new map touches nothing that exists, so it goes into the project at once; replacing a map's model waits for Save All.
        if (existingMapId is null)
        {
            lastAppliedImport = null;
            ApplyMapModelImport(import);
            if (lastAppliedImport is { } applied) creatorDocument.RememberBuiltMap(applied.MapId, applied.TexturePackId, applied.HeaderId);
        }
        else mapModelPendingImports.Add(import);
        RefreshUnsavedMarkers();

        string reduced = converted.TriangleFraction < 1f ? $" WARNING: the model was too big for the game, so {1f - converted.TriangleFraction:P0} of its triangles were thrown away and it will look broken. Use fewer tiles, stretchable tiles, or a smaller map." : string.Empty;
        string fit = converted.FitsLimit
            ? string.Empty
            : $" The model is {converted.ModelSize:N0} bytes - over {MapFileService.ModelCriticalSize:N0}, which crashes the game; use a smaller texture size or fewer tiles.";
        string added = existingMapId is null ? "and added to the project's map models" : "; it is not in the project yet, Save All adds it";
        if (existingMapId is not null) ShowBuiltMapInMapModelEditor(existingMapId, converted);
        else if (EditorList.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Content as string == "Map Model Editor") is { } editorItem) EditorList.SelectedItem = editorItem;
        MapCreatorStatus.Text = $"\"{creatorDocument.Name}\" built ({converted.ModelSize:N0} bytes) {added}.{reduced}{fit}";
    }

    #endregion

    /// <summary>Opens the Map Model Editor on the model just built, as an OBJ import there would: the replaced map is
    /// selected, or for a new map none is (it only joins the list once Save All adds it to the project).</summary>
    private void ShowBuiltMapInMapModelEditor(int? existingMapId, MapModelImportService.Converted converted)
    {
        ConfigureMapModelEditor();
        mapModelFile = converted.NsbmdPath;
        loadingMapModelEditor = true;
        try
        {
            MapModelList.SelectedIndex = existingMapId is int id ? mapEntries.FindIndex(entry => entry.Id == id) : -1;
            MapModelTextureSelector.SelectedIndex = 0;
        }
        finally
        {
            loadingMapModelEditor = false;
        }

        if (EditorList.Items.OfType<ListBoxItem>().FirstOrDefault(item => item.Content as string == "Map Model Editor") is { } entry) EditorList.SelectedItem = entry;
        _ = LoadMapModelPreviewAsync();
    }
}
