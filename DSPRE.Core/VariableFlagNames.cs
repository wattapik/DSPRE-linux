using System.Text.RegularExpressions;

namespace DSPRE.Core;

/// <summary>Edits the names global.lua gives variables and flags (<c>STARTER = var[0x4000]</c>,
/// <c>MET_RIVAL = flag[372]</c>): the Variable and Flag Editor's saves. A rename also renames the name (in code only, never in strings or comments) in
/// every Lua script file; removing a name writes the variable or flag back in its place there.</summary>
public static class VariableFlagNames
{
    /// <param name="OldName">The name now in global.lua, or null when there is none.</param>
    /// <param name="NewName">The name to have, or null to have none.</param>
    public sealed record Edit(bool IsFlag, int Id, string? OldName, string? NewName);

    private static readonly Regex Definition = new(
        @"^(?<indent>[ \t]*)(?<name>[A-Za-z_]\w*)(?<gap>[ \t]*=[ \t]*)(?<kind>var|flag)[ \t]*\[[ \t]*(?<id>0[xX][0-9A-Fa-f]+|\d+)[ \t]*\](?<rest>.*)$",
        RegexOptions.Compiled);

    private static readonly HashSet<string> Keywords = new(StringComparer.Ordinal)
    {
        "and", "break", "do", "else", "elseif", "end", "false", "for", "function", "if", "in", "local", "nil", "not", "or", "repeat", "return", "then", "true", "until", "while",
        "var", "flag", "script", "math", "type", "pairs", "ipairs",
    };

    public static bool IsValidName(string name) => Regex.IsMatch(name, @"^[A-Za-z_]\w*$") && !Keywords.Contains(name);

    public static string Reference(bool isFlag, int id) => isFlag ? $"flag[{id}]" : $"var[0x{id:X4}]";

    private static bool TryParseDefinition(string line, out Match match, out bool isFlag, out int id)
    {
        match = Definition.Match(line.TrimEnd('\r'));
        isFlag = false;
        id = 0;
        if (!match.Success) return false;
        isFlag = match.Groups["kind"].Value == "flag";
        string text = match.Groups["id"].Value;
        return text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out id)
            : int.TryParse(text, out id);
    }

    /// <summary>Applies the edits to global.lua's text. A name global.lua defines some other way than a plain
    /// <c>NAME = var[0x4000]</c> line cannot be renamed or removed here and throws.</summary>
    public static string EditGlobal(string global, IReadOnlyList<Edit> edits)
    {
        string newline = global.Contains("\r\n") ? "\r\n" : "\n";
        List<string> lines = global.Split('\n').Select(line => line.TrimEnd('\r')).ToList();
        if (lines.Count > 0 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);

        foreach (Edit edit in edits.Where(edit => edit.OldName is not null))
        {
            int index = lines.FindIndex(line => TryParseDefinition(line, out Match match, out bool isFlag, out int id)
                && isFlag == edit.IsFlag && id == edit.Id && match.Groups["name"].Value == edit.OldName);
            if (index < 0) throw new InvalidOperationException($"{edit.OldName} is not a plain `{edit.OldName} = {Reference(edit.IsFlag, edit.Id)}` line in global.lua, so it cannot be changed here. Edit global.lua directly.");
            if (edit.NewName is null)
            {
                lines.RemoveAt(index);
            }
            else
            {
                Match match = Definition.Match(lines[index]);
                lines[index] = $"{match.Groups["indent"].Value}{edit.NewName}{match.Groups["gap"].Value}{match.Groups["kind"].Value}[{match.Groups["id"].Value}]{match.Groups["rest"].Value}";
            }
        }

        foreach (Edit edit in edits.Where(edit => edit.OldName is null && edit.NewName is not null))
        {
            int last = lines.FindLastIndex(line => TryParseDefinition(line, out _, out bool isFlag, out _) && isFlag == edit.IsFlag);
            string line = $"{edit.NewName} = {Reference(edit.IsFlag, edit.Id)}";
            if (last >= 0) lines.Insert(last + 1, line);
            else
            {
                if (lines.Count > 0 && lines[^1].Length > 0) lines.Add("");
                lines.Add(line);
            }
        }

        return string.Join(newline, lines) + newline;
    }

    /// <summary>Saves the edits into global.lua and rewrites the names in every Lua script file. Returns the
    /// script files that changed.</summary>
    public static List<int> Apply(RomProject project, IReadOnlyList<Edit> edits)
    {
        string global = LuaScriptService.LoadGlobal(project) ?? LuaScriptService.DefaultGlobal;
        LuaScriptService.SaveGlobal(project, EditGlobal(global, edits));

        var changed = new List<int>();
        Dictionary<string, string> renames = RenameMap(edits);
        if (renames.Count == 0 || !Directory.Exists(project.ExpandedScriptsDirectory)) return changed;
        foreach (string path in Directory.GetFiles(project.ExpandedScriptsDirectory, "*.lua").Order(StringComparer.Ordinal))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out int id)) continue;
            string content = File.ReadAllText(path);
            string updated = RenameInLua(content, renames);
            if (updated == content) continue;
            LuaScriptService.Save(project, id, updated);
            changed.Add(id);
        }

        return changed;
    }

    /// <summary>Old name to what replaces it: the new name, or the variable or flag itself when the name is removed.</summary>
    public static Dictionary<string, string> RenameMap(IReadOnlyList<Edit> edits) => edits
        .Where(edit => edit.OldName is not null && edit.OldName != edit.NewName)
        .ToDictionary(edit => edit.OldName!, edit => edit.NewName ?? Reference(edit.IsFlag, edit.Id));

    /// <summary>Renames identifiers in Lua code only: text inside strings (a message that says the name) and
    /// comments is left alone, and so is a field after a dot (<c>table.NAME</c>).</summary>
    public static string RenameInLua(string text, IReadOnlyDictionary<string, string> renames)
    {
        var result = new System.Text.StringBuilder(text.Length);
        int i = 0;
        while (i < text.Length)
        {
            char c = text[i];
            if (c == '-' && i + 1 < text.Length && text[i + 1] == '-')
            {
                int end = LongBracketEnd(text, i + 2) ?? (text.IndexOf('\n', i) is var newline and >= 0 ? newline : text.Length);
                result.Append(text, i, end - i);
                i = end;
            }
            else if (c is '"' or '\'')
            {
                int end = i + 1;
                while (end < text.Length && text[end] != c && text[end] != '\n') end += text[end] == '\\' ? 2 : 1;
                end = Math.Min(end + 1, text.Length);
                result.Append(text, i, end - i);
                i = end;
            }
            else if (c == '[' && LongBracketEnd(text, i) is int longEnd)
            {
                result.Append(text, i, longEnd - i);
                i = longEnd;
            }
            else if (char.IsLetter(c) || c == '_')
            {
                int end = i + 1;
                while (end < text.Length && (char.IsLetterOrDigit(text[end]) || text[end] == '_')) end++;
                string word = text[i..end];
                bool field = i > 0 && text[i - 1] == '.' && !(i > 1 && text[i - 2] == '.');
                result.Append(!field && renames.TryGetValue(word, out string? replacement) ? replacement : word);
                i = end;
            }
            else
            {
                result.Append(c);
                i++;
            }
        }

        return result.ToString();
    }

    /// <summary>The end of a long bracket string or comment (<c>[[...]]</c>, <c>[==[...]==]</c>) starting at the index, if one does.</summary>
    private static int? LongBracketEnd(string text, int start)
    {
        if (start >= text.Length || text[start] != '[') return null;
        int level = 0;
        int i = start + 1;
        while (i < text.Length && text[i] == '=') { level++; i++; }
        if (i >= text.Length || text[i] != '[') return null;
        string close = "]" + new string('=', level) + "]";
        int end = text.IndexOf(close, i + 1, StringComparison.Ordinal);
        return end < 0 ? text.Length : end + close.Length;
    }
}
