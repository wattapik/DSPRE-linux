namespace DSPRE.Core;

/// <summary>Replaces and exports whole building model (NSBMD) files in the exterior or (HGSS-only)
/// interior model set - the Core counterpart of the legacy DS_Map/Editors/BuildingEditor.cs.</summary>
public static class BuildingModelService
{
    private const uint NsbmdSignature = 0x30444D42;

    public static string GetDirectory(RomProject project, bool interior) =>
        interior ? project.InteriorBuildingModelsDirectory : project.ExteriorBuildingModelsDirectory;

    public static string GetPath(RomProject project, bool interior, int id) =>
        Path.Combine(GetDirectory(project, interior), $"{id:D4}");

    /// <summary>Where converted previews of one model are cached, one subfolder per texture pack.</summary>
    public static string GetPreviewDirectory(RomProject project, bool interior, int id) =>
        Path.Combine(GetPreviewRoot(project), interior ? "interior" : "exterior", $"{id:D4}");

    public static bool IsNsbmd(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);
        return stream.Length >= 4 && reader.ReadUInt32() == NsbmdSignature;
    }

    public static void Import(RomProject project, bool interior, int id, string sourcePath)
    {
        string fullSource = Path.GetFullPath(sourcePath);
        if (!IsNsbmd(fullSource)) throw new InvalidDataException("The selected file is not an NSBMD model.");

        File.Copy(fullSource, GetPath(project, interior, id), true);

        // Both caches hold models converted from the file that was just replaced: this model's own
        // previews, and every map scene that places it.
        DeleteDirectory(GetPreviewRoot(project));
        DeleteDirectory(Path.Combine(project.RootPath, "expanded", "map_models"));

        EditJournal.Record(project, "ImportBuildingModel", ("interior", interior), ("model", id), ("source", fullSource));
    }

    public static void Export(RomProject project, bool interior, int id, string destinationPath) =>
        File.Copy(GetPath(project, interior, id), destinationPath, true);

    private static string GetPreviewRoot(RomProject project) =>
        Path.Combine(project.RootPath, "expanded", "building_models");

    private static void DeleteDirectory(string path)
    {
        if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
    }
}
