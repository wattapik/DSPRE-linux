using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DSPRE.Core;

/// <summary>One level-up move of one Pokémon, as the bulk learnset editor lists them.</summary>
public sealed record LearnsetRow(int PokemonId, int Level, int MoveId);

/// <summary>Learnset and egg move files in the formats legacy reads and writes (DS_Map/DocTool.cs and
/// LearnsetBulkEditor.cs): learnset CSV "ID,Name,Level,Move" with moves by name, learnset JSON keyed
/// by SPECIES_ constants, egg move CSV "SpeciesID,SpeciesName,MoveID,MoveName".</summary>
public static class LearnsetExchange
{
    public sealed record ImportResult(List<LearnsetRow> Rows, List<string> Errors, List<string> Warnings, int LinesRead);

    public static string ToCsv(IEnumerable<LearnsetRow> rows, int pokemonCount, IReadOnlyList<string> pokemonNames, IReadOnlyList<string> moveNames)
    {
        var text = new StringBuilder("ID,Name,Level,Move\n");
        ILookup<int, LearnsetRow> byPokemon = rows.ToLookup(row => row.PokemonId);
        for (int pokemon = 0; pokemon < pokemonCount; pokemon++)
        {
            string name = Csv(NameAt(pokemonNames, pokemon));
            // As legacy: a Pokémon with no moves still gets a row, so the file lists every species.
            if (!byPokemon[pokemon].Any()) text.Append($"{pokemon},{name},,\n");
            foreach (LearnsetRow row in byPokemon[pokemon]) text.Append($"{pokemon},{name},{row.Level},{Csv(NameAt(moveNames, row.MoveId))}\n");
        }

        return text.ToString();
    }

    /// <summary>Reads a learnset CSV. Rows naming an unknown Pokémon or move, or a level outside 1-100,
    /// are errors and left out; a name that disagrees with the ID is only a warning (the ID wins). Moves
    /// may be given by name or number.</summary>
    public static ImportResult FromCsv(string text, int pokemonCount, IReadOnlyList<string> pokemonNames, IReadOnlyList<string> moveNames)
    {
        var rows = new List<LearnsetRow>();
        var errors = new List<string>();
        var warnings = new List<string>();
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        string[] header = SplitCsv(lines[0]);
        if (header.Length < 4 || !header[0].Trim().Equals("ID", StringComparison.OrdinalIgnoreCase) || !header[3].Trim().Equals("Move", StringComparison.OrdinalIgnoreCase))
        {
            errors.Add($"Line 1: expected the header 'ID,Name,Level,Move', found '{lines[0]}'.");
            return new ImportResult(rows, errors, warnings, 0);
        }

        var moveIds = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (int i = moveNames.Count - 1; i >= 0; i--) moveIds[moveNames[i].Trim()] = i;

        int read = 0;
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            read++;
            string[] parts = SplitCsv(lines[i]);
            int line = i + 1;
            if (parts.Length < 4)
            {
                errors.Add($"Line {line}: expected 4 columns, found {parts.Length}.");
                continue;
            }

            if (!int.TryParse(parts[0].Trim(), out int pokemon) || pokemon < 0 || pokemon >= pokemonCount)
            {
                errors.Add($"Line {line}: '{parts[0]}' is not a Pokémon ID (0-{pokemonCount - 1}).");
                continue;
            }

            if (parts[1].Trim().Length > 0 && !parts[1].Trim().Equals(NameAt(pokemonNames, pokemon).Trim(), StringComparison.OrdinalIgnoreCase))
            {
                warnings.Add($"Line {line}: ID {pokemon} is {NameAt(pokemonNames, pokemon)}, not '{parts[1].Trim()}' - the ID is used.");
            }

            // "ID,Name,," marks a Pokémon with no moves; nothing to add, but it still counts as listed.
            if (parts[2].Trim().Length == 0 && parts[3].Trim().Length == 0)
            {
                rows.Add(new LearnsetRow(pokemon, 0, 0));
                continue;
            }

            if (!int.TryParse(parts[2].Trim(), out int level) || level is < 1 or > 100)
            {
                errors.Add($"Line {line}: level '{parts[2]}' is not 1-100.");
                continue;
            }

            string move = parts[3].Trim();
            if (!moveIds.TryGetValue(move, out int moveId) && !(int.TryParse(move, out moveId) && moveId > 0 && moveId < moveNames.Count))
            {
                errors.Add($"Line {line}: '{move}' is not a move.");
                continue;
            }

            rows.Add(new LearnsetRow(pokemon, level, moveId));
        }

        return new ImportResult(rows, errors, warnings, read);
    }

    /// <summary>Legacy's JSON export: { "SPECIES_X": { "LevelMoves": [ { "Level": 1, "Move": "MOVE_Y" } ] } }.</summary>
    public static string ToJson(IEnumerable<LearnsetRow> rows, int pokemonCount, IReadOnlyList<string> pokemonNames, IReadOnlyList<string> moveNames)
    {
        var root = new JsonObject();
        ILookup<int, LearnsetRow> byPokemon = rows.ToLookup(row => row.PokemonId);
        for (int pokemon = 0; pokemon < pokemonCount; pokemon++)
        {
            var moves = new JsonArray();
            foreach (LearnsetRow row in byPokemon[pokemon].Where(row => row.MoveId != 0))
            {
                moves.Add(new JsonObject { ["Level"] = row.Level, ["Move"] = Constant("MOVE_", NameAt(moveNames, row.MoveId)) });
            }

            string key = Constant("SPECIES_", NameAt(pokemonNames, pokemon));
            for (int copy = 2; root.ContainsKey(key); copy++) key = $"{Constant("SPECIES_", NameAt(pokemonNames, pokemon))}_{copy}";
            root[key] = new JsonObject { ["LevelMoves"] = moves };
        }

        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping });
    }

    public static string EggMovesToCsv(IEnumerable<EggMoveEntry> entries, IReadOnlyList<string> pokemonNames, IReadOnlyList<string> moveNames)
    {
        var text = new StringBuilder("SpeciesID,SpeciesName,MoveID,MoveName\n");
        foreach (EggMoveEntry entry in entries)
        {
            foreach (ushort move in entry.MoveIds) text.Append($"{entry.SpeciesId},{Csv(NameAt(pokemonNames, entry.SpeciesId))},{move},{Csv(NameAt(moveNames, move))}\n");
        }

        return text.ToString();
    }

    /// <summary>Reads an egg move CSV by its ID columns (names are only for people), grouping moves by
    /// species in the order they first appear - legacy ImportEggMoveDataFromCSV.</summary>
    public static List<EggMoveEntry> EggMovesFromCsv(string text)
    {
        var bySpecies = new Dictionary<int, EggMoveEntry>();
        var order = new List<EggMoveEntry>();
        string[] lines = text.Replace("\r\n", "\n").Split('\n');
        for (int i = 1; i < lines.Length; i++)
        {
            if (string.IsNullOrWhiteSpace(lines[i])) continue;
            string[] parts = SplitCsv(lines[i]);
            if (parts.Length < 4 || !int.TryParse(parts[0].Trim(), out int species) || !ushort.TryParse(parts[2].Trim(), out ushort move))
            {
                throw new FormatException($"Line {i + 1} is not 'SpeciesID,SpeciesName,MoveID,MoveName': '{lines[i]}'.");
            }

            if (!bySpecies.TryGetValue(species, out EggMoveEntry? entry))
            {
                bySpecies[species] = entry = new EggMoveEntry { SpeciesId = species };
                order.Add(entry);
            }

            entry.MoveIds.Add(move);
        }

        return order;
    }

    private static string NameAt(IReadOnlyList<string> names, int id) => id >= 0 && id < names.Count ? names[id] : $"#{id}";

    /// <summary>"SPECIES_BULBASAUR" style; a name with no letters or digits (entry 0's "-----") is NONE.</summary>
    private static string Constant(string prefix, string name)
    {
        string body = new string(name.ToUpperInvariant().Select(c => char.IsLetterOrDigit(c) ? c : '_').ToArray()).Replace("__", "_").Trim('_');
        return prefix + (body.Length == 0 ? "NONE" : body);
    }

    private static string Csv(string value) => value.IndexOfAny([',', '"', '\n']) < 0 ? value : $"\"{value.Replace("\"", "\"\"")}\"";

    private static string[] SplitCsv(string line)
    {
        var parts = new List<string>();
        var current = new StringBuilder();
        bool quoted = false;
        for (int i = 0; i < line.Length; i++)
        {
            char c = line[i];
            if (c == '"' && quoted && i + 1 < line.Length && line[i + 1] == '"') { current.Append('"'); i++; }
            else if (c == '"') quoted = !quoted;
            else if (c == ',' && !quoted) { parts.Add(current.ToString()); current.Clear(); }
            else current.Append(c);
        }

        parts.Add(current.ToString());
        return parts.ToArray();
    }
}
