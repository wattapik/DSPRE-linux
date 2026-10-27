using DSPRE.Core;
using DSPRE.LuaLanguageServer;

namespace DSPRE.Tests;

public class LuaEditorSupportTests
{
    private const string Global = """
        STARTER = var[0x4000]
        MET_RIVAL = flag[372]
        function npc_message(n, text)
            return function() end
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
    public void GlobalSymbolsListHelpersAndNamedVariablesAndFlagsWithTheirLines()
    {
        IReadOnlyList<LuaGlobalSymbol> symbols = LuaGlobalSymbols.Find(Global);

        Assert.Contains(symbols, s => s is { Name: "STARTER", Kind: LuaGlobalKind.Variable, Number: 0x4000, Line: 1 });
        Assert.Contains(symbols, s => s is { Name: "MET_RIVAL", Kind: LuaGlobalKind.Flag, Number: 372 });
        LuaGlobalSymbol helper = Assert.Single(symbols, s => s.Kind == LuaGlobalKind.Function);
        Assert.Equal(["n", "text"], helper.Parameters);
        Assert.Equal(3, helper.Line);
    }

    [Fact]
    public void GlobalSymbolsStillComeFromAGlobalLuaThatDoesNotParse()
    {
        IReadOnlyList<LuaGlobalSymbol> symbols = LuaGlobalSymbols.Find("function broken(a, b)\n\tif then\nSOMETHING = 1\n");

        Assert.Contains(symbols, s => s.Name == "broken" && s.Parameters.Count == 2);
    }

    [Fact]
    public void TheStubDeclaresCommandsAliasesAndGlobalNames()
    {
        ScriptLanguage? language = FindLanguage();
        if (language is null) return;

        string stub = LuaStubGenerator.Generate(language, Global);

        Assert.StartsWith("---@meta", stub);
        Assert.Contains("function LockAll()", stub);
        Assert.Contains("function lock_all()", stub);
        Assert.Contains("function npc_message(n, text)", stub);
        Assert.Contains("---@type integer\nSTARTER = 0", stub);
        Assert.Contains("---@type boolean\nMET_RIVAL = false", stub);
        Assert.DoesNotContain("nan", stub.Split('\n').Where(line => line.StartsWith("---") && !line.StartsWith("---@")));
    }

    [Fact]
    public void FormattingIndentsByBlockAndLeavesEverythingElseAlone()
    {
        string source = "script(1, function()\nLockAll()\nif var[1] == 1 then\nMessage(\"a\")\nelseif flag[2] then\nWaitButton()\nelse\nEnd()\nend\nMovement(Player, {\nFaceNorth,\nWalkSouth8(2),\n})\nend)\n\n\n\nscript(2, 1)  \n";

        string formatted = LuaFormatter.Format(source, "\t");

        Assert.Equal(
            "script(1, function()\n\tLockAll()\n\tif var[1] == 1 then\n\t\tMessage(\"a\")\n\telseif flag[2] then\n\t\tWaitButton()\n\telse\n\t\tEnd()\n\tend\n\tMovement(Player, {\n\t\tFaceNorth,\n\t\tWalkSouth8(2),\n\t})\nend)\n\nscript(2, 1)\n",
            formatted);
        Assert.Equal(formatted, LuaFormatter.Format(formatted, "\t"));
    }

    [Fact]
    public void FormattingKeepsTheInsideOfLongStringsAndLoopsIndentOnce()
    {
        string source = "for i = 1, 3 do\nasm [[\n   raw  \n]]\nwhile true do\nbreak\nend\nend\n";

        Assert.Equal("for i = 1, 3 do\n\tasm [[\n   raw  \n]]\n\twhile true do\n\t\tbreak\n\tend\nend\n", LuaFormatter.Format(source, "\t"));
    }

    [Fact]
    public void TheTokenizerKeepsPositionsAndSurvivesUnfinishedText()
    {
        List<Token> tokens = LuaTokens.Tokenize("local x = \"abc\n-- note\ny = [[a\nb]] 0x10");

        Assert.Equal(["local", "x", "=", "\"abc", "-- note", "y", "=", "[[a\nb]]", "0x10"], tokens.Select(t => t.Text));
        Assert.Equal(2, tokens[7].Line);
        Assert.Equal(3, tokens[7].EndLine);
        Assert.Equal(TokenKind.Number, tokens[^1].Kind);
    }

    [Fact]
    public void CompletionOffersLocalsAndKeywordsAndNothingInsideStringsOrAfterADot()
    {
        string text = "local walk = 1\nwa";
        List<Token> tokens = LuaTokens.Tokenize(text);

        Assert.Contains(LuaFeatures.Complete(text, tokens, 1, 2, null), item => item!["label"]!.GetValue<string>() == "walk");
        Assert.Contains(LuaFeatures.Complete(text, tokens, 1, 2, null), item => item!["label"]!.GetValue<string>() == "while");

        string quoted = "Message(\"hello";
        Assert.Empty(LuaFeatures.Complete(quoted, LuaTokens.Tokenize(quoted), 0, 12, null));

        string dotted = "a.";
        Assert.Empty(LuaFeatures.Complete(dotted, LuaTokens.Tokenize(dotted), 0, 2, null));
    }

    private static List<(string File, List<Token> Tokens)> Files(params (string Name, string Text)[] files) =>
        files.Select(file => (file.Name, LuaTokens.Tokenize(file.Text))).ToList();

    [Fact]
    public void ReferencesOfALocalStayInItsFileAndThoseOfAGlobalNameSpanFilesThatDoNotShadowIt()
    {
        var files = Files(
            ("global.lua", "RESULT = var[0x800C]\n"),
            ("0001.lua", "local n = 1\nRESULT = RESULT + n\nt.RESULT = 1\n"),
            ("0002.lua", "local RESULT = 5\nprint(RESULT)\n"),
            ("0003.lua", "script(1, function() YesNoBox(RESULT) end)\n"));
        var project = (ProjectContext?)null;

        var local = LuaRefactor.References(new NameTarget("n", NameTargetKind.Local), "0001.lua", files, project);
        var global = LuaRefactor.References(new NameTarget("RESULT", NameTargetKind.Global), "0001.lua", files, project);

        Assert.Equal(2, local.Count);
        Assert.Equal(["global.lua", "0001.lua", "0001.lua", "0003.lua"], global.Select(r => r.File));
    }

    [Fact]
    public void RenamingRefusesBuiltInsInvalidNamesAndNamesAlreadyInUse()
    {
        var tokens = new List<List<Token>> { LuaTokens.Tokenize("local clash = 1") };

        Assert.NotNull(LuaRefactor.RenameProblem(new NameTarget("LockAll", NameTargetKind.Builtin), "x", null, tokens));
        Assert.NotNull(LuaRefactor.RenameProblem(new NameTarget("A", NameTargetKind.Global), "1bad", null, tokens));
        Assert.NotNull(LuaRefactor.RenameProblem(new NameTarget("A", NameTargetKind.Global), "clash", null, tokens));
        Assert.Null(LuaRefactor.RenameProblem(new NameTarget("A", NameTargetKind.Global), "fine", null, tokens));
    }

    [Fact]
    public void SemanticTokensMarkLocalsDeclarationsAndScriptLanguageConstants()
    {
        var data = LuaRefactor.SemanticTokens(LuaTokens.Tokenize("local walk = 1\nscript(1, ITEM_POTION)\nwalk = 2"), null)!["data"]!.AsArray();

        int[] values = data.Select(n => n!.GetValue<int>()).ToArray();
        Assert.Equal([0, 6, 4, 1, 1], values[..5]);
        Assert.Equal([1, 0, 6, 0, 2], values[5..10]);
        Assert.Equal([0, 10, 11, 3, 2], values[10..15]);
        Assert.Equal([1, 0, 4, 1, 0], values[15..20]);
    }
}
