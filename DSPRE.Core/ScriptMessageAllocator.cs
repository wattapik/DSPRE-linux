using System.Globalization;

namespace DSPRE.Core;

/// <summary>Who else reads a script file's text archive, which decides how freely its messages can be
/// renumbered. <see cref="Exclusive"/>: only this script file uses the archive, through no other archive
/// and never by a variable's value, so the archive can be rebuilt from the script alone.</summary>
public sealed record ScriptMessageUse(bool Exclusive, IReadOnlySet<int> UsedElsewhere, bool KeepAll, string Note)
{
    /// <summary>Nothing known about other users: keep every existing entry where it is.</summary>
    public static ScriptMessageUse Unknown { get; } = new(false, new HashSet<int>(), true, "");

    /// <summary>For checking a conversion: the archive treated as the script's own.</summary>
    public static ScriptMessageUse Own { get; } = new(true, new HashSet<int>(), false, "");
}

public sealed record MessageAllocation(string ScriptText, List<string> Messages, string Note);

/// <summary>Gives a script's message texts their numbers. A Lua script writes messages as text,
/// <c>Message("Hello?")</c>, which compiles to a script line holding the text, <c>Message "Hello?"</c>; the
/// allocator turns each into a number in the script's text archive and returns the archive to save with it.
///
/// When the script owns its archive (<see cref="ScriptMessageUse.Exclusive"/>) the archive is rebuilt:
/// exactly the texts the script shows, numbered in the order they first appear, one entry per distinct
/// text. Otherwise entries other scripts use, and all entries when something reads the archive through a
/// variable, stay where they are: a text reuses an entry that already holds it, then an entry nothing uses
/// any more, then a new one at the end. An explicit number, <c>Message(4)</c>, refers to that entry, and
/// <c>Message(4, "text")</c> also sets its text.</summary>
public static class ScriptMessageAllocator
{
    public static ScriptMessageUse FindUse(RomProject project, RomGameMetadata metadata, ScriptLanguage language, int scriptId, int archiveId)
    {
        if (MapHeaderStoreFactory.Create(project, metadata) is not { } store) return ScriptMessageUse.Unknown;
        var scriptsUsingArchive = new HashSet<int>();
        var archivesOfScript = new HashSet<int>();
        for (ushort header = 0; header < project.GetHeaderCount(); header++)
        {
            MapHeaderRecord record = store.Load(header);
            if (record.TextArchiveId == archiveId) scriptsUsingArchive.Add(record.ScriptFileId);
            if (record.ScriptFileId == scriptId) archivesOfScript.Add(record.TextArchiveId);
        }

        var usedElsewhere = new HashSet<int>();
        bool keepAll = false;
        var others = scriptsUsingArchive.Where(other => other != scriptId).Order().ToList();
        foreach (int other in others)
        {
            string path = ScriptFileService.GetPath(project, other);
            if (!File.Exists(path)) continue;
            try
            {
                (HashSet<int> numbers, bool byVariable) = MessageNumbers(ScriptTextDocument.Parse(File.ReadAllText(path), language), language);
                usedElsewhere.UnionWith(numbers);
                keepAll |= byVariable;
            }
            catch (ScriptTextException)
            {
                keepAll = true;
            }
        }

        string note;
        if (others.Count > 0)
        {
            note = $"Text archive {archiveId:D4} is shared with script file(s) {string.Join(", ", others.Take(5).Select(id => id.ToString("D4")))}" +
                   (keepAll ? ", one of which picks messages by a variable, so every existing message stays where it is." : ", so the messages they use stay where they are.");
        }
        else if (archivesOfScript.Count > 1)
        {
            keepAll = true;
            note = $"This script file is used with text archives {string.Join(", ", archivesOfScript.Order().Select(id => id.ToString("D4")))}, so its message numbers must suit each of them; existing messages stay where they are.";
        }
        else note = $"Rebuilt text archive {archiveId:D4} from the script's messages.";

        return new ScriptMessageUse(others.Count == 0 && archivesOfScript.Count <= 1 && !keepAll, usedElsewhere, keepAll, note);
    }

    /// <summary>The message numbers a script shows, and whether any message slot is given a variable,
    /// whose value can be any message.</summary>
    private static (HashSet<int> Numbers, bool ByVariable) MessageNumbers(ScriptTextDocument document, ScriptLanguage language)
    {
        var numbers = new HashSet<int>();
        bool byVariable = false;
        foreach (ScriptTextLine line in document.Scripts.Concat(document.Functions).SelectMany(container => container.Lines))
        {
            if (language.FindCommand(line.Command) is not { } info || info.ParameterSizes is [0xFF, ..]) continue;
            for (int i = 0; i < line.Args.Count; i++)
            {
                if (!ScriptLanguage.IsMessageParameter(info, i) || !TryParseNumber(line.Args[i], out long value)) continue;
                if (value >= 0x4000) byVariable = true;
                else numbers.Add((int)value);
            }
        }
        return (numbers, byVariable);
    }

    private abstract record Slot;
    private sealed record Token(string Text) : Slot;
    private sealed record Message(int? Number, string? Text) : Slot
    {
        public int Assigned { get; set; } = -1;
    }

    public static MessageAllocation Allocate(ScriptLanguage language, string scriptText, IReadOnlyList<string> archive, ScriptMessageUse use)
    {
        ScriptTextDocument document = ScriptTextDocument.Parse(scriptText, language);
        var lines = new List<(ScriptTextContainer Container, int Index, List<Slot> Slots)>();
        var messages = new List<Message>();
        bool byVariable = false;
        bool brokenNumber = false;

        foreach (ScriptTextContainer container in document.Scripts.Concat(document.Functions))
        {
            for (int index = 0; index < container.Lines.Count; index++)
            {
                ScriptTextLine line = container.Lines[index];
                ScriptCommandInfo? info = language.FindCommand(line.Command);
                var slots = new List<Slot>();
                int parameter = 0;
                foreach (string arg in line.Args)
                {
                    bool messageSlot = info is not null && info.ParameterSizes is not [0xFF, ..] && ScriptLanguage.IsMessageParameter(info, parameter);
                    if (arg.StartsWith('"'))
                    {
                        string text = Unquote(arg);
                        if (slots.Count > 0 && slots[^1] is Message { Text: null, Number: int number } numbered)
                        {
                            slots[^1] = numbered with { Text = text };
                            messages[^1] = (Message)slots[^1];
                            continue;
                        }
                        if (!messageSlot) throw new InvalidDataException($"{line.Command} has text \"{text}\" where it takes no message.");
                        var message = new Message(null, text);
                        slots.Add(message);
                        messages.Add(message);
                    }
                    else if (messageSlot && TryParseNumber(arg, out long past) && past < 0x4000 && past >= archive.Count)
                    {
                        // A number past the archive's end shows nothing the script owns (often a script file
                        // shared with another archive); it stays as written, and so must every entry.
                        brokenNumber = true;
                        slots.Add(new Token(arg));
                    }
                    else if (messageSlot && TryParseNumber(arg, out long value) && value < 0x4000)
                    {
                        var message = new Message((int)value, null);
                        slots.Add(message);
                        messages.Add(message);
                    }
                    else
                    {
                        if (messageSlot) byVariable = true;
                        slots.Add(new Token(arg));
                    }
                    parameter++;
                }
                lines.Add((container, index, slots));
            }
        }

        List<string> result;
        string note = use.Note;
        if (use.Exclusive && !byVariable && !brokenNumber)
        {
            result = [];
            var numbers = new Dictionary<string, int>(StringComparer.Ordinal);
            foreach (Message message in messages)
            {
                string text = message.Text ?? ExistingText(archive, message.Number!.Value);
                if (!numbers.TryGetValue(text, out int number))
                {
                    number = result.Count;
                    result.Add(text);
                    numbers[text] = number;
                }
                message.Assigned = number;
            }
        }
        else
        {
            if (byVariable && use.Exclusive) note = "This script picks a message by a variable, so every existing message stays where it is.";
            else if (brokenNumber && use.Exclusive) note = "This script shows a message number past the end of its text archive, so every existing message stays where it is.";
            result = [.. archive];
            var pinned = new HashSet<int>(use.UsedElsewhere);
            if (use.KeepAll || byVariable || brokenNumber) pinned.UnionWith(Enumerable.Range(0, result.Count));
            var used = new HashSet<int>();

            foreach (Message message in messages.Where(message => message.Number is not null))
            {
                int number = message.Number!.Value;
                if (number >= result.Count) throw new InvalidDataException($"Message {number} does not exist; the text archive has {result.Count} messages.");
                if (message.Text is not null) result[number] = message.Text;
                message.Assigned = number;
                pinned.Add(number);
                used.Add(number);
            }

            // Texts already in the archive keep their entry, before new texts take any free one.
            foreach (Message message in messages.Where(message => message.Number is null))
            {
                int existing = result.IndexOf(message.Text!);
                if (existing < 0) continue;
                message.Assigned = existing;
                used.Add(existing);
            }
            foreach (Message message in messages.Where(message => message.Assigned < 0))
            {
                int existing = result.IndexOf(message.Text!);
                if (existing >= 0 && used.Contains(existing))
                {
                    message.Assigned = existing;
                    continue;
                }
                int free = Enumerable.Range(0, result.Count).FirstOrDefault(i => !pinned.Contains(i) && !used.Contains(i), -1);
                if (free < 0)
                {
                    free = result.Count;
                    result.Add(message.Text!);
                }
                else result[free] = message.Text!;
                message.Assigned = free;
                used.Add(free);
            }

            while (result.Count > 0 && !pinned.Contains(result.Count - 1) && !used.Contains(result.Count - 1)) result.RemoveAt(result.Count - 1);
        }

        foreach ((ScriptTextContainer container, int index, List<Slot> slots) in lines)
        {
            container.Lines[index] = container.Lines[index] with
            {
                Args = slots.Select(slot => slot is Message message ? message.Assigned.ToString(CultureInfo.InvariantCulture) : ((Token)slot).Text).ToList(),
            };
        }
        return new MessageAllocation(document.Write(language), result, note);
    }

    private static string ExistingText(IReadOnlyList<string> archive, int number) =>
        number < archive.Count ? archive[number] : throw new InvalidDataException($"Message {number} does not exist; the text archive has {archive.Count} messages.");

    /// <summary>The inline form's text: as stored, with quotes escaped (ScriptMessageText).</summary>
    private static string Unquote(string token) => token[1..^1].Replace("\\\"", "\"");

    private static bool TryParseNumber(string token, out long value) =>
        token.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(token[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out value)
            : long.TryParse(token, NumberStyles.Integer, CultureInfo.InvariantCulture, out value);
}
