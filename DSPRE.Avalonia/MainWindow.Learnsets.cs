using Avalonia.Interactivity;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Pokémon Editor's bulk learnset editor and egg move CSV import/export (legacy
/// LearnsetBulkEditor and EggMoveEditor's CSV buttons).</summary>
public partial class MainWindow
{
    private BulkLearnsetWindow? bulkLearnsetWindow;

    private void OpenBulkLearnsetEditor_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        if (bulkLearnsetWindow is not null)
        {
            bulkLearnsetWindow.Activate();
            return;
        }

        bulkLearnsetWindow = new BulkLearnsetWindow(currentProject, PokeEditorSpeciesNames.ToArray(), pokeMoveNames, () =>
        {
            // The open species' learnset tab shows what was just saved, unless that would lose its unsaved edits.
            if (PokeEditorSpeciesSelector.SelectedIndex < 0) return;
            if (HasOpenDocumentChanges("Pokemon Editor")) PokeEditorStatus.Text = "The bulk edit was saved; the open species has unsaved changes, so it was not reloaded.";
            else LoadPokeEditorSpecies(PokeEditorSpeciesSelector.SelectedIndex, metadata);
        });
        bulkLearnsetWindow.Closed += (_, _) => bulkLearnsetWindow = null;
        bulkLearnsetWindow.Show(this);
    }

    private async void ExportEggMovesCsv_Click(object? sender, RoutedEventArgs e)
    {
        string? output = await PickSavePathAsync("Save the egg moves as", "EggMoves.csv", "CSV", "*.csv");
        if (output is null) return;
        await File.WriteAllTextAsync(output, LearnsetExchange.EggMovesToCsv(eggMoveEntries, eggSpeciesNames, eggMoveNames));
        EggMoveEditorStatus.Text = $"Egg moves of {eggMoveEntries.Count} Pokémon exported to {Path.GetFileName(output)}.";
    }

    private async void ImportEggMovesCsv_Click(object? sender, RoutedEventArgs e)
    {
        string? path = await PickFileAsync("Pick the egg move CSV", "CSV", "*.csv");
        if (path is null) return;
        List<EggMoveEntry> imported;
        try
        {
            imported = LearnsetExchange.EggMovesFromCsv(await File.ReadAllTextAsync(path));
        }
        catch (FormatException ex)
        {
            EggMoveEditorStatus.Text = ex.Message;
            return;
        }

        if (!await ConfirmAsync("Import egg moves", $"Replace the whole egg move list ({eggMoveEntries.Count} Pokémon) with {Path.GetFileName(path)}'s ({imported.Count} Pokémon, {imported.Sum(entry => entry.MoveIds.Count)} moves)?", "Replace")) return;
        eggMoveEntries.Clear();
        eggMoveEntries.AddRange(imported);
        ShowEggMovesForSelectedSpecies();
        EggMoveEditorStatus.Text = $"Imported egg moves for {imported.Count} Pokémon. Save Egg Moves to keep them.";
    }
}
