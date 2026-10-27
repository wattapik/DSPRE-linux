using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Pokémon Editor's Shadow and Areas tabs (see PokemonShadowService, PokedexAreaService and PokedexMapService): how the open
/// species is drawn on the battle ground, and the Pokédex's area map - which places it is listed in, and the places themselves. Like the
/// Footprint tab, each keeps its whole table in memory, tracked by an <see cref="EditTracker"/>, and is written by its Save button or
/// Save All.</summary>
public partial class MainWindow
{
    private readonly EditTracker shadowEdits = new();
    private PokemonShadows? shadowSet;
    private int shadowSpecies = -1;
    private bool loadingShadow;
    private readonly ComboBox shadowSizeBox = new() { Width = 200 };
    private readonly NumericUpDown shadowXBox = new() { Minimum = sbyte.MinValue, Maximum = sbyte.MaxValue, Increment = 1, FormatString = "0", Width = 150 };
    private readonly NumericUpDown shadowYBox = new() { Minimum = sbyte.MinValue, Maximum = sbyte.MaxValue, Increment = 1, FormatString = "0", Width = 150 };
    private readonly TextBlock shadowTitle = new() { FontWeight = FontWeight.SemiBold, FontSize = 15 };
    private readonly TextBlock shadowStatus = new() { Foreground = ExtraMutedBrush, TextWrapping = TextWrapping.Wrap };
    private StackPanel? shadowControls;

    private readonly EditTracker areaEdits = new(), areaMapEdits = new();
    private PokedexAreas? areaSet;
    private PokedexAreaMap? areaMap;
    private IReadOnlyList<string> areaSpecialNames = [], areaRouteNames = [];
    private readonly Dictionary<uint, string> areaHeaderNames = [];
    private int areaSpecies = -1;
    private bool loadingAreas;
    private readonly AreaMapCanvas areaCanvas = new();
    private readonly ComboBox areaCategoryBox = new() { Name = "AreaCategorySelector", Width = 360 };
    private readonly WrapPanel areaChecks = new() { Orientation = Orientation.Horizontal, Width = 520 };
    private readonly TextBlock areaTitle = new() { FontWeight = FontWeight.SemiBold, FontSize = 15 };
    private readonly TextBlock areaSummary = new() { Foreground = ExtraMutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap, Width = 520 };
    private readonly TextBlock areaStatus = new() { Foreground = ExtraMutedBrush, TextWrapping = TextWrapping.Wrap };
    private StackPanel? areaControls;

    private void AddShadowTab()
    {
        foreach (ShadowSize size in Enum.GetValues<ShadowSize>()) shadowSizeBox.Items.Add(size.ToString());
        shadowSizeBox.SelectionChanged += (_, _) => EditShadow(entry => entry.Size = (ShadowSize)Math.Max(shadowSizeBox.SelectedIndex, 0));
        shadowXBox.ValueChanged += (_, _) => EditShadow(entry => entry.ShadowOffsetX = (sbyte)(shadowXBox.Value ?? 0));
        shadowYBox.ValueChanged += (_, _) => EditShadow(entry => entry.SpriteOffsetY = (sbyte)(shadowYBox.Value ?? 0));

        shadowControls = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                ActionButton("Save Shadows", "Write the shadow settings of every species to the project", (_, _) => SaveShadows()),
                new TextBlock { Text = "How this species is drawn in battle. The shadow size is none, small, medium or large. The two offsets are signed whole numbers: one moves the shadow sideways, the other moves the Pokémon's sprite up or down. How far one step moves them has not been measured, so try a value and look at it in the game.", Foreground = ExtraMutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { Field("Shadow size", shadowSizeBox), Field("Shadow offset X", shadowXBox), Field("Sprite offset Y", shadowYBox) } }
            }
        };
        var content = new StackPanel { Spacing = 10, Margin = new Thickness(0, 8, 0, 0), Children = { shadowTitle, shadowControls, shadowStatus } };
        PokemonEditorTabs.Items.Insert(PokemonEditorTabs.Items.IndexOf(PokemonSpritesTab) + 1, new TabItem { Header = "Shadow", Content = new ScrollViewer { Content = content } });
    }

    private void AddAreasTab()
    {
        foreach (string category in PokedexAreaService.Categories) areaCategoryBox.Items.Add(category);
        areaCategoryBox.SelectedIndex = 5;
        areaCategoryBox.SelectionChanged += (_, _) => { if (!loadingAreas) ShowAreaChecks(); };
        areaCanvas.CellInteracted += AreaCellInteracted;
        areaCanvas.Describe = (x, y) => DescribeMapCell(areaCanvas, x, y);

        var lists = new StackPanel
        {
            Spacing = 10, Width = 540,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom,
                    Children =
                    {
                        Field("List", areaCategoryBox),
                        ActionButton("Clear List", "Take this species off every place of the list", (_, _) => ChangeAreaList(_ => [])),
                        ActionButton("Save Pokédex Areas", "Write the area lists of every species to the project", (_, _) => SavePokedexAreas())
                    }
                },
                new TextBlock { Text = "The places the Pokédex's area map marks for this species, for each time of day, the Honey Tree and the Poké Radar. Tick a place - or click it on the map - to list the species there. This only changes the map: where the Pokémon really appears is set in the Wild Pokemon Encounter Editor, and the places themselves are in the Pokedex Map Editor.", Foreground = ExtraMutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap, Width = 520 },
                areaChecks,
                areaSummary
            }
        };
        areaControls = new StackPanel
        {
            Spacing = 10,
            Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 16, Children = { new Border { BorderBrush = new SolidColorBrush(Color.Parse("#33414D")), BorderThickness = new Thickness(1), Child = areaCanvas, VerticalAlignment = VerticalAlignment.Top }, lists } } }
        };
        var content = new StackPanel { Spacing = 10, Margin = new Thickness(0, 8, 0, 0), Children = { areaTitle, areaControls, areaStatus } };
        var tab = new TabItem { Header = "Areas", Content = new ScrollViewer { Content = content, HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto } };
        PokemonEditorTabs.Items.Insert(PokemonEditorTabs.Items.IndexOf(footprintTab!) + 1, tab);
    }

    private static Button MarginedButton(string text, string tip, Action action)
    {
        Button button = ActionButton(text, tip, (_, _) => action());
        button.Margin = new Thickness(0, 0, 8, 8);
        return button;
    }

    private string SpeciesLabel(int species) => $"[{species:D3}] {(species >= 0 && species < PokeEditorSpeciesNames.Count ? PokeEditorSpeciesNames[species] : $"#{species}")}";

    #region Shadow

    private void ShowShadowForSpecies(int species)
    {
        shadowSpecies = species;
        if (shadowControls is null) return;
        if (!TryGetGame(out RomProject project, out RomGameMetadata metadata) || !PokemonShadowService.IsSupported(project, metadata))
        {
            ShowNoShadow("Shadows are only supported for Platinum.");
            return;
        }

        try
        {
            if (shadowSet is null)
            {
                shadowSet = PokemonShadowService.Load(project);
                shadowEdits.Loaded(shadowSet, "Battle shadows");
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            LogUiError("Battle shadows could not be loaded", ex);
            ShowNoShadow(ex.Message);
            return;
        }

        if (species < 1 || species >= shadowSet.Species.Count)
        {
            ShowNoShadow("This entry has no shadow settings of its own: a form is drawn with its species.");
            return;
        }

        loadingShadow = true;
        try
        {
            ShadowEntry entry = shadowSet.Species[species];
            shadowControls.IsVisible = true;
            shadowTitle.Text = $"Battle shadow of {SpeciesLabel(species)}";
            shadowSizeBox.SelectedIndex = (int)entry.Size;
            shadowXBox.Value = entry.ShadowOffsetX;
            shadowYBox.Value = entry.SpriteOffsetY;
        }
        finally
        {
            loadingShadow = false;
        }
    }

    private void ShowNoShadow(string reason)
    {
        shadowControls!.IsVisible = false;
        shadowTitle.Text = reason;
    }

    private void EditShadow(Action<ShadowEntry> edit)
    {
        if (loadingShadow || shadowSet is null || shadowSpecies < 1 || shadowSpecies >= shadowSet.Species.Count) return;
        edit(shadowSet.Species[shadowSpecies]);
        ReportEdit(shadowEdits, () => shadowSet, shadowStatus);
    }

    private void SaveShadows()
    {
        if (!TryGetGame(out RomProject project, out _) || shadowSet is null || !shadowEdits.IsUnsaved(shadowSet)) return;
        try
        {
            PokemonShadowService.Save(project, shadowSet);
            shadowEdits.Saved(shadowSet);
            shadowStatus.Text = "Saved the shadow settings. They reach the ROM with Save ROM.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            LogUiError("Battle shadows could not be saved", ex);
            shadowStatus.Text = $"The shadow settings could not be saved: {ex.Message}";
        }
    }

    #endregion

    #region Areas

    /// <summary>Reads the map's places and draws the Town Map picture the first time either page needs them.</summary>
    private bool EnsureAreaMapLoaded(RomProject project, RomGameMetadata metadata)
    {
        if (areaMap is null)
        {
            if (!PokedexMapService.IsSupported(project, metadata)) return false;
            areaMap = PokedexMapService.Load(project);
            areaMapEdits.Loaded(areaMap, "Pokédex area map");
        }

        areaCanvas.Map = pokedexMapCanvas.Map = areaMap;
        EnsureTownPictureLoaded(project);
        if (areaSpecialNames.Count == 0) RefreshAreaPlaceNames(project, metadata);
        return true;
    }

    private void ShowAreasForSpecies(int species)
    {
        areaSpecies = species;
        if (areaControls is null) return;
        if (!TryGetGame(out RomProject project, out RomGameMetadata metadata) || !PokedexAreaService.IsSupported(project, metadata))
        {
            ShowNoAreas("Pokédex area lists are only supported for Platinum.");
            return;
        }

        try
        {
            if (areaSet is null)
            {
                areaSet = PokedexAreaService.Load(project);
                areaEdits.Loaded(areaSet, "Pokédex areas");
            }

            EnsureAreaMapLoaded(project, metadata);
            if (areaMap is null && areaSpecialNames.Count == 0) RefreshAreaPlaceNames(project, metadata);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            LogUiError("Pokédex areas could not be loaded", ex);
            ShowNoAreas(ex.Message);
            return;
        }

        if (species < 1 || species >= areaSet.Species.Count)
        {
            ShowNoAreas("This entry has no area lists of its own: a form is listed with its species.");
            return;
        }

        areaControls.IsVisible = true;
        areaTitle.Text = $"Pokédex areas of {SpeciesLabel(species)}";
        ShowAreaChecks();
    }

    private void ShowNoAreas(string reason)
    {
        areaControls!.IsVisible = false;
        areaTitle.Text = reason;
    }

    private void RefreshAreaPlaceNames(RomProject project, RomGameMetadata metadata)
    {
        if (areaMap is not null)
        {
            areaSpecialNames = areaMap.Specials.Select(place => HeaderLocationName(place.Header)).ToList();
            areaRouteNames = areaMap.Routes.Select(place => HeaderLocationName(place.Header)).ToList();
            return;
        }

        areaSpecialNames = PokedexAreaService.PlaceNames(project, metadata, true);
        areaRouteNames = PokedexAreaService.PlaceNames(project, metadata, false);
    }

    private string HeaderLocationName(uint header)
    {
        if (areaHeaderNames.TryGetValue(header, out string? cached)) return cached;
        string name = "(no such map header)";
        if (TryGetGame(out RomProject project, out RomGameMetadata metadata) && MapHeaderStoreFactory.Create(project, metadata) is { } store && header < store.Count)
        {
            IReadOnlyList<HeaderOption> locations = project.GetLocationNames(metadata);
            ushort location = store.Load((ushort)header).LocationNameId;
            name = location < locations.Count ? locations[location].Label : $"location {location}";
        }

        return areaHeaderNames[header] = name;
    }

    private AreaList? SelectedAreaList =>
        areaSet is not null && areaSpecies >= 1 && areaSpecies < areaSet.Species.Count && areaCategoryBox.SelectedIndex >= 0 ? areaSet.Species[areaSpecies].Lists[areaCategoryBox.SelectedIndex] : null;

    /// <summary>The map's places changed in the Pokedex Map Editor: the Areas tab's names and map follow.</summary>
    private void AreaMapChanged()
    {
        if (TryGetGame(out RomProject project, out RomGameMetadata metadata)) RefreshAreaPlaceNames(project, metadata);
        areaCanvas.InvalidateVisual();
        if (areaControls?.IsVisible == true) ShowAreaChecks();
    }

    private void ShowAreaChecks()
    {
        areaChecks.Children.Clear();
        int category = areaCategoryBox.SelectedIndex;
        areaCanvas.SpecialPlaces = category >= 0 && PokedexAreaService.UsesSpecialPlaces(category);
        areaCanvas.Selected = 0;
        if (SelectedAreaList is not { } list)
        {
            areaCanvas.Listed = new HashSet<int>();
            areaCanvas.InvalidateVisual();
            return;
        }

        areaCanvas.Listed = list.Areas.ToHashSet();
        IReadOnlyList<string> names = PokedexAreaService.UsesSpecialPlaces(category) ? areaSpecialNames : areaRouteNames;
        int placeCount = areaMap is null ? names.Count : PokedexAreaService.UsesSpecialPlaces(category) ? areaMap.Specials.Count : areaMap.Routes.Count;
        for (int area = 1; area <= placeCount; area++)
        {
            int captured = area;
            var check = new CheckBox { Content = $"{area}: {(area <= names.Count ? names[area - 1] : $"Area {area}")}", IsChecked = list.Areas.Contains(area), MinWidth = 250, Margin = new Thickness(0, 0, 8, 0) };
            check.IsCheckedChanged += (_, _) => SetAreaListed(captured, check.IsChecked == true);
            areaChecks.Children.Add(check);
        }

        RefreshAreaSummary();
        areaCanvas.InvalidateVisual();
    }

    private void SetAreaListed(int area, bool listed)
    {
        if (loadingAreas || SelectedAreaList is not { } list || list.Areas.Contains(area) == listed) return;
        List<int> areas = list.Areas.Where(existing => existing != area).ToList();
        if (listed) areas.Add(area);
        list.Areas = areas.Order().ToList();
        areaCanvas.Listed = list.Areas.ToHashSet();
        areaCanvas.InvalidateVisual();
        RefreshAreaSummary();
        ReportEdit(areaEdits, () => areaSet, areaStatus);
    }

    private void ChangeAreaList(Func<List<int>, List<int>> change)
    {
        if (SelectedAreaList is not { } list) return;
        list.Areas = change(list.Areas);
        ShowAreaChecks();
        ReportEdit(areaEdits, () => areaSet, areaStatus);
    }

    private void RefreshAreaSummary()
    {
        if (areaSet is null || areaSpecies < 1 || areaSpecies >= areaSet.Species.Count) return;
        areaSummary.Text = string.Join("   ", Enumerable.Range(0, PokedexAreaService.CategoryCount)
            .Select(category => $"{PokedexAreaService.Categories[category].Replace("Special places", "Special").Replace("Routes and cities", "Routes")}: {areaSet.Species[areaSpecies].Lists[category].Areas.Count}"));
    }

    /// <summary>The area (of the group the canvas works on) whose cells or marker include the cell, or 0.</summary>
    private static int AreaAt(AreaMapCanvas canvas, PokedexAreaMap? map, int x, int y)
    {
        if (map is null) return 0;
        if (canvas.SpecialPlaces) return map.Specials.FindIndex(place => place.X == x && place.Y == y) + 1;
        return map.Routes.FindIndex(place => place.Covers(x, y)) + 1;
    }

    private string? DescribeMapCell(AreaMapCanvas canvas, int x, int y)
    {
        if (areaMap is null) return null;
        foreach (bool specials in new[] { canvas.SpecialPlaces, !canvas.SpecialPlaces })
        {
            int area = specials ? areaMap.Specials.FindIndex(place => place.X == x && place.Y == y) + 1 : areaMap.Routes.FindIndex(place => place.Covers(x, y)) + 1;
            if (area > 0) return $"{(specials ? "Special place" : "Area")} {area}: {(specials ? areaSpecialNames : areaRouteNames)[area - 1]}";
        }

        return null;
    }

    private void AreaCellInteracted(int x, int y, bool secondary, bool press)
    {
        if (!press) return;
        int area = AreaAt(areaCanvas, areaMap, x, y);
        if (area == 0 || SelectedAreaList is not { } list) return;
        SetAreaListed(area, !secondary && !list.Areas.Contains(area));
        ShowAreaChecks();
    }

    private void SavePokedexAreas()
    {
        if (!TryGetGame(out RomProject project, out _) || areaSet is null || !areaEdits.IsUnsaved(areaSet)) return;
        try
        {
            // The lists may name places the map does not have saved yet.
            SaveAreaMap();
            if (areaMap is not null && areaMapEdits.IsUnsaved(areaMap)) return;
            PokedexAreaService.Save(project, areaSet);
            areaEdits.Saved(areaSet);
            areaStatus.Text = "Saved the Pokédex area lists. They reach the ROM with Save ROM.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            LogUiError("Pokédex areas could not be saved", ex);
            areaStatus.Text = $"The area lists could not be saved: {ex.Message}";
        }
    }

    #endregion
}
