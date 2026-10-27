using Avalonia.Interactivity;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>Event file export and the Event Importer, from the Map Editor's Events pane. Both work on
/// the open (in-memory) event file; Save Map writes and journals the result like any other edit.</summary>
public partial class MainWindow
{
    private const string EventFilePattern = "*.evt";

    private async void ExportEventFile_Click(object? sender, RoutedEventArgs e)
    {
        if (currentMapEventFile is null) return;
        string? output = await PickSavePathAsync("Save the event file as", $"Event File {currentMapEventFile.Id}.evt", "Event file", EventFilePattern);
        if (output is null) return;
        await File.WriteAllBytesAsync(output, MapEventService.Serialize(currentMapEventFile));
        StatusText.Text = $"Event file {currentMapEventFile.Id} saved to {Path.GetFileName(output)}" +
            (EventsHaveUnsavedChanges() ? " (with the changes not saved to the project yet)." : ".");
    }

    private bool EventsHaveUnsavedChanges() =>
        currentMapEventFile is not null && sharedMapSavedEvents.TryGetValue(currentMapEventFile.Id, out MapEventFile? saved) &&
        !MapEventService.Serialize(saved).AsSpan().SequenceEqual(MapEventService.Serialize(currentMapEventFile));

    private async void ImportEvents_Click(object? sender, RoutedEventArgs e)
    {
        if (currentMapEventFile is null) return;
        string? path = await PickFileAsync("Pick an event file to import from", "Event file", EventFilePattern);
        if (path is null) return;

        MapEventFile source;
        try
        {
            source = MapEventService.Parse(await File.ReadAllBytesAsync(path), -1);
        }
        catch (Exception ex) when (ex is IOException or EndOfStreamException)
        {
            StatusText.Text = $"{Path.GetFileName(path)} is not a readable event file: {ex.Message}";
            return;
        }

        if (source.IsEmpty)
        {
            // As legacy: importing an empty file means emptying this one.
            if (!await ConfirmAsync("Empty event file", $"{Path.GetFileName(path)} has no events. Import it anyway, removing every event from event file {currentMapEventFile.Id}?", "Remove All Events")) return;
            ClearEvents(currentMapEventFile);
            AfterEventImport("All events removed. Save Map to keep the change.");
            return;
        }

        EventImportWindow.Choice? choice = await new EventImportWindow(source, Path.GetFileName(path), currentEventMatrixCell is not null, DescribeEvent)
            .ShowDialog<EventImportWindow.Choice?>(this);
        if (choice is null) return;
        if (choice.ReplaceExisting &&
            !await ConfirmAsync("Replace events", $"Remove every event from event file {currentMapEventFile.Id} before importing?", "Replace")) return;

        if (choice.ReplaceExisting) ClearEvents(currentMapEventFile);
        foreach (MapEvent picked in choice.Events)
        {
            MapEvent copy = picked switch
            {
                SpawnableEvent spawnable => spawnable.Clone(),
                OverworldEvent overworld => overworld.Clone(),
                WarpEvent warp => warp.Clone(),
                TriggerEvent trigger => trigger.Clone(),
                _ => throw new InvalidOperationException("Unknown event kind.")
            };
            if (choice.MoveToCurrentSquare) PlaceOnCurrentEventCell(copy);
            ShowEventKind(copy.Kind);
            switch (copy)
            {
                case SpawnableEvent spawnable: currentMapEventFile.Spawnables.Add(spawnable); break;
                case OverworldEvent overworld: currentMapEventFile.Overworlds.Add(overworld); break;
                case WarpEvent warp: currentMapEventFile.Warps.Add(warp); break;
                case TriggerEvent trigger: currentMapEventFile.Triggers.Add(trigger); break;
            }
        }

        AfterEventImport($"Imported {choice.Events.Count} events from {Path.GetFileName(path)}. Save Map to keep them.");
    }

    /// <summary>Every event file edited since it was loaded or saved, whichever map it belongs to: events
    /// stay edited in memory after the map editor moves on to another map or view.</summary>
    private List<MapEventFile> UnsavedEventFiles() =>
        sharedMapEvents.Values
            .Where(file => !sharedMapSavedEvents.TryGetValue(file.Id, out MapEventFile? saved) ||
                           !MapEventService.Serialize(saved).AsSpan().SequenceEqual(MapEventService.Serialize(file)))
            .OrderBy(file => file.Id)
            .ToList();

    private static void ClearEvents(MapEventFile file)
    {
        file.Spawnables.Clear();
        file.Overworlds.Clear();
        file.Warps.Clear();
        file.Triggers.Clear();
    }

    private void AfterEventImport(string message)
    {
        RefreshEventListBoxItems();
        RefreshEventMarkers();
        RefreshEventDirtyHighlight();
        SyncEventFieldsToSelectedEvent();
        StatusText.Text = message;
    }
}
