using System.Text.Json.Nodes;
using DSPRE.Core;

namespace DSPRE.LuaLanguageServer;

/// <summary>What a name at the cursor refers to. A local lives in its file; a global.lua name is shared by every script file of the
/// project; anything else (a command, a movement, a built-in) belongs to DSPRE and cannot be renamed.</summary>
internal sealed record NameTarget(string Name, NameTargetKind Kind);

internal enum NameTargetKind { Local, Global, Builtin }

/// <summary>References, rename and semantic tokens. These work on tokens, not on a syntax tree, so scope is approximated: a name
/// declared as a local anywhere in a file is that file's own, and every other use of a global.lua name is that name.</summary>
internal static class LuaRefactor
{
    private static readonly string[] TokenTypes = ["function", "variable", "parameter", "enumMember", "namespace"];
    private static readonly string[] TokenModifiers = ["declaration", "defaultLibrary", "readonly"];

    public static JsonObject SemanticLegend() => new()
    {
        ["tokenTypes"] = new JsonArray(TokenTypes.Select(t => (JsonNode)t).ToArray()),
        ["tokenModifiers"] = new JsonArray(TokenModifiers.Select(t => (JsonNode)t).ToArray()),
    };

    /// <summary>The name tokens of a file that stand for a variable or function: not <c>b</c> in <c>a.b</c> or <c>a:b</c>.</summary>
    public static IEnumerable<int> NameIndexes(List<Token> tokens)
    {
        int previous = -1;
        for (int i = 0; i < tokens.Count; i++)
        {
            if (tokens[i].Kind == TokenKind.Comment) continue;
            if (tokens[i].Kind == TokenKind.Name && !(previous >= 0 && (tokens[previous].Is(".") || tokens[previous].Is(":")))) yield return i;
            previous = i;
        }
    }

    public static NameTarget? TargetAt(List<Token> tokens, int line, int column, ProjectContext? project)
    {
        int index = LuaFeatures.TokenAt(tokens, line, column);
        if (index < 0 || !NameIndexes(tokens).Contains(index)) return null;
        string name = tokens[index].Text;
        if (LuaFeatures.Locals(tokens).Any(local => local.Name == name)) return new NameTarget(name, NameTargetKind.Local);
        if (project?.Symbols.Any(symbol => symbol.Name == name) == true) return new NameTarget(name, NameTargetKind.Global);
        if (IsBuiltin(name, project)) return new NameTarget(name, NameTargetKind.Builtin);
        return null;
    }

    public static bool IsBuiltin(string name, ProjectContext? project) =>
        LuaFeatures.IsBuiltinName(name)
        || (project?.Language is { } language && (language.ResolveCommand(name) is not null || language.ResolveMovement(name) is not null));

    /// <summary>Every use of the target across the files; for a local, only the file it is in.</summary>
    public static List<(string File, Token Token)> References(NameTarget target, string currentFile, IEnumerable<(string File, List<Token> Tokens)> files, ProjectContext? project)
    {
        var found = new List<(string, Token)>();
        foreach ((string file, List<Token> tokens) in files)
        {
            if (target.Kind == NameTargetKind.Local && file != currentFile) continue;
            bool ownsLocal = LuaFeatures.Locals(tokens).Any(local => local.Name == target.Name);
            if (target.Kind != NameTargetKind.Local && ownsLocal) continue;
            foreach (int i in NameIndexes(tokens))
            {
                if (SameThing(tokens[i].Text, target, project)) found.Add((file, tokens[i]));
            }
        }
        return found;
    }

    // A command is the same command under its database name and its snake_case alias.
    private static bool SameThing(string name, NameTarget target, ProjectContext? project)
    {
        if (name == target.Name) return true;
        if (target.Kind != NameTargetKind.Builtin || project?.Language is not { } language) return false;
        if (language.ResolveCommand(target.Name) is { } command) return language.ResolveCommand(name) == command;
        if (language.ResolveMovement(target.Name) is { } movement) return language.ResolveMovement(name) == movement;
        return false;
    }

    /// <summary>Why <paramref name="newName"/> cannot replace the target's name, or null.</summary>
    public static string? RenameProblem(NameTarget target, string newName, ProjectContext? project, IEnumerable<List<Token>> files)
    {
        if (target.Kind == NameTargetKind.Builtin) return $"'{target.Name}' belongs to DSPRE's script language and cannot be renamed.";
        if (!ScriptLanguage.IsIdentifier(newName)) return $"'{newName}' is not a valid Lua name.";
        if (IsBuiltin(newName, project)) return $"'{newName}' is a built-in name.";
        if (target.Kind == NameTargetKind.Global)
        {
            if (project?.Symbols.Any(symbol => symbol.Name == newName) == true) return $"global.lua already defines '{newName}'.";
            if (files.Any(tokens => LuaFeatures.Locals(tokens).Any(local => local.Name == newName))) return $"A script file already has a local called '{newName}'; renaming would change what it means.";
        }
        return null;
    }

    public static JsonNode? SemanticTokens(List<Token> tokens, ProjectContext? project)
    {
        List<LocalDeclaration> locals = LuaFeatures.Locals(tokens);
        var kindOfLocal = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (LocalDeclaration local in locals) kindOfLocal.TryAdd(local.Name, local.Kind);
        var declarations = new HashSet<(int, int)>(locals.Select(local => (local.Token.Line, local.Token.Column)));

        var data = new JsonArray();
        int previousLine = 0, previousColumn = 0;
        foreach (int i in NameIndexes(tokens))
        {
            Token token = tokens[i];
            (int type, int modifiers)? classified = Classify(token.Text, kindOfLocal, project);
            if (classified is not { } c) continue;
            int modifierBits = c.modifiers;
            if (declarations.Contains((token.Line, token.Column))) modifierBits |= 1;
            int deltaLine = token.Line - previousLine;
            data.Add(deltaLine);
            data.Add(deltaLine == 0 ? token.Column - previousColumn : token.Column);
            data.Add(token.Text.Length);
            data.Add(c.type);
            data.Add(modifierBits);
            previousLine = token.Line;
            previousColumn = token.Column;
        }
        return new JsonObject { ["data"] = data };
    }

    private const int Function = 0, Variable = 1, Parameter = 2, EnumMember = 3, Namespace = 4;
    private const int DefaultLibrary = 2, Readonly = 4;

    private static (int, int)? Classify(string name, Dictionary<string, string> locals, ProjectContext? project)
    {
        if (locals.TryGetValue(name, out string? kind)) return kind switch { "function" => (Function, 0), "parameter" => (Parameter, 0), _ => (Variable, 0) };
        LuaGlobalSymbol? symbol = project?.Symbols.FirstOrDefault(s => s.Name == name);
        if (symbol is not null)
        {
            return symbol.Kind switch
            {
                LuaGlobalKind.Function => (Function, 0),
                LuaGlobalKind.Variable or LuaGlobalKind.Flag => (EnumMember, Readonly),
                _ => (Variable, 0),
            };
        }
        if (project?.Language is { } language)
        {
            if (language.ResolveCommand(name) is not null) return (Function, DefaultLibrary);
            if (language.ResolveMovement(name) is not null) return (EnumMember, DefaultLibrary);
        }
        if (name is "var" or "flag" or "Overworld" or "ow" or "Move" or "math") return (Namespace, DefaultLibrary);
        if (LuaFeatures.IsBuiltinName(name)) return (Function, DefaultLibrary);
        // Constants of the script language (ITEM_POTION, SEQ_SE_CONFIRM, EQUAL, Player, Up...).
        if (name.Length > 1 && name.All(c => char.IsUpper(c) || char.IsDigit(c) || c == '_')) return (EnumMember, DefaultLibrary);
        if (project?.Language is { } known && (known.Database.SpecialOverworlds.Values.Contains(name) || known.Database.OverworldDirections.Values.Contains(name))) return (EnumMember, DefaultLibrary);
        return null;
    }
}
