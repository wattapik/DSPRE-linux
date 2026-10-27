using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DSPRE.Core;
using System.Collections.ObjectModel;
using System.Diagnostics;

namespace DSPRE.Avalonia;

/// <summary>The status message log: everything the program reported this session, newest last,
/// filterable by level and text, with the on-disk log file one click away.</summary>
public sealed class StatusLogWindow : Window
{
    private static readonly IBrush InfoBrush = new SolidColorBrush(Color.Parse("#EDF2F7"));
    private static readonly IBrush WarningBrush = new SolidColorBrush(Color.Parse("#E8B75F"));
    private static readonly IBrush ErrorBrush = new SolidColorBrush(Color.Parse("#E26D6D"));

    private readonly ObservableCollection<StatusEntry> shown = [];
    private readonly ListBox list;
    private readonly ComboBox levelFilter = new() { Width = 170, Margin = new Thickness(0, 0, 8, 0) };
    private readonly TextBox textFilter = new() { Width = 240, Watermark = "Filter messages...", Margin = new Thickness(0, 0, 8, 0) };
    private readonly CheckBox followNewest = new() { Content = "Follow newest", IsChecked = true, VerticalAlignment = VerticalAlignment.Center };

    public StatusLogWindow()
    {
        Title = "Status Log";
        Width = 980;
        Height = 560;
        MinWidth = 520;
        MinHeight = 300;
        Background = new SolidColorBrush(Color.Parse("#11161C"));
        Foreground = InfoBrush;

        levelFilter.Items.Add("All messages");
        levelFilter.Items.Add("Warnings and errors");
        levelFilter.Items.Add("Errors only");
        levelFilter.SelectedIndex = 0;
        levelFilter.SelectionChanged += (_, _) => Refill();
        textFilter.TextChanged += (_, _) => Refill();

        list = new ListBox
        {
            ItemsSource = shown,
            FontFamily = new FontFamily("monospace"),
            ItemTemplate = new FuncDataTemplate<StatusEntry>((entry, _) => new SelectableTextBlock
            {
                Text = entry?.ToString(),
                TextWrapping = TextWrapping.Wrap,
                Foreground = entry?.Level switch
                {
                    StatusLevel.Error => ErrorBrush,
                    StatusLevel.Warning => WarningBrush,
                    _ => InfoBrush
                }
            })
        };

        var openFolder = new Button { Content = "Open Log Folder", Padding = new Thickness(12, 6), Margin = new Thickness(0, 0, 8, 0) };
        openFolder.Click += (_, _) => OpenLogFolder();

        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        toolbar.Children.Add(levelFilter);
        toolbar.Children.Add(textFilter);
        toolbar.Children.Add(openFolder);
        toolbar.Children.Add(followNewest);

        var path = new SelectableTextBlock
        {
            Text = $"Also written to {StatusLog.LogFilePath}",
            Foreground = new SolidColorBrush(Color.Parse("#AAB7C4")),
            TextWrapping = TextWrapping.Wrap
        };

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,*,Auto"), RowSpacing = 8, Margin = new Thickness(14) };
        root.Children.Add(toolbar);
        Grid.SetRow(list, 1);
        root.Children.Add(list);
        Grid.SetRow(path, 2);
        root.Children.Add(path);
        Content = root;

        Refill();
        StatusLog.Added += OnAdded;
        Closed += (_, _) => StatusLog.Added -= OnAdded;
    }

    private bool Matches(StatusEntry entry)
    {
        bool levelOk = levelFilter.SelectedIndex switch
        {
            1 => entry.Level >= StatusLevel.Warning,
            2 => entry.Level == StatusLevel.Error,
            _ => true
        };
        string filter = textFilter.Text?.Trim() ?? string.Empty;
        return levelOk && (filter.Length == 0 ||
            entry.Message.Contains(filter, StringComparison.OrdinalIgnoreCase) ||
            entry.Source.Contains(filter, StringComparison.OrdinalIgnoreCase));
    }

    private void Refill()
    {
        shown.Clear();
        foreach (StatusEntry entry in StatusLog.Snapshot().Where(Matches)) shown.Add(entry);
        ScrollToNewest();
    }

    private void OnAdded(StatusEntry entry) => Dispatcher.UIThread.Post(() =>
    {
        if (!Matches(entry)) return;
        shown.Add(entry);
        ScrollToNewest();
    });

    private void ScrollToNewest()
    {
        if (followNewest.IsChecked == true && shown.Count > 0) list.ScrollIntoView(shown.Count - 1);
    }

    private static void OpenLogFolder()
    {
        try
        {
            Directory.CreateDirectory(StatusLog.LogDirectory);
            string opener = OperatingSystem.IsWindows() ? "explorer" : OperatingSystem.IsMacOS() ? "open" : "xdg-open";
            Process.Start(new ProcessStartInfo(opener) { ArgumentList = { StatusLog.LogDirectory }, UseShellExecute = false });
        }
        catch (Exception ex)
        {
            StatusLog.Warning("Status Log", $"The log folder could not be opened: {ex.Message}");
        }
    }
}
