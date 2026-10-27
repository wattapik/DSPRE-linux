namespace DSPRE.Core;

public sealed record MapViewEntry(int Id, string Name, long Size, string FilePath, string? LocationName = null)
{
    public override string ToString() => LocationName is null ? $"{Id:D4} - {Name}" : $"{Id:D4} - {LocationName} - {Name}";
}

public static class MapViewService
{
    public static IReadOnlyList<MapViewEntry> GetMaps(RomProject project, RomGameMetadata metadata)
    {
        if (!Directory.Exists(project.MapsDirectory)) return [];
        return Directory.GetFiles(project.MapsDirectory)
            .Select(path => new { Path = path, Name = Path.GetFileNameWithoutExtension(path) })
            .Where(item => int.TryParse(item.Name, out _))
            .Select(item => new FileInfo(item.Path))
            .OrderBy(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Select(file =>
            {
                int id = int.Parse(Path.GetFileNameWithoutExtension(file.Name)!);
                string name = ReadEmbeddedModelName(file.FullName, metadata.HeaderFormat);
                return new MapViewEntry(id, string.IsNullOrWhiteSpace(name) ? "Unnamed map" : name, file.Length, file.FullName);
            })
            .ToArray();
    }

    public static byte[] LoadBytes(MapViewEntry map) => File.ReadAllBytes(map.FilePath);

    private static string ReadEmbeddedModelName(string path, MapHeaderFormat format)
    {
        try
        {
            using var stream = File.OpenRead(path);
            using var reader = new BinaryReader(stream);
            // Section sizes first (permissions, buildings, ...), then on HGSS the background-sound block
            // (u16 signature, u16 length, entries), then the sections - see MapFileService.Parse.
            long sectionsBeforeModel = reader.ReadUInt32() + (long)reader.ReadUInt32();
            long modelStart = 0x10 + sectionsBeforeModel;
            if (format == MapHeaderFormat.HeartGoldSoulSilver)
            {
                stream.Position = 0x12;
                modelStart += 4 + reader.ReadUInt16();
            }

            stream.Position = modelStart + 0x14;

            if (stream.Position + 4 > stream.Length) return string.Empty;
            uint signature = reader.ReadUInt32();
            stream.Position += signature == 0x304C444D ? 0x1C : 0x20;
            byte[] name = reader.ReadBytes(16);
            return System.Text.Encoding.UTF8.GetString(name).TrimEnd('\0', ' ');
        }
        catch (EndOfStreamException)
        {
            return string.Empty;
        }
        catch (IOException)
        {
            return string.Empty;
        }
    }
}