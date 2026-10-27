using Avalonia.Interactivity;
using DSPRE.Core;
using System.Text.Json.Nodes;

namespace DSPRE.Avalonia;

/// <summary>The top bar's "Save All": gathers what each editor has not saved yet, shows it, and saves
/// it all after the user confirms. Each editor is saved by its own Save handler.</summary>
public partial class MainWindow
{
    /// <param name="Editor">The sidebar entry of the editor the change was made in.</param>
    /// <param name="OpenDocument">The change is to the document the editor has open (one trainer, one script
    /// file...), which opening another one in that editor replaces; otherwise it is to something the editor
    /// keeps whole (a table), which survives moving around in it.</param>
    private sealed record UnsavedChange(string Editor, string Area, IReadOnlyList<string> Details, Action Save, bool OpenDocument = false);

    private async void SaveAll_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null)
        {
            StatusText.Text = "Open a ROM project first.";
            return;
        }

        List<UnsavedChange> changes = CollectUnsavedChanges();
        bool saveAll = await new UnsavedChangesWindow(changes.Select(change => ($"{change.Area} ({change.Editor})", change.Details)).ToList()).ShowDialog<bool>(this);
        if (!saveAll || changes.Count == 0) return;

        string? choice = await new ChoiceDialog(
            "Save all changes",
            $"Save the {changes.Count} unsaved change{(changes.Count == 1 ? "" : "s")} to the project?",
            ("save", "Save All"), ("cancel", "Cancel")).ShowDialog<string?>(this);
        if (choice != "save") return;

        List<UnsavedChange> remaining = SaveChanges(changes);
        StatusText.Text = remaining.Count == 0
            ? $"Saved {changes.Count} change{(changes.Count == 1 ? "" : "s")}. Use Save ROM to build them into the ROM."
            : $"Saved what could be saved; still unsaved: {DescribeUnsavedAreas(remaining)}.";
    }

    private List<UnsavedChange> CollectUnsavedChanges()
    {
        var changes = new List<UnsavedChange>();
        if (currentProject is null) return changes;

        CollectUnsavedMapChanges(changes);
        CollectUnsavedMapModelChanges(changes);
        CollectUnsavedMapCreatorChanges(changes);
        CollectUnsavedCryChanges(changes);
        CollectUnsavedMusicChanges(changes);
        CollectUnsavedBattleBackgroundChanges(changes);
        CollectUnsavedBattleDataChanges(changes);
        CollectUnsavedVariableFlagChanges(changes);

        void Tracked(string editor, string area, EditTracker tracker, object? document, Action save, bool alsoUnsaved = false, bool openDocument = false)
        {
            if (document is null) return;
            if (!tracker.IsUnsaved(document) && !alsoUnsaved) return;
            IReadOnlyList<string> details = tracker.DescribeUnsaved(document);
            changes.Add(new UnsavedChange(editor, area, details.Count > 0 ? details : ["Edited"], save, openDocument));
        }

        void Flagged(string editor, string area, bool unsaved, string detail, Action save, bool openDocument = false)
        {
            if (unsaved) changes.Add(new UnsavedChange(editor, area, [detail], save, openDocument));
        }

        RoutedEventArgs args = new();

        Tracked("Wild Pokemon Encounter Editor", "Wild encounters", wildEncounterEdits, currentWildEncounter, () => SaveWildEncounter_Click(null, args), openDocument: true);
        Tracked("Text Editor", "Text archive", textArchiveEdits, currentTextArchive, () => SaveCurrentTextArchive(), textDirty && currentTextArchive is not null, openDocument: true);
        Flagged("Text Editor", "Text archive name", TextArchiveNameUnsaved(), $"Text archive {currentTextArchive?.Id:D4}: name \"{TextArchiveNameInput.Text?.Trim()}\"", SaveTextArchiveName, openDocument: true);

        SyncTrainerParty();
        Tracked("Trainer Editor", "Trainer", trainerEdits, currentTrainer, () => SaveTrainer_Click(null, args), openDocument: true);

        Tracked("Pokemon Editor", "Pokémon data", personalDataEdits, currentPersonalData, () => SavePersonalData_Click(null, args), openDocument: true);
        Flagged("Pokemon Editor", "Hatch result", currentPersonalData is not null && !personalDataEdits.IsUnsaved(currentPersonalData)
            && pokeSavedHatchResult >= 0 && PokeHatchResultSelector.SelectedIndex != pokeSavedHatchResult,
            $"Species {pokeEditorSpeciesId:D3}", () => SavePersonalData_Click(null, args), openDocument: true);
        Tracked("Pokemon Editor", "Learnset", learnsetEdits, currentLearnset, () => SaveLearnset_Click(null, args), openDocument: true);
        Tracked("Pokemon Editor", "Evolutions", evolutionEdits, currentEvolutionFile, () => SaveEvolutions_Click(null, args), openDocument: true);
        Flagged("Pokemon Editor", "Pokédex text", PokedexTextUnsaved(), $"Name, category or entry of species {pokeEditorSpeciesId:D3}", () => SavePokedexText_Click(null, args), openDocument: true);
        Flagged("Pokemon Editor", "Battle sprites", pokeSpritesUnsaved, $"Imported sprites of species {pokeEditorSpeciesId:D3}", () => SavePokeSprites_Click(null, args), openDocument: true);
        Tracked("Pokemon Editor", "Egg moves", eggMoveEdits, eggMoveEntries, () => SaveEggMoves_Click(null, args));
        Flagged("Pokemon Editor", "Pokédex order", pokeDexOrder.Count > 0 && !pokeDexOrder.SequenceEqual(pokeDexSavedOrder),
            "Regional dex order edited", () => SavePokeDexOrder_Click(null, args));

        Tracked("Move Editor", "Move", moveEdits, currentMoveData, () => SaveMove_Click(null, args), openDocument: true);
        Flagged("Move Editor", "Move names and descriptions", MoveTextsUnsaved(), "Edited in the Move Editor", () => MoveEditorStatus.Text = SaveMoveTexts().Trim());

        Tracked("Item Editor", "Item data", itemDataEdits, currentItemData, () => SaveItem_Click(null, args), openDocument: true);
        Tracked("Item Editor", "Item table entry", itemTableEntryEdits, currentItemTableEntry, () => SaveItem_Click(null, args), openDocument: true);
        Flagged("Item Editor", "Item names and descriptions", ItemTextsUnsaved(), "Edited in the Item Editor", () => ItemEditorStatus.Text = SaveItemTexts().Trim());

        Tracked("Trade Editor", "Trade", tradeEdits, currentTradeData, () => SaveTradeData_Click(null, args), openDocument: true);
        Flagged("Trade Editor", "Trade text", TradeTextUnsaved(), "Nickname or OT name edited", () => SaveTradeText_Click(null, args), openDocument: true);

        Tracked("TM Editor", "TM/HM table", tmEdits, tmMachines, () => SaveTmEditor_Click(null, args));
        Tracked("Item Table Editor", "Hidden items", hiddenItemEdits, hiddenItems, () => SaveHiddenItems_Click(null, args));
        Tracked("Item Table Editor", "Pickup table", pickupEdits, pickupTable, () => SavePickupTable_Click(null, args));
        Tracked("Spawn Editor", "Spawn settings", spawnEdits, ReadSpawnSettingsFromFields(), () => SaveSpawnSettings_Click(null, args));
        Tracked("Fly Warp Editor", "Fly warp table", flyWarpEdits, flyWarpEntries, () => SaveFlyWarpTable_Click(null, args));

        Tracked("Table Editor", "Battle effect combos", tableComboEdits, tableCombos, () => SaveTableCombos_Click(null, args));
        Tracked("Table Editor", "VS. trainer class effects", tableVsTrainerEdits, tableVsTrainers, () => SaveTableVsTrainer_Click(null, args));
        Tracked("Table Editor", "VS. wild Pokemon effects", tableVsPokemonEdits, tableVsPokemon, () => SaveTableVsPokemon_Click(null, args));
        Tracked("Table Editor", "Conditional music table", tableConditionalMusicEdits, tableConditionalMusic, () => SaveTableConditionalMusic_Click(null, args));

        Tracked("Special Encounters", "Honey Tree encounters", honeyTreeEdits, honeyTreeGroups, () => SaveHoneyTree_Click(null, args));
        Tracked("Special Encounters", "Great Marsh encounters", greatMarshEdits, greatMarshGroups, () => SaveGreatMarsh_Click(null, args));
        Tracked("Special Encounters", "Bug Contest encounters", bugContestEdits, bugContestSets, () => SaveBugContest_Click(null, args));
        Tracked("Special Encounters", "Headbutt encounters", headbuttEdits, currentHeadbuttFile, () => SaveHeadbutt_Click(null, args), openDocument: true);
        Tracked("Special Encounters", "Safari Zone encounters", safariZoneEdits, currentSafariZoneFile, () => SaveSafariZone_Click(null, args), openDocument: true);

        Tracked("Camera Editor", "Camera table", cameraEdits, currentCameraTable, () => SaveCameraTable_Click(null, args));
        Tracked("Level Script Editor", "Level script", levelScriptEdits, currentLevelScript, () => SaveLevelScript_Click(null, args), openDocument: true);
        Flagged("Level Script Editor", "Level script name", LevelScriptNameUnsaved(), $"Level script {currentLevelScript?.Id:D4}: name \"{LevelScriptNameInput.Text?.Trim()}\"", SaveLevelScriptName, openDocument: true);
        Tracked("Script Editor", "Script", scriptEdits, CurrentScriptDocument(), () => SaveCurrentScript(), openDocument: true);

        if (nsbtxDirtyPalettes.Count > 0 && currentNsbtxPack is not null)
        {
            IEnumerable<string> names = nsbtxDirtyPalettes.Order().Where(index => index < currentNsbtxPack.Palettes.Count).Select(index => $"Palette {currentNsbtxPack.Palettes[index].Name}");
            changes.Add(new UnsavedChange("Texture Editor", "Texture pack palettes", names.ToList(), () => NsbtxSavePalette_Click(null, args), OpenDocument: true));
        }
        Flagged("Texture Editor", "Area data", AreaDataUnsaved(), $"Area data {currentAreaData?.Id:D3}", () => AreaDataSave_Click(null, args), openDocument: true);

        Flagged("Character Map", "Character map", charMapDirty, "Unsaved alias edits", () => CharMapSave_Click(null, args));
        Flagged("Settings", "Settings", SettingsUnsaved(), "Folders or startup project edited", () => SettingsSave_Click(null, args));

        return changes;
    }

    /// <summary>Mirrors what SaveMap_Click would write, without writing it.</summary>
    private void CollectUnsavedMapChanges(List<UnsavedChange> changes)
    {
        if (currentProject is null) return;
        var details = new List<string>();
        try
        {
            if (selectedHeader is not null && HeaderHasUnsavedChanges()) details.Add($"Header {selectedHeader.Id:D3}");
            details.AddRange(pendingHeaders.Keys.Order().Select(id => $"Header {id:D3}"));
            if (IsEditingMatrix && currentMatrix is not null && matrixEdits.IsUnsaved(currentMatrix))
            {
                details.Add($"Matrix {currentMatrix.Id:D4}: {string.Join("; ", matrixEdits.DescribeUnsaved(currentMatrix, 3))}");
            }

            details.AddRange(MapsWithUnsavedBuildings().Select(mapId => $"Buildings of map {mapId:D4}"));
            details.AddRange(UnsavedPermissions().Select(entry => $"Move permissions of map {entry.MapId:D4}"));

            bool single = ReferenceEquals(MapModeTabs.SelectedItem, SingleMapTab);
            if (single && MapSelector.SelectedIndex >= 0 && MapSelector.SelectedIndex < mapEntries.Count)
            {
                MapViewEntry map = mapEntries[MapSelector.SelectedIndex];
                if (mapAreaData.TryGetValue(map.Id, out AreaDataRecord? areaData))
                {
                    int mapPack = MapTextureSelector.SelectedIndex - 1;
                    int buildingPack = MapBuildingTextureSelector.SelectedIndex - 1;
                    if ((mapPack >= 0 && mapPack != areaData.MapTileset) || (buildingPack >= 0 && buildingPack != areaData.BuildingsTileset))
                    {
                        details.Add($"Texture packs (area data {areaData.Id:D3})");
                    }
                }
            }

            details.AddRange(UnsavedEventFiles().Select(file => $"Events (event file {file.Id:D4})"));
        }
        catch (Exception ex)
        {
            LogUiError("The map's unsaved changes could not be checked", ex);
        }

        if (details.Count > 0) changes.Add(new UnsavedChange("Map Editor", "Map editor", details, () => SaveMap_Click(null, new RoutedEventArgs())));
    }
}
