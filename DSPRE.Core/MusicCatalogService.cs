using System.Text;

namespace DSPRE.Core;

public static class MusicCatalogService
{
    public static IReadOnlyList<HeaderOption> GetMusic(RomProject project, RomGameMetadata metadata, IEnumerable<ushort>? referencedIds = null)
    {
        string? soundPath = FindSoundArchive(project, metadata);
        var options = new Dictionary<ushort, string>();
        if (soundPath is not null)
        {
            foreach ((ushort id, string name) in ReadSequenceNames(soundPath))
            {
                options[id] = name;
            }
        }

        foreach (ushort id in referencedIds ?? [])
        {
            if (!options.ContainsKey(id)) options[id] = "Unknown sequence";
        }

        return options.OrderBy(entry => entry.Key)
            .Select(entry => new HeaderOption(entry.Key, entry.Value))
            .ToArray();
    }

    private static string? FindSoundArchive(RomProject project, RomGameMetadata metadata)
    {
        string[] candidates = metadata.HeaderFormat switch
        {
            MapHeaderFormat.Platinum =>
            [
                Path.Combine(project.FilesPath, "data", "sound", "pl_sound_data.sdat"),
                Path.Combine(project.FilesPath, "data", "sound", "sound_data.sdat")
            ],
            MapHeaderFormat.DiamondPearl =>
            [Path.Combine(project.FilesPath, "data", "sound", "sound_data.sdat")],
            MapHeaderFormat.HeartGoldSoulSilver =>
            [
                Path.Combine(project.FilesPath, "data", "sound", "sound_data.sdat"),
                Path.Combine(project.FilesPath, "a", "0", "2", "1")
            ],
            _ => []
        };
        return candidates.FirstOrDefault(File.Exists);
    }

    private static IReadOnlyList<(ushort Id, string Name)> ReadSequenceNames(string path)
    {
        byte[] data = File.ReadAllBytes(path);
        var result = new List<(ushort, string)>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
    int index = 0;
        for (int offset = 0; offset < data.Length;)
        {
            int start = IndexOfSequencePrefix(data, offset);
            if (start < 0) break;
            int end = start;
            while (end < data.Length && data[end] >= 0x20 && data[end] <= 0x7E) end++;
            string name = Encoding.ASCII.GetString(data, start, end - start);
            if (name.StartsWith("SEQ_SE_", StringComparison.Ordinal))
            {
                break;
            }

            if (seen.Add(name))
            {
                result.Add(((ushort)(1000 + index), name));
                index++;
            }
            offset = end;
        }
        return result;
    }

    private static int IndexOfSequencePrefix(byte[] data, int offset)
    {
        for (int i = offset; i <= data.Length - 4; i++)
        {
            if (data[i] == (byte)'S' && data[i + 1] == (byte)'E' && data[i + 2] == (byte)'Q' && data[i + 3] == (byte)'_') return i;
        }
        return -1;
    }
}
