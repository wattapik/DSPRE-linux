using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>Asks how an OBJ is to be scaled down before it becomes a map model: the largest texture size
/// and a multiplier for the model's coordinates. Closing the window answers null.</summary>
public sealed class ObjImportOptionsDialog : Window
{
    private static readonly int[] TextureSizes = [32, 64, 128, 256, 512];

    public ObjImportOptionsDialog(string objName)
    {
        Title = "Import OBJ";
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#11161C"));
        Foreground = new SolidColorBrush(Color.Parse("#EDF2F7"));

        var sizes = new ComboBox { ItemsSource = TextureSizes.Select(size => $"{size} x {size}").ToList(), SelectedIndex = 2, Width = 160 };
        var scale = new NumericUpDown { Minimum = 0.001m, Maximum = 1000m, Value = 1m, Increment = 0.1m, FormatString = "0.###", Width = 160 };
        var fit = new CheckBox
        {
            Content = $"Reduce triangles until the model fits what the game can load ({MapFileService.ModelCriticalSize:N0} bytes; larger models crash)",
            IsChecked = true
        };
        var import = new Button { Content = "Import", Padding = new Thickness(14, 7) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 7) };
        import.Click += (_, _) => Close(new ObjToImd.Options(TextureSizes[Math.Max(sizes.SelectedIndex, 0)], (float)(scale.Value ?? 1m), 1f, fit.IsChecked == true));
        cancel.Click += (_, _) => Close(null);

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 12,
            Children =
            {
                new TextBlock { Text = $"Import {objName} as the map's model.", TextWrapping = TextWrapping.Wrap },
                new TextBlock { Text = "Largest texture size (bigger textures are scaled down; all textures together are also kept under 256 KiB)", TextWrapping = TextWrapping.Wrap, Foreground = new SolidColorBrush(Color.Parse("#AAB7C4")) },
                sizes,
                new TextBlock { Text = "Model scale (multiplies the model's size)", Foreground = new SolidColorBrush(Color.Parse("#AAB7C4")) },
                scale,
                fit,
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { cancel, import } }
            }
        };
    }
}
