using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Globalization;

namespace DSPRE.Avalonia;

/// <summary>Draws each permission-grid cell's raw byte value ("[XX]") on top of the 3D overhead view,
/// in 80% opaque black, skipping cells whose value is 0x00. Kept as a separate 2D overlay rather than
/// baked into MapGlView's own GL rendering because text is far simpler to lay out and measure through
/// Avalonia's own text APIs than through custom GL glyph rendering, and this only needs to track a
/// locked camera (recomputed on data/resize changes, not every frame).</summary>
public sealed class PermissionLabelOverlay : Control
{
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromArgb(204, 0, 0, 0));
    private static readonly Typeface LabelTypeface = new("Arial");
    private const double LabelFontSize = 11;

    public MapGlView? Target { get; set; }
    private byte[,]? values;

    public PermissionLabelOverlay()
    {
        IsHitTestVisible = false;
        SizeChanged += (_, _) => InvalidateVisual();
    }

    /// <summary>Sets the 32x32 grid of raw byte values to label. Pass null to clear.</summary>
    public void SetValues(byte[,]? grid)
    {
        values = grid;
        InvalidateVisual();
    }

    public override void Render(DrawingContext context)
    {
        if (Target is null || values is null) return;

        int size = values.GetLength(0);
        for (int row = 0; row < size; row++)
        {
            for (int col = 0; col < values.GetLength(1); col++)
            {
                byte value = values[row, col];
                if (value == 0) continue;
                if (!Target.TryProjectGridCellCenter(col, row, out Point point)) continue;
                if (point.X < 0 || point.Y < 0 || point.X > Bounds.Width || point.Y > Bounds.Height) continue;

                var text = new FormattedText(
                    value.ToString("X2", CultureInfo.InvariantCulture),
                    CultureInfo.InvariantCulture,
                    FlowDirection.LeftToRight,
                    LabelTypeface,
                    LabelFontSize,
                    LabelBrush);
                context.DrawText(text, new Point(point.X - text.Width / 2, point.Y - text.Height / 2));
            }
        }
    }
}
