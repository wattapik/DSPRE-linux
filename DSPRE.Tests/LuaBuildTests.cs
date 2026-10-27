using DSPRE.Core;

namespace DSPRE.Tests;

[Collection("Platinum")]
public sealed class LuaBuildTests(PlatinumFixture fixture)
{
    private static string? FindDatabases()
    {
        for (DirectoryInfo? directory = new(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "DSPRE.Avalonia", "bin", "Debug", "net8.0", "databases");
            if (Directory.Exists(path)) return path;
        }

        return null;
    }

    [Fact]
    public void ErrorsAreListedPerFileWithTheirLineAndGoodFilesCompile()
    {
        string? databases = FindDatabases();
        if (databases is null) return;
        TestProject t = fixture.CreateProject();
        Directory.CreateDirectory(t.Project.ExpandedScriptsDirectory);
        File.WriteAllText(Path.Combine(t.Project.ExpandedScriptsDirectory, "0001.lua"), "script(1, function()\n\tLockAll()\n\tReleaseAll()\nend)\n");
        File.WriteAllText(Path.Combine(t.Project.ExpandedScriptsDirectory, "0002.lua"), "script(1, function()\n\tLockAll(\nend)\n");

        LuaBuildService.Result check = LuaBuildService.Compile(t.Project, t.Metadata, databases, write: false);

        Assert.Equal(2, check.Files);
        LuaBuildService.Diagnostic error = Assert.Single(check.Errors);
        Assert.Equal("0002.lua", error.File);
        Assert.True(error.Line > 0);

        LuaBuildService.Result build = LuaBuildService.Compile(t.Project, t.Metadata, databases, write: true);
        Assert.Single(build.Errors);
        Assert.Contains("LockAll", File.ReadAllText(ScriptFileService.GetPath(t.Project, 1)));
    }

    [Fact]
    public void DecompilingWritesLuaForEachScriptAndSkipsFilesThatAlreadyHaveOne()
    {
        string? databases = FindDatabases();
        if (databases is null) return;
        TestProject t = fixture.CreateProject();
        Directory.CreateDirectory(t.Project.ExpandedScriptsDirectory);
        const string script = "Script 1:\n\tLockAll\n\tReleaseAll\nEnd\n";
        File.WriteAllText(ScriptFileService.GetPath(t.Project, 1), script);
        File.WriteAllText(ScriptFileService.GetPath(t.Project, 2), script);
        File.WriteAllText(LuaScriptService.GetPath(t.Project, 2), "-- mine\n");

        LuaLiftService.Result dry = LuaLiftService.LiftAll(t.Project, t.Metadata, databases, overwrite: false, only: null, write: false);
        Assert.Equal([1], dry.Converted);
        Assert.Equal([2], dry.Skipped);
        Assert.False(LuaScriptService.Exists(t.Project, 1));

        LuaLiftService.Result result = LuaLiftService.LiftAll(t.Project, t.Metadata, databases, overwrite: false, only: null, write: true);

        Assert.Empty(result.Failures);
        Assert.True(result.CreatedGlobal);
        Assert.Contains("LockAll()", File.ReadAllText(LuaScriptService.GetPath(t.Project, 1)));
        Assert.Equal("-- mine\n", File.ReadAllText(LuaScriptService.GetPath(t.Project, 2)));
        Assert.Equal(script, File.ReadAllText(ScriptFileService.GetPath(t.Project, 1)));
        Assert.Contains("npc_message", File.ReadAllText(LuaScriptService.GlobalPath(t.Project)));
    }
}
