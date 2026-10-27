using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>Code-only port of the legacy MoveDataImportPreviewForm's core review-before-apply flow
/// (Summary/Errors/Changes tabs). The legacy dialog's extra "Name Mismatches -&gt; rename in ROM" tooling
/// is not ported - out of scope for a data editor.</summary>
public sealed class MoveDataImportPreviewWindow : Window
{
    public bool Confirmed { get; private set; }

    public MoveDataImportPreviewWindow(MoveDataCsvImportResult result, IReadOnlyList<MoveDataCsvChange> changes)
    {
        Title = "Move Data Import Preview";
        Width = 780;
        Height = 560;

        TextBox summaryBox = MakeReadOnlyTextBox();
        TextBox errorsBox = MakeReadOnlyTextBox();
        TextBox changesBox = MakeReadOnlyTextBox();

        var tabs = new TabControl();
        tabs.Items.Add(new TabItem { Header = "Summary", Content = summaryBox });
        tabs.Items.Add(new TabItem { Header = $"Errors ({result.Errors.Count})", Content = errorsBox });
        tabs.Items.Add(new TabItem { Header = $"Changes ({changes.Count})", Content = changesBox });

        var applyButton = new Button { Content = "Apply Changes", Padding = new Thickness(14, 8), IsEnabled = result.ValidEntries.Count > 0 };
        applyButton.Click += (_, _) => { Confirmed = true; Close(); };

        var cancelButton = new Button { Content = "Cancel", Padding = new Thickness(14, 8), Margin = new Thickness(8, 0, 0, 0) };
        cancelButton.Click += (_, _) => { Confirmed = false; Close(); };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 8, 0, 0)
        };
        buttonPanel.Children.Add(cancelButton);
        buttonPanel.Children.Add(applyButton);

        var root = new Grid { RowDefinitions = new RowDefinitions("*,Auto"), Margin = new Thickness(12) };
        Grid.SetRow(tabs, 0);
        Grid.SetRow(buttonPanel, 1);
        root.Children.Add(tabs);
        root.Children.Add(buttonPanel);
        Content = root;

        var summary = new StringBuilder();
        summary.AppendLine($"Total rows read: {result.TotalRowsRead}");
        summary.AppendLine($"Valid entries: {result.ValidEntries.Count}");
        summary.AppendLine($"Moves with changes: {changes.Count}");
        summary.AppendLine($"Moves unchanged: {result.ValidEntries.Count - changes.Count}");
        summary.AppendLine($"Errors found: {result.Errors.Count}");
        summary.AppendLine();
        summary.Append(result.Errors.Count > 0
            ? "Errors were found - review the Errors tab before applying."
            : changes.Count == 0
                ? "No changes detected - import data matches current ROM data."
                : "All entries validated successfully.");
        summaryBox.Text = summary.ToString();

        errorsBox.Text = result.Errors.Count == 0 ? "No errors." : string.Join("\n", result.Errors);

        if (changes.Count == 0)
        {
            changesBox.Text = "No changes detected - import data matches current ROM data.";
        }
        else
        {
            var changesText = new StringBuilder();
            foreach (MoveDataCsvChange change in changes)
            {
                changesText.AppendLine($"[{change.MoveId:D3}] {change.MoveName}");
                foreach (string field in change.ChangedFields) changesText.AppendLine($"    {field}");
                changesText.AppendLine();
            }

            changesBox.Text = changesText.ToString();
        }
    }

    private static TextBox MakeReadOnlyTextBox() => new()
    {
        IsReadOnly = true,
        AcceptsReturn = true,
        TextWrapping = TextWrapping.NoWrap,
        FontFamily = new FontFamily("Consolas,monospace"),
        VerticalContentAlignment = VerticalAlignment.Top,
        Margin = new Thickness(0, 8, 0, 0)
    };
}
