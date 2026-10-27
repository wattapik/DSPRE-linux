using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace DSPRE.Avalonia;

/// <summary>Marks each input the user changed with "!" until the change is saved or discarded. An input is
/// watched from the moment the user changes it (while it has keyboard focus); its value then is what it is
/// compared against. The mark shows while the value differs from that and its editor still has unsaved
/// changes (see CollectUnsavedChanges). When the input's value is next changed by the program instead, it
/// is showing something else now (another document, another selected building), so it stops being watched.
/// Inputs that only navigate, filter or change the view are never marked.</summary>
public partial class MainWindow
{
    private sealed record WatchedInput(string Editor, object? Original, object? DataContext);

    private readonly Dictionary<Control, WatchedInput> watchedInputs = [];
    private readonly Dictionary<Control, TextBlock> inputMarks = [];
    private Dictionary<Control, string>? editorPanels;
    private DispatcherTimer? inputMarkRefresh;

    private static readonly HashSet<string> UnmarkedInputs =
    [
        // Document and item selectors: moving between things, not editing them.
        "HeaderSelector", "MapOwnerHeaderSelector", "MapMatrixSelector", "MapSelector", "WildEncounterSelector", "TextArchiveSelector", "ScriptFileSelector",
        "LevelScriptSelector", "TrainerSelector", "TrainerUsesSelector", "PokeEditorSpeciesSelector", "MoveSelector", "ItemSelector",
        "HoneyTreeGroupSelector", "GreatMarshGroupSelector", "BugContestSetSelector", "HeadbuttFileSelector", "SafariZoneFileSelector",
        "SafariZoneGroupSelector", "TradeIdInput", "MatrixCellRow", "MatrixCellColumn",
        // Searches and filters.
        "ScriptFileFindInput", "LevelScriptFindInput", "MatrixMapSearchInput", "MatrixPaneSearchInput", "HiddenItemsFilter", "VariableSearchInput", "FlagSearchInput", "VariableNamedOnly", "FlagNamedOnly",
        // What the views draw with or show, not saved.
        "OverworldTextureSelector", "OverworldBuildingTextureSelector", "IndoorTextureSelector", "IndoorBuildingTextureSelector",
        "CameraPreviewMapSelector", "BldModelTextureSelector", "MapModelList", "MapModelTextureSelector", "MapModelAnimationSlider", "MapAnimateTexturesCheckBox", "PermissionValueSelector", "PermissionCollisionModeRadio", "PermissionTypeModeRadio", "PermissionValueHexInput", "PermissionTransparencySlider", "EventTypeToAddSelector",
        "EventMarkerSizeInput", "EventMarkerHeightOffsetInput", "EventMarkerOffsetXInput", "EventMarkerOffsetZInput", "EventMarkerRotationInput",
        "OverworldShowBuildingsCheck", "OverworldShowOverworldsCheck", "OverworldShowOtherEventsCheck", "IndoorShowBuildingsCheck",
        "IndoorShowOverworldsCheck", "IndoorShowOtherEventsCheck", "ShowBuildingsCheckBox", "ShowEventsCheckBox", "ShowTileGridCheckBox",
        "BuildingShowAllModelsCheckBox", "ShowOverworldEventsCheckBox", "ShowWarpEventsCheckBox", "ShowTriggerEventsCheckBox",
        "ShowSpawnableEventsCheckBox", "MatrixPaneHeadersSection", "MatrixPaneAltitudesSection", "TrainerAnimateClassSpriteCheck",
        "PokeFlipCheck", "PokeCryHold", "PokeCryFormat", "PokeCryImportRate", "PokeCryGain", "PokeCryNormalize", "PokeSpriteShinyCheck", "TrainerGraphicAnimateCheck", "TrainerGraphicFrontRadio", "TrainerGraphicBackRadio", "FootprintDrawRadio", "FootprintEraseRadio", "FootprintCopyFrom", "AreaCategorySelector", "AreaRoutesRadio", "PdmPlacesRadio", "PdmPictureRadio", "PdmToolPencil", "PdmToolFill", "PdmToolPick", "PdmBrushSize", "PdmZoom", "PdmView", "PdmShowPlaces", "PdmSwatches", "AreaSpecialsRadio", "AreaPlaceList", "OwGraphicShinyCheck", "OwGraphicGridCheck",
        "BldModelInteriorCheckBox",
        // Inputs for a button's action (add, move to, replace with), and options of those actions.
        "PokeLearnsetMoveSelector", "PokeLearnsetLevelInput", "PokeDexReplaceSelector", "PokeDexMoveToInput",
        "TextSearchAllArchives", "TextSearchCaseSensitive", "LevelScriptPaddingCheckBox", "NsbmdSaveRemovedTexturesCheck",
        "SettingsBridgeAutoUpdateCheck", "UtilityRenameAfterUnpackCheck", "UtilityIsolateUnnamedCheck", "UtilitySaveContentListCheck",
    ];

    // Selectors that choose which document an editor shows. Whatever was typed into the inputs before they
    // changed belongs to the previous document, so undo must not reach back across it.
    private static readonly HashSet<string> NavigationInputs =
    [
        "HeaderSelector", "MapMatrixSelector", "MapSelector", "WildEncounterSelector", "TextArchiveSelector", "ScriptFileSelector",
        "LevelScriptSelector", "TrainerSelector", "PokeEditorSpeciesSelector", "MoveSelector", "ItemSelector",
        "HoneyTreeGroupSelector", "GreatMarshGroupSelector", "BugContestSetSelector", "HeadbuttFileSelector", "SafariZoneFileSelector",
        "SafariZoneGroupSelector", "TradeIdInput", "MatrixCellRow", "MatrixCellColumn",
    ];

    private void AttachInputMarks()
    {
        // A list's selection (events, buildings, area data, packs, the editors in the side bar) decides what the
        // inputs beside it show, so changing it ends the history of those inputs.
        SelectingItemsControl.SelectedIndexProperty.Changed.AddClassHandler<ListBox>((box, _) =>
        {
            if (!applyingUiUndo && TopLevel.GetTopLevel(box) == this) ClearUiUndo();
        });
        TextBox.TextProperty.Changed.AddClassHandler<TextBox>((box, e) => InputChanged(box, e.OldValue ?? string.Empty));
        SelectingItemsControl.SelectedIndexProperty.Changed.AddClassHandler<ComboBox>((box, e) => InputChanged(box, e.OldValue));
        ToggleButton.IsCheckedProperty.Changed.AddClassHandler<ToggleButton>((toggle, e) =>
        {
            if (toggle is CheckBox or RadioButton) InputChanged(toggle, e.OldValue);
        });
        NumericUpDown.ValueProperty.Changed.AddClassHandler<NumericUpDown>((box, e) => InputChanged(box, e.OldValue));

        // The marks are drawn on the window's adorner layer, above every page, so each is hidden whenever its
        // input is: on another editor's page, a tab not chosen, a collapsed pane.
        Visual.IsVisibleProperty.Changed.AddClassHandler<Control>((_, _) => ScheduleInputMarkVisibility());
        SelectingItemsControl.SelectedIndexProperty.Changed.AddClassHandler<TabControl>((_, _) => ScheduleInputMarkVisibility());
    }

    private bool inputMarkVisibilityPending;

    private void ScheduleInputMarkVisibility()
    {
        if (inputMarkVisibilityPending || inputMarks.Count == 0) return;
        inputMarkVisibilityPending = true;
        Dispatcher.UIThread.Post(() =>
        {
            inputMarkVisibilityPending = false;
            UpdateInputMarkVisibility();
        }, DispatcherPriority.Background);
    }

    private void UpdateInputMarkVisibility()
    {
        foreach ((Control input, TextBlock mark) in inputMarks)
        {
            mark.IsVisible = input.IsEffectivelyVisible && input.GetVisualRoot() is not null;
        }
    }

    private static object? InputValue(Control input) => input switch
    {
        TextBox box => box.Text ?? string.Empty,
        ComboBox box => box.SelectedIndex,
        ToggleButton toggle => toggle.IsChecked,
        NumericUpDown box => box.Value,
        _ => null,
    };

    private void InputChanged(Control input, object? oldValue)
    {
        if (switchingDocument || TopLevel.GetTopLevel(input) != this) return;
        if (!applyingUiUndo && input.Name is string inputName && NavigationInputs.Contains(inputName)) ClearUiUndo();

        // A ComboBox's choice is made in its drop-down, which holds the focus while open.
        bool byUser = applyingUiUndo || input.IsKeyboardFocusWithin || input is ComboBox { IsDropDownOpen: true } || (input is NumericUpDown && input.IsPointerOver);
        if (!byUser)
        {
            if (watchedInputs.Remove(input)) SetInputMark(input, false);
            return;
        }

        if (!applyingUiUndo && IsMarkableInput(input) && FindInputEditor(input) is string editedIn) RecordUiEdit(input, oldValue, editedIn);
        if (watchedInputs.ContainsKey(input) || !IsMarkableInput(input) || FindInputEditor(input) is not string editor) return;
        watchedInputs[input] = new WatchedInput(editor, oldValue, input.DataContext);
        ScheduleInputMarkRefresh();
    }

    private static bool IsMarkableInput(Control input)
    {
        // Parts of another control's template (a NumericUpDown's or editable ComboBox's own text box).
        if (input.TemplatedParent is not null) return false;
        if (input is TextBox { IsReadOnly: true }) return false;
        string name = input.Name ?? string.Empty;
        if (UnmarkedInputs.Contains(name)) return false;
        return !(name.Contains("Search", StringComparison.Ordinal) || name.Contains("Filter", StringComparison.Ordinal)
                 || name.Contains("Replace", StringComparison.Ordinal) || name.StartsWith("Research", StringComparison.Ordinal));
    }

    /// <summary>The editor whose page the input is on. The Map Editor's panes show other editors' panels,
    /// which still belong to those editors.</summary>
    private string? FindInputEditor(Control input)
    {
        editorPanels ??= new Dictionary<Control, string>
        {
            [HeaderMapEditorPanel] = "Map Editor", [ScriptEditorPanel] = "Script Editor", [LevelScriptEditorPanel] = "Level Script Editor",
            [TextEditorPanel] = "Text Editor", [WildEncounterEditorPanel] = "Wild Pokemon Encounter Editor", [NsbtxEditorPanel] = "Texture Editor",
            [CameraEditorPanel] = "Camera Editor", [SpecialEncounterEditorPanel] = "Special Encounters", [TrainerEditorPanel] = "Trainer Editor",
            [PokemonEditorPanel] = "Pokemon Editor", [MoveEditorPanel] = "Move Editor", [ItemEditorPanel] = "Item Editor",
            [TradeEditorPanel] = "Trade Editor", [FlyWarpEditorPanel] = "Fly Warp Editor", [SpawnEditorPanel] = "Spawn Editor",
            [TableEditorPanel] = "Table Editor", [ItemTableEditorPanel] = "Item Table Editor", [TmEditorPanel] = "TM Editor",
            [SettingsPanel] = "Settings", [CharMapPanel] = "Character Map", [MapModelEditorPanel] = "Map Model Editor", [VariableFlagEditorPanel] = "Variable and Flag Editor",
        };
        foreach (ExtraEditor extra in extraEditors) editorPanels[extra.Panel] = extra.Title;
        foreach (Visual ancestor in input.GetVisualAncestors())
        {
            if (ancestor is Control panel && editorPanels.TryGetValue(panel, out string? editor)) return editor;
        }
        return null;
    }

    /// <summary>Typing calls this on every key; checking every editor's documents that often would slow it,
    /// so the marks catch up a moment later.</summary>
    private void ScheduleInputMarkRefresh()
    {
        if (inputMarkRefresh is null)
        {
            inputMarkRefresh = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            inputMarkRefresh.Tick += (_, _) =>
            {
                inputMarkRefresh.Stop();
                RefreshUnsavedMarkers();
            };
        }
        inputMarkRefresh.Stop();
        inputMarkRefresh.Start();
    }

    private void RefreshInputMarks(HashSet<string> unsavedEditors)
    {
        foreach ((Control input, WatchedInput watched) in watchedInputs.ToList())
        {
            if (!unsavedEditors.Contains(watched.Editor))
            {
                watchedInputs.Remove(input);
                SetInputMark(input, false);
                continue;
            }

            bool changed = !Equals(InputValue(input), watched.Original) && ReferenceEquals(input.DataContext, watched.DataContext);
            SetInputMark(input, changed);
        }
        UpdateInputMarkVisibility();
    }

    private void ForgetInputMarks()
    {
        foreach (Control input in watchedInputs.Keys.ToList()) SetInputMark(input, false);
        watchedInputs.Clear();
    }

    private void SetInputMark(Control input, bool shown)
    {
        if (!shown)
        {
            if (inputMarks.Remove(input)) AdornerLayer.SetAdorner(input, null);
            return;
        }
        if (inputMarks.ContainsKey(input)) return;

        var mark = new TextBlock
        {
            Text = "!",
            FontWeight = FontWeight.Bold,
            FontSize = 12,
            Foreground = Brushes.Black,
            Background = new SolidColorBrush(Color.Parse("#E8B75F")),
            Padding = new Thickness(4, 0),
            HorizontalAlignment = HorizontalAlignment.Right,
            VerticalAlignment = VerticalAlignment.Top,
            IsHitTestVisible = false,
        };
        inputMarks[input] = mark;
        AdornerLayer.SetAdorner(input, mark);
        mark.IsVisible = input.IsEffectivelyVisible && input.GetVisualRoot() is not null;
    }
}
