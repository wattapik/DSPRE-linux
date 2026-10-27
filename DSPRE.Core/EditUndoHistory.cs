using System.Text.Json.Nodes;

namespace DSPRE.Core;

/// <summary>Undo and redo of saved edits, built on the edit journal. Every journaled document save keeps the
/// document as it was before and after; undoing writes the earlier version back through the same Core
/// service (so the undo is itself journaled, and the journal stays a true history) and redoing writes the
/// later one. Saves made close together (one Save All) undo as one step.
///
/// Only document saves can be undone. Anything else the journal records - adding or removing a whole file,
/// importing one - cannot be reversed here, so it ends the history: nothing before it is undone across it.</summary>
public sealed class EditUndoHistory
{
    private sealed record Entry(JournalDocument Document, JsonObject Keys, JsonNode? Before, JsonNode? After, string Description);

    // Saves within this long of the previous one belong to the same step.
    private static readonly TimeSpan GroupWindow = TimeSpan.FromMilliseconds(400);

    private readonly List<List<Entry>> undo = [];
    private readonly List<List<Entry>> redo = [];
    private DateTime lastRecorded = DateTime.MinValue;
    private bool applying;

    /// <summary>Raised whenever what can be undone or redone changed; may come from any thread.</summary>
    public event Action? Changed;

    public bool CanUndo => undo.Count > 0;
    public bool CanRedo => redo.Count > 0;
    public string? UndoDescription => undo.Count > 0 ? Describe(undo[^1]) : null;
    public string? RedoDescription => redo.Count > 0 ? Describe(redo[^1]) : null;

    private static string Describe(List<Entry> step) =>
        step.Count == 1 ? step[0].Description : $"{step[0].Description} and {step.Count - 1} more";

    internal void Record(JournalDocument document, JsonObject keys, JsonNode? before, JsonNode? after, string description)
    {
        if (applying) return;
        if (before is null || after is null)
        {
            Barrier();
            return;
        }

        DateTime now = DateTime.UtcNow;
        var entry = new Entry(document, keys, before, after, description);
        if (undo.Count > 0 && now - lastRecorded <= GroupWindow && redo.Count == 0) undo[^1].Add(entry);
        else undo.Add([entry]);
        lastRecorded = now;
        redo.Clear();
        Changed?.Invoke();
    }

    /// <summary>Something that cannot be undone was done: nothing earlier can be undone across it.</summary>
    internal void Barrier()
    {
        if (applying || (undo.Count == 0 && redo.Count == 0)) return;
        Clear();
    }

    public void Clear()
    {
        undo.Clear();
        redo.Clear();
        Changed?.Invoke();
    }

    /// <summary>Reverts the latest step; the description of what was undone, or null when there was nothing.</summary>
    public string? Undo(JournalContext context) => Apply(context, undo, redo, forward: false);

    public string? Redo(JournalContext context) => Apply(context, redo, undo, forward: true);

    private string? Apply(JournalContext context, List<List<Entry>> from, List<List<Entry>> to, bool forward)
    {
        if (from.Count == 0) return null;
        List<Entry> step = from[^1];
        string description = Describe(step);
        EditJournal.Comment(context.Project, $"{(forward ? "Redo" : "Undo")}: {description}");
        applying = true;
        try
        {
            foreach (Entry entry in forward ? step : Enumerable.Reverse(step))
            {
                object document = EditJournalModel.FromNode(forward ? entry.After : entry.Before, entry.Document.RootType)
                    ?? throw new InvalidOperationException($"{entry.Description} could not be restored.");
                entry.Document.Save(context, entry.Keys, document);
            }
        }
        finally
        {
            applying = false;
        }

        from.RemoveAt(from.Count - 1);
        to.Add(step);
        lastRecorded = DateTime.MinValue;
        Changed?.Invoke();
        return description;
    }
}
