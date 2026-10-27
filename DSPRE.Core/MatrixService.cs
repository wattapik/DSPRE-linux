using System.Text;
using System.ComponentModel;
using System.Runtime.CompilerServices;

namespace DSPRE.Core;

public sealed class MatrixCell : INotifyPropertyChanged
{
    internal MatrixCell(MatrixDocument matrix, MatrixSection section, int row, int column, int value)
    {
        Matrix = matrix;
        Section = section;
        Row = row;
        Column = column;
        NumericValue = value;
    }

    internal MatrixDocument Matrix { get; }
    public MatrixSection Section { get; }
    public int Row { get; }
    public int Column { get; }
    public int NumericValue { get; private set; }
    public string Value => NumericValue == MatrixDocument.EmptyMap && Section == MatrixSection.Maps
        ? string.Empty
        : NumericValue.ToString();
    private int ColorValue => Section == MatrixSection.Maps ? NumericValue : Matrix.GetMapValue(Row, Column);
    public string Background => ColorValue switch
    {
        MatrixDocument.EmptyMap => "#000000",
        174 => "#0000FF",
        173 or 177 => "#008000",
        175 => "#A52A2A",
        _ => "#FFFFFF"
    };
    public string Foreground => ColorValue != MatrixDocument.EmptyMap && (ColorValue is 174 or 173 or 177 or 175)
        ? "#FFFFFF"
        : "#000000";

    public event PropertyChangedEventHandler? PropertyChanged;

    internal void SetNumericValue(int value)
    {
        NumericValue = value;
        OnPropertyChanged(nameof(Value));
        OnPropertyChanged(nameof(Background));
        OnPropertyChanged(nameof(Foreground));
    }

    internal void RefreshColors()
    {
        OnPropertyChanged(nameof(Background));
        OnPropertyChanged(nameof(Foreground));
    }

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

public sealed class MatrixRow
{
    internal MatrixRow(IEnumerable<MatrixCell> cells) => Cells = cells.ToList();
    public IReadOnlyList<MatrixCell> Cells { get; }
}

public enum MatrixSection
{
    Maps,
    Headers,
    Altitudes
}

public sealed class MatrixDocument
{
    private readonly List<MatrixCell> cells = [];
    public MatrixDocument(int id, string path, byte[] originalData, string name, bool hasHeaders, bool hasAltitudes, ushort[,] maps, ushort[,] headers, byte[,] altitudes)
    {
        Id = id;
        Path = path;
        OriginalData = originalData;
        Name = name;
        HasHeadersSection = hasHeaders;
        HasAltitudesSection = hasAltitudes;
        Maps = maps;
        Headers = headers;
        Altitudes = altitudes;
    }

    public const ushort EmptyMap = ushort.MaxValue;
    internal string Path { get; }
    internal byte[] OriginalData { get; }
    public int Id { get; }
    public string Name { get; set; }
    public bool HasHeadersSection { get; set; }
    public bool HasAltitudesSection { get; set; }
    public ushort[,] Maps { get; private set; }
    public ushort[,] Headers { get; private set; }
    public byte[,] Altitudes { get; private set; }
    public int Width => Maps.GetLength(1);
    public int Height => Maps.GetLength(0);

    internal int GetMapValue(int row, int column) => Maps[row, column];

    public IReadOnlyList<MatrixRow> BuildRows(MatrixSection section)
    {
        var rows = new List<MatrixRow>();
        for (int row = 0; row < Height; row++)
        {
            var cells = new List<MatrixCell>();
            for (int column = 0; column < Width; column++)
            {
                int value = section switch
                {
                    MatrixSection.Maps => Maps[row, column],
                    MatrixSection.Headers => Headers[row, column],
                    MatrixSection.Altitudes => Altitudes[row, column],
                    _ => 0
                };
                cells.Add(new MatrixCell(this, section, row, column, value));
                this.cells.Add(cells[^1]);
            }
            rows.Add(new MatrixRow(cells));
        }
        return rows;
    }

    public void SetCell(MatrixCell cell, string? value)
    {
        int parsed = cell.Section == MatrixSection.Altitudes
            ? TryParseByte(value)
            : cell.Section == MatrixSection.Maps ? TryParseUShort(value, EmptyMap) : TryParseUShort(value, 0);
        cell.SetNumericValue(parsed);
        switch (cell.Section)
        {
            case MatrixSection.Maps: Maps[cell.Row, cell.Column] = (ushort)parsed; break;
            case MatrixSection.Headers: Headers[cell.Row, cell.Column] = (ushort)parsed; break;
            case MatrixSection.Altitudes: Altitudes[cell.Row, cell.Column] = (byte)parsed; break;
        }

        if (cell.Section == MatrixSection.Maps)
        {
            foreach (MatrixCell dependent in cells.Where(existing => existing.Row == cell.Row && existing.Column == cell.Column))
            {
                dependent.RefreshColors();
            }
        }
    }

    public void Resize(int height, int width)
    {
        height = Math.Clamp(height, 1, byte.MaxValue);
        width = Math.Clamp(width, 1, byte.MaxValue);
        var maps = NewUShortTable(height, width, EmptyMap);
        var headers = NewUShortTable(height, width, 0);
        var altitudes = new byte[height, width];
        Copy(Maps, maps); Copy(Headers, headers); Copy(Altitudes, altitudes);
        Maps = maps; Headers = headers; Altitudes = altitudes;
    }

    public byte[] ToByteArray()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        byte[] name = Encoding.UTF8.GetBytes(Name ?? string.Empty);
        writer.Write((byte)Width); writer.Write((byte)Height);
        writer.Write(HasHeadersSection); writer.Write(HasAltitudesSection);
        writer.Write((byte)Math.Min(name.Length, byte.MaxValue)); writer.Write(name, 0, Math.Min(name.Length, byte.MaxValue));
        if (HasHeadersSection) WriteUShortTable(writer, Headers);
        if (HasAltitudesSection) WriteByteTable(writer, Altitudes);
        WriteUShortTable(writer, Maps);
        return stream.ToArray();
    }

    private static int TryParseUShort(string? value, ushort fallback) => ushort.TryParse(value, out ushort result) ? result : fallback;
    private static int TryParseByte(string? value) => byte.TryParse(value, out byte result) ? result : 0;
    private static ushort[,] NewUShortTable(int height, int width, ushort value) { var table = new ushort[height, width]; if (value != 0) for (int r = 0; r < height; r++) for (int c = 0; c < width; c++) table[r, c] = value; return table; }
    private static void Copy<T>(T[,] source, T[,] target) { for (int r = 0; r < Math.Min(source.GetLength(0), target.GetLength(0)); r++) for (int c = 0; c < Math.Min(source.GetLength(1), target.GetLength(1)); c++) target[r, c] = source[r, c]; }
    private static void WriteUShortTable(BinaryWriter writer, ushort[,] table) { for (int r = 0; r < table.GetLength(0); r++) for (int c = 0; c < table.GetLength(1); c++) writer.Write(table[r, c]); }
    private static void WriteByteTable(BinaryWriter writer, byte[,] table) { for (int r = 0; r < table.GetLength(0); r++) for (int c = 0; c < table.GetLength(1); c++) writer.Write(table[r, c]); }
}

public static class MatrixService
{
    public static IReadOnlyList<int> GetIds(RomProject project) => Directory.Exists(project.MatricesDirectory)
        ? Directory.GetFiles(project.MatricesDirectory)
            .Select(path => Path.GetFileNameWithoutExtension(path))
            .Where(name => int.TryParse(name, out _))
            .Select(name => int.Parse(name!))
            .OrderBy(id => id)
            .ToArray()
        : [];

    public static MatrixDocument Load(RomProject project, int id)
    {
        string path = System.IO.Path.Combine(project.MatricesDirectory, $"{id:D4}");
        return LoadPath(path, id);
    }

    public static MatrixDocument LoadPath(string path, int id = -1)
    {
        byte[] data = File.ReadAllBytes(path);
        using var reader = new BinaryReader(new MemoryStream(data), Encoding.UTF8);
        int width = reader.ReadByte(), height = reader.ReadByte();
        bool headers = reader.ReadBoolean(), altitudes = reader.ReadBoolean();
        int nameLength = reader.ReadByte();
        string name = Encoding.UTF8.GetString(reader.ReadBytes(nameLength));
        ushort[,] headerTable = new ushort[height, width];
        byte[,] altitudeTable = new byte[height, width];
        ushort[,] mapTable = new ushort[height, width];
        if (headers) ReadUShortTable(reader, headerTable);
        if (altitudes) ReadByteTable(reader, altitudeTable);
        ReadUShortTable(reader, mapTable);
        return new MatrixDocument(id, path, data, name, headers, altitudes, mapTable, headerTable, altitudeTable);
    }

    public static void Save(RomProject project, MatrixDocument matrix) =>
        EditJournal.Track(project, EditJournalDocuments.Matrix, EditJournalDocuments.Keys(("matrix", matrix.Id)), () => SaveUntracked(project, matrix));

    private static void SaveUntracked(RomProject project, MatrixDocument matrix) => File.WriteAllBytes(System.IO.Path.Combine(project.MatricesDirectory, $"{matrix.Id:D4}"), matrix.ToByteArray());

    /// <summary>Appends a new 1x1 matrix after the highest existing ID and returns its ID.</summary>
    public static int Add(RomProject project)
    {
        IReadOnlyList<int> ids = GetIds(project);
        int id = ids.Count == 0 ? 0 : ids.Max() + 1;
        Directory.CreateDirectory(project.MatricesDirectory);
        var matrix = new MatrixDocument(id, string.Empty, [], "newMatrix", false, false, new ushort[1, 1] { { 0 } }, new ushort[1, 1], new byte[1, 1]);
        SaveUntracked(project, matrix);
        EditJournal.Record(project, "CreateMatrix", ("matrix", id));
        return id;
    }

    public static bool RemoveLast(RomProject project)
    {
        IReadOnlyList<int> ids = GetIds(project);
        if (ids.Count <= 1) return false;
        File.Delete(System.IO.Path.Combine(project.MatricesDirectory, $"{ids[^1]:D4}"));
        EditJournal.Record(project, "DeleteLastMatrix", ("matrix", ids[^1]));
        return true;
    }

    private static void ReadUShortTable(BinaryReader reader, ushort[,] table) { for (int r = 0; r < table.GetLength(0); r++) for (int c = 0; c < table.GetLength(1); c++) table[r, c] = reader.ReadUInt16(); }
    private static void ReadByteTable(BinaryReader reader, byte[,] table) { for (int r = 0; r < table.GetLength(0); r++) for (int c = 0; c < table.GetLength(1); c++) table[r, c] = reader.ReadByte(); }
}