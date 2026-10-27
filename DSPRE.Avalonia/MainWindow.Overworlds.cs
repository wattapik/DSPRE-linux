using Avalonia.Interactivity;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>Adding and deleting custom overworld entries on ROMs with hzla's overworld sprites
/// expansion (legacy BtxEditor's Add/Delete Entry).</summary>
public partial class MainWindow
{
    // The mmodel NARC mixes flat sprite sheets with 3D models, so unused files are probed for being
    // sheets; legacy caps the probe the same way.
    private const int MaxUnusedSpriteSlots = 60;
    private const int MaxUnusedSpritesProbed = 400;

    private void UpdateOwExpansionStatus()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        OverworldTableExpansion.Layout? layout = OverworldTableExpansion.Detect(currentProject, metadata);
        OwAddEntryButton.IsEnabled = layout is not null && layout.Used < layout.Capacity;
        OwDeleteEntryButton.IsEnabled = layout is not null && SelectedOwGraphicEntry is { } entry && OverworldTableExpansion.IsCustom(layout, entry.EntryId);
        OwExpansionStatus.Text = layout is null
            ? "Adding or deleting entries needs hzla's PlatPatches overworld sprites expansion (Platinum)."
            : $"Overworld expansion: {layout.Used} of {layout.Capacity} custom entries used. Only custom entries can be deleted.";
    }

    private void ReloadOwGraphicEditor(uint selectEntry)
    {
        owGraphicEditorProject = null;
        ConfigureOverworldGraphicEditor();
        int index = owGraphicEntries.FindIndex(entry => entry.EntryId == selectEntry);
        if (index >= 0) OwGraphicList.SelectedIndex = index;
        UpdateOwExpansionStatus();
    }

    private async void OwAddEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        if (OverworldTableExpansion.Detect(currentProject, metadata) is not { } layout) return;

        var slots = new List<AddOverworldEntryWindow.SlotOption>();
        var usedSprites = owGraphicEntries.Select(entry => entry.SpriteId).ToHashSet();
        int probed = 0;
        foreach (string path in Directory.GetFiles(currentProject.OverworldSpritesDirectory).OrderBy(path => path, StringComparer.Ordinal))
        {
            if (slots.Count >= MaxUnusedSpriteSlots || probed >= MaxUnusedSpritesProbed) break;
            if (!uint.TryParse(Path.GetFileName(path), out uint spriteId) || usedSprites.Contains(spriteId)) continue;
            probed++;
            if (OverworldSpriteService.LoadSheet(currentProject, spriteId, 0) is { } sheet)
            {
                slots.Add(new(spriteId, $"Unused sprite #{spriteId} - {sheet.Width}x{sheet.Height}", sheet.Width, sheet.Height));
            }
        }

        foreach (OverworldSpriteEntry entry in owGraphicEntries)
        {
            if (OverworldSpriteService.LoadSheet(currentProject, entry.SpriteId, 0) is { } sheet)
            {
                slots.Add(new(entry.SpriteId, $"Art of OW Entry {entry.EntryId} (sprite #{entry.SpriteId}) - {sheet.Width}x{sheet.Height}", sheet.Width, sheet.Height));
            }
        }

        AddOverworldEntryWindow.Choice? choice = await new AddOverworldEntryWindow(slots, owGraphicEntries.Select(entry => entry.EntryId).ToList(), OverworldTableExpansion.SuggestEntryId(layout))
            .ShowDialog<AddOverworldEntryWindow.Choice?>(this);
        if (choice is null) return;

        try
        {
            // With an image, the entry gets a new sprite file copied from the template, so the image
            // never overwrites another entry's art.
            uint sprite = choice.ImagePath is null ? choice.SpriteId : OverworldTableExpansion.NextSpriteSlot(currentProject);
            OverworldTableExpansion.Add(currentProject, metadata, choice.EntryId, sprite, choice.CloneFrom, choice.ImagePath is null ? null : choice.SpriteId);
            string note = string.Empty;
            if (choice.ImagePath is not null)
            {
                try
                {
                    OverworldSpriteService.ImportImage(currentProject, sprite, 0, choice.ImagePath);
                }
                catch (InvalidDataException ex)
                {
                    note = $" The image could not be imported ({ex.Message}); the entry shows a copy of sprite #{choice.SpriteId} until you import one.";
                }
            }

            ReloadOwGraphicEditor(choice.EntryId);
            OwGraphicStatus.Text = $"Added OW Entry {choice.EntryId} (0x{choice.EntryId:X}), showing sprite #{sprite}.{note}";
        }
        catch (InvalidOperationException ex)
        {
            OwGraphicStatus.Text = ex.Message;
        }
    }

    private async void OwDeleteEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        if (SelectedOwGraphicEntry is not { } entry) return;
        if (!await ConfirmAsync("Delete overworld entry", $"Delete custom OW Entry {entry.EntryId} (0x{entry.EntryId:X})? Events using it will show nothing.", "Delete")) return;
        try
        {
            OverworldTableExpansion.Delete(currentProject, metadata, entry.EntryId);
            ReloadOwGraphicEditor(owGraphicEntries.FirstOrDefault()?.EntryId ?? 0);
            OwGraphicStatus.Text = $"Deleted OW Entry {entry.EntryId}.";
        }
        catch (InvalidOperationException ex)
        {
            OwGraphicStatus.Text = ex.Message;
        }
    }
}
