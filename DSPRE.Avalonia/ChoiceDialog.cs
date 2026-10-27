using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DSPRE.Avalonia;

/// <summary>A small modal question with one button per answer; closing the window answers null.</summary>
public sealed class ChoiceDialog : Window
{
    public ChoiceDialog(string title, string message, params (string Key, string Label)[] choices)
    {
        Title = title;
        Width = 520;
        SizeToContent = SizeToContent.Height;
        CanResize = false;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#11161C"));
        Foreground = new SolidColorBrush(Color.Parse("#EDF2F7"));

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        foreach ((string key, string label) in choices)
        {
            var button = new Button { Content = label, Padding = new Thickness(14, 7) };
            button.Click += (_, _) => Close(key);
            buttons.Children.Add(button);
        }

        Content = new StackPanel
        {
            Margin = new Thickness(18),
            Spacing = 16,
            Children =
            {
                new TextBlock { Text = message, TextWrapping = TextWrapping.Wrap },
                buttons
            }
        };
    }
}
