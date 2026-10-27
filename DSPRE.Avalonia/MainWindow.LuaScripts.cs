using Avalonia.Controls;
using Avalonia.Interactivity;
using DSPRE.Core;
using DSPRE.Core.Lua;

namespace DSPRE.Avalonia;

/// <summary>The Script Editor's Lua tabs. A script file with a .lua beside it (LuaScriptService) is edited
/// as Lua: Save Script compiles the Lua into the Scripts/Functions/Actions sections, which are then
/// read-only, and saves both files the usual way. global.lua holds helpers for every Lua file.
///
/// Message texts written in the Lua (<c>Message(4, "Hi!")</c>, <c>npc_message(4, "Hi!")</c>) are the text
/// archive's: the archive's text replaces them when a file loads, typing in them edits the archive as the
/// plain script lines do, and editing a message in the panel rewrites them.</summary>
public partial class MainWindow
{
    private bool scriptUsesLua;
    private string savedGlobalLua = "";

    /// <summary>The archive texts the Lua's message texts were last brought in line with: a text in the
    /// Lua that still matches its entry here is rewritten when the archive changes from elsewhere.</summary>
    private List<string> luaMessageSnapshot = [];

    private void SnapshotLuaMessages() => luaMessageSnapshot = currentScriptTextArchive is null ? [] : [.. currentScriptTextArchive.Messages];

    /// <summary>The Script Editor's file list starts with global.lua under this id.</summary>
    private const int GlobalLuaEntryId = -1;

    /// <summary>Shows global.lua on its own, as the file list's first entry: only the Lua tab, showing it.</summary>
    private void LoadGlobalLuaEntry()
    {
        loadingScript = true;
        try
        {
            currentScriptTextArchive = null;
            scriptUsesLua = false;
            SetScriptSections("", "", "");
            LuaTextBox.Text = "";
            LoadGlobalLua();
            SetLuaMode(false);
            foreach (TextBox box in new[] { ScriptsTextBox, FunctionsTextBox, ActionsTextBox, LuaTextBox }) box.IsReadOnly = true;
            ConvertToLuaButton.IsEnabled = false;
            GlobalLuaToggle.IsEnabled = false;
            ShowGlobalLua(true);
            ScriptSectionTabs.SelectedItem = LuaTab;
            UpdateScriptSectionCounts();
            RefreshScriptMessageList();
            HeadersUsingScriptList.Items.Clear();
            loadedScriptFileIndex = ScriptFileSelector.SelectedIndex;
            scriptEdits.Loaded(CurrentScriptDocument(), "global.lua");
            LuaStatus.Text = currentProject is not null && LuaScriptService.LoadGlobal(currentProject) is null
                ? "There is no global.lua yet. Converting a script file to Lua creates one, or write it here and Save Script."
                : "Helpers, templates and variable/flag names for every Lua script file. Saving recompiles every Lua script file.";
            ScriptSearchStatus.Text = "Loaded global.lua.";
        }
        finally
        {
            loadingScript = false;
        }
    }

    private ScriptLanguage GetScriptLanguage()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            throw new InvalidOperationException("No project is open.");
        }
        return LuaScriptService.LoadLanguage(currentProject, metadata, Path.Combine(AppContext.BaseDirectory, "databases"));
    }

    private string? CurrentGlobalLua() => string.IsNullOrWhiteSpace(GlobalLuaTextBox.Text) ? null : GlobalLuaTextBox.Text;

    /// <summary>Reads global.lua, unless the tab holds unsaved edits to it.</summary>
    private void LoadGlobalLua()
    {
        if (currentProject is null || (GlobalLuaTextBox.Text ?? "") != savedGlobalLua) return;
        savedGlobalLua = LuaScriptService.LoadGlobal(currentProject) ?? "";
        GlobalLuaTextBox.Text = savedGlobalLua;
    }

    /// <summary>Shows the script file's Lua, if it has one, with its message texts brought up to date from
    /// the archive, and warns when the .script no longer matches it.</summary>
    private void LoadLuaForScript(int id, string scriptText)
    {
        LoadGlobalLua();
        if (!GlobalLuaToggle.IsEnabled)
        {
            // Coming from the global.lua entry, which locks the editor on global.lua.
            GlobalLuaToggle.IsEnabled = true;
            ShowGlobalLua(false);
        }
        scriptUsesLua = currentProject is not null && LuaScriptService.Exists(currentProject, id);
        string lua = scriptUsesLua ? File.ReadAllText(LuaScriptService.GetPath(currentProject!, id)) : "";
        if (scriptUsesLua && currentScriptTextArchive is not null)
        {
            List<string> messages = currentScriptTextArchive.Messages;
            lua = LuaMessageText.ReplaceTexts(lua, (index, _) => index < messages.Count ? messages[index] : null);
        }
        LuaTextBox.Text = lua;
        SnapshotLuaMessages();
        SetLuaMode(scriptUsesLua);
        if (!scriptUsesLua)
        {
            LuaStatus.Text = "This script file is not written in Lua. Convert to Lua to edit it as Lua.";
            return;
        }

        ScriptSectionTabs.SelectedItem = LuaTab;
        try
        {
            string? mismatch = LuaScriptService.FindMismatch(GetScriptLanguage(), lua, scriptText, currentScriptTextArchive?.Messages, CurrentGlobalLua());
            LuaStatus.Text = mismatch is null
                ? $"Edited as Lua: saving compiles {id:D4}.lua into {id:D4}.script."
                : $"{id:D4}.script no longer matches {id:D4}.lua ({mismatch}). Saving will replace the .script with the compiled Lua; Stop Using Lua, then Convert to Lua, to start again from the .script instead.";
        }
        catch (Exception ex)
        {
            LuaStatus.Text = ex.Message;
        }
    }

    private void SetLuaMode(bool on)
    {
        foreach (TextBox box in new[] { ScriptsTextBox, FunctionsTextBox, ActionsTextBox }) box.IsReadOnly = on;
        LuaTextBox.IsReadOnly = !on;
        ConvertToLuaButton.IsEnabled = !on;
        CheckLuaButton.IsEnabled = on;
        UseLuaNamesButton.IsEnabled = on;
        StopUsingLuaButton.IsEnabled = on;
    }

    private void LuaTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateLineNumbers(LuaTextBox, LuaLineNumbers);
        QueueScriptIconRefresh();
        if (scriptUsesLua && !loadingScript && !updatingScriptText) ApplyLiveScriptMessageEdits();
    }

    private void GlobalLuaTextChanged(object? sender, TextChangedEventArgs e) => UpdateLineNumbers(GlobalLuaTextBox, GlobalLuaLineNumbers);

    /// <summary>The Lua's message texts as <c>Message N "text"</c> lines, for ScriptMessageText.</summary>
    private string LuaMessageLines() =>
        string.Join('\n', LuaMessageText.FindTexts(LuaTextBox.Text ?? "")
            .Select(message => $"Message {message.Index} \"{message.Text.Replace("\"", "\\\"")}\""));

    /// <summary>After the archive changed from elsewhere (the message panel, the Text Editor), rewrites the
    /// Lua texts that still showed the old text.</summary>
    private void RefreshLuaMessageTexts()
    {
        if (!scriptUsesLua || currentScriptTextArchive is null) return;
        List<string> messages = currentScriptTextArchive.Messages;
        List<string> before = luaMessageSnapshot;
        string lua = LuaTextBox.Text ?? "";
        string updated = LuaMessageText.ReplaceTexts(lua, (index, text) =>
            index < before.Count && index < messages.Count && text == before[index] && messages[index] != before[index] ? messages[index] : null);
        SnapshotLuaMessages();
        if (updated == lua) return;

        loadingScript = true;
        try
        {
            int caret = LuaTextBox.CaretIndex;
            LuaTextBox.Text = updated;
            LuaTextBox.CaretIndex = Math.Min(caret, updated.Length);
        }
        finally
        {
            loadingScript = false;
        }
    }

    private void ConvertScriptToLua_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || ScriptFileSelector.SelectedIndex < 0 || ScriptFileSelector.SelectedIndex >= scriptFileIds.Count) return;
        if (scriptEdits.IsUnsaved(CurrentScriptDocument()))
        {
            LuaStatus.Text = "Save Script first: the conversion starts from the saved .script.";
            return;
        }

        int id = scriptFileIds[ScriptFileSelector.SelectedIndex];
        if (id == GlobalLuaEntryId) return;
        try
        {
            bool newGlobal = string.IsNullOrWhiteSpace(GlobalLuaTextBox.Text) && LuaScriptService.LoadGlobal(currentProject) is null;
            if (newGlobal) GlobalLuaTextBox.Text = LuaScriptService.DefaultGlobal;
            string lua = LuaScriptService.Lift(GetScriptLanguage(), id, File.ReadAllText(ScriptFileService.GetPath(currentProject, id)),
                currentScriptTextArchive?.Messages, CurrentGlobalLua());
            scriptUsesLua = true;
            SetLuaMode(true);
            loadingScript = true;
            try
            {
                LuaTextBox.Text = lua;
            }
            finally
            {
                loadingScript = false;
            }
            SnapshotLuaMessages();
            LuaTextBox.CaretIndex = 0;
            ScriptSectionTabs.SelectedItem = LuaTab;
            ShowGlobalLua(false);
            LuaStatus.Text = $"Converted, and checked to behave the same as {id:D4}.script. Save Script to start editing this file as Lua." +
                             (newGlobal ? " This also creates global.lua, with templates for common scripts." : "");
        }
        catch (Exception ex)
        {
            LogUiError("Script could not be converted to Lua", ex);
            LuaStatus.Text = ex.Message;
        }
    }

    private void CheckLua_Click(object? sender, RoutedEventArgs e)
    {
        if (TryCompileLuaIntoSections(out IReadOnlyList<string> warnings))
        {
            LuaStatus.Text = warnings.Count == 0
                ? "Compiles. The Scripts, Functions and Actions tabs show the result."
                : $"Compiles, with {warnings.Count} warning(s): {string.Join(" ", warnings.Take(3))}";
        }
    }

    private void UseLuaNames_Click(object? sender, RoutedEventArgs e)
    {
        try
        {
            string lua = LuaTextBox.Text ?? "";
            string named = LuaScriptService.UseNames(lua, GetScriptLanguage(), CurrentGlobalLua());
            ShowGlobalLua(false);
            if (named == lua)
            {
                LuaStatus.Text = "No var[...] or flag[...] in this file has a name in global.lua.";
                return;
            }
            LuaTextBox.Text = named;
            LuaStatus.Text = "Wrote global.lua's names for variables and flags. Save Script to keep them.";
        }
        catch (Exception ex)
        {
            LuaStatus.Text = ex.Message;
        }
    }

    private void StopUsingLua_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || !scriptUsesLua || ScriptFileSelector.SelectedIndex < 0 || ScriptFileSelector.SelectedIndex >= scriptFileIds.Count) return;
        if (scriptEdits.IsUnsaved(CurrentScriptDocument()))
        {
            LuaStatus.Text = "Save Script first, or reload the file to discard the changes.";
            return;
        }

        int id = scriptFileIds[ScriptFileSelector.SelectedIndex];
        LuaScriptService.Delete(currentProject, id);
        LoadSelectedScriptFile();
        ScriptSectionTabs.SelectedItem = ScriptsTab;
        ScriptSearchStatus.Text = $"Deleted {id:D4}.lua; {id:D4}.script is edited directly again.";
    }

    /// <summary>How freely the open script's messages can be renumbered (ScriptMessageAllocator.FindUse).</summary>
    private ScriptMessageUse FindMessageUse(int id, TextArchiveData? archive, ScriptLanguage language) =>
        archive is null || currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null
            ? ScriptMessageUse.Unknown
            : ScriptMessageAllocator.FindUse(currentProject, metadata, language, id, archive.Id);

    /// <summary>Compiles the Lua into the .script, numbers its messages and rebuilds the text archive from
    /// them, then saves all three (and global.lua when it changed).</summary>
    private void SaveLuaScript(int id)
    {
        if (currentProject is null) return;
        ScriptLanguage language = GetScriptLanguage();
        UndoLiveScriptMessageEdits();
        TextArchiveData? archive = currentScriptTextArchive;
        LuaScriptService.SaveResult result;
        try
        {
            result = LuaScriptService.CompileForSave(language, LuaTextBox.Text ?? "", CurrentGlobalLua(), archive?.Messages, FindMessageUse(id, archive, language));
        }
        catch (LuaException ex)
        {
            ShowLuaError(ex.Message, ex.InGlobal);
            MoveCaretToLine(ex.InGlobal ? GlobalLuaTextBox : LuaTextBox, ex.InGlobal ? ex.Line - LuaException.GlobalLineBase : ex.Line);
            return;
        }
        catch (Exception ex) when (ex is InvalidDataException or ScriptTextException)
        {
            ShowLuaError(ex.Message, false);
            return;
        }

        string globalNote = SaveGlobalLuaIfChanged(id);
        string messageNote = "";
        if (archive is not null && result.Messages is not null)
        {
            if (!archive.Messages.SequenceEqual(result.Messages))
            {
                archive.Messages.Clear();
                archive.Messages.AddRange(result.Messages);
                TextArchiveService.Save(currentProject, archive);
                messageNote = " " + result.Note;
            }
            scriptMessageBaseline = [.. archive.Messages];
            liveEditedScriptMessages = [];
        }
        ScriptFileService.Save(currentProject, id, result.ScriptText);
        LuaScriptService.Save(currentProject, id, LuaTextBox.Text ?? "");
        SnapshotLuaMessages();

        ShowCompiledSections(result.ScriptText, archive?.Messages);
        RefreshScriptMessageList();
        scriptEdits.Saved(CurrentScriptDocument());
        LuaStatus.Text = $"Saved {id:D4}.lua and compiled it into {id:D4}.script.{messageNote}";
        ScriptSearchStatus.Text = $"Saved {id:D4}.lua and {id:D4}.script" + (archive is null ? "." : $" with text archive {archive.Id:D4}.") + messageNote + globalNote;
    }

    private void ShowCompiledSections(string scriptText, IReadOnlyList<string>? messages)
    {
        (string scripts, string functions, string actions) = SplitScriptSections(scriptText);
        if (messages is not null)
        {
            (scripts, functions, actions) = (ScriptMessageText.Expand(scripts, messages), ScriptMessageText.Expand(functions, messages), ScriptMessageText.Expand(actions, messages));
        }
        SetScriptSections(scripts, functions, actions);
        UpdateScriptSectionCounts();
    }

    /// <summary>Compiles the Lua tab and shows the result in the Scripts/Functions/Actions tabs, numbered as
    /// saving would number its messages, without saving anything. On an error the Lua tab shows it, with
    /// the caret on its line.</summary>
    private bool TryCompileLuaIntoSections(out IReadOnlyList<string> warnings)
    {
        warnings = [];
        try
        {
            ScriptLanguage language = GetScriptLanguage();
            int id = scriptFileIds[ScriptFileSelector.SelectedIndex];
            LuaScriptService.SaveResult result = LuaScriptService.CompileForSave(language, LuaTextBox.Text ?? "", CurrentGlobalLua(),
                currentScriptTextArchive?.Messages, FindMessageUse(id, currentScriptTextArchive, language));
            warnings = result.Warnings;
            ShowCompiledSections(result.ScriptText, result.Messages);
            return true;
        }
        catch (LuaException ex)
        {
            ShowLuaError(ex.Message, ex.InGlobal);
            MoveCaretToLine(ex.InGlobal ? GlobalLuaTextBox : LuaTextBox, ex.InGlobal ? ex.Line - LuaException.GlobalLineBase : ex.Line);
            return false;
        }
        catch (Exception ex)
        {
            ShowLuaError(ex.Message, false);
            return false;
        }
    }

    private void ShowLuaError(string message, bool inGlobal)
    {
        ScriptSectionTabs.SelectedItem = LuaTab;
        ShowGlobalLua(inGlobal);
        LuaStatus.Text = message;
    }

    private void GlobalLuaToggle_Click(object? sender, RoutedEventArgs e) => ShowGlobalLua(GlobalLuaToggle.IsChecked == true);

    /// <summary>Switches the Lua tab's editor between the script file's Lua and global.lua.</summary>
    private void ShowGlobalLua(bool show)
    {
        GlobalLuaToggle.IsChecked = show;
        GlobalLuaScroller.IsVisible = show;
        LuaScroller.IsVisible = !show;
    }

    private Dictionary<int, string> scriptFileNames = [];

    /// <summary>"Script File 0012 - Rival intro": the file's number and the name global.lua gives it, if any.</summary>
    private string ScriptFileLabel(int id, string prefix = "Script File ") =>
        scriptFileNames.TryGetValue(id, out string? name) ? $"{prefix}{id:D4} - {name}" : $"{prefix}{id:D4}";

    private readonly Dictionary<int, (DateTime Written, Dictionary<int, string> Names)> subscriptNameCache = [];

    /// <summary>The name a Lua script file gives one of its numbered scripts (<c>script(3, "Rival intro", ...)</c>), if any.</summary>
    private string? SubscriptName(int fileId, int number)
    {
        if (currentProject is null) return null;
        string path = LuaScriptService.GetPath(currentProject, fileId);
        if (!File.Exists(path)) return null;
        DateTime written = File.GetLastWriteTimeUtc(path);
        if (!subscriptNameCache.TryGetValue(fileId, out var cached) || cached.Written != written)
        {
            cached = (written, LuaScriptService.ReadSubscriptNames(currentProject, fileId));
            subscriptNameCache[fileId] = cached;
        }

        return cached.Names.GetValueOrDefault(number);
    }

    private void RefreshScriptFileNames(string? global = null)
    {
        if (currentProject is null) return;
        ScriptLanguage language = GetScriptLanguage();
        headerIdCombosStale = true;
        scriptFileNames = global is null ? LuaScriptService.ReadScriptNames(currentProject, language) : LuaScriptCompiler.FindScriptNames(global, language);
        UpdateScriptNameHints();
    }

    /// <summary>Puts the names global.lua now gives into every list that shows script files, without
    /// changing which file is open.</summary>
    private void RelabelScriptFiles()
    {
        RunSwitchingDocument(() =>
        {
            int selected = ScriptFileSelector.SelectedIndex;
            for (int i = 0; i < scriptFileIds.Count; i++)
            {
                if (scriptFileIds[i] >= 0) ScriptFileSelector.Items[i] = ScriptFileLabel(scriptFileIds[i]);
            }

            ScriptFileSelector.SelectedIndex = selected;
        });
        RefreshResearchScriptNames();
    }

    /// <summary>Saves global.lua when it changed, then recompiles every other Lua script file with it, since
    /// their .script files depend on its helpers. Returns a note for the status line.</summary>
    private string SaveGlobalLuaIfChanged(int currentId)
    {
        if (currentProject is null || (GlobalLuaTextBox.Text ?? "") == savedGlobalLua) return "";
        string global = GlobalLuaTextBox.Text ?? "";
        LuaScriptService.SaveGlobal(currentProject, global);
        savedGlobalLua = global;
        RefreshScriptFileNames(global);
        RelabelScriptFiles();

        ScriptLanguage language = GetScriptLanguage();
        int recompiled = 0;
        var failed = new List<string>();
        foreach (string path in Directory.GetFiles(currentProject.ExpandedScriptsDirectory, "*.lua").Order(StringComparer.Ordinal))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out int id) || id == currentId) continue;
            try
            {
                TextArchiveData? archive = LoadScriptTextArchive((ushort)id, useOverride: false);
                LuaScriptService.SaveResult result = LuaScriptService.CompileForSave(language, File.ReadAllText(path), CurrentGlobalLua(),
                    archive?.Messages, FindMessageUse(id, archive, language));
                string text = result.ScriptText;
                if (archive is not null && result.Messages is not null && !archive.Messages.SequenceEqual(result.Messages))
                {
                    archive.Messages.Clear();
                    archive.Messages.AddRange(result.Messages);
                    TextArchiveService.Save(currentProject, archive);
                }
                ScriptFileService.Save(currentProject, id, text);
                recompiled++;
            }
            catch (Exception ex)
            {
                failed.Add($"{id:D4} ({ex.Message})");
                StatusLog.Warning("Scripts", $"{id:D4}.lua could not be recompiled with the new global.lua: {ex.Message}");
            }
        }

        string note = failed.Count == 0
            ? $"Saved global.lua and recompiled {recompiled} other Lua script file(s)."
            : $"Saved global.lua. {failed.Count} Lua script file(s) did not compile with it and keep their old .script: {string.Join(", ", failed.Take(3))}";
        LuaStatus.Text = note;
        return " " + note;
    }

    private static void MoveCaretToLine(TextBox box, int line)
    {
        string text = box.Text ?? "";
        int index = 0;
        for (int current = 1; current < line && index >= 0; current++)
        {
            index = text.IndexOf('\n', index);
            if (index >= 0) index++;
        }
        if (index < 0) index = text.Length;
        int end = text.IndexOf('\n', index);
        box.Focus();
        box.SelectionStart = index;
        box.SelectionEnd = end < 0 ? text.Length : end;
    }
}
