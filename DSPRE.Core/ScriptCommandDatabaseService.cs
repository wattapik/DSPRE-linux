using System.Net.Http;
using System.Text.Json;

namespace DSPRE.Core;

/// <summary>Parameter type tags used by script command database entries - ported verbatim from
/// DS_Map/Script/ScriptParameter.cs's ParameterType enum (same names, parsed case-insensitively from
/// the JSON's "parameter_types" strings, so this must stay in sync with that enum's member names).</summary>
public enum ScriptParameterType
{
    Integer,
    Variable,
    Flex,
    Overworld,
    OwMovementType,
    OwMovementDirection,
    ComparisonOperator,
    Function,
    Action,
    CMDNumber,
    Pokemon,
    Item,
    Move,
    Sound,
    Trainer,
}

/// <summary>One script command's reference entry: its opcode, names, description, and parameter
/// layout. Mirrors DS_Map/Resources/ScriptDatabase.cs's ScriptCommandInfo.</summary>
public sealed class ScriptCommandInfo
{
    public required ushort CommandId { get; init; }
    public string Name { get; init; } = "";
    public string DecompName { get; init; } = "";
    public string Description { get; init; } = "";
    /// <summary>Optional free-text caveat some entries carry (e.g. alignment requirements) - present
    /// in the source database but not read by legacy's own loader; kept here since it's useful in a
    /// reference/search view.</summary>
    public string? Notes { get; init; }
    public IReadOnlyList<byte> ParameterSizes { get; init; } = [];
    public IReadOnlyList<ScriptParameterType?> ParameterTypes { get; init; } = [];
    public IReadOnlyList<string> ParameterNames { get; init; } = [];
}

/// <summary>A named sub-entry keyed by its own small ID range - used for "movements" (overworld
/// movement command bytes), a separate, smaller reference table alongside the main script command
/// list in the same database file.</summary>
public sealed class ScriptSubEntryInfo
{
    public required ushort Id { get; init; }
    public string Name { get; init; } = "";
    public string DecompName { get; init; } = "";
    public string Description { get; init; } = "";
}

/// <summary>Everything loaded from one script command database JSON file - mirrors the dictionaries
/// DS_Map/Resources/ScriptDatabase.cs populates from the same file format.</summary>
public sealed class ScriptCommandDatabase
{
    public IReadOnlyDictionary<ushort, ScriptCommandInfo> Commands { get; init; } = new Dictionary<ushort, ScriptCommandInfo>();
    public IReadOnlyDictionary<ushort, ScriptSubEntryInfo> Movements { get; init; } = new Dictionary<ushort, ScriptSubEntryInfo>();
    public IReadOnlyDictionary<ushort, string> ComparisonOperators { get; init; } = new Dictionary<ushort, string>();
    public IReadOnlyDictionary<ushort, string> SpecialOverworlds { get; init; } = new Dictionary<ushort, string>();
    public IReadOnlyDictionary<byte, string> OverworldDirections { get; init; } = new Dictionary<byte, string>();
    public IReadOnlyDictionary<ushort, string> Sounds { get; init; } = new Dictionary<ushort, string>();
}

/// <summary>Loads the script command reference database - the same JSON files (and file-naming
/// convention) legacy's DS_Map/Helpers.cs InitializeScriptDatabase and
/// DS_Map/Resources/ScriptDatabase.cs's ScriptDatabaseJsonLoader use, sourced from
/// https://github.com/DS-Pokemon-Rom-Editor/scrcmd-database (cloned into a "databases" directory
/// alongside the built app by CI - see CLAUDE.md - not committed to this repo).</summary>
public static class ScriptCommandDatabaseService
{
    /// <summary>The exact built-in filename legacy expects for each header format, matching
    /// DS_Map/Helpers.cs's InitializeScriptDatabase switch and the scrcmd-database repo's own
    /// "*_scrcmd_database.json" naming.</summary>
    public static string GetFileName(MapHeaderFormat format) => format switch
    {
        MapHeaderFormat.DiamondPearl => "diamond_pearl_scrcmd_database.json",
        MapHeaderFormat.Platinum => "platinum_scrcmd_database.json",
        MapHeaderFormat.HeartGoldSoulSilver => "hgss_scrcmd_database.json",
        _ => throw new ArgumentOutOfRangeException(nameof(format), format, null),
    };

    /// <summary>Resolves the built-in database path for a header format under the given databases
    /// directory (e.g. Path.Combine(AppContext.BaseDirectory, "databases")), or null if it isn't
    /// present there.</summary>
    public static string? ResolvePath(string databasesDirectory, MapHeaderFormat format)
    {
        string path = Path.Combine(databasesDirectory, GetFileName(format));
        return File.Exists(path) ? path : null;
    }

    // "main" confirmed as the repo's default branch when this was written; raw.githubusercontent.com
    // serves the file directly with no API rate-limit concerns the way the GitHub REST API has.
    private const string RawBaseUrl = "https://raw.githubusercontent.com/DS-Pokemon-Rom-Editor/scrcmd-database/main/";
    private static readonly HttpClient DownloadClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <summary>Downloads the built-in database for a header format from the same
    /// DS-Pokemon-Rom-Editor/scrcmd-database repo legacy's own "check for database updates" pulls
    /// from, and writes it into databasesDirectory (overwriting any existing copy) - an on-demand
    /// counterpart to expecting the "databases" folder to already be populated externally (see
    /// ResolvePath). Returns the path it wrote.</summary>
    public static async Task<string> DownloadAsync(string databasesDirectory, MapHeaderFormat format, CancellationToken cancellationToken = default)
    {
        string fileName = GetFileName(format);
        using HttpResponseMessage response = await DownloadClient.GetAsync(RawBaseUrl + fileName, cancellationToken);
        response.EnsureSuccessStatusCode();
        byte[] content = await response.Content.ReadAsByteArrayAsync(cancellationToken);

        // Parse before overwriting anything on disk, so a truncated/malformed download (a bad response
        // body that still returned 200, a network blip mid-transfer) can't clobber a working database
        // with something Load() will just fail on the next time the panel opens.
        using (JsonDocument.Parse(content)) { }

        Directory.CreateDirectory(databasesDirectory);
        string path = Path.Combine(databasesDirectory, fileName);
        await File.WriteAllBytesAsync(path, content, cancellationToken);
        return path;
    }

    public static ScriptCommandDatabase Load(string jsonPath)
    {
        using FileStream stream = File.OpenRead(jsonPath);
        using JsonDocument document = JsonDocument.Parse(stream);
        JsonElement root = document.RootElement;

        return new ScriptCommandDatabase
        {
            Movements = ParseSubEntries(root, "movements"),
            ComparisonOperators = ParseStringMap(root, "comparisonOperators"),
            SpecialOverworlds = ParseStringMap(root, "specialOverworlds"),
            OverworldDirections = ParseByteStringMap(root, "overworldDirections"),
            Sounds = ParseSoundNames(root),
            Commands = ParseCommands(root),
        };
    }

    /// <summary>Keys in this format are always "0xXXXX" hex strings - matches
    /// ScriptDatabaseJsonLoader's own Convert.ToUInt16(name.Substring(2), 16) parsing exactly.</summary>
    private static ushort ParseHexKey(string key) => Convert.ToUInt16(key[2..], 16);

    private static Dictionary<ushort, string> ParseStringMap(JsonElement root, string propertyName)
    {
        var result = new Dictionary<ushort, string>();
        if (!root.TryGetProperty(propertyName, out JsonElement section)) return result;
        foreach (JsonProperty property in section.EnumerateObject())
        {
            result[ParseHexKey(property.Name)] = property.Value.GetString() ?? "";
        }

        return result;
    }

    private static Dictionary<byte, string> ParseByteStringMap(JsonElement root, string propertyName)
    {
        var result = new Dictionary<byte, string>();
        if (!root.TryGetProperty(propertyName, out JsonElement section)) return result;
        foreach (JsonProperty property in section.EnumerateObject())
        {
            result[(byte)ParseHexKey(property.Name)] = property.Value.GetString() ?? "";
        }

        return result;
    }

    private static Dictionary<ushort, ScriptSubEntryInfo> ParseSubEntries(JsonElement root, string propertyName)
    {
        var result = new Dictionary<ushort, ScriptSubEntryInfo>();
        if (!root.TryGetProperty(propertyName, out JsonElement section)) return result;
        foreach (JsonProperty property in section.EnumerateObject())
        {
            ushort id = ParseHexKey(property.Name);
            JsonElement entry = property.Value;
            result[id] = new ScriptSubEntryInfo
            {
                Id = id,
                Name = GetOptionalString(entry, "name") ?? "",
                DecompName = GetOptionalString(entry, "decomp_name") ?? "",
                Description = GetOptionalString(entry, "description") ?? "",
            };
        }

        return result;
    }

    private static Dictionary<ushort, string> ParseSoundNames(JsonElement root)
    {
        var result = new Dictionary<ushort, string>();
        if (!root.TryGetProperty("sounds", out JsonElement section)) return result;
        foreach (JsonProperty property in section.EnumerateObject())
        {
            if (ushort.TryParse(property.Name, out ushort id) &&
                property.Value.TryGetProperty("name", out JsonElement nameElement))
            {
                result[id] = nameElement.GetString() ?? "";
            }
        }

        return result;
    }

    private static Dictionary<ushort, ScriptCommandInfo> ParseCommands(JsonElement root)
    {
        var result = new Dictionary<ushort, ScriptCommandInfo>();
        if (!root.TryGetProperty("scrcmd", out JsonElement section))
        {
            throw new InvalidDataException("Script command database is missing the \"scrcmd\" key.");
        }

        foreach (JsonProperty property in section.EnumerateObject())
        {
            ushort code = ParseHexKey(property.Name);
            JsonElement entry = property.Value;

            var parameterSizes = new List<byte>();
            if (entry.TryGetProperty("parameters", out JsonElement parametersElement))
            {
                foreach (JsonElement sizeElement in parametersElement.EnumerateArray())
                {
                    parameterSizes.Add((byte)sizeElement.GetInt32());
                }
            }

            var parameterTypes = new List<ScriptParameterType?>();
            if (entry.TryGetProperty("parameter_types", out JsonElement typesElement))
            {
                foreach (JsonElement typeElement in typesElement.EnumerateArray())
                {
                    string? typeString = typeElement.GetString();
                    parameterTypes.Add(
                        !string.IsNullOrEmpty(typeString) && Enum.TryParse(typeString, ignoreCase: true, out ScriptParameterType parsed)
                            ? parsed
                            : null);
                }
            }

            var parameterNames = new List<string>();
            if (entry.TryGetProperty("parameter_values", out JsonElement namesElement))
            {
                foreach (JsonElement nameElement in namesElement.EnumerateArray())
                {
                    parameterNames.Add(nameElement.GetString() ?? "");
                }
            }

            result[code] = new ScriptCommandInfo
            {
                CommandId = code,
                Name = GetOptionalString(entry, "name") ?? "",
                DecompName = GetOptionalString(entry, "decomp_name") ?? "",
                Description = GetOptionalString(entry, "description") ?? "",
                Notes = GetOptionalString(entry, "notes"),
                ParameterSizes = parameterSizes,
                ParameterTypes = parameterTypes,
                ParameterNames = parameterNames,
            };
        }

        return result;
    }

    private static string? GetOptionalString(JsonElement entry, string propertyName) =>
        entry.TryGetProperty(propertyName, out JsonElement value) ? value.GetString() : null;
}
