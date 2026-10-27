using System.Text.RegularExpressions;

namespace DSPRE.Core;

/// <summary>Keeps the plaintext scripts' name constants in step with the names they come from. The
/// script tools (legacy ScriptDatabase) turn trainer, Pokémon, item and move names into constants such as
/// TRAINER_CYRUS_403 or SPECIES_BULBASAUR, rebuilt from the text archives each time a script is read; after
/// a rename the scripts still say the old constant, which then no longer parses and stops the script file
/// from building. Renames are applied through ScriptFileService.Save, so they are journaled as script edits
/// of their own and a replay does not depend on this running again.</summary>
public static class ScriptConstantSync
{
    public sealed record Rename(Regex Pattern, string Replacement, string Description);

    /// <summary>Legacy ScriptDatabase.FormatStringForScripting.</summary>
    public static string Format(string name) =>
        name.ToUpperInvariant().Replace(' ', '_')
            .Replace('É', 'E').Replace("&", "AND").Replace(".", "").Replace("-", "_").Replace("’", "")
            .Replace("♂", "_M").Replace("♀", "_F");

    public static string TrainerConstant(int id, string simpleName) => id == 0 ? "TRAINER_NONE" : $"TRAINER_{Format(simpleName)}_{id:D3}";

    /// <summary>The constant prefix names from this archive get, or null when its names are not used in scripts.</summary>
    private static string? Prefix(RomGameMetadata metadata, int archiveId)
    {
        if (archiveId == TrainerService.GetTrainerNamesArchiveId(metadata)) return "TRAINER_";
        if (archiveId == TrainerService.GetPokemonNamesArchiveId(metadata)) return "SPECIES_";
        if (archiveId == TrainerService.GetItemNamesArchiveId(metadata)) return "ITEM_";
        if (archiveId == TrainerService.GetAttackNamesArchiveId(metadata)) return "MOVE_";
        return null;
    }

    public static bool Tracks(RomGameMetadata metadata, int archiveId) => Prefix(metadata, archiveId) is not null;

    /// <summary>The constant renames an archive edit needs. A trainer's constant ends in its number, so
    /// any TRAINER_..._NNN for it is renamed, including one already out of date. Other constants are
    /// matched by their old text, so only when the old and new constants are each unique.</summary>
    public static List<Rename> FindRenames(RomGameMetadata metadata, int archiveId, IReadOnlyList<string> before, IReadOnlyList<string> after)
    {
        var renames = new List<Rename>();
        string? prefix = Prefix(metadata, archiveId);
        if (prefix is null) return renames;

        if (prefix == "TRAINER_")
        {
            for (int id = 1; id < after.Count; id++)
            {
                string oldName = id < before.Count ? TrainerService.UnwrapTrainerName(before[id]) : "";
                string newName = TrainerService.UnwrapTrainerName(after[id]);
                if (id < before.Count && Format(oldName) == Format(newName)) continue;
                renames.Add(new Rename(new Regex($@"(?<!\w)TRAINER_\w*?_{id:D3}(?!\w)"), TrainerConstant(id, newName),
                    $"trainer {id} ({TrainerConstant(id, oldName)} → {TrainerConstant(id, newName)})"));
            }

            return renames;
        }

        var oldCounts = before.GroupBy(name => prefix + Format(name)).ToDictionary(group => group.Key, group => group.Count());
        var newCounts = after.GroupBy(name => prefix + Format(name)).ToDictionary(group => group.Key, group => group.Count());
        for (int id = 0; id < Math.Min(before.Count, after.Count); id++)
        {
            string oldConstant = prefix + Format(before[id]);
            string newConstant = prefix + Format(after[id]);
            if (oldConstant == newConstant || oldCounts[oldConstant] != 1 || newCounts[newConstant] != 1 || newCounts.ContainsKey(oldConstant)) continue;
            renames.Add(new Rename(new Regex($@"(?<!\w){Regex.Escape(oldConstant)}(?!\w)"), newConstant, $"{oldConstant} → {newConstant}"));
        }

        return renames;
    }

    public static string Apply(string text, IReadOnlyList<Rename> renames) =>
        renames.Aggregate(text, (current, rename) => rename.Pattern.Replace(current, rename.Replacement));

    /// <summary>Applies the renames to every plaintext and Lua script, saving (and so journaling) each one
    /// that changes. Returns the script files changed.</summary>
    public static List<int> UpdateScripts(RomProject project, IReadOnlyList<Rename> renames)
    {
        var changed = new List<int>();
        if (renames.Count == 0 || !Directory.Exists(project.ExpandedScriptsDirectory)) return changed;
        foreach (string path in Directory.GetFiles(project.ExpandedScriptsDirectory, "*.script").Order(StringComparer.Ordinal))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out int id)) continue;
            string content = File.ReadAllText(path);
            string updated = Apply(content, renames);
            if (updated == content) continue;
            ScriptFileService.Save(project, id, updated);
            changed.Add(id);
        }

        // A script written in Lua names the same constants, and its .script is rebuilt from it on save.
        foreach (string path in Directory.GetFiles(project.ExpandedScriptsDirectory, "*.lua").Order(StringComparer.Ordinal))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out int id)) continue;
            string content = File.ReadAllText(path);
            string updated = Apply(content, renames);
            if (updated == content) continue;
            LuaScriptService.Save(project, id, updated);
            if (!changed.Contains(id)) changed.Add(id);
        }

        string? global = LuaScriptService.LoadGlobal(project);
        if (global is not null && Apply(global, renames) is string updatedGlobal && updatedGlobal != global) LuaScriptService.SaveGlobal(project, updatedGlobal);

        return changed;
    }
}
