using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Research Helper page: legacy's Research Helper window (DS_Map/ResearchHelper.cs).</summary>
public partial class MainWindow
{
    private ResearchService? research;
    private RomProject? researchProject;

    private sealed record ResearchScriptRow(ScriptFileStats Stats, string Name = "")
    {
        public override string ToString() => $"{Stats.Id,4}   {Stats.Total,6}  {Stats.Scripts,7}  {Stats.Functions,9}  {Stats.Actions,7}{(Name.Length > 0 ? "   " + Name : "")}";
    }

    private sealed record ResearchFileChoice(int Id, string Label)
    {
        public override string ToString() => Label;
    }

    private ResearchScriptRow NewResearchScriptRow(ScriptFileStats stats) =>
        new(stats, scriptFileNames.GetValueOrDefault(stats.Id, ""));

    private void RefreshResearchScriptNames()
    {
        if (research is null) return;
        int selected = ResearchScriptIdFile.SelectedIndex;
        ResearchScriptList.ItemsSource = research.ScriptStats.Select(NewResearchScriptRow).ToList();
        ResearchScriptIdFile.ItemsSource = research.ScriptFileIds.Select(id => new ResearchFileChoice(id, ScriptFileLabel(id, ""))).ToList();
        ResearchScriptIdFile.SelectedIndex = selected;
        UpdateScriptNameHints();
    }

    private sealed record ResearchLevelRow(LevelScriptStats Stats)
    {
        public override string ToString() => $"{Stats.Id,4}   {Stats.Total,6}  {Stats.MapChange,10}  {Stats.ScreenReset,12}  {Stats.LoadGame,9}  {Stats.VariableValue,14}";
    }

    private sealed record ResearchUsageRow(UsageResult Usage)
    {
        public override string ToString() => $"{Usage.FileType,-13} {Usage.FileId,4}  {Usage.Uses,5}  {Usage.Details}";
    }

    private sealed record ResearchHeaderRow(int HeaderId, string Text)
    {
        public override string ToString() => Text;
    }

    private sealed record ResearchEventRow(int EventFileId, string Text)
    {
        public override string ToString() => Text;
    }

    private sealed record ResearchHeaderInfoRow(string Property, string Value, Action? Open)
    {
        public override string ToString() => $"{Property,-18} {Value}";
    }

    private void LoadResearchPage()
    {
        if (research is not null && ReferenceEquals(researchProject, currentProject)) return;
        ReloadResearch();
    }

    private void ResearchRefresh_Click(object? sender, RoutedEventArgs e) => ReloadResearch();

    private void ReloadResearch()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        try
        {
            research = ResearchService.Load(currentProject, metadata, Path.Combine(AppContext.BaseDirectory, "databases"));
            researchProject = currentProject;
        }
        catch (Exception ex)
        {
            LogUiError("The Research Helper could not read the project", ex);
            ResearchStatus.Text = $"The project could not be read: {ex.Message}";
            research = null;
            return;
        }

        ResearchScriptList.ItemsSource = research.ScriptStats.Select(NewResearchScriptRow).ToList();
        ResearchLevelList.ItemsSource = research.LevelScriptStats.Select(stats => new ResearchLevelRow(stats)).ToList();
        ResearchScriptIdFile.ItemsSource = research.ScriptFileIds.Select(id => new ResearchFileChoice(id, ScriptFileLabel(id, ""))).ToList();
        ResearchScriptIdFile.SelectedIndex = research.ScriptFileIds.Count > 0 ? 0 : -1;
        ResearchHeaderValue.Maximum = Math.Max(0, research.HeaderCount - 1);
        ResearchStatus.Text = $"Read {research.ScriptStats.Count} script files, {research.LevelScriptStats.Count} level scripts, {research.EventFileCount} event files and {research.HeaderCount} headers." +
            (research.Problems.Count > 0 ? $" {research.Problems.Count} could not be read: {string.Join("; ", research.Problems.Take(3))}" : "");
    }

    private static Func<int, int, bool> ResearchComparison(ComboBox box) => box.SelectedIndex switch
    {
        0 => (value, target) => value == target,
        1 => (value, target) => value >= target,
        _ => (value, target) => value <= target
    };

    private void ResearchScriptFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (research is null) return;
        Func<ScriptFileStats, int> column = ResearchScriptFilterColumn.SelectedIndex switch
        {
            0 => s => s.Id,
            1 => s => s.Total,
            2 => s => s.Scripts,
            3 => s => s.Functions,
            _ => s => s.Actions
        };
        var compare = ResearchComparison(ResearchScriptFilterComparison);
        int target = (int)(ResearchScriptFilterValue.Value ?? 0);
        var rows = research.ScriptStats.Where(stats => compare(column(stats), target)).Select(NewResearchScriptRow).ToList();
        ResearchScriptList.ItemsSource = rows;
        ResearchStatus.Text = $"{rows.Count} script files match.";
    }

    private void ResearchScriptFilterClear_Click(object? sender, RoutedEventArgs e)
    {
        if (research is not null) ResearchScriptList.ItemsSource = research.ScriptStats.Select(stats => new ResearchScriptRow(stats)).ToList();
    }

    private void ResearchLevelFilter_Click(object? sender, RoutedEventArgs e)
    {
        if (research is null) return;
        Func<LevelScriptStats, int> column = ResearchLevelFilterColumn.SelectedIndex switch
        {
            0 => s => s.Id,
            1 => s => s.Total,
            2 => s => s.MapChange,
            3 => s => s.ScreenReset,
            4 => s => s.LoadGame,
            _ => s => s.VariableValue
        };
        var compare = ResearchComparison(ResearchLevelFilterComparison);
        int target = (int)(ResearchLevelFilterValue.Value ?? 0);
        var rows = research.LevelScriptStats.Where(stats => compare(column(stats), target)).Select(stats => new ResearchLevelRow(stats)).ToList();
        ResearchLevelList.ItemsSource = rows;
        ResearchStatus.Text = $"{rows.Count} level scripts match.";
    }

    private void ResearchLevelFilterClear_Click(object? sender, RoutedEventArgs e)
    {
        if (research is not null) ResearchLevelList.ItemsSource = research.LevelScriptStats.Select(stats => new ResearchLevelRow(stats)).ToList();
    }

    private void ResearchVariableSearch_Click(object? sender, RoutedEventArgs e)
    {
        if (research is null) return;
        if (ResearchService.ParseId(ResearchVariableBox.Text ?? "", ResearchVariableHex.IsChecked == true) is not int variable)
        {
            ResearchStatus.Text = "Type a variable number first.";
            return;
        }

        List<UsageResult> results = research.FindVariable(variable);
        ResearchVariableList.ItemsSource = results.Select(result => new ResearchUsageRow(result)).ToList();
        ResearchStatus.Text = $"Variable {variable} (0x{variable:X4}) is used in {results.Count} file(s).";
    }

    private void ResearchFlagSearch_Click(object? sender, RoutedEventArgs e)
    {
        if (research is null) return;
        if (ResearchService.ParseId(ResearchFlagBox.Text ?? "", ResearchFlagHex.IsChecked == true) is not int flag)
        {
            ResearchStatus.Text = "Type a flag number first.";
            return;
        }

        List<UsageResult> results = research.FindFlag(flag);
        ResearchFlagList.ItemsSource = results.Select(result => new ResearchUsageRow(result)).ToList();
        ResearchStatus.Text = $"Flag {flag} (0x{flag:X}): {results.Count} result(s).";
    }

    private void ResearchScriptFileSearch_Click(object? sender, RoutedEventArgs e)
    {
        if (research is null) return;
        int scriptFile = (int)(ResearchScriptFileValue.Value ?? 0);
        List<ScriptFileReference> results = research.FindScriptFileReferences(scriptFile);
        UpdateScriptNameHints();
        ResearchScriptFileList.ItemsSource = results.Select(r => new ResearchHeaderRow(r.HeaderId, $"{r.HeaderId,8}  {r.Field}")).ToList();
        ResearchStatus.Text = $"{results.Count} header reference(s) to script file {ScriptFileLabel(scriptFile, "")}.";
    }

    private void ResearchScriptIdFile_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (research is null || ResearchScriptIdFile.SelectedItem is not ResearchFileChoice { Id: var scriptFile }) return;
        ResearchScriptIdNumber.ItemsSource = Enumerable.Range(1, research.ScriptCount(scriptFile)).Select(n => SubscriptName(scriptFile, n) is { } name ? $"Script {n} - {name}" : $"Script {n}").ToList();
        ResearchScriptIdNumber.SelectedIndex = research.ScriptCount(scriptFile) > 0 ? 0 : -1;
    }

    private void ResearchScriptIdSearch_Click(object? sender, RoutedEventArgs e)
    {
        if (research is null || ResearchScriptIdFile.SelectedItem is not ResearchFileChoice { Id: var scriptFile } || ResearchScriptIdNumber.SelectedIndex < 0)
        {
            ResearchStatus.Text = "Pick a script file and one of its scripts first.";
            return;
        }

        int script = ResearchScriptIdNumber.SelectedIndex + 1;
        List<ScriptIdUse> results = research.FindScriptIdUses(scriptFile, script);
        ResearchScriptIdList.ItemsSource = results.Select(r => new ResearchEventRow(r.EventFileId, $"{r.EventFileId,12}  {r.EventType,-10}  {r.Index,5}  {r.Details}")).ToList();
        ResearchStatus.Text = $"Script {script} of script file {ScriptFileLabel(scriptFile, "")} is run by {results.Count} event(s) in the event files of the headers using that script file.";
    }

    private void ResearchHeaderShow_Click(object? sender, RoutedEventArgs e)
    {
        if (research is null) return;
        int headerId = (int)(ResearchHeaderValue.Value ?? 0);
        if (research.LoadHeader(headerId) is not MapHeaderRecord header)
        {
            ResearchStatus.Text = $"There is no header {headerId}.";
            return;
        }

        ResearchHeaderInfoList.ItemsSource = new List<ResearchHeaderInfoRow>
        {
            new("Header ID", headerId.ToString(), () => OpenHeaderFromSearch((ushort)headerId)),
            new("Script file", header.ScriptFileId.ToString(), () => OpenScriptFromSearch(header.ScriptFileId, null)),
            new("Level script", header.LevelScriptId.ToString(), () => OpenLevelScriptFromSearch(header.LevelScriptId)),
            new("Event file", header.EventFileId.ToString(), () => OpenHeaderFromSearch((ushort)headerId)),
            new("Text archive", header.TextArchiveId.ToString(), () => OpenTextArchiveFromSearch(header.TextArchiveId, 0)),
            new("Matrix", header.MatrixId.ToString(), null),
            new("Area data", header.AreaDataId.ToString(), null),
            new("Camera angle", header.CameraAngleId.ToString(), null),
            new("Music (day)", header.MusicDayId.ToString(), null),
            new("Music (night)", header.MusicNightId.ToString(), null),
            new("Weather", header.WeatherId.ToString(), null),
            new("Wild Pokémon", header.WildPokemonId.ToString(), null),
            new("Location name", header.LocationNameId.ToString(), null),
            new("Flags", $"0x{header.Flags:X2}", null),
        };

        List<WarpLink> incoming = research.IncomingWarps(headerId);
        ResearchIncomingList.ItemsSource = incoming.Select(w => new ResearchEventRow(w.EventFileId,
            $"{(research.HeaderForEventFile(w.EventFileId) is int from ? from.ToString() : "-"),11}  {w.EventFileId,10}  {w.WarpIndex,4}  {$"({w.X}, {w.Y})",-10}  {w.Anchor,15}")).ToList();
        List<WarpLink> outgoing = research.OutgoingWarps(header);
        ResearchOutgoingList.ItemsSource = outgoing.Select(w => new ResearchHeaderRow(w.Header, $"{w.WarpIndex,6}  {$"({w.X}, {w.Y})",-10}  {w.Header,9}  {w.Anchor,15}")).ToList();
        ResearchStatus.Text = $"Header {headerId}: {incoming.Count} incoming and {outgoing.Count} outgoing warp(s).";
    }

    private void ResearchScriptList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ResearchScriptList.SelectedItem is ResearchScriptRow row) OpenScriptFromSearch(row.Stats.Id, null);
    }

    private void ResearchLevelList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ResearchLevelList.SelectedItem is ResearchLevelRow row) OpenLevelScriptFromSearch(row.Stats.Id);
    }

    private void ResearchUsageList_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is not ResearchUsageRow { Usage: var usage }) return;
        switch (usage.FileType)
        {
            case "Script": OpenScriptFromSearch(usage.FileId, null); break;
            case "Level Script": OpenLevelScriptFromSearch(usage.FileId); break;
            default: OpenEventFileFromResearch(usage.FileId); break;
        }
    }

    private void ResearchHeaderRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is ResearchHeaderRow row) OpenHeaderFromSearch((ushort)row.HeaderId);
    }

    private void ResearchOutgoing_DoubleTapped(object? sender, TappedEventArgs e) => ResearchHeaderRow_DoubleTapped(sender, e);

    private void ResearchEventRow_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if ((sender as ListBox)?.SelectedItem is ResearchEventRow row) OpenEventFileFromResearch(row.EventFileId);
    }

    private void ResearchHeaderInfo_DoubleTapped(object? sender, TappedEventArgs e)
    {
        if (ResearchHeaderInfoList.SelectedItem is ResearchHeaderInfoRow { Open: { } open }) open();
    }

    private void ResearchAddressFind_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null) return;
        if (ResearchService.ParseId(ResearchAddressBox.Text ?? "", hex: true) is not int address || address < 0)
        {
            ResearchStatus.Text = "Type a hex RAM address first, e.g. 0x021E5A10.";
            return;
        }

        try
        {
            List<(string Where, uint Offset)> found = OverlayService.LocateRamAddress(currentProject, (uint)address);
            ResearchAddressList.ItemsSource = found.Select(f => $"{f.Where,-20}  0x{f.Offset:X}").ToList();
            ResearchStatus.Text = found.Count == 0 ? $"0x{address:X8} is not in ARM9, any overlay or the synthetic overlay." : $"0x{address:X8} is in {found.Count} file(s).";
        }
        catch (Exception ex)
        {
            LogUiError("The address could not be looked up", ex);
            ResearchStatus.Text = $"The overlay table could not be read: {ex.Message}";
        }
    }

    /// <summary>Event files are edited from the Map Editor, so this opens the first header using it.</summary>
    private void OpenEventFileFromResearch(int eventFileId)
    {
        if (research?.HeaderForEventFile(eventFileId) is int headerId)
        {
            OpenHeaderFromSearch((ushort)headerId);
        }
        else
        {
            ResearchStatus.Text = $"No header uses event file {eventFileId}, so it cannot be opened in the Map Editor.";
        }
    }
}

public partial class MainWindow
{
    /// <summary>Shows the name global.lua gives the script file typed in the header editor and in the Research
    /// "Headers using script file" search, beside the number.</summary>
    private void UpdateScriptNameHints()
    {
        string Name(int? id) => id is int file && scriptFileNames.TryGetValue(file, out string? name) ? name : string.Empty;
        if (ResearchScriptFileName is not null) ResearchScriptFileName.Text = Name((int?)ResearchScriptFileValue.Value);
    }

    private void ScriptFileNumber_Changed(object? sender, NumericUpDownValueChangedEventArgs e) => UpdateScriptNameHints();
}
