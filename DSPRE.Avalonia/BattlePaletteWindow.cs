using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>A Pokémon's battle sprite colors: its normal and shiny palettes, each 16 colors shared by all
/// four of its sprites. Shows the sprites as the Sprites tab has them (unsaved imports included) and
/// saves only the palettes. Closes with the saved palettes, or null when nothing was saved.</summary>
public sealed class BattlePaletteWindow : Window
{
    public sealed record SavedPalettes(RgbColor[]? Normal, RgbColor[]? Shiny);

    private const int Scale = 2;
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#AAB7C4"));
    private static readonly string[] SlotNames = ["Female back", "Male back", "Female front", "Male front"];

    private readonly RomProject project;
    private readonly int species;
    private readonly byte[]?[] sprites;
    private readonly RgbColor[]?[] palettes;
    private readonly RgbColor[]?[] savedPalettes;
    private readonly RadioButton normalButton = new() { Content = "Normal palette", GroupName = "BattlePaletteKind", IsChecked = true, Margin = new Thickness(0, 0, 16, 0) };
    private readonly RadioButton shinyButton = new() { Content = "Shiny palette", GroupName = "BattlePaletteKind", Margin = new Thickness(0, 0, 24, 0) };
    private readonly PaletteEditorPanel editor = new();
    private readonly Image[] previews;
    private readonly TextBlock status = new() { Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap, VerticalAlignment = VerticalAlignment.Center };
    private SavedPalettes? result;

    public BattlePaletteWindow(RomProject project, int species, string speciesName, byte[]?[] sprites, RgbColor[]? normal, RgbColor[]? shiny)
    {
        this.project = project;
        this.species = species;
        this.sprites = sprites;
        palettes = [normal?.ToArray(), shiny?.ToArray()];
        savedPalettes = [normal?.ToArray(), shiny?.ToArray()];

        Title = $"Battle Sprite Palettes - {speciesName}";
        Width = 1240;
        Height = 760;
        MinWidth = 820;
        MinHeight = 600;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#11161C"));
        Foreground = new SolidColorBrush(Color.Parse("#EDF2F7"));

        normalButton.IsCheckedChanged += (_, _) => ShowPalette();
        editor.Changed += (_, _) => Redraw();

        var copy = new Button { Content = "Copy the other palette's colors here", Padding = new Thickness(12, 6) };
        ToolTip.SetTip(copy, "Start this palette from the other one's colors, e.g. to make a shiny that differs in a few colors.");
        copy.Click += (_, _) =>
        {
            RgbColor[]? other = palettes[Shiny ? 0 : 1];
            if (other is null || palettes[Kind] is null) return;
            other.CopyTo(palettes[Kind]!, 0);
            ShowPalette();
        };

        var spriteGrid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto"), ColumnSpacing = 12, RowSpacing = 12 };
        previews = new Image[4];
        for (int slot = 0; slot < 4; slot++)
        {
            previews[slot] = IconPaletteWindow.PixelImage(BattleSpriteService.SpriteWidth * Scale, BattleSpriteService.SpriteHeight * Scale);
            Border cell = IconPaletteWindow.Panel(new StackPanel
            {
                Spacing = 6,
                Children =
                {
                    new TextBlock { Text = SlotNames[slot] + (sprites[slot] is null ? " (none)" : ""), Foreground = MutedBrush, FontSize = 12 },
                    previews[slot]
                }
            });
            // The tab lays the slots out female left, male right, backs above fronts.
            Grid.SetColumn(cell, slot % 2);
            Grid.SetRow(cell, slot / 2);
            spriteGrid.Children.Add(cell);
        }

        var save = new Button { Content = "Save Palettes", Padding = new Thickness(20, 8) };
        save.Click += (_, _) => Save();
        var revert = new Button { Content = "Revert", Padding = new Thickness(20, 8) };
        revert.Click += (_, _) =>
        {
            for (int p = 0; p < 2; p++) palettes[p] = savedPalettes[p]?.ToArray();
            ShowPalette();
        };
        var close = new Button { Content = "Close", Padding = new Thickness(20, 8) };
        close.Click += (_, _) => Close(result);

        Content = IconPaletteWindow.WindowLayout(
            [
                new StackPanel { Orientation = Orientation.Horizontal, Children = { normalButton, shinyButton, copy } },
                new TextBlock
                {
                    Text = "A Pokémon's four battle sprites share one palette, plus a second for its shiny form. The sprites below are as the Sprites tab shows them; only the palettes are saved here.",
                    Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap
                },
                Body(spriteGrid, IconPaletteWindow.Panel(editor))
            ],
            status, revert, save, close);

        ShowPalette();
    }

    /// <summary>The sprites beside the palette, the palette taking the remaining width.</summary>
    private static Grid Body(Control sprites, Control palette)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = 16 };
        palette.VerticalAlignment = VerticalAlignment.Top;
        grid.Children.Add(sprites);
        Grid.SetColumn(palette, 1);
        grid.Children.Add(palette);
        return grid;
    }

    // Read from the button whose change is being handled: while the group switches, the other button
    // still reports its old state.
    private bool Shiny => normalButton.IsChecked != true;

    private int Kind => Shiny ? 1 : 0;

    private void ShowPalette()
    {
        RgbColor[]? palette = palettes[Kind];
        editor.IsVisible = palette is not null;
        if (palette is not null) editor.Colors = palette;
        Redraw();
    }

    private void Redraw()
    {
        RgbColor[]? palette = palettes[Kind];
        for (int slot = 0; slot < 4; slot++)
        {
            previews[slot].Source = sprites[slot] is byte[] indices && palette is not null
                ? MainWindow.CreateBitmapFromRgba(BattleSpriteService.ComposeRgba(indices, palette), BattleSpriteService.SpriteWidth, BattleSpriteService.SpriteHeight)
                : null;
        }

        bool unsaved = Enumerable.Range(0, 2).Any(p => !Same(palettes[p], savedPalettes[p]));
        status.Text = palette is null ? $"This Pokémon has no {(Shiny ? "shiny" : "normal")} palette file." : unsaved ? "Not saved yet." : "";
    }

    private static bool Same(RgbColor[]? a, RgbColor[]? b) => a is null ? b is null : b is not null && a.SequenceEqual(b);

    private void Save()
    {
        try
        {
            bool changed = false;
            for (int p = 0; p < 2; p++)
            {
                if (palettes[p] is not RgbColor[] palette || Same(palette, savedPalettes[p])) continue;
                BattleSpriteService.SaveBattlePalette(project, species, shiny: p == 1, palette);
                savedPalettes[p] = palette.ToArray();
                changed = true;
            }

            if (!changed)
            {
                status.Text = "Nothing to save.";
                return;
            }

            result = new SavedPalettes(savedPalettes[0]?.ToArray(), savedPalettes[1]?.ToArray());
            Close(result);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            status.Text = $"Not saved: {ex.Message}";
        }
    }
}
