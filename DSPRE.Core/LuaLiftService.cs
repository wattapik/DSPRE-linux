namespace DSPRE.Core;

/// <summary>The text archive a script file's messages are written in: the one of the first header that uses the script file.</summary>
public sealed class ScriptArchiveLookup(RomProject project, RomGameMetadata metadata)
{
    private readonly IMapHeaderStore? store = MapHeaderStoreFactory.Create(project, metadata);
    private readonly Dictionary<int, TextArchiveData> archives = [];

    public TextArchiveData? Find(int scriptId)
    {
        if (store is null) return null;
        for (ushort header = 0; header < project.GetHeaderCount(); header++)
        {
            MapHeaderRecord record = store.Load(header);
            if (record.ScriptFileId != scriptId) continue;
            try
            {
                if (!archives.TryGetValue(record.TextArchiveId, out TextArchiveData? archive))
                {
                    archive = TextArchiveService.Load(project, record.TextArchiveId, metadata);
                    archives[record.TextArchiveId] = archive;
                }

                return archive;
            }
            catch (FileNotFoundException)
            {
            }
        }

        return null;
    }
}

/// <summary>Converts the project's plaintext script files (<c>expanded/scripts/NNNN.script</c>) to Lua (<c>NNNN.lua</c>), as "Convert to
/// Lua" does for one file: each conversion is checked to compile back to a script that behaves the same, and one that does not is
/// reported and left alone. The .script files are not touched; the first conversion in a project creates global.lua with the default
/// templates.</summary>
public static class LuaLiftService
{
    public sealed record Failure(int Script, string Message);

    public sealed record Result(IReadOnlyList<int> Converted, IReadOnlyList<int> Skipped, IReadOnlyList<Failure> Failures, bool CreatedGlobal);

    /// <param name="overwrite">Convert files that already have a .lua too (the .lua is replaced).</param>
    /// <param name="only">Script file numbers to convert; null for all.</param>
    /// <param name="write">Off, only check which files convert.</param>
    public static Result LiftAll(RomProject project, RomGameMetadata metadata, string databasesDirectory, bool overwrite, ISet<int>? only, bool write,
        Action<string>? progress = null)
    {
        var converted = new List<int>();
        var skipped = new List<int>();
        var failures = new List<Failure>();
        ScriptLanguage language = LuaScriptService.LoadLanguage(project, metadata, databasesDirectory);

        int[] ids = Directory.Exists(project.ExpandedScriptsDirectory)
            ? Directory.GetFiles(project.ExpandedScriptsDirectory, "*.script")
                .Select(path => int.TryParse(Path.GetFileNameWithoutExtension(path), out int id) ? id : -1)
                .Where(id => id >= 0 && (only is null || only.Contains(id))).Order().ToArray()
            : [];

        string? global = LuaScriptService.LoadGlobal(project);
        bool createdGlobal = false;
        if (global is null && ids.Length > 0)
        {
            global = LuaScriptService.DefaultGlobal;
            createdGlobal = true;
            if (write) LuaScriptService.SaveGlobal(project, global);
        }

        var archives = new ScriptArchiveLookup(project, metadata);
        foreach (int id in ids)
        {
            if (!overwrite && LuaScriptService.Exists(project, id))
            {
                skipped.Add(id);
                continue;
            }

            try
            {
                string lua = LuaScriptService.Lift(language, id, File.ReadAllText(ScriptFileService.GetPath(project, id)), archives.Find(id)?.Messages, global);
                if (write) LuaScriptService.Save(project, id, lua);
                converted.Add(id);
            }
            catch (Exception ex) when (ex is InvalidDataException or ScriptTextException or Lua.LuaException or IOException)
            {
                failures.Add(new Failure(id, ex.Message));
            }

            progress?.Invoke($"{id:D4}");
        }

        return new Result(converted, skipped, failures, createdGlobal);
    }
}
