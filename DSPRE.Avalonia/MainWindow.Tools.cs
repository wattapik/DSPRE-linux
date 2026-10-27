using Avalonia;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using DSPRE.Core;
using System.Globalization;

namespace DSPRE.Avalonia;

/// <summary>The TOOLS section: the ROM Toolbox's patches, the general file utilities, and the
/// Building Editor's NSBMD texture tools.</summary>
public partial class MainWindow
{
    #region ROM Toolbox

    private static readonly IBrush PatchAppliedBrush = new SolidColorBrush(Color.Parse("#65D8C7"));
    private static readonly IBrush PatchUnavailableBrush = new SolidColorBrush(Color.Parse("#7A8896"));
    private static readonly IBrush PatchWarningBrush = new SolidColorBrush(Color.Parse("#E8B75F"));
    private static readonly IBrush PatchCardBrush = new SolidColorBrush(Color.Parse("#151C23"));
    private static readonly IBrush PatchCardBorderBrush = new SolidColorBrush(Color.Parse("#33414D"));
    private RomToolboxStatus? romToolboxStatus;

    private void RomToolboxRefresh_Click(object? sender, RoutedEventArgs e) => _ = RefreshRomToolboxAsync();

    private async Task RefreshRomToolboxAsync()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        RomToolboxStatus.Text = "Checking which patches are applied...";
        RomToolboxRows.Children.Clear();
        try
        {
            romToolboxStatus = await RomToolboxService.GetStatusAsync(currentProject, metadata, ToolsDirectory);
            RomToolboxStatus.Text = $"{romToolboxStatus.Patches.Count(patch => patch.Applied)} of {RomToolboxService.Patches.Count} patches applied to {metadata.GameCode}.";
        }
        catch (Exception ex)
        {
            LogUiError("ROM Toolbox could not check the patches", ex);
            RomToolboxStatus.Text = $"The patches could not be checked: {ex.Message}";
            romToolboxStatus = null;
        }

        foreach (RomPatch patch in RomToolboxService.Patches)
        {
            RomToolboxRows.Children.Add(BuildRomPatchCard(patch, romToolboxStatus?.Patches.FirstOrDefault(state => state.Id == patch.Id)));
        }
    }

    private static string ToolsDirectory => Path.Combine(AppContext.BaseDirectory, "Tools");

    private Border BuildRomPatchCard(RomPatch patch, RomPatchState? state)
    {
        var title = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 };
        title.Children.Add(new TextBlock { Text = patch.Title, FontWeight = FontWeight.SemiBold, FontSize = 15 });
        if (patch.Beta) title.Children.Add(new TextBlock { Text = "BETA - needs testing", Foreground = PatchWarningBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center });

        string stateText = state switch
        {
            null => "Status unknown.",
            { Applied: true } => "Applied.",
            { Available: true } => "Not applied.",
            _ => $"Unavailable: {state.Reason}"
        };
        var details = new StackPanel { Spacing = 6 };
        details.Children.Add(title);
        details.Children.Add(new TextBlock { Text = patch.Description, Foreground = Brushes.LightGray, FontSize = 12, TextWrapping = TextWrapping.Wrap });
        details.Children.Add(new TextBlock
        {
            Text = stateText,
            FontSize = 12,
            Foreground = state is { Applied: true } ? PatchAppliedBrush : state is { Available: true } ? Brushes.White : PatchUnavailableBrush
        });

        var apply = new Button { Content = "Apply Patch", Padding = new Thickness(14, 7), VerticalAlignment = VerticalAlignment.Top, IsEnabled = state is { Available: true } };
        TextBox? offsetInput = null;
        if (patch.AtSyntheticOverlayOffset && state is { Available: true } && romToolboxStatus?.Defaults.TryGetValue(patch.Id, out (uint Offset, int Length) placement) == true)
        {
            // Legacy asked for this offset in its own dialog; here it sits on the card.
            offsetInput = new TextBox { Text = placement.Offset.ToString("X"), Width = 110 };
            var rangeStatus = new TextBlock { FontSize = 11, VerticalAlignment = VerticalAlignment.Center, TextWrapping = TextWrapping.Wrap };
            void UpdateRange()
            {
                (bool usable, string text) = uint.TryParse(offsetInput.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint offset)
                    ? RomToolboxService.DescribeSyntheticOverlayRange(romToolboxStatus.SyntheticOverlayPath, offset, placement.Length)
                    : (false, "Enter a hexadecimal offset.");
                rangeStatus.Text = text;
                rangeStatus.Foreground = !usable ? Brushes.IndianRed : text.Contains("overwrite", StringComparison.Ordinal) ? PatchWarningBrush : PatchAppliedBrush;
                apply.IsEnabled = usable;
            }

            offsetInput.TextChanged += (_, _) => UpdateRange();
            var row = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8 };
            row.Children.Add(new TextBlock { Text = $"Synthetic overlay offset (hex, {placement.Length} bytes)", FontSize = 12, VerticalAlignment = VerticalAlignment.Center });
            row.Children.Add(offsetInput);
            row.Children.Add(rangeStatus);
            details.Children.Add(row);
            UpdateRange();
        }

        apply.Click += async (_, _) => await ApplyRomPatchAsync(patch, offsetInput);
        var layout = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), ColumnSpacing = 16 };
        layout.Children.Add(details);
        Grid.SetColumn(apply, 1);
        layout.Children.Add(apply);
        return new Border { Background = PatchCardBrush, BorderBrush = PatchCardBorderBrush, BorderThickness = new Thickness(1), Padding = new Thickness(12), Child = layout };
    }

    private async Task ApplyRomPatchAsync(RomPatch patch, TextBox? offsetInput)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        uint? offset = null;
        if (offsetInput is not null)
        {
            if (!uint.TryParse(offsetInput.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out uint parsed)) return;
            offset = parsed;
        }

        string where = offset is uint at ? $"\n\nIt is written to the synthetic overlay at offset 0x{at:X}." : string.Empty;
        string? choice = await new ChoiceDialog(
            $"Apply {patch.Title}?",
            $"{patch.Description}{where}\n\nThis changes the project's game files directly, and the project is opened again afterwards.",
            ("apply", "Apply Patch"), ("cancel", "Cancel")).ShowDialog<string?>(this);
        if (choice != "apply") return;
        if (!await ConfirmLeavingProjectAsync("applying the patch")) return;

        BeginBlockingLoad($"Applying {patch.Title}...");
        try
        {
            RomToolboxResult result = await RomToolboxService.ApplyAsync(currentProject, metadata, ToolsDirectory, patch.Id, offset);
            if (!result.Succeeded)
            {
                RomToolboxStatus.Text = $"{patch.Title} was not applied: {result.Message}";
                return;
            }

            // Patches change data the editors have already read (text, scripts, events, headers, area
            // data), so the project is opened again rather than patching each editor's copy.
            ReportLoadProgress("Reloading the project...", null);
            await PrepareAndShowProjectAsync(currentProject);
            SelectEditor("ROM Toolbox");
            await RefreshRomToolboxAsync();
            RomToolboxStatus.Text = $"{patch.Title} applied." + (result.Message.Length > 0 ? $" {result.Message}" : string.Empty);
        }
        catch (Exception ex)
        {
            LogUiError($"{patch.Title} could not be applied", ex);
            RomToolboxStatus.Text = $"{patch.Title} could not be applied: {ex.Message}";
        }
        finally
        {
            EndBlockingLoad();
        }
    }

    #endregion

    #region Map data clean-up

    private async void CompressMapData_Click(object? sender, RoutedEventArgs e) => await CleanUpMapDataAsync(merge: true, remove: false);

    private async void RemoveUnusedMapData_Click(object? sender, RoutedEventArgs e) => await CleanUpMapDataAsync(merge: false, remove: true);

    private async Task CleanUpMapDataAsync(bool merge, bool remove)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        string title = merge ? "Compress map data" : "Remove unused map data";
        MapDataCleanupService.Report plan;
        try
        {
            plan = await Task.Run(() => MapDataCleanupService.Analyze(currentProject, metadata, merge, remove));
        }
        catch (Exception ex)
        {
            LogUiError($"{title} could not be checked", ex);
            RomToolboxStatus.Text = $"{title}: {ex.Message}";
            return;
        }

        if (plan.Removed == 0)
        {
            RomToolboxStatus.Text = merge ? "No map data is a duplicate of other map data." : "All map data is in use.";
            return;
        }

        string lines = string.Join("\n", plan.Kinds.Where(kind => kind.Removed > 0).Select(kind =>
            $"  {kind.Kind}: {kind.Removed} of {kind.Total} removed" + (merge && kind.Unused > 0 ? $" ({kind.Merged} duplicates, {kind.Unused} unused)" : string.Empty)));
        string what = merge
            ? "Identical entries are merged into the first copy of each, and the extra copies are removed."
            : "Entries no header uses (through its matrix and area data) are removed.";
        string? choice = await new ChoiceDialog(
            $"{title}?",
            $"{what}\n\n{lines}\n\nWhat is left is renumbered, and every header, matrix and area data is rewritten to match. The game's own code is not looked at: anything it refers to by a fixed number would now point at something else. This cannot be undone from the editors (the edit journal records it, and replaying it does the same again). The project is opened again afterwards.",
            ("apply", "Apply"), ("cancel", "Cancel")).ShowDialog<string?>(this);
        if (choice != "apply") return;
        if (!await ConfirmLeavingProjectAsync("cleaning up map data")) return;

        BeginBlockingLoad($"{title}...");
        try
        {
            MapDataCleanupService.Report done = await Task.Run(() => MapDataCleanupService.Apply(currentProject, metadata, merge, remove));
            ReportLoadProgress("Reloading the project...", null);
            await PrepareAndShowProjectAsync(currentProject);
            SelectEditor("ROM Toolbox");
            await RefreshRomToolboxAsync();
            RomToolboxStatus.Text = $"{title}: {done.Removed} entries removed.";
        }
        catch (Exception ex)
        {
            LogUiError($"{title} failed", ex);
            RomToolboxStatus.Text = $"{title} failed: {ex.Message}. The project may be half changed; reopen it to check.";
        }
        finally
        {
            EndBlockingLoad();
        }
    }

    #endregion

    #region Utilities

    private async Task<string?> PickFolderAsync(string title)
    {
        IReadOnlyList<IStorageFolder> folders = await StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions { Title = title, AllowMultiple = false });
        return folders.Count == 1 ? folders[0].Path.LocalPath : null;
    }

    private async Task<string?> PickFileAsync(string title, string typeName, params string[] patterns)
    {
        IReadOnlyList<IStorageFile> files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            SuggestedStartLocation = await ImportStartFolderAsync(),
            FileTypeFilter = [new FilePickerFileType(typeName) { Patterns = patterns }, FilePickerFileTypes.All]
        });
        return files.Count == 1 ? files[0].Path.LocalPath : null;
    }

    private async Task<string?> PickSavePathAsync(string title, string suggestedName, string typeName, params string[] patterns)
    {
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = title,
            SuggestedFileName = suggestedName,
            FileTypeChoices = [new FilePickerFileType(typeName) { Patterns = patterns }]
        });
        return file?.Path.LocalPath;
    }

    private async Task<bool> ConfirmAsync(string title, string message, string confirmLabel) =>
        await new ChoiceDialog(title, message, ("yes", confirmLabel), ("cancel", "Cancel")).ShowDialog<string?>(this) == "yes";

    private async void UtilityUnpackNarc_Click(object? sender, RoutedEventArgs e)
    {
        string? narc = await PickFileAsync("Pick a NARC to unpack", "NARC", "*.narc");
        if (narc is null) return;
        string? destination = await PickFolderAsync("Pick where to unpack it (a folder named after the NARC is made there)");
        if (destination is null) return;

        try
        {
            string folder = Path.Combine(destination, Path.GetFileNameWithoutExtension(narc));
            if (Directory.Exists(folder) && Directory.EnumerateFileSystemEntries(folder).Any() &&
                !await ConfirmAsync("Folder exists", $"{folder} already has files in it. Unpacked files with the same names replace them.", "Unpack Anyway"))
            {
                return;
            }

            int count = NarcExtractor.Extract(narc, folder);
            string renamed = string.Empty;
            if (UtilityRenameAfterUnpackCheck.IsChecked == true)
            {
                renamed = $", then renamed {FileUtilities.RenameByContent(folder).Count} by content";
            }

            UtilitiesStatus.Text = $"Unpacked {count} files from {Path.GetFileName(narc)} into {folder}{renamed}.";
        }
        catch (Exception ex)
        {
            LogUiError("NARC could not be unpacked", ex);
            UtilitiesStatus.Text = $"The NARC could not be unpacked: {ex.Message}";
        }
    }

    private async void UtilityPackNarc_Click(object? sender, RoutedEventArgs e)
    {
        string? folder = await PickFolderAsync("Pick the folder to pack");
        if (folder is null) return;
        string? output = await PickSavePathAsync("Save the NARC as", Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) + ".narc", "NARC", "*.narc");
        if (output is null) return;

        try
        {
            NarcPacker.Pack(folder, output);
            UtilitiesStatus.Text = $"Packed {Directory.GetFiles(folder, "*", SearchOption.AllDirectories).Length} files from {folder} into {output}.";
        }
        catch (Exception ex)
        {
            LogUiError("NARC could not be packed", ex);
            UtilitiesStatus.Text = $"The folder could not be packed: {ex.Message}";
        }
    }

    private async void UtilityBuildList_Click(object? sender, RoutedEventArgs e)
    {
        string? folder = await PickFolderAsync("Pick the folder to list");
        if (folder is null) return;
        if (!Directory.EnumerateFiles(folder).Any())
        {
            UtilitiesStatus.Text = $"{folder} has no files to list.";
            return;
        }

        string? output = await PickSavePathAsync("Save the list as", Path.GetFileName(Path.TrimEndingDirectorySeparator(folder)) + ".list", "List file", "*.list", "*.txt");
        if (output is null) return;
        try
        {
            FileUtilities.BuildListFromFolder(folder, output);
            UtilitiesStatus.Text = $"Listed {Directory.GetFiles(folder).Length} files of {folder} in {output}.";
        }
        catch (Exception ex)
        {
            LogUiError("List file could not be written", ex);
            UtilitiesStatus.Text = $"The list could not be written: {ex.Message}";
        }
    }

    private async void UtilityBuildListFromEnum_Click(object? sender, RoutedEventArgs e)
    {
        string? enumPath = await PickFileAsync("Pick a C file with an enum (one entry per line)", "C source or header", "*.h", "*.c", "*.txt");
        if (enumPath is null) return;
        string? output = await PickSavePathAsync("Save the list as", Path.GetFileNameWithoutExtension(enumPath) + ".list", "List file", "*.list", "*.txt");
        if (output is null) return;
        try
        {
            int entries = FileUtilities.BuildListFromEnum(enumPath, output);
            UtilitiesStatus.Text = $"Wrote {entries} names from {Path.GetFileName(enumPath)} to {output}.";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException or FormatException or UnauthorizedAccessException)
        {
            UtilitiesStatus.Text = $"The enum could not be read, so no list was written: {ex.Message}";
        }
    }

    private async void UtilityRenameFromList_Click(object? sender, RoutedEventArgs e)
    {
        string? folder = await PickFolderAsync("Pick the folder whose files to rename");
        if (folder is null) return;
        string? listPath = await PickFileAsync("Pick the list file", "List file", "*.list", "*.txt");
        if (listPath is null) return;

        try
        {
            List<string> names = FileUtilities.ReadList(listPath);
            int files = FileUtilities.NaturalOrderFiles(folder).Count;
            if (names.Count == 0 || files == 0)
            {
                UtilitiesStatus.Text = names.Count == 0 ? "The list has no names in it (only blank or # comment lines)." : $"{folder} has no files.";
                return;
            }

            int renamed = Math.Min(names.Count, files);
            string note = names.Count > files ? $"\n\nThe list has {names.Count} names but the folder only {files} files; the extra names are not used."
                : names.Count < files ? $"\n\nThe list only names {names.Count} of the {files} files; the other {files - names.Count} " +
                    (UtilityIsolateUnnamedCheck.IsChecked == true ? $"are moved into {FileUtilities.IsolatedFolderName}." : "keep their names.")
                : string.Empty;
            if (!await ConfirmAsync("Rename from list", $"Rename {renamed} files of {folder}, in natural order, to the names in {Path.GetFileName(listPath)}?{note}", "Rename")) return;

            FileUtilities.RenameResult result = FileUtilities.RenameFromList(folder, names, UtilityIsolateUnnamedCheck.IsChecked == true);
            UtilitiesStatus.Text = $"Renamed {result.Renamed} files." +
                (result.Isolated > 0 ? $" Moved {result.Isolated} unnamed files into {FileUtilities.IsolatedFolderName}." : result.Unnamed > 0 ? $" {result.Unnamed} files kept their names." : string.Empty);
        }
        catch (Exception ex)
        {
            LogUiError("Files could not be renamed", ex);
            UtilitiesStatus.Text = $"The files could not be renamed: {ex.Message}";
        }
    }

    private async void UtilityRenameByContent_Click(object? sender, RoutedEventArgs e)
    {
        string? folder = await PickFolderAsync("Pick the folder whose files to rename");
        if (folder is null) return;
        int files = FileUtilities.NaturalOrderFiles(folder).Count;
        if (files == 0)
        {
            UtilitiesStatus.Text = $"{folder} has no files.";
            return;
        }

        if (!await ConfirmAsync("Rename by content", $"Rename the {files} files of {folder} after what they contain?", "Rename")) return;
        try
        {
            List<string> renamed = FileUtilities.RenameByContent(folder);
            if (renamed.Count == 0)
            {
                UtilitiesStatus.Text = "No file's contents were recognised; nothing was renamed.";
                return;
            }

            string listNote = string.Empty;
            if (UtilitySaveContentListCheck.IsChecked == true)
            {
                string folderName = Path.GetFileName(Path.TrimEndingDirectorySeparator(folder));
                string? listPath = await PickSavePathAsync("Save the list of new names as", folderName + ".list", "List file", "*.list", "*.txt");
                if (listPath is not null)
                {
                    File.WriteAllLines(listPath, FileUtilities.ListHeader($"File enumeration definition for folder \"{folderName}\"").Concat(renamed));
                    listNote = $" The new names are listed in {listPath}.";
                }
            }

            UtilitiesStatus.Text = $"Renamed {renamed.Count} of {files} files by content.{listNote}";
        }
        catch (Exception ex)
        {
            LogUiError("Files could not be renamed", ex);
            UtilitiesStatus.Text = $"The files could not be renamed: {ex.Message}";
        }
    }

    #endregion

    #region Journal reader

    private static readonly IBrush JournalCommentBrush = new SolidColorBrush(Color.Parse("#7A8896"));
    private static readonly IBrush JournalCallBrush = new SolidColorBrush(Color.Parse("#EDF2F7"));

    private void JournalReaderReload_Click(object? sender, RoutedEventArgs e) => LoadJournalReader();

    /// <summary>Shows the open project's journal as it is on disk. Read with sharing, since the journal
    /// stays open for appending while the project is.</summary>
    private void LoadJournalReader()
    {
        JournalReaderLines.ItemsSource = null;
        if (currentProject is null) return;

        string path = EditJournal.Active?.FilePath ?? EditJournal.GetDefaultPath(currentProject, null);
        if (!File.Exists(path))
        {
            JournalReaderInfo.Text = $"No journal yet: {path} is made with the first edit.";
            return;
        }

        try
        {
            var lines = new List<JournalLine>();
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
            using (var reader = new StreamReader(stream))
            {
                for (string? text = reader.ReadLine(); text is not null; text = reader.ReadLine())
                {
                    lines.Add(new JournalLine(lines.Count + 1, text, text.TrimStart().StartsWith('#') ? JournalCommentBrush : JournalCallBrush));
                }
            }

            JournalReaderLines.ItemsSource = lines;
            // BaseRom(...) only names the ROM the journal applies to; it is not an edit.
            int edits = lines.Count(line => line.Text.Length > 0 && !line.Text.TrimStart().StartsWith('#') && !line.Text.StartsWith("BaseRom(", StringComparison.Ordinal));
            JournalReaderInfo.Text = $"{path} - {lines.Count} lines, {edits} edits, last written {File.GetLastWriteTime(path):g}.";
            if (lines.Count > 0) JournalReaderLines.ScrollIntoView(lines[^1]);
        }
        catch (IOException ex)
        {
            JournalReaderInfo.Text = $"The journal could not be read: {ex.Message}";
        }
    }

    #endregion

    #region NSBMD texture tools

    private const string NsbmdPattern = "*.nsbmd";

    private async Task<byte[]?> PickNsbmdAsync(string title)
    {
        string? path = await PickFileAsync(title, "NSBMD model", NsbmdPattern);
        if (path is null) return null;
        byte[] data = await File.ReadAllBytesAsync(path);
        if (!NsbmdTextureTools.IsNsbmd(data))
        {
            NsbmdToolsStatus.Text = $"{Path.GetFileName(path)} is not an NSBMD model.";
            return null;
        }

        lastNsbmdName = Path.GetFileNameWithoutExtension(path);
        return data;
    }

    private string lastNsbmdName = "model";

    private async void NsbmdAddTextures_Click(object? sender, RoutedEventArgs e)
    {
        byte[]? model = await PickNsbmdAsync("Pick the model to add textures to");
        if (model is null) return;
        if (NsbmdTextureTools.HasTextures(model) &&
            !await ConfirmAsync("Model is already textured", "This model already carries textures. Replace them?", "Replace")) return;

        string? texturePath = await PickFileAsync("Pick the NSBTX textures", "NSBTX textures", "*.nsbtx");
        if (texturePath is null) return;
        try
        {
            byte[] textured = NsbmdTextureTools.WithTextures(model, await File.ReadAllBytesAsync(texturePath));
            string baseName = lastNsbmdName.EndsWith("_untextured", StringComparison.Ordinal) ? lastNsbmdName[..^"_untextured".Length] : lastNsbmdName;
            string? output = await PickSavePathAsync("Save the textured model as", baseName + "_textured.nsbmd", "NSBMD model", NsbmdPattern);
            if (output is null) return;
            await File.WriteAllBytesAsync(output, textured);
            NsbmdToolsStatus.Text = $"Saved {Path.GetFileName(output)} with the textures of {Path.GetFileName(texturePath)}.";
        }
        catch (Exception ex)
        {
            LogUiError("Textures could not be added", ex);
            NsbmdToolsStatus.Text = ex.Message;
        }
    }

    private async void NsbmdRemoveTextures_Click(object? sender, RoutedEventArgs e)
    {
        byte[]? model = await PickNsbmdAsync("Pick the model to remove textures from");
        if (model is null) return;
        if (!NsbmdTextureTools.HasTextures(model))
        {
            NsbmdToolsStatus.Text = "This model has no textures to remove.";
            return;
        }

        try
        {
            string texturesNote = string.Empty;
            if (NsbmdSaveRemovedTexturesCheck.IsChecked == true &&
                await PickSavePathAsync("Save the removed textures as", lastNsbmdName + ".nsbtx", "NSBTX textures", "*.nsbtx") is string texturePath)
            {
                await File.WriteAllBytesAsync(texturePath, NsbmdTextureTools.ExtractTextures(model)!);
                texturesNote = $" Its textures were saved to {Path.GetFileName(texturePath)}.";
            }

            string? output = await PickSavePathAsync("Save the untextured model as", lastNsbmdName + "_untextured.nsbmd", "NSBMD model", NsbmdPattern);
            if (output is null) return;
            await File.WriteAllBytesAsync(output, NsbmdTextureTools.WithoutTextures(model));
            NsbmdToolsStatus.Text = $"Saved {Path.GetFileName(output)} without textures.{texturesNote}";
        }
        catch (Exception ex)
        {
            LogUiError("Textures could not be removed", ex);
            NsbmdToolsStatus.Text = ex.Message;
        }
    }

    private async void NsbmdExtractTextures_Click(object? sender, RoutedEventArgs e)
    {
        byte[]? model = await PickNsbmdAsync("Pick the model to take textures from");
        if (model is null) return;
        byte[]? textures = NsbmdTextureTools.ExtractTextures(model);
        if (textures is null)
        {
            NsbmdToolsStatus.Text = "This model has no textures of its own.";
            return;
        }

        string? output = await PickSavePathAsync("Save the textures as", lastNsbmdName + ".nsbtx", "NSBTX textures", "*.nsbtx");
        if (output is null) return;
        await File.WriteAllBytesAsync(output, textures);
        NsbmdToolsStatus.Text = $"Saved the textures of {lastNsbmdName} to {Path.GetFileName(output)}.";
    }

    #endregion
}

/// <summary>One line of the edit journal as the Journal Reader shows it.</summary>
public sealed record JournalLine(int Number, string Text, IBrush Brush);
