using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>Picks which events of another event file to bring into the one being edited - the legacy
/// Event Importer (DS_Map/EventFileImport.cs): a column per kind with every event ticked, all/none
/// buttons, and the choice to clear the current events first. Closes with the choice, or null.</summary>
public sealed class EventImportWindow : Window
{
    public sealed record Choice(IReadOnlyList<MapEvent> Events, bool ReplaceExisting, bool MoveToCurrentSquare);

    private readonly List<(CheckBox Box, MapEvent Event)> rows = [];

    public EventImportWindow(MapEventFile source, string sourceName, bool canMoveToSquare, Func<EventKind, int, MapEvent, string> describe)
    {
        Title = "Import Events";
        Width = 1000;
        Height = 620;
        MinWidth = 640;
        MinHeight = 360;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#11161C"));
        Foreground = new SolidColorBrush(Color.Parse("#EDF2F7"));

        var columns = new Grid { ColumnDefinitions = new ColumnDefinitions("*,*,*,*"), ColumnSpacing = 10 };
        AddColumn(columns, 0, "Spawnables", EventKind.Spawnable, source.Spawnables, describe);
        AddColumn(columns, 1, "Overworlds", EventKind.Overworld, source.Overworlds, describe);
        AddColumn(columns, 2, "Warps", EventKind.Warp, source.Warps, describe);
        AddColumn(columns, 3, "Triggers", EventKind.Trigger, source.Triggers, describe);

        var replace = new CheckBox { Content = "Clear this file's events first (replace instead of add)" };
        var move = new CheckBox
        {
            Content = "Move them onto this map's matrix square",
            IsChecked = canMoveToSquare,
            IsEnabled = canMoveToSquare
        };
        ToolTip.SetTip(move, "Events record which matrix square they are on; ones from another map's square would not show on this map.");
        var status = new TextBlock { Foreground = Brushes.IndianRed, VerticalAlignment = VerticalAlignment.Center };
        var import = new Button { Content = "Import", Padding = new Thickness(16, 7) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(16, 7) };
        import.Click += (_, _) =>
        {
            List<MapEvent> picked = rows.Where(row => row.Box.IsChecked == true).Select(row => row.Event).ToList();
            if (picked.Count == 0)
            {
                status.Text = "Tick at least one event.";
                return;
            }

            Close(new Choice(picked, replace.IsChecked == true, move.IsChecked == true));
        };
        cancel.Click += (_, _) => Close(null);

        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(status);
        buttons.Children.Add(import);
        buttons.Children.Add(cancel);

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto,Auto,Auto"), RowSpacing = 10, Margin = new Thickness(16) };
        layout.Children.Add(new TextBlock { Text = $"Events in {sourceName}. Untick the ones to leave out.", Foreground = new SolidColorBrush(Color.Parse("#AAB7C4")) });
        Grid.SetRow(columns, 1);
        layout.Children.Add(columns);
        Grid.SetRow(replace, 2);
        layout.Children.Add(replace);
        Grid.SetRow(move, 3);
        layout.Children.Add(move);
        Grid.SetRow(buttons, 4);
        layout.Children.Add(buttons);
        Content = layout;
    }

    private void AddColumn<T>(Grid columns, int column, string title, EventKind kind, IReadOnlyList<T> events, Func<EventKind, int, MapEvent, string> describe) where T : MapEvent
    {
        var list = new StackPanel { Spacing = 2 };
        var boxes = new List<CheckBox>();
        for (int i = 0; i < events.Count; i++)
        {
            var box = new CheckBox { Content = describe(kind, i, events[i]), IsChecked = true, FontSize = 12 };
            boxes.Add(box);
            rows.Add((box, events[i]));
            list.Children.Add(box);
        }

        var all = new Button { Content = "All", Padding = new Thickness(8, 3), FontSize = 11 };
        var none = new Button { Content = "None", Padding = new Thickness(8, 3), FontSize = 11 };
        all.Click += (_, _) => boxes.ForEach(box => box.IsChecked = true);
        none.Click += (_, _) => boxes.ForEach(box => box.IsChecked = false);
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        header.Children.Add(new TextBlock { Text = $"{title} ({events.Count})", FontWeight = FontWeight.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(all);
        header.Children.Add(none);

        var panel = new Grid { RowDefinitions = new RowDefinitions("Auto,*"), RowSpacing = 6 };
        panel.Children.Add(header);
        var scroll = new ScrollViewer { Content = list };
        Grid.SetRow(scroll, 1);
        panel.Children.Add(scroll);
        var border = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#151C23")),
            BorderBrush = new SolidColorBrush(Color.Parse("#33414D")),
            BorderThickness = new Thickness(1),
            Padding = new Thickness(8),
            Child = panel
        };
        Grid.SetColumn(border, column);
        columns.Children.Add(border);
    }
}
