using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>A Pokémon's menu icon colors: which of the shared icon palettes it uses, and those palettes'
/// colors (see PokemonIconService). Every icon using a palette changes with it, so the icons sharing the
/// one being edited are previewed alongside. Closes with true when something was saved.</summary>
public sealed class IconPaletteWindow : Window
{
    private const int IconScale = 4;
    private const int SharerScale = 2;
    private const int SharerPreviews = 24;
    private const int FrameBytes = 32 * 32 * 4;
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#AAB7C4"));
    private static readonly IBrush PanelBrush = new SolidColorBrush(Color.Parse("#151C23"));
    private static readonly IBrush PanelBorder = new SolidColorBrush(Color.Parse("#33414D"));

    private readonly RomProject project;
    private readonly RomGameMetadata metadata;
    private readonly int iconId;
    private readonly int[] iconPalettes;
    private readonly RgbColor[][] palettes;
    private readonly RgbColor[][] savedPalettes;
    private readonly int savedChoice;
    private readonly int choices;
    private readonly ComboBox paletteSelector;
    private readonly PaletteEditorPanel editor = new();
    private readonly Image frame1 = PixelImage(32 * IconScale, 32 * IconScale);
    private readonly Image frame2 = PixelImage(32 * IconScale, 32 * IconScale);
    private readonly WrapPanel sharers = new() { Orientation = Orientation.Horizontal };
    private readonly TextBlock sharerNote = new() { Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap };
    private readonly TextBlock status = new() { Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private bool saved;

    public IconPaletteWindow(RomProject project, RomGameMetadata metadata, int iconId, string speciesName)
    {
        this.project = project;
        this.metadata = metadata;
        this.iconId = iconId;
        iconPalettes = PokemonIconService.GetPalettes(project, metadata);
        palettes = PokemonIconService.LoadPalettes(project).Select(p => p.ToArray()).ToArray();
        savedPalettes = palettes.Select(p => p.ToArray()).ToArray();
        savedChoice = Math.Max(0, iconPalettes[iconId]);

        Title = $"Menu Icon Palette - {speciesName}";
        Width = 1000;
        Height = 780;
        MinWidth = 760;
        MinHeight = 560;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#11161C"));
        Foreground = new SolidColorBrush(Color.Parse("#EDF2F7"));

        choices = Math.Min(palettes.Length, Math.Max(PokemonIconService.PalettesLoadedEverywhere, iconPalettes.DefaultIfEmpty(0).Max() + 1));
        paletteSelector = new ComboBox
        {
            ItemsSource = Enumerable.Range(0, choices).Select(p => $"Palette {p} ({iconPalettes.Count(used => used == p)} icons)").ToList(),
            SelectedIndex = Math.Min(savedChoice, choices - 1),
            Width = 240
        };
        paletteSelector.SelectionChanged += (_, _) => ShowPalette();
        editor.Changed += (_, _) => Redraw();

        var save = new Button { Content = "Save", Padding = new Thickness(20, 8) };
        save.Click += (_, _) => Save();
        var revert = new Button { Content = "Revert", Padding = new Thickness(20, 8) };
        revert.Click += (_, _) =>
        {
            for (int p = 0; p < palettes.Length; p++) palettes[p] = savedPalettes[p].ToArray();
            paletteSelector.SelectedIndex = Math.Min(savedChoice, choices - 1);
            ShowPalette();
        };
        var close = new Button { Content = "Close", Padding = new Thickness(20, 8) };
        close.Click += (_, _) => Close(saved);

        var iconPanel = Panel(new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new TextBlock { Text = speciesName, FontWeight = FontWeight.SemiBold },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { frame1, frame2 } },
                new TextBlock { Text = "The icon's two bounce frames", Foreground = MutedBrush, FontSize = 12 }
            }
        });
        iconPanel.VerticalAlignment = VerticalAlignment.Top;

        var body = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16 };
        body.Children.Add(iconPanel);
        Border paletteBox = Panel(editor);
        Grid.SetColumn(paletteBox, 1);
        body.Children.Add(paletteBox);

        Content = WindowLayout(
            [
                new StackPanel
                {
                    Orientation = Orientation.Horizontal,
                    Spacing = 10,
                    Children = { new TextBlock { Text = "Uses shared palette", VerticalAlignment = VerticalAlignment.Center, FontSize = 15 }, paletteSelector }
                },
                new TextBlock
                {
                    Text = $"Icons have no colors of their own: each uses one of the shared palettes, so changing a palette's colors changes every icon using it. Most screens load only the first {PokemonIconService.PalettesLoadedEverywhere} palettes.",
                    Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap
                },
                body,
                sharerNote,
                sharers
            ],
            status, revert, save, close);

        ShowPalette();
    }

    private int Selected => Math.Max(0, paletteSelector.SelectedIndex);

    internal static Image PixelImage(int width, int height)
    {
        var image = new Image { Width = width, Height = height, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);
        return image;
    }

    internal static Border Panel(Control child) => new()
    {
        Background = PanelBrush, BorderBrush = PanelBorder, BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = child
    };

    /// <summary>Scrolling content above a fixed row of status text and buttons.</summary>
    internal static Control WindowLayout(IEnumerable<Control> content, TextBlock status, params Button[] buttons)
    {
        var footer = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 12, 0, 0) };
        footer.Children.Add(status);
        var buttonRow = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
        foreach (Button button in buttons) buttonRow.Children.Add(button);
        Grid.SetColumn(buttonRow, 1);
        footer.Children.Add(buttonRow);

        var stack = new StackPanel { Spacing = 14 };
        foreach (Control control in content) stack.Children.Add(control);
        var root = new DockPanel { Margin = new Thickness(20) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);
        root.Children.Add(new ScrollViewer { Content = stack });
        return root;
    }

    private void ShowPalette()
    {
        editor.Colors = palettes[Selected];
        Redraw();
    }

    private void Redraw()
    {
        RgbColor[] palette = palettes[Selected];
        byte[] frames = PokemonIconService.Render(project, iconId, palette);
        frame1.Source = MainWindow.CreateBitmapFromRgba(frames[..FrameBytes], 32, 32);
        frame2.Source = MainWindow.CreateBitmapFromRgba(frames[FrameBytes..], 32, 32);

        List<int> others = Enumerable.Range(0, iconPalettes.Length).Where(icon => icon != iconId && PaletteOf(icon) == Selected).ToList();
        sharerNote.Text = others.Count == 0
            ? "No other icon uses this palette."
            : $"Changing these colors also changes the {others.Count} other icons using palette {Selected}{(others.Count > SharerPreviews ? $", such as these {SharerPreviews}" : "")}:";
        sharers.Children.Clear();
        foreach (int icon in others.Take(SharerPreviews))
        {
            Image image = PixelImage(32 * SharerScale, 32 * SharerScale);
            image.Margin = new Thickness(0, 0, 6, 6);
            image.Source = MainWindow.CreateBitmapFromRgba(PokemonIconService.Render(project, icon, palette)[..FrameBytes], 32, 32);
            sharers.Children.Add(image);
        }

        bool unsaved = Selected != savedChoice || !palettes.Zip(savedPalettes).All(pair => pair.First.SequenceEqual(pair.Second));
        status.Text = unsaved ? "Not saved yet." : "";
    }

    // The icon being edited counts as using whichever palette is chosen for it here.
    private int PaletteOf(int icon) => icon == iconId ? Selected : iconPalettes[icon];

    private void Save()
    {
        try
        {
            bool changed = false;
            if (Selected != savedChoice)
            {
                PokemonIconService.SetPalette(project, metadata, iconId, Selected);
                changed = true;
            }

            for (int p = 0; p < palettes.Length; p++)
            {
                if (palettes[p].SequenceEqual(savedPalettes[p])) continue;
                PokemonIconService.SaveSharedPalette(project, p, palettes[p]);
                savedPalettes[p] = palettes[p].ToArray();
                changed = true;
            }

            if (!changed)
            {
                status.Text = "Nothing to save.";
                return;
            }

            saved = true;
            Close(true);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentOutOfRangeException)
        {
            status.Text = $"Not saved: {ex.Message}";
        }
    }
}
