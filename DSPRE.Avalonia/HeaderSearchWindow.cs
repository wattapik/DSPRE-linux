using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>Lists the headers whose chosen property matches a value - the legacy DS_Map/HeaderSearch.cs
/// "Advanced Header Search" dialog, except that results are shown here instead of replacing the Header
/// Editor's own list. Double-clicking a result opens that header in the editor.</summary>
public sealed class HeaderSearchWindow : Window
{
    public sealed record HeaderEntry(ushort Id, string InternalName, string Display)
    {
        public override string ToString() => Display;
    }

    // A null Value marks the one text field.
    private static readonly (string Label, Func<MapHeaderRecord, int>? Value)[] Fields =
    [
        ("Area Data (ID)", header => header.AreaDataId),
        ("Camera Angle (ID)", header => header.CameraAngleId),
        ("Event File (ID)", header => header.EventFileId),
        ("Internal Name", null),
        ("Level Script (ID)", header => header.LevelScriptId),
        ("Location Name (ID)", header => header.LocationNameId),
        ("Matrix (ID)", header => header.MatrixId),
        ("Music Day (ID)", header => header.MusicDayId),
        ("Music Night (ID)", header => header.MusicNightId),
        ("Script File (ID)", header => header.ScriptFileId),
        ("Text Archive (ID)", header => header.TextArchiveId),
        ("Weather (ID)", header => header.WeatherId),
        ("Wild Pokemon (ID)", header => header.WildPokemonId)
    ];

    private static readonly string[] NumberOperators =
        ["Equals", "Is Different than", "Is Less than", "Is Greater than", "Is Less than or Equal to", "Is Greater than or Equal to"];

    private static readonly string[] TextOperators = ["Contains", "Does not contain", "Is Exactly", "Is Not"];

    private readonly ComboBox fieldSelector = new() { Width = 190, Margin = new Thickness(0, 0, 8, 8) };
    private readonly ComboBox operatorSelector = new() { Width = 230, Margin = new Thickness(0, 0, 8, 8) };
    private readonly TextBox valueInput = new() { Width = 150, Margin = new Thickness(0, 0, 8, 8), Watermark = "Value..." };
    private readonly ListBox results = new();
    private readonly TextBlock status = new() { Foreground = new SolidColorBrush(Color.Parse("#AAB7C4")), TextWrapping = TextWrapping.Wrap };

    private readonly IReadOnlyList<HeaderEntry> headers;
    private readonly Func<ushort, MapHeaderRecord> loadHeader;
    private bool updatingOperators;

    public HeaderSearchWindow(IReadOnlyList<HeaderEntry> headers, Func<ushort, MapHeaderRecord> loadHeader, Action<ushort> openHeader)
    {
        this.headers = headers;
        this.loadHeader = loadHeader;

        Title = "Search Headers by Property";
        Width = 720;
        Height = 520;
        MinWidth = 480;
        MinHeight = 300;
        Background = new SolidColorBrush(Color.Parse("#11161C"));
        Foreground = new SolidColorBrush(Color.Parse("#EDF2F7"));

        var openButton = new Button { Content = "Open Header", Padding = new Thickness(12, 6), HorizontalAlignment = HorizontalAlignment.Left };
        void OpenSelected()
        {
            if (results.SelectedItem is HeaderEntry entry) openHeader(entry.Id);
        }

        openButton.Click += (_, _) => OpenSelected();
        results.DoubleTapped += (_, _) => OpenSelected();

        var criteria = new WrapPanel { Orientation = Orientation.Horizontal };
        criteria.Children.Add(fieldSelector);
        criteria.Children.Add(operatorSelector);
        criteria.Children.Add(valueInput);

        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto"), RowSpacing = 8, Margin = new Thickness(14) };
        root.Children.Add(criteria);
        Grid.SetRow(status, 1);
        root.Children.Add(status);
        Grid.SetRow(results, 2);
        root.Children.Add(results);
        Grid.SetRow(openButton, 3);
        root.Children.Add(openButton);
        Content = root;

        foreach ((string label, _) in Fields) fieldSelector.Items.Add(label);
        fieldSelector.SelectionChanged += (_, _) =>
        {
            UpdateOperators();
            Search();
        };
        operatorSelector.SelectionChanged += (_, _) =>
        {
            if (!updatingOperators) Search();
        };
        valueInput.TextChanged += (_, _) => Search();
        fieldSelector.SelectedIndex = 0;
        Opened += (_, _) => valueInput.Focus();
    }

    private void UpdateOperators()
    {
        if (fieldSelector.SelectedIndex < 0) return;
        updatingOperators = true;
        operatorSelector.Items.Clear();
        foreach (string label in Fields[fieldSelector.SelectedIndex].Value is null ? TextOperators : NumberOperators)
        {
            operatorSelector.Items.Add(label);
        }

        operatorSelector.SelectedIndex = 0;
        updatingOperators = false;
    }

    private void Search()
    {
        results.Items.Clear();
        string value = valueInput.Text?.Trim() ?? string.Empty;
        int field = fieldSelector.SelectedIndex;
        int operation = operatorSelector.SelectedIndex;
        if (value.Length == 0 || field < 0 || operation < 0)
        {
            status.Text = "Enter a value to search for. Double-click a result to open it in the Header Editor.";
            return;
        }

        (string label, Func<MapHeaderRecord, int>? read) = Fields[field];
        Func<HeaderEntry, bool> matches;
        string description;
        if (read is null)
        {
            description = $"{label} {TextOperators[operation].ToLowerInvariant()} \"{value}\"";
            matches = operation switch
            {
                0 => header => header.InternalName.Contains(value, StringComparison.OrdinalIgnoreCase),
                1 => header => !header.InternalName.Contains(value, StringComparison.OrdinalIgnoreCase),
                2 => header => header.InternalName.Equals(value, StringComparison.Ordinal),
                _ => header => !header.InternalName.Equals(value, StringComparison.Ordinal)
            };
        }
        else
        {
            if (!ushort.TryParse(value, out ushort number))
            {
                status.Text = $"\"{value}\" is not a whole number between 0 and {ushort.MaxValue}.";
                return;
            }

            description = $"{label} {NumberOperators[operation].ToLowerInvariant()} {number}";
            Func<int, bool> compare = operation switch
            {
                0 => actual => actual == number,
                1 => actual => actual != number,
                2 => actual => actual < number,
                3 => actual => actual > number,
                4 => actual => actual <= number,
                _ => actual => actual >= number
            };
            matches = header => compare(read(loadHeader(header.Id)));
        }

        try
        {
            foreach (HeaderEntry header in headers)
            {
                if (matches(header)) results.Items.Add(header);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Header search failed: {ex.Message}");
            status.Text = ex.Message;
            return;
        }

        status.Text = results.Items.Count == 0 ? $"No header's {description}." : $"{results.Items.Count} headers whose {description}.";
    }
}
