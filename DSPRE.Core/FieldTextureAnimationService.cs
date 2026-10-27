namespace DSPRE.Core;

/// <summary>Platinum's animated map textures (water, flowers): <c>data/fldtanime.narc</c> holds a table of animations as its first
/// member (52 entries of a name, then pairs of frame number and how many game ticks it is shown, ended by 0xFF) and, as
/// member N+1, an NSBTX with the frame pictures of entry N. An entry applies to whichever map texture of its name an area's
/// texture pack has: the game keeps copying the current frame over that texture. (The area data's third value does not pick
/// the entry: packs hold several animated textures at once, and their areas' values do not follow them.)</summary>
public static class FieldTextureAnimationService
{
    private const int EntrySize = 52, NameLength = 16, MaxFrames = 18;

    /// <param name="TargetTexture">The map texture the frames replace, by name.</param>
    /// <param name="Frames">Which picture of <paramref name="Textures"/> to show, and for how many ticks (60 a second).</param>
    /// <param name="Textures">The NSBTX holding the frame pictures.</param>
    public sealed record Animation(string TargetTexture, IReadOnlyList<(int Texture, int Ticks)> Frames, byte[] Textures)
    {
        public int TotalTicks => Frames.Sum(frame => frame.Ticks);
    }

    public static string GetArchivePath(RomProject project) => Path.Combine(project.FilesPath, "data", "fldtanime.narc");

    public static bool IsSupported(RomProject project, RomGameMetadata metadata) =>
        metadata.HeaderFormat == MapHeaderFormat.Platinum && File.Exists(GetArchivePath(project));

    public static int Count(RomProject project, RomGameMetadata metadata)
    {
        if (!IsSupported(project, metadata)) return 0;
        byte[] table = NarcFile.ReadMember(GetArchivePath(project), 0);
        return table.Length < 4 ? 0 : Math.Min((int)BitConverter.ToUInt32(table, 0), (table.Length - 4) / EntrySize);
    }

    /// <summary>Every animation of the game, in table order.</summary>
    public static List<Animation> LoadAll(RomProject project, RomGameMetadata metadata) =>
        Enumerable.Range(0, Count(project, metadata)).Select(index => Load(project, metadata, index)).OfType<Animation>().ToList();

    /// <summary>The animation of a table entry, or null when it has no frames (or this game has none).</summary>
    public static Animation? Load(RomProject project, RomGameMetadata metadata, int index)
    {
        if (index < 0 || index >= Count(project, metadata)) return null;
        string path = GetArchivePath(project);
        byte[] table = NarcFile.ReadMember(path, 0);
        int start = 4 + index * EntrySize;
        string name = System.Text.Encoding.ASCII.GetString(table, start, NameLength).TrimEnd('\0', ' ');

        var frames = new List<(int, int)>();
        for (int i = 0; i < MaxFrames; i++)
        {
            byte texture = table[start + NameLength + i * 2];
            if (texture == 0xFF) break;
            frames.Add((texture, table[start + NameLength + i * 2 + 1]));
        }

        if (frames.Count == 0 || index + 1 >= NarcFile.MemberCount(path)) return null;
        return new Animation(name, frames, NarcFile.ReadMember(path, index + 1));
    }
}
