using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>Headless <c>build</c> and <c>check</c> commands: <c>check</c> compiles the project's Lua scripts and prints their errors;
/// <c>build</c> does that, then builds the ROM the way Save ROM does (the ROM the project was extracted from is overwritten unless
/// <c>-o</c> names another file). Edits are journaled like the editor's.</summary>
internal static class BuildCommand
{
    private const string Usage =
        "Usage: DSPRE.Avalonia build -p <project_dir> [-o <out.nds>]\n" +
        "       DSPRE.Avalonia check -p <project_dir>\n" +
        "       DSPRE.Avalonia decompile -p <project_dir> [--only 1,5-9] [--force] [--dry-run]\n" +
        "  build  compile the Lua scripts (errors are listed and stop the build), then build the ROM\n" +
        "  check  only compile the Lua scripts and list errors; nothing is written\n" +
        "  decompile  convert the project's .script files to Lua (NNNN.lua), each checked to behave the same; .script files are untouched\n" +
        "  --only a list of script file numbers/ranges     --force replace .lua files that exist     --dry-run only report what converts\n" +
        "  -p     the project's contents folder (the one the editor extracted the ROM to)\n" +
        "  -o     where to write the ROM (default: the ROM the project was extracted from)";

    public static bool Handles(string[] args) => args.Length > 0 && args[0] is "build" or "check" or "decompile";

    public static async Task<int> RunAsync(string[] args)
    {
        bool build = args[0] == "build", decompile = args[0] == "decompile", force = false, dryRun = false;
        string? projectPath = null, output = null, only = null;
        for (int i = 1; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "-p" or "--project": projectPath = Next(); break;
                case "-o" or "--output": output = Next(); break;
                case "--only": only = Next(); break;
                case "--force": force = true; break;
                case "--dry-run": dryRun = true; break;
                default:
                    Console.Error.WriteLine($"Unknown argument '{args[i]}'.\n{Usage}");
                    return 2;
            }
        }

        if (projectPath is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        projectPath = Path.GetFullPath(projectPath);
        if (!RomProject.TryOpen(projectPath, out RomProject? project) || project is null || !project.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            Console.Error.WriteLine($"'{projectPath}' is not a DSPRE project folder.");
            return 2;
        }

        if (!Directory.Exists(project.ExpandedScriptsDirectory))
        {
            Console.Error.WriteLine("The project has not been opened in DSPRE yet (no expanded/scripts folder); open it once first.");
            return 2;
        }

        if (decompile) return Decompile(project, metadata, only, force, dryRun);

        string toolsDirectory = Path.Combine(AppContext.BaseDirectory, "Tools");
        RomSourceStamp? stamp = RomSourceStamp.Read(projectPath);
        try
        {
            if (build) EditJournal.Open(project, metadata, stamp is { } source && File.Exists(source.RomPath) ? source.RomPath : null);

            LuaBuildService.Result lua = LuaBuildService.Compile(project, metadata, Path.Combine(AppContext.BaseDirectory, "databases"), write: build);
            foreach (LuaBuildService.Diagnostic warning in lua.Warnings) Console.Error.WriteLine($"warning: {warning}");
            foreach (LuaBuildService.Diagnostic error in lua.Errors) Console.Error.WriteLine($"error: {error}");
            if (lua.Errors.Count > 0)
            {
                Console.Error.WriteLine($"{lua.Errors.Count} error(s) in {lua.Files} Lua script file(s).{(build ? " The ROM was not built." : "")}");
                return 1;
            }

            Console.WriteLine($"{lua.Files} Lua script file(s) compile.");
            if (!build) return 0;

            output = Path.GetFullPath(output ?? stamp?.RomPath ?? DefaultOutput(projectPath));
            ToolResult result = await RomProjectPipeline.BuildRomAsync(project, metadata, output, toolsDirectory, Console.WriteLine);
            if (!result.Succeeded)
            {
                Console.Error.WriteLine($"ROM build failed with exit code {result.ExitCode}.");
                if (!string.IsNullOrWhiteSpace(result.StandardOutput)) Console.Error.WriteLine(result.StandardOutput.Trim());
                if (!string.IsNullOrWhiteSpace(result.StandardError)) Console.Error.WriteLine(result.StandardError.Trim());
                return 1;
            }

            Console.WriteLine($"Wrote {output}");
            return 0;
        }
        finally
        {
            EditJournal.Close();
        }
    }

    private static int Decompile(RomProject project, RomGameMetadata metadata, string? only, bool force, bool dryRun)
    {
        HashSet<int>? ids = null;
        if (only is not null)
        {
            ids = [];
            foreach (string part in only.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                string[] range = part.Split('-', 2);
                if (!int.TryParse(range[0], out int from) || !int.TryParse(range[^1], out int to) || to < from)
                {
                    Console.Error.WriteLine($"'{part}' is not a script file number or range.");
                    return 2;
                }
                for (int id = from; id <= to; id++) ids.Add(id);
            }
        }

        RomSourceStamp? stamp = RomSourceStamp.Read(project.RootPath);
        try
        {
            if (!dryRun) EditJournal.Open(project, metadata, stamp is { } source && File.Exists(source.RomPath) ? source.RomPath : null);
            int shown = 0;
            LuaLiftService.Result result = LuaLiftService.LiftAll(project, metadata, Path.Combine(AppContext.BaseDirectory, "databases"), force, ids, write: !dryRun,
                _ => { if (++shown % 100 == 0) Console.Error.WriteLine($"{shown} script files done..."); });
            foreach (LuaLiftService.Failure failure in result.Failures) Console.Error.WriteLine($"error: {failure.Script:D4}.script: {failure.Message}");
            if (result.CreatedGlobal) Console.WriteLine(dryRun ? "Would create global.lua with the default templates." : "Created global.lua with the default templates.");
            Console.WriteLine($"{(dryRun ? "Would convert" : "Converted")} {result.Converted.Count} script file(s) to Lua; {result.Skipped.Count} already had a .lua{(result.Skipped.Count > 0 ? " (--force replaces them)" : "")}; {result.Failures.Count} could not be converted.");
            return result.Failures.Count > 0 ? 1 : 0;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            EditJournal.Close();
        }
    }

    private static string DefaultOutput(string projectPath)
    {
        string name = Path.GetFileName(projectPath.TrimEnd(Path.DirectorySeparatorChar));
        const string suffix = "_DSPRE_contents";
        if (name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) name = name[..^suffix.Length];
        return Path.Combine(Directory.GetParent(projectPath)?.FullName ?? Environment.CurrentDirectory, name + ".nds");
    }
}
