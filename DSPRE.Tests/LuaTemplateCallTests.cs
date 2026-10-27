using DSPRE.Core;

namespace DSPRE.Tests;

public class LuaTemplateCallTests
{
    private const string Global = """
        function npc_message(text)
            return function()
                LockAll()
                FacePlayer()
                Message(text)
                WaitButton()
                CloseMessage()
                ReleaseAll()
            end
        end
        """;

    private static ScriptLanguage? FindLanguage()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "DSPRE.Avalonia", "bin", "Debug", "net8.0", "databases", "platinum_scrcmd_database.json");
            if (File.Exists(path)) return new ScriptLanguage(ScriptCommandDatabaseService.Load(path));
        }

        return null;
    }

    [Fact]
    public void ATemplateHelperCalledInsideABodyRunsItsBodyThere()
    {
        ScriptLanguage? language = FindLanguage();
        if (language is null) return;

        string asArgument = LuaScriptService.Compile(language, "script(1, npc_message(\"Hello?\"))", Global).ScriptText;
        string asStatement = LuaScriptService.Compile(language, "script(1, function()\n\tnpc_message(\"Hello?\")\nend)", Global).ScriptText;

        Assert.Contains("Message", asStatement);
        Assert.Equal(Body(asArgument), Body(asStatement));
    }

    [Fact]
    public void ATableOfTextsGivesSeveralMessagesInTurn()
    {
        ScriptLanguage? language = FindLanguage();
        if (language is null) return;

        string script = LuaScriptService.Compile(language, "script(1, npc_message({\"Hello?\", \"Hello, again?\"}))", Global).ScriptText;

        Assert.Contains("Hello?", script);
        Assert.Contains("Hello, again?", script);
        Assert.True(script.IndexOf("Hello?", StringComparison.Ordinal) < script.IndexOf("Hello, again?", StringComparison.Ordinal));
    }

    private static string Body(string script) => script[script.IndexOf("//===== SCRIPTS", StringComparison.Ordinal)..];
}
