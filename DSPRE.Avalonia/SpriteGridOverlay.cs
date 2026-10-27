using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace DSPRE.Avalonia;

/// <summary>Lines every <see cref="CellSize"/> sprite pixels over a zoomed sprite sheet, so frame
/// boundaries can be seen; the overlay is sized to the zoomed image and scales with it.</summary>
public sealed class SpriteGridOverlay : Control
{
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.FromArgb(220, 232, 183, 95)), 1);

    public int CellSize { get; set; } = 32;
    public double Zoom { get; private set; } = 1;

    public SpriteGridOverlay()
    {
        IsHitTestVisible = false;
    }

    public void SetZoom(double zoom)
    {
        Zoom = Math.Max(1, zoom);
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        double step = CellSize * Zoom;
        double width = Bounds.Width, height = Bounds.Height;
        if (step <= 0 || width <= 0 || height <= 0) return;

        // Half-pixel offsets keep 1px lines crisp; the outer edge is drawn too so the last row and
        // column of frames are closed off.
        for (double x = 0; x <= width + 0.01; x += step)
        {
            double lineX = Math.Min(x, width - 0.5) + (x == 0 ? 0.5 : 0);
            context.DrawLine(GridPen, new Point(lineX, 0), new Point(lineX, height));
        }

        for (double y = 0; y <= height + 0.01; y += step)
        {
            double lineY = Math.Min(y, height - 0.5) + (y == 0 ? 0.5 : 0);
            context.DrawLine(GridPen, new Point(0, lineY), new Point(width, lineY));
        }
    }
}
