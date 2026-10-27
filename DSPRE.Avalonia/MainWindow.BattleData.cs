using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Trainer Message Editor, Growth Rate Editor and Battle Frontier Editor (and the Pokémon Editor's Footprint, Shadow and Areas tabs, in MainWindow.Footprints.cs and MainWindow.PokemonBattleTabs.cs): editors whose pages are built in code and
/// plugged into the sidebar through <see cref="RegisterExtraEditor"/> (see TrainerMessageService, GrowthTableService, FrontierService).
/// Each keeps its whole table in memory and tracks it with an <see cref="EditTracker"/>, written by its Save button or Save All.</summary>
public partial class MainWindow
{
    private static readonly IBrush ExtraMutedBrush = new SolidColorBrush(Color.Parse("#AAB7C4"));

    private sealed record ExtraEditor(string Title, Control Panel, Action Configure);

    private readonly List<ExtraEditor> extraEditors = [];

    private void BuildExtraEditors()
    {
        RegisterExtraEditor("Pokedex Map Editor", "map_editor", BuildPokedexMapPanel(), ConfigurePokedexMapEditor, after: "Fly Warp Editor");
        RegisterExtraEditor("Header Manager", "map_editor", BuildHeaderManagerPanel(), ConfigureHeaderManager, after: "Map Editor");
        RegisterExtraEditor("Trainer Message Editor", "trainer_editor", BuildTrainerMessagePanel(), ConfigureTrainerMessageEditor, after: "Trainer Editor");
        RegisterExtraEditor("Growth Rate Editor", "pokemon_editor_icon", BuildGrowthPanel(), ConfigureGrowthEditor, after: "Pokemon Editor");
        AddFootprintTab();
        AddShadowTab();
        AddAreasTab();
        RegisterExtraEditor("Battle Frontier Editor", "trainer_editor", BuildFrontierPanel(), ConfigureFrontierEditor, after: "Trade Editor");
    }

    private void RegisterExtraEditor(string title, string icon, Control panel, Action configure, string after)
    {
        panel.IsVisible = false;
        ((Panel)TrainerGraphicEditorPanel.Parent!).Children.Add(panel);
        EditorIcons.Add(title, icon);
        int index = EditorList.Items.OfType<ListBoxItem>().Select((item, i) => (item, i)).First(pair => pair.item.Content as string == after).i;
        EditorList.Items.Insert(index + 1, new ListBoxItem { Content = title });
        extraEditors.Add(new ExtraEditor(title, panel, configure));
    }

    private bool IsExtraEditor(string title) => extraEditors.Any(editor => editor.Title == title);

    private void ShowExtraEditor(string title, bool projectOpen)
    {
        foreach (ExtraEditor editor in extraEditors) editor.Panel.IsVisible = projectOpen && editor.Title == title;
    }

    private void ConfigureExtraEditor(string title) => extraEditors.First(editor => editor.Title == title).Configure();

    private void CollectUnsavedBattleDataChanges(List<UnsavedChange> changes)
    {
        void Add(string editor, string area, EditTracker tracker, object? document, Action save)
        {
            if (document is null || !tracker.IsUnsaved(document)) return;
            IReadOnlyList<string> details = tracker.DescribeUnsaved(document);
            changes.Add(new UnsavedChange(editor, area, details.Count > 0 ? details : ["Edited"], save));
        }

        foreach (ushort id in headerManagerPending.Keys.Order().ToList())
        {
            ushort captured = id;
            changes.Add(new UnsavedChange("Header Manager", $"Header {id:D4}", ["Edited"], () => SaveManagedHeaders([captured])));
        }

        Add("Trainer Message Editor", "Trainer messages", trainerMessageEdits, trainerMessageTable, SaveTrainerMessages);
        Add("Growth Rate Editor", "Growth rates", growthEdits, growthTables, SaveGrowthTables);
        Add("Pokemon Editor", "Footprints", footprintEdits, footprintSet, SaveFootprints);
        Add("Pokemon Editor", "Battle shadows", shadowEdits, shadowSet, SaveShadows);
        Add("Pokedex Map Editor", "Pokédex area map", areaMapEdits, areaMap, SaveAreaMap);
        foreach (TownPictureState state in townStates.Values) Add("Pokedex Map Editor", state.View == TownMapView.Terrain ? "Town Map terrain picture" : "Town Map route diagram", state.Edits, state.Picture, SaveTownMapPicture);
        Add("Pokemon Editor", "Pokédex areas", areaEdits, areaSet, SavePokedexAreas);
        Add("Battle Frontier Editor", "Battle Tower Pokémon", frontierTowerEdits, frontierTowerSets, () => SaveFrontierSets(FrontierFacility.Tower));
        Add("Battle Frontier Editor", "Battle Stage Pokémon", frontierStageEdits, frontierStageSets, () => SaveFrontierSets(FrontierFacility.Stage));
        Add("Battle Frontier Editor", "Battle Tower trainers", frontierTrainerEdits, frontierTrainers, SaveFrontierTrainers);
    }

    private bool TryGetGame(out RomProject project, out RomGameMetadata metadata)
    {
        project = null!;
        metadata = null!;
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? found) || found is null) return false;
        (project, metadata) = (currentProject, found);
        return true;
    }

    private static string[] LoadNames(RomProject project, RomGameMetadata metadata, int archive) =>
        TextArchiveService.Load(project, archive, metadata).Messages.ToArray();

    private static string Name(string[] names, int index) => index >= 0 && index < names.Length ? names[index] : $"#{index}";

    private static Control Field(string label, Control control) =>
        new StackPanel { Spacing = 2, Children = { new TextBlock { Text = label, Foreground = ExtraMutedBrush, FontSize = 11 }, control } };

    private static Button ActionButton(string text, string tip, EventHandler<global::Avalonia.Interactivity.RoutedEventArgs> click)
    {
        var button = new Button { Content = text, Padding = new Thickness(12, 6) };
        ToolTip.SetTip(button, tip);
        button.Click += click;
        return button;
    }

    private static Border SideList(string title, Control top, ListBox list) =>
        new()
        {
            Background = new SolidColorBrush(Color.Parse("#151C23")), BorderBrush = new SolidColorBrush(Color.Parse("#33414D")), BorderThickness = new Thickness(1), Padding = new Thickness(8),
            Child = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 8,
                Children = { new TextBlock { Text = title, FontWeight = FontWeight.SemiBold }, Row(top, 1), Row(list, 2) }
            }
        };

    private static T Row<T>(T control, int row) where T : Control
    {
        Grid.SetRow(control, row);
        return control;
    }

    private static Control EditorPage(Control side, Control content, TextBlock status)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("320,*"), RowDefinitions = new RowDefinitions("*,Auto"), ColumnSpacing = 12, RowSpacing = 8 };
        grid.Children.Add(side);
        Grid.SetColumn(content, 1);
        grid.Children.Add(content);
        Grid.SetRow(status, 1);
        Grid.SetColumnSpan(status, 2);
        grid.Children.Add(status);
        return grid;
    }

    #region Trainer Message Editor

    private readonly EditTracker trainerMessageEdits = new();
    private TrainerMessageTable? trainerMessageTable;
    private string[] trainerMessageTrainerNames = [];
    private string[] trainerMessageClassNames = [];
    private int[] trainerMessageClasses = [];
    private bool loadingTrainerMessages;
    private readonly TextBox trainerMessageSearch = new() { Watermark = "Search trainers" };
    private readonly ListBox trainerMessageTrainerList = new();
    private readonly ListBox trainerMessageEntryList = new() { MinHeight = 120, MaxHeight = 260 };
    private readonly ComboBox trainerMessageTrigger = new() { Width = 420 };
    private readonly TextBox trainerMessageText = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 140, FontFamily = new FontFamily("monospace") };
    private readonly TextBlock trainerMessageTitle = new() { FontWeight = FontWeight.SemiBold, FontSize = 15 };
    private readonly TextBlock trainerMessageStatus = new() { Text = "No ROM loaded.", Foreground = ExtraMutedBrush, TextWrapping = TextWrapping.Wrap };
    private readonly List<int> trainerMessageShownTrainers = [];
    private List<TrainerMessageEntry> trainerMessageShownEntries = [];

    private sealed record TriggerItem(int Value)
    {
        public override string ToString() => $"{Value}: {TrainerMessageService.TriggerName(Value)}";
    }

    private Control BuildTrainerMessagePanel()
    {
        trainerMessageSearch.TextChanged += (_, _) => FillTrainerMessageTrainers();
        trainerMessageTrainerList.SelectionChanged += (_, _) => ShowTrainerMessageEntries();
        trainerMessageEntryList.SelectionChanged += (_, _) => ShowSelectedTrainerMessage();
        trainerMessageTrigger.SelectionChanged += (_, _) =>
        {
            if (loadingTrainerMessages || SelectedTrainerMessage is not { } entry || trainerMessageTrigger.SelectedItem is not TriggerItem trigger) return;
            entry.Trigger = trigger.Value;
            AfterTrainerMessageEdit();
        };
        trainerMessageText.TextChanged += (_, _) =>
        {
            if (loadingTrainerMessages || SelectedTrainerMessage is not { } entry) return;
            entry.Text = trainerMessageText.Text ?? string.Empty;
            AfterTrainerMessageEdit();
        };

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8,
            Children =
            {
                ActionButton("Add Message", "Add a line for this trainer, with the first trigger it has no line for", (_, _) => AddTrainerMessage()),
                ActionButton("Remove Message", "Delete the selected line", (_, _) => RemoveTrainerMessage()),
                ActionButton("Save", "Write the messages to the project", (_, _) => SaveTrainerMessages())
            }
        };
        var content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                trainerMessageTitle,
                new TextBlock { Text = "Each line is said at one point of the trainer's battle. A trainer's lines are kept together in the table.", Foreground = ExtraMutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                trainerMessageEntryList,
                buttons,
                Field("Said", trainerMessageTrigger),
                Field("Text (as in the text archive: \\n new line, \\r new box, \\f next page)", trainerMessageText)
            }
        };
        return EditorPage(SideList("Trainers", trainerMessageSearch, trainerMessageTrainerList), new ScrollViewer { Content = content, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }, trainerMessageStatus);
    }

    private TrainerMessageEntry? SelectedTrainerMessage =>
        trainerMessageEntryList.SelectedIndex >= 0 && trainerMessageEntryList.SelectedIndex < trainerMessageShownEntries.Count ? trainerMessageShownEntries[trainerMessageEntryList.SelectedIndex] : null;

    private int? SelectedMessageTrainer =>
        trainerMessageTrainerList.SelectedIndex >= 0 && trainerMessageTrainerList.SelectedIndex < trainerMessageShownTrainers.Count ? trainerMessageShownTrainers[trainerMessageTrainerList.SelectedIndex] : null;

    private void ConfigureTrainerMessageEditor()
    {
        if (!TryGetGame(out RomProject project, out RomGameMetadata metadata)) return;
        if (!TrainerMessageService.IsSupported(project, metadata))
        {
            trainerMessageStatus.Text = "This project has no trainer message tables.";
            return;
        }

        try
        {
            if (trainerMessageTable is null || !trainerMessageEdits.IsUnsaved(trainerMessageTable))
            {
                trainerMessageTable = TrainerMessageService.Load(project, metadata);
                trainerMessageEdits.Loaded(trainerMessageTable, "Trainer messages");
            }

            string[] rawTrainerNames = LoadNames(project, metadata, TrainerService.GetTrainerNamesArchiveId(metadata));
            trainerMessageTrainerNames = rawTrainerNames.Select(TrainerService.UnwrapTrainerName).ToArray();
            trainerMessageClassNames = LoadNames(project, metadata, TrainerService.GetTrainerClassNamesArchiveId(metadata));
            if (trainerMessageClasses.Length != trainerMessageTrainerNames.Length)
            {
                trainerMessageClasses = new int[trainerMessageTrainerNames.Length];
                for (int id = 0; id < trainerMessageClasses.Length; id++)
                {
                    try { trainerMessageClasses[id] = TrainerService.Load(project, id, metadata, rawTrainerNames[id]).TrainerClass; }
                    catch (Exception ex) when (ex is IOException or InvalidDataException) { trainerMessageClasses[id] = -1; }
                }
            }

            FillTrainerMessageTrainers(SelectedMessageTrainer ?? trainerMessageTable.Entries.FirstOrDefault()?.Trainer);
            trainerMessageStatus.Text = $"{trainerMessageTable.Entries.Count} messages for {trainerMessageTable.Entries.Select(entry => entry.Trainer).Distinct().Count()} trainers.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileNotFoundException)
        {
            LogUiError("Trainer messages could not be loaded", ex);
            trainerMessageStatus.Text = ex.Message;
        }
    }

    private string TrainerMessageLabel(int id)
    {
        string name = Name(trainerMessageTrainerNames, id);
        string cls = id < trainerMessageClasses.Length && trainerMessageClasses[id] >= 0 ? Name(trainerMessageClassNames, trainerMessageClasses[id]) : string.Empty;
        int count = trainerMessageTable?.Entries.Count(entry => entry.Trainer == id) ?? 0;
        return $"[{id}] {cls} {name}".Replace("  ", " ") + (count > 0 ? $"  ({count})" : string.Empty);
    }

    private void FillTrainerMessageTrainers(int? select = null)
    {
        if (trainerMessageTable is null) return;
        string query = trainerMessageSearch.Text?.Trim() ?? string.Empty;
        int? keep = select ?? SelectedMessageTrainer;
        int total = Math.Max(trainerMessageTrainerNames.Length, trainerMessageTable.Entries.Count == 0 ? 0 : trainerMessageTable.Entries.Max(entry => entry.Trainer) + 1);
        loadingTrainerMessages = true;
        trainerMessageShownTrainers.Clear();
        trainerMessageTrainerList.Items.Clear();
        for (int id = 0; id < total; id++)
        {
            string label = TrainerMessageLabel(id);
            if (query.Length > 0 && !label.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            trainerMessageShownTrainers.Add(id);
            trainerMessageTrainerList.Items.Add(label);
        }

        int at = keep is int wanted ? trainerMessageShownTrainers.IndexOf(wanted) : -1;
        loadingTrainerMessages = false;
        trainerMessageTrainerList.SelectedIndex = at >= 0 ? at : trainerMessageShownTrainers.Count > 0 ? 0 : -1;
        if (trainerMessageTrainerList.SelectedIndex < 0) ShowTrainerMessageEntries();
    }

    private void ShowTrainerMessageEntries(int selectEntry = 0)
    {
        trainerMessageShownEntries = [];
        trainerMessageEntryList.Items.Clear();
        if (trainerMessageTable is null || SelectedMessageTrainer is not int trainer)
        {
            trainerMessageTitle.Text = string.Empty;
            ShowSelectedTrainerMessage();
            return;
        }

        trainerMessageTitle.Text = TrainerMessageLabel(trainer);
        trainerMessageShownEntries = trainerMessageTable.Entries.Where(entry => entry.Trainer == trainer).ToList();
        foreach (TrainerMessageEntry entry in trainerMessageShownEntries) trainerMessageEntryList.Items.Add(EntryLabel(entry));
        trainerMessageEntryList.SelectedIndex = trainerMessageShownEntries.Count > 0 ? Math.Clamp(selectEntry, 0, trainerMessageShownEntries.Count - 1) : -1;
        ShowSelectedTrainerMessage();
    }

    private static string EntryLabel(TrainerMessageEntry entry)
    {
        string first = entry.Text.Replace("\\n", " ").Replace("\\r", " ").Replace("\\f", " ").Replace("\n", " ");
        return $"{TrainerMessageService.TriggerName(entry.Trigger)}: {(first.Length > 50 ? first[..50] + "..." : first)}";
    }

    private void ShowSelectedTrainerMessage()
    {
        loadingTrainerMessages = true;
        try
        {
            TrainerMessageEntry? entry = SelectedTrainerMessage;
            trainerMessageTrigger.IsEnabled = trainerMessageText.IsEnabled = entry is not null;
            trainerMessageTrigger.Items.Clear();
            if (entry is null)
            {
                trainerMessageText.Text = string.Empty;
                return;
            }

            IEnumerable<int> values = TrainerMessageService.Triggers.Keys.Concat([entry.Trigger]).Distinct().Order();
            foreach (int value in values) trainerMessageTrigger.Items.Add(new TriggerItem(value));
            trainerMessageTrigger.SelectedItem = trainerMessageTrigger.Items.OfType<TriggerItem>().First(item => item.Value == entry.Trigger);
            trainerMessageText.Text = entry.Text;
        }
        finally
        {
            loadingTrainerMessages = false;
        }
    }

    private void AfterTrainerMessageEdit()
    {
        int at = trainerMessageEntryList.SelectedIndex;
        if (at >= 0 && at < trainerMessageShownEntries.Count)
        {
            loadingTrainerMessages = true;
            trainerMessageEntryList.Items[at] = EntryLabel(trainerMessageShownEntries[at]);
            trainerMessageEntryList.SelectedIndex = at;
            loadingTrainerMessages = false;
        }

        ReportEdit(trainerMessageEdits, () => trainerMessageTable, trainerMessageStatus);
    }

    private void AddTrainerMessage()
    {
        if (trainerMessageTable is null || SelectedMessageTrainer is not int trainer) return;
        HashSet<int> used = trainerMessageShownEntries.Select(entry => entry.Trigger).ToHashSet();
        int trigger = TrainerMessageService.Triggers.Keys.FirstOrDefault(key => !used.Contains(key));
        var entry = new TrainerMessageEntry { Trainer = trainer, Trigger = trigger };
        int last = trainerMessageTable.Entries.FindLastIndex(existing => existing.Trainer == trainer);
        trainerMessageTable.Entries.Insert(last >= 0 ? last + 1 : trainerMessageTable.Entries.Count, entry);
        FillTrainerMessageTrainers(trainer);
        ShowTrainerMessageEntries(trainerMessageShownEntries.IndexOf(entry));
        ReportEdit(trainerMessageEdits, () => trainerMessageTable, trainerMessageStatus);
    }

    private void RemoveTrainerMessage()
    {
        if (trainerMessageTable is null || SelectedTrainerMessage is not { } entry || SelectedMessageTrainer is not int trainer) return;
        int at = trainerMessageEntryList.SelectedIndex;
        trainerMessageTable.Entries.Remove(entry);
        FillTrainerMessageTrainers(trainer);
        ShowTrainerMessageEntries(at);
        ReportEdit(trainerMessageEdits, () => trainerMessageTable, trainerMessageStatus);
    }

    private void SaveTrainerMessages()
    {
        if (!TryGetGame(out RomProject project, out RomGameMetadata metadata) || trainerMessageTable is null) return;
        try
        {
            int? trainer = SelectedMessageTrainer;
            TrainerMessageService.Save(project, metadata, trainerMessageTable);
            trainerMessageTable = TrainerMessageService.Load(project, metadata);
            trainerMessageEdits.Loaded(trainerMessageTable, "Trainer messages");
            FillTrainerMessageTrainers(trainer);
            trainerMessageStatus.Text = $"Saved {trainerMessageTable.Entries.Count} trainer messages. They reach the ROM with Save ROM.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            LogUiError("Trainer messages could not be saved", ex);
            trainerMessageStatus.Text = $"The messages could not be saved: {ex.Message}";
        }
    }

    #endregion

    #region Growth Rate Editor

    private readonly EditTracker growthEdits = new();
    private GrowthTables? growthTables;
    private bool loadingGrowth;
    private readonly ListBox growthCurveList = new();
    private readonly StackPanel growthRows = new() { Spacing = 2 };
    private readonly List<(NumericUpDown Value, TextBlock Step)> growthRowControls = [];
    private readonly ComboBox growthFormula = new() { Width = 220 };
    private readonly ComboBox growthCopyFrom = new() { Width = 220 };
    private readonly TextBlock growthStatus = new() { Text = "No ROM loaded.", Foreground = ExtraMutedBrush, TextWrapping = TextWrapping.Wrap };

    private Control BuildGrowthPanel()
    {
        growthCurveList.SelectionChanged += (_, _) => ShowGrowthCurve();
        for (int level = 0; level < GrowthTableService.Levels; level++)
        {
            int captured = level;
            var value = new NumericUpDown { Minimum = 0, Maximum = uint.MaxValue, Increment = 100, FormatString = "0", Width = 170, Height = 30, MinHeight = 30 };
            var step = new TextBlock { Foreground = ExtraMutedBrush, VerticalAlignment = VerticalAlignment.Center, FontSize = 11 };
            value.ValueChanged += (_, _) => GrowthValueEdited(captured, value);
            growthRowControls.Add((value, step));
            growthRows.Children.Add(new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 10,
                Children = { new TextBlock { Text = $"Level {level}", Width = 60, VerticalAlignment = VerticalAlignment.Center }, value, step }
            });
        }

        foreach (string name in GrowthTableService.CurveNames.Take(6)) growthFormula.Items.Add(name);
        growthFormula.SelectedIndex = 0;
        var toolbar = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Bottom,
            Children =
            {
                Field("Standard formula", growthFormula),
                ActionButton("Apply Formula", "Set every level of this curve from the standard formula of the growth rate picked", (_, _) => ApplyGrowthFormula()),
                Field("Copy from", growthCopyFrom),
                ActionButton("Copy", "Replace this curve with the curve picked", (_, _) => CopyGrowthCurve()),
                ActionButton("Save", "Write the experience tables to the project", (_, _) => SaveGrowthTables())
            }
        };
        var content = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*"), RowSpacing = 8 };
        content.Children.Add(toolbar);
        var note = new TextBlock { Text = "The experience a Pokémon has in total at each level. A species uses the curve its personal data names as its growth rate. Levels 0 and 1 are always 0.", Foreground = ExtraMutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap };
        Grid.SetRow(note, 1);
        content.Children.Add(note);
        var scroll = new ScrollViewer { Content = growthRows, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto };
        Grid.SetRow(scroll, 2);
        content.Children.Add(scroll);
        return EditorPage(SideList("Growth rates", new TextBlock { Text = "Pick a curve", Foreground = ExtraMutedBrush, FontSize = 11 }, growthCurveList), content, growthStatus);
    }

    private void ConfigureGrowthEditor()
    {
        if (!TryGetGame(out RomProject project, out RomGameMetadata metadata)) return;
        if (!GrowthTableService.IsSupported(project, metadata))
        {
            growthStatus.Text = "Growth rate tables are only supported for Platinum.";
            return;
        }

        try
        {
            if (growthTables is null || !growthEdits.IsUnsaved(growthTables))
            {
                growthTables = GrowthTableService.Load(project);
                growthEdits.Loaded(growthTables, "Growth rates");
            }

            int keep = Math.Max(growthCurveList.SelectedIndex, 0);
            growthCurveList.Items.Clear();
            growthCopyFrom.Items.Clear();
            for (int i = 0; i < growthTables.Curves.Count; i++)
            {
                growthCurveList.Items.Add($"{i}: {GrowthTableService.CurveName(i)}");
                growthCopyFrom.Items.Add($"{i}: {GrowthTableService.CurveName(i)}");
            }

            growthCopyFrom.SelectedIndex = 0;
            growthCurveList.SelectedIndex = Math.Min(keep, growthTables.Curves.Count - 1);
            growthStatus.Text = $"{growthTables.Curves.Count} growth rates loaded.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            LogUiError("Growth rates could not be loaded", ex);
            growthStatus.Text = ex.Message;
        }
    }

    private GrowthCurve? SelectedGrowthCurve =>
        growthTables is not null && growthCurveList.SelectedIndex >= 0 && growthCurveList.SelectedIndex < growthTables.Curves.Count ? growthTables.Curves[growthCurveList.SelectedIndex] : null;

    private void ShowGrowthCurve()
    {
        if (SelectedGrowthCurve is not { } curve) return;
        loadingGrowth = true;
        for (int level = 0; level < GrowthTableService.Levels; level++) growthRowControls[level].Value.Value = curve.Experience[level];
        loadingGrowth = false;
        RefreshGrowthSteps(curve);
    }

    private void RefreshGrowthSteps(GrowthCurve curve)
    {
        for (int level = 0; level < GrowthTableService.Levels; level++)
        {
            long step = level == 0 ? 0 : (long)curve.Experience[level] - curve.Experience[level - 1];
            growthRowControls[level].Step.Text = level == 0 ? string.Empty : step < 0 ? $"{step:N0} to level up (lower than the level before!)" : $"+{step:N0} from level {level - 1}";
        }
    }

    private void GrowthValueEdited(int level, NumericUpDown value)
    {
        if (loadingGrowth || SelectedGrowthCurve is not { } curve) return;
        curve.Experience[level] = (uint)(value.Value ?? 0);
        RefreshGrowthSteps(curve);
        ReportEdit(growthEdits, () => growthTables, growthStatus);
    }

    private void ApplyGrowthFormula()
    {
        if (SelectedGrowthCurve is not { } curve || growthFormula.SelectedIndex < 0) return;
        for (int level = 0; level < GrowthTableService.Levels; level++) curve.Experience[level] = GrowthTableService.Standard(growthFormula.SelectedIndex, level);
        ShowGrowthCurve();
        ReportEdit(growthEdits, () => growthTables, growthStatus);
    }

    private void CopyGrowthCurve()
    {
        if (SelectedGrowthCurve is not { } curve || growthTables is null || growthCopyFrom.SelectedIndex < 0) return;
        curve.Experience = growthTables.Curves[growthCopyFrom.SelectedIndex].Experience.ToList();
        ShowGrowthCurve();
        ReportEdit(growthEdits, () => growthTables, growthStatus);
    }

    private void SaveGrowthTables()
    {
        if (!TryGetGame(out RomProject project, out _) || growthTables is null) return;
        try
        {
            List<string> decreasing = [];
            for (int curve = 0; curve < growthTables.Curves.Count; curve++)
            {
                List<uint> experience = growthTables.Curves[curve].Experience;
                if (Enumerable.Range(1, experience.Count - 1).Any(level => experience[level] < experience[level - 1])) decreasing.Add(GrowthTableService.CurveName(curve));
            }

            GrowthTableService.Save(project, growthTables);
            growthEdits.Saved(growthTables);
            growthStatus.Text = "Saved the growth rates. They reach the ROM with Save ROM." +
                (decreasing.Count > 0 ? $" Careful: {string.Join(", ", decreasing)} needs less experience at some level than at the level before." : string.Empty);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            LogUiError("Growth rates could not be saved", ex);
            growthStatus.Text = $"The growth rates could not be saved: {ex.Message}";
        }
    }

    #endregion

    #region Battle Frontier Editor

    private readonly EditTracker frontierTowerEdits = new(), frontierStageEdits = new(), frontierTrainerEdits = new();
    private FrontierPokemonSets? frontierTowerSets, frontierStageSets;
    private FrontierTrainers? frontierTrainers;
    private string[] frontierSpecies = [], frontierMoves = [], frontierItems = [], frontierClasses = [];
    private bool loadingFrontier;
    private readonly RadioButton frontierTowerRadio = new() { Content = "Tower Pokémon", GroupName = "FrontierPart", IsChecked = true };
    private readonly RadioButton frontierStageRadio = new() { Content = "Stage Pokémon", GroupName = "FrontierPart" };
    private readonly RadioButton frontierTrainerRadio = new() { Content = "Tower trainers", GroupName = "FrontierPart" };
    private readonly ListBox frontierList = new();
    private readonly TextBox frontierSearch = new() { Watermark = "Search" };
    private readonly List<int> frontierShown = [];
    private readonly ComboBox frontierSpeciesBox = new() { Width = 260 };
    private readonly ComboBox[] frontierMoveBoxes = Enumerable.Range(0, 4).Select(_ => new ComboBox { Width = 260 }).ToArray();
    private readonly ComboBox frontierNatureBox = new() { Width = 260 };
    private readonly ComboBox frontierItemBox = new() { Width = 260 };
    private readonly CheckBox[] frontierEvBoxes = new[] { "HP", "Atk", "Def", "Spe", "SpA", "SpD" }.Select(stat => new CheckBox { Content = stat }).ToArray();
    private readonly NumericUpDown frontierExtraBox = new() { Minimum = 0, Maximum = ushort.MaxValue, Increment = 1, FormatString = "0", Width = 120 };
    private readonly ComboBox frontierClassBox = new() { Width = 260 };
    private readonly TextBox frontierSetIdsBox = new() { AcceptsReturn = true, TextWrapping = TextWrapping.Wrap, MinHeight = 120, FontFamily = new FontFamily("monospace") };
    private readonly TextBlock frontierSetNames = new() { Foreground = ExtraMutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap };
    private readonly StackPanel frontierSetPanel = new() { Spacing = 10 };
    private readonly StackPanel frontierTrainerPanel = new() { Spacing = 10 };
    private readonly TextBlock frontierTitle = new() { FontWeight = FontWeight.SemiBold, FontSize = 15 };
    private readonly TextBlock frontierStatus = new() { Text = "No ROM loaded.", Foreground = ExtraMutedBrush, TextWrapping = TextWrapping.Wrap };

    private enum FrontierPart { Tower, Stage, Trainers }

    private FrontierPart CurrentFrontierPart => frontierStageRadio.IsChecked == true ? FrontierPart.Stage : frontierTrainerRadio.IsChecked == true ? FrontierPart.Trainers : FrontierPart.Tower;

    private FrontierPokemonSets? CurrentFrontierSets => CurrentFrontierPart == FrontierPart.Stage ? frontierStageSets : frontierTowerSets;

    private EditTracker CurrentFrontierTracker => CurrentFrontierPart switch { FrontierPart.Stage => frontierStageEdits, FrontierPart.Trainers => frontierTrainerEdits, _ => frontierTowerEdits };

    private object? CurrentFrontierDocument => CurrentFrontierPart == FrontierPart.Trainers ? frontierTrainers : CurrentFrontierSets;

    private Control BuildFrontierPanel()
    {
        foreach (RadioButton radio in new[] { frontierTowerRadio, frontierStageRadio, frontierTrainerRadio }) radio.IsCheckedChanged += (_, _) => { if (radio.IsChecked == true) FillFrontierList(0); };
        frontierSearch.TextChanged += (_, _) => FillFrontierList(null);
        frontierList.SelectionChanged += (_, _) => ShowFrontierItem();

        frontierSpeciesBox.SelectionChanged += (_, _) => EditFrontierSet(set => set.Species = (ushort)Math.Max(frontierSpeciesBox.SelectedIndex, 0));
        for (int move = 0; move < 4; move++)
        {
            int captured = move;
            frontierMoveBoxes[move].SelectionChanged += (_, _) => EditFrontierSet(set => set.Moves[captured] = (ushort)Math.Max(frontierMoveBoxes[captured].SelectedIndex, 0));
        }

        for (int stat = 0; stat < 6; stat++)
        {
            int captured = stat;
            frontierEvBoxes[stat].IsCheckedChanged += (_, _) => EditFrontierSet(set => set.EvFlags = (byte)(frontierEvBoxes[captured].IsChecked == true ? set.EvFlags | (1 << captured) : set.EvFlags & ~(1 << captured)));
        }

        foreach (string nature in TrainerPersonality.Natures) frontierNatureBox.Items.Add(nature);
        frontierNatureBox.SelectionChanged += (_, _) => EditFrontierSet(set => set.Nature = (byte)Math.Max(frontierNatureBox.SelectedIndex, 0));
        frontierItemBox.SelectionChanged += (_, _) => EditFrontierSet(set => set.Item = (ushort)Math.Max(frontierItemBox.SelectedIndex, 0));
        frontierExtraBox.ValueChanged += (_, _) => EditFrontierSet(set => set.Extra = (ushort)(frontierExtraBox.Value ?? 0));

        frontierClassBox.SelectionChanged += (_, _) =>
        {
            if (loadingFrontier || SelectedFrontierTrainer is not { } trainer || frontierClassBox.SelectedIndex < 0) return;
            trainer.TrainerClass = (ushort)frontierClassBox.SelectedIndex;
            AfterFrontierEdit();
        };
        frontierSetIdsBox.TextChanged += (_, _) =>
        {
            if (loadingFrontier || SelectedFrontierTrainer is not { } trainer) return;
            if (!TryParseSetIds(frontierSetIdsBox.Text, out List<ushort>? ids))
            {
                frontierStatus.Text = "The sets must be numbers separated by commas or spaces.";
                return;
            }

            trainer.SetIds = ids!;
            frontierSetNames.Text = DescribeSetIds(ids!);
            AfterFrontierEdit();
        };

        frontierSetPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Field("Pokémon", frontierSpeciesBox), Field("Nature", frontierNatureBox) } });
        frontierSetPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Field("Move 1", frontierMoveBoxes[0]), Field("Move 2", frontierMoveBoxes[1]) } });
        frontierSetPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Field("Move 3", frontierMoveBoxes[2]), Field("Move 4", frontierMoveBoxes[3]) } });
        frontierSetPanel.Children.Add(new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Field("Held item", frontierItemBox), Field("Spare value", frontierExtraBox) } });
        var evs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12 };
        foreach (CheckBox box in frontierEvBoxes) evs.Children.Add(box);
        frontierSetPanel.Children.Add(Field("Stats that get effort values", evs));

        frontierTrainerPanel.Children.Add(Field("Trainer class", frontierClassBox));
        frontierTrainerPanel.Children.Add(Field("Pokémon sets it may bring (set numbers of the Tower Pokémon list)", frontierSetIdsBox));
        frontierTrainerPanel.Children.Add(frontierSetNames);

        var tabs = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 14, Children = { frontierTowerRadio, frontierStageRadio, frontierTrainerRadio } };
        var content = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { tabs, ActionButton("Save", "Write the Battle Tower and Battle Stage data to the project", (_, _) => SaveFrontierAll()) } },
                frontierTitle, frontierSetPanel, frontierTrainerPanel
            }
        };
        return EditorPage(SideList("Entries", frontierSearch, frontierList), new ScrollViewer { Content = content, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto }, frontierStatus);
    }

    private static bool TryParseSetIds(string? text, out List<ushort>? ids)
    {
        ids = [];
        foreach (string part in (text ?? string.Empty).Split([',', ' ', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries))
        {
            if (!ushort.TryParse(part, out ushort id)) return false;
            ids.Add(id);
        }

        return true;
    }

    private string DescribeSetIds(IEnumerable<ushort> ids) =>
        string.Join(", ", ids.Take(12).Select(id => frontierTowerSets is not null && id < frontierTowerSets.Sets.Count ? $"{id} = {Name(frontierSpecies, frontierTowerSets.Sets[id].Species)}" : $"{id} = (missing)")) + (ids.Count() > 12 ? $", and {ids.Count() - 12} more" : string.Empty);

    private void ConfigureFrontierEditor()
    {
        if (!TryGetGame(out RomProject project, out RomGameMetadata metadata)) return;
        if (!FrontierService.IsSupported(project, metadata))
        {
            frontierStatus.Text = "The Battle Frontier editor is only supported for Platinum.";
            return;
        }

        try
        {
            if (frontierTowerSets is null || !frontierTowerEdits.IsUnsaved(frontierTowerSets))
            {
                frontierTowerSets = FrontierService.LoadSets(project, FrontierFacility.Tower);
                frontierTowerEdits.Loaded(frontierTowerSets, "Battle Tower Pokémon");
            }

            if (frontierStageSets is null || !frontierStageEdits.IsUnsaved(frontierStageSets))
            {
                frontierStageSets = FrontierService.LoadSets(project, FrontierFacility.Stage);
                frontierStageEdits.Loaded(frontierStageSets, "Battle Stage Pokémon");
            }

            if (frontierTrainers is null || !frontierTrainerEdits.IsUnsaved(frontierTrainers))
            {
                frontierTrainers = FrontierService.LoadTrainers(project);
                frontierTrainerEdits.Loaded(frontierTrainers, "Battle Tower trainers");
            }

            frontierSpecies = LoadNames(project, metadata, TrainerService.GetPokemonNamesArchiveId(metadata));
            frontierMoves = LoadNames(project, metadata, TrainerService.GetAttackNamesArchiveId(metadata));
            frontierItems = LoadNames(project, metadata, TrainerService.GetItemNamesArchiveId(metadata));
            frontierClasses = LoadNames(project, metadata, TrainerService.GetTrainerClassNamesArchiveId(metadata));
            loadingFrontier = true;
            SetItems(frontierSpeciesBox, frontierSpecies.Select((name, i) => $"[{i:D3}] {name}"));
            foreach (ComboBox box in frontierMoveBoxes) SetItems(box, frontierMoves.Select((name, i) => $"[{i:D3}] {name}"));
            SetItems(frontierItemBox, frontierItems.Select((name, i) => $"[{i:D3}] {name}"));
            SetItems(frontierClassBox, frontierClasses.Select((name, i) => $"[{i:D3}] {name}"));
            loadingFrontier = false;
            FillFrontierList(null);
            frontierStatus.Text = $"{frontierTowerSets.Sets.Count - 1} Tower sets, {frontierStageSets.Sets.Count - 1} Stage sets and {frontierTrainers.Trainers.Count} Tower trainers loaded.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileNotFoundException)
        {
            loadingFrontier = false;
            LogUiError("Battle Frontier data could not be loaded", ex);
            frontierStatus.Text = ex.Message;
        }
    }

    private string FrontierLabel(int index) => CurrentFrontierPart == FrontierPart.Trainers
        ? $"[{index:D3}] {(frontierTrainers is not null && index < frontierTrainers.Trainers.Count ? $"{Name(frontierClasses, frontierTrainers.Trainers[index].TrainerClass)}, {frontierTrainers.Trainers[index].SetIds.Count} sets" : string.Empty)}"
        : $"[{index:D3}] {(CurrentFrontierSets is { } sets && index < sets.Sets.Count ? Name(frontierSpecies, sets.Sets[index].Species) : string.Empty)}";

    private void FillFrontierList(int? select)
    {
        int count = CurrentFrontierPart == FrontierPart.Trainers ? frontierTrainers?.Trainers.Count ?? 0 : CurrentFrontierSets?.Sets.Count ?? 0;
        string query = frontierSearch.Text?.Trim() ?? string.Empty;
        int? keep = select ?? (frontierList.SelectedIndex >= 0 && frontierList.SelectedIndex < frontierShown.Count ? frontierShown[frontierList.SelectedIndex] : null);
        loadingFrontier = true;
        frontierShown.Clear();
        frontierList.Items.Clear();
        for (int i = CurrentFrontierPart == FrontierPart.Trainers ? 0 : 1; i < count; i++)
        {
            string label = FrontierLabel(i);
            if (query.Length > 0 && !label.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            frontierShown.Add(i);
            frontierList.Items.Add(label);
        }

        frontierSetPanel.IsVisible = CurrentFrontierPart != FrontierPart.Trainers;
        frontierTrainerPanel.IsVisible = CurrentFrontierPart == FrontierPart.Trainers;
        int at = keep is int wanted ? frontierShown.IndexOf(wanted) : -1;
        loadingFrontier = false;
        frontierList.SelectedIndex = at >= 0 ? at : frontierShown.Count > 0 ? 0 : -1;
        if (frontierList.SelectedIndex < 0) ShowFrontierItem();
    }

    private int SelectedFrontierIndex => frontierList.SelectedIndex >= 0 && frontierList.SelectedIndex < frontierShown.Count ? frontierShown[frontierList.SelectedIndex] : -1;

    private FrontierTrainer? SelectedFrontierTrainer =>
        CurrentFrontierPart == FrontierPart.Trainers && frontierTrainers is not null && SelectedFrontierIndex is >= 0 and var index && index < frontierTrainers.Trainers.Count ? frontierTrainers.Trainers[index] : null;

    private FrontierPokemon? SelectedFrontierSet =>
        CurrentFrontierPart != FrontierPart.Trainers && CurrentFrontierSets is { } sets && SelectedFrontierIndex is >= 0 and var index && index < sets.Sets.Count ? sets.Sets[index] : null;

    private void ShowFrontierItem()
    {
        loadingFrontier = true;
        try
        {
            frontierTitle.Text = SelectedFrontierIndex >= 0 ? FrontierLabel(SelectedFrontierIndex) : string.Empty;
            if (SelectedFrontierSet is { } set)
            {
                frontierSpeciesBox.SelectedIndex = Math.Min(set.Species, frontierSpeciesBox.Items.Count - 1);
                for (int move = 0; move < 4; move++) frontierMoveBoxes[move].SelectedIndex = Math.Min(set.Moves[move], frontierMoveBoxes[move].Items.Count - 1);
                frontierNatureBox.SelectedIndex = Math.Min((int)set.Nature, frontierNatureBox.Items.Count - 1);
                frontierItemBox.SelectedIndex = Math.Min(set.Item, frontierItemBox.Items.Count - 1);
                frontierExtraBox.Value = set.Extra;
                for (int stat = 0; stat < 6; stat++) frontierEvBoxes[stat].IsChecked = (set.EvFlags & (1 << stat)) != 0;
            }
            else if (SelectedFrontierTrainer is { } trainer)
            {
                frontierClassBox.SelectedIndex = Math.Min((int)trainer.TrainerClass, frontierClassBox.Items.Count - 1);
                frontierSetIdsBox.Text = string.Join(", ", trainer.SetIds);
                frontierSetNames.Text = DescribeSetIds(trainer.SetIds);
            }
        }
        finally
        {
            loadingFrontier = false;
        }
    }

    private void EditFrontierSet(Action<FrontierPokemon> edit)
    {
        if (loadingFrontier || SelectedFrontierSet is not { } set) return;
        edit(set);
        AfterFrontierEdit();
    }

    private void AfterFrontierEdit()
    {
        int at = frontierList.SelectedIndex;
        if (at >= 0 && at < frontierShown.Count)
        {
            loadingFrontier = true;
            frontierList.Items[at] = FrontierLabel(frontierShown[at]);
            frontierList.SelectedIndex = at;
            frontierTitle.Text = FrontierLabel(frontierShown[at]);
            loadingFrontier = false;
        }

        ReportEdit(CurrentFrontierTracker, () => CurrentFrontierDocument, frontierStatus);
    }

    private void SaveFrontierAll()
    {
        SaveFrontierSets(FrontierFacility.Tower);
        SaveFrontierSets(FrontierFacility.Stage);
        SaveFrontierTrainers();
    }

    private void SaveFrontierSets(FrontierFacility facility)
    {
        if (!TryGetGame(out RomProject project, out _)) return;
        FrontierPokemonSets? sets = facility == FrontierFacility.Tower ? frontierTowerSets : frontierStageSets;
        EditTracker tracker = facility == FrontierFacility.Tower ? frontierTowerEdits : frontierStageEdits;
        if (sets is null || !tracker.IsUnsaved(sets)) return;
        try
        {
            FrontierService.SaveSets(project, facility, sets);
            tracker.Saved(sets);
            frontierStatus.Text = $"Saved the Battle {facility} Pokémon. They reach the ROM with Save ROM.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            LogUiError($"Battle {facility} Pokémon could not be saved", ex);
            frontierStatus.Text = $"The {facility} Pokémon could not be saved: {ex.Message}";
        }
    }

    private void SaveFrontierTrainers()
    {
        if (!TryGetGame(out RomProject project, out _) || frontierTrainers is null || !frontierTrainerEdits.IsUnsaved(frontierTrainers)) return;
        try
        {
            FrontierService.SaveTrainers(project, frontierTrainers);
            frontierTrainerEdits.Saved(frontierTrainers);
            frontierStatus.Text = "Saved the Battle Tower trainers. They reach the ROM with Save ROM.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            LogUiError("Battle Tower trainers could not be saved", ex);
            frontierStatus.Text = $"The trainers could not be saved: {ex.Message}";
        }
    }

    #endregion
}
