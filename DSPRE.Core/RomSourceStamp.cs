using System.Text.Json;

namespace DSPRE.Core;

/// <summary>Records which ROM file a contents folder was extracted from, so opening that ROM again can
/// reuse the folder (and every edit in it) instead of extracting from scratch.</summary>
public sealed record RomSourceStamp(string RomPath, long Length, DateTime LastWriteUtc)
{
    private const string FileName = ".dspre-source.json";

    public static RomSourceStamp FromRom(string romPath)
    {
        var info = new FileInfo(romPath);
        return new RomSourceStamp(info.FullName, info.Length, info.LastWriteTimeUtc);
    }

    public static RomSourceStamp? Read(string projectDirectory)
    {
        string path = Path.Combine(projectDirectory, FileName);
        try
        {
            return File.Exists(path) ? JsonSerializer.Deserialize<RomSourceStamp>(File.ReadAllText(path)) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    public void Write(string projectDirectory) =>
        File.WriteAllText(Path.Combine(projectDirectory, FileName), JsonSerializer.Serialize(this));

    /// <summary>Same file size and modification time: the ROM has not been replaced or rebuilt since
    /// the folder was extracted from it.</summary>
    public bool Matches(RomSourceStamp other) => Length == other.Length && LastWriteUtc == other.LastWriteUtc;
}
