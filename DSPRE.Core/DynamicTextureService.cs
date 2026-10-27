namespace DSPRE.Core;

/// <summary>The scrolling-texture animation an area data's "dynamic textures" value selects. HeartGold and
/// SoulSilver keep them as NSBTA files in one archive (a/1/4/0), indexed by that value; 65535 means none.</summary>
public static class DynamicTextureService
{
    public const ushort None = 0xFFFF;

    public static string GetArchivePath(RomProject project) => Path.Combine(project.FilesPath, "a", "1", "4", "0");

    public static bool IsSupported(RomProject project, RomGameMetadata metadata) =>
        metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver && File.Exists(GetArchivePath(project));

    public static int Count(RomProject project, RomGameMetadata metadata) =>
        IsSupported(project, metadata) ? NarcFile.MemberCount(GetArchivePath(project)) : 0;

    /// <summary>The animation for the value, or null when it is "none", out of range or this game has no such archive.</summary>
    public static TextureAnimation? Load(RomProject project, RomGameMetadata metadata, int type)
    {
        if (type < 0 || type >= Count(project, metadata)) return null;
        return TextureAnimation.Parse(NarcFile.ReadMember(GetArchivePath(project), type));
    }
}
