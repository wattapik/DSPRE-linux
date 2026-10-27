using System.Globalization;
using System.Text;

namespace DSPRE.Core;

/// <summary>The 32 x 32 grid of tile indices and heights a map is built from, in nine layers, stored as a
/// Pokemon DS Map Studio .pdsmap text file. Column 0 / row 0 is the map's south-west corner; a tile index of -1 is empty.</summary>
public sealed class MapCreatorMap
{
    public const int Columns = 32, Rows = 32, Layers = 9;
    public const int MinHeight = -15, MaxHeight = 15;
    public const string FileExtension = ".pdsmap";

    // Studio's game numbers: Diamond 0, Pearl 1, Platinum 2, HeartGold 3, SoulSilver 4.
    public int GameIndex { get; set; } = 2;
    public string TilesetFile { get; set; } = string.Empty;
    public int[][,] Tiles { get; } = Enumerable.Range(0, Layers).Select(_ => NewGrid(-1)).ToArray();
    public int[][,] Heights { get; } = Enumerable.Range(0, Layers).Select(_ => NewGrid(0)).ToArray();

    private static int[,] NewGrid(int fill)
    {
        var grid = new int[Columns, Rows];
        if (fill != 0) for (int c = 0; c < Columns; c++) for (int r = 0; r < Rows; r++) grid[c, r] = fill;
        return grid;
    }

    public bool IsEmpty => Tiles.All(layer => layer.Cast<int>().All(value => value < 0));

    public MapCreatorMap Clone()
    {
        var copy = new MapCreatorMap { GameIndex = GameIndex, TilesetFile = TilesetFile };
        for (int layer = 0; layer < Layers; layer++)
        {
            Array.Copy(Tiles[layer], copy.Tiles[layer], Tiles[layer].Length);
            Array.Copy(Heights[layer], copy.Heights[layer], Heights[layer].Length);
        }

        return copy;
    }

    public IEnumerable<int> TilesUsed() => Tiles.SelectMany(layer => layer.Cast<int>()).Where(value => value >= 0).Distinct();

    /// <summary>Rewrites tile indices after tiles were removed or reordered; an index missing from the table becomes empty.</summary>
    public void Remap(Func<int, int> map)
    {
        foreach (int[,] layer in Tiles)
        {
            for (int c = 0; c < Columns; c++) for (int r = 0; r < Rows; r++) if (layer[c, r] >= 0) layer[c, r] = map(layer[c, r]);
        }
    }

    public static MapCreatorMap Read(string path, int mapNumber = 0)
    {
        var map = new MapCreatorMap();
        string[] lines = File.ReadAllLines(path);
        int found = -1, tileLayer = 0, heightLayer = 0;
        for (int i = 0; i < lines.Length; i++)
        {
            string line = lines[i];
            if (line.StartsWith("gameindex", StringComparison.Ordinal)) map.GameIndex = int.Parse(lines[++i].Trim(), CultureInfo.InvariantCulture);
            else if (line.StartsWith("tileset", StringComparison.Ordinal)) map.TilesetFile = lines[++i].Trim();
            else if (line.StartsWith("mapstart", StringComparison.Ordinal))
            {
                found++;
                tileLayer = heightLayer = 0;
                i++;
            }
            else if (Math.Max(found, 0) == mapNumber && line.StartsWith("tilegrid", StringComparison.Ordinal) && tileLayer < Layers) ReadGrid(lines, ref i, map.Tiles[tileLayer++]);
            else if (Math.Max(found, 0) == mapNumber && line.StartsWith("heightgrid", StringComparison.Ordinal) && heightLayer < Layers) ReadGrid(lines, ref i, map.Heights[heightLayer++]);
        }

        if (found >= 0 && found < mapNumber) throw new InvalidDataException($"The file holds only {found + 1} map(s).");
        return map;
    }

    /// <summary>How many maps the .pdsmap holds (a Studio project can lay out several).</summary>
    public static int CountMaps(string path) => File.ReadLines(path).Count(line => line.StartsWith("mapstart", StringComparison.Ordinal));

    private static void ReadGrid(string[] lines, ref int index, int[,] grid)
    {
        for (int r = 0; r < Rows; r++)
        {
            string[] values = lines[++index].Split(' ', StringSplitOptions.RemoveEmptyEntries);
            for (int c = 0; c < Columns; c++) grid[c, r] = int.Parse(values[c], CultureInfo.InvariantCulture);
        }
    }

    public void Write(string path)
    {
        var text = new StringBuilder();
        text.Append("gameindex\n").Append(GameIndex.ToString(CultureInfo.InvariantCulture)).Append('\n');
        text.Append("tileset\n").Append(TilesetFile).Append('\n');
        text.Append("mapstart\n0 0\nareaindex\n0\nexportgroup\n0\n");
        foreach (int[,] layer in Tiles) AppendGrid(text, "tilegrid", layer);
        foreach (int[,] layer in Heights) AppendGrid(text, "heightgrid", layer);
        text.Append("mapend\n");
        File.WriteAllText(path, text.ToString());
    }

    private static void AppendGrid(StringBuilder text, string tag, int[,] grid)
    {
        text.Append(tag).Append('\n');
        for (int r = 0; r < Rows; r++)
        {
            for (int c = 0; c < Columns; c++) text.Append(grid[c, r].ToString(CultureInfo.InvariantCulture)).Append(' ');
            text.Append('\n');
        }
    }
}
