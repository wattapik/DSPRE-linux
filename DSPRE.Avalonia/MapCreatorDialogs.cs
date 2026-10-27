using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>Asks for one line of text; closing the window answers null.</summary>
public sealed class TextPromptDialog : Window
{
    public TextPromptDialog(string title, string message, string initial = "")
    {
        Title = title;
        Width = 460;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#11161C"));
        Foreground = new SolidColorBrush(Color.Parse("#EDF2F7"));

        var input = new TextBox { Text = initial };
        var ok = new Button { Content = "OK", Padding = new Thickness(14, 7), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 7), IsCancel = true };
        ok.Click += (_, _) => Close(string.IsNullOrWhiteSpace(input.Text) ? null : input.Text.Trim());
        cancel.Click += (_, _) => Close(null);
        Opened += (_, _) =>
        {
            input.Focus();
            input.SelectAll();
        };

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                input,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, ok } }
            }
        };
    }
}

/// <summary>Asks which map to cut into tiles and how. Closing the window answers null.</summary>
public sealed class MapCreatorCutDialog : Window
{
    public sealed record Choice(int MapIndex, int TexturePack, bool PlaceOnGrid);

    /// <param name="packOfMap">The map texture pack each map's area data names (-1 when unknown), by position in <paramref name="maps"/>.</param>
    public MapCreatorCutDialog(IReadOnlyList<string> maps, IReadOnlyList<int> packOfMap, int packCount, int selected)
    {
        Title = "Cut Tiles From Map";
        Width = 560;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#11161C"));
        Foreground = new SolidColorBrush(Color.Parse("#EDF2F7"));

        var mapBox = new ComboBox { ItemsSource = maps.ToList(), SelectedIndex = Math.Max(selected, 0), HorizontalAlignment = HorizontalAlignment.Stretch };
        var packBox = new ComboBox { HorizontalAlignment = HorizontalAlignment.Stretch };
        for (int pack = 0; pack < packCount; pack++) packBox.Items.Add($"Map Texture Pack [{pack:D2}]");
        void ShowPack()
        {
            int pack = mapBox.SelectedIndex >= 0 && mapBox.SelectedIndex < packOfMap.Count ? packOfMap[mapBox.SelectedIndex] : -1;
            packBox.SelectedIndex = pack >= 0 && pack < packCount ? pack : 0;
        }

        mapBox.SelectionChanged += (_, _) => ShowPack();
        ShowPack();
        var place = new CheckBox { Content = "Also place the map on the grid (replaces the active layer)", IsChecked = true };
        var cut = new Button { Content = "Cut", Padding = new Thickness(14, 7), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 7), IsCancel = true };
        cut.Click += (_, _) => Close(new Choice(mapBox.SelectedIndex, packBox.SelectedIndex, place.IsChecked == true));
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = "The map's terrain is cut along its 32 x 32 cells into one-cell tiles, textured as the map's own texture pack textures it. Identical cells share one tile. Cells are cut cleanly at their borders, so a map can come out with several times its original polygons.", TextWrapping = TextWrapping.Wrap },
                mapBox,
                new TextBlock { Text = "Textures (the map's own pack is picked for you; a pack that does not belong to the map leaves it untextured)", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#AAB7C4")) },
                packBox,
                place,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, cut } }
            }
        };
    }
}

/// <summary>Lists the tilesets that ship with Pokemon DS Map Studio. Answers the chosen path, "" to browse for a file, or null when closed.</summary>
public sealed class MapCreatorTilesetPickerDialog : Window
{
    public MapCreatorTilesetPickerDialog(IReadOnlyList<(string Label, string Path)> tilesets)
    {
        Title = "Add Tileset";
        Width = 820;
        Height = 760;
        MinWidth = 520;
        MinHeight = 420;
        CanResize = true;
        SizeToContent = SizeToContent.Manual;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#11161C"));
        Foreground = new SolidColorBrush(Color.Parse("#EDF2F7"));

        var list = new ListBox { ItemsSource = tilesets.Select(item => item.Label).ToList() };
        var add = new Button { Content = "Add", Padding = new Thickness(14, 7), IsDefault = true };
        var browse = new Button { Content = "Browse for a .pdsts...", Padding = new Thickness(14, 7) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 7), IsCancel = true };
        add.Click += (_, _) => { if (list.SelectedIndex >= 0) Close(tilesets[list.SelectedIndex].Path); };
        list.DoubleTapped += (_, _) => { if (list.SelectedIndex >= 0) Close(tilesets[list.SelectedIndex].Path); };
        browse.Click += (_, _) => Close("");
        cancel.Click += (_, _) => Close(null);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { browse, cancel, add } };
        var layout = new DockPanel { Margin = new Thickness(18), LastChildFill = true };
        DockPanel.SetDock(buttons, Dock.Bottom);
        var header = new TextBlock
        {
            Text = tilesets.Count > 0 ? $"{tilesets.Count} tilesets from the Pokemon DS Map Studio folder. Pick one to add its tiles and textures." : "No Pokemon DS Map Studio folder was found next to DSPRE; browse for a tileset instead.",
            TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 10)
        };
        DockPanel.SetDock(header, Dock.Top);
        buttons.Margin = new Thickness(0, 10, 0, 0);
        layout.Children.Add(header);
        layout.Children.Add(buttons);
        layout.Children.Add(list);
        Content = layout;
    }
}

/// <summary>Asks which matrix cell the built map goes into. Closing the window answers null.</summary>
public sealed class MatrixPlacementDialog : Window
{
    public sealed record Choice(int Matrix, int Column, int Row);

    public MatrixPlacementDialog(string mapName, int matrixCount)
    {
        Title = "Place In Matrix";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#11161C"));
        Foreground = new SolidColorBrush(Color.Parse("#EDF2F7"));

        NumericUpDown Number(decimal max) => new() { Minimum = 0, Maximum = max, Value = 0, FormatString = "0", Width = 120 };
        var matrix = Number(Math.Max(matrixCount - 1, 0));
        var column = Number(255);
        var row = Number(255);
        var place = new Button { Content = "Place", Padding = new Thickness(14, 7), IsDefault = true };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 7), IsCancel = true };
        place.Click += (_, _) => Close(new Choice((int)(matrix.Value ?? 0), (int)(column.Value ?? 0), (int)(row.Value ?? 0)));
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = $"Puts {mapName} into a cell of a matrix and gives the cell the header made for it, so the game loads its own area data and textures there. The cell's old map and header are replaced.", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Matrix (0 is the overworld)" }, matrix,
                new TextBlock { Text = "Column (from the left, starting at 0)" }, column,
                new TextBlock { Text = "Row (from the top, starting at 0)" }, row,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, place } }
            }
        };
    }
}
