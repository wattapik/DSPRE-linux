using Avalonia.Controls;
using DSPRE.Core;
using System.IO;

namespace DSPRE.Avalonia;

/// <summary>The Header pane picks the files a header uses (area data, matrix, script file, level script, wild
/// encounters) from lists of what the project has, instead of by typed number.</summary>
public partial class MainWindow
{
    private sealed record IdChoice(int Id, string Label)
    {
        public override string ToString() => Label;
    }

    private bool headerIdCombosStale = true;

    private void FillHeaderIdCombos()
    {
        if (!headerIdCombosStale || currentProject is null) return;
        headerIdCombosStale = false;
        EnsureMatrixSelectorLoaded();

        FillIdCombo(AreaDataInput, AreaDataService.GetIds(currentProject).Select(id => new IdChoice(id, $"Area data {id:D4}")));
        FillIdCombo(MatrixInput, allMapMatrices.Select(entry => new IdChoice(entry.Id, $"Matrix {entry.Label}")));
        FillIdCombo(ScriptInput, ScriptFileIdsOnDisk(currentProject).Select(id => new IdChoice(id, ScriptFileLabel(id))));
        FillIdCombo(LevelScriptInput, LevelScriptService.GetIds(currentProject).Select(id => new IdChoice(id, LevelScriptLabel(id))));
        FillIdCombo(TextArchiveInput, TextArchiveService.GetArchiveIds(currentProject).Select(id => new IdChoice(id, TextArchiveLabel(id, "Text archive "))));
        FillIdCombo(EventInput, EventFileIdsOnDisk(currentProject).Select(id => new IdChoice(id, $"Event file {id:D4}")));
        Dictionary<int, List<string>> encounterMaps = EncounterFileHeaderNames();
        FillIdCombo(WildPokemonInput, WildEncounterService.GetFileIds(currentProject).Select(id =>
            new IdChoice(id, encounterMaps.TryGetValue(id, out List<string>? names) ? $"Encounter file {id:D4} - {string.Join(" + ", names.Take(3))}{(names.Count > 3 ? $" + {names.Count - 3} more" : "")}" : $"Encounter file {id:D4} - unused")));
    }

    /// <summary>For each wild encounter file, the internal names (and locations) of the headers that use it.</summary>
    private Dictionary<int, List<string>> EncounterFileHeaderNames()
    {
        var result = new Dictionary<int, List<string>>();
        if (headerStore is null || currentProject is null) return result;
        IReadOnlyList<HeaderOption> locations = currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata is not null
            ? currentProject.GetLocationNames(metadata)
            : [];
        for (ushort headerId = 0; headerId < currentProject.GetHeaderCount(); headerId++)
        {
            try
            {
                MapHeaderRecord header = headerStore.Load(headerId);
                if (header.WildPokemonId == NoWildEncounters) continue;
                string name = currentProject.GetInternalName(headerId);
                if (string.IsNullOrWhiteSpace(name)) continue;
                string location = locations.FirstOrDefault(option => option.Id == header.LocationNameId)?.Label ?? "";
                string label = location.Length > 0 ? $"{name} [{location}]" : name;
                if (!result.TryGetValue(header.WildPokemonId, out List<string>? names)) result[header.WildPokemonId] = names = [];
                if (!names.Contains(label)) names.Add(label);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DSPRE] Header {headerId:D4} could not be checked for its encounter file: {ex.Message}");
            }
        }

        return result;
    }

    private static IEnumerable<int> ScriptFileIdsOnDisk(RomProject project) =>
        Directory.Exists(project.ExpandedScriptsDirectory)
            ? Directory.GetFiles(project.ExpandedScriptsDirectory, "*.script")
                .Select(path => int.TryParse(Path.GetFileNameWithoutExtension(path), out int id) ? id : -1)
                .Where(id => id >= 0).Order()
            : [];

    private static IEnumerable<int> EventFileIdsOnDisk(RomProject project) =>
        Directory.Exists(project.EventsDirectory)
            ? Directory.GetFiles(project.EventsDirectory)
                .Select(path => int.TryParse(Path.GetFileNameWithoutExtension(path), out int id) ? id : -1)
                .Where(id => id >= 0).Order()
            : [];

    /// <summary>The encounter file number that means a header has none: no such file exists.</summary>
    private const ushort NoWildEncounters = 65535;

    private bool settingWildEncounters;

    private void SelectWildEncounters(int id)
    {
        settingWildEncounters = true;
        try
        {
            NoWildEncountersCheck.IsChecked = id == NoWildEncounters;
            WildPokemonInput.IsEnabled = id != NoWildEncounters;
            if (id == NoWildEncounters) WildPokemonInput.SelectedItem = null;
            else SelectIdChoice(WildPokemonInput, id);
        }
        finally
        {
            settingWildEncounters = false;
        }
    }

    private void NoWildEncountersCheck_Changed(object? sender, global::Avalonia.Interactivity.RoutedEventArgs e)
    {
        bool none = NoWildEncountersCheck.IsChecked == true;
        WildPokemonInput.IsEnabled = !none;
        if (settingWildEncounters) return;
        if (!none && WildPokemonInput.SelectedItem is null && WildPokemonInput.ItemCount > 0) WildPokemonInput.SelectedIndex = 0;
    }

    /// <summary>Refills the list and keeps the choice that was picked (a missing one stays listed), so a refill never leaves the header's fields empty.</summary>
    private static void FillIdCombo(ComboBox box, IEnumerable<IdChoice> choices)
    {
        IdChoice? picked = box.SelectedItem as IdChoice;
        box.Items.Clear();
        foreach (IdChoice choice in choices) box.Items.Add(choice);
        if (picked is null) return;
        box.SelectedItem = box.Items.OfType<IdChoice>().FirstOrDefault(choice => choice.Id == picked.Id) ?? AddAndReturn(box, picked);
    }

    private static IdChoice AddAndReturn(ComboBox box, IdChoice choice)
    {
        box.Items.Add(choice);
        return choice;
    }

    /// <summary>Selects the id; one the project has no file for is listed as missing so the header keeps it.</summary>
    private void SelectIdChoice(ComboBox box, int id)
    {
        IdChoice? choice = box.Items.OfType<IdChoice>().FirstOrDefault(item => item.Id == id);
        if (choice is null)
        {
            choice = new IdChoice(id, $"{id} (not in the project)");
            box.Items.Add(choice);
            headerIdCombosStale = true;
        }

        box.SelectedItem = choice;
    }

    private static int ComboId(ComboBox box, string what) =>
        box.SelectedItem is IdChoice choice ? choice.Id : throw new FormatException($"The header has no {what} picked; choose one in its list.");
}
