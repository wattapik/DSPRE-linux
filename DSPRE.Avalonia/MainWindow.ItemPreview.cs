using Avalonia.Media.Imaging;

namespace DSPRE.Avalonia;

/// <summary>The Item Editor's in-game preview: the Bag's description strip, with the item's icon in the
/// white box and its description beside it, drawn over a screenshot of the empty strip
/// (Assets/ItemPreview/item_blank.png). Positions are DS pixels, measured from a screenshot of Max Repel,
/// which this reproduces pixel for pixel.</summary>
public partial class MainWindow
{
    private static readonly (double X, double Y) ItemPreviewOrigin = (0, 0);
    private static readonly GameTextColors ItemPreviewColors = new((255, 255, 255), (0, 0, 0));
    private const int ItemPreviewTextLeft = 40;
    private const int ItemPreviewTextTop = 3;
    private const int ItemPreviewLineSpacing = 16;
    private const int ItemPreviewIconLeft = 6;
    private const int ItemPreviewIconTop = 15;
    private const int ItemPreviewLines = 3;

    private void RenderItemPreview()
    {
        ItemPreviewLayer.Children.Clear();
        ItemPreviewStatus.Text = string.Empty;
        if (currentItemId < 0) return;

        if (ItemIconImage.Source is Bitmap icon) AddGameImage(ItemPreviewLayer, ItemPreviewOrigin, icon, ItemPreviewIconLeft, ItemPreviewIconTop, 32, 32);

        string[] lines = PreviewLines(currentItemId < itemEditorDescriptions.Length ? itemEditorDescriptions[currentItemId] : string.Empty);
        var tooWide = new List<int>();
        for (int i = 0; i < Math.Min(lines.Length, ItemPreviewLines); i++)
        {
            if (AddGameText(ItemPreviewLayer, ItemPreviewOrigin, lines[i], ItemPreviewTextLeft, ItemPreviewTextTop + i * ItemPreviewLineSpacing, ItemPreviewColors) is not int right)
            {
                ItemPreviewStatus.Text = $"The description can't be shown: {previewFontError}";
                return;
            }
            if (right > 256) tooWide.Add(i + 1);
        }

        var notes = new List<string>();
        if (tooWide.Count > 0) notes.Add($"Description line {string.Join(" and ", tooWide)} runs past the edge of the screen.");
        if (lines.Length > ItemPreviewLines) notes.Add($"The description has {lines.Length} lines; the Bag shows {ItemPreviewLines}.");
        ItemPreviewStatus.Text = string.Join(" ", notes);
    }
}
