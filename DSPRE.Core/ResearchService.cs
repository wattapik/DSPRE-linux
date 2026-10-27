using System.Globalization;
using System.Text.RegularExpressions;

namespace DSPRE.Core;

public sealed record ScriptFileStats(int Id, int Scripts, int Functions, int Actions)
{
    public int Total => Scripts + Functions + Actions;
}

public sealed record LevelScriptStats(int Id, int MapChange, int ScreenReset, int LoadGame, int VariableValue)
{
    public int Total => MapChange + ScreenReset + LoadGame + VariableValue;
}

/// <summary>A file that uses a variable or flag: "Script", "Level Script" or "Event".</summary>
public sealed record UsageResult(string FileType, int FileId, int Uses, string Details);

public sealed record ScriptFileReference(int HeaderId, string Field);

public sealed record ScriptIdUse(int EventFileId, string EventType, int Index, string Details);

public sealed record WarpLink(int EventFileId, int WarpIndex, int X, int Y, int Header, int Anchor);

/// <summary>Legacy's Research Helper (DS_Map/ResearchHelper.cs): statistics over every script and level
/// script, and where a variable, flag, script file, script number or map header is used. Scripts are read
/// from their plaintext (what the Script Editor edits), with the command database telling which
/// arguments are variables; the counting rules are legacy's, heuristics included.</summary>
public sealed class ResearchService
{
    private sealed record Command(string Name, IReadOnlyList<string> Arguments);

    private sealed record ParsedScriptFile(int Id, List<Command> Commands, ScriptFileStats Stats);

    private static readonly Regex SectionHeader = new(@"^(Script|Function|Action)\s*#?\s*\d+:$");

    private readonly IMapHeaderStore? headers;
    private readonly Dictionary<string, ScriptCommandInfo> commandsByName = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<ParsedScriptFile> scriptFiles = [];
    private readonly List<LevelScriptFile> levelScripts = [];
    private readonly List<MapEventFile> eventFiles = [];

    public IReadOnlyList<ScriptFileStats> ScriptStats { get; }
    public IReadOnlyList<LevelScriptStats> LevelScriptStats { get; }
    public int EventFileCount => eventFiles.Count;
    public int HeaderCount => headers?.Count ?? 0;
    /// <summary>Files that could not be read, for the status line.</summary>
    public List<string> Problems { get; } = [];

    private ResearchService(RomProject project, RomGameMetadata metadata, ScriptCommandDatabase? database)
    {
        headers = MapHeaderStoreFactory.Create(project, metadata);
        foreach (ScriptCommandInfo command in database?.Commands.Values ?? [])
        {
            if (command.Name.Length > 0) commandsByName.TryAdd(command.Name, command);
            if (command.DecompName.Length > 0) commandsByName.TryAdd(command.DecompName, command);
        }

        var levelScriptIds = LevelScriptService.GetIds(project).ToHashSet();
        foreach (int id in levelScriptIds)
        {
            try
            {
                levelScripts.Add(LevelScriptService.Load(project, id));
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                Problems.Add($"Level script {id}: {ex.Message}");
            }
        }

        if (Directory.Exists(project.ExpandedScriptsDirectory))
        {
            foreach (string path in Directory.GetFiles(project.ExpandedScriptsDirectory, "*.script"))
            {
                if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out int id) || levelScriptIds.Contains(id)) continue;
                scriptFiles.Add(ParseScriptFile(id, File.ReadAllLines(path)));
            }
        }

        scriptFiles.Sort((a, b) => a.Id.CompareTo(b.Id));
        if (Directory.Exists(project.EventsDirectory))
        {
            foreach (string path in Directory.GetFiles(project.EventsDirectory))
            {
                if (!int.TryParse(Path.GetFileName(path), out int id)) continue;
                try
                {
                    eventFiles.Add(MapEventService.Parse(File.ReadAllBytes(path), id));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
                {
                    Problems.Add($"Event file {id}: {ex.Message}");
                }
            }
        }

        eventFiles.Sort((a, b) => a.Id.CompareTo(b.Id));
        ScriptStats = scriptFiles.Select(file => file.Stats).ToList();
        LevelScriptStats = levelScripts.Select(file => new LevelScriptStats(file.Id,
            file.Triggers.Count(t => t.TriggerType == LevelScriptTriggerType.MapChange),
            file.Triggers.Count(t => t.TriggerType == LevelScriptTriggerType.ScreenReset),
            file.Triggers.Count(t => t.TriggerType == LevelScriptTriggerType.LoadGame),
            file.Triggers.Count(t => t.TriggerType == LevelScriptTriggerType.VariableValue))).ToList();
    }

    /// <summary>Reads every script, level script and event file once. The command database is the one the
    /// ROM's scripts are parsed with when there is one, else the built-in one in
    /// <paramref name="databasesDirectory"/>; without either, every argument counts as a plain number.</summary>
    public static ResearchService Load(RomProject project, RomGameMetadata metadata, string databasesDirectory)
    {
        string? path = File.Exists(ScriptDatabaseStore.ProjectDatabasePath(project))
            ? ScriptDatabaseStore.ProjectDatabasePath(project)
            : ScriptCommandDatabaseService.ResolvePath(databasesDirectory, metadata.HeaderFormat);
        ScriptCommandDatabase? database = null;
        if (path is not null)
        {
            try
            {
                database = ScriptCommandDatabaseService.Load(path);
            }
            catch (Exception ex)
            {
                StatusLog.Warning("Research Helper", $"The script command database could not be read, so variables are found by value only: {ex.Message}");
            }
        }

        return new ResearchService(project, metadata, database);
    }

    /// <summary>Variables and flags are only looked for in scripts and functions, as legacy does: actions
    /// hold movements, not commands.</summary>
    private static ParsedScriptFile ParseScriptFile(int id, string[] lines)
    {
        var commands = new List<Command>();
        int scripts = 0, functions = 0, actions = 0;
        bool inAction = false;
        foreach (string raw in lines)
        {
            string line = raw.Trim();
            if (line.Length == 0 || line.StartsWith("//", StringComparison.Ordinal) || line.StartsWith("/*", StringComparison.Ordinal) || line.StartsWith('*')) continue;
            if (SectionHeader.Match(line) is { Success: true } header)
            {
                switch (header.Groups[1].Value)
                {
                    case "Script": scripts++; inAction = false; break;
                    case "Function": functions++; inAction = false; break;
                    default: actions++; inAction = true; break;
                }

                continue;
            }

            if (inAction || line is "End" or "Return") continue;
            string[] parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
            commands.Add(new Command(parts[0], parts[1..]));
        }

        return new ParsedScriptFile(id, commands, new ScriptFileStats(id, scripts, functions, actions));
    }

    /// <summary>The number an argument stands for, when it is written as one (decimal or 0x hex).</summary>
    private static int? Number(string argument)
    {
        if (argument.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(argument[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex) ? hex : null;
        }

        return int.TryParse(argument, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
    }

    /// <summary>Accepts "16384", or with <paramref name="hex"/> "4000" or "0x4000".</summary>
    public static int? ParseId(string text, bool hex)
    {
        text = text.Trim();
        if (hex)
        {
            if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) text = text[2..];
            return int.TryParse(text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int value) ? value : null;
        }

        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int number) ? number : null;
    }

    /// <summary>Legacy SearchVariableUsage: script arguments typed as variables equal to it, any other
    /// argument equal to it when it is 0x4000 or above; level script and event triggers watching it.</summary>
    public List<UsageResult> FindVariable(int variable)
    {
        var results = new List<UsageResult>();
        foreach (ParsedScriptFile file in scriptFiles)
        {
            int uses = 0;
            foreach (Command command in file.Commands)
            {
                commandsByName.TryGetValue(command.Name, out ScriptCommandInfo? info);
                for (int i = 0; i < command.Arguments.Count; i++)
                {
                    if (Number(command.Arguments[i]) != variable) continue;
                    ScriptParameterType? type = info is not null && i < info.ParameterTypes.Count ? info.ParameterTypes[i] : null;
                    bool sized = info is null || i >= info.ParameterSizes.Count || info.ParameterSizes[i] >= 2;
                    if (type is ScriptParameterType.Variable or ScriptParameterType.Flex || (sized && variable >= 0x4000)) uses++;
                }
            }

            if (uses > 0) results.Add(new UsageResult("Script", file.Id, uses, $"{uses} use(s)"));
        }

        foreach (LevelScriptFile file in levelScripts)
        {
            int uses = file.Triggers.Count(t => t.IsVariableValue && t.VariableToWatch == variable);
            if (uses > 0) results.Add(new UsageResult("Level Script", file.Id, uses, $"{uses} trigger(s) watch it"));
        }

        foreach (MapEventFile file in eventFiles)
        {
            int uses = file.Triggers.Count(t => t.VariableWatched == variable);
            if (uses > 0) results.Add(new UsageResult("Event", file.Id, uses, $"{uses} trigger(s) watch it"));
        }

        return results;
    }

    /// <summary>Legacy SearchFlagUsage: overworlds hidden by it, and script arguments of two bytes or more
    /// equal to it (flags are below 0x4000). Script matches are only a guide: any number can match.</summary>
    public List<UsageResult> FindFlag(int flag)
    {
        var results = new List<UsageResult>();
        foreach (MapEventFile file in eventFiles)
        {
            for (int i = 0; i < file.Overworlds.Count; i++)
            {
                OverworldEvent ow = file.Overworlds[i];
                if (ow.Flag == flag) results.Add(new UsageResult("Event", file.Id, 1, $"Overworld {i} (ID {ow.OverworldId}, entry {ow.OverlayTableEntry}, script {ow.ScriptNumber})"));
            }
        }

        if (flag < 0x4000)
        {
            foreach (ParsedScriptFile file in scriptFiles)
            {
                int uses = 0;
                foreach (Command command in file.Commands)
                {
                    commandsByName.TryGetValue(command.Name, out ScriptCommandInfo? info);
                    for (int i = 0; i < command.Arguments.Count; i++)
                    {
                        bool sized = info is null || i >= info.ParameterSizes.Count || info.ParameterSizes[i] >= 2;
                        if (sized && Number(command.Arguments[i]) == flag) uses++;
                    }
                }

                if (uses > 0) results.Add(new UsageResult("Script", file.Id, uses, $"{uses} flag operation(s)"));
            }
        }

        return results;
    }

    /// <summary>Headers whose script file or level script is <paramref name="scriptFileId"/>.</summary>
    public List<ScriptFileReference> FindScriptFileReferences(int scriptFileId)
    {
        var results = new List<ScriptFileReference>();
        foreach ((int id, MapHeaderRecord header) in AllHeaders())
        {
            if (header.ScriptFileId == scriptFileId) results.Add(new ScriptFileReference(id, "Script file"));
            if (header.LevelScriptId == scriptFileId) results.Add(new ScriptFileReference(id, "Level script"));
        }

        return results;
    }

    public IReadOnlyList<int> ScriptFileIds => scriptFiles.Select(file => file.Id).ToList();

    public int ScriptCount(int scriptFileId) => scriptFiles.FirstOrDefault(file => file.Id == scriptFileId)?.Stats.Scripts ?? 0;

    /// <summary>Overworlds, spawnables and triggers that run Script <paramref name="scriptNumber"/>, in
    /// the event files of headers using <paramref name="scriptFileId"/>.</summary>
    public List<ScriptIdUse> FindScriptIdUses(int scriptFileId, int scriptNumber)
    {
        var eventFileIds = AllHeaders().Where(h => h.Header.ScriptFileId == scriptFileId).Select(h => (int)h.Header.EventFileId).ToHashSet();
        var results = new List<ScriptIdUse>();
        foreach (MapEventFile file in eventFiles.Where(file => eventFileIds.Contains(file.Id)))
        {
            for (int i = 0; i < file.Overworlds.Count; i++)
            {
                OverworldEvent ow = file.Overworlds[i];
                if (ow.ScriptNumber == scriptNumber) results.Add(new ScriptIdUse(file.Id, "Overworld", i, $"ID {ow.OverworldId}, entry {ow.OverlayTableEntry}, at ({Position(ow)})"));
            }

            for (int i = 0; i < file.Spawnables.Count; i++)
            {
                SpawnableEvent spawnable = file.Spawnables[i];
                if (spawnable.ScriptNumber == scriptNumber) results.Add(new ScriptIdUse(file.Id, "Spawnable", i, $"type {spawnable.Type}, at ({Position(spawnable)})"));
            }

            for (int i = 0; i < file.Triggers.Count; i++)
            {
                TriggerEvent trigger = file.Triggers[i];
                if (trigger.ScriptNumber == scriptNumber) results.Add(new ScriptIdUse(file.Id, "Trigger", i, $"var 0x{trigger.VariableWatched:X4} == {trigger.ExpectedVarValue}, at ({Position(trigger)})"));
            }
        }

        return results;
    }

    private static string Position(MapEvent e) => $"{e.MapPositionX + 32 * e.MatrixPositionX}, {e.MapPositionY + 32 * e.MatrixPositionY}";

    public MapHeaderRecord? LoadHeader(int id) => headers is not null && id >= 0 && id < headers.Count ? headers.Load((ushort)id) : null;

    /// <summary>Warps anywhere leading into the header.</summary>
    public List<WarpLink> IncomingWarps(int headerId) =>
        eventFiles.SelectMany(file => file.Warps.Select((warp, index) => (file, warp, index)))
            .Where(item => item.warp.DestinationHeaderId == headerId)
            .Select(item => new WarpLink(item.file.Id, item.index, item.warp.MapPositionX, item.warp.MapPositionY, item.warp.DestinationHeaderId, item.warp.DestinationWarpIndex))
            .ToList();

    /// <summary>The warps in the header's own event file.</summary>
    public List<WarpLink> OutgoingWarps(MapHeaderRecord header) =>
        eventFiles.FirstOrDefault(file => file.Id == header.EventFileId) is { } file
            ? file.Warps.Select((warp, index) => new WarpLink(file.Id, index, warp.MapPositionX, warp.MapPositionY, warp.DestinationHeaderId, warp.DestinationWarpIndex)).ToList()
            : [];

    /// <summary>The first header using the event file (legacy FindHeaderByEventFileId), to open it in.</summary>
    public int? HeaderForEventFile(int eventFileId) => AllHeaders().Where(h => h.Header.EventFileId == eventFileId).Select(h => (int?)h.Id).FirstOrDefault();

    private List<(int Id, MapHeaderRecord Header)>? allHeaders;

    private List<(int Id, MapHeaderRecord Header)> AllHeaders()
    {
        if (allHeaders is not null) return allHeaders;
        allHeaders = [];
        for (int id = 0; id < HeaderCount; id++) allHeaders.Add((id, headers!.Load((ushort)id)));
        return allHeaders;
    }
}
