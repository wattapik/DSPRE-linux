using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Pokémon Editor's Pokédex tab: the species' name and dex text, and the regional dex
/// order - see PokedexService.</summary>
public partial class MainWindow
{
    private PokedexText? pokeDexText;
    private string[] pokeDexNames = [];
    private List<int> pokeDexOrder = [];
    private List<int> pokeDexSavedOrder = [];
    private string pokeDexRegion = "Regional";
    private int pokeDexNationalCount;

    private void ConfigurePokedexTab(RomGameMetadata metadata)
    {
        if (currentProject is null) return;
        pokeDexNames = TextArchiveService.Load(currentProject, TrainerService.GetPokemonNamesArchiveId(metadata), metadata).Messages.ToArray();
        try
        {
            PokedexService.RegionalDex dex = PokedexService.LoadRegionalDex(currentProject, metadata);
            pokeDexRegion = dex.Region;
            pokeDexOrder = dex.Order;
            pokeDexSavedOrder = [.. dex.Order];
            pokeDexNationalCount = dex.NationalCount;
            PokeDexOrderStatus.Text = "";
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            pokeDexOrder = [];
            pokeDexSavedOrder = [];
            pokeDexNationalCount = 0;
            PokeDexOrderStatus.Text = $"The regional dex could not be read: {ex.Message}";
        }

        PokeDexMoveToInput.Maximum = Math.Max(1, pokeDexOrder.Count);
        SetItems(PokeDexReplaceSelector, Enumerable.Range(1, Math.Max(0, Math.Min(pokeDexNationalCount, pokeDexNames.Length - 1))).Select(i => $"{i:D3} {pokeDexNames[i]}"));
        RefreshPokeDexOrderList();
    }

    private string PokeDexSlotLabel(int index) =>
        $"#{index + 1:D3}  {(pokeDexOrder[index] < pokeDexNames.Length ? pokeDexNames[pokeDexOrder[index]] : $"Species {pokeDexOrder[index]}")}";

    private void RefreshPokeDexOrderList(int select = -1)
    {
        int selected = select >= 0 ? select : PokeDexOrderList.SelectedIndex;
        PokeDexOrderList.ItemsSource = Enumerable.Range(0, pokeDexOrder.Count).Select(PokeDexSlotLabel).ToList();
        if (selected >= 0 && selected < pokeDexOrder.Count)
        {
            PokeDexOrderList.SelectedIndex = selected;
            PokeDexOrderList.ScrollIntoView(selected);
        }

        bool unsaved = !pokeDexOrder.SequenceEqual(pokeDexSavedOrder);
        PokeDexOrderTitle.Text = $"{pokeDexRegion} Pokédex Order ({pokeDexOrder.Count} Pokémon){(unsaved ? " - not saved yet" : "")}";
    }

    private void LoadPokedexText(int species, RomGameMetadata metadata)
    {
        if (currentProject is null) return;
        try
        {
            pokeDexText = PokedexService.LoadText(currentProject, metadata, species);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            pokeDexText = null;
            PokeDexTextStatus.Text = $"The text could not be read: {ex.Message}";
            return;
        }

        ShowPokedexText();
        int slot = pokeDexOrder.IndexOf(species);
        PokeDexTextStatus.Text = slot >= 0 ? $"{pokeDexRegion} dex number #{slot + 1:D3}." : $"Not in the {pokeDexRegion} dex.";
        if (slot >= 0) RefreshPokeDexOrderList(slot);
        RefreshCrySettings();
    }

    private void ShowPokedexText()
    {
        PokeDexNameBox.Text = pokeDexText?.Name ?? "";
        PokeDexCategoryBox.Text = pokeDexText?.Category ?? "";
        PokeDexEntryBox.Text = pokeDexText?.Entry ?? "";
        PokeDexCategoryBox.IsEnabled = pokeDexText?.Category is not null;
        PokeDexEntryBox.IsEnabled = pokeDexText?.Entry is not null;
    }

    /// <summary>Whether the name, category or entry boxes differ from the species' saved Pokédex text.</summary>
    private bool PokedexTextUnsaved() =>
        pokeDexText is not null &&
        ((PokeDexNameBox.Text ?? "") != pokeDexText.Name
         || (pokeDexText.Category is not null && (PokeDexCategoryBox.Text ?? "") != pokeDexText.Category)
         || (pokeDexText.Entry is not null && (PokeDexEntryBox.Text ?? "") != pokeDexText.Entry));

    private void RevertPokedexText_Click(object? sender, RoutedEventArgs e) => ShowPokedexText();

    private void SavePokedexText_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || pokeDexText is null || pokeEditorSpeciesId < 0 || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        int species = pokeEditorSpeciesId;
        string oldName = pokeDexText.Name;
        var text = new PokedexText(PokeDexNameBox.Text ?? "",
            pokeDexText.Category is null ? null : PokeDexCategoryBox.Text ?? "",
            pokeDexText.Entry is null ? null : PokeDexEntryBox.Text ?? "");
        string scriptNote = "";
        try
        {
            PokedexService.SaveText(currentProject, metadata, species, text, names => scriptNote = SaveTextArchiveKeepingScripts(names));
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            PokeDexTextStatus.Text = $"Not saved: {ex.Message}";
            return;
        }

        pokeDexText = PokedexService.LoadText(currentProject, metadata, species);
        ShowPokedexText();
        if (pokeDexText.Name != oldName) RenameSpeciesInLists(species, oldName, pokeDexText.Name);
        PokeDexTextStatus.Text = $"Saved {pokeDexText.Name}'s name and Pokédex text.{scriptNote} It reaches the ROM with Save ROM.";
    }

    /// <summary>Updates the open editor's lists after a rename, without reloading the species (which
    /// would drop unsaved edits in the other tabs).</summary>
    private void RenameSpeciesInLists(int species, string oldName, string newName)
    {
        if (species < pokeDexNames.Length) pokeDexNames[species] = newName;
        bool wasLoading = loadingPokeEditor;
        loadingPokeEditor = true;
        try
        {
            for (int i = 0; i < PokeEditorSpeciesNames.Count; i++)
            {
                string label = PokeEditorSpeciesNames[i];
                string renamed = i == species ? newName
                    : i >= pokeBaseSpeciesCount && label.StartsWith(oldName + " - ", StringComparison.Ordinal) ? newName + label[oldName.Length..]
                    : label;
                if (renamed == label) continue;
                PokeEditorSpeciesNames[i] = renamed;
                ReplaceItem(PokeEditorSpeciesSelector, i, renamed);
                ReplaceItem(PokeHatchResultSelector, i, renamed);
            }

            if (species - 1 < PokeDexReplaceSelector.Items.Count) ReplaceItem(PokeDexReplaceSelector, species - 1, $"{species:D3} {newName}");
        }
        finally
        {
            loadingPokeEditor = wasLoading;
        }

        RefreshPokeDexOrderList();
    }

    private static void ReplaceItem(ComboBox box, int index, string value)
    {
        if (index < 0 || index >= box.Items.Count) return;
        int selected = box.SelectedIndex;
        box.Items[index] = value;
        box.SelectedIndex = selected;
    }

    private void PokeDexOrderList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PokeDexOrderList.SelectedIndex >= 0) PokeDexMoveToInput.Value = PokeDexOrderList.SelectedIndex + 1;
    }

    private bool TrySelectedDexSlot(out int slot)
    {
        slot = PokeDexOrderList.SelectedIndex;
        if (slot >= 0 && slot < pokeDexOrder.Count) return true;
        PokeDexOrderStatus.Text = "Select a Pokémon in the list first.";
        return false;
    }

    private void MovePokeDexSlot(int from, int to)
    {
        to = Math.Clamp(to, 0, pokeDexOrder.Count - 1);
        if (from == to) return;
        int species = pokeDexOrder[from];
        pokeDexOrder.RemoveAt(from);
        pokeDexOrder.Insert(to, species);
        RefreshPokeDexOrderList(to);
        PokeDexOrderStatus.Text = $"Moved {pokeDexNames.ElementAtOrDefault(species)} from #{from + 1:D3} to #{to + 1:D3}. Not saved yet.";
    }

    private void PokeDexMoveUp_Click(object? sender, RoutedEventArgs e)
    {
        if (TrySelectedDexSlot(out int slot)) MovePokeDexSlot(slot, slot - 1);
    }

    private void PokeDexMoveDown_Click(object? sender, RoutedEventArgs e)
    {
        if (TrySelectedDexSlot(out int slot)) MovePokeDexSlot(slot, slot + 1);
    }

    private void PokeDexMoveTo_Click(object? sender, RoutedEventArgs e)
    {
        if (TrySelectedDexSlot(out int slot)) MovePokeDexSlot(slot, (int)(PokeDexMoveToInput.Value ?? 1) - 1);
    }

    private void PokeDexPutInSlot_Click(object? sender, RoutedEventArgs e)
    {
        if (!TrySelectedDexSlot(out int slot)) return;
        if (PokeDexReplaceSelector.SelectedIndex < 0)
        {
            PokeDexOrderStatus.Text = "Choose the Pokémon to put in the slot first.";
            return;
        }

        int species = PokeDexReplaceSelector.SelectedIndex + 1;
        int replaced = pokeDexOrder[slot];
        if (species == replaced) return;
        int existing = pokeDexOrder.IndexOf(species);
        pokeDexOrder[slot] = species;
        if (existing >= 0)
        {
            pokeDexOrder[existing] = replaced;
            PokeDexOrderStatus.Text = $"Swapped {pokeDexNames.ElementAtOrDefault(species)} (now #{slot + 1:D3}) and {pokeDexNames.ElementAtOrDefault(replaced)} (now #{existing + 1:D3}). Not saved yet.";
        }
        else
        {
            PokeDexOrderStatus.Text = $"{pokeDexNames.ElementAtOrDefault(species)} is now #{slot + 1:D3}; {pokeDexNames.ElementAtOrDefault(replaced)} is no longer in the {pokeDexRegion} dex. Not saved yet.";
        }

        RefreshPokeDexOrderList(slot);
    }

    private async void PokeDexOpenSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (!TrySelectedDexSlot(out int slot)) return;
        await OpenPokemonSpeciesAsync(pokeDexOrder[slot]);
    }

    private void RevertPokeDexOrder_Click(object? sender, RoutedEventArgs e)
    {
        pokeDexOrder = [.. pokeDexSavedOrder];
        RefreshPokeDexOrderList();
        PokeDexOrderStatus.Text = "Reverted to the saved order.";
    }

    private void SavePokeDexOrder_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || pokeDexOrder.Count == 0 || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        try
        {
            PokedexService.SaveRegionalDex(currentProject, metadata, pokeDexOrder);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            PokeDexOrderStatus.Text = $"Not saved: {ex.Message}";
            return;
        }

        pokeDexSavedOrder = [.. pokeDexOrder];
        RefreshPokeDexOrderList();
        PokeDexOrderStatus.Text = $"Saved the {pokeDexRegion} dex order. It reaches the ROM with Save ROM.";
    }

    private bool loadingCrySettings;

    // Cry changes made here wait in this table until they are saved (Save All, or the Save ROM prompt), by species.
    private readonly Dictionary<int, PokemonCryService.Pending> pendingCries = [];

    /// <summary>The sound archive with the selected species' pending cry change applied, as it will be once saved.</summary>
    private byte[]? EffectiveSoundArchive(int species, out string? problem)
    {
        problem = null;
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null || PokemonCryService.FindSoundArchive(currentProject, metadata) is not { } path) return null;
        try
        {
            byte[] sdat = File.ReadAllBytes(path);
            return pendingCries.TryGetValue(species, out PokemonCryService.Pending? pending) ? PokemonCryService.Preview(sdat, species, pending) : sdat;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or IndexOutOfRangeException or ArgumentException)
        {
            problem = ex.Message;
            return null;
        }
    }

    private void RefreshCrySettings()
    {
        loadingCrySettings = true;
        try
        {
            PokeCryDiscardButton.IsEnabled = pokeEditorSpeciesId > 0 && pendingCries.ContainsKey(pokeEditorSpeciesId);
            if (currentProject is null || pokeEditorSpeciesId <= 0 || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null || !PokemonCryService.IsSupported(currentProject, metadata))
            {
                PokeCryInfo.Text = "This game has no sound archive.";
                return;
            }

            byte[]? sdat = EffectiveSoundArchive(pokeEditorSpeciesId, out string? problem);
            if (sdat is null)
            {
                PokeCryInfo.Text = $"This species' cry cannot be read: {problem}";
                return;
            }

            try
            {
                PokemonCryService.Settings settings = PokemonCryService.ReadSettings(sdat, pokeEditorSpeciesId);
                PokeCryNote.Value = settings.Note;
                PokeCryAttack.Value = settings.Attack;
                PokeCryDecay.Value = settings.Decay;
                PokeCrySustain.Value = settings.Sustain;
                PokeCryRelease.Value = settings.Release;
                PokeCryPan.Value = settings.Pan;
                PokeCryRate.Value = settings.SampleRate;
                PokeCryLoops.IsChecked = settings.Loops;
                PokeCryLoopStart.Value = settings.LoopStart;
                string format = settings.Format switch { 0 => "8-bit", 1 => "16-bit", _ => "ADPCM" };
                PokeCryInfo.Text = $"{format}, {settings.Samples:N0} samples, {settings.Seconds:0.00} s; {settings.Bytes:N0} of the {settings.Budget:N0} bytes this cry has room for."
                    + (PokeCryDiscardButton.IsEnabled ? " Changes not saved yet." : string.Empty);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or IndexOutOfRangeException or ArgumentException)
            {
                PokeCryInfo.Text = $"This species has no cry that can be edited: {ex.Message}";
            }
        }
        finally
        {
            loadingCrySettings = false;
        }
    }

    private void ReloadCrySettings_Click(object? sender, RoutedEventArgs e) => RefreshCrySettings();

    private void DiscardCryChanges_Click(object? sender, RoutedEventArgs e)
    {
        if (pokeEditorSpeciesId <= 0 || !pendingCries.Remove(pokeEditorSpeciesId)) return;
        RefreshCrySettings();
        RefreshUnsavedMarkers();
        PokeDexTextStatus.Text = $"The unsaved cry changes of species {pokeEditorSpeciesId:D3} were dropped.";
    }

    private void ApplyCrySettings_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || pokeEditorSpeciesId <= 0) return;
        int species = pokeEditorSpeciesId;
        byte[]? sdat = EffectiveSoundArchive(species, out string? problem);
        if (sdat is null)
        {
            PokeDexTextStatus.Text = $"The cry settings could not be changed: {problem}";
            return;
        }

        try
        {
            PokemonCryService.Settings current = PokemonCryService.ReadSettings(sdat, species);
            PokemonCryService.Settings changed = current with
            {
                Note = (int)(PokeCryNote.Value ?? 60),
                Attack = (int)(PokeCryAttack.Value ?? 127),
                Decay = (int)(PokeCryDecay.Value ?? 127),
                Sustain = (int)(PokeCrySustain.Value ?? 127),
                Release = (int)(PokeCryRelease.Value ?? 127),
                Pan = (int)(PokeCryPan.Value ?? 64),
                SampleRate = (int)(PokeCryRate.Value ?? current.SampleRate),
                Loops = PokeCryLoops.IsChecked == true,
                LoopStart = (int)(PokeCryLoopStart.Value ?? 0)
            };
            pendingCries.TryGetValue(species, out PokemonCryService.Pending? pending);
            pendingCries[species] = (pending ?? new PokemonCryService.Pending(null, null, null)) with { Settings = changed };
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or IndexOutOfRangeException or ArgumentException)
        {
            LogUiError("Cry settings could not be changed", ex);
            PokeDexTextStatus.Text = $"The cry settings could not be changed: {ex.Message}";
            return;
        }

        RefreshCrySettings();
        RefreshUnsavedMarkers();
        PokeDexTextStatus.Text = $"Cry settings of species {pokeEditorSpeciesId:D3} changed; Save All writes them to the sound archive.";
    }

    private async Task<PokemonCryService.Cry?> RenderSelectedCryAsync()
    {
        if (currentProject is null || pokeEditorSpeciesId <= 0) return null;
        int species = pokeEditorSpeciesId;
        double hold = (double)(PokeCryHold.Value ?? 0) / 1000.0;
        byte[]? sdat = EffectiveSoundArchive(species, out string? problem);
        if (sdat is null)
        {
            PokeDexTextStatus.Text = $"The cry could not be read: {problem}";
            return null;
        }

        try
        {
            PokemonCryService.Cry? cry = await Task.Run(() => PokemonCryService.Render(sdat, species, hold));
            if (cry is null) PokeDexTextStatus.Text = $"This game has no cry for species {species:D3}.";
            return cry;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
        {
            LogUiError("Cry could not be read", ex);
            PokeDexTextStatus.Text = $"The cry could not be read: {ex.Message}";
            return null;
        }
    }

    private async void PlayCry_Click(object? sender, RoutedEventArgs e)
    {
        if (await RenderSelectedCryAsync() is not { } cry) return;
        string? problem = AudioPlayer.Play(cry.ToWav());
        PokeDexTextStatus.Text = problem ?? $"Playing the cry of species {pokeEditorSpeciesId:D3}{(pendingCries.ContainsKey(pokeEditorSpeciesId) ? " with its unsaved changes" : string.Empty)} ({cry.Seconds:0.0} s).";
    }

    private async void SaveCry_Click(object? sender, RoutedEventArgs e)
    {
        if (await RenderSelectedCryAsync() is not { } cry) return;
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save cry",
            SuggestedFileName = $"cry_{pokeEditorSpeciesId:D3}.wav",
            DefaultExtension = "wav",
            FileTypeChoices = [new FilePickerFileType("WAV audio") { Patterns = ["*.wav"] }]
        });
        if (file is null) return;
        await File.WriteAllBytesAsync(file.Path.LocalPath, cry.ToWav());
        PokeDexTextStatus.Text = $"Saved {Path.GetFileName(file.Path.LocalPath)}.";
    }

    private async void ReplaceCry_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || pokeEditorSpeciesId <= 0 || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null || PokemonCryService.FindSoundArchive(currentProject, metadata) is not { } archivePath) return;
        string? path = await PickFileAsync("Pick the WAV file for the new cry", "WAV audio", "*.wav");
        if (path is null) return;
        int species = pokeEditorSpeciesId;
        var options = new PokemonCryService.ImportOptions(Math.Max(PokeCryFormat.SelectedIndex, 0), (int)(PokeCryImportRate.Value ?? 0), (double)(PokeCryGain.Value ?? 0), PokeCryNormalize.IsChecked == true);
        AudioPlayer.Stop();
        try
        {
            // Done on the sound archive as it is on disk, so the result shows what saving it will give.
            PokemonCryService.ImportResult result = await Task.Run(() => PokemonCryService.ImportInto(File.ReadAllBytes(archivePath), species, path, options).Result);
            // Settings typed before a new recording would be applied over its rate and loop, so they are dropped with it.
            pendingCries[species] = new PokemonCryService.Pending(path, options, null);
            RefreshCrySettings();
            RefreshUnsavedMarkers();
            PokeDexTextStatus.Text = (result.Cut
                ? $"The new cry of species {species:D3} was longer than the old cry's room, so it is cut to {result.Seconds:0.0} s."
                : $"The new cry of species {species:D3} is {result.Seconds:0.0} s (there is room for more).") + " Save All writes it to the sound archive.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or IndexOutOfRangeException or ArgumentException)
        {
            LogUiError("Cry could not be replaced", ex);
            PokeDexTextStatus.Text = $"The cry could not be replaced: {ex.Message}";
        }
    }

    private void CollectUnsavedCryChanges(List<UnsavedChange> changes)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        foreach ((int species, PokemonCryService.Pending pending) in pendingCries.OrderBy(entry => entry.Key).ToList())
        {
            var parts = new List<string>();
            if (pending.WavPath is not null) parts.Add($"new recording from {Path.GetFileName(pending.WavPath)}");
            if (pending.Settings is not null) parts.Add("instrument and wave settings");
            changes.Add(new UnsavedChange("Pokemon Editor", "Pokémon cry", [$"Species {species:D3}: {string.Join(" and ", parts)}"], () => SaveCry(species, pending, metadata), OpenDocument: false));
        }
    }

    private void SaveCry(int species, PokemonCryService.Pending pending, RomGameMetadata metadata)
    {
        if (currentProject is null) return;
        try
        {
            PokemonCryService.Save(currentProject, metadata, species, pending);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or IndexOutOfRangeException or ArgumentException)
        {
            LogUiError("Cry could not be saved", ex);
            PokeDexTextStatus.Text = $"The cry of species {species:D3} could not be saved: {ex.Message}";
            return;
        }

        pendingCries.Remove(species);
        if (species == pokeEditorSpeciesId) RefreshCrySettings();
        PokeDexTextStatus.Text = $"Saved the cry of species {species:D3} to the sound archive.";
    }
}
