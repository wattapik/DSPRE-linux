using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>Edits one 16-color DS palette in place: a swatch per color, and for the selected one red,
/// green and blue in the DS's own 0-31 steps (slider and number) plus a #RRGGBB box for pasting. Color 0
/// is transparent on sprites and icons, so it is shown but not editable. Raises Changed after each edit.</summary>
public sealed class PaletteEditorPanel : StackPanel
{
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#AAB7C4"));
    private static readonly IBrush SwatchBorder = new SolidColorBrush(Color.Parse("#33414D"));
    private static readonly IBrush SelectedBorder = new SolidColorBrush(Color.Parse("#65D8C7"));

    private readonly WrapPanel swatches = new() { Orientation = Orientation.Horizontal };
    private readonly Grid editor = new() { ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"), RowDefinitions = new RowDefinitions("Auto,Auto,Auto,Auto"), ColumnSpacing = 10, RowSpacing = 6, IsVisible = false };
    private readonly (Slider Slider, NumericUpDown Number)[] channels;
    private readonly TextBox hex = new() { Width = 110 };
    private readonly Border preview = new() { Width = 56, Height = 56, BorderBrush = SwatchBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(4) };
    private readonly TextBlock selectedLabel = new() { FontWeight = FontWeight.SemiBold };
    private RgbColor[] colors = [];
    private int selected = -1;
    private bool loading;

    public event EventHandler? Changed;

    public PaletteEditorPanel()
    {
        Spacing = 10;
        channels = new[] { "Red", "Green", "Blue" }.Select((name, row) =>
        {
            var slider = new Slider { Minimum = 0, Maximum = 31, TickFrequency = 1, IsSnapToTickEnabled = true, MinWidth = 140 };
            var number = new NumericUpDown { Minimum = 0, Maximum = 31, Increment = 1, FormatString = "0", Width = 110 };
            slider.ValueChanged += (_, _) =>
            {
                if (loading) return;
                loading = true;
                number.Value = (decimal)slider.Value;
                loading = false;
                ApplyChannels();
            };
            number.ValueChanged += (_, _) =>
            {
                if (loading) return;
                loading = true;
                slider.Value = (double)(number.Value ?? 0);
                loading = false;
                ApplyChannels();
            };
            editor.Children.Add(Place(new TextBlock { Text = name, VerticalAlignment = VerticalAlignment.Center }, row + 1, 0));
            editor.Children.Add(Place(slider, row + 1, 1));
            editor.Children.Add(Place(number, row + 1, 2));
            return (slider, number);
        }).ToArray();

        hex.LostFocus += (_, _) => ApplyHex();
        hex.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            ApplyHex();
            e.Handled = true;
        };
        editor.Children.Add(Place(new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children = { preview, new StackPanel { Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Children = { selectedLabel, new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6, Children = { new TextBlock { Text = "Hex", VerticalAlignment = VerticalAlignment.Center, Foreground = MutedBrush }, hex } } } } }
        }, 0, 0, 3));

        Children.Add(new TextBlock { Text = "Click a color to edit it. Color 0 is transparent. Channels run 0-31, the steps the DS stores.", Foreground = MutedBrush, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        Children.Add(swatches);
        Children.Add(editor);
    }

    /// <summary>The palette being edited; the array is edited in place.</summary>
    public RgbColor[] Colors
    {
        get => colors;
        set
        {
            colors = value;
            if (selected >= colors.Length) selected = -1;
            Refresh();
        }
    }

    private static Control Place(Control control, int row, int column, int columnSpan = 1)
    {
        Grid.SetRow(control, row);
        Grid.SetColumn(control, column);
        Grid.SetColumnSpan(control, columnSpan);
        return control;
    }

    private static IBrush Brush(RgbColor color) => new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B));

    private void Refresh()
    {
        swatches.Children.Clear();
        for (int c = 0; c < colors.Length; c++)
        {
            int index = c;
            var swatch = new Border
            {
                Width = 40,
                Height = 40,
                Margin = new Thickness(0, 0, 4, 4),
                CornerRadius = new CornerRadius(3),
                Background = c == 0 ? Brushes.Transparent : Brush(colors[c]),
                BorderBrush = c == selected ? SelectedBorder : SwatchBorder,
                BorderThickness = new Thickness(c == selected ? 3 : 1),
                Cursor = c == 0 ? Cursor.Default : new Cursor(StandardCursorType.Hand),
                Child = new TextBlock
                {
                    Text = c.ToString(CultureInfo.InvariantCulture),
                    FontSize = 10,
                    Foreground = c == 0 ? MutedBrush : Contrast(colors[c]),
                    HorizontalAlignment = HorizontalAlignment.Center,
                    VerticalAlignment = VerticalAlignment.Center
                }
            };
            ToolTip.SetTip(swatch, c == 0 ? "Color 0 is transparent" : $"Color {c}: {Hex(colors[c])}");
            if (c > 0) swatch.PointerPressed += (_, _) => Select(index);
            swatches.Children.Add(swatch);
        }

        editor.IsVisible = selected > 0;
        if (!editor.IsVisible) return;
        RgbColor color = colors[selected];
        loading = true;
        int[] values = [color.R >> 3, color.G >> 3, color.B >> 3];
        for (int i = 0; i < 3; i++)
        {
            channels[i].Slider.Value = values[i];
            channels[i].Number.Value = values[i];
        }

        hex.Text = Hex(color);
        loading = false;
        preview.Background = Brush(color);
        selectedLabel.Text = $"Color {selected}";
    }

    private static IBrush Contrast(RgbColor color) => color.R * 299 + color.G * 587 + color.B * 114 > 128000 ? Brushes.Black : Brushes.White;

    private static string Hex(RgbColor color) => $"#{color.R:X2}{color.G:X2}{color.B:X2}";

    private void Select(int index)
    {
        selected = index;
        Refresh();
    }

    private void ApplyChannels()
    {
        if (selected <= 0) return;
        byte Channel(int i) => (byte)(Math.Clamp((int)(channels[i].Number.Value ?? 0), 0, 31) << 3);
        Set(new RgbColor(Channel(0), Channel(1), Channel(2)));
    }

    private void ApplyHex()
    {
        if (loading || selected <= 0) return;
        string text = (hex.Text ?? "").Trim().TrimStart('#');
        if (text.Length != 6 || !int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int rgb))
        {
            hex.Text = Hex(colors[selected]);
            return;
        }

        // Rounded down to the DS's 5 bits per channel, the precision the palette can hold.
        Set(new RgbColor((byte)(((rgb >> 16) & 0xFF) & 0xF8), (byte)(((rgb >> 8) & 0xFF) & 0xF8), (byte)((rgb & 0xFF) & 0xF8)));
    }

    private void Set(RgbColor color)
    {
        if (colors[selected] == color)
        {
            Refresh();
            return;
        }

        colors[selected] = color;
        Refresh();
        Changed?.Invoke(this, EventArgs.Empty);
    }
}
