namespace DSPRE.Core;

public sealed class MoveDataCsvEntry
{
    public int MoveId;
    public string MoveName = string.Empty;
    public PokemonType MoveType;
    public MoveSplit Split;
    public byte Power;
    public byte Accuracy;
    public sbyte Priority;
    public byte SideEffectProbability;
    public byte Pp;
    public ushort Range;
}

public sealed class MoveDataCsvImportResult
{
    public List<MoveDataCsvEntry> ValidEntries { get; } = [];
    public List<string> Errors { get; } = [];
    public int TotalRowsRead { get; set; }
}

public sealed record MoveDataCsvChange(int MoveId, string MoveName, IReadOnlyList<string> ChangedFields);

/// <summary>CSV export/import for the move data table, matching the legacy MoveDataEditor's 10-column
/// format ("Move ID,Move Name,Move Type,Move Split,Power,Accuracy,Priority,Side Effect Probability,PP,
/// Range") so files stay interchangeable with the legacy tool. The legacy importer's "rename mismatched
/// move names in ROM" bonus feature is not ported - out of scope for a data editor.</summary>
public static class MoveDataCsvService
{
    public const string Header = "Move ID,Move Name,Move Type,Move Split,Power,Accuracy,Priority,Side Effect Probability,PP,Range";

    public static string ExportRow(int id, string name, MoveRecord move, string[] typeNames)
    {
        string typeStr = (int)move.MoveType < typeNames.Length ? typeNames[(int)move.MoveType] : $"UnknownType_{(int)move.MoveType}";
        string rangeStr = MoveDataService.GetAttackRangeName(move.Target);
        return $"{id},{name},{typeStr},{move.Split},{move.Damage},{move.Accuracy},{move.Priority},{move.SideEffectProbability},{move.Pp},{rangeStr}";
    }

    public static MoveDataCsvImportResult Parse(string[] lines, string[] typeNames, int maxId)
    {
        var result = new MoveDataCsvImportResult();
        if (lines.Length == 0)
        {
            result.Errors.Add("File is empty.");
            return result;
        }

        result.TotalRowsRead = lines.Length - 1;

        Dictionary<string, int> typeByName = BuildNameIndex(typeNames);
        var splitByName = Enum.GetValues<MoveSplit>().ToDictionary(s => s.ToString(), s => s, StringComparer.OrdinalIgnoreCase);
        var rangeByName = MoveDataService.AttackRangeDescriptions.ToDictionary(r => r.Name, r => r.Value, StringComparer.OrdinalIgnoreCase);

        for (int lineNumber = 1; lineNumber < lines.Length; lineNumber++)
        {
            string line = lines[lineNumber];
            if (string.IsNullOrWhiteSpace(line)) continue;

            string[] parts = line.Split(',');
            if (parts.Length < 10)
            {
                result.Errors.Add($"Line {lineNumber + 1}: expected 10 columns, got {parts.Length}.");
                continue;
            }

            if (!int.TryParse(parts[0].Trim(), out int id) || id < 0 || id > maxId)
            {
                result.Errors.Add($"Line {lineNumber + 1}: invalid Move ID '{parts[0]}'.");
                continue;
            }

            if (!typeByName.TryGetValue(parts[2].Trim(), out int type)) { result.Errors.Add($"Line {lineNumber + 1}: unknown Type '{parts[2]}'."); continue; }
            if (!splitByName.TryGetValue(parts[3].Trim(), out MoveSplit split)) { result.Errors.Add($"Line {lineNumber + 1}: unknown Split '{parts[3]}'."); continue; }
            if (!byte.TryParse(parts[4].Trim(), out byte power)) { result.Errors.Add($"Line {lineNumber + 1}: invalid Power '{parts[4]}'."); continue; }
            if (!byte.TryParse(parts[5].Trim(), out byte accuracy)) { result.Errors.Add($"Line {lineNumber + 1}: invalid Accuracy '{parts[5]}'."); continue; }
            if (!sbyte.TryParse(parts[6].Trim(), out sbyte priority)) { result.Errors.Add($"Line {lineNumber + 1}: invalid Priority '{parts[6]}'."); continue; }
            if (!byte.TryParse(parts[7].Trim(), out byte sideEffect)) { result.Errors.Add($"Line {lineNumber + 1}: invalid Side Effect Probability '{parts[7]}'."); continue; }
            if (!byte.TryParse(parts[8].Trim(), out byte pp)) { result.Errors.Add($"Line {lineNumber + 1}: invalid PP '{parts[8]}'."); continue; }
            if (!rangeByName.TryGetValue(parts[9].Trim(), out ushort range)) { result.Errors.Add($"Line {lineNumber + 1}: unknown Range '{parts[9]}'."); continue; }

            result.ValidEntries.Add(new MoveDataCsvEntry
            {
                MoveId = id,
                MoveName = parts[1].Trim(),
                MoveType = (PokemonType)type,
                Split = split,
                Power = power,
                Accuracy = accuracy,
                Priority = priority,
                SideEffectProbability = sideEffect,
                Pp = pp,
                Range = range
            });
        }

        return result;
    }

    public static List<MoveDataCsvChange> ComputeChanges(RomProject project, IReadOnlyList<MoveDataCsvEntry> entries, string[] typeNames)
    {
        var changes = new List<MoveDataCsvChange>();
        foreach (MoveDataCsvEntry entry in entries)
        {
            MoveRecord current;
            try
            {
                current = MoveDataService.Load(project, entry.MoveId);
            }
            catch (Exception)
            {
                changes.Add(new MoveDataCsvChange(entry.MoveId, entry.MoveName, ["Could not load current data"]));
                continue;
            }

            var fields = new List<string>();
            void Diff<T>(string label, T oldValue, T newValue) where T : IEquatable<T>
            {
                if (!oldValue.Equals(newValue)) fields.Add($"{label}: {oldValue} -> {newValue}");
            }

            Diff("Type", (int)current.MoveType < typeNames.Length ? typeNames[(int)current.MoveType] : current.MoveType.ToString(),
                (int)entry.MoveType < typeNames.Length ? typeNames[(int)entry.MoveType] : entry.MoveType.ToString());
            Diff("Split", current.Split.ToString(), entry.Split.ToString());
            Diff("Power", current.Damage, entry.Power);
            Diff("Accuracy", current.Accuracy, entry.Accuracy);
            Diff("Priority", current.Priority, entry.Priority);
            Diff("SideEffect%", current.SideEffectProbability, entry.SideEffectProbability);
            Diff("PP", current.Pp, entry.Pp);
            Diff("Range", MoveDataService.GetAttackRangeName(current.Target), MoveDataService.GetAttackRangeName(entry.Range));

            if (fields.Count > 0)
            {
                changes.Add(new MoveDataCsvChange(entry.MoveId, entry.MoveName, fields));
            }
        }

        return changes;
    }

    /// <summary>Preserves fields not in the CSV (battle effect, flags, contest data), matching the legacy
    /// importer.</summary>
    public static void Apply(RomProject project, MoveDataCsvEntry entry)
    {
        MoveRecord move = MoveDataService.Load(project, entry.MoveId);
        move.MoveType = entry.MoveType;
        move.Split = entry.Split;
        move.Damage = entry.Power;
        move.Accuracy = entry.Accuracy;
        move.Priority = entry.Priority;
        move.SideEffectProbability = entry.SideEffectProbability;
        move.Pp = entry.Pp;
        move.Target = entry.Range;

        MoveDataService.Save(project, entry.MoveId, move);
    }

    private static Dictionary<string, int> BuildNameIndex(string[] names)
    {
        var dict = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = 0; i < names.Length; i++)
        {
            if (!string.IsNullOrEmpty(names[i]) && !dict.ContainsKey(names[i])) dict[names[i]] = i;
        }
        return dict;
    }
}
