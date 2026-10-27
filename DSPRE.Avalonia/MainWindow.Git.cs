using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Threading;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Git pane, and the commits behind it: the edit journal is committed to a git repository kept beside it
/// (see JournalGitService) a moment after it was last written by a save. The pane lists the commits with when
/// they were made and shows what each added to the journal; anything more is for a git client.</summary>
public partial class MainWindow
{
    private static readonly IBrush GitAddedBrush = new SolidColorBrush(Color.Parse("#7FD99A"));
    private static readonly IBrush GitRemovedBrush = new SolidColorBrush(Color.Parse("#E88A8A"));

    // Saves write the journal in several lines, and Save All in several saves; one commit follows them all.
    private static readonly TimeSpan GitCommitDelay = TimeSpan.FromSeconds(1.5);
    private DispatcherTimer? gitCommitTimer;
    private string? gitJournalPath;
    private bool gitAvailable = true;

    private sealed record GitCommitEntry(string Id, string Label)
    {
        public override string ToString() => Label;
    }

    private void HookJournalCommits() => EditJournal.Appended += journal => Dispatcher.UIThread.Post(() =>
    {
        gitJournalPath = journal.FilePath;
        if (gitCommitTimer is null)
        {
            gitCommitTimer = new DispatcherTimer { Interval = GitCommitDelay };
            gitCommitTimer.Tick += (_, _) =>
            {
                gitCommitTimer.Stop();
                _ = CommitJournalAsync();
            };
        }

        gitCommitTimer.Stop();
        gitCommitTimer.Start();
    });

    private async Task CommitJournalAsync()
    {
        if (gitJournalPath is not { } path || !gitAvailable) return;
        try
        {
            if (!await JournalGitService.IsAvailableAsync())
            {
                gitAvailable = false;
                return;
            }

            if (await JournalGitService.CommitAsync(path) && GitPanel.IsVisible) await LoadGitCommitsAsync();
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            LogUiError("The journal could not be committed", ex);
            GitInfo.Text = $"The journal could not be committed: {ex.Message}";
        }
    }

    private async void ConfigureGitPane() => await LoadGitCommitsAsync();

    private async void GitReload_Click(object? sender, RoutedEventArgs e) => await LoadGitCommitsAsync();

    private async Task LoadGitCommitsAsync()
    {
        GitCommits.ItemsSource = null;
        GitChanges.ItemsSource = null;
        if (currentProject is null) return;

        string path = EditJournal.Active?.FilePath ?? EditJournal.GetDefaultPath(currentProject, null);
        if (!await JournalGitService.IsAvailableAsync())
        {
            gitAvailable = false;
            GitInfo.Text = "git was not found. Install git and the journal is committed from the next save.";
            return;
        }

        gitAvailable = true;
        List<JournalGitService.Commit> commits = await JournalGitService.LogAsync(path);
        GitInfo.Text = commits.Count == 0
            ? $"No commits yet: the journal ({path}) is committed after the first save."
            : $"{commits.Count} commit{(commits.Count == 1 ? "" : "s")} of {path}, in the repository {JournalGitService.RepositoryPath(path)}.";
        GitCommits.ItemsSource = commits.Select(commit => new GitCommitEntry(commit.Id, commit.Time.LocalDateTime.ToString("yyyy-MM-dd HH:mm:ss"))).ToList();
        if (commits.Count > 0) GitCommits.SelectedIndex = 0;
    }

    private async void GitCommits_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (GitCommits.SelectedItem is not GitCommitEntry commit || currentProject is null) return;
        string path = EditJournal.Active?.FilePath ?? EditJournal.GetDefaultPath(currentProject, null);
        List<string> changes = await JournalGitService.ChangesAsync(path, commit.Id);
        // The selection may have moved on while git was running.
        if (GitCommits.SelectedItem is not GitCommitEntry current || current.Id != commit.Id) return;
        GitChanges.ItemsSource = changes.Select((line, index) => new JournalLine(index + 1, line,
            line.StartsWith('+') ? (line.TrimStart('+').TrimStart().StartsWith('#') ? JournalCommentBrush : GitAddedBrush) : GitRemovedBrush)).ToList();
    }
}
