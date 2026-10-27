using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DSPRE.Core;
using System.Globalization;

namespace DSPRE.Avalonia;

/// <summary>The Map Creator's 32 x 32 grid seen from above, north at the top: the tiles of the map (the active
/// layer in full, the others faded), their heights, and the tile about to be placed under the pointer.</summary>
public sealed class MapCreatorGridView : Control
{
    private const int Cells = MapCreatorMap.Columns;
    private static readonly Typeface HeightFont = new("Inter, Segoe UI, sans-serif");

    private bool pressing;
    private bool secondary;
    private (int Column, int Row) lastCell = (-1, -1);

    public MapCreatorMap? Map { get; set; }
    public Func<int, Bitmap?>? TileImage { get; set; }

    /// <summary>The whole layer drawn as the finished map would show it (polygons that reach over a cell border, stretched
    /// tiles); null to draw the layer tile by tile instead.</summary>
    public Func<int, Bitmap?>? LayerImage { get; set; }
    public Func<int, (int Width, int Height)>? TileSize { get; set; }
    public int ActiveLayer { get; set; }
    public bool ShowOtherLayers { get; set; } = true;
    public bool ShowHeights { get; set; } = true;
    public int SelectedTile { get; set; } = -1;
    public (int Column, int Row) Hover { get; private set; } = (-1, -1);

    /// <summary>A stroke began; true when with the secondary button (which erases).</summary>
    public event Action<bool>? StrokeStarted;
    public event Action<int, int, bool>? CellTouched;
    public event Action? StrokeEnded;
    public event Action<int, int>? HoverChanged;

    public MapCreatorGridView()
    {
        ClipToBounds = true;
        Cursor = new Cursor(StandardCursorType.Cross);
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.MediumQuality);
    }

    private (double Size, double X, double Y) Layout()
    {
        double size = Math.Max(Math.Min(Bounds.Width, Bounds.Height) / Cells, 1);
        return (size, (Bounds.Width - size * Cells) / 2, (Bounds.Height - size * Cells) / 2);
    }

    private (int Column, int Row) CellAt(Point point)
    {
        (double size, double x, double y) = Layout();
        int column = (int)Math.Floor((point.X - x) / size);
        int row = Cells - 1 - (int)Math.Floor((point.Y - y) / size);
        return column is >= 0 and < Cells && row is >= 0 and < Cells ? (column, row) : (-1, -1);
    }

    public override void Render(DrawingContext context)
    {
        (double size, double originX, double originY) = Layout();
        var board = new Rect(originX, originY, size * Cells, size * Cells);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0F1418")), board);

        if (Map is { } map)
        {
            for (int layer = 0; layer < MapCreatorMap.Layers; layer++)
            {
                if (layer != ActiveLayer && !ShowOtherLayers) continue;
                using (context.PushOpacity(layer == ActiveLayer ? 1.0 : 0.35)) DrawLayer(context, map, layer, size, originX, originY, board);
            }
        }

        var line = new Pen(new SolidColorBrush(Color.FromArgb(70, 160, 180, 200)), 1);
        for (int i = 0; i <= Cells; i++)
        {
            bool major = i % 8 == 0;
            var pen = major ? new Pen(new SolidColorBrush(Color.FromArgb(150, 160, 180, 200)), 1) : line;
            context.DrawLine(pen, new Point(originX + i * size, originY), new Point(originX + i * size, originY + Cells * size));
            context.DrawLine(pen, new Point(originX, originY + i * size), new Point(originX + Cells * size, originY + i * size));
        }

        if (Hover.Column >= 0)
        {
            (int width, int height) = SelectedTile >= 0 && TileSize is not null ? TileSize(SelectedTile) : (1, 1);
            var area = CellRect(Hover.Column, Hover.Row, width, height, size, originX, originY);
            if (SelectedTile >= 0 && TileImage?.Invoke(SelectedTile) is { } ghost)
            {
                using (context.PushOpacity(0.55)) context.DrawImage(ghost, area);
            }

            context.DrawRectangle(null, new Pen(Brushes.White, 2), area);
        }
    }

    private void DrawLayer(DrawingContext context, MapCreatorMap map, int layer, double size, double originX, double originY, Rect board)
    {
        int[,] tiles = map.Tiles[layer], heights = map.Heights[layer];
        Bitmap? whole = LayerImage?.Invoke(layer);
        if (whole is not null) context.DrawImage(whole, board);
        for (int column = 0; column < Cells; column++)
        {
            for (int row = 0; row < Cells; row++)
            {
                int tile = tiles[column, row];
                if (tile < 0) continue;
                (int width, int height) = TileSize?.Invoke(tile) ?? (1, 1);
                Rect area = CellRect(column, row, width, height, size, originX, originY);
                if (whole is null)
                {
                    if (TileImage?.Invoke(tile) is { } image) context.DrawImage(image, area);
                    else context.FillRectangle(Brushes.DimGray, area);
                }

                if (layer == ActiveLayer && ShowHeights && heights[column, row] != 0 && size >= 14)
                {
                    var text = new FormattedText(heights[column, row].ToString(CultureInfo.InvariantCulture), CultureInfo.InvariantCulture, FlowDirection.LeftToRight,
                        HeightFont, Math.Max(size * 0.4, 8), Brushes.White);
                    var spot = new Point(area.Left + 2, area.Bottom - text.Height - 1);
                    context.DrawRectangle(new SolidColorBrush(Color.FromArgb(150, 0, 0, 0)), null, new Rect(spot.X - 1, spot.Y, text.Width + 2, text.Height));
                    context.DrawText(text, spot);
                }
            }
        }
    }

    private static Rect CellRect(int column, int row, int width, int height, double size, double originX, double originY) =>
        new(originX + column * size, originY + (Cells - row - height) * size, width * size, height * size);

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
        if (!properties.IsLeftButtonPressed && !properties.IsRightButtonPressed) return;
        pressing = true;
        secondary = properties.IsRightButtonPressed;
        e.Pointer.Capture(this);
        StrokeStarted?.Invoke(secondary);
        lastCell = (-1, -1);
        Touch(e.GetPosition(this));
        e.Handled = true;
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        var cell = CellAt(e.GetPosition(this));
        if (cell != Hover)
        {
            Hover = cell;
            HoverChanged?.Invoke(cell.Column, cell.Row);
            InvalidateVisual();
        }

        if (pressing) Touch(e.GetPosition(this));
    }

    protected override void OnPointerExited(PointerEventArgs e)
    {
        base.OnPointerExited(e);
        if (pressing) return;
        Hover = (-1, -1);
        HoverChanged?.Invoke(-1, -1);
        InvalidateVisual();
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!pressing) return;
        pressing = false;
        e.Pointer.Capture(null);
        StrokeEnded?.Invoke();
    }

    private void Touch(Point point)
    {
        var cell = CellAt(point);
        if (cell.Column < 0 || cell == lastCell) return;
        lastCell = cell;
        CellTouched?.Invoke(cell.Column, cell.Row, secondary);
    }
}
