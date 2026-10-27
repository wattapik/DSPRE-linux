using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Trade Editor's "Used By" list: the scripts that start the trade and the maps that load
/// them - see TradeDataService.FindUses.</summary>
public partial class MainWindow
{
    private void ShowTradeUses(int tradeId)
    {
        TradeUsesPanel.Children.Clear();
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;

        List<TradeUse> uses;
        try
        {
            uses = TradeDataService.FindUses(currentProject, metadata, tradeId);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            TradeUsesPanel.Children.Add(Note($"The scripts could not be searched: {ex.Message}"));
            return;
        }

        if (uses.Count == 0)
        {
            TradeUsesPanel.Children.Add(Note($"No script starts this trade (no \"InitTrade {tradeId}\" in the scripts)."));
            return;
        }

        foreach (TradeUse use in uses)
        {
            var row = new WrapPanel { Orientation = Orientation.Horizontal };
            row.Children.Add(new TextBlock
            {
                Text = $"Script file {use.ScriptFile:D4}{(use.Section.Length > 0 ? $", {use.Section}" : "")}",
                VerticalAlignment = VerticalAlignment.Center,
                Margin = new Thickness(0, 0, 10, 0)
            });
            int scriptFile = use.ScriptFile;
            row.Children.Add(LinkButton("Open Script", () => OpenScriptFromSearch(scriptFile, $"InitTrade {tradeId}")));
            if (use.Maps.Count == 0)
            {
                row.Children.Add(new TextBlock { Text = "no map loads this script file", Foreground = new SolidColorBrush(Color.Parse("#5F7282")), VerticalAlignment = VerticalAlignment.Center });
            }

            foreach ((int header, string name) in use.Maps)
            {
                ushort headerId = (ushort)header;
                row.Children.Add(LinkButton($"Map {header:D4} {name}", () => OpenHeaderFromSearch(headerId)));
            }

            TradeUsesPanel.Children.Add(row);
        }

        static TextBlock Note(string text) => new() { Text = text, Foreground = new SolidColorBrush(Color.Parse("#5F7282")), TextWrapping = TextWrapping.Wrap };

        static Button LinkButton(string text, Action open)
        {
            var button = new Button { Content = text, Padding = new Thickness(8, 3), Margin = new Thickness(0, 0, 6, 2) };
            button.Click += (_, _) => open();
            return button;
        }
    }
}
