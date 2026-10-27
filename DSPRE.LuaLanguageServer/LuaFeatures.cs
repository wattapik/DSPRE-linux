using System.Text.Json.Nodes;
using DSPRE.Core;

namespace DSPRE.LuaLanguageServer;

internal sealed record LocalDeclaration(string Name, Token Token, string Kind);

/// <summary>The language features, each a function of the file's text, a position and the project: completion, hover, definition,
/// signature help and the outline. Results are LSP JSON.</summary>
internal static class LuaFeatures
{
    private const int KindFunction = 3, KindVariable = 6, KindConstant = 21, KindKeyword = 14, KindSnippet = 15, KindModule = 9, KindEnumMember = 20;

    private static readonly (string Label, string Snippet)[] KeywordSnippets =
    [
        ("if", "if ${1:condition} then\n\t$0\nend"),
        ("elseif", "elseif ${1:condition} then"),
        ("else", "else"),
        ("for", "for ${1:i} = ${2:1}, ${3:3} do\n\t$0\nend"),
        ("while", "while ${1:condition} do\n\t$0\nend"),
        ("repeat", "repeat\n\t$0\nuntil ${1:condition}"),
        ("function", "function ${1:name}($2)\n\t$0\nend"),
        ("local", "local "),
        ("return", "return "),
        ("end", "end"), ("then", "then"), ("do", "do"), ("in", "in"), ("break", "break"), ("until", "until"),
        ("and", "and"), ("or", "or"), ("not", "not "), ("true", "true"), ("false", "false"), ("nil", "nil"),
    ];

    private static readonly (string Name, string Snippet, string Doc)[] Builtins =
    [
        ("script", "script(${1:1}, function()\n\t$0\nend)", "Defines script `number`: a function of no parameters, or the number of a script to share code with. A name may come before the body: `script(1, \"Rival intro\", function() ... end)`."),
        ("use_script", "use_script(${1:number})", "A script that runs the same code as another script."),
        ("var", "var[${1:0x4000}]", "A game variable: `var[0x4000] = 5`, `if var[0x4000] >= 1 then`."),
        ("flag", "flag[${1:0}]", "A game flag: `flag[372] = true`, `if flag[144] then`."),
        ("condition", "condition(${1:EQUAL})", "Tests the condition register the last command left: `if condition(EQUAL) then`."),
        ("sym", "sym(\"${1:TOKEN}\")", "Passes any other token through to the script unchanged."),
        ("step", "step(${1:id}, ${2:count})", "A movement by number: `step(0x41, 2)`."),
        ("cmd", "cmd(\"${1:Name}\", $0)", "Calls a command whose database name is not a valid Lua name."),
        ("has_command", "has_command(\"${1:Name}\")", "True when the ROM's command database has that command."),
        ("asm", "asm [[\n\t$0\n]]", "Raw script lines copied into the script as written. `{name}` refers to a Lua value."),
        ("Overworld", "Overworld[${1:0}]", "An overworld (NPC) by number."),
        ("ow", "ow[${1:0}]", "An overworld (NPC) by number; same as `Overworld`."),
        ("Move", "Move[${1:0}]", "An overworld movement type by number."),
        ("print", "print($0)", "Prints a warning while compiling."),
        ("error", "error(\"$0\")", "Stops compiling with an error."),
        ("tostring", "tostring($0)", "Compile-time string conversion."),
        ("tonumber", "tonumber($0)", "Compile-time number conversion."),
        ("type", "type($0)", "Compile-time type name."),
        ("ipairs", "ipairs($0)", "Loops over a table's items in order, at compile time."),
        ("pairs", "pairs($0)", "Loops over a table, at compile time."),
        ("math", "math", "`math.floor`, `ceil`, `abs`, `max` and `min`, worked out while compiling."),
    ];

    public static bool IsBuiltinName(string name) => Builtins.Any(builtin => builtin.Name == name);

    private static readonly string[] MathFunctions = ["floor", "ceil", "abs", "max", "min"];

    public static int Offset(string text, int line, int column)
    {
        int offset = 0;
        for (int i = 0; i < line; i++)
        {
            int next = text.IndexOf('\n', offset);
            if (next < 0) return text.Length;
            offset = next + 1;
        }
        return Math.Min(text.Length, offset + column);
    }

    /// <summary>The index of the token the position is in or at the end of, or -1.</summary>
    public static int TokenAt(List<Token> tokens, int line, int column)
    {
        for (int i = 0; i < tokens.Count; i++)
        {
            Token t = tokens[i];
            if (t.Line == line && t.EndLine == line && column >= t.Column && column <= t.EndColumn && t.Kind != TokenKind.Comment) return i;
        }
        return -1;
    }

    public static bool InsideStringOrComment(List<Token> tokens, int line, int column)
    {
        foreach (Token t in tokens)
        {
            if (t.Kind is not (TokenKind.String or TokenKind.Comment)) continue;
            bool afterStart = line > t.Line || (line == t.Line && column > t.Column);
            bool beforeEnd = line < t.EndLine || (line == t.EndLine && column < t.EndColumn);
            if (afterStart && beforeEnd) return true;
            // An unfinished one-line string or comment runs to the end of its line.
            if (t.Kind == TokenKind.Comment && line == t.Line && column > t.Column) return true;
        }
        return false;
    }

    public static List<LocalDeclaration> Locals(List<Token> tokens)
    {
        var result = new List<LocalDeclaration>();
        List<Token> code = tokens.Where(t => t.Kind != TokenKind.Comment).ToList();
        for (int i = 0; i < code.Count; i++)
        {
            Token t = code[i];
            if (t.Is("local"))
            {
                int j = i + 1;
                if (j < code.Count && code[j].Is("function")) j++;
                while (j < code.Count && code[j].Kind == TokenKind.Name)
                {
                    result.Add(new LocalDeclaration(code[j].Text, code[j], "local"));
                    j++;
                    if (j < code.Count && code[j].Is(",")) j++;
                    else break;
                }
            }
            else if (t.Is("function"))
            {
                int j = i + 1;
                if (j < code.Count && code[j].Kind == TokenKind.Name && !(i > 0 && code[i - 1].Is("local")) && (j + 1 >= code.Count || !code[j + 1].Is(".")))
                {
                    result.Add(new LocalDeclaration(code[j].Text, code[j], "function"));
                }
                while (j < code.Count && !code[j].Is("(")) j++;
                for (j++; j < code.Count && !code[j].Is(")"); j++)
                {
                    if (code[j].Kind == TokenKind.Name) result.Add(new LocalDeclaration(code[j].Text, code[j], "parameter"));
                }
            }
            else if (t.Is("for"))
            {
                for (int j = i + 1; j < code.Count && code[j].Kind is TokenKind.Name || (j < code.Count && code[j].Is(",")); j++)
                {
                    if (code[j].Kind == TokenKind.Name) result.Add(new LocalDeclaration(code[j].Text, code[j], "loop variable"));
                }
            }
        }
        return result;
    }

    private static JsonObject Item(string label, int kind, string sortGroup, string? detail, string? documentation, string? snippet)
    {
        var item = new JsonObject { ["label"] = label, ["kind"] = kind, ["sortText"] = sortGroup + label };
        if (detail is not null) item["detail"] = detail;
        if (documentation is not null) item["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = documentation };
        if (snippet is not null)
        {
            item["insertText"] = snippet;
            item["insertTextFormat"] = 2;
        }
        return item;
    }

    public static JsonArray Complete(string text, List<Token> tokens, int line, int column, ProjectContext? project)
    {
        var items = new JsonArray();
        if (InsideStringOrComment(tokens, line, column)) return items;

        int current = TokenAt(tokens, line, column);
        int previous = current >= 0 && tokens[current].Kind is TokenKind.Name or TokenKind.Keyword ? current - 1 : LastBefore(tokens, line, column);

        // var[ and flag[ take numbers; offer the names global.lua gave them, inserting the number.
        if (previous >= 1 && tokens[previous].Is("[") && tokens[previous - 1].Kind == TokenKind.Name && tokens[previous - 1].Text is "var" or "flag")
        {
            bool variables = tokens[previous - 1].Text == "var";
            if (project is not null)
            {
                foreach (LuaGlobalSymbol symbol in project.Symbols.Where(s => s.Kind == (variables ? LuaGlobalKind.Variable : LuaGlobalKind.Flag)))
                {
                    string number = variables ? $"0x{symbol.Number:X}" : symbol.Number.ToString();
                    JsonObject item = Item(symbol.Name, KindConstant, "0", number, $"`{(variables ? "var" : "flag")}[{number}]`, named in global.lua line {symbol.Line}", null);
                    item["insertText"] = number;
                    item["filterText"] = symbol.Name;
                    items.Add(item);
                }
            }
            return items;
        }

        if (previous >= 0 && tokens[previous].Is("."))
        {
            if (previous >= 1 && tokens[previous - 1] is { Kind: TokenKind.Name, Text: "math" })
            {
                foreach (string name in MathFunctions) items.Add(Item(name, KindFunction, "0", $"math.{name}", null, null));
            }
            return items;
        }
        if (previous >= 0 && tokens[previous].Is(":")) return items;

        bool callFollows = false;
        int nextOffset = Offset(text, line, column);
        if (current >= 0) nextOffset = Offset(text, tokens[current].EndLine, tokens[current].EndColumn);
        if (nextOffset < text.Length && text[nextOffset] == '(') callFollows = true;

        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (LocalDeclaration local in Locals(tokens))
        {
            if (local.Token.Line > line || !seen.Add(local.Name)) continue;
            items.Add(Item(local.Name, local.Kind == "function" ? KindFunction : KindVariable, "0", local.Kind, null, null));
        }

        if (project is not null)
        {
            foreach (LuaGlobalSymbol symbol in project.Symbols.Where(s => seen.Add(s.Name)))
            {
                switch (symbol.Kind)
                {
                    case LuaGlobalKind.Function:
                        string args = string.Join(", ", symbol.Parameters);
                        string snippet = symbol.Parameters.Count == 0 ? $"{symbol.Name}()" : $"{symbol.Name}({string.Join(", ", symbol.Parameters.Select((p, i) => $"${{{i + 1}:{p}}}"))})";
                        items.Add(Item(symbol.Name, KindFunction, "1", $"{symbol.Name}({args})", $"Helper in global.lua, line {symbol.Line}.", callFollows ? null : snippet));
                        break;
                    case LuaGlobalKind.Variable:
                        items.Add(Item(symbol.Name, KindConstant, "1", $"var[0x{symbol.Number:X}]", $"Variable named in global.lua line {symbol.Line}.", null));
                        break;
                    case LuaGlobalKind.Flag:
                        items.Add(Item(symbol.Name, KindConstant, "1", $"flag[{symbol.Number}]", $"Flag named in global.lua line {symbol.Line}.", null));
                        break;
                    default:
                        items.Add(Item(symbol.Name, KindVariable, "1", "global.lua", $"Defined in global.lua line {symbol.Line}.", null));
                        break;
                }
            }

            if (project.Language is { } language) AddLanguageItems(items, language, seen, callFollows);
        }

        foreach ((string name, string snippet, string doc) in Builtins)
        {
            if (seen.Add(name)) items.Add(Item(name, name is "var" or "flag" or "Overworld" or "ow" or "Move" or "math" ? KindModule : KindFunction, "4", null, doc, callFollows ? null : snippet));
        }
        foreach ((string label, string snippet) in KeywordSnippets)
        {
            if (seen.Add(label)) items.Add(Item(label, label.Length > 4 && snippet.Contains('\n') ? KindSnippet : KindKeyword, "5", null, null, snippet == label ? null : snippet));
        }
        return items;
    }

    private static void AddLanguageItems(JsonArray items, ScriptLanguage language, HashSet<string> seen, bool callFollows)
    {
        foreach (ScriptCommandInfo info in language.Database.Commands.Values.Where(c => ScriptLanguage.IsIdentifier(c.Name)))
        {
            foreach (string alias in new[] { info.Name, ScriptLanguage.ToSnakeCase(info.Name) }.Distinct())
            {
                if (language.ResolveCommand(alias) != info || !seen.Add(alias)) continue;
                IReadOnlyList<string> labels = LuaCommandDocs.ParameterLabels(info);
                int required = LuaCommandDocs.RequiredCount(info, language);
                string snippet = required == 0 ? $"{alias}()" : $"{alias}({string.Join(", ", labels.Take(required).Select((p, i) => $"${{{i + 1}:{p}}}"))})";
                items.Add(Item(alias, KindFunction, "2", LuaCommandDocs.Signature(info, alias, language), LuaCommandDocs.Markdown(info, alias, language), callFollows ? null : snippet));
            }
        }

        foreach (ScriptSubEntryInfo movement in language.Database.Movements.Values.Where(m => ScriptLanguage.IsIdentifier(m.Name)))
        {
            foreach (string alias in new[] { movement.Name, ScriptLanguage.ToSnakeCase(movement.Name) }.Distinct())
            {
                if (language.ResolveMovement(alias) != movement || !seen.Add(alias)) continue;
                items.Add(Item(alias, KindEnumMember, "3", "movement", MovementMarkdown(movement, alias), null));
            }
        }

        foreach (string op in language.ComparisonOperators)
        {
            string name = op.Replace('/', '_');
            if (seen.Add(name)) items.Add(Item(name, KindConstant, "3", "comparison operator", null, null));
        }
        foreach (string name in language.Database.SpecialOverworlds.Values.Concat(language.Database.OverworldDirections.Values).Where(ScriptLanguage.IsIdentifier))
        {
            if (seen.Add(name)) items.Add(Item(name, KindConstant, "3", "overworld", null, null));
        }
    }

    private static string MovementMarkdown(ScriptSubEntryInfo movement, string alias) =>
        $"```lua\n{alias}\n```\nMovement 0x{movement.Id:X}. Use it in a list for `Movement(...)`; call it with a count to repeat: `{alias}(2)`.\n\n{LuaCommandDocs.Description(movement.Description)}";

    private static int LastBefore(List<Token> tokens, int line, int column)
    {
        int result = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            Token t = tokens[i];
            if (t.Kind == TokenKind.Comment) continue;
            if (t.EndLine < line || (t.EndLine == line && t.EndColumn <= column)) result = i;
        }
        return result;
    }

    public static JsonNode? Hover(List<Token> tokens, int line, int column, ProjectContext? project)
    {
        int index = TokenAt(tokens, line, column);
        if (index < 0) return null;
        Token token = tokens[index];

        // A number inside var[...] / flag[...] shows the name global.lua gave it.
        if (token.Kind == TokenKind.Number && index >= 2 && tokens[index - 1].Is("[") && tokens[index - 2] is { Kind: TokenKind.Name, Text: "var" or "flag" } kind && project is not null)
        {
            long number = ParseNumber(token.Text);
            LuaGlobalSymbol? named = project.Symbols.FirstOrDefault(s => s.Kind == (kind.Text == "var" ? LuaGlobalKind.Variable : LuaGlobalKind.Flag) && s.Number == number);
            return named is null ? null : Markdown($"`{named.Name}` is `{kind.Text}[{token.Text}]` (global.lua line {named.Line})", token);
        }
        if (token.Kind != TokenKind.Name) return null;

        string name = token.Text;
        LocalDeclaration? local = null;
        foreach (LocalDeclaration candidate in Locals(tokens))
        {
            if (candidate.Name == name && (candidate.Token.Line < line || (candidate.Token.Line == line && candidate.Token.Column <= token.Column))) local = candidate;
        }
        if (local is not null) return Markdown($"```lua\n({local.Kind}) {name}\n```\nDeclared on line {local.Token.Line + 1}.", token);

        if (project is not null)
        {
            LuaGlobalSymbol? symbol = project.Symbols.FirstOrDefault(s => s.Name == name);
            if (symbol is not null)
            {
                string body = symbol.Kind switch
                {
                    LuaGlobalKind.Function => $"```lua\nfunction {name}({string.Join(", ", symbol.Parameters)})\n```\nHelper defined in global.lua, line {symbol.Line}.",
                    LuaGlobalKind.Variable => $"```lua\n{name} = var[0x{symbol.Number:X}]\n```\nVariable named in global.lua, line {symbol.Line}.",
                    LuaGlobalKind.Flag => $"```lua\n{name} = flag[{symbol.Number}]\n```\nFlag named in global.lua, line {symbol.Line}.",
                    _ => $"`{name}`, defined in global.lua line {symbol.Line}.",
                };
                return Markdown(body, token);
            }

            if (project.Language is { } language)
            {
                if (language.ResolveCommand(name) is { } command) return Markdown(LuaCommandDocs.Markdown(command, name, language), token);
                if (language.ResolveMovement(name) is { } movement) return Markdown(MovementMarkdown(movement, name), token);
            }
        }

        foreach ((string builtin, _, string doc) in Builtins)
        {
            if (builtin == name) return Markdown($"```lua\n{name}\n```\n{doc}", token);
        }
        return null;
    }

    private static long ParseNumber(string text) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(text[2..], 16) : long.TryParse(text, out long value) ? value : -1;

    private static JsonObject Markdown(string value, Token token) => new()
    {
        ["contents"] = new JsonObject { ["kind"] = "markdown", ["value"] = value },
        ["range"] = Range(token.Line, token.Column, token.EndLine, token.EndColumn),
    };

    public static JsonObject Range(int line, int column, int endLine, int endColumn) => new()
    {
        ["start"] = new JsonObject { ["line"] = line, ["character"] = column },
        ["end"] = new JsonObject { ["line"] = endLine, ["character"] = endColumn },
    };

    /// <summary>Where a name is defined: a local or function in this file, or a global.lua name (given as its line there).</summary>
    public static (LocalDeclaration? Local, LuaGlobalSymbol? Global) Definition(List<Token> tokens, int line, int column, ProjectContext? project)
    {
        int index = TokenAt(tokens, line, column);
        if (index < 0 || tokens[index].Kind != TokenKind.Name) return (null, null);
        string name = tokens[index].Text;
        LocalDeclaration? local = null;
        foreach (LocalDeclaration candidate in Locals(tokens))
        {
            if (candidate.Name == name && candidate.Token.Line <= line) local = candidate;
        }
        if (local is not null) return (local, null);
        return (null, project?.Symbols.FirstOrDefault(s => s.Name == name));
    }

    public static JsonNode? SignatureHelp(List<Token> tokens, int line, int column, ProjectContext? project)
    {
        if (InsideStringOrComment(tokens, line, column) && !IsInsideCallString(tokens, line, column)) return null;
        int index = LastBefore(tokens, line, column);
        int depth = 0, commas = 0;
        for (int i = index; i >= 0; i--)
        {
            Token t = tokens[i];
            if (t.Kind == TokenKind.Comment) continue;
            if (t.Is(")") || t.Is("]") || t.Is("}")) depth++;
            else if (t.Is("(") || t.Is("[") || t.Is("{"))
            {
                if (depth > 0) depth--;
                else if (t.Is("(") && i > 0 && tokens[i - 1].Kind == TokenKind.Name) return SignatureFor(tokens[i - 1].Text, commas, project);
                else if (t.Is("(")) return null;
                else commas = 0;
            }
            else if (t.Is(",") && depth == 0) commas++;
        }
        return null;
    }

    private static bool IsInsideCallString(List<Token> tokens, int line, int column) =>
        tokens.Any(t => t.Kind == TokenKind.String && t.Line == line && t.EndLine == line && column > t.Column && column < t.EndColumn);

    private static JsonNode? SignatureFor(string name, int active, ProjectContext? project)
    {
        if (project is null) return null;
        string? label = null, doc = null;
        List<string> parameters = [];
        if (project.Language is { } language && language.ResolveCommand(name) is { } command)
        {
            label = LuaCommandDocs.Signature(command, name, language);
            doc = LuaCommandDocs.Description(command.Description);
            int required = LuaCommandDocs.RequiredCount(command, language);
            parameters = LuaCommandDocs.ParameterLabels(command).Select((p, i) => i < required ? p : p + "?").ToList();
        }
        else if (project.Symbols.FirstOrDefault(s => s.Kind == LuaGlobalKind.Function && s.Name == name) is { } function)
        {
            label = $"{name}({string.Join(", ", function.Parameters)})";
            doc = $"Helper in global.lua, line {function.Line}.";
            parameters = function.Parameters.ToList();
        }
        if (label is null) return null;

        var parameterInfo = new JsonArray();
        int from = label.IndexOf('(') + 1;
        foreach (string parameter in parameters)
        {
            int start = label.IndexOf(parameter, from, StringComparison.Ordinal);
            if (start < 0) continue;
            parameterInfo.Add(new JsonObject { ["label"] = new JsonArray(start, start + parameter.Length) });
            from = start + parameter.Length;
        }
        var signature = new JsonObject { ["label"] = label, ["parameters"] = parameterInfo };
        if (!string.IsNullOrEmpty(doc)) signature["documentation"] = new JsonObject { ["kind"] = "markdown", ["value"] = doc };
        return new JsonObject
        {
            ["signatures"] = new JsonArray(signature),
            ["activeSignature"] = 0,
            ["activeParameter"] = Math.Min(active, Math.Max(0, parameterInfo.Count - 1)),
        };
    }

    public static JsonArray Symbols(List<Token> tokens, ProjectContext? project, bool isGlobal)
    {
        var result = new JsonArray();
        List<Token> code = tokens.Where(t => t.Kind != TokenKind.Comment).ToList();
        for (int i = 0; i < code.Count; i++)
        {
            Token t = code[i];
            string? name = null;
            if (t.Is("function") && i + 1 < code.Count && code[i + 1].Kind == TokenKind.Name)
            {
                Token nameToken = code[i + 1];
                result.Add(Symbol(nameToken.Text, "function", 12, nameToken));
                continue;
            }
            if (t is { Kind: TokenKind.Name, Text: "script" } && i + 3 < code.Count && code[i + 1].Is("(") && code[i + 2].Kind == TokenKind.Number)
            {
                name = $"script {code[i + 2].Text}";
                if (code[i + 3].Is(",") && i + 4 < code.Count && code[i + 4].Kind == TokenKind.String) name += $" {code[i + 4].Text}";
                result.Add(Symbol(name, "", 12, t));
            }
            else if (isGlobal && t.Kind == TokenKind.Name && i + 1 < code.Count && code[i + 1].Is("=") && (i == 0 || code[i - 1].Line != t.Line) && project is not null)
            {
                result.Add(Symbol(t.Text, "", 13, t));
            }
        }
        return result;
    }

    private static JsonObject Symbol(string name, string detail, int kind, Token token) => new()
    {
        ["name"] = name,
        ["detail"] = detail,
        ["kind"] = kind,
        ["range"] = Range(token.Line, token.Column, token.EndLine, token.EndColumn),
        ["selectionRange"] = Range(token.Line, token.Column, token.EndLine, token.EndColumn),
    };
}
