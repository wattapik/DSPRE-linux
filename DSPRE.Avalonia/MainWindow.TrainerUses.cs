using Avalonia.Interactivity;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Trainer Editor's "Used on": the maps a trainer is fought on (TrainerUsageService), with
/// Go To Map opening one in the Map Editor.</summary>
public partial class MainWindow
{
    private Dictionary<int, List<TrainerUse>>? trainerUses;
    private List<TrainerUse> shownTrainerUses = [];

    private void ShowTrainerUses(int trainerId, RomGameMetadata metadata)
    {
        if (currentProject is null) return;
        try
        {
            RomProject project = currentProject;
            trainerUses ??= TrainerUsageService.FindAll(project, metadata,
                eventFileId => sharedMapEvents.TryGetValue(eventFileId, out MapEventFile? edited) ? edited : MapEventService.Load(project, eventFileId));
        }
        catch (Exception ex)
        {
            LogUiError("The maps using trainers could not be found", ex);
            trainerUses = [];
        }

        shownTrainerUses = trainerUses.TryGetValue(trainerId, out List<TrainerUse>? uses) ? uses : [];
        TrainerUsesSelector.ItemsSource = shownTrainerUses.Count == 0
            ? ["Not placed on any map or fought in any script"]
            : shownTrainerUses.Select(use => $"{HeaderDisplayName(use.HeaderId)}: {use.How}").ToList();
        TrainerUsesSelector.SelectedIndex = 0;
    }

    private string HeaderDisplayName(int headerId)
    {
        foreach (object? item in HeaderSelector.Items)
        {
            if (item is HeaderListEntry entry && entry.Id == headerId) return $"{entry.Id:D3} {entry.InternalName} ({entry.LocationName})";
        }
        return $"Header {headerId:D3}";
    }

    private void TrainerGoToMap_Click(object? sender, RoutedEventArgs e)
    {
        int index = TrainerUsesSelector.SelectedIndex;
        if (index < 0 || index >= shownTrainerUses.Count)
        {
            TrainerEditorStatus.Text = "This trainer is not used on any map.";
            return;
        }

        int headerId = shownTrainerUses[index].HeaderId;
        SelectEditor("Map Editor");
        ClearHeaderFilters();
        for (int i = 0; i < HeaderSelector.Items.Count; i++)
        {
            if (HeaderSelector.Items[i] is HeaderListEntry entry && entry.Id == headerId)
            {
                HeaderSelector.SelectedIndex = i;
                return;
            }
        }
    }
}
