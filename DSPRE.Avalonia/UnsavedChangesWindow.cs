using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DSPRE.Avalonia;

/// <summary>Lists every editor with changes that are not saved to the project yet. Closing with
/// "Save All" answers true; the caller does the saving.</summary>
public sealed class UnsavedChangesWindow : Window
{
    public UnsavedChangesWindow(IReadOnlyList<(string Area, IReadOnlyList<string> Details)> changes)
    {
        Title = "Unsaved Changes";
        Width = 640;
        Height = 560;
        MinWidth = 420;
        MinHeight = 300;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#11161C"));
        Foreground = new SolidColorBrush(Color.Parse("#EDF2F7"));

        var muted = new SolidColorBrush(Color.Parse("#AAB7C4"));
        var list = new StackPanel { Spacing = 10 };
        foreach ((string area, IReadOnlyList<string> details) in changes)
        {
            var card = new StackPanel { Spacing = 3 };
            card.Children.Add(new TextBlock { Text = area, FontWeight = FontWeight.SemiBold });
            foreach (string detail in details)
            {
                card.Children.Add(new TextBlock { Text = detail, Foreground = muted, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(12, 0, 0, 0) });
            }

            list.Children.Add(new Border
            {
                Background = new SolidColorBrush(Color.Parse("#151C23")),
                BorderBrush = new SolidColorBrush(Color.Parse("#33414D")),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(4),
                Padding = new Thickness(12, 8),
                Child = card
            });
        }

        if (changes.Count == 0) list.Children.Add(new TextBlock { Text = "Nothing is waiting to be saved.", Foreground = muted });

        var saveAll = new Button { Content = "Save All", Padding = new Thickness(18, 8), IsEnabled = changes.Count > 0 };
        saveAll.Click += (_, _) => Close(true);
        var close = new Button { Content = "Close", Padding = new Thickness(18, 8) };
        close.Click += (_, _) => Close(false);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), Margin = new Thickness(16), RowSpacing = 12 };
        root.Children.Add(new TextBlock
        {
            Text = changes.Count == 0 ? "No unsaved changes" : $"{changes.Count} unsaved change{(changes.Count == 1 ? "" : "s")}",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold
        });
        var scroll = new ScrollViewer { Content = list };
        Grid.SetRow(scroll, 1);
        root.Children.Add(scroll);
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right, Children = { saveAll, close } };
        Grid.SetRow(buttons, 2);
        root.Children.Add(buttons);
        Content = root;
    }
}
