using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>Drawing text in the ROM's own font (GameFont) onto the in-game previews, which are screenshots of
/// the DS screen at 868/256. A preview's screenshot may not start on a DS pixel boundary, so each passes the
/// canvas position of its DS pixel grid's origin.</summary>
public partial class MainWindow
{
    private const double GameTextDsPixel = 868.0 / 256.0;

    private readonly record struct GameTextColors((byte R, byte G, byte B) Text, (byte R, byte G, byte B) Shadow);

    private GameFont? previewFont;
    private GameTextEncoder? previewEncoder;
    private string? previewFontError;

    /// <summary>Loads the project's font and the character map once; false (with previewFontError saying
    /// why) when either is missing.</summary>
    private bool EnsurePreviewFont()
    {
        if (previewFont is not null && previewEncoder is not null) return true;
        if (previewFontError is not null || currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return false;
        try
        {
            previewFont = GameFontService.Load(currentProject, metadata);
            string charMapPath = CharMapStore.ActivePath(ToolsDirectory);
            previewEncoder = File.Exists(charMapPath) ? new GameTextEncoder(CharMapStore.Load(charMapPath)) : null;
            if (previewFont is null) previewFontError = $"{GameFontService.GetArchivePath(currentProject, metadata)} was not found.";
            else if (previewEncoder is null) previewFontError = $"{charMapPath} was not found.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
        {
            previewFontError = ex.Message;
        }
        return previewFontError is null;
    }

    private void ResetPreviewFont()
    {
        previewFont = null;
        previewEncoder = null;
        previewFontError = null;
    }

    /// <summary>Draws one line with its glyph cells' top-left (or, right-aligned, top-right) corner at DS
    /// pixel (x, y). Returns the DS pixel the line ends at on the right, or null when the font is missing.</summary>
    private int? AddGameText(Canvas canvas, (double X, double Y) origin, string text, int x, int y, GameTextColors colors, bool rightAligned = false)
    {
        if (!EnsurePreviewFont()) return null;
        List<ushort> codes = previewEncoder!.Encode(text);
        if (codes.Count == 0) return x;
        (byte[] rgba, int width) = previewFont!.RenderLine(codes, colors.Text, colors.Shadow);
        int left = rightAligned ? x - width : x;
        if (CreateBitmapFromRgba(rgba, width, GameFont.GlyphSize) is Bitmap bitmap)
        {
            AddGameImage(canvas, origin, bitmap, left, y, width, GameFont.GlyphSize);
        }
        return left + width;
    }

    /// <summary>Places a bitmap of DS pixels (an icon, a line of text) at DS pixel (x, y).</summary>
    private static void AddGameImage(Canvas canvas, (double X, double Y) origin, Bitmap bitmap, int x, int y, int width, int height)
    {
        var image = new Image { Source = bitmap, Width = width * GameTextDsPixel, Height = height * GameTextDsPixel, Stretch = Stretch.Fill };
        RenderOptions.SetBitmapInterpolationMode(image, BitmapInterpolationMode.None);
        Canvas.SetLeft(image, origin.X + x * GameTextDsPixel);
        Canvas.SetTop(image, origin.Y + y * GameTextDsPixel);
        canvas.Children.Add(image);
    }

    /// <summary>A stored message's lines: \r and \f also start a new line, and the line break most
    /// descriptions end in starts none.</summary>
    private static string[] PreviewLines(string stored)
    {
        string[] lines = stored.Replace("\\r", "\\n").Replace("\\f", "\\n").TrimEnd().Split("\\n");
        return lines.Length > 1 && lines[^1].Length == 0 ? lines[..^1] : lines;
    }
}
