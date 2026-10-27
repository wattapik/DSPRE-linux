using DSPRE.Core;
using DSPRE.Core.Lua;

namespace DSPRE.LuaLanguageServer;

/// <summary>A DSPRE project as the language server sees it: the script language of its command database, and what its global.lua
/// defines. global.lua comes from the editor's unsaved buffer when it is open, so a name typed there completes at once.</summary>
internal sealed class ProjectContext
{
    private readonly string databasesDirectory;
    private readonly object gate = new();
    private string? loadedGlobal;
    private bool loaded;

    public RomProject Project { get; }
    public RomGameMetadata Metadata { get; }
    public string ScriptsDirectory => Project.ExpandedScriptsDirectory;
    public string GlobalPath => Path.Combine(ScriptsDirectory, "global.lua");

    public ScriptLanguage? Language { get; private set; }
    public string? LanguageError { get; private set; }
    public string? GlobalText { get; private set; }
    public IReadOnlyList<LuaGlobalSymbol> Symbols { get; private set; } = [];
    public LuaNames Names { get; private set; } = LuaNames.None;

    private ProjectContext(RomProject project, RomGameMetadata metadata, string databasesDirectory)
    {
        Project = project;
        Metadata = metadata;
        this.databasesDirectory = databasesDirectory;
    }

    /// <summary>The project the file belongs to: the nearest folder above it that DSPRE opens as a project and that holds
    /// expanded/scripts.</summary>
    public static ProjectContext? Find(string filePath, string databasesDirectory)
    {
        string? directory = Path.GetDirectoryName(filePath);
        while (directory is not null)
        {
            if (Directory.Exists(Path.Combine(directory, "expanded", "scripts")) && RomProject.TryOpen(directory, out RomProject? project) && project is not null
                && project.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata is not null)
            {
                return new ProjectContext(project, metadata, databasesDirectory);
            }
            directory = Path.GetDirectoryName(directory);
        }
        return null;
    }

    public bool IsProjectScript(string path) =>
        string.Equals(Path.GetDirectoryName(Path.GetFullPath(path)), Path.GetFullPath(ScriptsDirectory), StringComparison.Ordinal);

    /// <summary>Reloads the language and global.lua. Returns whether global.lua or the language changed since the last time.</summary>
    public bool Refresh(string? globalBuffer)
    {
        lock (gate)
        {
            string? global = globalBuffer ?? (File.Exists(GlobalPath) ? File.ReadAllText(GlobalPath) : null);
            ScriptLanguage? previous = Language;
            try
            {
                Language = LuaScriptService.LoadLanguage(Project, Metadata, databasesDirectory);
                LanguageError = null;
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or System.Text.Json.JsonException)
            {
                Language = null;
                LanguageError = ex.Message;
            }

            bool changed = !loaded || global != loadedGlobal || !ReferenceEquals(previous, Language);
            if (changed)
            {
                loaded = true;
                loadedGlobal = global;
                GlobalText = global;
                Symbols = LuaGlobalSymbols.Find(global);
                Names = Language is null ? LuaNames.None : LuaScriptCompiler.FindNames(global, Language);
            }
            return changed;
        }
    }
}
