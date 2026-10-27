namespace DSPRE.Core;

public interface IMapHeaderStore
{
    int Count { get; }
    MapHeaderRecord Load(ushort id);
    void Save(MapHeaderRecord header);
}

public static class MapHeaderStoreFactory
{
    /// <summary>Headers live in the extracted dynamic-headers directory when the ROM has the Dynamic Header
    /// Allocation patch, otherwise directly in ARM9's header table. The directory alone is not enough: an
    /// unpatched Platinum project used to extract its unrelated debug NARC there too.</summary>
    public static IMapHeaderStore? Create(RomProject project, RomGameMetadata metadata)
    {
        int headerCount = project.GetHeaderCount();
        if (headerCount == 0 || !File.Exists(project.Arm9Path)) return null;

        return DynamicHeadersPatch.IsApplied(project, metadata) && Directory.Exists(project.DynamicHeadersDirectory)
            ? new DirectoryMapHeaderStore(project.DynamicHeadersDirectory, headerCount, metadata.HeaderFormat)
            : new Arm9MapHeaderStore(project.Arm9Path, metadata.HeaderTableOffset, headerCount, metadata.HeaderFormat);
    }
}

public sealed class Arm9MapHeaderStore : IMapHeaderStore
{
    private readonly string arm9Path;
    private readonly long tableOffset;
    private readonly MapHeaderFormat format;

    public Arm9MapHeaderStore(string arm9Path, long tableOffset, int count, MapHeaderFormat format)
    {
        this.arm9Path = Path.GetFullPath(arm9Path);
        this.tableOffset = tableOffset;
        Count = count;
        this.format = format;
    }

    public int Count { get; }

    public MapHeaderRecord Load(ushort id)
    {
        ValidateId(id);
        using FileStream stream = File.OpenRead(arm9Path);
        stream.Position = HeaderOffset(id);
        byte[] data = new byte[MapHeaderRecord.SerializedLength];
        stream.ReadExactly(data);
        return MapHeaderRecord.Parse(data, id, format);
    }

    public void Save(MapHeaderRecord header) =>
        EditJournal.Track(null, EditJournalDocuments.MapHeader, EditJournalDocuments.Keys(("header", header.Id)), () => SaveUntracked(header));

    private void SaveUntracked(MapHeaderRecord header)
    {
        ValidateId(header.Id);
        if (header.Format != format)
        {
            throw new InvalidOperationException($"Header format {header.Format} does not match this store's format {format}.");
        }

        using FileStream stream = new FileStream(arm9Path, FileMode.Open, FileAccess.Write, FileShare.Read);
        stream.Position = HeaderOffset(header.Id);
        stream.Write(header.Serialize());
    }

    private long HeaderOffset(ushort id) => tableOffset + (long)id * MapHeaderRecord.SerializedLength;

    private void ValidateId(ushort id)
    {
        if (id >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, $"Header ID must be less than {Count}.");
        }
    }
}

public sealed class DirectoryMapHeaderStore : IMapHeaderStore
{
    private readonly string directoryPath;
    private readonly MapHeaderFormat format;

    public DirectoryMapHeaderStore(string directoryPath, int count, MapHeaderFormat format)
    {
        this.directoryPath = Path.GetFullPath(directoryPath);
        Count = count;
        this.format = format;
    }

    public int Count { get; }

    public MapHeaderRecord Load(ushort id)
    {
        ValidateId(id);
        string path = HeaderPath(id);
        return MapHeaderRecord.Parse(File.ReadAllBytes(path), id, format);
    }

    public void Save(MapHeaderRecord header) =>
        EditJournal.Track(null, EditJournalDocuments.MapHeader, EditJournalDocuments.Keys(("header", header.Id)), () => SaveUntracked(header));

    private void SaveUntracked(MapHeaderRecord header)
    {
        ValidateId(header.Id);
        if (header.Format != format)
        {
            throw new InvalidOperationException($"Header format {header.Format} does not match this store's format {format}.");
        }

        Directory.CreateDirectory(directoryPath);
        File.WriteAllBytes(HeaderPath(header.Id), header.Serialize());
    }

    private string HeaderPath(ushort id) => Path.Combine(directoryPath, id.ToString("D4"));

    private void ValidateId(ushort id)
    {
        if (id >= Count)
        {
            throw new ArgumentOutOfRangeException(nameof(id), id, $"Header ID must be less than {Count}.");
        }
    }
}
