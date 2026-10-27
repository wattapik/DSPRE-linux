using System.Text.RegularExpressions;

namespace DSPRE.Core;

/// <summary>A Lua file in <c>expanded/scripts</c> holding one table of names by number, such as
/// <c>LEVEL_SCRIPT_NAMES = { [3] = "Twinleaf Town" }</c>. Editing a name rewrites its line, or adds one.</summary>
public sealed class NameTableFile(string fileName, string table, string journalFunction, string intro)
{
    private static readonly Regex Entry = new(@"^(?<indent>[ \t]*)\[\s*(?<id>\d+)\s*\]\s*=\s*""(?:[^""\\]|\\.)*""\s*,?(?<rest>.*)$", RegexOptions.Compiled);

    public string FileName { get; } = fileName;

    public string GetPath(RomProject project) => Path.Combine(project.ExpandedScriptsDirectory, FileName);

    private string Template => $"-- {FileName}: {intro}\n{table} = {{\n}}\n";

    public Dictionary<int, string> Read(RomProject project, ScriptLanguage language) =>
        File.Exists(GetPath(project)) ? Core.Lua.LuaScriptCompiler.FindNameTable(File.ReadAllText(GetPath(project)), language, table) : [];

    public static string Quote(string name) => "\"" + name.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\"";

    /// <summary>The file's text with the number's name set, or removed when the name is empty.</summary>
    public string WithName(string? source, int id, string name)
    {
        source ??= Template;
        string newline = source.Contains("\r\n") ? "\r\n" : "\n";
        List<string> lines = source.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        int index = lines.FindIndex(line => Entry.Match(line) is { Success: true } match && int.Parse(match.Groups["id"].Value) == id);
        if (name.Length == 0)
        {
            if (index >= 0) lines.RemoveAt(index);
        }
        else if (index >= 0)
        {
            Match match = Entry.Match(lines[index]);
            lines[index] = $"{match.Groups["indent"].Value}[{id}] = {Quote(name)},{match.Groups["rest"].Value}";
        }
        else
        {
            int close = lines.FindLastIndex(line => line.Trim() == "}");
            string line = $"\t[{id}] = {Quote(name)},";
            if (close >= 0) lines.Insert(close, line);
            else lines.AddRange([$"{table} = {{", line, "}"]);
        }

        return string.Join(newline, lines) + newline;
    }

    public void Save(RomProject project, int id, string name)
    {
        string? source = File.Exists(GetPath(project)) ? File.ReadAllText(GetPath(project)) : null;
        SaveText(project, WithName(source, id, name));
    }

    public void SaveText(RomProject project, string content) =>
        ScriptFileService.SaveJournaled(project, GetPath(project), journalFunction, [], $"Saved {FileName}", content);

    public void ApplyEdit(RomProject project, int line, int remove, IReadOnlyList<string> insert) =>
        ScriptFileService.ApplyHunk(GetPath(project), FileName, line, remove, insert, content => SaveText(project, content));
}

/// <summary>The names <c>expanded/scripts/level.lua</c> gives level scripts (<c>LEVEL_SCRIPT_NAMES</c>).</summary>
public static class LevelScriptNames
{
    public static readonly NameTableFile File = new("level.lua", "LEVEL_SCRIPT_NAMES", "EditLevelLua", "names for level scripts, shown in the Level Script Editor and the Map Editor.");
    public static Dictionary<int, string> Read(RomProject project, ScriptLanguage language) => File.Read(project, language);
    public static void Save(RomProject project, int id, string name) => File.Save(project, id, name);
    public static void ApplyEdit(RomProject project, int line, int remove, IReadOnlyList<string> insert) => File.ApplyEdit(project, line, remove, insert);
}

/// <summary>The names <c>expanded/scripts/text.lua</c> gives text archives (<c>TEXT_ARCHIVE_NAMES</c>).</summary>
public static class TextArchiveNames
{
    public static readonly NameTableFile File = new("text.lua", "TEXT_ARCHIVE_NAMES", "EditTextLua", "names for text archives, shown in the Text Editor and the Map Editor.");
    public static Dictionary<int, string> Read(RomProject project, ScriptLanguage language) => File.Read(project, language);
    public static void Save(RomProject project, int id, string name) => File.Save(project, id, name);
    public static void ApplyEdit(RomProject project, int line, int remove, IReadOnlyList<string> insert) => File.ApplyEdit(project, line, remove, insert);
}
