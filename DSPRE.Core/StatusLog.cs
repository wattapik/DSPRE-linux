namespace DSPRE.Core;

public enum StatusLevel
{
    Info,
    Warning,
    Error
}

public sealed record StatusEntry(DateTime Time, StatusLevel Level, string Source, string Message)
{
    public override string ToString() => $"{Time:HH:mm:ss}  {Level,-7}  {Source}: {Message}";
}

/// <summary>The program's status message log: kept in memory for the log window and appended to a
/// text file in the user's local application data folder, which is rotated once it grows large.</summary>
public static class StatusLog
{
    private const int MaxEntriesInMemory = 5000;
    private const long MaxFileBytes = 5 * 1024 * 1024;

    private static readonly object Gate = new();
    private static readonly List<StatusEntry> entries = [];
    private static bool sessionStarted;

    public static string LogDirectory { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "DSPRE Reloaded", "Logs");

    public static string LogFilePath { get; } = Path.Combine(LogDirectory, "status.log");

    /// <summary>Raised on whichever thread wrote the entry.</summary>
    public static event Action<StatusEntry>? Added;

    public static IReadOnlyList<StatusEntry> Snapshot()
    {
        lock (Gate) return entries.ToList();
    }

    public static void Info(string source, string message) => Write(StatusLevel.Info, source, message);
    public static void Warning(string source, string message) => Write(StatusLevel.Warning, source, message);
    public static void Error(string source, string message) => Write(StatusLevel.Error, source, message);

    public static void Write(StatusLevel level, string source, string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        var entry = new StatusEntry(DateTime.Now, level, source, message.Trim());
        lock (Gate)
        {
            entries.Add(entry);
            if (entries.Count > MaxEntriesInMemory) entries.RemoveRange(0, entries.Count - MaxEntriesInMemory);
            AppendToFile(entry);
        }

        Added?.Invoke(entry);
    }

    private static void AppendToFile(StatusEntry entry)
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            if (File.Exists(LogFilePath) && new FileInfo(LogFilePath).Length > MaxFileBytes)
            {
                File.Move(LogFilePath, Path.Combine(LogDirectory, "status.old.log"), overwrite: true);
            }

            string text = $"{entry.Time:yyyy-MM-dd HH:mm:ss} [{entry.Level}] {entry.Source}: {entry.Message.Replace("\n", "\n    ")}{Environment.NewLine}";
            if (!sessionStarted)
            {
                sessionStarted = true;
                text = $"{Environment.NewLine}===== Session started {DateTime.Now:yyyy-MM-dd HH:mm:ss} ====={Environment.NewLine}" + text;
            }

            File.AppendAllText(LogFilePath, text);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A log that cannot be written must never take the program down with it.
        }
    }
}
