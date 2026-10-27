namespace DSPRE.Core;

/// <summary>Reads the per-building-tileset "building config" files (area_build.narc in DP/Pt) - the
/// game's own list of which building models are loaded alongside each building texture pack. A model
/// placed on a map but missing from its area's list is what the game renders as dmybox00. The legacy
/// editor never parsed these (it only copies file 0000 when adding a pack), so the layout here - a
/// u16 model count followed by that many u16 model IDs - is taken from the files themselves.</summary>
public static class BuildingConfigService
{
    /// <summary>False when the pack has no config file or the file does not match the expected
    /// layout, so callers can fall back to another way of deciding which models are usable.</summary>
    public static bool TryGetModelIds(RomProject project, int buildingTexturePackId, out IReadOnlyList<int> modelIds)
    {
        modelIds = [];
        if (buildingTexturePackId < 0) return false;

        string path = Path.Combine(project.BuildingConfigsDirectory, $"{buildingTexturePackId:D4}");
        if (!File.Exists(path)) return false;

        byte[] data = File.ReadAllBytes(path);
        if (data.Length < 2) return false;

        int count = BitConverter.ToUInt16(data, 0);
        if (data.Length != 2 + count * 2) return false;

        var ids = new int[count];
        for (int i = 0; i < count; i++) ids[i] = BitConverter.ToUInt16(data, 2 + i * 2);
        modelIds = ids;
        return true;
    }
}
