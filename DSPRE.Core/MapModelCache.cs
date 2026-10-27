using System.Text.Json;

namespace DSPRE.Core;

/// <summary>Decides whether a map's converted 3D model (the scene.json the export bridge writes) can
/// be reused. Converting runs apicula once per model under the bridge, which is by far the slowest
/// part of opening a map, so a conversion is only redone when something it was made from changed.</summary>
public static class MapModelCache
{
    /// <param name="interiorBuildings">Whether the map places HGSS interior building models; a different
    /// set of models under the same numbers, so it gets its own folder.</param>
    public static string GetModelDirectory(RomProject project, int mapId, int texturePackId, int buildingTexturePackId, bool interiorBuildings = false) =>
        Path.Combine(project.RootPath, "expanded", "map_models", mapId.ToString("D4"), $"tex{texturePackId:D2}_bld{buildingTexturePackId:D2}{(interiorBuildings ? "_int" : "")}");

    /// <summary>True when the scene exists and is newer than the map file, both texture packs and
    /// every building model file.</summary>
    public static bool IsFresh(RomProject project, int mapId, int texturePackId, int buildingTexturePackId, string scenePath)
    {
        if (!File.Exists(scenePath) || !HasCurrentVersion(scenePath)) return false;
        DateTime built = File.GetLastWriteTimeUtc(scenePath);
        return LatestInputWrite(project, mapId, texturePackId, buildingTexturePackId) < built;
    }

    // Version 2 manifests keep an empty part for every building that could not be drawn, so part
    // indices match building indices; older ones skipped them, shifting every later building.
    private const int SceneVersion = 2;

    private static bool HasCurrentVersion(string scenePath)
    {
        try
        {
            using JsonDocument scene = JsonDocument.Parse(File.ReadAllText(scenePath));
            return scene.RootElement.TryGetProperty("Version", out JsonElement version) &&
                version.TryGetInt32(out int value) && value >= SceneVersion;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static DateTime LatestInputWrite(RomProject project, int mapId, int texturePackId, int buildingTexturePackId)
    {
        DateTime latest = DateTime.MinValue;
        void Consider(string path)
        {
            if (File.Exists(path))
            {
                DateTime written = File.GetLastWriteTimeUtc(path);
                if (written > latest) latest = written;
            }
        }

        Consider(Path.Combine(project.MapsDirectory, mapId.ToString("D4")));
        if (texturePackId >= 0) Consider(Path.Combine(project.MapTexturesDirectory, texturePackId.ToString("D4")));
        if (buildingTexturePackId >= 0) Consider(Path.Combine(project.BuildingTexturesDirectory, buildingTexturePackId.ToString("D4")));
        foreach (string directory in new[] { project.ExteriorBuildingModelsDirectory, project.InteriorBuildingModelsDirectory })
        {
            if (!Directory.Exists(directory)) continue;
            foreach (string file in Directory.EnumerateFiles(directory)) Consider(file);
        }

        return latest;
    }
}
