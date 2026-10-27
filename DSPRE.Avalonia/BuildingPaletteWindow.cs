using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DSPRE.Core;
using System.IO;

namespace DSPRE.Avalonia;

/// <summary>A floating grid of the building models usable with a map's building texture pack. Clicking
/// a tile previews that model in 3D, rendered the same way the Building Editor previews one (the
/// export-building bridge command into a MapGlView); double-clicking hands its model ID back to
/// whichever map editor opened the palette. Tiles are names only: rendering a thumbnail of every model
/// meant converting each one through the bridge first, which kept the palette busy for minutes - so the
/// thumbnails are filled in afterwards, in the background, in batches (see FillThumbnailsAsync), and
/// cached as PNGs next to each model's scene.</summary>
public sealed class BuildingPaletteWindow : Window
{
    private const int TileWidth = 180;
    private const int ThumbnailHeight = 110;
    private const int ThumbnailBatchSize = 12;
    private static readonly IBrush TileBrush = new SolidColorBrush(Color.Parse("#151C23"));
    private static readonly IBrush TileBorderBrush = new SolidColorBrush(Color.Parse("#33414D"));
    private static readonly IBrush SelectedBorderBrush = new SolidColorBrush(Color.Parse("#65D8C7"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#AAB7C4"));

    private readonly WrapPanel tiles = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock header = new() { FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap };
    private readonly MapGlView preview = new() { MinHeight = 200 };
    // Has to be laid out and (nearly) visible for GL to draw into it; it sits behind the real content.
    private readonly MapGlView thumbnailRenderer = new() { Width = TileWidth, Height = ThumbnailHeight, Opacity = 0.01, IsHitTestVisible = false, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly Dictionary<int, Image> thumbnailImages = [];
    private readonly TextBlock previewLabel = new() { Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap };
    private readonly Button addButton = new() { Content = "Add to map", Padding = new Thickness(12, 8), IsEnabled = false };

    private RomProject? project;
    private RomGameMetadata? metadata;
    private bool interior;
    private int texturePackId;
    private Action<int>? addModel;
    private Border? selectedTile;
    private int selectedModelId = -1;
    private int generation;

    public BuildingPaletteWindow()
    {
        Title = "Building Palette";
        Width = 900;
        Height = 620;
        MinWidth = 560;
        MinHeight = 360;
        Background = new SolidColorBrush(Color.Parse("#11161C"));
        Foreground = new SolidColorBrush(Color.Parse("#EDF2F7"));

        addButton.Click += (_, _) => AddSelected();

        var side = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto"), RowSpacing = 8 };
        side.Children.Add(new TextBlock { Text = "Preview", FontWeight = FontWeight.SemiBold });
        var previewBorder = new Border { Child = preview, BorderBrush = TileBorderBrush, BorderThickness = new Thickness(1) };
        Grid.SetRow(previewBorder, 1);
        side.Children.Add(previewBorder);
        Grid.SetRow(previewLabel, 2);
        side.Children.Add(previewLabel);
        Grid.SetRow(addButton, 3);
        side.Children.Add(addButton);

        var left = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8 };
        left.Children.Add(header);
        var scroll = new ScrollViewer { Content = tiles };
        Grid.SetRow(scroll, 1);
        left.Children.Add(scroll);
        Grid.SetRow(status, 2);
        left.Children.Add(status);

        var root = new Grid { ColumnDefinitions = new ColumnDefinitions("*,300"), ColumnSpacing = 12, Margin = new Thickness(14) };
        root.Children.Add(left);
        Grid.SetColumn(side, 1);
        root.Children.Add(side);
        var layers = new Grid();
        layers.Children.Add(thumbnailRenderer);
        layers.Children.Add(root);
        Content = layers;

        Closed += (_, _) => generation++;
    }

    /// <summary>Replaces the palette's contents. texturePackId below zero renders each model with
    /// whatever textures it embeds itself, as the Building Editor's "Embedded textures" does.</summary>
    public void SetModels(
        RomProject project,
        RomGameMetadata metadata,
        bool interior,
        int texturePackId,
        IReadOnlyList<(int Id, string Name)> models,
        string description,
        Action<int> addModel)
    {
        this.project = project;
        this.metadata = metadata;
        this.interior = interior;
        this.texturePackId = texturePackId;
        this.addModel = addModel;
        header.Text = description;

        generation++;
        tiles.Children.Clear();
        thumbnailImages.Clear();
        selectedTile = null;
        selectedModelId = -1;
        addButton.IsEnabled = false;
        previewLabel.Text = "Click a model to preview it. Double-click to add it to the centre of the map.";

        foreach ((int id, string name) in models)
        {
            string label = $"[{id}] {(string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name)}";
            var thumbnail = new Image { Width = TileWidth, Height = ThumbnailHeight, Stretch = Stretch.Uniform };
            thumbnailImages[id] = thumbnail;
            var tile = new Border
            {
                Background = TileBrush,
                BorderBrush = TileBorderBrush,
                BorderThickness = new Thickness(2),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(6),
                Margin = new Thickness(0, 0, 8, 8),
                Child = new StackPanel
                {
                    Spacing = 4,
                    Children = { thumbnail, new TextBlock { Text = label, FontSize = 12, Width = TileWidth, TextTrimming = TextTrimming.CharacterEllipsis } }
                }
            };
            ToolTip.SetTip(tile, label);
            tile.PointerPressed += (_, _) => Select(tile, id, label);
            tile.DoubleTapped += (_, _) =>
            {
                Select(tile, id, label);
                AddSelected();
            };
            tiles.Children.Add(tile);
        }

        status.Text = models.Count == 0 ? "No building models are available for this building texture." : $"{models.Count} models available.";
        if (models.Count > 0) _ = FillThumbnailsAsync(generation, models.Select(model => model.Id).ToList());
    }

    private string ThumbnailPath(int modelId) => Path.Combine(GetModelDirectory(modelId), "thumbnail.png");

    /// <summary>Shows cached thumbnails first, then converts the remaining models a batch at a time (one
    /// bridge launch each, since under wine the launch costs more than the conversion) and renders each
    /// through thumbnailRenderer. A newer SetModels call (or closing the window) bumps generation and
    /// ends the run.</summary>
    private async Task FillThumbnailsAsync(int run, List<int> modelIds)
    {
        try
        {
            var missing = new List<int>();
            foreach (int id in modelIds)
            {
                if (File.Exists(ThumbnailPath(id))) ShowThumbnail(id);
                else missing.Add(id);
            }

            for (int start = 0; start < missing.Count; start += ThumbnailBatchSize)
            {
                if (run != generation) return;
                List<int> batch = missing.Skip(start).Take(ThumbnailBatchSize).ToList();
                var pending = batch.Where(id => !File.Exists(Path.Combine(GetModelDirectory(id), "scene.json"))).ToList();
                if (pending.Count > 0)
                {
                    status.Text = $"Preparing thumbnails ({Math.Min(start + batch.Count, missing.Count)}/{missing.Count})...";
                    await new RomExtractionService().ExportBuildingModelsAsync(
                        project!, metadata!, interior, pending, TextureSubfolder, Path.Combine(AppContext.BaseDirectory, "Tools"), texturePackId);
                }

                foreach (int id in batch)
                {
                    if (run != generation) return;
                    string scenePath = Path.Combine(GetModelDirectory(id), "scene.json");
                    if (!File.Exists(scenePath)) continue;
                    await RenderThumbnailAsync(id, scenePath);
                    if (run == generation) ShowThumbnail(id);
                }
            }

            if (run == generation) status.Text = $"{modelIds.Count} models available.";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Building palette thumbnails failed: {ex.Message}");
        }
    }

    private void ShowThumbnail(int modelId)
    {
        if (!thumbnailImages.TryGetValue(modelId, out Image? image)) return;
        try
        {
            image.Source = new Bitmap(ThumbnailPath(modelId));
        }
        catch (Exception ex) when (ex is IOException or ArgumentException)
        {
        }
    }

    private async Task RenderThumbnailAsync(int modelId, string scenePath)
    {
        try
        {
            thumbnailRenderer.LoadScene(scenePath);
            // The first frame after a load can still show the previous scene.
            for (int attempt = 0; attempt < 2; attempt++)
            {
                var done = new TaskCompletionSource<WriteableBitmap?>();
                thumbnailRenderer.CaptureFrame(bitmap => done.TrySetResult(bitmap));
                WriteableBitmap? captured = await Task.WhenAny(done.Task, Task.Delay(1500)) == done.Task ? done.Task.Result : null;
                if (attempt == 1 && captured is not null) captured.Save(ThumbnailPath(modelId));
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Thumbnail for model {modelId} failed: {ex.Message}");
        }
    }

    private void AddSelected()
    {
        if (selectedModelId >= 0) addModel?.Invoke(selectedModelId);
    }

    private void Select(Border tile, int modelId, string label)
    {
        if (selectedTile is not null) selectedTile.BorderBrush = TileBorderBrush;
        selectedTile = tile;
        tile.BorderBrush = SelectedBorderBrush;
        if (selectedModelId == modelId) return;

        selectedModelId = modelId;
        addButton.IsEnabled = true;
        previewLabel.Text = label;
        _ = ShowPreviewAsync(generation, modelId, label);
    }

    private async Task ShowPreviewAsync(int run, int modelId, string label)
    {
        try
        {
            string? scenePath = await EnsureSceneAsync(modelId);
            if (run != generation || selectedModelId != modelId) return;
            if (scenePath is null)
            {
                previewLabel.Text = $"{label} could not be converted.";
                return;
            }

            preview.LoadScene(scenePath);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Building palette preview failed for model {modelId}: {ex.Message}");
            if (run == generation && selectedModelId == modelId) previewLabel.Text = ex.Message;
        }
    }

    private string TextureSubfolder => texturePackId < 0 ? "embedded" : $"tex{texturePackId:D2}";

    private string GetModelDirectory(int modelId) =>
        Path.Combine(BuildingModelService.GetPreviewDirectory(project!, interior, modelId), TextureSubfolder);

    private async Task<string?> EnsureSceneAsync(int modelId)
    {
        if (project is null || metadata is null) return null;
        string directory = GetModelDirectory(modelId);
        string scenePath = Path.Combine(directory, "scene.json");
        if (File.Exists(scenePath)) return scenePath;

        ToolResult result = await new RomExtractionService().ExportBuildingModelAsync(
            project, metadata, interior, modelId, directory, Path.Combine(AppContext.BaseDirectory, "Tools"), texturePackId);
        return result.Succeeded && File.Exists(scenePath) ? scenePath : null;
    }
}
