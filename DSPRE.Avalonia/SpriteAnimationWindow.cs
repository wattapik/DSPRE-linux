using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>A Pokémon's battle sprite animation (see SpriteAnimationService): for its front and back, the
/// sequence of the sheet's two frames with timing and shifts, the motion routine and delays, and where the
/// sprite and its shadow sit. Plays the sequence the way the game steps through it. Closes with true when
/// something was saved.</summary>
public sealed class SpriteAnimationWindow : Window
{
    private const int Scale = 2;
    private const int FrameSize = 80;
    private const int Stage = 200;
    // Each step lasts Duration + 1 of the game's frame ticks.
    private static readonly TimeSpan Tick = TimeSpan.FromSeconds(1 / 60.0);
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#AAB7C4"));

    private readonly RomProject project;
    private readonly RomGameMetadata metadata;
    private readonly int species;
    private readonly byte[]?[] sprites;
    private readonly RgbColor[]? palette;
    private readonly PokemonSpriteAnimation animation;
    private string savedSnapshot;
    private bool saved;
    private bool loading;

    private readonly RadioButton frontButton = new() { Content = "Front (opponent's side)", GroupName = "AnimFace", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
    private readonly RadioButton backButton = new() { Content = "Back (player's side)", GroupName = "AnimFace", Margin = new Thickness(0, 0, 32, 0) };
    private readonly RadioButton maleButton = new() { Content = "Male sprite", GroupName = "AnimGender", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
    private readonly RadioButton femaleButton = new() { Content = "Female sprite", GroupName = "AnimGender" };
    private readonly Canvas stage = new() { Width = Stage * Scale, Height = Stage * Scale, Background = new SolidColorBrush(Color.Parse("#0D1217")), ClipToBounds = true };
    private readonly Image stageImage = IconPaletteWindow.PixelImage(FrameSize * Scale, FrameSize * Scale);
    private readonly Image frame1 = IconPaletteWindow.PixelImage(FrameSize, FrameSize);
    private readonly Image frame2 = IconPaletteWindow.PixelImage(FrameSize, FrameSize);
    private readonly TextBlock playStatus = new() { Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap };
    private readonly CheckBox loopPreview = new() { Content = "Repeat", IsChecked = true };
    private readonly NumericUpDown cryDelay = Number(0, 255), motion = Number(0, 255), startDelay = Number(0, 255);
    private readonly NumericUpDown heightFemale = Number(0, 255), heightMale = Number(0, 255), frontYOffset = Number(-128, 127), shadowX = Number(-128, 127), shadowSize = Number(0, 255);
    private readonly (ComboBox Action, NumericUpDown Duration, NumericUpDown X, NumericUpDown Y)[] rows = new (ComboBox, NumericUpDown, NumericUpDown, NumericUpDown)[SpriteAnimationService.StepCount];
    private readonly TextBlock status = new() { Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private DispatcherTimer? timer;
    private List<SpriteAnimationService.PlayedFrame> sequence = [];
    private int playIndex;
    private int ticksLeft;

    public SpriteAnimationWindow(RomProject project, RomGameMetadata metadata, int species, string speciesName, byte[]?[] sprites, RgbColor[]? palette)
    {
        this.project = project;
        this.metadata = metadata;
        this.species = species;
        this.sprites = sprites;
        this.palette = palette;
        animation = SpriteAnimationService.Load(project, metadata, species);
        savedSnapshot = Snapshot();

        Title = $"Battle Sprite Animation - {speciesName}";
        Width = 1280;
        Height = 860;
        MinWidth = 960;
        MinHeight = 620;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#11161C"));
        Foreground = new SolidColorBrush(Color.Parse("#EDF2F7"));

        stage.Children.Add(stageImage);
        foreach (RadioButton button in new[] { frontButton, maleButton }) button.IsCheckedChanged += (_, _) => ShowFace();
        Closed += (_, _) => timer?.Stop();

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
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { play, stop, loopPreview } },
                playStatus,
                new TextBlock { Text = "The sheet's two frames:", Foreground = MutedBrush, FontSize = 12 },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { Labeled("Frame 1", frame1), Labeled("Frame 2", frame2) } },
                new TextBlock
                {
                    Text = "The motion routine (a bounce, stretch...) plays on top in the game and is not previewed here.",
                    Foreground = MutedBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap, MaxWidth = Stage * Scale
                }
            }
        });
        preview.VerticalAlignment = VerticalAlignment.Top;

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16 };
        body.Children.Add(preview);
        Control editor = BuildEditor();
        Grid.SetColumn(editor, 1);
        body.Children.Add(editor);

        var saveButton = new Button { Content = "Save", Padding = new Thickness(20, 8) };
        saveButton.Click += (_, _) => Save();
        var revert = new Button { Content = "Revert", Padding = new Thickness(20, 8) };
        revert.Click += (_, _) =>
        {
            PokemonSpriteAnimation original = SpriteAnimationService.Load(project, metadata, species);
            animation.Front = original.Front;
            animation.Back = original.Back;
            animation.FrontYOffset = original.FrontYOffset;
            animation.ShadowXOffset = original.ShadowXOffset;
            animation.ShadowSize = original.ShadowSize;
            animation.Heights = original.Heights;
            ShowFace();
        };
        var close = new Button { Content = "Close", Padding = new Thickness(20, 8) };
        close.Click += (_, _) => Close(saved);

        Content = IconPaletteWindow.WindowLayout(
            [
                new StackPanel { Orientation = Orientation.Horizontal, Children = { frontButton, backButton, maleButton, femaleButton } },
                body
            ],
            status, revert, saveButton, close);

        ShowFace();
    }

    // Both read the button whose change is handled: while a group switches, the other still reports
    // its old state.
    private bool Front => frontButton.IsChecked == true;

    private bool Male => maleButton.IsChecked == true;

    private SpriteFaceAnimation Face => Front ? animation.Front : animation.Back;

    private static NumericUpDown Number(int min, int max) => new() { Minimum = min, Maximum = max, Increment = 1, FormatString = "0", Width = 120 };

    private static Control Labeled(string label, Control control) =>
        new StackPanel { Spacing = 2, Children = { new TextBlock { Text = label, Foreground = MutedBrush, FontSize = 11 }, control } };

    private Control BuildEditor()
    {
        var steps = new Grid { ColumnDefinitions = new ColumnDefinitions("40,220,110,110,110"), ColumnSpacing = 8, RowSpacing = 4 };
        string[] headers = ["Step", "Shows", "Ticks / repeats", "X shift", "Y shift"];
        steps.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
        for (int c = 0; c < headers.Length; c++) steps.Children.Add(Cell(new TextBlock { Text = headers[c], Foreground = MutedBrush, FontSize = 12 }, 0, c));
        for (int i = 0; i < rows.Length; i++)
        {
            int index = i;
            steps.RowDefinitions.Add(new RowDefinition(GridLength.Auto));
            var action = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
            NumericUpDown duration = Number(0, 255), x = Number(-128, 127), y = Number(-128, 127);
            ToolTip.SetTip(duration, "For a frame: how long it shows, in ticks minus one. For a repeat: how many times to go back.");
            action.SelectionChanged += (_, _) => StepChanged(index);
            foreach (NumericUpDown input in new[] { duration, x, y }) input.ValueChanged += (_, _) => StepChanged(index);
            rows[i] = (action, duration, x, y);
            steps.Children.Add(Cell(new TextBlock { Text = (i + 1).ToString(), VerticalAlignment = VerticalAlignment.Center }, i + 1, 0));
            steps.Children.Add(Cell(action, i + 1, 1));
            steps.Children.Add(Cell(duration, i + 1, 2));
            steps.Children.Add(Cell(x, i + 1, 3));
            steps.Children.Add(Cell(y, i + 1, 4));
        }

        foreach (NumericUpDown input in new[] { cryDelay, motion, startDelay, heightFemale, heightMale, frontYOffset, shadowX, shadowSize })
        {
            input.ValueChanged += (_, _) => SettingsChanged();
        }

        ToolTip.SetTip(motion, "Which built-in motion routine plays over the frames. The routines are game code, so only the number can be chosen.");
        return new StackPanel
        {
            Spacing = 12,
            Children =
            {
                IconPaletteWindow.Panel(new StackPanel
                {
                    Spacing = 8,
                    Children =
                    {
                        new TextBlock { Text = "Frame sequence", FontWeight = FontWeight.SemiBold },
                        new TextBlock
                        {
                            Text = "Each step shows one of the sheet's two frames for its ticks plus one (60 ticks a second), shifted by X/Y. A repeat step goes back to an earlier step the given number of times; End or step 10 stops the sequence.",
                            Foreground = MutedBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap
                        },
                        steps
                    }
                }),
                IconPaletteWindow.Panel(new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    ItemSpacing = 16,
                    LineSpacing = 8,
                    Children =
                    {
                        Labeled("Cry delay", cryDelay), Labeled("Motion routine", motion), Labeled("Start delay", startDelay),
                        Labeled("Height, female", heightFemale), Labeled("Height, male", heightMale),
                        Labeled("Front extra Y offset", frontYOffset), Labeled("Shadow X offset", shadowX), Labeled("Shadow size", shadowSize)
                    }
                })
            }
        };
    }

    private static Control Cell(Control control, int row, int column)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        return control;
    }

    private static List<string> ActionChoices(sbyte value)
    {
        var choices = new List<string> { "End", "Frame 1", "Frame 2" };
        for (int step = 1; step <= SpriteAnimationService.StepCount; step++) choices.Add($"Repeat from step {step}");
        if (value > 1 || value < -1 - SpriteAnimationService.StepCount) choices.Add($"Other value ({value})");
        return choices;
    }

    // End is -1, frames are 0 and 1, and a repeat to step N (1-based) is -(N + 1).
    private static int ActionIndex(sbyte value) => value switch
    {
        -1 => 0,
        0 or 1 => value + 1,
        < -1 when -value - 1 <= SpriteAnimationService.StepCount => 2 + (-value - 1),
        _ => 3 + SpriteAnimationService.StepCount
    };

    private static sbyte ActionValue(int index, sbyte previous) => index switch
    {
        0 => -1,
        1 or 2 => (sbyte)(index - 1),
        _ when index <= 2 + SpriteAnimationService.StepCount => (sbyte)(-(index - 2) - 1),
        _ => previous
    };

    private void ShowFace()
    {
        loading = true;
        try
        {
            SpriteFaceAnimation face = Face;
            for (int i = 0; i < rows.Length; i++)
            {
                SpriteAnimationStep step = face.Steps[i];
                rows[i].Action.ItemsSource = ActionChoices(step.Frame);
                rows[i].Action.SelectedIndex = ActionIndex(step.Frame);
                rows[i].Duration.Value = step.Duration;
                rows[i].X.Value = step.XShift;
                rows[i].Y.Value = step.YShift;
            }

            cryDelay.Value = face.CryDelay;
            motion.Value = face.Motion;
            startDelay.Value = face.StartDelay;
            int offset = Front ? 2 : 0;
            ShowHeight(heightFemale, animation.Heights[offset]);
            ShowHeight(heightMale, animation.Heights[offset + 1]);
            frontYOffset.Value = animation.FrontYOffset;
            frontYOffset.IsEnabled = Front;
            shadowX.Value = animation.ShadowXOffset;
            shadowSize.Value = animation.ShadowSize;
        }
        finally
        {
            loading = false;
        }

        UpdateRowStates();
        DrawFrames();
        StartPreview();
    }

    private static void ShowHeight(NumericUpDown input, int value)
    {
        input.IsEnabled = value >= 0;
        input.Value = Math.Max(0, value);
        ToolTip.SetTip(input, value >= 0 ? "Vertical position of this sprite in battle" : "This species has no sprite of this gender, so the game has no entry to edit.");
    }

    private void UpdateRowStates()
    {
        // Steps after the end never play; dim them so the live part of the sequence stands out.
        bool ended = false;
        foreach ((ComboBox action, NumericUpDown duration, NumericUpDown x, NumericUpDown y) in rows)
        {
            action.Opacity = duration.Opacity = x.Opacity = y.Opacity = ended ? 0.45 : 1;
            bool isFrame = action.SelectedIndex is 1 or 2;
            x.IsEnabled = y.IsEnabled = isFrame;
            duration.IsEnabled = action.SelectedIndex != 0;
            if (action.SelectedIndex == 0) ended = true;
        }
    }

    private void StepChanged(int index)
    {
        if (loading) return;
        SpriteAnimationStep step = Face.Steps[index];
        (ComboBox action, NumericUpDown duration, NumericUpDown x, NumericUpDown y) = rows[index];
        step.Frame = ActionValue(action.SelectedIndex, step.Frame);
        step.Duration = (byte)(duration.Value ?? 0);
        step.XShift = (sbyte)(x.Value ?? 0);
        step.YShift = (sbyte)(y.Value ?? 0);
        UpdateRowStates();
        Edited();
        StartPreview();
    }

    private void SettingsChanged()
    {
        if (loading) return;
        SpriteFaceAnimation face = Face;
        face.CryDelay = (byte)(cryDelay.Value ?? 0);
        face.Motion = (byte)(motion.Value ?? 0);
        face.StartDelay = (byte)(startDelay.Value ?? 0);
        int offset = Front ? 2 : 0;
        if (heightFemale.IsEnabled) animation.Heights[offset] = (int)(heightFemale.Value ?? 0);
        if (heightMale.IsEnabled) animation.Heights[offset + 1] = (int)(heightMale.Value ?? 0);
        if (Front) animation.FrontYOffset = (sbyte)(frontYOffset.Value ?? 0);
        animation.ShadowXOffset = (sbyte)(shadowX.Value ?? 0);
        animation.ShadowSize = (byte)(shadowSize.Value ?? 0);
        Edited();
    }

    private void Edited() => status.Text = Snapshot() == savedSnapshot ? "" : "Not saved yet.";

    private string Snapshot() => System.Text.Json.JsonSerializer.Serialize(animation);

    /// <summary>The sprite sheet for the chosen side and gender, falling back to the other gender when
    /// this one has none.</summary>
    private byte[]? Sheet()
    {
        int back = Front ? 2 : 0;
        int female = back, male = back + 1;
        byte[]? preferred = sprites[Male ? male : female];
        return preferred ?? sprites[Male ? female : male];
    }

    private byte[]? FrameRgba(int frame)
    {
        if (Sheet() is not byte[] indices || palette is null || frame is < 0 or > 1) return null;
        byte[] sheet = BattleSpriteService.ComposeRgba(indices, palette);
        byte[] rgba = new byte[FrameSize * FrameSize * 4];
        for (int y = 0; y < FrameSize; y++)
        {
            Array.Copy(sheet, (y * BattleSpriteService.SpriteWidth + frame * FrameSize) * 4, rgba, y * FrameSize * 4, FrameSize * 4);
        }

        return rgba;
    }

    private void DrawFrames()
    {
        frame1.Source = MainWindow.CreateBitmapFromRgba(FrameRgba(0), FrameSize, FrameSize);
        frame2.Source = MainWindow.CreateBitmapFromRgba(FrameRgba(1), FrameSize, FrameSize);
        ShowStageFrame(0, 0, 0);
    }

    private void ShowStageFrame(int frame, int xShift, int yShift)
    {
        stageImage.Source = MainWindow.CreateBitmapFromRgba(FrameRgba(frame), FrameSize, FrameSize);
        Canvas.SetLeft(stageImage, (Stage - FrameSize) / 2.0 * Scale + xShift * Scale);
        Canvas.SetTop(stageImage, (Stage - FrameSize) / 2.0 * Scale + yShift * Scale);
    }

    private void StartPreview()
    {
        timer?.Stop();
        sequence = SpriteAnimationService.Play(Face.Steps);
        if (Sheet() is null)
        {
            playStatus.Text = "This side has no sprite to preview.";
            return;
        }

        if (sequence.Count == 0)
        {
            ShowStageFrame(0, 0, 0);
            playStatus.Text = "Step 1 ends the sequence, so the sprite does not animate.";
            return;
        }

        int ticks = sequence.Sum(frame => frame.Ticks);
        playStatus.Text = $"{sequence.Count} frame changes, {ticks} ticks ({ticks / 60.0:0.00} s).";
        playIndex = 0;
        ticksLeft = sequence[0].Ticks;
        ShowPlayed();
        timer = new DispatcherTimer { Interval = Tick };
        timer.Tick += (_, _) => Advance();
        timer.Start();
    }

    private void StopPreview()
    {
        timer?.Stop();
        ShowStageFrame(0, 0, 0);
    }

    private void ShowPlayed()
    {
        SpriteAnimationService.PlayedFrame played = sequence[playIndex];
        ShowStageFrame(played.Frame, played.XShift, played.YShift);
    }

    private void Advance()
    {
        if (--ticksLeft > 0) return;
        playIndex++;
        if (playIndex >= sequence.Count)
        {
            // The game rests on frame 1 when the sequence ends; pause there before repeating.
            ShowStageFrame(0, 0, 0);
            if (loopPreview.IsChecked != true)
            {
                timer?.Stop();
                return;
            }

            playIndex = -1;
            ticksLeft = 45;
            return;
        }

        ticksLeft = sequence[playIndex].Ticks;
        ShowPlayed();
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

            SpriteAnimationService.Save(project, metadata, species, animation);
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
