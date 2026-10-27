using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>Headless <c>--replay-journal</c> mode: regenerates an edited ROM from a base ROM and an edit
/// journal (extract → prepare → replay → build), without starting the UI.</summary>
internal static class JournalReplayCommand
{
    private const string Usage =
        "Usage: DSPRE.Avalonia --replay-journal <edit-journal.dspre> --base-rom <base.nds> --output <edited.nds> [--work-dir <dir>] [--force]\n" +
        "  --work-dir  where the base ROM is extracted (default: <output name>_DSPRE_contents next to the output); must not exist yet\n" +
        "  --force     replay even if the base ROM's SHA-1 differs from the one the journal was recorded against";

    public static async Task<int> RunAsync(string[] args)
    {
        string? journalPath = null, baseRomPath = null, outputPath = null, workDirectory = null;
        bool force = false;
        for (int i = 0; i < args.Length; i++)
        {
            string? Next() => i + 1 < args.Length ? args[++i] : null;
            switch (args[i])
            {
                case "--replay-journal": journalPath = Next(); break;
                case "--base-rom": baseRomPath = Next(); break;
                case "--output": outputPath = Next(); break;
                case "--work-dir": workDirectory = Next(); break;
                case "--force": force = true; break;
                default:
                    Console.Error.WriteLine($"Unknown argument '{args[i]}'.\n{Usage}");
                    return 2;
            }
        }

        if (journalPath is null || baseRomPath is null || outputPath is null)
        {
            Console.Error.WriteLine(Usage);
            return 2;
        }

        journalPath = Path.GetFullPath(journalPath);
        baseRomPath = Path.GetFullPath(baseRomPath);
        outputPath = Path.GetFullPath(outputPath);
        workDirectory = Path.GetFullPath(workDirectory ?? Path.Combine(
            Path.GetDirectoryName(outputPath)!, Path.GetFileNameWithoutExtension(outputPath) + "_DSPRE_contents"));
        string toolsDirectory = Path.Combine(AppContext.BaseDirectory, "Tools");

        if (!File.Exists(journalPath) || !File.Exists(baseRomPath))
        {
            Console.Error.WriteLine($"Journal or base ROM not found.\n{Usage}");
            return 2;
        }
        if (Directory.Exists(workDirectory) && Directory.EnumerateFileSystemEntries(workDirectory).Any())
        {
            Console.Error.WriteLine($"Work directory '{workDirectory}' already exists; replay needs a fresh extraction. Delete it or pass --work-dir.");
            return 2;
        }

        if (!CheckBaseRom(journalPath, baseRomPath, force)) return 3;

        try
        {
            Console.WriteLine($"Extracting {baseRomPath} to {workDirectory}...");
            ToolResult extract = await new RomExtractionService().ExtractAsync(baseRomPath, workDirectory, RomProjectFormat.DsRom, toolsDirectory);
            if (!extract.Succeeded) return Fail("ROM extraction", extract);
            if (!RomProject.TryOpen(workDirectory, out RomProject? project) || project is null)
            {
                Console.Error.WriteLine("Extraction produced an unrecognized project.");
                return 1;
            }

            ProjectPreparationResult prepared = await RomProjectPipeline.PrepareAsync(project, toolsDirectory, Console.WriteLine);
            if (!prepared.Arm9.Succeeded) return Fail("ARM9 preparation", prepared.Arm9);
            if (prepared.TextArchives is { Succeeded: false } text) return Fail("Text archive preparation", text);
            if (prepared.ScriptExport is { Succeeded: false } scripts) return Fail("Script export", scripts);
            RomGameMetadata metadata = prepared.Metadata!;
            RomProjectPipeline.PrepareEditorData(project, metadata);

            // The replay is journaled into the new project too, so the regenerated journal can be diffed
            // against the original (ignoring '#' comment lines) to confirm a faithful replay.
            // Kept inside the regenerated project: next to the base ROM is where the journal being
            // replayed may well live.
            EditJournal.Open(project, metadata, baseRomPath, Path.Combine(project.RootPath, EditJournal.FileName));
            int applied = EditJournalReplayer.Replay(project, metadata, journalPath, Console.WriteLine);
            Console.WriteLine($"Replayed {applied} journal call(s).");

            ToolResult build = await RomProjectPipeline.BuildRomAsync(project, metadata, outputPath, toolsDirectory, Console.WriteLine);
            if (!build.Succeeded) return Fail("ROM build", build);
            Console.WriteLine($"Wrote {outputPath}");
            return 0;
        }
        catch (EditJournalReplayer.ReplayException ex)
        {
            Console.Error.WriteLine(ex.Message);
            return 1;
        }
        finally
        {
            EditJournal.Close();
        }
    }

    private static bool CheckBaseRom(string journalPath, string baseRomPath, bool force)
    {
        JournalCall? baseRom = EditJournalFormat.Parse(journalPath).FirstOrDefault(call => call.Function == "BaseRom");
        string? expectedSha1 = baseRom?.Get("sha1")?.GetValue<string>();
        if (expectedSha1 is null) return true;

        string actualSha1 = EditJournal.ComputeSha1(baseRomPath);
        if (string.Equals(expectedSha1, actualSha1, StringComparison.OrdinalIgnoreCase)) return true;

        Console.Error.WriteLine($"Base ROM SHA-1 {actualSha1} does not match the journal's base ROM ({expectedSha1}).");
        if (force)
        {
            Console.Error.WriteLine("--force given; replaying anyway.");
            return true;
        }
        Console.Error.WriteLine("Pass --force to replay anyway.");
        return false;
    }

    private static int Fail(string step, ToolResult result)
    {
        Console.Error.WriteLine($"{step} failed with exit code {result.ExitCode}.");
        if (!string.IsNullOrWhiteSpace(result.StandardOutput)) Console.Error.WriteLine(result.StandardOutput.Trim());
        if (!string.IsNullOrWhiteSpace(result.StandardError)) Console.Error.WriteLine(result.StandardError.Trim());
        return 1;
    }
}
