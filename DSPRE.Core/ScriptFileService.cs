using System.Text.Json.Nodes;

namespace DSPRE.Core;

/// <summary>Plaintext script files (expanded/scripts/NNNN.script). Saves are journaled as line hunks,
/// <c>EditScriptFile(script: 12, line: 30, remove: 2, insert: ["...", "..."])</c>, applied in order,
/// with each hunk's 1-based line number referring to the file as left by the previous hunk.</summary>
public static class ScriptFileService
{
    public static string GetPath(RomProject project, int id) => Path.Combine(project.ExpandedScriptsDirectory, $"{id:D4}.script");

    public static void Save(RomProject project, int id, string content) =>
        SaveJournaled(project, GetPath(project, id), "EditScriptFile", [("script", id)], $"Saved script file {id:D4}", content);

    /// <summary>Writes a text file and journals the change as line hunks of <paramref name="function"/>
    /// (<paramref name="key"/>..., line, remove, insert).</summary>
    internal static void SaveJournaled(RomProject project, string path, string function, (string, JsonNode?)[] key, string description, string content)
    {
        string previous = File.Exists(path) ? File.ReadAllText(path) : string.Empty;
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, content);
        if (previous == content) return;

        var calls = new List<(string, (string, JsonNode?)[])>();
        foreach ((int line, int remove, string[] insert) in ComputeHunks(SplitLines(previous), SplitLines(content)))
        {
            calls.Add((function, [.. key, ("line", line), ("remove", remove), ("insert", new JsonArray(insert.Select(text => (JsonNode?)text).ToArray()))]));
        }
        EditJournal.RecordAll(project, description, calls);
    }

    /// <summary>Applies one journaled line hunk to a text file, then saves it the journaled way.</summary>
    internal static void ApplyHunk(string path, string fileDescription, int line, int remove, IReadOnlyList<string> insert, Action<string> save)
    {
        List<string> lines = SplitLines(File.Exists(path) ? File.ReadAllText(path) : string.Empty).ToList();
        int index = line - 1;
        if (index < 0 || index + remove > lines.Count)
        {
            throw new InvalidDataException($"{fileDescription} has {lines.Count} lines; cannot replace {remove} at line {line}.");
        }
        lines.RemoveRange(index, remove);
        lines.InsertRange(index, insert);
        save(string.Join('\n', lines));
    }

    /// <summary>Every trainer a script file fights: the TRAINER_NAME_ID arguments of its battle
    /// commands (TrainerBattle, Battle2vs2, FirstBattle...), with the Script/Function each sits in.
    /// Gym leaders, rivals and the like are fought this way instead of through a trainer overworld.
    /// Flag commands such as SetTrainerFlag name trainers too but start no battle, so they are left out.</summary>
    public static List<ScriptTrainerBattle> FindTrainerBattles(RomProject project, int id)
    {
        var battles = new List<ScriptTrainerBattle>();
        string path = GetPath(project, id);
        if (!File.Exists(path)) return battles;

        string section = string.Empty;
        foreach (string rawLine in File.ReadLines(path))
        {
            string line = rawLine.Trim();
            if (SectionHeader.Match(line) is { Success: true } header)
            {
                section = header.Groups[1].Value;
                continue;
            }

            int space = line.IndexOf(' ');
            if (space <= 0 || !line[..space].Contains("Battle", StringComparison.Ordinal)) continue;
            foreach (System.Text.RegularExpressions.Match trainer in TrainerConstant.Matches(line))
            {
                battles.Add(new ScriptTrainerBattle(int.Parse(trainer.Groups[1].Value), line[..space], section));
            }
        }

        return battles;
    }

    private static readonly System.Text.RegularExpressions.Regex SectionHeader = new(@"^((?:Script|Function|Action)\s*#?\s*\d+):$");
    private static readonly System.Text.RegularExpressions.Regex TrainerConstant = new(@"\bTRAINER_\w*?_(\d+)\b");

    public static void ApplyEdit(RomProject project, int id, int line, int remove, IReadOnlyList<string> insert) =>
        ApplyHunk(GetPath(project, id), $"Script {id:D4}", line, remove, insert, content => Save(project, id, content));

    private static string[] SplitLines(string text) => text.Length == 0 ? [] : text.Split('\n');

    private static IEnumerable<(int Line, int Remove, string[] Insert)> ComputeHunks(string[] before, string[] after)
    {
        IReadOnlyList<(int OldIndex, int NewIndex)> matches = EditJournalModel.LongestCommonSubsequence(before, after)
            ?? TrimOnly(before, after);

        int oldCursor = 0, newCursor = 0, offset = 0;
        foreach ((int oldIndex, int newIndex) in matches.Append((before.Length, after.Length)))
        {
            int removed = oldIndex - oldCursor, inserted = newIndex - newCursor;
            if (removed > 0 || inserted > 0)
            {
                yield return (oldCursor + offset + 1, removed, after[newCursor..newIndex]);
                offset += inserted - removed;
            }
            oldCursor = oldIndex + 1;
            newCursor = newIndex + 1;
        }
    }

    /// <summary>Fallback when the changed region is too large to align line by line: keep the common
    /// prefix and suffix and replace everything between as one hunk.</summary>
    private static List<(int, int)> TrimOnly(string[] before, string[] after)
    {
        int prefix = 0;
        while (prefix < before.Length && prefix < after.Length && before[prefix] == after[prefix]) prefix++;
        int suffix = 0;
        while (suffix < before.Length - prefix && suffix < after.Length - prefix && before[^(suffix + 1)] == after[^(suffix + 1)]) suffix++;

        var matches = new List<(int, int)>();
        for (int i = 0; i < prefix; i++) matches.Add((i, i));
        for (int i = 0; i < suffix; i++) matches.Add((before.Length - suffix + i, after.Length - suffix + i));
        return matches;
    }
}

/// <summary>One trainer battle found in a script file by ScriptFileService.FindTrainerBattles.</summary>
public sealed record ScriptTrainerBattle(int TrainerId, string Command, string Section);
