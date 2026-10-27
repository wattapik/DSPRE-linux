namespace DSPRE.Core;

/// <summary>Deletes one map. Every matrix square that placed it is emptied. No map is renumbered, since the game's code may name
/// map numbers: the last map's file is removed, any other becomes a placeholder (the smallest model in the project, no
/// permissions or buildings) so the numbers after it stay put. Events standing in the emptied squares are left alone.</summary>
public static class MapDeletionService
{
    public sealed record Report(int MapId, int SquaresEmptied, bool Removed);

    public static Report Plan(RomProject project, RomGameMetadata metadata, int mapId) => Run(project, metadata, mapId, commit: false);

    public static Report Delete(RomProject project, RomGameMetadata metadata, int mapId)
    {
        Report report;
        using (EditJournal.Suppress())
        {
            report = Run(project, metadata, mapId, commit: true);
        }

        EditJournal.Record(project, "DeleteMap", ("map", mapId));
        return report;
    }

    private static Report Run(RomProject project, RomGameMetadata metadata, int mapId, bool commit)
    {
        string path = MapFileService.GetPath(project, mapId);
        if (!File.Exists(path)) throw new InvalidOperationException($"Map file {mapId:D4} does not exist.");
        int last = HeaderFileService.NextFileId(project.MapsDirectory) - 1;
        if (last < 1) throw new InvalidOperationException("At least one map file must stay.");

        int emptied = 0;
        foreach (string file in Directory.Exists(project.MatricesDirectory) ? Directory.GetFiles(project.MatricesDirectory) : [])
        {
            if (!int.TryParse(Path.GetFileName(file), out int id)) continue;
            MatrixDocument matrix = MatrixService.Load(project, id);
            bool changed = false;
            for (int r = 0; r < matrix.Height; r++)
            {
                for (int c = 0; c < matrix.Width; c++)
                {
                    ushort map = matrix.Maps[r, c];
                    if (map == MatrixDocument.EmptyMap) continue;
                    if (map == mapId)
                    {
                        matrix.Maps[r, c] = MatrixDocument.EmptyMap;
                        emptied++;
                        changed = true;
                    }
                }
            }

            if (commit && changed) MatrixService.Save(project, matrix);
        }

        if (!commit) return new Report(mapId, emptied, mapId == last);

        if (mapId == last)
        {
            File.Delete(path);
        }
        else
        {
            MapFileParts template = Enumerable.Range(0, last + 1).Where(id => id != mapId && File.Exists(MapFileService.GetPath(project, id)))
                .Select(id => MapFileService.Load(project, metadata, id)).MinBy(parts => parts.Model.Length)!;
            File.WriteAllBytes(path, MapFileService.Serialize(template with { Permissions = new byte[template.Permissions.Length], Buildings = [] }, MapFileService.IsHgss(metadata)));
        }

        string creator = MapCreatorDocument.RootDirectory(project);
        if (Directory.Exists(creator))
        {
            foreach (string file in Directory.GetDirectories(creator).Select(directory => Path.Combine(directory, "built.txt")).Where(File.Exists))
            {
                if (int.TryParse(File.ReadAllText(file).Split(' ')[0], out int map) && map == mapId) File.Delete(file);
            }
        }

        foreach (string cache in new[] { "map_models", "map_preview" })
        {
            string cachePath = Path.Combine(project.RootPath, "expanded", cache);
            if (Directory.Exists(cachePath)) Directory.Delete(cachePath, true);
        }

        return new Report(mapId, emptied, mapId == last);
    }
}
