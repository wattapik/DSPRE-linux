using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;

namespace DSPRE.Core;

public sealed record JournalContext(RomProject Project, RomGameMetadata Metadata);

/// <summary>A kind of document the journal can diff and replay. Every op on it is written as
/// <c>{Verb}{Name}(key: value..., path: "...", value: ...)</c>, e.g.
/// <c>AddMapEvent(eventFile: 12, path: "Overworlds[3]", value: {...})</c>. Load/Save go through the same
/// Core services the editors use, so replay exercises the real save code.</summary>
public sealed class JournalDocument
{
    public required string Name { get; init; }
    public required Type RootType { get; init; }
    public required Func<JournalContext, JsonObject, object?> Load { get; init; }
    public required Action<JournalContext, JsonObject, object> Save { get; init; }
}

/// <summary>An append-only, human-readable, replayable log of every edit made to a ROM project (see
/// FUNCTIONS.md / docs in EditJournalReplayer). Lives at the project root so it is tracked alongside the
/// extracted files by git.
///
/// Editors never write to it directly: each Core save method wraps its write in <see cref="Track"/>,
/// which reloads the document from disk before and after the write and journals the difference. Diffing
/// what actually landed on disk (rather than in-memory editor state) means fields a format doesn't
/// persist never show up, and every save path - including ones added later - is covered as long as it
/// goes through a Core service.</summary>
public sealed class EditJournal
{
    public const string FileName = "edit-journal.dspre";

    private EditJournal(JournalContext context, string filePath)
    {
        Context = context;
        FilePath = filePath;
    }

    /// <summary>Where a project's journal lives: next to the ROM it was extracted from, as
    /// "&lt;rom name&gt;.edit-journal.dspre", so deleting or re-extracting the contents folder never takes
    /// the journal with it. A project with no known ROM keeps it inside the project folder.</summary>
    public static string GetDefaultPath(RomProject project, string? romPath)
    {
        romPath ??= RomSourceStamp.Read(project.RootPath)?.RomPath;
        if (string.IsNullOrEmpty(romPath) || Path.GetDirectoryName(Path.GetFullPath(romPath)) is not string romDirectory)
        {
            return Path.Combine(project.RootPath, FileName);
        }

        return Path.Combine(romDirectory, $"{Path.GetFileNameWithoutExtension(romPath)}.{FileName}");
    }

    public static EditJournal? Active { get; private set; }

    public JournalContext Context { get; }
    public string FilePath { get; }

    /// <summary>What can be undone: the saves made since this journal was opened.</summary>
    public EditUndoHistory History { get; } = new();

    /// <summary>Starts journaling edits to <paramref name="project"/>. Creating the journal records the
    /// base ROM it was extracted from (when known), which replay checks against.</summary>
    public static EditJournal Open(RomProject project, RomGameMetadata metadata, string? baseRomPath = null, string? journalPath = null)
    {
        journalPath ??= GetDefaultPath(project, baseRomPath);
        // Journals used to be kept inside the contents folder; carry an old one over to its new home.
        string legacyPath = Path.Combine(project.RootPath, FileName);
        if (!File.Exists(journalPath) && File.Exists(legacyPath) && !string.Equals(Path.GetFullPath(legacyPath), Path.GetFullPath(journalPath), StringComparison.Ordinal))
        {
            File.Move(legacyPath, journalPath);
        }

        var journal = new EditJournal(new JournalContext(project, metadata), journalPath);
        if (!File.Exists(journal.FilePath))
        {
            var header = new StringBuilder();
            header.AppendLine("# DSPRE edit journal - every edit made to this project, in order.");
            header.AppendLine("# Replay onto a fresh extraction of the base ROM to regenerate the edited ROM:");
            header.AppendLine("#   DSPRE.Avalonia --replay-journal <this file> --base-rom <base.nds> --output <edited.nds>");
            header.AppendLine("# Lines are Function(arg: <json>, ...); '#' lines are comments and are ignored on replay.");
            var args = new List<(string, JsonNode?)> { ("gameCode", metadata.GameCode) };
            if (baseRomPath is not null && File.Exists(baseRomPath))
            {
                args.Add(("file", Path.GetFileName(baseRomPath)));
                args.Add(("sha1", ComputeSha1(baseRomPath)));
            }
            else
            {
                header.AppendLine("# Opened from an existing project folder: edits made before this journal existed are not included.");
            }
            header.AppendLine(EditJournalFormat.FormatCall("BaseRom", args));
            File.WriteAllText(journal.FilePath, header.ToString());
        }

        Active = journal;
        return journal;
    }

    public static void Close() => Active = null;

    public static string ComputeSha1(string path)
    {
        using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(SHA1.HashData(stream)).ToLowerInvariant();
    }

    private static int suppressed;

    /// <summary>While the returned scope is open nothing is journaled: for an operation that is journaled as one call of
    /// its own (and replayed by running it again), whose inner saves must not appear in the journal as well.</summary>
    public static IDisposable Suppress()
    {
        Interlocked.Increment(ref suppressed);
        return new SuppressScope();
    }

    private sealed class SuppressScope : IDisposable
    {
        private int disposed;

        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) == 0) Interlocked.Decrement(ref suppressed);
        }
    }

    private bool IsFor(RomProject? project) =>
        Volatile.Read(ref suppressed) > 0 ? false :
        project is null || string.Equals(Path.GetFullPath(project.RootPath), Path.GetFullPath(Context.Project.RootPath), StringComparison.Ordinal);

    /// <summary>Runs <paramref name="write"/> and journals whatever it changed in the given document.
    /// A no-op wrapper when no journal is active (or the write targets another project).</summary>
    public static void Track(RomProject? project, JournalDocument document, JsonObject keys, Action write)
    {
        EditJournal? journal = Active;
        if (journal is null || !journal.IsFor(project))
        {
            write();
            return;
        }

        JsonNode? before = journal.TrySnapshot(document, keys, out _);
        write();

        // The write has happened; a journaling failure past this point must not surface as a failed save.
        try
        {
            JsonNode? after = journal.TrySnapshot(document, keys, out string? error);
            if (after is null)
            {
                journal.AppendComment($"WARNING: {document.Name} ({EditJournalFormat.FormatArgs(keys)}) was saved but could not be re-read for journaling: {error}");
                return;
            }

            List<JournalOp> ops = before is null
                ? [new JournalOp(JournalOpVerb.Set, string.Empty, after)]
                : EditJournalModel.Diff(document.RootType, before, after);
            if (ops.Count == 0) return;
            journal.History.Record(document, keys, before, after, keys.Count == 0 ? document.Name : $"{document.Name} ({EditJournalFormat.FormatArgs(keys)})");

            var lines = new List<string> { Stamp(keys.Count == 0 ? $"Saved {document.Name}" : $"Saved {document.Name} ({EditJournalFormat.FormatArgs(keys)})") };
            lines.AddRange(ops.Select(op => EditJournalFormat.FormatOp(document, keys, op)));
            journal.AppendLines(lines);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Edit journal could not record {document.Name}: {ex}");
        }
    }

    /// <summary>Journals an operation that is not a field-level document edit (adding/removing whole
    /// files, importing a file from disk...). It must have a handler in EditJournalReplayer.</summary>
    public static void Record(RomProject? project, string function, params (string Name, JsonNode? Value)[] args)
    {
        EditJournal? journal = Active;
        if (journal is null || !journal.IsFor(project)) return;
        journal.History.Barrier();
        journal.AppendLines([Stamp(function), EditJournalFormat.FormatCall(function, args)]);
    }

    /// <summary>Journals several calls that together make up one edit, under a single comment.</summary>
    public static void RecordAll(RomProject? project, string description, IReadOnlyList<(string Function, (string Name, JsonNode? Value)[] Args)> calls)
    {
        EditJournal? journal = Active;
        if (journal is null || !journal.IsFor(project) || calls.Count == 0) return;
        journal.History.Barrier();
        journal.AppendLines(calls.Select(call => EditJournalFormat.FormatCall(call.Function, call.Args)).Prepend(Stamp(description)));
    }

    public static void Comment(RomProject? project, string text)
    {
        EditJournal? journal = Active;
        if (journal is null || !journal.IsFor(project)) return;
        journal.AppendComment(text);
    }

    private JsonNode? TrySnapshot(JournalDocument document, JsonObject keys, out string? error)
    {
        try
        {
            error = null;
            return EditJournalModel.ToNode(document.Load(Context, keys));
        }
        catch (Exception ex)
        {
            // Journaling must never block a save; the caller records the failure in the journal.
            error = ex.Message;
            return null;
        }
    }

    private static string Stamp(string text) => $"# [{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {text}";

    private void AppendComment(string text) => AppendLines([Stamp(text)]);

    /// <summary>Raised after lines were added to the journal (the UI uses it to commit the journal after saves).</summary>
    public static event Action<EditJournal>? Appended;

    private void AppendLines(IEnumerable<string> lines)
    {
        File.AppendAllText(FilePath, string.Join('\n', lines) + "\n");
        Appended?.Invoke(this);
    }
}
