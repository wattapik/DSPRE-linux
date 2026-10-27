using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Script Editor's messages: each <c>Message N</c> shown with its text from the header's
/// text archive (see ScriptMessageText), and the archive listed beside the script. The archive object
/// is the one the Text Editor edits too (GetOpenTextArchive), so both always show the same text.</summary>
public partial class MainWindow
{
    private const string ScriptSectionBreak = "\n//===== DSPRE SECTION BREAK =====//\n";

    /// <summary>The archive's text as the script's lines last showed it: an entry changes only where a
    /// line's text differs from this.</summary>
    private List<string> scriptMessageBaseline = [];

    /// <summary>Entries changed by typing in the script since the baseline, undone again if the text is
    /// typed back.</summary>
    private HashSet<int> liveEditedScriptMessages = [];
    private bool loadingScriptMessageList;

    private string ExpandScriptMessages(string content)
    {
        liveEditedScriptMessages = [];
        if (currentScriptTextArchive is null)
        {
            scriptMessageBaseline = [];
            return content;
        }

        scriptMessageBaseline = [.. currentScriptTextArchive.Messages];
        return ScriptMessageText.Expand(content, currentScriptTextArchive.Messages);
    }

    private string JoinedScriptSections() =>
        string.Join(ScriptSectionBreak, ScriptsTextBox.Text ?? "", FunctionsTextBox.Text ?? "", ActionsTextBox.Text ?? "");

    private void UndoLiveScriptMessageEdits()
    {
        if (currentScriptTextArchive is null) return;
        foreach (int index in liveEditedScriptMessages)
        {
            if (index < currentScriptTextArchive.Messages.Count && index < scriptMessageBaseline.Count) currentScriptTextArchive.Messages[index] = scriptMessageBaseline[index];
        }

        liveEditedScriptMessages = [];
    }

    /// <summary>Carries text typed into the script's numbered messages into the archive as it is typed.
    /// Unnumbered (new) messages wait for Save Script, so a half-typed line never becomes one.</summary>
    private void ApplyLiveScriptMessageEdits()
    {
        if (currentScriptTextArchive is null) return;
        List<string> before = [.. currentScriptTextArchive.Messages];
        UndoLiveScriptMessageEdits();
        string source = scriptUsesLua ? LuaMessageLines() : JoinedScriptSections();
        ScriptMessageText.CollapseResult result = ScriptMessageText.Collapse(source, currentScriptTextArchive.Messages, scriptMessageBaseline, appendNew: false);
        liveEditedScriptMessages = [.. result.Edited];
        if (scriptUsesLua) SnapshotLuaMessages();
        if (!before.SequenceEqual(currentScriptTextArchive.Messages)) RefreshScriptMessageList();
    }

    /// <summary>The three sections with every message back to <c>Message N</c>, new texts appended to the
    /// archive and the archive saved, plus a note for the status line.</summary>
    private (string Scripts, string Functions, string Actions, string Note) CollapseScriptMessagesForSave()
    {
        if (currentProject is null || currentScriptTextArchive is null)
        {
            return (ScriptsTextBox.Text ?? "", FunctionsTextBox.Text ?? "", ActionsTextBox.Text ?? "", "");
        }

        UndoLiveScriptMessageEdits();
        ScriptMessageText.CollapseResult result = ScriptMessageText.Collapse(JoinedScriptSections(), currentScriptTextArchive.Messages, scriptMessageBaseline, appendNew: true);
        string[] sections = result.Content.Split(ScriptSectionBreak);
        if (sections.Length != 3) throw new InvalidDataException("A script section contains the editor's own section marker, so the script could not be split back into its sections.");

        TextArchiveService.Save(currentProject, currentScriptTextArchive);
        scriptMessageBaseline = [.. currentScriptTextArchive.Messages];
        string note = "";
        if (result.Added.Count > 0) note += $" Added message{(result.Added.Count == 1 ? "" : "s")} {string.Join(", ", result.Added)} to the text archive.";
        if (result.Conflicts.Count > 0) note += $" Message{(result.Conflicts.Count == 1 ? "" : "s")} {string.Join(", ", result.Conflicts)} had different text on different lines; the last line's text was kept.";
        return (sections[0], sections[1], sections[2], note);
    }

    /// <summary>Shows the saved sections with their texts again, new messages now numbered.</summary>
    private void ShowSavedScriptMessages(string scripts, string functions, string actions)
    {
        if (currentScriptTextArchive is null) return;
        SetScriptSections(
            ScriptMessageText.Expand(scripts, currentScriptTextArchive.Messages),
            ScriptMessageText.Expand(functions, currentScriptTextArchive.Messages),
            ScriptMessageText.Expand(actions, currentScriptTextArchive.Messages));
        RefreshScriptMessageList();
    }

    private void SetScriptSections(string scripts, string functions, string actions)
    {
        loadingScript = true;
        try
        {
            foreach ((TextBox box, string text) in new[] { (ScriptsTextBox, scripts), (FunctionsTextBox, functions), (ActionsTextBox, actions) })
            {
                if (box.Text == text) continue;
                int caret = box.CaretIndex;
                box.Text = text;
                box.CaretIndex = Math.Min(caret, text.Length);
            }
        }
        finally
        {
            loadingScript = false;
        }
    }

    /// <summary>Redraws the script's message text from the archive after it changed elsewhere (the Text
    /// Editor, or this editor's own message panel), keeping any unsaved script edits.</summary>
    private void RefreshScriptMessages()
    {
        if (currentScriptTextArchive is null) return;
        List<string> scratch = [.. currentScriptTextArchive.Messages];
        string numbered = ScriptMessageText.Collapse(JoinedScriptSections(), scratch, scriptMessageBaseline, appendNew: false).Content;
        string[] sections = ScriptMessageText.Expand(numbered, currentScriptTextArchive.Messages).Split(ScriptSectionBreak);
        if (sections.Length != 3) return;
        SetScriptSections(sections[0], sections[1], sections[2]);
        RefreshLuaMessageTexts();
        scriptMessageBaseline = [.. currentScriptTextArchive.Messages];
        liveEditedScriptMessages = [];
        RefreshScriptMessageList();
    }

    private void RefreshScriptMessageList()
    {
        loadingScriptMessageList = true;
        try
        {
            if (currentScriptTextArchive is null)
            {
                ScriptMessagesTitle.Text = "Text Archive";
                ScriptMessagesList.ItemsSource = null;
                ScriptMessageEditBox.Text = "";
                ScriptMessagesStatus.Text = "No header using this script has a text archive.";
                return;
            }

            int selected = ScriptMessagesList.SelectedIndex;
            List<string> messages = currentScriptTextArchive.Messages;
            if (ScriptMessagesStatus.Text?.StartsWith("No header", StringComparison.Ordinal) == true) ScriptMessagesStatus.Text = "";
            ScriptMessagesTitle.Text = $"Text Archive {currentScriptTextArchive.Id:D4} ({messages.Count} messages)";
            ScriptMessagesList.ItemsSource = messages.Select((text, index) => $"{index}: {text}").ToList();
            if (selected >= 0 && selected < messages.Count)
            {
                ScriptMessagesList.SelectedIndex = selected;
                ScriptMessagesList.ScrollIntoView(selected);
            }
        }
        finally
        {
            loadingScriptMessageList = false;
        }
    }

    private void ScriptMessagesList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingScriptMessageList || currentScriptTextArchive is null) return;
        int index = ScriptMessagesList.SelectedIndex;
        loadingScriptMessageList = true;
        try
        {
            ScriptMessageEditBox.Text = index >= 0 && index < currentScriptTextArchive.Messages.Count ? currentScriptTextArchive.Messages[index] : "";
        }
        finally
        {
            loadingScriptMessageList = false;
        }
    }

    private void ScriptMessageEditBox_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (loadingScriptMessageList || currentScriptTextArchive is null) return;
        int index = ScriptMessagesList.SelectedIndex;
        if (index < 0 || index >= currentScriptTextArchive.Messages.Count) return;
        string text = ScriptMessageEditBox.Text ?? "";
        if (currentScriptTextArchive.Messages[index] == text) return;

        currentScriptTextArchive.Messages[index] = text;
        RefreshScriptMessages();
        ScriptMessagesStatus.Text = $"Edited message {index}. Save Script saves the text archive.";
    }

    private void ScriptMessagesList_DoubleTapped(object? sender, TappedEventArgs e) => InsertSelectedScriptMessage();

    private void ScriptMessageInsert_Click(object? sender, RoutedEventArgs e) => InsertSelectedScriptMessage();

    private void InsertSelectedScriptMessage()
    {
        if (currentScriptTextArchive is null) return;
        int index = ScriptMessagesList.SelectedIndex;
        if (index < 0 || index >= currentScriptTextArchive.Messages.Count)
        {
            ScriptMessagesStatus.Text = "Select a message to insert.";
            return;
        }

        TextBox box = GetActiveScriptTextBox();
        string text = box.Text ?? "";
        int caret = Math.Clamp(box.CaretIndex, 0, text.Length);
        string quoted = $"\"{currentScriptTextArchive.Messages[index].Replace("\"", "\\\"")}\"";
        string line = box == LuaTextBox
            ? $"Message({DSPRE.Core.Lua.LuaMessageText.ToLuaLiteral(currentScriptTextArchive.Messages[index])})"
            : $"Message {index} {quoted}";
        box.Text = text.Insert(caret, line);
        box.CaretIndex = caret + line.Length;
        box.Focus();
    }

    private void ScriptMessageNew_Click(object? sender, RoutedEventArgs e)
    {
        if (currentScriptTextArchive is null) return;
        currentScriptTextArchive.Messages.Add("");
        // Not an edit made through the script's lines, so the baseline grows with it.
        scriptMessageBaseline.Add("");
        int index = currentScriptTextArchive.Messages.Count - 1;
        RefreshScriptMessageList();
        ScriptMessagesList.SelectedIndex = index;
        ScriptMessagesList.ScrollIntoView(index);
        ScriptMessageEditBox.Focus();
        ScriptMessagesStatus.Text = $"Added message {index}. Type its text below, then insert it; Save Script saves the text archive.";
    }
}
