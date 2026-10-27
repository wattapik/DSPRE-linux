using DSPRE.Core;
using DSPRE.LuaLanguageServer;

const string Usage =
    "dspre-lua-ls: language server for the Lua scripts of a DSPRE project.\n" +
    "Usage: dspre-lua-ls [--stdio] [--databases <folder>]      run the language server on stdin/stdout\n" +
    "       dspre-lua-ls stubs -p <project_dir> [--databases <folder>]   write expanded/scripts/.dspre/api.lua and .luarc.json\n" +
    "  --databases  folder with the built-in script command databases (default: DSPRE_DATABASES, a databases folder next to this program,\n" +
    "               DSPRE's own data folder). A project's own database always wins.";

string? databases = null, projectPath = null;
bool stubs = false;
for (int i = 0; i < args.Length; i++)
{
    switch (args[i])
    {
        case "--stdio": break;
        case "stubs": stubs = true; break;
        case "--databases": databases = ++i < args.Length ? args[i] : null; break;
        case "-p" or "--project": projectPath = ++i < args.Length ? args[i] : null; break;
        case "-h" or "--help":
            Console.WriteLine(Usage);
            return 0;
        default:
            Console.Error.WriteLine($"Unknown argument '{args[i]}'.\n{Usage}");
            return 2;
    }
}

databases = ResolveDatabases(databases);

if (stubs)
{
    if (projectPath is null || !RomProject.TryOpen(Path.GetFullPath(projectPath), out RomProject? project) || project is null
        || !project.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
    {
        Console.Error.WriteLine($"'{projectPath}' is not a DSPRE project folder.\n{Usage}");
        return 2;
    }
    try
    {
        ScriptLanguage language = LuaScriptService.LoadLanguage(project, metadata, databases);
        if (!LuaStubGenerator.WriteProjectStubs(project, language, LuaScriptService.LoadGlobal(project)))
        {
            Console.Error.WriteLine("The project has no expanded/scripts folder yet; open it in DSPRE once first.");
            return 2;
        }
        Console.WriteLine($"Wrote {Path.Combine(project.ExpandedScriptsDirectory, LuaStubGenerator.StubDirectoryName, LuaStubGenerator.StubFileName)} and .luarc.json.");
        return 0;
    }
    catch (Exception ex) when (ex is IOException or InvalidDataException)
    {
        Console.Error.WriteLine(ex.Message);
        return 1;
    }
}

using Stream input = Console.OpenStandardInput();
using Stream output = Console.OpenStandardOutput();
return new LuaServer(new JsonRpc(input, output), databases).Run();

static string ResolveDatabases(string? given)
{
    string[] candidates =
    [
        given ?? "",
        Environment.GetEnvironmentVariable("DSPRE_DATABASES") ?? "",
        Path.Combine(AppContext.BaseDirectory, "databases"),
        ScriptDatabaseStore.DatabasesDirectory,
    ];
    return candidates.FirstOrDefault(path => path.Length > 0 && Directory.Exists(path) && Directory.GetFiles(path, "*_scrcmd_database.json").Length > 0)
           ?? candidates.First(path => path.Length > 0);
}
