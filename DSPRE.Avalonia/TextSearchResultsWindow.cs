using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;

namespace DSPRE.Avalonia;

/// <summary>Lists the messages a Text Editor search found. Double-clicking a result (or "Go To")
/// opens its archive in the editor and selects the message; the window stays open so the rest of
/// the results can be worked through.</summary>
public sealed class TextSearchResultsWindow : Window
{
    public sealed record Result(int ArchiveId, int MessageIndex, string Text)
    {
        public override string ToString() => $"Archive {ArchiveId:D4}, message {MessageIndex}:  {Text}";
    }

    public TextSearchResultsWindow(string query, IReadOnlyList<Result> results, Action<Result> goTo)
    {
        Title = "Text Search Results";
        Width = 760;
        Height = 520;
        MinWidth = 420;
        MinHeight = 260;
        Background = new SolidColorBrush(Color.Parse("#11161C"));
        Foreground = new SolidColorBrush(Color.Parse("#EDF2F7"));

        var summary = new TextBlock
        {
            Text = $"{results.Count} message(s) contain \"{query}\". Double-click one to open it in the Text Editor.",
            Foreground = new SolidColorBrush(Color.Parse("#AAB7C4")),
            TextWrapping = TextWrapping.Wrap
        };
        var list = new ListBox { ItemsSource = results };
        ScrollViewer.SetHorizontalScrollBarVisibility(list, global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto);
        var goButton = new Button { Content = "Go To", Padding = new Thickness(14, 6), HorizontalAlignment = HorizontalAlignment.Left };

        void GoToSelected()
        {
            if (list.SelectedItem is Result result) goTo(result);
        }

        goButton.Click += (_, _) => GoToSelected();
        list.DoubleTapped += (_, _) => GoToSelected();

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8, Margin = new Thickness(14) };
        root.Children.Add(summary);
        Grid.SetRow(list, 1);
        root.Children.Add(list);
        Grid.SetRow(goButton, 2);
        root.Children.Add(goButton);
        Content = root;
    }
}
