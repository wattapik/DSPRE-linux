using System.Buffers.Binary;

namespace DSPRE.Core;

/// <summary>The animations a building (map prop) model has in HeartGold and SoulSilver. A list archive holds one
/// 24-byte record per model (a/1/0/7 for the outdoor models, a/1/0/8 for the indoor ones): flags, then four
/// signed indexes into the animation archive a/1/0/6 (-1 for none), whose files are joint (BCA0), texture
/// scroll (BTA0), texture swap (BTP0) or material (BMA0) animations - the layout is the decompilation's
/// MapPropAnimListFile. Only the texture scroll and joint ones that run by themselves are read here.</summary>
public static class BuildingAnimationService
{
    private const int RecordSize = 24;
    private const int AnimationsPerModel = 4;

    public static bool IsSupported(RomProject project, RomGameMetadata metadata) =>
        metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver && File.Exists(AnimationArchive(project)) && File.Exists(ListArchive(project, false));

    private static string AnimationArchive(RomProject project) => Path.Combine(project.FilesPath, "a", "1", "0", "6");

    private static string ListArchive(RomProject project, bool interior) => Path.Combine(project.FilesPath, "a", "1", "0", interior ? "8" : "7");

    /// <summary>The model's texture scroll animation, or null when it has none (or has only other kinds).</summary>
    public static TextureAnimation? LoadTextureAnimation(RomProject project, RomGameMetadata metadata, bool interior, int modelId) =>
        Load(project, metadata, interior, modelId, "BTA0"u8.ToArray(), TextureAnimation.Parse);

    /// <summary>The model's joint animation (a windmill's sails, a sign that turns), or null when it has none.</summary>
    public static JointAnimation? LoadJointAnimation(RomProject project, RomGameMetadata metadata, bool interior, int modelId) =>
        Load(project, metadata, interior, modelId, "BCA0"u8.ToArray(), JointAnimation.Parse);

    private static T? Load<T>(RomProject project, RomGameMetadata metadata, bool interior, int modelId, byte[] kind, Func<byte[], T> parse) where T : class
    {
        if (!IsSupported(project, metadata)) return null;
        string list = ListArchive(project, interior);
        if (modelId < 0 || modelId >= NarcFile.MemberCount(list)) return null;

        byte[] record = NarcFile.ReadMember(list, modelId);
        // 0xFF in the first byte marks a model with no animations. A flags byte of 0 means they are added to the
        // model as soon as it is placed, so they run by themselves; anything else (a door, a gate) waits for
        // the game to start it, which a still view has no way to do.
        if (record.Length < RecordSize || record[0] == 0xFF || record[1] != 0) return null;

        string animations = AnimationArchive(project);
        int count = NarcFile.MemberCount(animations);
        for (int i = 0; i < AnimationsPerModel; i++)
        {
            int index = BinaryPrimitives.ReadInt32LittleEndian(record.AsSpan(8 + 4 * i));
            if (index < 0 || index >= count) continue;
            byte[] file = NarcFile.ReadMember(animations, index);
            if (file.Length < 4 || !file.AsSpan(0, 4).SequenceEqual(kind)) continue;
            try
            {
                return parse(file);
            }
            catch (InvalidDataException)
            {
                // A file of a layout this reader does not know is skipped, as is any other kind.
            }
        }

        return null;
    }
}
