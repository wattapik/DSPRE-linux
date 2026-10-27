using Avalonia;
using Avalonia.Input;
using Avalonia.Media;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>A 16x16 grid a footprint is painted on: the left button paints (or erases, in erase mode), the right button erases.
/// Raises <see cref="Edited"/> after each pixel it changes.</summary>
public sealed class FootprintCanvas : Control
{
    private const int Cells = FootprintService.Size;
    private const int CellPixels = 20;
    private static readonly IBrush Light = new SolidColorBrush(Color.Parse("#C9D2DA"));
    private static readonly IBrush Dark = new SolidColorBrush(Color.Parse("#B4BFC9"));
    private static readonly IBrush Ink = new SolidColorBrush(Colors.Black);
    private static readonly IPen GridPen = new Pen(new SolidColorBrush(Color.Parse("#7F8C99"), 0.45), 1);

    private Footprint? footprint;
    private char? painting;

    public FootprintCanvas()
    {
        Width = Height = Cells * CellPixels;
        Cursor = new Cursor(StandardCursorType.Cross);
    }

    public bool EraseMode { get; set; }

    public event Action? Edited;

    public Footprint? Footprint
    {
        get => footprint;
        set
        {
            footprint = value;
            painting = null;
            InvalidateVisual();
        }
    }

    public override void Render(DrawingContext context)
    {
        for (int y = 0; y < Cells; y++)
        {
            for (int x = 0; x < Cells; x++)
            {
                bool set = footprint is not null && footprint.Rows[y][x] != '.';
                context.FillRectangle(set ? Ink : (x + y) % 2 == 0 ? Light : Dark, new Rect(x * CellPixels, y * CellPixels, CellPixels, CellPixels));
            }
        }

        for (int i = 0; i <= Cells; i++)
        {
            bool band = i % 8 == 0;
            IPen pen = band ? new Pen(new SolidColorBrush(Color.Parse("#4F6A85")), 1.5) : GridPen;
            context.DrawLine(pen, new Point(i * CellPixels, 0), new Point(i * CellPixels, Cells * CellPixels));
            context.DrawLine(pen, new Point(0, i * CellPixels), new Point(Cells * CellPixels, i * CellPixels));
        }
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
        if (footprint is null || !(properties.IsLeftButtonPressed || properties.IsRightButtonPressed)) return;
        painting = properties.IsRightButtonPressed || EraseMode ? '.' : 'k';
        e.Pointer.Capture(this);
        Paint(e.GetPosition(this));
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        if (painting is not null) Paint(e.GetPosition(this));
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        painting = null;
        e.Pointer.Capture(null);
    }

    private void Paint(Point at)
    {
        int x = (int)(at.X / CellPixels), y = (int)(at.Y / CellPixels);
        if (footprint is null || painting is not char value || x is < 0 or >= Cells || y is < 0 or >= Cells || footprint.Rows[y][x] == value) return;
        char[] row = footprint.Rows[y].ToCharArray();
        row[x] = value;
        footprint.Rows[y] = new string(row);
        InvalidateVisual();
        Edited?.Invoke();
    }
}
