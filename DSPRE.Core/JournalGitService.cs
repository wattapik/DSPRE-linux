using System.Globalization;

namespace DSPRE.Core;

/// <summary>Keeps the edit journal under git: a commit of the journal file whenever it has changed after a save. The
/// repository sits beside the journal as "&lt;journal&gt;.git" with the journal's folder as its work tree, so
/// nothing is created inside the ROM's own folder and an existing repository around it is left alone, and
/// only the journal is ever added. It is plain git: a git client opened on that folder shows the same history.
/// Commits are not named or managed here.</summary>
public static class JournalGitService
{
    private static readonly SemaphoreSlim Gate = new(1, 1);
    private const char FieldSeparator = '\u001f';

    public sealed record Commit(string Id, DateTimeOffset Time);

    public static string RepositoryPath(string journalPath) => journalPath + ".git";

    private static string[] Arguments(string journalPath, params string[] command) =>
    [
        "-c", "core.quotepath=false", "-c", "user.name=DSPRE", "-c", "user.email=dspre@localhost", "-c", "commit.gpgsign=false",
        $"--git-dir={RepositoryPath(journalPath)}", $"--work-tree={Path.GetDirectoryName(Path.GetFullPath(journalPath))}", ..command
    ];

    private static Task<ToolResult> RunAsync(string journalPath, params string[] command) =>
        new ProcessToolRunner().RunAsync("git", Arguments(journalPath, command), Path.GetDirectoryName(Path.GetFullPath(journalPath)));

    /// <summary>Whether git can be run at all.</summary>
    public static async Task<bool> IsAvailableAsync()
    {
        try
        {
            return (await new ProcessToolRunner().RunAsync("git", ["--version"])).Succeeded;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or IOException or InvalidOperationException)
        {
            return false;
        }
    }

    /// <summary>Commits the journal if it differs from the last commit (or is not in the repository yet), starting
    /// the repository first when there is none. Returns whether a commit was made.</summary>
    public static async Task<bool> CommitAsync(string journalPath)
    {
        if (!File.Exists(journalPath)) return false;
        string file = Path.GetFileName(journalPath);
        await Gate.WaitAsync();
        try
        {
            if (!Directory.Exists(RepositoryPath(journalPath)))
            {
                ToolResult init = await RunAsync(journalPath, "init", "--quiet");
                if (!init.Succeeded) throw new IOException($"git could not start a repository: {init.StandardError.Trim()}");
            }

            ToolResult status = await RunAsync(journalPath, "status", "--porcelain", "--", file);
            if (!status.Succeeded) throw new IOException($"git status failed: {status.StandardError.Trim()}");
            if (string.IsNullOrWhiteSpace(status.StandardOutput)) return false;

            ToolResult add = await RunAsync(journalPath, "add", "--", file);
            if (!add.Succeeded) throw new IOException($"git add failed: {add.StandardError.Trim()}");
            ToolResult commit = await RunAsync(journalPath, "commit", "--quiet", "-m", "Save", "--", file);
            if (!commit.Succeeded) throw new IOException($"git commit failed: {(commit.StandardError + commit.StandardOutput).Trim()}");
            return true;
        }
        finally
        {
            Gate.Release();
        }
    }

    /// <summary>The commits of the journal, newest first.</summary>
    public static async Task<List<Commit>> LogAsync(string journalPath, int limit = 300)
    {
        if (!Directory.Exists(RepositoryPath(journalPath))) return [];
        ToolResult log = await RunAsync(journalPath, "log", $"--max-count={limit}", $"--format=%H{FieldSeparator}%aI");
        if (!log.Succeeded) return [];
        var commits = new List<Commit>();
        foreach (string line in log.StandardOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string[] fields = line.Trim().Split(FieldSeparator);
            if (fields.Length == 2 && DateTimeOffset.TryParse(fields[1], CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTimeOffset time)) commits.Add(new Commit(fields[0], time));
        }

        return commits;
    }

    /// <summary>What a commit changed in the journal: the lines it added and removed, as the journal's own lines
    /// prefixed with + or -.</summary>
    public static async Task<List<string>> ChangesAsync(string journalPath, string commitId)
    {
        ToolResult show = await RunAsync(journalPath, "show", "--format=", "-U0", "--no-color", commitId, "--", Path.GetFileName(journalPath));
        if (!show.Succeeded) return [];
        return show.StandardOutput.Split('\n').Select(line => line.TrimEnd('\r'))
            .Where(line => (line.StartsWith('+') && !line.StartsWith("+++")) || (line.StartsWith('-') && !line.StartsWith("---")))
            .ToList();
    }
}
