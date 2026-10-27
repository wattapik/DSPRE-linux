using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Pokédex area map: the Town Map picture with the routes' cells and the special places' markers laid over it (see
/// PokedexMapService). It draws what it is given and reports the cell under the pointer (or, in pixel mode, the picture pixel); what a click
/// means is up to the page.</summary>
public sealed class AreaMapCanvas : Control
{
    private static readonly IBrush Backdrop = new SolidColorBrush(Color.Parse("#1E2832"));
    private static readonly IBrush DimRoute = new SolidColorBrush(Color.FromArgb(70, 70, 210, 110));
    private static readonly IBrush ListedRoute = new SolidColorBrush(Color.FromArgb(215, 255, 160, 0));
    private static readonly IBrush SelectedRoute = new SolidColorBrush(Color.FromArgb(215, 60, 140, 255));
    private static readonly IBrush DimMarker = new SolidColorBrush(Color.FromArgb(110, 225, 70, 205));
    private static readonly IBrush ListedMarker = new SolidColorBrush(Color.FromArgb(235, 255, 60, 40));
    private static readonly IBrush SelectedMarker = new SolidColorBrush(Color.FromArgb(245, 255, 230, 0));
    private static readonly IPen Outline = new Pen(new SolidColorBrush(Colors.White), 2);

    private bool dragging;
    private bool secondary;
    private (int X, int Y) lastCell = (-1, -1);
    private int zoom = 1;

    public AreaMapCanvas()
    {
        Width = Height = PokedexMapService.MapSize;
        Cursor = new Cursor(StandardCursorType.Hand);
        RenderOptions.SetBitmapInterpolationMode(this, BitmapInterpolationMode.None);
    }

    public IImage? Picture { get; set; }

    public PokedexAreaMap? Map { get; set; }

    /// <summary>The area numbers to draw as ticked (the open species' places in the open list).</summary>
    public IReadOnlySet<int> Listed { get; set; } = new HashSet<int>();

    /// <summary>The place being edited: an area number of the routes or the special places (0 for none).</summary>
    public int Selected { get; set; }

    /// <summary>Which places the page works on: the special places' markers, or the routes' cells.</summary>
    public bool SpecialPlaces { get; set; }

    /// <summary>Report picture pixels instead of cells; the places are then drawn faintly, or not at all (<see cref="ShowPlaces"/>).</summary>
    public bool PixelMode { get; set; }

    public bool ShowPlaces { get; set; } = true;

    /// <summary>How many screen pixels one picture pixel takes.</summary>
    public int Zoom
    {
        get => zoom;
        set
        {
            zoom = Math.Max(1, value);
            Width = Height = PokedexMapService.MapSize * zoom;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    /// <summary>Raised for the cell under the pointer when a button goes down (<c>press</c>) and for each new cell dragged over.</summary>
    public event Action<int, int, bool, bool>? CellInteracted;

    /// <summary>The same in pixel mode: the picture pixel (x, y), whether it was the right button, whether the button went down.</summary>
    public event Action<int, int, bool, bool>? PixelInteracted;

    /// <summary>Raised when the button is let go after a press that reported something.</summary>
    public event Action? Released;

    /// <summary>Asked for the text to show while the pointer rests on a cell.</summary>
    public Func<int, int, string?>? Describe { get; set; }

    public override void Render(DrawingContext context)
    {
        double side = PokedexMapService.MapSize * zoom;
        context.FillRectangle(Backdrop, new Rect(0, 0, side, side));
        if (Picture is not null) context.DrawImage(Picture, new Rect(0, 0, side, side));
        if (Map is null || (PixelMode && !ShowPlaces)) return;

        double dim = PixelMode ? 0.45 : 1;
        for (int i = 0; i < Map.Routes.Count; i++)
        {
            int area = i + 1;
            bool active = !SpecialPlaces && !PixelMode;
            IBrush brush = active && area == Selected ? SelectedRoute : active && Listed.Contains(area) ? ListedRoute : DimRoute;
            using (context.PushOpacity(dim))
            {
                foreach ((int x, int y) in Map.Routes[i].Cells()) context.FillRectangle(brush, CellRect(x, y));
            }
        }

        for (int i = 0; i < Map.Specials.Count; i++)
        {
            int area = i + 1;
            bool active = SpecialPlaces && !PixelMode;
            IBrush brush = active && area == Selected ? SelectedMarker : active && Listed.Contains(area) ? ListedMarker : DimMarker;
            Rect marker = CellRect(Map.Specials[i].X, Map.Specials[i].Y).Deflate(2);
            using (context.PushOpacity(dim))
            {
                context.DrawEllipse(brush, active && (area == Selected || Listed.Contains(area)) ? Outline : null, marker.Center, marker.Width / 2, marker.Height / 2);
            }
        }
    }

    private Rect CellRect(int x, int y) =>
        new((PokedexMapService.OriginX + x * PokedexMapService.CellSize) * zoom, (PokedexMapService.OriginY + y * PokedexMapService.CellSize) * zoom, PokedexMapService.CellSize * zoom, PokedexMapService.CellSize * zoom);

    private (int X, int Y)? CellAt(Point at)
    {
        if (PixelMode)
        {
            int px = (int)Math.Floor(at.X / zoom), py = (int)Math.Floor(at.Y / zoom);
            return px is >= 0 and < PokedexMapService.MapSize && py is >= 0 and < PokedexMapService.MapSize ? (px, py) : null;
        }

        int x = (int)Math.Floor((at.X / zoom - PokedexMapService.OriginX) / PokedexMapService.CellSize), y = (int)Math.Floor((at.Y / zoom - PokedexMapService.OriginY) / PokedexMapService.CellSize);
        return x is >= 0 and < 32 && y is >= 0 and < 32 ? (x, y) : null;
    }

    private void Report(int x, int y, bool press)
    {
        if (PixelMode) PixelInteracted?.Invoke(x, y, secondary, press);
        else CellInteracted?.Invoke(x, y, secondary, press);
        InvalidateVisual();
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        PointerPointProperties properties = e.GetCurrentPoint(this).Properties;
        if (!(properties.IsLeftButtonPressed || properties.IsRightButtonPressed) || CellAt(e.GetPosition(this)) is not var (x, y)) return;
        dragging = true;
        secondary = properties.IsRightButtonPressed;
        lastCell = (x, y);
        e.Pointer.Capture(this);
        Report(x, y, true);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        (int X, int Y)? cell = CellAt(e.GetPosition(this));
        ToolTip.SetTip(this, !PixelMode && cell is var (hx, hy) ? Describe?.Invoke(hx, hy) : null);
        if (!dragging || cell is not var (x, y) || (x, y) == lastCell) return;
        lastCell = (x, y);
        Report(x, y, false);
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (dragging) Released?.Invoke();
        dragging = false;
        e.Pointer.Capture(null);
    }
}
