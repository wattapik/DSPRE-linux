using System.Collections.ObjectModel;
using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>Every Pokémon's level-up moves in one list - the legacy Bulk Learnset Editor
/// (DS_Map/Editors/LearnsetBulkEditor.cs): edit level and move in place, filter, add, delete, sort,
/// bulk operations, CSV import/export and JSON export. Nothing is written until Save All, which saves
/// (and journals) each Pokémon whose moves changed.</summary>
public sealed class BulkLearnsetWindow : Window
{
    public sealed class Row(int pokemonId, string pokemonName, int level, int moveId) : INotifyPropertyChanged
    {
        private int level = level;
        private int moveId = moveId;

        public event PropertyChangedEventHandler? PropertyChanged;
        public int PokemonId { get; } = pokemonId;
        public string PokemonLabel { get; } = $"{pokemonId:D3} {pokemonName}";

        public int Level
        {
            get => level;
            set { level = Math.Clamp(value, 1, 100); PropertyChanged?.Invoke(this, new(nameof(Level))); }
        }

        public int MoveId
        {
            get => moveId;
            set { if (value > 0) moveId = value; PropertyChanged?.Invoke(this, new(nameof(MoveId))); }
        }
    }

    private static readonly IBrush Muted = new SolidColorBrush(Color.Parse("#AAB7C4"));
    private readonly RomProject project;
    private readonly string[] pokemonNames;
    private readonly string[] moveNames;
    private readonly int pokemonCount;
    private readonly Action onSaved;
    private readonly List<Row> rows = [];
    private readonly Dictionary<int, List<LearnsetEntry>> saved = [];
    private readonly ObservableCollection<Row> shown = [];
    private readonly ListBox list = new() { SelectionMode = SelectionMode.Multiple };
    private readonly TextBox filter = new() { Watermark = "Filter by Pokémon or move...", Width = 240 };
    private readonly TextBlock status = new() { Foreground = Muted, TextWrapping = TextWrapping.Wrap };
    private bool dirty;

    public BulkLearnsetWindow(RomProject project, string[] pokemonNames, string[] moveNames, Action onSaved)
    {
        this.project = project;
        this.pokemonNames = pokemonNames;
        this.moveNames = moveNames;
        this.onSaved = onSaved;
        Title = "Bulk Learnset Editor";
        Width = 900;
        Height = 700;
        MinWidth = 700;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Background = new SolidColorBrush(Color.Parse("#11161C"));
        Foreground = new SolidColorBrush(Color.Parse("#EDF2F7"));

        IReadOnlyList<int> ids = LearnsetService.GetIds(project);
        pokemonCount = ids.Count == 0 ? 0 : ids.Max() + 1;
        foreach (int id in ids)
        {
            List<LearnsetEntry> entries = LearnsetService.Load(project, id).Entries.Where(entry => entry.Move != 0).ToList();
            saved[id] = entries;
            rows.AddRange(entries.Select(entry => new Row(id, PokemonName(id), entry.Level, entry.Move)));
        }

        list.ItemsSource = shown;
        list.ItemTemplate = new FuncDataTemplate<Row>((row, _) => BuildRow(), supportsRecycling: true);
        filter.TextChanged += (_, _) => ApplyFilter();
        Closing += async (_, e) =>
        {
            if (!dirty) return;
            e.Cancel = true;
            if (await Ask("Unsaved changes", "Close without saving the learnset changes?", "Discard Changes"))
            {
                dirty = false;
                Close();
            }
        };

        var toolbar = new WrapPanel { Orientation = Orientation.Horizontal };
        void Add(Control control) { control.Margin = new Thickness(0, 0, 6, 6); toolbar.Children.Add(control); }
        Add(filter);
        Add(Button("Save All", SaveAll));
        Add(Button("Add Move", AddMove));
        Add(Button("Delete Selected", DeleteSelected));
        Add(Button("Sort", () => { SortRows(); MarkDirty("Sorted by Pokémon, then level."); }));
        Add(Menu("Bulk Operations",
            ("Copy the selected Pokémon's learnset to...", CopyLearnset),
            ("Remove a move from every learnset...", RemoveMoveEverywhere),
            ("Replace a move everywhere...", ReplaceMoveEverywhere),
            ("Change the levels of the selected moves...", AdjustLevels)));
        Add(Menu("Import / Export",
            ("Export CSV...", () => _ = ExportAsync(json: false)),
            ("Export JSON...", () => _ = ExportAsync(json: true)),
            ("Import CSV...", () => _ = ImportAsync())));

        var header = new Grid { ColumnDefinitions = new ColumnDefinitions("240,100,*"), Margin = new Thickness(8, 0, 0, 2) };
        header.Children.Add(new TextBlock { Text = "Pokémon", Foreground = Muted, FontSize = 12 });
        header.Children.Add(Placed(new TextBlock { Text = "Level", Foreground = Muted, FontSize = 12 }, 1));
        header.Children.Add(Placed(new TextBlock { Text = "Move", Foreground = Muted, FontSize = 12 }, 2));

        var layout = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 6, Margin = new Thickness(14) };
        layout.Children.Add(toolbar);
        layout.Children.Add(Placed(header, 0, 1));
        layout.Children.Add(Placed(new Border { BorderBrush = new SolidColorBrush(Color.Parse("#33414D")), BorderThickness = new Thickness(1), Child = list }, 0, 2));
        layout.Children.Add(Placed(status, 0, 3));
        Content = layout;
        ApplyFilter();
    }

    private string PokemonName(int id) => id < pokemonNames.Length ? pokemonNames[id] : $"#{id}";

    private Control BuildRow()
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("240,100,*"), ColumnSpacing = 8 };
        var name = new TextBlock { VerticalAlignment = VerticalAlignment.Center };
        name.Bind(TextBlock.TextProperty, new global::Avalonia.Data.Binding(nameof(Row.PokemonLabel)));
        var level = new NumericUpDown { Minimum = 1, Maximum = 100, Increment = 1, FormatString = "0", Width = 92 };
        level.Bind(NumericUpDown.ValueProperty, new global::Avalonia.Data.Binding(nameof(Row.Level)) { Mode = global::Avalonia.Data.BindingMode.TwoWay, Converter = NumericValueConverter.Instance });
        level.ValueChanged += (_, e) => { if (e.OldValue is not null && e.NewValue is not null && e.OldValue != e.NewValue) MarkDirty(); };
        var move = new ComboBox { ItemsSource = moveNames, HorizontalAlignment = HorizontalAlignment.Stretch };
        move.Bind(ComboBox.SelectedIndexProperty, new global::Avalonia.Data.Binding(nameof(Row.MoveId)) { Mode = global::Avalonia.Data.BindingMode.TwoWay });
        move.SelectionChanged += (_, e) => { if (e.RemovedItems.Count > 0 && e.AddedItems.Count > 0) MarkDirty(); };
        grid.Children.Add(name);
        grid.Children.Add(Placed(level, 1));
        grid.Children.Add(Placed(move, 2));
        return grid;
    }

    private static T Placed<T>(T control, int column, int row = 0) where T : Control
    {
        Grid.SetColumn(control, column);
        Grid.SetRow(control, row);
        return control;
    }

    private static Button Button(string label, Action action)
    {
        var button = new Button { Content = label, Padding = new Thickness(10, 5) };
        button.Click += (_, _) => action();
        return button;
    }

    private static Button Menu(string label, params (string Label, Action Action)[] items)
    {
        var flyout = new MenuFlyout();
        foreach ((string text, Action action) in items)
        {
            var item = new MenuItem { Header = text };
            item.Click += (_, _) => action();
            flyout.Items.Add(item);
        }

        return new Button { Content = label + " ▾", Padding = new Thickness(10, 5), Flyout = flyout };
    }

    private void ApplyFilter()
    {
        string query = filter.Text?.Trim() ?? string.Empty;
        shown.Clear();
        foreach (Row row in rows)
        {
            if (query.Length == 0 || row.PokemonLabel.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                (row.MoveId < moveNames.Length && moveNames[row.MoveId].Contains(query, StringComparison.OrdinalIgnoreCase)))
            {
                shown.Add(row);
            }
        }

        UpdateStatus();
    }

    private void UpdateStatus(string? message = null)
    {
        string counts = $"{rows.Count:N0} moves across {rows.Select(row => row.PokemonId).Distinct().Count()} Pokémon" +
            (shown.Count != rows.Count ? $", {shown.Count:N0} shown" : string.Empty) + (dirty ? " - not saved" : string.Empty) + ".";
        status.Text = message is null ? counts : $"{message} {counts}";
    }

    private void MarkDirty(string? message = null)
    {
        dirty = true;
        UpdateStatus(message);
    }

    private List<Row> SelectedRows => list.SelectedItems?.OfType<Row>().ToList() ?? [];

    private void SortRows()
    {
        List<Row> sorted = rows.OrderBy(row => row.PokemonId).ThenBy(row => row.Level).ToList();
        rows.Clear();
        rows.AddRange(sorted);
        ApplyFilter();
    }

    private void SaveAll()
    {
        var changed = new List<int>();
        Dictionary<int, List<LearnsetEntry>> current = rows.GroupBy(row => row.PokemonId)
            .ToDictionary(group => group.Key, group => group.OrderBy(row => row.Level).Select(row => new LearnsetEntry((byte)row.Level, (ushort)row.MoveId)).ToList());
        // Every Pokémon whose moves differ is saved - including one emptied here, which legacy skipped.
        foreach (int id in saved.Keys.Union(current.Keys).Order())
        {
            List<LearnsetEntry> now = current.TryGetValue(id, out List<LearnsetEntry>? entries) ? entries : [];
            // Compared in level order on both sides: some vanilla learnsets are not stored sorted, and an
            // untouched one must not be rewritten just to sort it.
            if (saved.TryGetValue(id, out List<LearnsetEntry>? before) && before.OrderBy(entry => entry.Level).SequenceEqual(now)) continue;
            LearnsetService.Save(project, new LearnsetFile(id, now));
            saved[id] = now;
            changed.Add(id);
        }

        dirty = false;
        UpdateStatus(changed.Count == 0 ? "Nothing to save." : $"Saved the learnsets of {changed.Count} Pokémon.");
        if (changed.Count > 0) onSaved();
    }

    private async void AddMove()
    {
        List<int> species = SelectedRows.Select(row => row.PokemonId).Distinct().ToList();
        int pokemon = species.Count == 1 ? species[0] : -1;
        if (pokemon < 0)
        {
            int? picked = await PickIndex("Add move", "Add a move to which Pokémon?", Enumerable.Range(0, pokemonCount).Select(id => $"{id:D3} {PokemonName(id)}").ToArray());
            if (picked is null) return;
            pokemon = picked.Value;
        }

        var row = new Row(pokemon, PokemonName(pokemon), 1, 1);
        int insertAt = rows.FindLastIndex(existing => existing.PokemonId == pokemon) + 1;
        if (insertAt == 0) insertAt = rows.FindIndex(existing => existing.PokemonId > pokemon) is int next and >= 0 ? next : rows.Count;
        rows.Insert(insertAt, row);
        filter.Text = string.Empty;
        ApplyFilter();
        list.SelectedItem = row;
        list.ScrollIntoView(row);
        MarkDirty($"Added a move to {PokemonName(pokemon)} - set its level and move.");
    }

    private void DeleteSelected()
    {
        List<Row> selected = SelectedRows;
        if (selected.Count == 0) return;
        foreach (Row row in selected) rows.Remove(row);
        ApplyFilter();
        MarkDirty($"Deleted {selected.Count} moves.");
    }

    private async void CopyLearnset()
    {
        List<int> species = SelectedRows.Select(row => row.PokemonId).Distinct().ToList();
        if (species.Count != 1)
        {
            UpdateStatus("Select moves of exactly one Pokémon to copy its learnset.");
            return;
        }

        string? targets = await AskText("Copy learnset", $"Copy {PokemonName(species[0])}'s learnset over which Pokémon? Give IDs, e.g. 1, 4, 10-15.");
        List<int>? ids = targets is null ? null : ParseIds(targets);
        if (ids is null || ids.Count == 0) return;

        List<Row> source = rows.Where(row => row.PokemonId == species[0]).ToList();
        foreach (int id in ids.Where(id => id != species[0]))
        {
            rows.RemoveAll(row => row.PokemonId == id);
            rows.AddRange(source.Select(row => new Row(id, PokemonName(id), row.Level, row.MoveId)));
        }

        SortRows();
        MarkDirty($"Copied {PokemonName(species[0])}'s {source.Count} moves to {ids.Count} Pokémon.");
    }

    private List<int>? ParseIds(string text)
    {
        var ids = new List<int>();
        foreach (string part in text.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            string[] range = part.Split('-', StringSplitOptions.TrimEntries);
            if (range.Length == 1 && int.TryParse(range[0], out int single) && single >= 0 && single < pokemonCount) ids.Add(single);
            else if (range.Length == 2 && int.TryParse(range[0], out int from) && int.TryParse(range[1], out int to) && from <= to && from >= 0 && to < pokemonCount) ids.AddRange(Enumerable.Range(from, to - from + 1));
            else
            {
                UpdateStatus($"'{part}' is not a Pokémon ID or range (0-{pokemonCount - 1}).");
                return null;
            }
        }

        return ids.Distinct().ToList();
    }

    private async void RemoveMoveEverywhere()
    {
        int? move = await PickIndex("Remove move", "Remove which move from every learnset?", moveNames, firstSelectable: 1);
        if (move is null) return;
        int removed = rows.RemoveAll(row => row.MoveId == move);
        ApplyFilter();
        MarkDirty($"Removed {moveNames[move.Value]} from {removed} learnset entries.");
    }

    private async void ReplaceMoveEverywhere()
    {
        int? from = await PickIndex("Replace move", "Replace which move?", moveNames, firstSelectable: 1);
        if (from is null) return;
        int? to = await PickIndex("Replace move", $"Replace {moveNames[from.Value]} with which move?", moveNames, firstSelectable: 1);
        if (to is null) return;
        int count = 0;
        foreach (Row row in rows.Where(row => row.MoveId == from))
        {
            row.MoveId = to.Value;
            count++;
        }

        MarkDirty($"Replaced {moveNames[from.Value]} with {moveNames[to.Value]} in {count} entries.");
    }

    private async void AdjustLevels()
    {
        List<Row> selected = SelectedRows;
        if (selected.Count == 0)
        {
            UpdateStatus("Select the moves whose levels to change first.");
            return;
        }

        string? answer = await AskText("Change levels", $"Change the level of {selected.Count} moves: +N adds, -N subtracts, N sets (levels stay 1-100).");
        if (answer is null || !int.TryParse(answer.Trim(), out int amount)) return;
        bool relative = answer.TrimStart().StartsWith('+') || answer.TrimStart().StartsWith('-');
        foreach (Row row in selected) row.Level = relative ? row.Level + amount : amount;
        MarkDirty($"Changed the level of {selected.Count} moves.");
    }

    private List<LearnsetRow> Snapshot() => rows.Select(row => new LearnsetRow(row.PokemonId, row.Level, row.MoveId)).ToList();

    private async Task ExportAsync(bool json)
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = json ? "Export learnsets as JSON" : "Export learnsets as CSV",
            SuggestedFileName = json ? "LearnsetData.json" : "LearnsetData.csv",
            FileTypeChoices = [new FilePickerFileType(json ? "JSON" : "CSV") { Patterns = [json ? "*.json" : "*.csv"] }]
        });
        if (file is null) return;
        string text = json
            ? LearnsetExchange.ToJson(Snapshot(), pokemonCount, pokemonNames, moveNames)
            : LearnsetExchange.ToCsv(Snapshot(), pokemonCount, pokemonNames, moveNames);
        await File.WriteAllTextAsync(file.Path.LocalPath, text);
        UpdateStatus($"Exported to {Path.GetFileName(file.Path.LocalPath)}" + (dirty ? " (including the changes not saved yet)." : "."));
    }

    private async Task ImportAsync()
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "Import learnsets from CSV",
            FileTypeFilter = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
        });
        if (files.Count != 1) return;

        LearnsetExchange.ImportResult result = LearnsetExchange.FromCsv(await File.ReadAllTextAsync(files[0].Path.LocalPath), pokemonCount, pokemonNames, moveNames);
        List<int> species = result.Rows.Select(row => row.PokemonId).Distinct().ToList();
        string problems = string.Join("\n", result.Errors.Take(8).Concat(result.Warnings.Take(Math.Max(0, 8 - result.Errors.Count))));
        string summary = $"{result.LinesRead} rows read: {result.Rows.Count(row => row.MoveId != 0)} moves for {species.Count} Pokémon, {result.Errors.Count} errors, {result.Warnings.Count} warnings." +
            (problems.Length > 0 ? $"\n\n{problems}" + (result.Errors.Count + result.Warnings.Count > 8 ? "\n..." : string.Empty) : string.Empty) +
            "\n\nThe learnsets of the Pokémon in the file are replaced; rows with errors are left out. Other Pokémon are not changed.";
        if (species.Count == 0)
        {
            await Ask("Nothing to import", summary, "OK");
            return;
        }

        if (!await Ask("Import learnsets", summary, "Import")) return;
        var replaced = species.ToHashSet();
        rows.RemoveAll(row => replaced.Contains(row.PokemonId));
        rows.AddRange(result.Rows.Where(row => row.MoveId != 0).Select(row => new Row(row.PokemonId, PokemonName(row.PokemonId), row.Level, row.MoveId)));
        SortRows();
        MarkDirty($"Imported the learnsets of {species.Count} Pokémon.");
    }

    private async Task<bool> Ask(string title, string message, string confirm) =>
        await new ChoiceDialog(title, message, ("yes", confirm), ("cancel", "Cancel")).ShowDialog<string?>(this) == "yes";

    private async Task<int?> PickIndex(string title, string prompt, string[] options, int firstSelectable = 0)
    {
        var picker = new ComboBox { ItemsSource = options, SelectedIndex = firstSelectable, HorizontalAlignment = HorizontalAlignment.Stretch };
        return await Prompt<int?>(title, prompt, picker, () => picker.SelectedIndex >= firstSelectable ? picker.SelectedIndex : null);
    }

    private async Task<string?> AskText(string title, string prompt)
    {
        var input = new TextBox();
        return await Prompt(title, prompt, input, () => string.IsNullOrWhiteSpace(input.Text) ? null : input.Text);
    }

    private async Task<T?> Prompt<T>(string title, string prompt, Control input, Func<T?> read)
    {
        var dialog = new Window
        {
            Title = title,
            Width = 460,
            SizeToContent = SizeToContent.Height,
            CanResize = false,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Background = Background,
            Foreground = Foreground
        };
        T? answer = default;
        var ok = new Button { Content = "OK", Padding = new Thickness(14, 6) };
        var cancel = new Button { Content = "Cancel", Padding = new Thickness(14, 6) };
        ok.Click += (_, _) => { answer = read(); dialog.Close(); };
        cancel.Click += (_, _) => dialog.Close();
        var buttons = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, HorizontalAlignment = HorizontalAlignment.Right };
        buttons.Children.Add(ok);
        buttons.Children.Add(cancel);
        dialog.Content = new StackPanel
        {
            Margin = new Thickness(16),
            Spacing = 12,
            Children = { new TextBlock { Text = prompt, TextWrapping = TextWrapping.Wrap }, input, buttons }
        };
        await dialog.ShowDialog(this);
        return answer;
    }
}
