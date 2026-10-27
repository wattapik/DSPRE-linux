using Avalonia.Interactivity;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Item Editor's icon import - see ItemIconService.</summary>
public partial class MainWindow
{
    private async void ImportItemIcon_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentItemId < 0 || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        // The import reloads the item afterwards, which would drop unsaved edits.
        if (itemTableEntryEdits.IsUnsaved(currentItemTableEntry) || (currentItemData is not null && itemDataEdits.IsUnsaved(currentItemData)))
        {
            ItemEditorStatus.Text = "This item has unsaved changes. Save the item first, then import the icon.";
            return;
        }

        if (await PickFileAsync("Item icon: 32x32", "PNG image", "*.png") is not string path) return;
        int itemId = currentItemId;
        bool newFiles = false;
        (List<int> iconSharers, List<int> paletteSharers) = ItemIconService.FindSharers(currentProject, metadata, itemId, itemEditorNames.Length);
        if (iconSharers.Count > 0 || paletteSharers.Count > 0)
        {
            string Names(List<int> ids) => string.Join(", ", ids.Take(4).Select(i => itemEditorNames[i])) + (ids.Count > 4 ? $" and {ids.Count - 4} more" : "");
            var shared = new List<string>();
            if (iconSharers.Count > 0) shared.Add($"its icon with {Names(iconSharers)}");
            if (paletteSharers.Count > 0) shared.Add($"its palette with {Names(paletteSharers)}");
            string? choice = await new ChoiceDialog("Shared Icon",
                $"{itemEditorNames[itemId]} shares {string.Join(", and ", shared)}.\n\n" +
                "Give it its own copy to change only this item. Overwriting the shared files changes the others too, and an item that shares only the palette will show wrong colors.",
                ("new", "Give It Its Own Copy"), ("overwrite", "Overwrite Shared Files"), ("cancel", "Cancel")).ShowDialog<string?>(this);
            if (choice is null or "cancel") return;
            newFiles = choice == "new";
        }

        try
        {
            ItemIconService.ImportResult result = ItemIconService.Import(currentProject, metadata, itemId, path, newFiles);
            loadingItemEditor = true;
            try
            {
                (List<int> iconIds, List<int> paletteIds) = ItemDataService.ListItemIconFiles(currentProject);
                SetItems(ItemIconSelector, iconIds.Select(i => i.ToString("D4")));
                SetItems(ItemPaletteSelector, paletteIds.Select(i => i.ToString("D4")));
            }
            finally
            {
                loadingItemEditor = false;
            }

            LoadItem(itemId, metadata);
            ItemEditorStatus.Text = $"Imported the icon with {result.Colors} color{(result.Colors == 1 ? "" : "s")}." +
                (result.SourceColors > result.Colors ? $" The image had {result.SourceColors}, reduced to {result.Colors}: an item icon has 15 colors plus transparency." : "") +
                (newFiles ? $" The item now has its own icon file {result.IconId:D4} and palette {result.PaletteId:D4}." : "") +
                " It reaches the ROM with Save ROM.";
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
        {
            ItemEditorStatus.Text = $"The icon could not be imported: {ex.Message}";
        }
    }
}
