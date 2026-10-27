using System.Text.RegularExpressions;

namespace DSPRE.Core;

/// <summary>The Script Editor shows each <c>Message N</c> with its text from the header's text archive,
/// as <c>Message N "text"</c>, and turns it back into <c>Message N</c> on save. The number, not the
/// line's position among the messages, says which archive entry the text belongs to, so adding, moving
/// or deleting Message lines never shifts text onto the wrong entry. A <c>Message "text"</c> typed
/// without a number is a new message: it reuses an entry holding the same text, or is appended to the
/// archive. The text is shown as stored (escape codes like \n as written) with only quotes escaped.</summary>
public static partial class ScriptMessageText
{
    public sealed record CollapseResult(string Content, IReadOnlyList<int> Edited, IReadOnlyList<int> Added, IReadOnlyList<int> Conflicts);

    [GeneratedRegex(@"\bMessage[ \t]+(?:(?<index>\d+)[ \t]+)?""(?<text>(?:\\.|[^""\\])*)""", RegexOptions.IgnoreCase)]
    private static partial Regex InlineMessage();

    // Exported scripts end lines with \r\n, and Multiline's $ only stops before the \n.
    [GeneratedRegex(@"\bMessage[ \t]+(?<index>\d+)(?=[ \t]*(?://|\r?$))", RegexOptions.IgnoreCase | RegexOptions.Multiline)]
    private static partial Regex NumberedMessage();

    /// <summary>Adds the text to every <c>Message N</c> whose entry exists.</summary>
    public static string Expand(string content, IReadOnlyList<string> messages) =>
        NumberedMessage().Replace(content, match =>
        {
            int index = int.Parse(match.Groups["index"].Value);
            return index < messages.Count ? $"{match.Value} \"{messages[index].Replace("\"", "\\\"")}\"" : match.Value;
        });

    /// <summary>Writes the script's inline texts into <paramref name="messages"/> and strips them from the
    /// script. An entry changes only where a line's text differs from <paramref name="baseline"/> (the
    /// archive as the script was loaded): a message used on several lines and edited on one keeps that
    /// edit, and entries no line edited keep whatever the archive holds now. Two lines editing one entry
    /// differently are reported as conflicts; the later line wins. With <paramref name="appendNew"/> false
    /// (live editing, where a half-typed line must not become a message), unnumbered texts are left in
    /// place and nothing is appended.</summary>
    public static CollapseResult Collapse(string content, List<string> messages, IReadOnlyList<string> baseline, bool appendNew)
    {
        var edits = new Dictionary<int, string>();
        var conflicts = new List<int>();
        var added = new List<int>();
        string Edit(int index, string text)
        {
            string original = index < baseline.Count ? baseline[index] : messages[index];
            if (text != original)
            {
                if (edits.TryGetValue(index, out string? earlier) && earlier != text) conflicts.Add(index);
                edits[index] = text;
            }
            return index.ToString(System.Globalization.CultureInfo.InvariantCulture);
        }

        string result = InlineMessage().Replace(content, match =>
        {
            string text = match.Groups["text"].Value.Replace("\\\"", "\"");
            if (match.Groups["index"].Success && int.TryParse(match.Groups["index"].Value, out int index) && index < messages.Count)
            {
                return $"Message {Edit(index, text)}";
            }

            if (!appendNew) return match.Value;
            int existing = messages.IndexOf(text);
            if (existing < 0)
            {
                messages.Add(text);
                existing = messages.Count - 1;
                added.Add(existing);
            }

            return $"Message {existing}";
        });

        // Other commands' message slots (GenderMessage 12 "..." 13 "...", SetIconBoard 4 "..." ...) carry
        // their text the same way, after the number; Lua scripts write them like this.
        result = NumberedText().Replace(result, match =>
        {
            int index = int.Parse(match.Groups["index"].Value);
            return index < messages.Count ? Edit(index, match.Groups["text"].Value.Replace("\\\"", "\"")) : match.Value;
        });

        var edited = new List<int>();
        foreach ((int index, string text) in edits)
        {
            if (messages[index] == text) continue;
            messages[index] = text;
            edited.Add(index);
        }

        return new CollapseResult(result, edited, added, conflicts.Distinct().ToList());
    }

    /// <summary>For a script with no text archive: <c>Message N "text"</c> loses the text it has nowhere to
    /// store, and a new <c>Message "text"</c> is an error.</summary>
    public static string StripTexts(string content) =>
        NumberedText().Replace(InlineMessage().Replace(content, match => match.Groups["index"].Success
            ? $"Message {match.Groups["index"].Value}"
            : throw new InvalidDataException($"No header using this script has a text archive, so the new message \"{match.Groups["text"].Value}\" has nowhere to go.")),
            match => match.Groups["index"].Value);

    [GeneratedRegex(@"(?<=[ \t])(?<index>\d+)[ \t]+""(?<text>(?:\\.|[^""\\])*)""")]
    private static partial Regex NumberedText();
}
