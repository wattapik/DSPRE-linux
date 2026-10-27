using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;

namespace DSPRE.Avalonia;

/// <summary>One thing a project search found: what kind of thing it is, how to describe it, and how
/// to open it in its editor.</summary>
public sealed record ProjectSearchResult(string Category, string Label, Action Open);

/// <summary>Searches the whole project for a phrase - headers, maps, matrices, scripts, texts and the
/// named game data - and opens whatever result is double-clicked in its editor. A header found by name
/// also lists everything linked to it (its maps, matrix, scripts, texts and encounters).</summary>
public sealed class ProjectSearchWindow : Window
{
    private static readonly IBrush CategoryBrush = new SolidColorBrush(Color.Parse("#65D8C7"));
    private static readonly IBrush MutedBrush = new SolidColorBrush(Color.Parse("#AAB7C4"));

    private readonly Func<string, Task<IReadOnlyList<ProjectSearchResult>>> search;
    private readonly TextBox queryInput = new() { Watermark = "Search the whole project, e.g. Jubilife City", MinWidth = 360 };
    private readonly ComboBox categoryFilter = new() { Width = 220, Margin = new Thickness(8, 0, 0, 0) };
    private readonly ListBox results = new();
    private readonly TextBlock status = new() { Foreground = MutedBrush, TextWrapping = TextWrapping.Wrap };
    private IReadOnlyList<ProjectSearchResult> allResults = [];
    private int searchRun;

    public ProjectSearchWindow(Func<string, Task<IReadOnlyList<ProjectSearchResult>>> search, string? initialQuery = null)
    {
        this.search = search;
        Title = "Search Project";
        Width = 900;
        Height = 620;
        MinWidth = 520;
        MinHeight = 320;
        Background = new SolidColorBrush(Color.Parse("#11161C"));
        Foreground = new SolidColorBrush(Color.Parse("#EDF2F7"));

        var searchButton = new Button { Content = "Search", Padding = new Thickness(14, 6), Margin = new Thickness(8, 0, 0, 0) };
        searchButton.Click += (_, _) => _ = RunSearchAsync();
        queryInput.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            _ = RunSearchAsync();
        };
        categoryFilter.SelectionChanged += (_, _) => ShowResults();

        results.ItemTemplate = new FuncDataTemplate<ProjectSearchResult>((result, _) => new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 10,
            Children =
            {
                new TextBlock { Text = result?.Category, Width = 150, Foreground = CategoryBrush, FontWeight = FontWeight.SemiBold },
                new TextBlock { Text = result?.Label, TextTrimming = TextTrimming.CharacterEllipsis }
            }
        });
        results.DoubleTapped += (_, _) => OpenSelected();
        results.KeyDown += (_, e) =>
        {
            if (e.Key != Key.Enter) return;
            e.Handled = true;
            OpenSelected();
        };

        var openButton = new Button { Content = "Open", Padding = new Thickness(14, 6), HorizontalAlignment = HorizontalAlignment.Left };
        openButton.Click += (_, _) => OpenSelected();

        var top = new DockPanel();
        DockPanel.SetDock(categoryFilter, Dock.Right);
        DockPanel.SetDock(searchButton, Dock.Right);
        top.Children.Add(categoryFilter);
        top.Children.Add(searchButton);
        top.Children.Add(queryInput);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 8, Margin = new Thickness(14) };
        root.Children.Add(top);
        Grid.SetRow(status, 1);
        root.Children.Add(status);
        Grid.SetRow(results, 2);
        root.Children.Add(results);
        Grid.SetRow(openButton, 3);
        root.Children.Add(openButton);
        Content = root;

        status.Text = "Type a phrase and press Enter. Double-click a result to open it.";
        Opened += (_, _) =>
        {
            queryInput.Focus();
            if (!string.IsNullOrWhiteSpace(initialQuery))
            {
                queryInput.Text = initialQuery;
                _ = RunSearchAsync();
            }
        };
    }

    private async Task RunSearchAsync()
    {
        string query = queryInput.Text?.Trim() ?? string.Empty;
        if (query.Length < 2)
        {
            status.Text = "Type at least two characters.";
            return;
        }

        int run = ++searchRun;
        status.Text = $"Searching for \"{query}\"...";
        IReadOnlyList<ProjectSearchResult> found;
        try
        {
            found = await search(query);
        }
        catch (Exception ex)
        {
            status.Text = $"The search failed: {ex.Message}";
            return;
        }

        if (run != searchRun) return;
        allResults = found;
        string? previousCategory = categoryFilter.SelectedItem as string;
        categoryFilter.Items.Clear();
        categoryFilter.Items.Add("All categories");
        foreach (string category in found.Select(result => result.Category).Distinct()) categoryFilter.Items.Add(category);
        int restore = previousCategory is null ? 0 : categoryFilter.Items.IndexOf(previousCategory);
        categoryFilter.SelectedIndex = Math.Max(0, restore);
        ShowResults();
        status.Text = found.Count == 0
            ? $"Nothing in the project matches \"{query}\"."
            : $"{found.Count} result(s) for \"{query}\" in {found.Select(result => result.Category).Distinct().Count()} categories. Double-click one to open it.";
    }

    private void ShowResults()
    {
        string? category = categoryFilter.SelectedIndex > 0 ? categoryFilter.SelectedItem as string : null;
        results.ItemsSource = category is null ? allResults : allResults.Where(result => result.Category == category).ToList();
    }

    private void OpenSelected()
    {
        if (results.SelectedItem is ProjectSearchResult result) result.Open();
    }
}
