using System.Reflection;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>Opening a project (another one, or the same one again after a ROM Toolbox patch) starts every
/// editor afresh: nothing read from the previous project may be shown, reused or saved into the new one.</summary>
public partial class MainWindow
{
    /// <summary>Asks what to do with unsaved edits before the project is opened afresh (another project, or
    /// this one again after a patch or a journal replay), which replaces them. False to stay.</summary>
    private async Task<bool> ConfirmLeavingProjectAsync(string beforeWhat = "opening another project")
    {
        List<UnsavedChange> changes = CollectUnsavedChanges();
        if (changes.Count == 0) return true;

        string? choice = await new ChoiceDialog(
            "Unsaved changes",
            $"{DescribeUnsavedAreas(changes)} {(changes.Count == 1 ? "has" : "have")} unsaved changes. Save them before {beforeWhat}?",
            ("save", "Save All"), ("discard", "Discard"), ("cancel", "Cancel")).ShowDialog<string?>(this);
        if (choice == "save")
        {
            List<UnsavedChange> remaining = SaveChanges(changes);
            if (remaining.Count > 0)
            {
                StatusText.Text = $"Stopped: {DescribeUnsavedAreas(remaining)} could not be saved.";
                return false;
            }
        }
        return choice is "save" or "discard";
    }

    /// <summary>Forgets every editor's loaded document, its unsaved state, and anything cached from the
    /// previous project. Each editor reads the new project when it is next opened.</summary>
    private void ResetEditorsForProject()
    {
        // A tracker that has loaded nothing reports nothing unsaved, so Save All cannot write a document of
        // the previous project into this one.
        foreach (FieldInfo field in typeof(MainWindow).GetFields(BindingFlags.Instance | BindingFlags.NonPublic).Where(field => field.FieldType == typeof(EditTracker)))
        {
            ((EditTracker)field.GetValue(this)!).Loaded(null, string.Empty);
        }
        textDirty = false;
        currentTradeArchive = null;
        tradeLoadedNickname = tradeLoadedOtName = string.Empty;
        pokeDexOrder = [];
        pokeDexSavedOrder = [];
        currentMoveId = -1;
        currentItemId = -1;
        moveNamesArchive = moveDescriptionsArchive = null;
        savedMoveNames = [];
        itemNamesArchive = itemDescriptionsArchive = null;
        ResetPreviewFont();
        savedItemNames = [];
        savedItemDescriptions = [];
        savedMoveDescriptions = [];
        pokeSavedHatchResult = -1;
        pokeSpritesUnsaved = false;
        pokeDexText = null;
        pendingCries.Clear();
        pendingSequences.Clear();
        battleBackgrounds = null;
        footprintSet = null;
        shadowSet = null;
        areaSet = null;
        areaMap = null;
        areaCanvas.Map = pokedexMapCanvas.Map = null;
        areaCanvas.Picture = pokedexMapCanvas.Picture = null;
        ResetTownPictures();
        areaSpecialNames = areaRouteNames = [];
        areaHeaderNames.Clear();
        musicEntries = [];
        instrumentBanks = [];
        InstrumentBankList.Items.Clear();
        InstrumentList.Items.Clear();
        InstrumentRegionList.Items.Clear();
        battleBackgroundView = null;
        pendingBackdrops.Clear();
        battleBackgroundImage = null;
        currentAreaData = null;
        selectedHeader = null;
        pendingHeaders.Clear();
        headerManagerPending.Clear();
        ClearUiUndo();
        ResetMapAnimations();
        mapModelPendingPacks.Clear();
        ResetVariableFlagEditor();
        mapModelPendingImports.Clear();
        ResetMapCreator();
        mapModelPendingAnimations.Clear();
        mapModelNewPackId = -1;
        MapModelViewer.SetTextureAnimation(null);
        ForgetInputMarks();
        pendingPermissions.Clear();
        permissionsMapId = -1;
        ClearPermissionHistory();

        // Lists and caches some editors only fill when empty.
        scriptFileIds.Clear();
        loadedScriptFileIndex = -1;
        textArchiveIds.Clear();
        areaDataRecords.Clear();
        scriptCommandDatabase = null;
        research = null;
        trainerUses = null;

        currentScriptTextArchive = null;
        scriptUsesLua = false;
        LuaTextBox.Text = string.Empty;
        GlobalLuaTextBox.Text = string.Empty;
        savedGlobalLua = string.Empty;
        scriptFileNames = [];
        headerIdCombosStale = true;
        levelScriptNames = [];
        textArchiveNames = [];
        subscriptNameCache.Clear();

        // The tile and camera previews remember the map they show by number, which names a different map
        // in another project; they start empty and load again when asked.
        foreach (TilePreviewState state in new[] { flyWarpPreviewState, spawnPreviewState })
        {
            state.MapId = -1;
            state.Request++;
        }
        flyWarpPreviewSlot = -1;
        cameraPreviewIndex = -1;
        cameraPreviewMapId = -1;
        cameraPreviewRequest++;
        foreach (MapGlView view in new[] { MapModelViewer, FlyWarpPreviewViewer, SpawnPreviewViewer, CameraPreviewViewer }) view.ClearScene();
        FlyWarpPreviewDescription.Text = SpawnPreviewDescription.Text = CameraPreviewDescription.Text = string.Empty;
        FlyWarpPreviewPlaceholder.Text = "Click a slot to see where it lands.";
        FlyWarpPreviewPlaceholder.IsVisible = true;
        SpawnPreviewPlaceholder.IsVisible = true;
    }
}
