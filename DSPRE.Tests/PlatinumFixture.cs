using DSPRE.Core;

namespace DSPRE.Tests;

/// <summary>Extracts test/pokeplat.nds once per test run and hands every test its own copy of the prepared project,
/// so edits never leak between tests or into the repo (the copy has no ROM source stamp, so its journal stays inside it).</summary>
public sealed class PlatinumFixture : IDisposable
{
    private readonly string workRoot = Path.Combine(Path.GetTempPath(), "dspre-tests-" + Guid.NewGuid().ToString("N"));
    private readonly string pristine;
    private int copies;

    public PlatinumFixture()
    {
        string repo = FindRepoRoot();
        RomPath = Path.Combine(repo, "test", "pokeplat.nds");
        ToolsDirectory = Path.Combine(repo, "DS_Map", "Tools");
        if (!File.Exists(RomPath)) throw new FileNotFoundException("The Platinum test ROM is missing.", RomPath);

        pristine = Path.Combine(workRoot, "pristine");
        Directory.CreateDirectory(workRoot);
        var service = new RomExtractionService();
        ToolResult extract = service.ExtractAsync(RomPath, pristine, RomProjectFormat.DsRom, ToolsDirectory).GetAwaiter().GetResult();
        if (!extract.Succeeded) throw new InvalidOperationException("Extraction failed: " + extract.StandardError);

        if (!RomProject.TryOpen(pristine, out RomProject? project) || project is null || !project.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
            throw new InvalidOperationException("The extracted ROM is not a recognised project.");

        service.PrepareArm9Async(project, ToolsDirectory).GetAwaiter().GetResult();
        service.PrepareDynamicHeaders(project, metadata);
        service.PrepareTextArchivesAsync(project, metadata, ToolsDirectory).GetAwaiter().GetResult();
        service.PrepareMatrices(project, metadata);
        service.PrepareMaps(project, metadata);
        service.PrepareTilesets(project, metadata);
        service.PrepareEncounters(project, metadata);
        service.PrepareTrainers(project, metadata);
        service.PrepareScripts(project, metadata);
        RomProjectPipeline.PrepareEditorData(project, metadata);
        service.PrepareEggMoves(project, metadata);
    }

    public string RomPath { get; }
    public string ToolsDirectory { get; }

    public TestProject CreateProject()
    {
        string target = Path.Combine(workRoot, "copy" + Interlocked.Increment(ref copies));
        CopyDirectory(pristine, target);
        RomProject.TryOpen(target, out RomProject? project);
        project!.TryGetGameMetadata(out RomGameMetadata? metadata);
        return new TestProject(project, metadata!, ToolsDirectory);
    }

    public void Dispose()
    {
        EditJournal.Close();
        try { Directory.Delete(workRoot, recursive: true); } catch (IOException) { }
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (string file in Directory.GetFiles(source)) File.Copy(file, Path.Combine(target, Path.GetFileName(file)));
        foreach (string directory in Directory.GetDirectories(source)) CopyDirectory(directory, Path.Combine(target, Path.GetFileName(directory)));
    }

    private static string FindRepoRoot()
    {
        for (string? directory = AppContext.BaseDirectory; directory is not null; directory = Path.GetDirectoryName(directory))
        {
            if (File.Exists(Path.Combine(directory, "DS_Map.sln"))) return directory;
        }
        throw new DirectoryNotFoundException("Could not find the repository root above " + AppContext.BaseDirectory);
    }
}

public sealed record TestProject(RomProject Project, RomGameMetadata Metadata, string ToolsDirectory);

[CollectionDefinition("Platinum")]
public sealed class PlatinumCollection : ICollectionFixture<PlatinumFixture>;
