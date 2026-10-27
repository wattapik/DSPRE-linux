using System.Text;
using System.Text.RegularExpressions;

namespace DSPRE.Core.Lua;

/// <summary>Message text between a Lua string and the text archive. In Lua the text reads as the archive
/// stores it, with the game's escape codes written the same way: <c>"Hi!\rBye."</c>. Lua turns \r, \n and
/// the like into control characters, which <see cref="ToInlineToken"/> turns back into the codes, and keeps
/// any other escape (\x, \1...) as written.</summary>
public static class LuaMessageText
{
    private static readonly (char Character, string Code)[] Codes =
        [('\n', "\\n"), ('\r', "\\r"), ('\t', "\\t"), ('\f', "\\f"), ('\v', "\\v"), ('\a', "\\a"), ('\b', "\\b")];

    /// <summary>A Lua string literal whose value <see cref="ToInlineToken"/> turns back into exactly
    /// <paramref name="stored"/>. Text a quoted string cannot carry exactly goes in a [[long string]].</summary>
    public static string ToLuaLiteral(string stored)
    {
        bool quotable = !stored.Contains("\\\\") && !stored.Contains("\\\"") && !stored.Contains("\\'") && !stored.EndsWith('\\')
                        && !stored.Any(char.IsControl);
        if (quotable) return "\"" + stored.Replace("\"", "\\\"") + "\"";

        int level = 0;
        while (stored.Contains("]" + new string('=', level) + "]")) level++;
        string equals = new('=', level);
        // A long string drops a newline right after its opening bracket, so one there would be lost.
        return $"[{equals}[" + (stored.StartsWith('\n') ? "\n" : "") + stored + $"]{equals}]";
    }

    /// <summary>A message number followed by its text among a call's arguments: <c>Message(4, "Hi!")</c>,
    /// <c>GenderMessage(4, "Hi, sir!", 5, "Hi, ma'am!")</c>, <c>npc_message(4, "Hi!")</c>.</summary>
    private static readonly Regex MessageArgument = new(@"(?<=[(,]\s*)(?<index>\d+|0[xX][0-9A-Fa-f]+)\s*,\s*""(?<text>(?:\\.|[^""\\\n])*)""");

    /// <summary>Lists of message numbers followed by their texts: <c>npc_message({4, 5}, {"Hi!", "Bye!"})</c>.</summary>
    private static readonly Regex MessageListArgument = new(
        @"(?<=[(,]\s*)\{(?<numbers>\s*(?:\d+|0[xX][0-9A-Fa-f]+)\s*(?:,\s*(?:\d+|0[xX][0-9A-Fa-f]+)\s*)*,?\s*)\}\s*,\s*\{(?<texts>\s*""(?:\\.|[^""\\\n])*""\s*(?:,\s*""(?:\\.|[^""\\\n])*""\s*)*,?\s*)\}");

    private static readonly Regex ListNumber = new(@"0[xX][0-9A-Fa-f]+|\d+");
    private static readonly Regex ListText = new(@"""(?<text>(?:\\.|[^""\\\n])*)""");

    /// <summary>The message texts written in Lua: each message number with the text as the archive
    /// would store it.</summary>
    public static IEnumerable<(int Index, string Text)> FindTexts(string lua)
    {
        foreach (Match match in MessageArgument.Matches(lua))
        {
            yield return (ParseIndex(match.Groups["index"].Value), Unquote(match.Groups["text"].Value));
        }
        foreach (Match match in MessageListArgument.Matches(lua))
        {
            foreach ((int index, string text) in ListPairs(match)) yield return (index, text);
        }
    }

    /// <summary>Rewrites message texts: <paramref name="replace"/> gets the number and current text and
    /// returns the new text, or null to leave it.</summary>
    public static string ReplaceTexts(string lua, Func<int, string, string?> replace)
    {
        lua = MessageArgument.Replace(lua, match =>
        {
            string current = Unquote(match.Groups["text"].Value);
            string? literal = Replacement(ParseIndex(match.Groups["index"].Value), current, replace);
            if (literal is null) return match.Value;
            Group text = match.Groups["text"];
            return match.Value[..(text.Index - match.Index - 1)] + literal;
        });
        return MessageListArgument.Replace(lua, match =>
        {
            List<(int Index, string Text)> pairs = ListPairs(match);
            if (pairs.Count == 0) return match.Value;
            int pair = 0;
            Group texts = match.Groups["texts"];
            string rewritten = ListText.Replace(texts.Value, text =>
            {
                (int index, string current) = pairs[pair++];
                return Replacement(index, current, replace) ?? text.Value;
            });
            return match.Value[..(texts.Index - match.Index)] + rewritten + match.Value[(texts.Index - match.Index + texts.Length)..];
        });
    }

    private static string? Replacement(int index, string current, Func<int, string, string?> replace)
    {
        string? updated = replace(index, current);
        if (updated is null || updated == current) return null;
        string literal = ToLuaLiteral(updated);
        return literal.StartsWith('"') ? literal : null;
    }

    /// <summary>A list's numbers paired with its texts; none when the two lists differ in length.</summary>
    private static List<(int Index, string Text)> ListPairs(Match match)
    {
        List<int> numbers = ListNumber.Matches(match.Groups["numbers"].Value).Select(number => ParseIndex(number.Value)).ToList();
        List<string> texts = ListText.Matches(match.Groups["texts"].Value).Select(text => Unquote(text.Groups["text"].Value)).ToList();
        return numbers.Count == texts.Count ? numbers.Zip(texts).ToList() : [];
    }

    private static string Unquote(string text) => text.Replace("\\\"", "\"");

    private static int ParseIndex(string token) =>
        token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt32(token[2..], 16) : int.Parse(token);

    /// <summary>The script editor's inline form of message text, <c>"..."</c>, as ScriptMessageText reads
    /// it: the text as stored with quotes escaped.</summary>
    public static string ToInlineToken(string value)
    {
        var builder = new StringBuilder("\"");
        foreach (char c in value)
        {
            string? code = Codes.FirstOrDefault(pair => pair.Character == c).Code;
            builder.Append(code ?? (c == '"' ? "\\\"" : c.ToString()));
        }
        return builder.Append('"').ToString();
    }
}
