using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using System.Globalization;

namespace DSPRE.Avalonia;

/// <summary>Draws matrix squares over a multi-map view: each square's outline where that map tile sits
/// in the 3D scene and a short label, either for every square of a matrix being edited
/// (<see cref="SetMatrix"/>) or just for the maps on show (<see cref="SetCells"/>), with the selected
/// square filled in. Cell (row, col) is the square of <see cref="CellSize"/> world units centred on
/// (col, row) * CellSize, the same placement the composite scene gives each map.</summary>
public sealed class MatrixGridOverlay : Control
{
    private static readonly IPen CellPen = new Pen(new SolidColorBrush(Color.FromArgb(170, 101, 216, 199)), 1.5);
    private static readonly IPen SelectedPen = new Pen(new SolidColorBrush(Color.FromRgb(232, 183, 95)), 3);
    private static readonly IBrush SelectedFill = new SolidColorBrush(Color.FromArgb(60, 232, 183, 95));
    private static readonly IBrush LabelBackground = new SolidColorBrush(Color.FromArgb(170, 17, 22, 28));
    private static readonly IBrush LabelBrush = new SolidColorBrush(Color.FromRgb(237, 242, 247));
    private static readonly Typeface LabelTypeface = new("monospace");

    public const float CellSize = 512f;

    public MapGlView? Target { get; set; }
    public int Rows { get; private set; }
    public int Columns { get; private set; }
    public (int Row, int Column)? Selected { get; private set; }
    private Func<int, int, string>? labelFor;
    private Dictionary<(int Row, int Column), string>? cells;

    public MatrixGridOverlay()
    {
        IsHitTestVisible = false;
        SizeChanged += (_, _) => InvalidateVisual();
    }

    public void SetMatrix(int rows, int columns, Func<int, int, string> label)
    {
        Rows = rows;
        Columns = columns;
        labelFor = label;
        cells = null;
        InvalidateVisual();
    }

    /// <summary>Only these squares are drawn and can be picked.</summary>
    public void SetCells(IEnumerable<(int Row, int Column, string Label)> squares)
    {
        cells = squares.ToDictionary(square => (square.Row, square.Column), square => square.Label);
        Rows = cells.Count == 0 ? 0 : cells.Keys.Max(key => key.Row) + 1;
        Columns = cells.Count == 0 ? 0 : cells.Keys.Max(key => key.Column) + 1;
        labelFor = (row, column) => cells.TryGetValue((row, column), out string? label) ? label : string.Empty;
        Selected = null;
        InvalidateVisual();
    }

    public void Select(int row, int column)
    {
        Selected = (row, column);
        InvalidateVisual();
    }

    /// <summary>The cell a point in the scene's world coordinates falls in, if it is inside the matrix.</summary>
    public bool TryGetCell(float worldX, float worldZ, out int row, out int column)
    {
        column = (int)MathF.Floor(worldX / CellSize + 0.5f);
        row = (int)MathF.Floor(worldZ / CellSize + 0.5f);
        if (cells is not null) return cells.ContainsKey((row, column));
        return row >= 0 && row < Rows && column >= 0 && column < Columns;
    }

    public override void Render(DrawingContext context)
    {
        if (Target is null || labelFor is null) return;
        float height = Target.GroundHeight;
        for (int row = 0; row < Rows; row++)
        {
            for (int column = 0; column < Columns; column++)
            {
                if (cells is not null && !cells.ContainsKey((row, column))) continue;
                if (!TryProjectCell(row, column, height, out Point[] corners)) continue;
                var geometry = new StreamGeometry();
                using (StreamGeometryContext geometryContext = geometry.Open())
                {
                    geometryContext.BeginFigure(corners[0], true);
                    for (int i = 1; i < corners.Length; i++) geometryContext.LineTo(corners[i]);
                    geometryContext.EndFigure(true);
                }

                bool selected = Selected == (row, column);
                context.DrawGeometry(selected ? SelectedFill : null, selected ? SelectedPen : CellPen, geometry);

                var centre = new Point(corners.Average(point => point.X), corners.Average(point => point.Y));
                var text = new FormattedText(labelFor(row, column), CultureInfo.InvariantCulture, FlowDirection.LeftToRight, LabelTypeface, 11, LabelBrush);
                var box = new Rect(centre.X - text.Width / 2 - 3, centre.Y - text.Height / 2 - 1, text.Width + 6, text.Height + 2);
                context.FillRectangle(LabelBackground, box, 3);
                context.DrawText(text, box.TopLeft + new Vector(3, 1));
            }
        }
    }

    private bool TryProjectCell(int row, int column, float height, out Point[] corners)
    {
        corners = new Point[4];
        float half = CellSize / 2f;
        float centreX = column * CellSize;
        float centreZ = row * CellSize;
        (float X, float Z)[] world = [(centreX - half, centreZ - half), (centreX + half, centreZ - half), (centreX + half, centreZ + half), (centreX - half, centreZ + half)];
        for (int i = 0; i < world.Length; i++)
        {
            if (Target is null || !Target.TryProjectWorldPoint(world[i].X, height, world[i].Z, out corners[i])) return false;
        }

        return true;
    }
}
