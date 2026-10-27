using DSPRE.Core;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace DSPRE.Avalonia;

/// <summary>Knows what one editor's document looked like when it was loaded or last saved, and
/// describes the most recent change to it in the words of the edit journal ("Maps[2][4] 65535 → 12").
/// Comparing the data itself, rather than trusting control events, is what keeps a list merely
/// being drawn - which fires the same TextChanged/SelectionChanged events an edit does - from being
/// reported as a modification.</summary>
public sealed class EditTracker
{
    private const int MaxValueLength = 48;
    private static readonly Regex PathSegment = new(@"([^.\[\]]+)|\[([^\]]*)\]", RegexOptions.Compiled);

    private JsonNode? saved;
    private JsonNode? lastReported;
    private string label = string.Empty;

    // An editor's document object can exist before the editor has loaded anything into it (an empty
    // list, a default record); until a document has been loaded there is nothing to have changed.
    private bool hasBaseline;

    public void Loaded(object? document, string label)
    {
        this.label = label;
        saved = lastReported = Snapshot(document);
        hasBaseline = document is not null;
    }

    public void Saved(object? document)
    {
        saved = lastReported = Snapshot(document);
        hasBaseline = document is not null;
    }

    /// <summary>Whether the document differs from what was last loaded or saved.</summary>
    public bool IsUnsaved(object? document) => hasBaseline && !JsonNode.DeepEquals(saved, Snapshot(document));

    /// <summary>What differs from the loaded or saved version, in the journal's words.</summary>
    public IReadOnlyList<string> DescribeUnsaved(object? document, int maxLines = 6)
    {
        if (document is null || !hasBaseline) return [];
        List<JournalOp> ops = EditJournalModel.Diff(document.GetType(), saved, Snapshot(document));
        var lines = ops.Take(maxLines).Select(op => DescribeOp(op, saved)).ToList();
        if (ops.Count > maxLines) lines.Add($"...and {ops.Count - maxLines} more");
        return lines;
    }

    /// <summary>Null when the document is the same as when this was last asked.</summary>
    public string? DescribeLatestChange(object? document)
    {
        if (document is null || !hasBaseline) return null;
        JsonNode? now = Snapshot(document);
        List<JournalOp> ops = EditJournalModel.Diff(document.GetType(), lastReported, now);
        if (ops.Count == 0) return null;

        JsonNode? before = lastReported;
        lastReported = now;
        string change = string.Join("; ", ops.Take(2).Select(op => DescribeOp(op, before)));
        if (ops.Count > 2) change += $" (and {ops.Count - 2} more)";
        return JsonNode.DeepEquals(saved, now)
            ? $"{label}: {change}. It now matches the saved version again."
            : $"{label}: {change}. Not saved yet.";
    }

    private static JsonNode? Snapshot(object? document) => document is null ? null : EditJournalModel.ToNode(document);

    private static string DescribeOp(JournalOp op, JsonNode? before)
    {
        string path = op.Path.Length == 0 ? "everything" : op.Path;
        return op.Verb switch
        {
            JournalOpVerb.Add => $"added {path}",
            JournalOpVerb.Remove => $"removed {path}",
            _ => $"{path} {FormatValue(Resolve(before, op.Path))} → {FormatValue(op.Value)}"
        };
    }

    private static JsonNode? Resolve(JsonNode? root, string path)
    {
        JsonNode? node = root;
        foreach (Match segment in PathSegment.Matches(path))
        {
            if (node is null) return null;
            string key = segment.Groups[1].Success ? segment.Groups[1].Value : segment.Groups[2].Value;
            node = node switch
            {
                JsonArray array when int.TryParse(key, out int index) => index >= 0 && index < array.Count ? array[index] : null,
                JsonObject obj => obj[key],
                _ => null
            };
        }

        return node;
    }

    private static string FormatValue(JsonNode? value)
    {
        string text = EditJournalFormat.FormatValue(value);
        return text.Length <= MaxValueLength ? text : text[..(MaxValueLength - 1)] + "…";
    }
}
