using Avalonia.Controls;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using DSPRE.Core;
using DSPRE.Core.Lua;
using System.IO;

namespace DSPRE.Avalonia;

/// <summary>The Variable and Flag Editor: every variable and flag the scripts can use in a grid, with the name
/// global.lua gives each. Names are edited here and written into global.lua (and renamed in the Lua script
/// files that use them) when saved.</summary>
public partial class MainWindow
{
    private const int FirstGeneralVariable = 0x4000, GeneralVariableCount = 0x100;
    private const int FirstSpecialVariable = 0x8000, SpecialVariableCount = 0x20;
    private const int FlagCount = 3000;

    private sealed class VarFlagRow(bool isFlag, int id, string original)
    {
        public bool IsFlag { get; } = isFlag;
        public int Id { get; } = id;
        public string Original { get; } = original;
        public string Name { get; set; } = original;
        public bool Edited => Name != Original;
        public string Label => IsFlag ? $"{Id,5}   0x{Id:X4}" : $"0x{Id:X4}   {Id,5}";
    }

    private List<VarFlagRow> variableRows = [];
    private List<VarFlagRow> flagRows = [];
    private bool variableFlagEditorLoaded;

    private void ConfigureVariableFlagEditor()
    {
        if (currentProject is null) return;
        ScriptLanguage language = GetScriptLanguage();
        string? global = LuaScriptService.LoadGlobal(currentProject);
        LuaNames names = LuaScriptCompiler.FindNames(global, language);

        IEnumerable<int> variableIds = Enumerable.Range(FirstGeneralVariable, GeneralVariableCount)
            .Concat(Enumerable.Range(FirstSpecialVariable, SpecialVariableCount))
            .Concat(names.Variables.Keys.Select(id => (int)id)).Distinct().Order();
        IEnumerable<int> flagIds = Enumerable.Range(0, FlagCount).Concat(names.Flags.Keys.Select(id => (int)id)).Distinct().Order();
        variableRows = variableIds.Select(id => new VarFlagRow(false, id, names.Variables.GetValueOrDefault(id, ""))).ToList();
        flagRows = flagIds.Select(id => new VarFlagRow(true, id, names.Flags.GetValueOrDefault(id, ""))).ToList();

        if (!variableFlagEditorLoaded)
        {
            variableFlagEditorLoaded = true;
            VariableList.ItemTemplate = RowTemplate();
            FlagList.ItemTemplate = RowTemplate();
        }

        VariableFlagStatus.Text = global is null
            ? "This project has no global.lua yet; saving a name creates it."
            : $"{names.Variables.Count} variable name{(names.Variables.Count == 1 ? "" : "s")} and {names.Flags.Count} flag name{(names.Flags.Count == 1 ? "" : "s")} in global.lua.";
        RefreshVariableFlagLists();
    }

    private FuncDataTemplate<VarFlagRow> RowTemplate() => new((row, _) =>
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("170,*"), Margin = new global::Avalonia.Thickness(0, 1) };
        var label = new TextBlock { Text = row?.Label, FontFamily = new FontFamily("monospace"), VerticalAlignment = VerticalAlignment.Center, Foreground = new SolidColorBrush(Color.Parse("#AAB7C4")) };
        var box = new TextBox { Text = row?.Name, Watermark = "(no name)", MaxLength = 64, Padding = new global::Avalonia.Thickness(6, 3) };
        if (row is not null)
        {
            box.Foreground = row.Edited ? new SolidColorBrush(Color.Parse("#E8B75F")) : null;
            box.LostFocus += (_, _) => CommitVariableFlagName(row, box);
            box.KeyDown += (_, e) =>
            {
                if (e.Key == Key.Enter) CommitVariableFlagName(row, box);
                else if (e.Key == Key.Escape) box.Text = row.Name;
            };
        }

        Grid.SetColumn(box, 1);
        grid.Children.Add(label);
        grid.Children.Add(box);
        return grid;
    });

    private void CommitVariableFlagName(VarFlagRow row, TextBox box)
    {
        string name = (box.Text ?? "").Trim();
        if (name == row.Name) return;
        if (name.Length > 0)
        {
            if (!VariableFlagNames.IsValidName(name))
            {
                VariableFlagStatus.Text = $"\"{name}\" is not a usable name: letters, digits and _, not starting with a digit, and not a Lua word.";
                box.Text = row.Name;
                return;
            }

            VarFlagRow? taken = variableRows.Concat(flagRows).FirstOrDefault(other => !ReferenceEquals(other, row) && other.Name == name);
            if (taken is not null || (currentProject is not null && GetScriptLanguage().FindCommand(name) is not null))
            {
                VariableFlagStatus.Text = taken is not null
                    ? $"\"{name}\" already names {(taken.IsFlag ? "flag" : "variable")} {taken.Id}."
                    : $"\"{name}\" is a script command's name.";
                box.Text = row.Name;
                return;
            }
        }

        row.Name = name;
        box.Foreground = row.Edited ? new SolidColorBrush(Color.Parse("#E8B75F")) : null;
        VariableFlagStatus.Text = "Names changed here are written to global.lua with Save Names (or Save All).";
        RefreshUnsavedMarkers();
    }

    private void RefreshVariableFlagLists()
    {
        VariableList.ItemsSource = FilterRows(variableRows, VariableSearchInput.Text, VariableNamedOnly.IsChecked == true);
        FlagList.ItemsSource = FilterRows(flagRows, FlagSearchInput.Text, FlagNamedOnly.IsChecked == true);
    }

    private static List<VarFlagRow> FilterRows(List<VarFlagRow> rows, string? query, bool namedOnly)
    {
        query = query?.Trim() ?? "";
        return rows.Where(row => (!namedOnly || row.Name.Length > 0 || row.Edited) && (query.Length == 0 ||
            row.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
            row.Id.ToString().Contains(query, StringComparison.Ordinal) ||
            $"0x{row.Id:X4}".Contains(query, StringComparison.OrdinalIgnoreCase) ||
            row.Id.ToString("X").Contains(query.Replace("0x", "", StringComparison.OrdinalIgnoreCase), StringComparison.OrdinalIgnoreCase))).ToList();
    }

    private void VariableFlagFilter_Changed(object? sender, RoutedEventArgs e)
    {
        if (VariableList is null || FlagList is null) return;
        RefreshVariableFlagLists();
    }

    private List<VariableFlagNames.Edit> PendingVariableFlagEdits() => variableRows.Concat(flagRows)
        .Where(row => row.Edited)
        .Select(row => new VariableFlagNames.Edit(row.IsFlag, row.Id, row.Original.Length > 0 ? row.Original : null, row.Name.Length > 0 ? row.Name : null))
        .ToList();

    private void SaveVariableFlagNames_Click(object? sender, RoutedEventArgs e) => SaveVariableFlagNames();

    private void SaveVariableFlagNames()
    {
        if (currentProject is null) return;
        List<VariableFlagNames.Edit> edits = PendingVariableFlagEdits();
        if (edits.Count == 0)
        {
            VariableFlagStatus.Text = "No name changes to save.";
            return;
        }

        if ((GlobalLuaTextBox.Text ?? "") != savedGlobalLua)
        {
            VariableFlagStatus.Text = "global.lua has unsaved edits in the Script Editor; save or discard them first.";
            return;
        }

        List<int> changed;
        try
        {
            changed = VariableFlagNames.Apply(currentProject, edits);
        }
        catch (InvalidOperationException ex)
        {
            VariableFlagStatus.Text = ex.Message;
            return;
        }

        savedGlobalLua = LuaScriptService.LoadGlobal(currentProject) ?? "";
        GlobalLuaTextBox.Text = savedGlobalLua;
        Dictionary<string, string> renames = VariableFlagNames.RenameMap(edits);
        if (renames.Count > 0)
        {
            loadingScript = true;
            try
            {
                LuaTextBox.Text = VariableFlagNames.RenameInLua(LuaTextBox.Text ?? "", renames);
            }
            finally
            {
                loadingScript = false;
            }
        }

        string note = changed.Count > 0 ? $" Renamed in script file{(changed.Count == 1 ? "" : "s")} {string.Join(", ", changed.Take(8).Select(id => id.ToString("D4")))}{(changed.Count > 8 ? "..." : "")}." : "";
        ConfigureVariableFlagEditor();
        VariableFlagStatus.Text = $"Saved {edits.Count} name change{(edits.Count == 1 ? "" : "s")} to global.lua.{note}";
        RefreshUnsavedMarkers();
    }

    private void CollectUnsavedVariableFlagChanges(List<UnsavedChange> changes)
    {
        List<string> details = variableRows.Concat(flagRows).Where(row => row.Edited)
            .Select(row => $"{(row.IsFlag ? "Flag" : "Variable")} {(row.IsFlag ? row.Id.ToString() : $"0x{row.Id:X4}")}: {(row.Original.Length > 0 ? row.Original : "(no name)")} -> {(row.Name.Length > 0 ? row.Name : "(no name)")}")
            .ToList();
        if (details.Count > 0) changes.Add(new UnsavedChange("Variable and Flag Editor", "Variable and flag names", details, SaveVariableFlagNames));
    }

    private void ResetVariableFlagEditor()
    {
        variableRows = [];
        flagRows = [];
        if (VariableList is not null) VariableList.ItemsSource = null;
        if (FlagList is not null) FlagList.ItemsSource = null;
    }
}
