using System.Text;
using System.Text.RegularExpressions;

namespace DSPRE.Core;

/// <summary>One command line of a plaintext script: the command name and its argument tokens as written.</summary>
public sealed record ScriptTextLine(string Command, IReadOnlyList<string> Args)
{
    public override string ToString() => Args.Count == 0 ? Command : Command + " " + string.Join(' ', Args);
}

public enum ScriptTextTerminator { End, Return, Jump }

/// <summary>A Script or Function: straight-line commands closed by End, Return or a Jump to a Function.
/// One that shares another script's code instead says <c>UseScript_#N</c>.</summary>
public sealed class ScriptTextContainer
{
    public required int Number { get; init; }
    public int? UseScript { get; init; }
    public List<ScriptTextLine> Lines { get; init; } = [];
    public ScriptTextTerminator Terminator { get; init; }
    /// <summary>The Function a Jump terminator goes to.</summary>
    public int JumpTarget { get; init; }
}

/// <summary>An Action: overworld movement steps, each a movement name and a repeat count.</summary>
public sealed class ScriptTextAction
{
    public required int Number { get; init; }
    public List<(string Movement, string? Count)> Steps { get; init; } = [];
}

/// <summary>The plaintext script format of expanded/scripts/NNNN.script, as read and written by legacy
/// DS_Map/ROMFiles/ScriptFile.cs: Scripts, then Functions, then Actions, each numbered, with
/// <c>Function#N</c> and <c>Action#N</c> referring to them.</summary>
public sealed partial class ScriptTextDocument
{
    public List<ScriptTextContainer> Scripts { get; } = [];
    public List<ScriptTextContainer> Functions { get; } = [];
    public List<ScriptTextAction> Actions { get; } = [];

    public static bool TryParseFunctionRef(string token, out int number) => TryParseRef(token, "Function#", out number);
    public static bool TryParseActionRef(string token, out int number) => TryParseRef(token, "Action#", out number);

    private static bool TryParseRef(string token, string prefix, out int number)
    {
        number = 0;
        return token.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && int.TryParse(token.AsSpan(prefix.Length), out number);
    }

    public static ScriptTextDocument Parse(string text, ScriptLanguage language)
    {
        var document = new ScriptTextDocument();
        List<(int Number, string Text)> lines = StripComments(text);
        int i = 0;
        while (i < lines.Count)
        {
            (int lineNumber, string line) = lines[i];
            Match header = ContainerHeader().Match(line);
            if (!header.Success) throw new ScriptTextException(lineNumber, $"Expected a Script, Function or Action header, found \"{line}\".");
            string kind = header.Groups[1].Value;
            int number = int.Parse(header.Groups[2].Value);
            i++;

            if (kind.Equals("Action", StringComparison.OrdinalIgnoreCase))
            {
                var action = new ScriptTextAction { Number = number };
                while (true)
                {
                    if (i >= lines.Count) throw new ScriptTextException(lineNumber, $"Action {number} has no End.");
                    string[] parts = Tokenize(lines[i].Text);
                    i++;
                    if (parts[0].Equals("End", StringComparison.OrdinalIgnoreCase) && parts.Length == 1) break;
                    action.Steps.Add((parts[0], parts.Length > 1 ? parts[1] : null));
                }
                document.Actions.Add(action);
                continue;
            }

            var body = new List<ScriptTextLine>();
            int? useScript = null;
            ScriptTextTerminator? terminator = null;
            int jumpTarget = 0;
            while (terminator is null && useScript is null)
            {
                if (i >= lines.Count) throw new ScriptTextException(lineNumber, $"{kind} {number} is not closed by End, Return or Jump.");
                (int bodyLineNumber, string bodyLine) = lines[i];
                if (ContainerHeader().IsMatch(bodyLine)) throw new ScriptTextException(bodyLineNumber, $"{kind} {number} is not closed by End, Return or Jump before \"{bodyLine}\".");
                i++;

                Match use = UseScriptLine().Match(bodyLine);
                if (use.Success && body.Count == 0)
                {
                    useScript = int.Parse(use.Groups[1].Value);
                    break;
                }

                string[] parts = Tokenize(bodyLine);
                if (language.IsEnd(parts[0]) && parts.Length == 1) terminator = ScriptTextTerminator.End;
                else if (language.IsReturn(parts[0]) && parts.Length == 1) terminator = ScriptTextTerminator.Return;
                else if (language.IsJump(parts[0]) && parts.Length == 2 && TryParseFunctionRef(parts[1], out jumpTarget)) terminator = ScriptTextTerminator.Jump;
                else body.Add(new ScriptTextLine(parts[0], parts[1..]));
            }

            var container = new ScriptTextContainer
            {
                Number = number,
                UseScript = useScript,
                Lines = body,
                Terminator = terminator ?? ScriptTextTerminator.End,
                JumpTarget = jumpTarget,
            };
            if (kind.Equals("Script", StringComparison.OrdinalIgnoreCase)) document.Scripts.Add(container);
            else document.Functions.Add(container);
        }

        return document;
    }

    public string Write(ScriptLanguage language, IEnumerable<string>? headerComment = null)
    {
        var builder = new StringBuilder();
        if (headerComment is not null)
        {
            builder.Append("/*\n");
            foreach (string line in headerComment) builder.Append(" * ").Append(line).Append('\n');
            builder.Append(" */\n\n");
        }

        builder.Append("//===== SCRIPTS =====//\n");
        foreach (ScriptTextContainer script in Scripts) WriteContainer(builder, "Script", script, language);
        builder.Append("//===== FUNCTIONS =====//\n");
        foreach (ScriptTextContainer function in Functions) WriteContainer(builder, "Function", function, language);
        builder.Append("//===== ACTIONS =====//\n");
        foreach (ScriptTextAction action in Actions)
        {
            builder.Append("Action ").Append(action.Number).Append(":\n");
            foreach ((string movement, string? count) in action.Steps)
            {
                builder.Append('\t').Append(movement);
                if (count is not null) builder.Append(' ').Append(count);
                builder.Append('\n');
            }
            builder.Append("End\n\n");
        }

        return builder.ToString();
    }

    private static void WriteContainer(StringBuilder builder, string kind, ScriptTextContainer container, ScriptLanguage language)
    {
        builder.Append(kind).Append(' ').Append(container.Number).Append(":\n");
        if (container.UseScript is int used)
        {
            builder.Append("\tUseScript_#").Append(used).Append("\n\n");
            return;
        }

        foreach (ScriptTextLine line in container.Lines) builder.Append('\t').Append(line).Append('\n');
        builder.Append(container.Terminator switch
        {
            ScriptTextTerminator.End => language.End,
            ScriptTextTerminator.Return => language.Return,
            _ => $"{language.Jump} Function#{container.JumpTarget}",
        }).Append("\n\n");
    }

    /// <summary>Splits a line on whitespace, keeping a [bracketed name] or "quoted text" together as one
    /// token the way legacy's ProcessBracketedItems keeps names like [Mr. Mime] whole.</summary>
    public static string[] Tokenize(string line)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < line.Length)
        {
            if (char.IsWhiteSpace(line[i])) { i++; continue; }
            int start = i;
            while (i < line.Length && !char.IsWhiteSpace(line[i]))
            {
                char close = line[i] switch { '[' => ']', '"' => '"', _ => '\0' };
                if (close == '\0') { i++; continue; }
                i++;
                while (i < line.Length && line[i] != close)
                {
                    if (close == '"' && line[i] == '\\') i++;
                    i++;
                }
                i = Math.Min(i + 1, line.Length);
            }
            tokens.Add(line[start..i]);
        }
        return tokens.ToArray();
    }

    /// <summary>Legacy ScriptFile.PreprocessLines: drops /* */ and // comments and blank lines, keeping
    /// 1-based line numbers for error messages.</summary>
    private static List<(int, string)> StripComments(string text)
    {
        var result = new List<(int, string)>();
        string[] lines = text.Split('\n');
        bool inBlock = false;
        for (int index = 0; index < lines.Length; index++)
        {
            string line = lines[index].TrimEnd('\r');
            var kept = new StringBuilder();
            int i = 0;
            bool inQuote = false;
            while (i < line.Length)
            {
                if (inBlock)
                {
                    int close = line.IndexOf("*/", i, StringComparison.Ordinal);
                    if (close < 0) { i = line.Length; break; }
                    inBlock = false;
                    i = close + 2;
                    continue;
                }
                char c = line[i];
                if (c == '"') inQuote = !inQuote;
                if (!inQuote && c == '/' && i + 1 < line.Length && line[i + 1] == '*') { inBlock = true; i += 2; continue; }
                if (!inQuote && c == '/' && i + 1 < line.Length && line[i + 1] == '/') break;
                kept.Append(c);
                i++;
            }
            string trimmed = kept.ToString().Trim();
            if (trimmed.Length > 0) result.Add((index + 1, trimmed));
        }
        return result;
    }

    [GeneratedRegex(@"^(Script|Function|Action)\s*#?\s*(\d+)\s*:$", RegexOptions.IgnoreCase)]
    private static partial Regex ContainerHeader();

    [GeneratedRegex(@"^UseScript_?#(\d+)$", RegexOptions.IgnoreCase)]
    private static partial Regex UseScriptLine();
}

public sealed class ScriptTextException(int line, string message) : Exception($"Line {line}: {message}")
{
    public int Line { get; } = line;
}
