using System.Text.Json.Nodes;

namespace DSPRE.Core;

public sealed record JournalCall(string Function, IReadOnlyList<(string Name, JsonNode? Value)> Args, int LineNumber)
{
    public JsonNode? Get(string name) => Args.FirstOrDefault(arg => arg.Name == name).Value;

    public JsonNode Require(string name) =>
        Args.Any(arg => arg.Name == name)
            ? Get(name) ?? throw new FormatException($"Line {LineNumber}: argument '{name}' of {Function} is null.")
            : throw new FormatException($"Line {LineNumber}: {Function} is missing argument '{name}'.");
}

/// <summary>The journal's line format: one call per line, <c>Function(name: &lt;json&gt;, ...)</c>, where each
/// argument value is a single-line JSON literal. '#' starts a comment line.</summary>
public static class EditJournalFormat
{
    public static string FormatValue(JsonNode? value) => value?.ToJsonString(EditJournalModel.JsonOptions) ?? "null";

    public static string FormatArgs(IEnumerable<KeyValuePair<string, JsonNode?>> args) =>
        string.Join(", ", args.Select(arg => $"{arg.Key}: {FormatValue(arg.Value)}"));

    public static string FormatCall(string function, IEnumerable<(string Name, JsonNode? Value)> args) =>
        $"{function}({FormatArgs(args.Select(arg => new KeyValuePair<string, JsonNode?>(arg.Name, arg.Value)))})";

    public static string FormatOp(JournalDocument document, JsonObject keys, JournalOp op)
    {
        var args = keys.Select(pair => (pair.Key, pair.Value?.DeepClone())).ToList();
        if (op.Path.Length > 0) args.Add(("path", JsonValue.Create(op.Path)));
        if (op.Verb != JournalOpVerb.Remove) args.Add(("value", op.Value?.DeepClone()));
        return FormatCall(op.Verb + document.Name, args);
    }

    public static IEnumerable<JournalCall> Parse(string path)
    {
        int lineNumber = 0;
        foreach (string rawLine in File.ReadLines(path))
        {
            lineNumber++;
            string line = rawLine.Trim();
            if (line.Length == 0 || line[0] == '#') continue;
            yield return ParseLine(line, lineNumber);
        }
    }

    public static JournalCall ParseLine(string line, int lineNumber)
    {
        int open = line.IndexOf('(');
        if (open <= 0 || line[^1] != ')') throw new FormatException($"Line {lineNumber}: expected Function(args...), got '{line}'.");
        string function = line[..open].Trim();

        var args = new List<(string, JsonNode?)>();
        int i = open + 1;
        int end = line.Length - 1;
        while (true)
        {
            while (i < end && char.IsWhiteSpace(line[i])) i++;
            if (i >= end) break;

            int colon = line.IndexOf(':', i);
            if (colon < 0) throw new FormatException($"Line {lineNumber}: expected 'name: value' in {function}.");
            string name = line[i..colon].Trim();
            i = colon + 1;

            int valueEnd = FindValueEnd(line, i, end, lineNumber);
            string literal = line[i..valueEnd].Trim();
            JsonNode? value;
            try
            {
                value = JsonNode.Parse(literal);
            }
            catch (System.Text.Json.JsonException ex)
            {
                throw new FormatException($"Line {lineNumber}: argument '{name}' of {function} is not valid JSON: {ex.Message}");
            }
            args.Add((name, value));
            i = valueEnd;
            if (i < end && line[i] == ',') i++;
        }

        return new JournalCall(function, args, lineNumber);
    }

    /// <summary>Scans one JSON literal starting at <paramref name="start"/>, stopping at the first
    /// top-level ',' or the closing ')'.</summary>
    private static int FindValueEnd(string line, int start, int end, int lineNumber)
    {
        int depth = 0;
        bool inString = false;
        for (int i = start; i < end; i++)
        {
            char c = line[i];
            if (inString)
            {
                if (c == '\\') i++;
                else if (c == '"') inString = false;
                continue;
            }
            switch (c)
            {
                case '"': inString = true; break;
                case '[' or '{': depth++; break;
                case ']' or '}': depth--; break;
                case ',' when depth == 0: return i;
            }
        }
        if (inString || depth != 0) throw new FormatException($"Line {lineNumber}: unbalanced JSON value.");
        return end;
    }
}
