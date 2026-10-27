namespace DSPRE.Core;

public sealed class PersonalDataCsvEntry
{
    public int PokemonId;
    public string PokemonName = string.Empty;
    public PokemonType Type1;
    public PokemonType Type2;
    public byte BaseHP, BaseAtk, BaseDef, BaseSpAtk, BaseSpDef, BaseSpeed;
    public byte EvHP, EvAtk, EvDef, EvSpAtk, EvSpDef, EvSpeed;
    public byte Ability1, Ability2;
    public ushort Item1, Item2;
    public byte CatchRate, GivenExp, GenderRatio, EggSteps, BaseFriendship;
    public PokemonGrowthCurve GrowthCurve;
    public byte EggGroup1, EggGroup2, EscapeRate;
    public PokemonDexColor DexColor;
    public bool Flip;
}

public sealed class PersonalDataCsvImportResult
{
    public List<PersonalDataCsvEntry> ValidEntries { get; } = [];
    public List<string> Errors { get; } = [];
    public int TotalRowsRead { get; set; }
}

public sealed record PersonalDataCsvChange(int PokemonId, string PokemonName, IReadOnlyList<string> ChangedFields);

/// <summary>CSV export/import for the whole personal data table, in the exact 31-column format the
/// legacy PersonalDataEditor.ExportToCSV/ValidateAndParseCSV used, so files remain interchangeable with
/// the legacy tool. Kept UI-framework-agnostic (Avalonia builds the preview dialog around this).</summary>
public static class PersonalDataCsvService
{
    public const string Header = "Pokemon ID,Pokemon Name,Type 1,Type 2,Base HP,Base Atk,Base Def,Base SpAtk,Base SpDef,Base Speed," +
        "EV HP,EV Atk,EV Def,EV SpAtk,EV SpDef,EV Speed," +
        "Ability 1,Ability 2,Item 1,Item 2," +
        "Catch Rate,Base Exp,Gender Ratio,Egg Steps,Base Friendship,Growth Curve," +
        "Egg Group 1,Egg Group 2,Escape Rate,Dex Color,Flip";

    public static string ExportRow(int id, string name, PersonalDataRecord d, string[] typeNames, string[] abilityNames, string[] itemNames)
    {
        string type1 = (int)d.Type1 < typeNames.Length ? typeNames[(int)d.Type1] : d.Type1.ToString();
        string type2 = (int)d.Type2 < typeNames.Length ? typeNames[(int)d.Type2] : d.Type2.ToString();
        string ability1 = d.FirstAbility < abilityNames.Length ? abilityNames[d.FirstAbility] : $"Ability_{d.FirstAbility}";
        string ability2 = d.SecondAbility < abilityNames.Length ? abilityNames[d.SecondAbility] : $"Ability_{d.SecondAbility}";
        string item1 = d.Item1 < itemNames.Length ? itemNames[d.Item1] : $"Item_{d.Item1}";
        string item2 = d.Item2 < itemNames.Length ? itemNames[d.Item2] : $"Item_{d.Item2}";

        return $"{id},{name},{type1},{type2}," +
            $"{d.BaseHP},{d.BaseAtk},{d.BaseDef},{d.BaseSpAtk},{d.BaseSpDef},{d.BaseSpeed}," +
            $"{d.EvHP},{d.EvAtk},{d.EvDef},{d.EvSpAtk},{d.EvSpDef},{d.EvSpeed}," +
            $"{ability1},{ability2},{item1},{item2}," +
            $"{d.CatchRate},{d.GivenExp},{d.GenderRatio},{d.EggSteps},{d.BaseFriendship},{d.GrowthCurve}," +
            $"{(PokemonEggGroup)d.EggGroup1},{(PokemonEggGroup)d.EggGroup2},{d.EscapeRate},{d.Color},{d.Flip}";
    }

    public static PersonalDataCsvImportResult Parse(string[] lines, string[] typeNames, string[] abilityNames, string[] itemNames, int maxId)
    {
        var result = new PersonalDataCsvImportResult();
        if (lines.Length == 0)
        {
            result.Errors.Add("File is empty.");
            return result;
        }

        result.TotalRowsRead = lines.Length - 1;

        Dictionary<string, int> typeByName = BuildNameIndex(typeNames);
        Dictionary<string, int> abilityByName = BuildNameIndex(abilityNames);
        Dictionary<string, int> itemByName = BuildNameIndex(itemNames);
        var eggGroupByName = Enum.GetValues<PokemonEggGroup>().ToDictionary(g => g.ToString(), g => (byte)g, StringComparer.OrdinalIgnoreCase);
        var growthByName = Enum.GetValues<PokemonGrowthCurve>().ToDictionary(g => g.ToString(), g => g, StringComparer.OrdinalIgnoreCase);
        var colorByName = Enum.GetValues<PokemonDexColor>().ToDictionary(c => c.ToString(), c => c, StringComparer.OrdinalIgnoreCase);

        for (int lineNumber = 1; lineNumber < lines.Length; lineNumber++)
        {
            string line = lines[lineNumber];
            if (string.IsNullOrWhiteSpace(line)) continue;

            string[] parts = line.Split(',');
            if (parts.Length < 31)
            {
                result.Errors.Add($"Line {lineNumber + 1}: expected 31 columns, got {parts.Length}.");
                continue;
            }

            if (!int.TryParse(parts[0].Trim(), out int id) || id < 0 || id > maxId)
            {
                result.Errors.Add($"Line {lineNumber + 1}: invalid Pokemon ID '{parts[0]}'.");
                continue;
            }

            if (!typeByName.TryGetValue(parts[2].Trim(), out int type1)) { result.Errors.Add($"Line {lineNumber + 1}: unknown Type 1 '{parts[2]}'."); continue; }
            if (!typeByName.TryGetValue(parts[3].Trim(), out int type2)) { result.Errors.Add($"Line {lineNumber + 1}: unknown Type 2 '{parts[3]}'."); continue; }
            if (!byte.TryParse(parts[4].Trim(), out byte hp)) { result.Errors.Add($"Line {lineNumber + 1}: invalid Base HP '{parts[4]}'."); continue; }
            if (!byte.TryParse(parts[5].Trim(), out byte atk)) { result.Errors.Add($"Line {lineNumber + 1}: invalid Base Atk '{parts[5]}'."); continue; }
            if (!byte.TryParse(parts[6].Trim(), out byte def)) { result.Errors.Add($"Line {lineNumber + 1}: invalid Base Def '{parts[6]}'."); continue; }
            if (!byte.TryParse(parts[7].Trim(), out byte spAtk)) { result.Errors.Add($"Line {lineNumber + 1}: invalid Base SpAtk '{parts[7]}'."); continue; }
            if (!byte.TryParse(parts[8].Trim(), out byte spDef)) { result.Errors.Add($"Line {lineNumber + 1}: invalid Base SpDef '{parts[8]}'."); continue; }
            if (!byte.TryParse(parts[9].Trim(), out byte speed)) { result.Errors.Add($"Line {lineNumber + 1}: invalid Base Speed '{parts[9]}'."); continue; }
            if (!byte.TryParse(parts[10].Trim(), out byte evHp) || evHp > 3) { result.Errors.Add($"Line {lineNumber + 1}: invalid EV HP '{parts[10]}'. Must be 0-3."); continue; }
            if (!byte.TryParse(parts[11].Trim(), out byte evAtk) || evAtk > 3) { result.Errors.Add($"Line {lineNumber + 1}: invalid EV Atk '{parts[11]}'. Must be 0-3."); continue; }
            if (!byte.TryParse(parts[12].Trim(), out byte evDef) || evDef > 3) { result.Errors.Add($"Line {lineNumber + 1}: invalid EV Def '{parts[12]}'. Must be 0-3."); continue; }
            if (!byte.TryParse(parts[13].Trim(), out byte evSpAtk) || evSpAtk > 3) { result.Errors.Add($"Line {lineNumber + 1}: invalid EV SpAtk '{parts[13]}'. Must be 0-3."); continue; }
            if (!byte.TryParse(parts[14].Trim(), out byte evSpDef) || evSpDef > 3) { result.Errors.Add($"Line {lineNumber + 1}: invalid EV SpDef '{parts[14]}'. Must be 0-3."); continue; }
            if (!byte.TryParse(parts[15].Trim(), out byte evSpeed) || evSpeed > 3) { result.Errors.Add($"Line {lineNumber + 1}: invalid EV Speed '{parts[15]}'. Must be 0-3."); continue; }
            if (!abilityByName.TryGetValue(parts[16].Trim(), out int ability1)) { result.Errors.Add($"Line {lineNumber + 1}: unknown Ability 1 '{parts[16]}'."); continue; }
            if (!abilityByName.TryGetValue(parts[17].Trim(), out int ability2)) { result.Errors.Add($"Line {lineNumber + 1}: unknown Ability 2 '{parts[17]}'."); continue; }
            if (!itemByName.TryGetValue(parts[18].Trim(), out int item1)) { result.Errors.Add($"Line {lineNumber + 1}: unknown Item 1 '{parts[18]}'."); continue; }
            if (!itemByName.TryGetValue(parts[19].Trim(), out int item2)) { result.Errors.Add($"Line {lineNumber + 1}: unknown Item 2 '{parts[19]}'."); continue; }
            if (!byte.TryParse(parts[20].Trim(), out byte catchRate)) { result.Errors.Add($"Line {lineNumber + 1}: invalid Catch Rate '{parts[20]}'."); continue; }
            if (!byte.TryParse(parts[21].Trim(), out byte baseExp)) { result.Errors.Add($"Line {lineNumber + 1}: invalid Base Exp '{parts[21]}'."); continue; }
            if (!byte.TryParse(parts[22].Trim(), out byte genderRatio)) { result.Errors.Add($"Line {lineNumber + 1}: invalid Gender Ratio '{parts[22]}'."); continue; }
            if (!byte.TryParse(parts[23].Trim(), out byte eggSteps)) { result.Errors.Add($"Line {lineNumber + 1}: invalid Egg Steps '{parts[23]}'."); continue; }
            if (!byte.TryParse(parts[24].Trim(), out byte baseFriendship)) { result.Errors.Add($"Line {lineNumber + 1}: invalid Base Friendship '{parts[24]}'."); continue; }
            if (!growthByName.TryGetValue(parts[25].Trim(), out PokemonGrowthCurve growth)) { result.Errors.Add($"Line {lineNumber + 1}: unknown Growth Curve '{parts[25]}'."); continue; }
            if (!eggGroupByName.TryGetValue(parts[26].Trim(), out byte eggGroup1)) { result.Errors.Add($"Line {lineNumber + 1}: unknown Egg Group 1 '{parts[26]}'."); continue; }
            if (!eggGroupByName.TryGetValue(parts[27].Trim(), out byte eggGroup2)) { result.Errors.Add($"Line {lineNumber + 1}: unknown Egg Group 2 '{parts[27]}'."); continue; }
            if (!byte.TryParse(parts[28].Trim(), out byte escapeRate)) { result.Errors.Add($"Line {lineNumber + 1}: invalid Escape Rate '{parts[28]}'."); continue; }
            if (!colorByName.TryGetValue(parts[29].Trim(), out PokemonDexColor color)) { result.Errors.Add($"Line {lineNumber + 1}: unknown Dex Color '{parts[29]}'."); continue; }
            if (!bool.TryParse(parts[30].Trim(), out bool flip)) { result.Errors.Add($"Line {lineNumber + 1}: invalid Flip value '{parts[30]}'. Must be True or False."); continue; }

            result.ValidEntries.Add(new PersonalDataCsvEntry
            {
                PokemonId = id,
                PokemonName = parts[1].Trim(),
                Type1 = (PokemonType)type1,
                Type2 = (PokemonType)type2,
                BaseHP = hp, BaseAtk = atk, BaseDef = def, BaseSpAtk = spAtk, BaseSpDef = spDef, BaseSpeed = speed,
                EvHP = evHp, EvAtk = evAtk, EvDef = evDef, EvSpAtk = evSpAtk, EvSpDef = evSpDef, EvSpeed = evSpeed,
                Ability1 = (byte)ability1, Ability2 = (byte)ability2,
                Item1 = (ushort)item1, Item2 = (ushort)item2,
                CatchRate = catchRate, GivenExp = baseExp, GenderRatio = genderRatio,
                EggSteps = eggSteps, BaseFriendship = baseFriendship, GrowthCurve = growth,
                EggGroup1 = eggGroup1, EggGroup2 = eggGroup2, EscapeRate = escapeRate,
                DexColor = color, Flip = flip
            });
        }

        return result;
    }

    /// <summary>Diffs each valid entry against what's currently on disk, for the import preview's
    /// "Changes" tab - entries with no field differences are omitted, matching the legacy preview.</summary>
    public static List<PersonalDataCsvChange> ComputeChanges(RomProject project, IReadOnlyList<PersonalDataCsvEntry> entries, string[] typeNames, string[] abilityNames)
    {
        var changes = new List<PersonalDataCsvChange>();
        foreach (PersonalDataCsvEntry entry in entries)
        {
            PersonalDataRecord current;
            try
            {
                current = PersonalDataService.Load(project, entry.PokemonId);
            }
            catch (Exception)
            {
                changes.Add(new PersonalDataCsvChange(entry.PokemonId, entry.PokemonName, ["Could not load current data"]));
                continue;
            }

            var fields = new List<string>();
            void Diff<T>(string label, T oldValue, T newValue) where T : IEquatable<T>
            {
                if (!oldValue.Equals(newValue)) fields.Add($"{label}: {oldValue} -> {newValue}");
            }

            Diff("Type1", (int)current.Type1 < typeNames.Length ? typeNames[(int)current.Type1] : current.Type1.ToString(),
                (int)entry.Type1 < typeNames.Length ? typeNames[(int)entry.Type1] : entry.Type1.ToString());
            Diff("Type2", (int)current.Type2 < typeNames.Length ? typeNames[(int)current.Type2] : current.Type2.ToString(),
                (int)entry.Type2 < typeNames.Length ? typeNames[(int)entry.Type2] : entry.Type2.ToString());
            Diff("BaseHP", current.BaseHP, entry.BaseHP);
            Diff("BaseAtk", current.BaseAtk, entry.BaseAtk);
            Diff("BaseDef", current.BaseDef, entry.BaseDef);
            Diff("BaseSpAtk", current.BaseSpAtk, entry.BaseSpAtk);
            Diff("BaseSpDef", current.BaseSpDef, entry.BaseSpDef);
            Diff("BaseSpeed", current.BaseSpeed, entry.BaseSpeed);
            Diff("EvHP", current.EvHP, entry.EvHP);
            Diff("EvAtk", current.EvAtk, entry.EvAtk);
            Diff("EvDef", current.EvDef, entry.EvDef);
            Diff("EvSpAtk", current.EvSpAtk, entry.EvSpAtk);
            Diff("EvSpDef", current.EvSpDef, entry.EvSpDef);
            Diff("EvSpeed", current.EvSpeed, entry.EvSpeed);
            Diff("Ability1", current.FirstAbility < abilityNames.Length ? abilityNames[current.FirstAbility] : current.FirstAbility.ToString(),
                entry.Ability1 < abilityNames.Length ? abilityNames[entry.Ability1] : entry.Ability1.ToString());
            Diff("Ability2", current.SecondAbility < abilityNames.Length ? abilityNames[current.SecondAbility] : current.SecondAbility.ToString(),
                entry.Ability2 < abilityNames.Length ? abilityNames[entry.Ability2] : entry.Ability2.ToString());
            Diff("Item1", current.Item1, entry.Item1);
            Diff("Item2", current.Item2, entry.Item2);
            Diff("CatchRate", current.CatchRate, entry.CatchRate);
            Diff("BaseExp", current.GivenExp, entry.GivenExp);
            Diff("GenderRatio", current.GenderRatio, entry.GenderRatio);
            Diff("EggSteps", current.EggSteps, entry.EggSteps);
            Diff("BaseFriendship", current.BaseFriendship, entry.BaseFriendship);
            Diff("GrowthCurve", current.GrowthCurve.ToString(), entry.GrowthCurve.ToString());
            Diff("EggGroup1", ((PokemonEggGroup)current.EggGroup1).ToString(), ((PokemonEggGroup)entry.EggGroup1).ToString());
            Diff("EggGroup2", ((PokemonEggGroup)current.EggGroup2).ToString(), ((PokemonEggGroup)entry.EggGroup2).ToString());
            Diff("EscapeRate", current.EscapeRate, entry.EscapeRate);
            Diff("DexColor", current.Color.ToString(), entry.DexColor.ToString());
            Diff("Flip", current.Flip, entry.Flip);

            if (fields.Count > 0)
            {
                changes.Add(new PersonalDataCsvChange(entry.PokemonId, entry.PokemonName, fields));
            }
        }

        return changes;
    }

    /// <summary>Applies one entry to the ROM's personal data, preserving the existing TM/HM compatibility
    /// bitfield (not part of the CSV), matching the legacy importer.</summary>
    public static void Apply(RomProject project, PersonalDataCsvEntry entry)
    {
        PersonalDataRecord data = PersonalDataService.Load(project, entry.PokemonId);
        data.Type1 = entry.Type1;
        data.Type2 = entry.Type2;
        data.BaseHP = entry.BaseHP; data.BaseAtk = entry.BaseAtk; data.BaseDef = entry.BaseDef;
        data.BaseSpAtk = entry.BaseSpAtk; data.BaseSpDef = entry.BaseSpDef; data.BaseSpeed = entry.BaseSpeed;
        data.EvHP = entry.EvHP; data.EvAtk = entry.EvAtk; data.EvDef = entry.EvDef;
        data.EvSpAtk = entry.EvSpAtk; data.EvSpDef = entry.EvSpDef; data.EvSpeed = entry.EvSpeed;
        data.FirstAbility = entry.Ability1; data.SecondAbility = entry.Ability2;
        data.Item1 = entry.Item1; data.Item2 = entry.Item2;
        data.CatchRate = entry.CatchRate; data.GivenExp = entry.GivenExp; data.GenderRatio = entry.GenderRatio;
        data.EggSteps = entry.EggSteps; data.BaseFriendship = entry.BaseFriendship; data.GrowthCurve = entry.GrowthCurve;
        data.EggGroup1 = entry.EggGroup1; data.EggGroup2 = entry.EggGroup2; data.EscapeRate = entry.EscapeRate;
        data.Color = entry.DexColor; data.Flip = entry.Flip;

        PersonalDataService.Save(project, entry.PokemonId, data);
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
