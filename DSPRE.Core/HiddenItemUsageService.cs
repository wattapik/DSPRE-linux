namespace DSPRE.Core;

/// <summary>A hidden item spawnable: the event that runs script 8000 + <see cref="ScriptId"/>.</summary>
public sealed record HiddenItemUse(int ScriptId, int EventFileId, int SpawnableIndex, int X, int Y, int? HeaderId);

/// <summary>Where the project's hidden item spawnables are, from the saved event files: which map each entry of the table is on, and
/// which spawnables point at an ID the table does not have.</summary>
public static class HiddenItemUsageService
{
    public const int ScriptBase = 8000;

    public static List<HiddenItemUse> Find(RomProject project, RomGameMetadata metadata)
    {
        var uses = new List<HiddenItemUse>();
        if (!Directory.Exists(project.EventsDirectory)) return uses;

        IMapHeaderStore? headers = MapHeaderStoreFactory.Create(project, metadata);
        var headerOfEventFile = new Dictionary<int, int>();
        if (headers is not null)
        {
            for (int id = 0; id < headers.Count; id++) headerOfEventFile.TryAdd(headers.Load((ushort)id).EventFileId, id);
        }

        foreach (string path in Directory.GetFiles(project.EventsDirectory).Order(StringComparer.Ordinal))
        {
            if (!int.TryParse(Path.GetFileName(path), out int eventFileId)) continue;
            MapEventFile file;
            try
            {
                file = MapEventService.Load(project, eventFileId);
            }
            catch (Exception ex) when (ex is IOException or EndOfStreamException or InvalidDataException)
            {
                continue;
            }

            for (int i = 0; i < file.Spawnables.Count; i++)
            {
                SpawnableEvent spawnable = file.Spawnables[i];
                if (spawnable.Type != SpawnableEvent.TypeHiddenItem || spawnable.ScriptNumber < ScriptBase) continue;
                uses.Add(new HiddenItemUse(spawnable.ScriptNumber - ScriptBase, eventFileId, i,
                    spawnable.MapPositionX + 32 * spawnable.MatrixPositionX, spawnable.MapPositionY + 32 * spawnable.MatrixPositionY,
                    headerOfEventFile.TryGetValue(eventFileId, out int header) ? header : null));
            }
        }

        return uses;
    }
}
