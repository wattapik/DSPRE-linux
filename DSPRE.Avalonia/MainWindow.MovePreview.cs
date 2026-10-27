using System.Globalization;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Move Editor's in-game preview: the move as the summary screen shows it, its row in the
/// move list (type, name, PP) and its details (category, power, accuracy, description), drawn over
/// screenshots of the empty screen in the ROM's own font. Text positions are DS pixels, measured from
/// screenshots of Steel Wing (moves/example_move_*.png), which this reproduces pixel for pixel; the badges
/// are placed in the screenshots' own pixels.</summary>
public partial class MainWindow
{
    // Where each screenshot's DS pixel grid starts.
    private static readonly (double X, double Y) MovePreviewListingOrigin = (3.07, 2.81);
    private static readonly (double X, double Y) MovePreviewDetailsOrigin = (2.14, 2.69);
    private const int MovePreviewDescriptionLeft = 5;
    private const int MovePreviewDescriptionTop = 48;
    private const int MovePreviewDescriptionSpacing = 16;
    private const int MovePreviewDescriptionMaxRight = 120;
    // The striped rows run to the bottom of the screen: four full lines and a fifth that some moves use.
    private const int MovePreviewDescriptionLines = 5;

    private static readonly GameTextColors MovePreviewDarkColors = new((16, 24, 33), (173, 189, 189));
    private static readonly GameTextColors MovePreviewNameColors = new((255, 255, 255), (82, 82, 82));

    private readonly Dictionary<string, Bitmap?> movePreviewImages = [];

    private Bitmap? MovePreviewImage(string path)
    {
        if (movePreviewImages.TryGetValue(path, out Bitmap? cached)) return cached;
        var uri = new Uri($"avares://DSPRE.Avalonia/Assets/MovePreview/{path}");
        Bitmap? bitmap = AssetLoader.Exists(uri) ? new Bitmap(AssetLoader.Open(uri)) : null;
        movePreviewImages[path] = bitmap;
        return bitmap;
    }

    private void RenderMovePreview()
    {
        // Each canvas's first child is the empty screen; everything after it is the last move drawn.
        foreach (Canvas canvas in new[] { MovePreviewListing, MovePreviewDetails })
        {
            while (canvas.Children.Count > 1) canvas.Children.RemoveAt(canvas.Children.Count - 1);
        }
        MovePreviewStatus.Text = string.Empty;
        if (currentMoveData is not MoveRecord move) return;

        // Move list row: type badge, name, PP (shown full, as for a move just learned).
        if (MovePreviewImage($"Types/{move.MoveType}.png") is Bitmap typeBadge) AddMovePreviewImage(MovePreviewListing, typeBadge, 10, 10);
        string name = currentMoveId >= 0 && currentMoveId < moveEditorMoveNames.Length ? moveEditorMoveNames[currentMoveId] : string.Empty;
        if (AddGameText(MovePreviewListing, MovePreviewListingOrigin, name, 36, 1, MovePreviewNameColors) is null)
        {
            MovePreviewStatus.Text = $"The text can't be shown: {previewFontError}";
            return;
        }
        string pp = move.Pp.ToString(CultureInfo.InvariantCulture);
        AddGameText(MovePreviewListing, MovePreviewListingOrigin, pp, 92, 15, MovePreviewDarkColors, rightAligned: true);
        AddGameText(MovePreviewListing, MovePreviewListingOrigin, pp, 110, 15, MovePreviewDarkColors, rightAligned: true);

        // Details: category badge (status has none of its own yet), power and accuracy, description.
        string? category = move.Split switch
        {
            MoveSplit.Physical => "physical_box.png",
            MoveSplit.Special => "special_box.png",
            _ => null,
        };
        if (category is not null && MovePreviewImage(category) is Bitmap badge) AddMovePreviewImage(MovePreviewDetails, badge, 304, 6);
        // The game shows "---" for no fixed power (0, and 1 for moves whose power is worked out in
        // battle) and for moves that never miss (accuracy 0).
        string power = move.Damage <= 1 ? "---" : move.Damage.ToString(CultureInfo.InvariantCulture);
        string accuracy = move.Accuracy == 0 ? "---" : move.Accuracy.ToString(CultureInfo.InvariantCulture);
        AddGameText(MovePreviewDetails, MovePreviewDetailsOrigin, power, 114, 16, MovePreviewDarkColors, rightAligned: true);
        AddGameText(MovePreviewDetails, MovePreviewDetailsOrigin, accuracy, 114, 32, MovePreviewDarkColors, rightAligned: true);

        string[] lines = PreviewLines(currentMoveId >= 0 && currentMoveId < moveEditorDescriptions.Length ? moveEditorDescriptions[currentMoveId] : string.Empty);
        var tooWide = new List<int>();
        for (int i = 0; i < Math.Min(lines.Length, MovePreviewDescriptionLines); i++)
        {
            int? right = AddGameText(MovePreviewDetails, MovePreviewDetailsOrigin, lines[i], MovePreviewDescriptionLeft,
                MovePreviewDescriptionTop + i * MovePreviewDescriptionSpacing, MovePreviewDarkColors);
            if (right > MovePreviewDescriptionMaxRight) tooWide.Add(i + 1);
        }

        var notes = new List<string>();
        if (tooWide.Count > 0) notes.Add($"Description line {string.Join(" and ", tooWide)} runs past the edge of the screen.");
        if (lines.Length > MovePreviewDescriptionLines) notes.Add($"The description has {lines.Length} lines; the screen shows {MovePreviewDescriptionLines}.");
        MovePreviewStatus.Text = string.Join(" ", notes);
    }

    private static void AddMovePreviewImage(Canvas canvas, Bitmap bitmap, double left, double top)
    {
        var image = new Image { Source = bitmap, Width = bitmap.PixelSize.Width, Height = bitmap.PixelSize.Height };
        RenderOptions.SetBitmapInterpolationMode(image, global::Avalonia.Media.Imaging.BitmapInterpolationMode.None);
        Canvas.SetLeft(image, left);
        Canvas.SetTop(image, top);
        canvas.Children.Add(image);
    }
}
