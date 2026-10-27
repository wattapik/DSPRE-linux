using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DSPRE.Core;
using System.Text.Json;

namespace DSPRE.Avalonia;

/// <summary>A trainer class graphic's animation (see TrainerAnimationService): for each sequence the game
/// can play, its frames - which cell is shown, for how many ticks, and how far it is shifted - with the
/// play mode. Plays a sequence the way the game steps through it. Closes with true when something was saved.</summary>
public sealed class TrainerAnimationWindow : Window
{
    private const int Scale = 2;
    private const int PreviewSize = 128;
    private const int Margin = 40;
    // The game's frame ticks.
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1 / 60.0);
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#AAB7C4"));
    private static readonly (uint Mode, string Label)[] PlayModes =
    [
        (1, "Play once"), (2, "Loop"), (3, "Play backwards once"), (4, "Loop backwards")
    ];

    private readonly RomProject project;
    private readonly int trainerClass;
    private readonly TrainerGraphicSide side;
    private readonly NdsTileImage tiles;
    private readonly RgbColor[][] palettes;
    private readonly NdsCellBank cells;
    private readonly int cellCount;
    private readonly TrainerAnimation animation;
    private readonly Dictionary<int, byte[]?> cellImages = [];
    private string savedSnapshot;
    private bool saved;
    private bool loading;

    private readonly ComboBox sequenceSelector = new() { Width = 380 };
    private readonly ComboBox playMode = new() { Width = 200 };
    private readonly NumericUpDown loopStart = Number(0, 255);
    private readonly StackPanel frameRows = new() { Spacing = 4 };
    private readonly Canvas stage = new() { Width = PreviewSize * Scale + Margin * 2, Height = PreviewSize * Scale + Margin * 2, Background = new SolidColorBrush(Color.Parse("#0D1217")), ClipToBounds = true };
    private readonly Image stageImage = IconPaletteWindow.PixelImage(PreviewSize * Scale, PreviewSize * Scale);
    private readonly TextBlock playStatus = new() { Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox repeatPreview = new() { Content = "Repeat", IsChecked = true };
    private readonly TextBlock status = new() { Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private DispatcherTimer? timer;
    private int playIndex;
    private int ticksLeft;
    private bool backwards;

    public TrainerAnimationWindow(RomProject project, int trainerClass, string className, NdsTileImage tiles, RgbColor[][] palettes, NdsCellBank cells, int cellCount, TrainerGraphicSide side = TrainerGraphicSide.Front)
    {
        this.project = project;
        this.side = side;
        this.trainerClass = trainerClass;
        this.tiles = tiles;
        this.palettes = palettes;
        this.cells = cells;
        this.cellCount = Math.Max(cellCount, 1);
        animation = TrainerAnimationService.Load(project, trainerClass, side);
        savedSnapshot = Snapshot();

        Title = $"Trainer Animation - {className}";
        Width = 1280;
        Height = 780;
        MinWidth = 900;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#11161C"));
        Foreground = new SolidColorBrush(Color.Parse("#EDF2F7"));
        Closed += (_, _) => timer?.Stop();

        stage.Children.Add(stageImage);
        ShowCell(0, 0, 0);

        for (int i = 0; i < animation.Sequences.Count; i++) sequenceSelector.Items.Add(SequenceLabel(i));
        foreach ((_, string label) in PlayModes) playMode.Items.Add(label);
        sequenceSelector.SelectionChanged += (_, _) => { if (!loading) ShowSequence(); };
        playMode.SelectionChanged += (_, _) =>
        {
            if (loading || playMode.SelectedIndex < 0) return;
            Sequence.PlayMode = PlayModes[playMode.SelectedIndex].Mode;
            Changed();
        };
        loopStart.ValueChanged += (_, _) =>
        {
            if (loading) return;
            Sequence.LoopStart = (ushort)(loopStart.Value ?? 0);
            Changed();
        };

        var play = new Button { Content = "Play", Padding = new Thickness(16, 6) };
        play.Click += (_, _) => StartPreview();
        var stop = new Button { Content = "Stop", Padding = new Thickness(16, 6) };
        stop.Click += (_, _) => StopPreview();
        var preview = IconPaletteWindow.Panel(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                stage,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { play, stop, repeatPreview } },
                playStatus,
                new TextBlock
                {
                    Text = "A tick is 1/60 of a second. Cells are the pictures in the Trainer Graphic Editor's frame sheet; a shift moves the picture on screen.",
                    Foreground = MutedBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = PreviewSize * Scale + Margin * 2
                }
            }
        });
        preview.VerticalAlignment = VerticalAlignment.Top;

        var add = new Button { Content = "Add Frame", Padding = new Thickness(12, 6) };
        add.Click += (_, _) => AddFrame();
        var editor = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Labeled("Sequence", sequenceSelector), Labeled("Plays", playMode), Labeled("Loop starts at frame", loopStart) } },
                IconPaletteWindow.Panel(new StackPanel { Spacing = 8, Children = { frameRows, add } })
            }
        };

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16 };
        body.Children.Add(preview);
        Grid.SetColumn(editor, 1);
        body.Children.Add(editor);

        var save = new Button { Content = "Save", Padding = new Thickness(20, 8) };
        save.Click += (_, _) => Save();
        var revert = new Button { Content = "Revert", Padding = new Thickness(20, 8) };
        revert.Click += (_, _) => Revert();
        var close = new Button { Content = "Close", Padding = new Thickness(20, 8) };
        close.Click += (_, _) => Close(saved);
        Content = IconPaletteWindow.WindowLayout([body], status, revert, save, close);

        sequenceSelector.SelectedIndex = 0;
        if (animation.Sequences.Count == 0) status.Text = "This class has no animation sequences.";
    }

    private TrainerAnimationSequence Sequence => animation.Sequences[Math.Max(sequenceSelector.SelectedIndex, 0)];

    private string SequenceLabel(int index)
    {
        TrainerAnimationSequence sequence = animation.Sequences[index];
        string role = index == 0 ? "the still when it appears" : sequence.Frames.Count > 1 ? "an animation" : "a still";
        return $"Sequence {index}: {sequence.Frames.Count} frame{(sequence.Frames.Count == 1 ? "" : "s")}, {role}";
    }

    private static NumericUpDown Number(int min, int max) => new() { Minimum = min, Maximum = max, Increment = 1, FormatString = "0", Width = 108 };

    private static Control Labeled(string label, Control control) =>
        new StackPanel { Spacing = 2, Children = { new TextBlock { Text = label, Foreground = MutedBrush, FontSize = 11 }, control } };

    private string Snapshot() => JsonSerializer.Serialize(animation);

    private void Changed()
    {
        status.Text = Snapshot() == savedSnapshot ? string.Empty : "Unsaved changes.";
        int index = sequenceSelector.SelectedIndex;
        if (index < 0 || index >= sequenceSelector.Items.Count) return;
        // Replacing the selected item clears the selection, so put it back without reloading the sequence.
        bool wasLoading = loading;
        loading = true;
        sequenceSelector.Items[index] = SequenceLabel(index);
        sequenceSelector.SelectedIndex = index;
        loading = wasLoading;
    }

    private void ShowSequence()
    {
        StopPreview();
        TrainerAnimationSequence sequence = Sequence;
        loading = true;
        playMode.SelectedIndex = Math.Max(Array.FindIndex(PlayModes, mode => mode.Mode == sequence.PlayMode), 0);
        loopStart.Value = sequence.LoopStart;
        loading = false;
        RebuildRows();
    }

    private void RebuildRows()
    {
        frameRows.Children.Clear();
        TrainerAnimationSequence sequence = Sequence;
        var header = new Grid { ColumnDefinitions = FrameColumns(sequence) , ColumnSpacing = 8 };
        string[] labels = sequence.HasShift ? ["Frame", "Cell", "Ticks", "X shift", "Y shift", ""] : ["Frame", "Cell", "Ticks", ""];
        for (int i = 0; i < labels.Length; i++)
        {
            var label = new TextBlock { Text = labels[i], Foreground = MutedBrush, FontSize = 11 };
            Grid.SetColumn(label, i);
            header.Children.Add(label);
        }

        frameRows.Children.Add(header);
        for (int i = 0; i < sequence.Frames.Count; i++) frameRows.Children.Add(BuildRow(sequence, i));
        if (!sequence.HasShift)
        {
            frameRows.Children.Add(new TextBlock { Text = "This sequence's frames have no shift.", Foreground = MutedBrush, FontSize = 11 });
        }
    }

    private static ColumnDefinitions FrameColumns(TrainerAnimationSequence sequence) =>
        new(sequence.HasShift ? "40,112,112,112,112,Auto" : "40,112,112,Auto");

    private Control BuildRow(TrainerAnimationSequence sequence, int index)
    {
        TrainerAnimationFrame frame = sequence.Frames[index];
        var row = new Grid { ColumnDefinitions = FrameColumns(sequence), ColumnSpacing = 8 };
        void Put(Control control, int column)
        {
            Grid.SetColumn(control, column);
            row.Children.Add(control);
        }

        Put(new TextBlock { Text = index.ToString(), VerticalAlignment = VerticalAlignment.Center }, 0);
        NumericUpDown cell = Number(0, cellCount - 1);
        cell.Value = Math.Min(frame.Cell, cellCount - 1);
        cell.ValueChanged += (_, _) => Edited(() => frame.Cell = (ushort)(cell.Value ?? 0), index);
        Put(cell, 1);
        NumericUpDown ticks = Number(1, ushort.MaxValue);
        ticks.Value = frame.Duration;
        ticks.ValueChanged += (_, _) => Edited(() => frame.Duration = (ushort)(ticks.Value ?? 1), index);
        Put(ticks, 2);
        int actionsColumn = 3;
        if (sequence.HasShift)
        {
            NumericUpDown x = Number(short.MinValue, short.MaxValue), y = Number(short.MinValue, short.MaxValue);
            x.Value = frame.XShift;
            y.Value = frame.YShift;
            x.ValueChanged += (_, _) => Edited(() => frame.XShift = (short)(x.Value ?? 0), index);
            y.ValueChanged += (_, _) => Edited(() => frame.YShift = (short)(y.Value ?? 0), index);
            Put(x, 3);
            Put(y, 4);
            actionsColumn = 5;
        }

        var actions = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
        actions.Children.Add(RowButton("Up", "Move up", () => Move(index, -1), index > 0));
        actions.Children.Add(RowButton("Down", "Move down", () => Move(index, 1), index < sequence.Frames.Count - 1));
        actions.Children.Add(RowButton("Copy", "Duplicate this frame", () => Duplicate(index), true));
        actions.Children.Add(RowButton("Delete", "Delete this frame", () => Delete(index), sequence.Frames.Count > 1));
        Put(actions, actionsColumn);
        return row;
    }

    private static Button RowButton(string text, string tip, Action click, bool enabled)
    {
        var button = new Button { Content = text, Padding = new Thickness(8, 3), FontSize = 11, IsEnabled = enabled };
        ToolTip.SetTip(button, tip);
        button.Click += (_, _) => click();
        return button;
    }

    private void Edited(Action edit, int index)
    {
        if (loading) return;
        edit();
        Changed();
        TrainerAnimationFrame frame = Sequence.Frames[index];
        ShowCell(frame.Cell, frame.XShift, frame.YShift);
    }

    private void Move(int index, int by)
    {
        List<TrainerAnimationFrame> frames = Sequence.Frames;
        (frames[index], frames[index + by]) = (frames[index + by], frames[index]);
        Changed();
        RebuildRows();
    }

    private void Duplicate(int index)
    {
        TrainerAnimationFrame frame = Sequence.Frames[index];
        Sequence.Frames.Insert(index + 1, new TrainerAnimationFrame { Cell = frame.Cell, Duration = frame.Duration, XShift = frame.XShift, YShift = frame.YShift, Attribute = frame.Attribute });
        Changed();
        RebuildRows();
    }

    private void Delete(int index)
    {
        Sequence.Frames.RemoveAt(index);
        Sequence.LoopStart = (ushort)Math.Min(Sequence.LoopStart, Sequence.Frames.Count - 1);
        loading = true;
        loopStart.Value = Sequence.LoopStart;
        loading = false;
        Changed();
        RebuildRows();
    }

    private void AddFrame()
    {
        TrainerAnimationSequence sequence = Sequence;
        TrainerAnimationFrame last = sequence.Frames[^1];
        sequence.Frames.Add(new TrainerAnimationFrame { Cell = last.Cell, Duration = last.Duration, XShift = last.XShift, YShift = last.YShift });
        Changed();
        RebuildRows();
    }

    private void ShowCell(int cell, int xShift, int yShift)
    {
        if (!cellImages.TryGetValue(cell, out byte[]? rgba))
        {
            cellImages[cell] = rgba = SpriteService.GetTrainerClassSpriteRgba(tiles, palettes, cells, Math.Clamp(cell, 0, cellCount - 1), PreviewSize, PreviewSize);
        }

        stageImage.Source = MainWindow.CreateBitmapFromRgba(rgba, PreviewSize, PreviewSize);
        Canvas.SetLeft(stageImage, Margin + xShift * Scale);
        Canvas.SetTop(stageImage, Margin + yShift * Scale);
    }

    private void StartPreview()
    {
        StopPreview();
        TrainerAnimationSequence sequence = Sequence;
        if (sequence.Frames.Count == 0) return;
        backwards = sequence.PlayMode is 3 or 4;
        playIndex = backwards ? sequence.Frames.Count - 1 : 0;
        ShowPlayed();
        ticksLeft = Sequence.Frames[playIndex].Duration;
        timer = new DispatcherTimer { Interval = Tick };
        timer.Tick += (_, _) => Advance();
        timer.Start();
    }

    private void StopPreview()
    {
        timer?.Stop();
        timer = null;
        playStatus.Text = string.Empty;
        ShowCell(0, 0, 0);
    }

    private void ShowPlayed()
    {
        TrainerAnimationFrame frame = Sequence.Frames[playIndex];
        ShowCell(frame.Cell, frame.XShift, frame.YShift);
        playStatus.Text = $"Frame {playIndex}: cell {frame.Cell}, {frame.Duration} ticks";
    }

    private void Advance()
    {
        if (--ticksLeft > 0) return;
        TrainerAnimationSequence sequence = Sequence;
        bool loops = sequence.PlayMode is 2 or 4;
        int next = backwards ? playIndex - 1 : playIndex + 1;
        if (next < 0 || next >= sequence.Frames.Count)
        {
            if (!loops && repeatPreview.IsChecked != true)
            {
                timer?.Stop();
                return;
            }

            next = loops ? Math.Clamp(sequence.LoopStart, 0, sequence.Frames.Count - 1) : backwards ? sequence.Frames.Count - 1 : 0;
            if (backwards && loops) next = sequence.Frames.Count - 1;
        }

        playIndex = next;
        ShowPlayed();
        ticksLeft = sequence.Frames[playIndex].Duration;
    }

    private void Revert()
    {
        StopPreview();
        TrainerAnimation original = TrainerAnimationService.Load(project, trainerClass, side);
        animation.Sequences = original.Sequences;
        int selected = Math.Max(sequenceSelector.SelectedIndex, 0);
        loading = true;
        for (int i = 0; i < sequenceSelector.Items.Count; i++) sequenceSelector.Items[i] = SequenceLabel(i);
        sequenceSelector.SelectedIndex = selected;
        loading = false;
        savedSnapshot = Snapshot();
        status.Text = string.Empty;
        ShowSequence();
    }

    private void Save()
    {
        try
        {
            if (Snapshot() == savedSnapshot)
            {
                status.Text = "Nothing to save.";
                return;
            }

            TrainerAnimationService.Save(project, trainerClass, animation, side);
            savedSnapshot = Snapshot();
            saved = true;
            status.Text = "Saved. It reaches the ROM with Save ROM.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            status.Text = $"Not saved: {ex.Message}";
        }
    }
}
