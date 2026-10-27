namespace DSPRE.Core;

/// <summary>A map's events say where they are by the matrix square they stand in plus their place in the map, so moving a map to
/// another square of its matrix (expanding the matrix around it, say) leaves its warps, NPCs, signs and triggers behind in the
/// old square, on some other map or none, and a warp into the map then lands in black space. Saving a matrix through here moves
/// the events of every map that changed square along with it.</summary>
public static class MatrixEventShiftService
{
    public sealed record MapMove(int MapId, int FromRow, int FromColumn, int ToRow, int ToColumn);

    public sealed record Result(IReadOnlyList<MapMove> Moves, int EventsMoved, int SkippedMoves);

    /// <summary>The maps that appear once in both versions of a matrix, in different squares.</summary>
    public static List<MapMove> FindMoves(MatrixDocument before, MatrixDocument after)
    {
        Dictionary<int, (int Row, int Column)> Single(MatrixDocument matrix)
        {
            var seen = new Dictionary<int, (int Row, int Column)>();
            var repeated = new HashSet<int>();
            for (int row = 0; row < matrix.Height; row++)
            {
                for (int column = 0; column < matrix.Width; column++)
                {
                    int map = matrix.Maps[row, column];
                    if (map == MatrixDocument.EmptyMap) continue;
                    if (!seen.TryAdd(map, (row, column))) repeated.Add(map);
                }
            }

            foreach (int map in repeated) seen.Remove(map);
            return seen;
        }

        Dictionary<int, (int Row, int Column)> was = Single(before), now = Single(after);
        return was.Where(pair => now.TryGetValue(pair.Key, out (int Row, int Column) square) && square != pair.Value)
            .Select(pair => new MapMove(pair.Key, pair.Value.Row, pair.Value.Column, now[pair.Key].Row, now[pair.Key].Column))
            .OrderBy(move => move.MapId)
            .ToList();
    }

    /// <summary>Saves the matrix and moves the events of the maps that changed square (see <see cref="FindMoves"/>).</summary>
    public static Result Save(RomProject project, RomGameMetadata metadata, MatrixDocument matrix)
    {
        MatrixDocument? before = null;
        try
        {
            before = MatrixService.Load(project, matrix.Id);
        }
        catch (IOException)
        {
        }

        MatrixService.Save(project, matrix);
        IMapHeaderStore? store = MapHeaderStoreFactory.Create(project, metadata);
        if (before is null || store is null) return new Result([], 0, 0);

        List<MapMove> moves = FindMoves(before, matrix);
        int moved = 0, skipped = 0;
        var perFile = new Dictionary<int, List<MapMove>>();
        foreach (MapMove move in moves)
        {
            int? eventFile = EventFileOfSquare(project, store, before, move.FromRow, move.FromColumn);
            if (eventFile is null)
            {
                skipped++;
                continue;
            }

            if (!perFile.TryGetValue(eventFile.Value, out List<MapMove>? list)) perFile[eventFile.Value] = list = [];
            list.Add(move);
        }

        foreach ((int eventFileId, List<MapMove> fileMoves) in perFile)
        {
            if (!File.Exists(MapEventService.GetPath(project, eventFileId))) continue;
            MapEventFile file = MapEventService.Load(project, eventFileId);
            IEnumerable<MapEvent> all = file.Spawnables.Cast<MapEvent>().Concat(file.Overworlds).Concat(file.Warps).Concat(file.Triggers);
            var assignments = new List<(MapEvent Event, MapMove Move)>();
            foreach (MapEvent mapEvent in all)
            {
                MapMove? move = fileMoves.FirstOrDefault(candidate => candidate.FromColumn == mapEvent.MatrixPositionX && candidate.FromRow == mapEvent.MatrixPositionY);
                if (move is not null) assignments.Add((mapEvent, move));
            }

            if (assignments.Count == 0) continue;
            foreach ((MapEvent mapEvent, MapMove move) in assignments)
            {
                mapEvent.MatrixPositionX = (ushort)move.ToColumn;
                mapEvent.MatrixPositionY = (ushort)move.ToRow;
            }

            MapEventService.Save(project, file);
            moved += assignments.Count;
        }

        return new Result(moves, moved, skipped);
    }

    /// <summary>The event file of the header that owns a square: its own header when the matrix has a header per square, else the first header that uses the matrix.</summary>
    private static int? EventFileOfSquare(RomProject project, IMapHeaderStore store, MatrixDocument matrix, int row, int column)
    {
        if (matrix.HasHeadersSection && matrix.Headers[row, column] < store.Count) return store.Load(matrix.Headers[row, column]).EventFileId;
        for (ushort id = 0; id < store.Count; id++)
        {
            MapHeaderRecord header = store.Load(id);
            if (header.MatrixId == matrix.Id) return header.EventFileId;
        }

        return null;
    }
}
