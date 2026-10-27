using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Trainer Graphic Editor: trainer class front sprites, and the back sprites of the player and partners (Platinum), as a sheet of frames - see
/// TrainerGraphicService.</summary>
public partial class MainWindow
{
    private const int TrainerGraphicPreviewWidth = 128;
    // Tall enough for the animated classes whose frames move up and down.
    private const int TrainerGraphicPreviewHeight = 128;

    private TrainerGraphicSide trainerGraphicSide = TrainerGraphicSide.Front;
    private string[] trainerGraphicClassNames = [];
    private int trainerGraphicClass = -1;
    private NdsTileImage? trainerGraphicTiles;
    private RgbColor[][]? trainerGraphicPalettes;
    private NdsCellBank? trainerGraphicCells;
    private int trainerGraphicCellCount;
    private int trainerGraphicCell;
    private DispatcherTimer? trainerGraphicTimer;
    private TrainerAnimation? trainerGraphicAnimation;
    private readonly Dictionary<int, global::Avalonia.Media.Imaging.WriteableBitmap?> trainerGraphicCellBitmaps = [];
    private int trainerGraphicSequence;
    private int trainerGraphicFrame;
    private int trainerGraphicTicksLeft;

    private void ConfigureTrainerGraphicEditor()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        TrainerGraphicSide side = trainerGraphicSide;
        TrainerGraphicListTitle.Text = side == TrainerGraphicSide.Back ? "Back Sprites" : "Trainer Classes";
        if (!TrainerGraphicService.IsSupported(metadata, side))
        {
            TrainerGraphicClassList.ItemsSource = null;
            TrainerGraphicStatus.Text = side == TrainerGraphicSide.Back
                ? "Back sprites are only supported for Platinum."
                : "Diamond/Pearl trainer graphics are not supported: they have no cell files describing how the frames are laid out.";
            return;
        }

        try
        {
            if (!Directory.Exists(currentProject.TrainerGraphicsDirectoryFor(side))) new RomExtractionService().PrepareSprites(currentProject, metadata);
            string[] names = side == TrainerGraphicSide.Back ? [] : TextArchiveService.Load(currentProject, TrainerService.GetTrainerClassNamesArchiveId(metadata), metadata).Messages.ToArray();
            int count = TrainerGraphicService.ClassCount(currentProject, side);
            trainerGraphicClassNames = Enumerable.Range(0, count).Select(i => side == TrainerGraphicSide.Back ? $"Back {i:D2}" : $"{i:D3} {(i < names.Length ? names[i] : "")}").ToArray();
            int selected = trainerGraphicClass >= 0 && trainerGraphicClass < count ? trainerGraphicClass : 0;
            TrainerGraphicClassList.ItemsSource = trainerGraphicClassNames;
            TrainerGraphicClassList.SelectedIndex = selected;
            LoadTrainerGraphic(selected);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            LogUiError("Trainer graphics could not be loaded", ex);
            TrainerGraphicStatus.Text = ex.Message;
        }
    }

    private void TrainerGraphicSide_Checked(object? sender, RoutedEventArgs e)
    {
        // The radio that was just checked decides: its group mate may not have been unchecked yet.
        if (sender is not RadioButton { IsChecked: true } radio) return;
        TrainerGraphicSide side = radio == TrainerGraphicBackRadio ? TrainerGraphicSide.Back : TrainerGraphicSide.Front;
        if (side == trainerGraphicSide) return;
        StopTrainerGraphicAnimation();
        trainerGraphicSide = side;
        trainerGraphicClass = -1;
        (trainerGraphicTiles, trainerGraphicPalettes, trainerGraphicCells) = (null, null, null);
        TrainerGraphicPreview.Source = null;
        TrainerGraphicSheet.Source = null;
        ConfigureTrainerGraphicEditor();
    }

    private void TrainerGraphicClassList_SelectionChanged(object? sender, global::Avalonia.Controls.SelectionChangedEventArgs e)
    {
        if (TrainerGraphicClassList.SelectedIndex >= 0 && TrainerGraphicClassList.SelectedIndex != trainerGraphicClass) LoadTrainerGraphic(TrainerGraphicClassList.SelectedIndex);
    }

    private void LoadTrainerGraphic(int trainerClass)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        StopTrainerGraphicAnimation();
        trainerGraphicClass = trainerClass;
        trainerGraphicCell = 0;
        trainerGraphicCellCount = 0;
        (trainerGraphicTiles, trainerGraphicPalettes, trainerGraphicCells) = (null, null, null);
        if (SpriteService.TryLoadTrainerClassSprite(currentProject, metadata, trainerClass, out NdsTileImage? tiles, out RgbColor[][]? palettes, out NdsCellBank? cells, out int cellCount, trainerGraphicSide))
        {
            (trainerGraphicTiles, trainerGraphicPalettes, trainerGraphicCells, trainerGraphicCellCount) = (tiles, palettes, cells, cellCount);
        }

        LoadTrainerGraphicAnimation(trainerClass);
        RenderTrainerGraphicPreview();
        if (TrainerGraphicAnimateCheck.IsChecked == true) StartTrainerGraphicAnimation();

        string name = trainerClass < trainerGraphicClassNames.Length ? trainerGraphicClassNames[trainerClass] : $"{trainerClass:D3}";
        try
        {
            int frames = TrainerGraphicService.FrameCount(currentProject, trainerClass, trainerGraphicSide);
            TrainerGraphicSheet.Source = CreateBitmapFromRgba(TrainerGraphicService.RenderSheet(currentProject, trainerClass, trainerGraphicSide), TrainerGraphicService.FrameSize, TrainerGraphicService.FrameSize * frames);
            TrainerGraphicSheet.Width = TrainerGraphicService.FrameSize * 2;
            TrainerGraphicSheet.Height = TrainerGraphicService.FrameSize * 2 * frames;
            TrainerGraphicSheetTitle.Text = frames == 1 ? "Frame (80x80)" : $"{frames} frames (80x{80 * frames})";
            TrainerGraphicStatus.Text = $"{name}: {frames} frame{(frames == 1 ? "" : "s")}, {trainerGraphicCellCount} animation cell{(trainerGraphicCellCount == 1 ? "" : "s")}.";
        }
        catch (InvalidDataException ex)
        {
            TrainerGraphicSheet.Source = null;
            TrainerGraphicSheetTitle.Text = "Frames";
            TrainerGraphicStatus.Text = $"{name}: {ex.Message}";
        }
    }

    /// <summary>The class's own frame sequences, which the in-battle preview plays; null when the file is not
    /// one this app reads, which falls back to stepping through the cells.</summary>
    private void LoadTrainerGraphicAnimation(int trainerClass)
    {
        trainerGraphicCellBitmaps.Clear();
        trainerGraphicAnimation = null;
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null || !TrainerAnimationService.IsSupported(metadata, trainerGraphicSide)) return;
        try
        {
            trainerGraphicAnimation = TrainerAnimationService.Load(currentProject, trainerClass, trainerGraphicSide);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            LogUiError("Trainer animation could not be read", ex);
        }
    }

    private void RenderTrainerGraphicPreview(int xShift = 0, int yShift = 0)
    {
        if (trainerGraphicTiles is null || trainerGraphicPalettes is null || trainerGraphicCells is null || trainerGraphicCellCount == 0)
        {
            TrainerGraphicPreview.Source = null;
            return;
        }

        int cell = Math.Clamp(trainerGraphicCell, 0, trainerGraphicCellCount - 1);
        if (!trainerGraphicCellBitmaps.TryGetValue(cell, out var bitmap))
        {
            trainerGraphicCellBitmaps[cell] = bitmap = CreateBitmapFromRgba(
                SpriteService.GetTrainerClassSpriteRgba(trainerGraphicTiles, trainerGraphicPalettes, trainerGraphicCells, cell, TrainerGraphicPreviewWidth, TrainerGraphicPreviewHeight),
                TrainerGraphicPreviewWidth, TrainerGraphicPreviewHeight);
        }

        TrainerGraphicPreview.Source = bitmap;
        // The preview is drawn at twice its size.
        TrainerGraphicPreview.RenderTransform = xShift == 0 && yShift == 0 ? null : new TranslateTransform(xShift * 2, yShift * 2);
    }

    /// <summary>Plays the way the game does: the first sequence (the still shown as the trainer appears), then
    /// the second (its animation) over and over, at the sequences' own timing and shifts.</summary>
    private void StartTrainerGraphicAnimation()
    {
        StopTrainerGraphicAnimation();
        if (trainerGraphicAnimation is { Sequences.Count: > 0 } animation)
        {
            trainerGraphicSequence = 0;
            trainerGraphicFrame = 0;
            ShowTrainerGraphicFrame(animation);
            trainerGraphicTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1 / 60.0) };
            trainerGraphicTimer.Tick += (_, _) => AdvanceTrainerGraphicAnimation(animation);
            trainerGraphicTimer.Start();
            return;
        }

        if (trainerGraphicCellCount <= 1) return;
        trainerGraphicTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        trainerGraphicTimer.Tick += (_, _) =>
        {
            trainerGraphicCell = (trainerGraphicCell + 1) % trainerGraphicCellCount;
            RenderTrainerGraphicPreview();
        };
        trainerGraphicTimer.Start();
    }

    private void ShowTrainerGraphicFrame(TrainerAnimation animation)
    {
        TrainerAnimationFrame frame = animation.Sequences[trainerGraphicSequence].Frames[trainerGraphicFrame];
        trainerGraphicCell = frame.Cell;
        trainerGraphicTicksLeft = Math.Max((int)frame.Duration, 1);
        RenderTrainerGraphicPreview(frame.XShift, frame.YShift);
    }

    private void AdvanceTrainerGraphicAnimation(TrainerAnimation animation)
    {
        if (--trainerGraphicTicksLeft > 0) return;
        TrainerAnimationSequence sequence = animation.Sequences[trainerGraphicSequence];
        bool backwards = sequence.PlayMode is 3 or 4;
        int next = backwards ? trainerGraphicFrame - 1 : trainerGraphicFrame + 1;
        if (next < 0 || next >= sequence.Frames.Count)
        {
            // After the still comes the animation, which then repeats (from its loop point if it has one).
            if (trainerGraphicSequence == 0 && animation.Sequences.Count > 1) trainerGraphicSequence = 1;
            sequence = animation.Sequences[trainerGraphicSequence];
            backwards = sequence.PlayMode is 3 or 4;
            bool loops = sequence.PlayMode is 2 or 4;
            next = backwards ? sequence.Frames.Count - 1 : loops ? Math.Clamp((int)sequence.LoopStart, 0, sequence.Frames.Count - 1) : 0;
        }

        trainerGraphicFrame = next;
        ShowTrainerGraphicFrame(animation);
    }

    private void StopTrainerGraphicAnimation()
    {
        trainerGraphicTimer?.Stop();
        trainerGraphicTimer = null;
    }

    private void TrainerGraphicAnimateCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (TrainerGraphicAnimateCheck.IsChecked == true)
        {
            StartTrainerGraphicAnimation();
            return;
        }

        StopTrainerGraphicAnimation();
        trainerGraphicCell = 0;
        RenderTrainerGraphicPreview();
    }

    private async void ExportTrainerGraphic_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || trainerGraphicClass < 0) return;
        string name = trainerGraphicClass < trainerGraphicClassNames.Length ? trainerGraphicClassNames[trainerGraphicClass] : $"{trainerGraphicClass:D3}";
        try
        {
            int frames = TrainerGraphicService.FrameCount(currentProject, trainerGraphicClass, trainerGraphicSide);
            byte[] sheet = TrainerGraphicService.RenderSheet(currentProject, trainerGraphicClass, trainerGraphicSide);
            if (await PickSavePathAsync("Export trainer graphic", $"{string.Concat(name.Split(Path.GetInvalidFileNameChars()))}.png", "PNG image", "*.png") is not string path) return;
            using var bitmap = CreateBitmapFromRgba(sheet, TrainerGraphicService.FrameSize, TrainerGraphicService.FrameSize * frames);
            bitmap?.Save(path);
            TrainerGraphicStatus.Text = $"Exported {frames} frame{(frames == 1 ? "" : "s")} to {path}.";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            TrainerGraphicStatus.Text = $"The graphic could not be exported: {ex.Message}";
        }
    }

    private async void ImportTrainerGraphic_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || trainerGraphicClass < 0 || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        if (await PickFileAsync("Trainer graphic sheet: 80 wide, frames stacked", "PNG image", "*.png") is not string path) return;
        int trainerClass = trainerGraphicClass;
        try
        {
            TrainerGraphicService.ImportResult result = TrainerGraphicService.Import(currentProject, metadata, trainerClass, path, trainerGraphicSide);
            LoadTrainerGraphic(trainerClass);
            TrainerGraphicStatus.Text = $"Imported {result.Frames} frame{(result.Frames == 1 ? "" : "s")} with {result.Colors} color{(result.Colors == 1 ? "" : "s")}." +
                (result.SourceColors > result.Colors ? $" The image had {result.SourceColors}, reduced to {result.Colors}: a trainer graphic has 15 colors plus transparency." : "") +
                (result.ScanUpdated ? "" : " The scan copy (Hall of Fame) was not in the expected format and was left as it was.") +
                " It reaches the ROM with Save ROM.";
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
        {
            TrainerGraphicStatus.Text = $"The graphic could not be imported: {ex.Message}";
        }
    }

    private async void EditTrainerAnimation_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || trainerGraphicClass < 0 || trainerGraphicTiles is null || trainerGraphicPalettes is null || trainerGraphicCells is null) return;
        string name = trainerGraphicClass < trainerGraphicClassNames.Length ? trainerGraphicClassNames[trainerGraphicClass] : $"{trainerGraphicClass:D3}";
        try
        {
            StopTrainerGraphicAnimation();
            var window = new TrainerAnimationWindow(currentProject, trainerGraphicClass, name, trainerGraphicTiles, trainerGraphicPalettes, trainerGraphicCells, trainerGraphicCellCount, trainerGraphicSide);
            if (await window.ShowDialog<bool>(this)) TrainerGraphicStatus.Text = $"Saved the animation of {name}. It reaches the ROM with Save ROM.";
            LoadTrainerGraphicAnimation(trainerGraphicClass);
            trainerGraphicCell = 0;
            RenderTrainerGraphicPreview();
            if (TrainerGraphicAnimateCheck.IsChecked == true) StartTrainerGraphicAnimation();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            TrainerGraphicStatus.Text = $"The animation could not be read: {ex.Message}";
        }
    }
}
