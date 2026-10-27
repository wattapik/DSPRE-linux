using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DSPRE.Core.Lua;

namespace DSPRE.Core;

/// <summary>What a global.lua defines that other Lua files can use: <c>function npc_message(text)</c> (a helper) or
/// <c>STARTER = var[0x4000]</c> (a name for a variable or flag), with the line it is defined on.</summary>
public sealed record LuaGlobalSymbol(string Name, LuaGlobalKind Kind, int Line, IReadOnlyList<string> Parameters, long Number = 0);

public enum LuaGlobalKind { Function, Variable, Flag, Value }

/// <summary>Reads global.lua for editors that complete, hover and jump to definitions (the language server, the generated
/// API stub) without running it.</summary>
public static partial class LuaGlobalSymbols
{
    public static IReadOnlyList<LuaGlobalSymbol> Find(string? global)
    {
        if (string.IsNullOrWhiteSpace(global)) return [];
        IReadOnlyList<LuaStmt> chunk;
        try
        {
            chunk = LuaParser.Parse(global);
        }
        catch (LuaException)
        {
            return FindByText(global);
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var symbols = new List<LuaGlobalSymbol>();
        foreach (LuaStmt statement in chunk)
        {
            switch (statement)
            {
                case LuaFunctionDecl { IsLocal: false, Path.Count: 1 } function:
                    if (seen.Add(function.Path[0])) symbols.Add(new LuaGlobalSymbol(function.Path[0], LuaGlobalKind.Function, function.Line, function.Function.Parameters));
                    break;
                case LuaAssign assign:
                    for (int i = 0; i < assign.Targets.Count; i++)
                    {
                        if (assign.Targets[i] is not LuaName name || !seen.Add(name.Name)) continue;
                        symbols.Add(Classify(name, i < assign.Values.Count ? assign.Values[i] : null));
                    }
                    break;
            }
        }
        return symbols;
    }

    private static LuaGlobalSymbol Classify(LuaName name, LuaExpr? value) => value switch
    {
        LuaIndex { Target: LuaName { Name: "var" }, Key: LuaNumber variable } => new LuaGlobalSymbol(name.Name, LuaGlobalKind.Variable, name.Line, [], (long)variable.Value),
        LuaIndex { Target: LuaName { Name: "flag" }, Key: LuaNumber flag } => new LuaGlobalSymbol(name.Name, LuaGlobalKind.Flag, name.Line, [], (long)flag.Value),
        _ => new LuaGlobalSymbol(name.Name, LuaGlobalKind.Value, name.Line, []),
    };

    // A global.lua that does not parse (mid-edit) still completes from what its lines say.
    private static IReadOnlyList<LuaGlobalSymbol> FindByText(string global)
    {
        var symbols = new List<LuaGlobalSymbol>();
        string[] lines = global.Replace("\r", "").Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            Match function = FunctionLine().Match(lines[i]);
            if (function.Success)
            {
                symbols.Add(new LuaGlobalSymbol(function.Groups[1].Value, LuaGlobalKind.Function, i + 1,
                    function.Groups[2].Value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)));
                continue;
            }
            Match assign = AssignLine().Match(lines[i]);
            if (assign.Success) symbols.Add(new LuaGlobalSymbol(assign.Groups[1].Value, LuaGlobalKind.Value, i + 1, []));
        }
        return symbols;
    }

    [GeneratedRegex(@"^function\s+([A-Za-z_]\w*)\s*\(([^)]*)\)")]
    private static partial Regex FunctionLine();

    [GeneratedRegex(@"^([A-Za-z_]\w*)\s*=")]
    private static partial Regex AssignLine();
}

/// <summary>Documentation for script commands as Lua functions: parameter names a signature can show and the text a hover
/// or an API stub carries.</summary>
public static class LuaCommandDocs
{
    public static IReadOnlyList<string> ParameterLabels(ScriptCommandInfo info)
    {
        var labels = new List<string>();
        var used = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < info.ParameterNames.Count; i++)
        {
            string text = info.ParameterNames[i].Split(": ", 2)[^1].Split('\n')[0];
            string label = Regex.Replace(text.Trim().ToLowerInvariant(), @"[^a-z0-9]+", "_").Trim('_');
            if (label.Length == 0 || char.IsDigit(label[0])) label = "arg" + (i + 1);
            if (ScriptLanguage.LuaKeywords.Contains(label)) label += "_";
            string unique = label;
            for (int n = 2; !used.Add(unique); n++) unique = label + n;
            labels.Add(unique);
        }
        return labels;
    }

    /// <summary>The database's description, empty where it has none (it writes "nan" for a missing one).</summary>
    public static string Description(string? text) => text is null || text.Trim().Equals("nan", StringComparison.OrdinalIgnoreCase) ? "" : text.Trim();

    /// <summary>The parameters a call must give: the last one is left out when the command stores its result there and can be
    /// used as a value.</summary>
    public static int RequiredCount(ScriptCommandInfo info, ScriptLanguage language) =>
        info.ParameterNames.Count - (language.HasResultParameter(info) ? 1 : 0);

    public static string Signature(ScriptCommandInfo info, string alias, ScriptLanguage language)
    {
        IReadOnlyList<string> labels = ParameterLabels(info);
        int required = RequiredCount(info, language);
        return $"{alias}({string.Join(", ", labels.Select((label, i) => i < required ? label : label + "?"))})";
    }

    public static string TypeOf(ScriptCommandInfo info, int index)
    {
        if (ScriptLanguage.IsMessageParameter(info, index)) return "integer|string|string[]";
        return index < info.ParameterTypes.Count && info.ParameterTypes[index] == ScriptParameterType.Variable ? "integer" : "any";
    }

    public static string Markdown(ScriptCommandInfo info, string alias, ScriptLanguage language)
    {
        var text = new StringBuilder();
        text.Append("```lua\n").Append(Signature(info, alias, language)).Append("\n```\n");
        text.Append(alias != info.Name ? $"Database name `{info.Name}`, command 0x{info.CommandId:X}\n\n" : $"Command 0x{info.CommandId:X}\n\n");
        if (Description(info.Description).Length > 0) text.Append(Description(info.Description)).Append("\n\n");
        IReadOnlyList<string> labels = ParameterLabels(info);
        for (int i = 0; i < info.ParameterNames.Count; i++) text.Append($"- `{labels[i]}`: {info.ParameterNames[i].Replace('\n', ' ')}\n");
        if (language.HasResultParameter(info)) text.Append("\nWithout its last argument it is a value: `RESULT = ").Append(alias).Append("(...)`.\n");
        if (Description(info.Notes).Length > 0) text.Append('\n').Append(Description(info.Notes)).Append('\n');
        return text.ToString();
    }
}

/// <summary>Writes the project's Lua API for generic Lua language servers (LuaLS, EmmyLua): a <c>---@meta</c> file declaring
/// every command and global.lua name, and the <c>.luarc.json</c> that points at it. The DSPRE language server needs neither, but
/// writing them gives any editor completion and hover with no extra install.</summary>
public static class LuaStubGenerator
{
    public const string StubDirectoryName = ".dspre";
    public const string StubFileName = "api.lua";

    public static string Generate(ScriptLanguage language, string? global)
    {
        var text = new StringBuilder();
        text.Append("---@meta\n-- Generated by DSPRE from the project's script command database and global.lua. Do not edit; it is rewritten when the project opens.\n\n");
        text.Append("---@type table<integer, integer>\nvar = {}\n\n---@type table<integer, boolean>\nflag = {}\n\n");
        text.Append("---@type table<integer, any>\nOverworld = {}\n\n---@type table<integer, any>\now = {}\n\n---@type table<integer, any>\nMove = {}\n\n");
        text.Append("---Defines script `number`: a function of no parameters, or the number of a script to share code with. A name may come before the body.\n");
        text.Append("---@param number integer\n---@param nameOrBody string|function|integer\n---@param body? function|integer\nfunction script(number, nameOrBody, body) end\n\n");
        text.Append("---Calls a command whose database name is not a valid Lua name.\n---@param name string\n---@param ... any\nfunction cmd(name, ...) end\n\n");
        text.Append("---@param name string\n---@return boolean\nfunction has_command(name) end\n\n---@param operator any\n---@return any\nfunction condition(operator) end\n\n");
        text.Append("---Passes any other token through to the script unchanged.\n---@param token string\n---@return any\nfunction sym(token) end\n\n");
        text.Append("---Raw script lines, copied into the script as written; `{name}` refers to a Lua value.\n---@param lines string\nfunction asm(lines) end\n\n");
        text.Append("---A movement by number.\n---@param id integer\n---@param count? integer\n---@return any\nfunction step(id, count) end\n\n");
        text.Append("---@param script integer\n---@return any\nfunction use_script(script) end\n\n");

        foreach (string op in language.ComparisonOperators) text.Append($"---@type any\n{op.Replace('/', '_')} = nil\n\n");
        foreach (string name in language.Database.SpecialOverworlds.Values.Concat(language.Database.OverworldDirections.Values).Where(ScriptLanguage.IsIdentifier).Distinct())
        {
            text.Append($"---@type any\n{name} = nil\n\n");
        }

        foreach (ScriptCommandInfo info in language.Database.Commands.Values.Where(c => ScriptLanguage.IsIdentifier(c.Name)).OrderBy(c => c.Name, StringComparer.Ordinal))
        {
            AppendFunction(text, info, info.Name, language);
            string alias = ScriptLanguage.ToSnakeCase(info.Name);
            if (alias != info.Name && language.ResolveCommand(alias) == info) AppendFunction(text, info, alias, language);
        }

        foreach (ScriptSubEntryInfo movement in language.Database.Movements.Values.Where(m => ScriptLanguage.IsIdentifier(m.Name)).OrderBy(m => m.Name, StringComparer.Ordinal))
        {
            AppendMovement(text, movement, movement.Name);
            string alias = ScriptLanguage.ToSnakeCase(movement.Name);
            if (alias != movement.Name && language.ResolveMovement(alias) == movement) AppendMovement(text, movement, alias);
        }

        foreach (LuaGlobalSymbol symbol in LuaGlobalSymbols.Find(global))
        {
            switch (symbol.Kind)
            {
                case LuaGlobalKind.Function:
                    text.Append("---Defined in global.lua line ").Append(symbol.Line).Append('\n');
                    foreach (string parameter in symbol.Parameters) text.Append("---@param ").Append(parameter).Append(" any\n");
                    text.Append("---@return any\nfunction ").Append(symbol.Name).Append('(').Append(string.Join(", ", symbol.Parameters)).Append(") end\n\n");
                    break;
                case LuaGlobalKind.Variable:
                    text.Append($"---Variable 0x{symbol.Number:X} (global.lua line {symbol.Line})\n---@type integer\n{symbol.Name} = 0\n\n");
                    break;
                case LuaGlobalKind.Flag:
                    text.Append($"---Flag {symbol.Number} (global.lua line {symbol.Line})\n---@type boolean\n{symbol.Name} = false\n\n");
                    break;
                default:
                    text.Append($"---Defined in global.lua line {symbol.Line}\n---@type any\n{symbol.Name} = nil\n\n");
                    break;
            }
        }
        return text.ToString();
    }

    private static void AppendFunction(StringBuilder text, ScriptCommandInfo info, string alias, ScriptLanguage language)
    {
        text.Append("---").Append(FirstLine(LuaCommandDocs.Description(info.Description), info.Name)).Append('\n');
        if (alias != info.Name) text.Append("---Alias of ").Append(info.Name).Append('\n');
        IReadOnlyList<string> labels = LuaCommandDocs.ParameterLabels(info);
        int required = LuaCommandDocs.RequiredCount(info, language);
        for (int i = 0; i < labels.Count; i++)
        {
            text.Append("---@param ").Append(labels[i]).Append(i >= required ? "? " : " ").Append(LuaCommandDocs.TypeOf(info, i)).Append(' ')
                .Append(info.ParameterNames[i].Replace('\n', ' ')).Append('\n');
        }
        if (language.HasResultParameter(info)) text.Append("---@return integer\n");
        text.Append("function ").Append(alias).Append('(').Append(string.Join(", ", labels)).Append(") end\n\n");
    }

    private static void AppendMovement(StringBuilder text, ScriptSubEntryInfo movement, string name) =>
        text.Append("---").Append(FirstLine(LuaCommandDocs.Description(movement.Description), $"Movement 0x{movement.Id:X}")).Append("\n---@param count? integer\n---@return any\nfunction ")
            .Append(name).Append("(count) end\n\n");

    private static string FirstLine(string description, string fallback)
    {
        string line = description.Trim().Split('\n')[0].Trim();
        return line.Length > 0 ? line : fallback;
    }

    public static string LuaRc() =>
        new JsonObject
        {
            ["$schema"] = "https://raw.githubusercontent.com/LuaLS/vscode-lua/master/setting/schema.json",
            ["runtime"] = new JsonObject { ["version"] = "Lua 5.4" },
            ["workspace"] = new JsonObject { ["library"] = new JsonArray(StubDirectoryName), ["checkThirdParty"] = false },
            ["diagnostics"] = new JsonObject
            {
                ["disable"] = new JsonArray("undefined-global", "lowercase-global", "unused-local", "duplicate-set-field", "redefined-local"),
            },
        }.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    /// <summary>Writes the stub and .luarc.json into the project's expanded/scripts folder, leaving files whose content
    /// is already right alone. Returns false when the project has no scripts folder yet.</summary>
    public static bool WriteProjectStubs(RomProject project, ScriptLanguage language, string? global)
    {
        string directory = project.ExpandedScriptsDirectory;
        if (!Directory.Exists(directory)) return false;
        WriteIfChanged(Path.Combine(directory, StubDirectoryName, StubFileName), Generate(language, global));
        WriteIfChanged(Path.Combine(directory, ".luarc.json"), LuaRc());
        return true;
    }

    private static void WriteIfChanged(string path, string content)
    {
        if (File.Exists(path) && File.ReadAllText(path) == content) return;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
    }
}
