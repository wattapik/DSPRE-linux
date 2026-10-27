using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Media;
using DSPRE.Core;
using System.Globalization;

namespace DSPRE.Avalonia;

/// <summary>A sequence's notes drawn as a piano roll: time across (bars of four beats, 48 ticks a beat), keys up the side,
/// one colour a track. A selected track is drawn in full and the others faded.</summary>
public sealed class PianoRollView : Control
{
    public const int TicksPerBeat = 48;
    public const int BeatsPerBar = 4;
    private const double Gutter = 44, Header = 34, RowHeight = 9;
    private static readonly Typeface Font = new("Inter, Segoe UI, sans-serif");
    private static readonly string[] NoteNames = ["C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B"];

    private NitroSequencer.SequenceView? view;
    private double pixelsPerTick = 0.25;
    private int selectedTrack = -1;
    private int selectedOffset = -1;
    private int lowKey, highKey;

    public static string NoteName(int key) => $"{NoteNames[key % 12]}{key / 12 - 1}";

    public static Color TrackColor(int track) => HsvColor(track * 47 % 360, 0.65, 0.95);

    private static Color HsvColor(double hue, double saturation, double value)
    {
        double c = value * saturation, x = c * (1 - Math.Abs(hue / 60 % 2 - 1)), m = value - c;
        (double r, double g, double b) = (int)(hue / 60) switch { 0 => (c, x, 0.0), 1 => (x, c, 0.0), 2 => (0.0, c, x), 3 => (0.0, x, c), 4 => (x, 0.0, c), _ => (c, 0.0, x) };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    public NitroSequencer.SequenceView? View
    {
        get => view;
        set
        {
            view = value;
            selectedTrack = -1;
            preview = null;
            lowKey = value is { Notes.Count: > 0 } ? Math.Max(value.Notes.Min(note => note.Key) - 1, 0) : 36;
            highKey = value is { Notes.Count: > 0 } ? Math.Min(value.Notes.Max(note => note.Key) + 1, 127) : 84;
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public double PixelsPerTick
    {
        get => pixelsPerTick;
        set
        {
            pixelsPerTick = Math.Max(value, 0.01);
            InvalidateMeasure();
            InvalidateVisual();
        }
    }

    public int SelectedTrack
    {
        get => selectedTrack;
        set
        {
            selectedTrack = value;
            InvalidateVisual();
        }
    }

    /// <summary>The note command (its offset in the sequence) drawn with a white outline; every play of it is outlined.</summary>
    public int SelectedOffset
    {
        get => selectedOffset;
        set
        {
            selectedOffset = value;
            InvalidateVisual();
        }
    }

    private double playheadTick;
    private bool draggingPlayhead;

    /// <summary>The play line: where Play starts, and where the sound is while it plays.</summary>
    public double PlayheadTick
    {
        get => playheadTick;
        set
        {
            playheadTick = Math.Max(value, 0);
            InvalidateVisual();
        }
    }

    public double PlayheadX => Gutter + playheadTick * pixelsPerTick;

    private (int Offset, int Key, int Velocity, int Length, int Shift)? preview;
    private (int Track, int Start, int Key, int Velocity, int Length)? ghost;

    /// <summary>Draws every play of the note command at <paramref name="offset"/> with these values, before they are applied.</summary>
    public void SetPreview(int offset, int key, int velocity, int length, int shift)
    {
        preview = (offset, key, velocity, length, shift);
        FitKey(key);
    }

    /// <summary>Draws a note that does not exist yet, outlined, for a note about to be created.</summary>
    public void SetGhost(int track, int start, int key, int velocity, int length)
    {
        ghost = (track, start, key, velocity, length);
        FitKey(key);
    }

    private void FitKey(int key)
    {
        if (key < lowKey || key > highKey)
        {
            lowKey = Math.Min(lowKey, Math.Max(key - 1, 0));
            highKey = Math.Max(highKey, Math.Min(key + 1, 127));
            InvalidateMeasure();
        }

        InvalidateVisual();
    }

    public void ClearPreview()
    {
        if (preview is null && ghost is null) return;
        preview = null;
        ghost = null;
        InvalidateVisual();
    }

    public event Action<NitroSequencer.NoteEvent>? NoteClicked;
    public event Action<double>? PlayheadDragged;
    public event Action<double>? PlayheadReleased;

    private double TickAt(double x) => Math.Clamp((x - Gutter) / pixelsPerTick, 0, view?.EndTick ?? 0);

    private Rect NoteRect(NitroSequencer.NoteEvent note)
    {
        double bottom = Header + (highKey - lowKey + 1) * RowHeight;
        return new Rect(Gutter + note.Start * pixelsPerTick, bottom - (note.Key - lowKey + 1) * RowHeight + 1, Math.Max(note.Length * pixelsPerTick, 1.5), RowHeight - 2);
    }

    protected override void OnPointerPressed(PointerPressedEventArgs e)
    {
        base.OnPointerPressed(e);
        if (view is null || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
        Point point = e.GetPosition(this);
        if (point.Y < Header || Math.Abs(point.X - PlayheadX) <= 4)
        {
            draggingPlayhead = true;
            e.Pointer.Capture(this);
            PlayheadTick = TickAt(point.X);
            PlayheadDragged?.Invoke(playheadTick);
            return;
        }

        NitroSequencer.NoteEvent? hit = view.Notes.LastOrDefault(note => (selectedTrack < 0 || note.Track == selectedTrack) && NoteRect(note).Inflate(1.5).Contains(point));
        if (hit is not null) NoteClicked?.Invoke(hit);
    }

    protected override void OnPointerMoved(PointerEventArgs e)
    {
        base.OnPointerMoved(e);
        Point point = e.GetPosition(this);
        if (draggingPlayhead)
        {
            PlayheadTick = TickAt(point.X);
            PlayheadDragged?.Invoke(playheadTick);
        }
        else
        {
            Cursor = point.Y < Header || Math.Abs(point.X - PlayheadX) <= 4 ? new Cursor(StandardCursorType.SizeWestEast) : Cursor.Default;
        }
    }

    protected override void OnPointerReleased(PointerReleasedEventArgs e)
    {
        base.OnPointerReleased(e);
        if (!draggingPlayhead) return;
        draggingPlayhead = false;
        e.Pointer.Capture(null);
        PlayheadReleased?.Invoke(playheadTick);
    }

    protected override Size MeasureOverride(Size availableSize) =>
        new(Gutter + Math.Max(view?.EndTick ?? 0, ghost is { } g ? g.Start + g.Length : 0) * pixelsPerTick + 40, Header + (highKey - lowKey + 1) * RowHeight);

    public override void Render(DrawingContext context)
    {
        base.Render(context);
        context.FillRectangle(new SolidColorBrush(Color.Parse("#0E1318")), new Rect(Bounds.Size));
        if (view is null) return;
        double top = Header, bottom = Header + (highKey - lowKey + 1) * RowHeight;
        var labelBrush = new SolidColorBrush(Color.Parse("#7F90A0"));
        var blackRow = new SolidColorBrush(Color.Parse("#141B22"));
        var barPen = new Pen(new SolidColorBrush(Color.Parse("#3A4856")), 1);
        var beatPen = new Pen(new SolidColorBrush(Color.Parse("#222C36")), 1);

        for (int key = lowKey; key <= highKey; key++)
        {
            double y = bottom - (key - lowKey + 1) * RowHeight;
            if (NoteNames[key % 12].Contains('#')) context.FillRectangle(blackRow, new Rect(Gutter, y, Bounds.Width - Gutter, RowHeight));
            if (key % 12 == 0)
            {
                context.DrawLine(beatPen, new Point(Gutter, y + RowHeight), new Point(Bounds.Width, y + RowHeight));
                DrawText(context, NoteName(key), 4, y - 2, labelBrush, 10);
            }
        }

        int ticksPerBar = TicksPerBeat * BeatsPerBar;
        for (int tick = 0; tick <= view.EndTick; tick += TicksPerBeat)
        {
            double x = Gutter + tick * pixelsPerTick;
            bool bar = tick % ticksPerBar == 0;
            if (!bar && pixelsPerTick * TicksPerBeat < 6) continue;
            context.DrawLine(bar ? barPen : beatPen, new Point(x, bar ? 14 : top), new Point(x, bottom));
            if (bar && (pixelsPerTick * ticksPerBar >= 24 || tick / ticksPerBar % 4 == 0)) DrawText(context, (tick / ticksPerBar + 1).ToString(CultureInfo.InvariantCulture), x + 3, 0, labelBrush, 10);
        }

        foreach (NitroSequencer.TempoEvent tempo in view.Tempos)
        {
            double x = Gutter + tempo.Tick * pixelsPerTick;
            DrawText(context, $"♩={tempo.Tempo:0}", x + 3, 14, new SolidColorBrush(Color.Parse("#E5C07B")), 10);
        }

        foreach (NitroSequencer.NoteEvent shown in view.Notes)
        {
            NitroSequencer.NoteEvent note = preview is { } p && p.Offset == shown.Offset ? shown with { Key = p.Key, Velocity = p.Velocity, Length = p.Length, Start = Math.Max(shown.Start + p.Shift, 0) } : shown;
            double x = Gutter + note.Start * pixelsPerTick, width = Math.Max(note.Length * pixelsPerTick, 1.5);
            double y = bottom - (note.Key - lowKey + 1) * RowHeight;
            Color color = TrackColor(note.Track);
            bool dim = selectedTrack >= 0 && note.Track != selectedTrack;
            byte alpha = (byte)((dim ? 40 : 120) + note.Velocity * (dim ? 0 : 1));
            bool chosen = note.Offset == selectedOffset && !dim;
            context.DrawRectangle(new SolidColorBrush(color, alpha / 255.0), chosen ? new Pen(Brushes.White, 2) : dim ? null : new Pen(new SolidColorBrush(color), 1), new Rect(x, y + 1, width, RowHeight - 2), 2, 2);
        }

        if (ghost is { } g)
        {
            double gx = Gutter + g.Start * pixelsPerTick, gy = bottom - (g.Key - lowKey + 1) * RowHeight;
            context.DrawRectangle(new SolidColorBrush(TrackColor(g.Track), 0.5), new Pen(Brushes.White, 2, DashStyle.Dash), new Rect(gx, gy + 1, Math.Max(g.Length * pixelsPerTick, 3), RowHeight - 2), 2, 2);
        }

        double playX = PlayheadX;
        context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#FF6B6B")), 2), new Point(playX, 0), new Point(playX, bottom));
        context.DrawGeometry(new SolidColorBrush(Color.Parse("#FF6B6B")), null, new PolylineGeometry([new Point(playX - 6, 0), new Point(playX + 6, 0), new Point(playX, 10)], true));

        if (view.LoopTick >= 0)
        {
            double x = Gutter + view.LoopTick * pixelsPerTick;
            context.DrawLine(new Pen(new SolidColorBrush(Color.Parse("#65D8C7")), 2, DashStyle.Dash), new Point(x, 12), new Point(x, bottom));
            DrawText(context, "loops back", x + 4, bottom - 14, new SolidColorBrush(Color.Parse("#65D8C7")), 10);
        }
    }

    private static void DrawText(DrawingContext context, string text, double x, double y, IBrush brush, double size)
    {
        var formatted = new FormattedText(text, CultureInfo.InvariantCulture, FlowDirection.LeftToRight, Font, size, brush);
        context.DrawText(formatted, new Point(x, y));
    }
}
