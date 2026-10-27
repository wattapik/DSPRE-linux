using DSPRE.Core.Lua;

namespace DSPRE.Core;

/// <summary>Script files written in Lua (expanded/scripts/NNNN.lua, beside NNNN.script). The .lua is the
/// one edited; saving it compiles it into the .script, which the ROM is still built from. Saves are
/// journaled as line hunks like the .script's, <c>EditLuaScript(script: 12, line: 30, remove: 2,
/// insert: [...])</c>, and going back to plain script editing as <c>DeleteLuaScript(script: 12)</c>.</summary>
public static class LuaScriptService
{
    public static string GetPath(RomProject project, int id) => Path.Combine(project.ExpandedScriptsDirectory, $"{id:D4}.lua");

    public static bool Exists(RomProject project, int id) => File.Exists(GetPath(project, id));

    public static void Save(RomProject project, int id, string content) =>
        ScriptFileService.SaveJournaled(project, GetPath(project, id), "EditLuaScript", [("script", id)], $"Saved Lua script file {id:D4}", content);

    public static void ApplyEdit(RomProject project, int id, int line, int remove, IReadOnlyList<string> insert) =>
        ScriptFileService.ApplyHunk(GetPath(project, id), $"Lua script {id:D4}", line, remove, insert, content => Save(project, id, content));

    public static void Delete(RomProject project, int id)
    {
        string path = GetPath(project, id);
        if (!File.Exists(path)) return;
        File.Delete(path);
        EditJournal.Record(project, "DeleteLuaScript", ("script", id));
    }

    private static readonly Dictionary<string, (DateTime Written, ScriptLanguage Language)> LanguageCache = [];

    /// <summary>The script language of the database the project's scripts are written with: the ROM's own
    /// copy when it has one (see ScriptDatabaseStore), else the built-in one in
    /// <paramref name="databasesDirectory"/>.</summary>
    public static ScriptLanguage LoadLanguage(RomProject project, RomGameMetadata metadata, string databasesDirectory)
    {
        string path = File.Exists(ScriptDatabaseStore.ProjectDatabasePath(project))
            ? ScriptDatabaseStore.ProjectDatabasePath(project)
            : ScriptCommandDatabaseService.ResolvePath(databasesDirectory, metadata.HeaderFormat)
              ?? throw new FileNotFoundException($"No script command database was found for this ROM; download the built-in databases from Settings first.");
        DateTime written = File.GetLastWriteTimeUtc(path);
        lock (LanguageCache)
        {
            if (LanguageCache.TryGetValue(path, out (DateTime Written, ScriptLanguage Language) cached) && cached.Written == written) return cached.Language;
            var language = new ScriptLanguage(ScriptCommandDatabaseService.Load(path));
            LanguageCache[path] = (written, language);
            return language;
        }
    }

    /// <summary>Lua for a plaintext script, checked to compile back to code that behaves the same before
    /// it is returned. Given <paramref name="messages"/>, the script's text archive, messages are written
    /// as their texts, <c>Message("Hello?")</c>, and the check compares the texts shown rather than message
    /// numbers, which saving reassigns. Scripts that a template in <paramref name="global"/> makes are
    /// written as calls to it.</summary>
    public static string Lift(ScriptLanguage language, int id, string scriptText, IReadOnlyList<string>? messages, string? global)
    {
        ScriptTextDocument original = ScriptTextDocument.Parse(scriptText, language);
        string lua = LuaScriptLifter.Lift(original, language,
            $"Script file {id:D4}, converted from {id:D4}.script.\nSaving compiles this file into {id:D4}.script; edit this file rather than the .script.\n" +
            "Messages are written as their text; saving numbers them and rebuilds the text archive. Helpers like npc_message are in global.lua.",
            messages, LuaScriptCompiler.FindTemplates(global, language), LuaScriptCompiler.FindNames(global, language));

        string? difference = FindMismatch(language, lua, scriptText, messages, global, ScriptMessageUse.Own);
        if (difference is not null) throw new InvalidDataException($"The Lua version of script file {id:D4} would not behave the same ({difference}), so it was not converted.");
        return lua;
    }

    /// <summary>Compiles Lua into the text of a .script file, before message numbering: a message written
    /// as text is left as <c>Message "text"</c> (see <see cref="CompileForSave"/>).</summary>
    public static (string ScriptText, IReadOnlyList<string> Warnings) Compile(ScriptLanguage language, string lua, string? global)
    {
        LuaCompileResult result = LuaScriptCompiler.Compile(lua, language, global);
        return (result.Document.Write(language), result.Warnings);
    }

    public sealed record SaveResult(string ScriptText, List<string>? Messages, string Note, IReadOnlyList<string> Warnings);

    /// <summary>Compiles Lua into the .script to save and numbers its messages
    /// (<see cref="ScriptMessageAllocator"/>): <see cref="SaveResult.Messages"/> is the text archive to save
    /// with it, or null when the script has no text archive, in which case it must give messages by number.</summary>
    public static SaveResult CompileForSave(ScriptLanguage language, string lua, string? global, IReadOnlyList<string>? archive, ScriptMessageUse use)
    {
        (string text, IReadOnlyList<string> warnings) = Compile(language, lua, global);
        if (archive is null)
        {
            if (ScriptTextDocument.Parse(text, language).Scripts.Concat(ScriptTextDocument.Parse(text, language).Functions)
                .SelectMany(container => container.Lines).Any(line => line.Args.Any(arg => arg.StartsWith('"'))))
            {
                throw new InvalidDataException("No header using this script has a text archive, so its messages cannot be written as text; give them by number.");
            }
            return new SaveResult(text, null, "", warnings);
        }
        MessageAllocation allocation = ScriptMessageAllocator.Allocate(language, text, archive, use);
        return new SaveResult(allocation.ScriptText, allocation.Messages, allocation.Note, warnings);
    }

    /// <summary>Null when the Lua compiles to code that behaves like <paramref name="scriptText"/>,
    /// otherwise why not: the .script was edited some other way since the Lua was last saved, or the Lua
    /// no longer compiles. Messages compare by text, against <paramref name="messages"/>, the script's
    /// text archive.</summary>
    public static string? FindMismatch(ScriptLanguage language, string lua, string scriptText, IReadOnlyList<string>? messages, string? global,
        ScriptMessageUse? use = null)
    {
        try
        {
            SaveResult compiled = CompileForSave(language, lua, global, messages, use ?? ScriptMessageUse.Unknown);
            return ScriptEquivalence.FindDifference(ScriptTextDocument.Parse(scriptText, language), ScriptTextDocument.Parse(compiled.ScriptText, language),
                language, messages, compiled.Messages);
        }
        catch (Exception ex) when (ex is LuaException or ScriptTextException or InvalidDataException)
        {
            return ex.Message;
        }
    }

    /// <summary>Writes global.lua's variable and flag names into Lua written before they existed:
    /// <c>var[0x4000]</c> becomes <c>STARTER</c>, <c>flag[372]</c> becomes <c>MET_RIVAL</c>.</summary>
    public static string UseNames(string lua, ScriptLanguage language, string? global)
    {
        LuaNames names = LuaScriptCompiler.FindNames(global, language);
        string Replace(string text, string table, IReadOnlyDictionary<long, string> named) =>
            System.Text.RegularExpressions.Regex.Replace(text, $@"(?<![\w.]){table}\[\s*(0[xX][0-9A-Fa-f]+|\d+)\s*\]", match =>
            {
                string token = match.Groups[1].Value;
                long id = token.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? Convert.ToInt64(token[2..], 16) : long.Parse(token);
                return named.TryGetValue(id, out string? name) ? name : match.Value;
            });
        return Replace(Replace(lua, "var", names.Variables), "flag", names.Flags);
    }

    /// <summary>The script file names global.lua gives (see <see cref="LuaScriptCompiler.FindScriptNames"/>).</summary>
    public static Dictionary<int, string> ReadScriptNames(RomProject project, ScriptLanguage language) =>
        File.Exists(GlobalPath(project)) ? LuaScriptCompiler.FindScriptNames(File.ReadAllText(GlobalPath(project)), language) : [];

    private static readonly System.Text.RegularExpressions.Regex NamedScript = new(
        @"^\s*script\s*\(\s*(\d+)\s*,\s*""((?:[^""\\]|\\.)*)""", System.Text.RegularExpressions.RegexOptions.Multiline);

    /// <summary>The names a Lua script file gives its numbered scripts: <c>script(3, "Rival intro", function()</c>.
    /// Scripts written without one, or converted from a plaintext script, have none.</summary>
    public static Dictionary<int, string> ReadSubscriptNames(RomProject project, int id)
    {
        var names = new Dictionary<int, string>();
        string path = GetPath(project, id);
        if (!File.Exists(path)) return names;
        foreach (System.Text.RegularExpressions.Match match in NamedScript.Matches(File.ReadAllText(path)))
        {
            if (int.TryParse(match.Groups[1].Value, out int number)) names.TryAdd(number, match.Groups[2].Value.Replace("\\\"", "\""));
        }
        return names;
    }

    public static string GlobalPath(RomProject project) => Path.Combine(project.ExpandedScriptsDirectory, "global.lua");

    public static string? LoadGlobal(RomProject project) => File.Exists(GlobalPath(project)) ? File.ReadAllText(GlobalPath(project)) : null;

    public static void SaveGlobal(RomProject project, string content) =>
        ScriptFileService.SaveJournaled(project, GlobalPath(project), "EditGlobalLua", [], "Saved global.lua", content);

    public static void ApplyGlobalEdit(RomProject project, int line, int remove, IReadOnlyList<string> insert) =>
        ScriptFileService.ApplyHunk(GlobalPath(project), "global.lua", line, remove, insert, content => SaveGlobal(project, content));

    /// <summary>The global.lua a project gets when its first script file is converted: templates for the
    /// scripts that repeat most across DPPt and HGSS (an NPC or object saying one message, a sign).</summary>
    public const string DefaultGlobal = """
        -- global.lua: helpers every Lua script file in this project can use.
        -- Functions here are copied into each place they are called. One that returns a script body is a
        -- template: converting a script file to Lua writes any script it would make as a call to it,
        -- for example script(18, npc_message("Hello?")). Add your own the same way.
        -- Messages are written as their text, Message("Hi!"), GenderMessage("Hi, sir!", "Hi, ma'am!");
        -- saving numbers them and rebuilds the script's text archive.

        -- Names for variables and flags, usable in every Lua script file. Converting a script file writes
        -- these names in place of the numbers; Use Names on the Lua tab applies them to a converted file.
        --   STARTER = var[0x4000]      if STARTER == 1 then ... end      STARTER = 2      STARTER = STARTER + 1
        --   MET_RIVAL = flag[372]      if MET_RIVAL then ... end         MET_RIVAL = true
        RESULT = var[0x800C]            -- where commands such as YesNoBox and WaitTime leave their result

        -- Someone the player talks to: turns to face the player and says something.
        -- npc_message("Hello!"), or several messages in turn: npc_message({"Hello!", "Nice day."})
        function npc_message(text)
        	return function()
        		PlayFanfare(SEQ_SE_CONFIRM)
        		LockAll()
        		FacePlayer()
        		Message(text)
        		WaitButton()
        		CloseMessage()
        		ReleaseAll()
        	end
        end

        -- Something the player checks, such as a bin or a TV.
        function object_message(text)
        	return function()
        		PlayFanfare(SEQ_SE_CONFIRM)
        		LockAll()
        		Message(text)
        		WaitButton()
        		CloseMessage()
        		ReleaseAll()
        	end
        end

        -- Says one of two things depending on the player's gender: gender_message("Hi, sir!", "Hi, ma'am!").
        -- HGSS has a command for this, GenderMessage; DP and Platinum check the player's gender first.
        function gender_message(male_text, female_text)
        	if has_command("GenderMessage") then
        		GenderMessage(male_text, female_text)
        	else
        		CheckPlayerGender(var[0x800C])
        		if var[0x800C] == 0 then
        			Message(male_text)
        		else
        			Message(female_text)
        		end
        	end
        end

        -- Someone the player talks to who says one of two things depending on the player's gender.
        function npc_gender_message(male_text, female_text)
        	return function()
        		PlayFanfare(SEQ_SE_CONFIRM)
        		LockAll()
        		FacePlayer()
        		gender_message(male_text, female_text)
        		WaitButton()
        		CloseMessage()
        		ReleaseAll()
        	end
        end

        -- A sign showing a text on a board (kind 0 brown, 1 green, 2 gray, 3 blue; DPPt).
        function sign(text, kind)
        	return function()
        		SetIconBoard(text, kind, 0, 0x800C)
        		ShowBoard(3)
        		WaitBoard()
        		CloseBoard(0x800C)
        		CommonScript(2000)
        	end
        end

        """;
}
