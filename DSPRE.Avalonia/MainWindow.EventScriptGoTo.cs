using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using Avalonia.VisualTree;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>"Go to script" beside an event's script number: opens the header's script file in the Script Editor on the line where
/// that script starts, in the Lua tab when the file is written in Lua and in the Scripts tab otherwise.</summary>
public partial class MainWindow
{
    private async void EventGoToScript_Click(object? sender, RoutedEventArgs e)
    {
        NumericUpDown? field = (sender as Control)?.Tag switch
        {
            "Overworld" => EventOverworldScriptNumber,
            "Trigger" => EventTriggerScriptNumber,
            "Spawnable" => EventSpawnableScriptNumber,
            _ => null,
        };
        if (field is null) return;
        if (selectedHeader is null)
        {
            StatusText.Text = "Select a header first: an event's script number is a script of that header's script file.";
            return;
        }

        int number = (int)(field.Value ?? 0);
        if (SelectedMapEvent is SpawnableEvent { Type: SpawnableEvent.TypeHiddenItem } && number >= HiddenItemUsageService.ScriptBase)
        {
            StatusText.Text = $"{number} is a hidden item (entry {number - HiddenItemUsageService.ScriptBase} of the Hidden Items table), not a script.";
            return;
        }

        await GoToScriptLineAsync(selectedHeader.ScriptFileId, number);
    }

    /// <summary>Opens script file <paramref name="scriptFileId"/> in the Script Editor and moves to script <paramref name="number"/>.
    /// Moving to another file may ask about unsaved changes first; nothing happens if that is cancelled.</summary>
    private async Task GoToScriptLineAsync(int scriptFileId, int number)
    {
        SelectEditor("Script Editor");
        if (scriptFileIds.Count == 0) ConfigureScriptEditor();
        int index = scriptFileIds.IndexOf(scriptFileId);
        if (index < 0)
        {
            ScriptSearchStatus.Text = $"Script file {scriptFileId:D4} was not exported.";
            return;
        }

        if (ScriptFileSelector.SelectedIndex != index) ScriptFileSelector.SelectedIndex = index;

        // The file loads once any unsaved-changes question is answered; the selector going back means it was refused.
        for (int waited = 0; loadedScriptFileIndex != index; waited++)
        {
            if (ScriptFileSelector.SelectedIndex != index || waited > 3000) return;
            await Task.Delay(20);
        }

        ShowScriptLine(scriptFileId, number);
    }

    private void ShowScriptLine(int scriptFileId, int number)
    {
        bool lua = scriptUsesLua;
        if (lua && GlobalLuaToggle.IsChecked == true) ShowGlobalLua(false);
        TextBox box = lua ? LuaTextBox : ScriptsTextBox;
        ScriptSectionTabs.SelectedItem = lua ? LuaTab : ScriptsTab;

        string[] lines = (box.Text ?? "").Replace("\r\n", "\n").Split('\n');
        var luaStart = new Regex($@"^\s*script\s*\(\s*{number}\s*[,)]");
        string marker = $"Script {number}:";
        int line = Array.FindIndex(lines, text => lua ? luaStart.IsMatch(text) : text.Trim() == marker);
        string file = lua ? $"{scriptFileId:D4}.lua" : $"{scriptFileId:D4}.script";
        if (line < 0)
        {
            ScriptSearchStatus.Text = $"Script {number} is not in {file}; it may be a script shared by several files.";
            return;
        }

        int start = lines.Take(line).Sum(text => text.Length + 1);
        box.CaretIndex = start;
        box.SelectionStart = start;
        box.SelectionEnd = start + lines[line].Length;
        ScriptSearchStatus.Text = $"Script {number} is on line {line + 1} of {file}.";
        // The text boxes grow to their text inside a ScrollViewer of their own, so the viewer is what moves.
        Dispatcher.UIThread.Post(() =>
        {
            box.Focus();
            if (box.FindAncestorOfType<ScrollViewer>() is not { } scroller || lines.Length == 0) return;
            double lineHeight = (box.Bounds.Height - box.Padding.Top - box.Padding.Bottom) / lines.Length;
            double top = box.Padding.Top + line * lineHeight;
            scroller.Offset = new Vector(scroller.Offset.X, Math.Max(0, top - scroller.Viewport.Height / 3));
        }, DispatcherPriority.Background);
    }
}
