using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSPRE.Core;

/// <summary>The character map chatot encodes and decodes the game's text with: game character code
/// (4 hex digits) -> character, plus aliases that also encode to that code. Same file format and
/// operations as legacy DS_Map/Resources/CharMaps/CharMapManager.cs.</summary>
public sealed class CharMap
{
    [JsonPropertyName("metadata")]
    public CharMapMetadata Metadata { get; set; } = new();

    [JsonPropertyName("char_map")]
    public Dictionary<string, CharMapEntry> CharacterMap { get; set; } = [];

    [JsonPropertyName("command_map")]
    public Dictionary<string, string> CommandMap { get; set; } = [];

    public CharMapEntry? GetEntry(ushort code) => CharacterMap.GetValueOrDefault(code.ToString("X4"));

    public IEnumerable<ushort> GetAllCodes()
    {
        foreach (string key in CharacterMap.Keys)
        {
            if (ushort.TryParse(key, NumberStyles.HexNumber, null, out ushort code)) yield return code;
        }
    }

    /// <summary>The code a character or alias encodes to; characters are matched before aliases.</summary>
    public ushort? FindCode(string characterOrAlias)
    {
        foreach (var (key, entry) in CharacterMap)
        {
            if (entry.Character == characterOrAlias && ushort.TryParse(key, NumberStyles.HexNumber, null, out ushort code)) return code;
        }

        foreach (var (key, entry) in CharacterMap)
        {
            if (entry.Aliases?.Contains(characterOrAlias) == true && ushort.TryParse(key, NumberStyles.HexNumber, null, out ushort code)) return code;
        }

        return null;
    }

    public CharMap Clone() => new()
    {
        Metadata = new CharMapMetadata { Description = Metadata.Description, Version = Metadata.Version },
        CharacterMap = CharacterMap.ToDictionary(pair => pair.Key, pair => new CharMapEntry { Character = pair.Value.Character, Aliases = pair.Value.Aliases?.ToList() }),
        CommandMap = new Dictionary<string, string>(CommandMap)
    };
}

public sealed class CharMapMetadata
{
    [JsonPropertyName("description")]
    public string Description { get; set; } = "";

    [JsonPropertyName("version")]
    public string Version { get; set; } = "1.0.0";
}

public sealed class CharMapEntry
{
    [JsonPropertyName("char")]
    public string Character { get; set; } = "";

    [JsonPropertyName("aliases")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? Aliases { get; set; }

    public void AddAlias(string alias)
    {
        Aliases ??= [];
        if (!Aliases.Contains(alias)) Aliases.Add(alias);
    }

    public bool RemoveAlias(string alias)
    {
        if (Aliases is null) return false;
        bool removed = Aliases.Remove(alias);
        if (Aliases.Count == 0) Aliases = null;
        return removed;
    }
}

/// <summary>How a rebase settles a code the custom and default maps give different characters.</summary>
public enum CharMapMergeStrategy
{
    /// <summary>Keep the custom character, merging both alias lists.</summary>
    PreferCustom,
    /// <summary>Keep the default character, adding the custom aliases.</summary>
    PreferBase,
    /// <summary>Use only the custom map (taking the default's version).</summary>
    ReplaceBase
}

public sealed class CharMapMergeResult
{
    public required CharMap MergedMap { get; init; }
    public int AddedEntries { get; set; }
    public int UpdatedEntries { get; set; }
    public int ConflictingEntries { get; set; }
    public int AddedAliases { get; set; }
    public List<string> Conflicts { get; } = [];

    public string Summary =>
        $"Added entries: {AddedEntries}\nUpdated entries: {UpdatedEntries}\nConflicting entries: {ConflictingEntries}\nAdded aliases: {AddedAliases}\nTotal conflicts: {Conflicts.Count}";
}

/// <summary>The default character map ships in Tools/charmap.json; a user can make a custom copy
/// (aliases, changed characters), which then replaces the default for all text decoding and encoding.
/// Kept per user rather than per project, as in legacy.</summary>
public static class CharMapStore
{
    public const string FileName = "charmap.json";

    public static string CustomPath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSPRE Reloaded", FileName);

    public static string DefaultPath(string toolsDirectory) => Path.Combine(toolsDirectory, FileName);

    /// <summary>The map chatot should use: the custom one when there is one.</summary>
    public static string ActivePath(string toolsDirectory) => File.Exists(CustomPath) ? CustomPath : DefaultPath(toolsDirectory);

    private static readonly JsonSerializerOptions ReadOptions = new()
    {
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public static CharMap Load(string path) =>
        JsonSerializer.Deserialize<CharMap>(File.ReadAllText(path, Encoding.UTF8), ReadOptions)
        ?? throw new InvalidDataException($"{path} is not a character map.");

    public static void SaveCustom(CharMap map)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CustomPath)!);
        File.WriteAllText(CustomPath, JsonSerializer.Serialize(map, WriteOptions), new UTF8Encoding(false));
    }

    public static void CreateCustom(string toolsDirectory)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(CustomPath)!);
        File.Copy(DefaultPath(toolsDirectory), CustomPath, overwrite: true);
    }

    public static void DeleteCustom()
    {
        if (File.Exists(CustomPath)) File.Delete(CustomPath);
    }

    /// <summary>The map's metadata version, 1.0 when it has none (legacy GetCharMapVersion).</summary>
    public static Version GetVersion(string path)
    {
        try
        {
            using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
            if (document.RootElement.TryGetProperty("metadata", out JsonElement metadata) &&
                metadata.TryGetProperty("version", out JsonElement version) &&
                Version.TryParse(version.GetString(), out Version? parsed))
            {
                return parsed;
            }
        }
        catch (JsonException)
        {
        }

        return new Version(1, 0);
    }

    /// <summary>True when the custom map was made from an older default map than the one shipped now.</summary>
    public static bool IsCustomOutdated(string toolsDirectory) =>
        File.Exists(CustomPath) && File.Exists(DefaultPath(toolsDirectory)) && GetVersion(CustomPath) < GetVersion(DefaultPath(toolsDirectory));

    /// <summary>Legacy MergeCharMaps: rebases the custom map onto the (newer) default one.</summary>
    public static CharMapMergeResult Merge(CharMap baseMap, CharMap customMap, CharMapMergeStrategy strategy)
    {
        if (strategy == CharMapMergeStrategy.ReplaceBase)
        {
            CharMap replaced = customMap.Clone();
            replaced.Metadata.Version = baseMap.Metadata.Version;
            return new CharMapMergeResult { MergedMap = replaced, AddedEntries = customMap.CharacterMap.Count };
        }

        CharMap merged = baseMap.Clone();
        merged.Metadata.Description = customMap.Metadata.Description;
        var result = new CharMapMergeResult { MergedMap = merged };
        foreach (var (code, custom) in customMap.CharacterMap)
        {
            if (!merged.CharacterMap.TryGetValue(code, out CharMapEntry? baseEntry))
            {
                merged.CharacterMap[code] = new CharMapEntry { Character = custom.Character, Aliases = custom.Aliases?.ToList() };
                result.AddedEntries++;
                continue;
            }

            if (baseEntry.Character != custom.Character)
            {
                result.ConflictingEntries++;
                result.Conflicts.Add($"0x{code}: Base='{baseEntry.Character}' vs Custom='{custom.Character}'");
                if (strategy == CharMapMergeStrategy.PreferCustom)
                {
                    merged.CharacterMap[code] = new CharMapEntry { Character = custom.Character, Aliases = MergeAliases(baseEntry.Aliases, custom.Aliases) };
                    result.UpdatedEntries++;
                }
                else
                {
                    foreach (string alias in custom.Aliases ?? [])
                    {
                        if (baseEntry.Aliases?.Contains(alias) == true) continue;
                        baseEntry.AddAlias(alias);
                        result.AddedAliases++;
                    }
                }

                continue;
            }

            List<string>? aliases = MergeAliases(baseEntry.Aliases, custom.Aliases);
            int before = baseEntry.Aliases?.Count ?? 0;
            if (aliases is not null && aliases.Count > before)
            {
                merged.CharacterMap[code] = new CharMapEntry { Character = baseEntry.Character, Aliases = aliases };
                result.AddedAliases += aliases.Count - before;
            }
        }

        foreach (var (code, command) in customMap.CommandMap)
        {
            if (!merged.CommandMap.TryGetValue(code, out string? baseCommand))
            {
                merged.CommandMap[code] = command;
            }
            else if (baseCommand != command)
            {
                result.Conflicts.Add($"Command 0x{code}: Base='{baseCommand}' vs Custom='{command}'");
                if (strategy == CharMapMergeStrategy.PreferCustom) merged.CommandMap[code] = command;
            }
        }

        return result;
    }

    private static List<string>? MergeAliases(List<string>? first, List<string>? second)
    {
        if (first is null && second is null) return null;
        List<string> merged = (first ?? []).Concat(second ?? []).Distinct().ToList();
        return merged.Count > 0 ? merged : null;
    }
}
