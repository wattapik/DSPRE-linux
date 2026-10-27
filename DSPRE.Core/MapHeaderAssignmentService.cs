namespace DSPRE.Core;

/// <summary>Makes a header the one a map belongs to. The game and the editor reach a map through a header: its matrix places the
/// map, and when that matrix has a header per square the square's own header says which one. So the map's square is given the
/// header (a matrix that had no header section gets one first, every other square keeping the header that owned the matrix),
/// and the header is pointed at the matrix.</summary>
public static class MapHeaderAssignmentService
{
    public sealed record Result(int MatrixId, ushort PreviousMatrixId, bool AddedHeadersSection, int SquaresChanged, bool MatrixChanged);

    /// <summary>The matrix that places the map: the one of the header that reaches it now, else the first matrix holding it.</summary>
    public static int? FindMatrix(RomProject project, IMapHeaderStore store, int mapId)
    {
        ushort? owner = AreaDataService.FindHeaderForMap(project, store.Load, store.Count, mapId);
        if (owner is ushort id && Holds(project, store.Load(id).MatrixId, mapId)) return store.Load(id).MatrixId;
        foreach (int matrixId in MatrixService.GetIds(project))
        {
            if (Holds(project, matrixId, mapId)) return matrixId;
        }

        return null;
    }

    public static Result Assign(RomProject project, IMapHeaderStore store, int mapId, ushort headerId)
    {
        if (headerId >= store.Count) throw new ArgumentOutOfRangeException(nameof(headerId), $"There is no header {headerId}.");
        int matrixId = FindMatrix(project, store, mapId) ?? throw new InvalidOperationException($"Map {mapId} is not in any matrix, so no header can reach it. Put it in a matrix square first.");
        MatrixDocument matrix = MatrixService.Load(project, matrixId);

        bool added = false;
        if (!matrix.HasHeadersSection)
        {
            ushort previousOwner = headerId;
            for (ushort id = 0; id < store.Count; id++)
            {
                if (id == headerId || store.Load(id).MatrixId != matrixId) continue;
                previousOwner = id;
                break;
            }

            for (int row = 0; row < matrix.Height; row++)
            {
                for (int column = 0; column < matrix.Width; column++) matrix.Headers[row, column] = previousOwner;
            }

            matrix.HasHeadersSection = true;
            added = true;
        }

        int changed = 0;
        for (int row = 0; row < matrix.Height; row++)
        {
            for (int column = 0; column < matrix.Width; column++)
            {
                if (matrix.Maps[row, column] != mapId || matrix.Headers[row, column] == headerId) continue;
                matrix.Headers[row, column] = headerId;
                changed++;
            }
        }

        if (added || changed > 0) MatrixService.Save(project, matrix);

        MapHeaderRecord header = store.Load(headerId);
        ushort previousMatrix = header.MatrixId;
        bool matrixChanged = previousMatrix != matrixId;
        if (matrixChanged) store.Save(header with { MatrixId = (ushort)matrixId });
        return new Result(matrixId, previousMatrix, added, changed, matrixChanged);
    }

    private static bool Holds(RomProject project, int matrixId, int mapId)
    {
        try
        {
            MatrixDocument matrix = MatrixService.Load(project, matrixId);
            for (int row = 0; row < matrix.Height; row++)
            {
                for (int column = 0; column < matrix.Width; column++)
                {
                    if (matrix.Maps[row, column] == mapId) return true;
                }
            }
        }
        catch (IOException)
        {
        }

        return false;
    }
}
