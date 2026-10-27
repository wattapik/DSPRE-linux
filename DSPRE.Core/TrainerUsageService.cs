namespace DSPRE.Core;

/// <summary>One place a trainer is fought: a header whose event file has the trainer's overworld, or whose
/// script file starts a battle with it.</summary>
public sealed record TrainerUse(int HeaderId, string How);

/// <summary>Which maps each trainer is used on, the reverse of the Map Editor's Trainers pane: trainer
/// overworlds in each header's event file (TrainerService.GetTrainerIdForOverworldScript), and battles
/// started from each header's script file (ScriptFileService.FindTrainerBattles).</summary>
public static class TrainerUsageService
{
    /// <param name="loadEvents">An event file by id; the caller's copy, so events edited but not yet
    /// saved count too.</param>
    public static Dictionary<int, List<TrainerUse>> FindAll(RomProject project, RomGameMetadata metadata, Func<int, MapEventFile> loadEvents)
    {
        var uses = new Dictionary<int, List<TrainerUse>>();
        if (MapHeaderStoreFactory.Create(project, metadata) is not { } store) return uses;

        var eventTrainers = new Dictionary<int, List<int>>();
        var scriptBattles = new Dictionary<int, List<ScriptTrainerBattle>>();
        for (ushort header = 0; header < project.GetHeaderCount(); header++)
        {
            MapHeaderRecord record = store.Load(header);
            if (!eventTrainers.TryGetValue(record.EventFileId, out List<int>? placed))
            {
                placed = [];
                try
                {
                    foreach (OverworldEvent overworld in loadEvents(record.EventFileId).Overworlds)
                    {
                        if (overworld.Type == OverworldEvent.TypeTrainer
                            && TrainerService.GetTrainerIdForOverworldScript(metadata, overworld.ScriptNumber) is int trainer && !placed.Contains(trainer))
                        {
                            placed.Add(trainer);
                        }
                    }
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                }
                eventTrainers[record.EventFileId] = placed;
            }
            foreach (int trainer in placed) Add(uses, trainer, new TrainerUse(header, $"overworld in event file {record.EventFileId:D4}"));

            if (!scriptBattles.TryGetValue(record.ScriptFileId, out List<ScriptTrainerBattle>? battles))
            {
                battles = ScriptFileService.FindTrainerBattles(project, record.ScriptFileId);
                scriptBattles[record.ScriptFileId] = battles;
            }
            foreach (ScriptTrainerBattle battle in battles)
            {
                Add(uses, battle.TrainerId, new TrainerUse(header, $"{battle.Command} in {battle.Section} of script file {record.ScriptFileId:D4}"));
            }
        }
        return uses;
    }

    private static void Add(Dictionary<int, List<TrainerUse>> uses, int trainer, TrainerUse use)
    {
        if (!uses.TryGetValue(trainer, out List<TrainerUse>? list)) uses[trainer] = list = [];
        if (!list.Contains(use)) list.Add(use);
    }
}
