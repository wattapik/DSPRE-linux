using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Pokedex Map Editor: the places of the Pokédex's area map (see PokedexMapService) on the Town Map picture. The species lists
/// that tick these places are in the Pokémon Editor's Areas tab, which shares the map loaded here (<see cref="EnsureAreaMapLoaded"/>).</summary>
public partial class MainWindow
{
    private readonly AreaMapCanvas pokedexMapCanvas = new();
    private readonly RadioButton areaRoutesRadio = new() { Name = "AreaRoutesRadio", Content = "Routes and cities", GroupName = "AreaGroup", IsChecked = true };
    private readonly RadioButton areaSpecialsRadio = new() { Name = "AreaSpecialsRadio", Content = "Special places", GroupName = "AreaGroup" };
    private readonly ListBox areaPlaceList = new() { Name = "AreaPlaceList" };
    private readonly NumericUpDown areaHeaderBox = new() { Minimum = 0, Maximum = ushort.MaxValue, Increment = 1, FormatString = "0", Width = 150 };
    private readonly TextBlock areaHeaderName = new() { Foreground = ExtraMutedBrush, FontSize = 11, VerticalAlignment = VerticalAlignment.Center };
    private readonly NumericUpDown areaXBox = new() { Minimum = 0, Maximum = 255, Increment = 1, FormatString = "0", Width = 120 };
    private readonly NumericUpDown areaYBox = new() { Minimum = 0, Maximum = 255, Increment = 1, FormatString = "0", Width = 120 };
    private readonly TextBlock pokedexMapStatus = new() { Text = "No ROM loaded.", Foreground = ExtraMutedBrush, TextWrapping = TextWrapping.Wrap };
    private StackPanel? areaSpecialFields, areaRouteFields;
    private bool loadingAreaPlace;

    /// <summary>Everything about one of the two Town Map pictures: the picture, its palette and bitmap, whether it has unsaved edits and its
    /// undo history. The editor shows one at a time (<see cref="townView"/>); the properties below are those of that one.</summary>
    private sealed class TownPictureState(TownMapView view)
    {
        public TownMapView View { get; } = view;
        public TownMapPicture? Picture { get; set; }
        public RgbColor[] Palette { get; set; } = [];
        public global::Avalonia.Media.Imaging.WriteableBitmap? Bitmap { get; set; }
        public EditTracker Edits { get; } = new();
        public List<List<string>> Undo { get; } = [];
        public List<List<string>> Redo { get; } = [];
        public DateTime LastEdit { get; set; }
    }

    private Dictionary<TownMapView, TownPictureState> townStates = NewTownStates();
    private TownMapView townView = TownMapView.Terrain;
    private bool pictureUndoneLast;

    /// <summary>Editing the Town Map picture is switched off for now: an edit to either picture did not show up in the game's Pokédex or Pokétch
    /// (see CLAUDE.md), so the Places/Picture switch and the Picture selector are hidden. The picture is still drawn behind the places.
    /// Everything else (the service, undo, the painting tools) is kept and comes back by setting this to true.</summary>
    private static readonly bool PictureEditingEnabled = false;

    private static Dictionary<TownMapView, TownPictureState> NewTownStates() => new() { [TownMapView.Routes] = new(TownMapView.Routes), [TownMapView.Terrain] = new(TownMapView.Terrain) };

    private TownPictureState Town => townStates[townView];
    private TownMapPicture? townPicture { get => Town.Picture; set => Town.Picture = value; }
    private RgbColor[] townPalette => Town.Palette;
    private global::Avalonia.Media.Imaging.WriteableBitmap? areaPicture => Town.Bitmap;
    private EditTracker townPictureEdits => Town.Edits;
    private List<List<string>> townPictureUndo => Town.Undo;
    private List<List<string>> townPictureRedo => Town.Redo;
    private DateTime townPictureLastEdit { get => Town.LastEdit; set => Town.LastEdit = value; }
    private int PictureUndoCount => townStates.Values.Sum(state => state.Undo.Count);
    private int PictureRedoCount => townStates.Values.Sum(state => state.Redo.Count);
    private DateTime LatestPictureEdit => townStates.Values.Where(state => state.Undo.Count > 0).Select(state => state.LastEdit).DefaultIfEmpty(DateTime.MinValue).Max();

    private void ResetTownPictures()
    {
        townStates = NewTownStates();
        pokedexMapCanvas.Picture = areaCanvas.Picture = null;
        pdmSwatches.Items.Clear();
    }

    /// <summary>Reads the shown picture (and draws its bitmap) the first time it is needed, and puts it on both maps.</summary>
    private void EnsureTownPictureLoaded(RomProject project)
    {
        TownPictureState state = Town;
        if (state.Picture is null)
        {
            state.Picture = TownMapPictureService.Load(project, state.View);
            state.Edits.Loaded(state.Picture, state.View == TownMapView.Terrain ? "Town Map terrain picture" : "Town Map route diagram");
            state.Palette = TownMapPictureService.Palette(project, state.View);
            state.Bitmap = CreateBitmapFromRgba(TownMapPictureService.ToRgba(state.Picture, state.Palette), PokedexMapService.MapSize, PokedexMapService.MapSize);
        }

        areaCanvas.Picture = pokedexMapCanvas.Picture = state.Bitmap;
        areaCanvas.InvalidateVisual();
        pokedexMapCanvas.InvalidateVisual();
    }

    private void SelectTownView(TownMapView view)
    {
        if (view == townView && pdmView.SelectedIndex == (view == TownMapView.Terrain ? 0 : 1)) return;
        pdmView.SelectedIndex = view == TownMapView.Terrain ? 0 : 1;
    }

    private void TownViewChanged()
    {
        TownMapView view = pdmView.SelectedIndex == 1 ? TownMapView.Routes : TownMapView.Terrain;
        if (view == townView && Town.Picture is not null) return;
        townView = view;
        if (TryGetGame(out RomProject project, out _) && areaMap is not null)
        {
            try
            {
                EnsureTownPictureLoaded(project);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                LogUiError("The Town Map picture could not be loaded", ex);
                pokedexMapStatus.Text = ex.Message;
                return;
            }
        }

        pdmSwatches.Items.Clear();
        if (pokedexMapCanvas.PixelMode)
        {
            FillSwatches();
            RefreshTileBudget();
        }

        pokedexMapStatus.Text = view == TownMapView.Terrain ? "Showing the terrain map picture." : "Showing the route diagram picture.";
    }
    private const int PictureUndoLimit = 300;
    private readonly TextBlock pdmBudget = new() { FontSize = 12, TextWrapping = TextWrapping.Wrap, Width = 340 };
    private readonly RadioButton pdmPlacesRadio = new() { Name = "PdmPlacesRadio", Content = "Places", GroupName = "PdmMode", IsChecked = true };
    private readonly RadioButton pdmPictureRadio = new() { Name = "PdmPictureRadio", Content = "Picture", GroupName = "PdmMode" };
    private readonly RadioButton pdmPencil = new() { Name = "PdmToolPencil", Content = "Pencil", GroupName = "PdmTool", IsChecked = true };
    private readonly RadioButton pdmFill = new() { Name = "PdmToolFill", Content = "Fill", GroupName = "PdmTool" };
    private readonly RadioButton pdmPick = new() { Name = "PdmToolPick", Content = "Pick color", GroupName = "PdmTool" };
    private readonly ComboBox pdmBrush = new() { Name = "PdmBrushSize", Width = 90 };
    private readonly ComboBox pdmZoom = new() { Name = "PdmZoom", Width = 90 };
    private readonly ComboBox pdmView = new() { Name = "PdmView", Width = 170 };
    private readonly CheckBox pdmShowPlaces = new() { Name = "PdmShowPlaces", Content = "Show the places over the picture", IsChecked = true, IsVisible = false };
    private readonly ListBox pdmSwatches = new() { Name = "PdmSwatches", Width = 330 };
    private readonly TextBlock pdmColorLabel = new() { Foreground = ExtraMutedBrush, FontSize = 11 };
    private StackPanel? pdmPlaceControls, pdmPictureControls;
    private Control? pdmSide;

    private Control BuildPokedexMapPanel()
    {
        areaRoutesRadio.IsCheckedChanged += (_, _) => { if (areaRoutesRadio.IsChecked == true) FillAreaPlaceList(1); };
        areaSpecialsRadio.IsCheckedChanged += (_, _) => { if (areaSpecialsRadio.IsChecked == true) FillAreaPlaceList(1); };
        areaPlaceList.SelectionChanged += (_, _) => ShowSelectedPlace();
        pokedexMapCanvas.CellInteracted += PokedexMapCellInteracted;
        pokedexMapCanvas.PixelInteracted += PictureInteracted;
        pokedexMapCanvas.Released += () => { if (pokedexMapCanvas.PixelMode) AfterPictureEdit(); };
        pokedexMapCanvas.Describe = (x, y) => DescribeMapCell(pokedexMapCanvas, x, y);
        pdmPlacesRadio.IsCheckedChanged += (_, _) => SetPokedexMapMode();
        pdmPictureRadio.IsCheckedChanged += (_, _) => SetPokedexMapMode();
        foreach (int size in new[] { 1, 2, 3, 4, 6, 8 }) pdmBrush.Items.Add($"{size} px");
        pdmBrush.SelectedIndex = 0;
        foreach (int zoom in new[] { 1, 2, 3 }) pdmZoom.Items.Add($"{zoom}x");
        pdmZoom.SelectedIndex = 0;
        pdmZoom.SelectionChanged += (_, _) => { pokedexMapCanvas.Zoom = pdmZoom.SelectedIndex + 1; };
        pdmView.Items.Add("Terrain map");
        pdmView.Items.Add("Route diagram");
        pdmView.SelectedIndex = 0;
        pdmView.SelectionChanged += (_, _) => TownViewChanged();
        pdmSwatches.ItemsPanel = new global::Avalonia.Controls.Templates.FuncTemplate<Panel?>(() => new WrapPanel { Orientation = Orientation.Horizontal });
        pdmSwatches.SelectionChanged += (_, _) =>
        {
            int color = pdmSwatches.SelectedIndex;
            pdmColorLabel.Text = color < 0 || color >= townPalette.Length ? string.Empty : color == 0 ? "0: see-through" : $"{color}: #{townPalette[color].R:X2}{townPalette[color].G:X2}{townPalette[color].B:X2}";
        };
        pdmShowPlaces.IsCheckedChanged += (_, _) =>
        {
            pokedexMapCanvas.ShowPlaces = pdmShowPlaces.IsChecked == true;
            pokedexMapCanvas.InvalidateVisual();
        };
        areaHeaderBox.ValueChanged += (_, _) => EditSelectedPlace(place => { if (place is RoutePlace route) route.Header = (uint)(areaHeaderBox.Value ?? 0); else if (place is SpecialPlace special) special.Header = (uint)(areaHeaderBox.Value ?? 0); });
        areaXBox.ValueChanged += (_, _) => EditSelectedPlace(place => { if (place is SpecialPlace special) special.X = (int)(areaXBox.Value ?? 0); });
        areaYBox.ValueChanged += (_, _) => EditSelectedPlace(place => { if (place is SpecialPlace special) special.Y = (int)(areaYBox.Value ?? 0); });

        areaSpecialFields = new StackPanel
        {
            Spacing = 8,
            Children = { new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Field("Marker column", areaXBox), Field("Marker row", areaYBox) } }, new TextBlock { Text = "Click a cell of the map to move the marker there.", Foreground = ExtraMutedBrush, FontSize = 11 } }
        };
        areaRouteFields = new StackPanel
        {
            Spacing = 8,
            Children =
            {
                new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        MarginedButton("Left", "Move the whole place one cell left", () => MoveSelectedRoute(-1, 0)), MarginedButton("Right", "Move the whole place one cell right", () => MoveSelectedRoute(1, 0)),
                        MarginedButton("Up", "Move the whole place one cell up", () => MoveSelectedRoute(0, -1)), MarginedButton("Down", "Move the whole place one cell down", () => MoveSelectedRoute(0, 1))
                    }
                },
                new TextBlock { Text = "Left button paints cells onto the map, right button erases them. A place covers a box of up to 32 cells.", Foreground = ExtraMutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap, Width = 330 }
            }
        };

        pdmPlaceControls = new StackPanel
        {
            Spacing = 12,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 8,
                    Children =
                    {
                        ActionButton("Add Place", "Add a new place at the end of the list shown (a route with no cells yet, or a marker in the top-left corner) with the selected place's map header", (_, _) => AddPlace()),
                        ActionButton("Remove Last Place", "Remove the last place of the list shown, if no species lists it", (_, _) => RemoveLastPlace())
                    }
                },
                new TextBlock { Text = "The places the Pokédex's area map is drawn from: each route or city is a block of cells and each special place (mine, cave, lake...) a marker, on the Town Map picture. Which species is listed where is set in the Pokémon Editor's Areas tab.", Foreground = ExtraMutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                Field("Map header the game ties it to", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Children = { areaHeaderBox, areaHeaderName } }),
                areaSpecialFields, areaRouteFields
            }
        };

        pdmPictureControls = new StackPanel
        {
            Spacing = 10, IsVisible = false,
            Children =
            {
                new TextBlock { Text = "The picture under the cells. Left button paints with the chosen color, right button makes pixels see-through. The picture can only use the palette's colors (they are shared with other Town Map screens, so they are not editable here) and has room for a limited number of different 8 x 8 tiles; saving tells you if it runs out.", Foreground = ExtraMutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { pdmPencil, pdmFill, pdmPick } },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { Field("Brush", pdmBrush) } },
                Field("Color", new StackPanel { Spacing = 4, Children = { pdmSwatches, pdmColorLabel } }),
                pdmBudget,
                new WrapPanel
                {
                    Orientation = Orientation.Horizontal,
                    Children =
                    {
                        MarginedButton("Undo", "Take back the last stroke, fill or import (Ctrl+Z, or the top bar's Undo)", UndoPicture), MarginedButton("Redo", "Put back what was undone (Ctrl+Y, or the top bar's Redo)", RedoPicture),
                        MarginedButton("Revert Picture", "Go back to the saved picture. This can itself be undone.", RevertPicture),
                        MarginedButton("Import PNG...", "Replace the picture with a 512x512 PNG: pixels under half opacity become see-through, the others take the closest palette color", ImportPicture),
                        MarginedButton("Export PNG...", "Save the picture as a 512x512 PNG to edit elsewhere", ExportPicture)
                    }
                }
            }
        };

        Control pictureField = Field("Picture", pdmView);
        pictureField.IsVisible = PictureEditingEnabled;
        pdmPlacesRadio.IsVisible = pdmPictureRadio.IsVisible = PictureEditingEnabled;
        var fields = new StackPanel
        {
            Spacing = 12, Width = 380,
            Children =
            {
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 12,
                    Children =
                    {
                        ActionButton("Save", "Write the map's places and picture to the project", (_, _) => { SaveAreaMap(); SaveTownMapPicture(); }),
                        pdmPlacesRadio, pdmPictureRadio
                    }
                },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, VerticalAlignment = VerticalAlignment.Center, Children = { pictureField, Field("Zoom", pdmZoom) } },
                new Border { Child = pdmShowPlaces },
                pdmPlaceControls, pdmPictureControls
            }
        };
        var content = new ScrollViewer
        {
            HorizontalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto,
            Content = new StackPanel
            {
                Orientation = Orientation.Horizontal, Spacing = 16,
                Children = { new Border { BorderBrush = new SolidColorBrush(Color.Parse("#33414D")), BorderThickness = new Thickness(1), Child = pokedexMapCanvas, VerticalAlignment = VerticalAlignment.Top }, fields }
            }
        };
        pdmSide = SideList("Places", new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { areaRoutesRadio, areaSpecialsRadio } }, areaPlaceList);
        return EditorPage(pdmSide, content, pokedexMapStatus);
    }

    private void SetPokedexMapMode()
    {
        bool picture = PictureEditingEnabled && pdmPictureRadio.IsChecked == true;
        if (pdmPlaceControls is null || pdmPictureControls is null) return;
        pdmPlaceControls.IsVisible = !picture;
        pdmPictureControls.IsVisible = picture;
        pdmShowPlaces.IsVisible = picture;
        pokedexMapCanvas.PixelMode = picture;
        pokedexMapCanvas.Cursor = new global::Avalonia.Input.Cursor(picture ? global::Avalonia.Input.StandardCursorType.Cross : global::Avalonia.Input.StandardCursorType.Hand);
        if (picture)
        {
            FillSwatches();
            RefreshTileBudget();
        }

        pokedexMapCanvas.InvalidateVisual();
    }

    private void FillSwatches()
    {
        if (townPalette.Length == 0 || pdmSwatches.Items.Count > 0) return;
        for (int i = 0; i < townPalette.Length; i++)
        {
            RgbColor color = townPalette[i];
            var chip = new Border
            {
                Width = 30, Height = 30, BorderBrush = new SolidColorBrush(Color.Parse("#7F8C99")), BorderThickness = new Thickness(1),
                Background = i == 0 ? new SolidColorBrush(Color.Parse("#2A3340")) : new SolidColorBrush(Color.FromRgb(color.R, color.G, color.B)),
                Child = i == 0 ? new TextBlock { Text = "x", HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center, Foreground = ExtraMutedBrush } : null
            };
            ToolTip.SetTip(chip, i == 0 ? "See-through" : $"{i}: #{color.R:X2}{color.G:X2}{color.B:X2}");
            pdmSwatches.Items.Add(chip);
        }

        // Start on the color the picture uses most.
        int[] counts = new int[16];
        if (townPicture is not null) foreach (string row in townPicture.Rows) foreach (char c in row) counts[Convert.ToInt32(c.ToString(), 16)]++;
        pdmSwatches.SelectedIndex = Enumerable.Range(1, 15).MaxBy(i => counts[i]);
    }

    private void ConfigurePokedexMapEditor()
    {
        if (!TryGetGame(out RomProject project, out RomGameMetadata metadata)) return;
        try
        {
            if (!EnsureAreaMapLoaded(project, metadata))
            {
                pokedexMapStatus.Text = "The Pokédex area map is only supported for Platinum.";
                return;
            }

            FillAreaPlaceList(Math.Max(areaPlaceList.SelectedIndex + 1, 1));
            pokedexMapStatus.Text = $"{areaMap!.Routes.Count} routes and cities and {areaMap.Specials.Count} special places loaded.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileNotFoundException)
        {
            LogUiError("The Pokédex area map could not be loaded", ex);
            pokedexMapStatus.Text = ex.Message;
        }
    }

    private int SelectedPlaceNumber => areaPlaceList.SelectedIndex + 1;

    private object? SelectedPlace =>
        areaMap is null || SelectedPlaceNumber < 1 ? null
        : areaSpecialsRadio.IsChecked == true ? (SelectedPlaceNumber <= areaMap.Specials.Count ? areaMap.Specials[SelectedPlaceNumber - 1] : null)
        : SelectedPlaceNumber <= areaMap.Routes.Count ? areaMap.Routes[SelectedPlaceNumber - 1] : null;

    private string PlaceLabel(int area)
    {
        IReadOnlyList<string> names = areaSpecialsRadio.IsChecked == true ? areaSpecialNames : areaRouteNames;
        return $"{area}: {(area <= names.Count ? names[area - 1] : $"Area {area}")}";
    }

    private void FillAreaPlaceList(int select)
    {
        if (areaMap is null) return;
        bool specials = areaSpecialsRadio.IsChecked == true;
        pokedexMapCanvas.SpecialPlaces = specials;
        loadingAreaPlace = true;
        areaPlaceList.Items.Clear();
        for (int area = 1; area <= (specials ? areaMap.Specials.Count : areaMap.Routes.Count); area++) areaPlaceList.Items.Add(PlaceLabel(area));
        areaPlaceList.SelectedIndex = Math.Clamp(select - 1, 0, areaPlaceList.Items.Count - 1);
        loadingAreaPlace = false;
        areaSpecialFields!.IsVisible = specials;
        areaRouteFields!.IsVisible = !specials;
        ShowSelectedPlace();
    }

    private void ShowSelectedPlace()
    {
        if (loadingAreaPlace) return;
        pokedexMapCanvas.Selected = SelectedPlaceNumber;
        loadingAreaPlace = true;
        try
        {
            if (SelectedPlace is SpecialPlace special)
            {
                areaHeaderBox.Value = special.Header;
                areaHeaderName.Text = HeaderLocationName(special.Header);
                areaXBox.Value = special.X;
                areaYBox.Value = special.Y;
            }
            else if (SelectedPlace is RoutePlace route)
            {
                areaHeaderBox.Value = route.Header;
                areaHeaderName.Text = HeaderLocationName(route.Header);
            }
        }
        finally
        {
            loadingAreaPlace = false;
        }

        pokedexMapCanvas.InvalidateVisual();
    }

    private void PokedexMapCellInteracted(int x, int y, bool secondary, bool press)
    {
        if (SelectedPlace is not { } place) return;
        if (place is SpecialPlace special)
        {
            if (!press || secondary) return;
            (special.X, special.Y) = (x, y);
        }
        else if (place is RoutePlace route && !route.SetCell(x, y, !secondary))
        {
            pokedexMapStatus.Text = "A place covers a box of at most 32 cells.";
            return;
        }

        FinishPlaceEdit();
    }

    private void EditSelectedPlace(Action<object> edit)
    {
        if (loadingAreaPlace || SelectedPlace is not { } place) return;
        edit(place);
        FinishPlaceEdit();
    }

    private void MoveSelectedRoute(int dx, int dy)
    {
        if (SelectedPlace is not RoutePlace route) return;
        if (!route.MoveBy(dx, dy))
        {
            pokedexMapStatus.Text = "The place cannot move off the map.";
            return;
        }

        FinishPlaceEdit();
    }

    private void FinishPlaceEdit()
    {
        loadingAreaPlace = true;
        if (SelectedPlace is { } place)
        {
            uint header = place is RoutePlace route ? route.Header : ((SpecialPlace)place).Header;
            areaHeaderName.Text = HeaderLocationName(header);
            if (place is SpecialPlace special)
            {
                areaXBox.Value = special.X;
                areaYBox.Value = special.Y;
            }

            int at = areaPlaceList.SelectedIndex;
            AreaMapChanged();
            if (at >= 0)
            {
                areaPlaceList.Items[at] = PlaceLabel(at + 1);
                areaPlaceList.SelectedIndex = at;
            }
        }

        loadingAreaPlace = false;
        pokedexMapCanvas.InvalidateVisual();
        ReportEdit(areaMapEdits, () => areaMap, pokedexMapStatus);
    }

    private void AddPlace()
    {
        if (areaMap is null) return;
        bool specials = areaSpecialsRadio.IsChecked == true;
        if ((specials ? areaMap.Specials.Count : areaMap.Routes.Count) >= PokedexMapService.MaxPlaces)
        {
            pokedexMapStatus.Text = $"There can be at most {PokedexMapService.MaxPlaces} places in a list.";
            return;
        }

        uint header = SelectedPlace switch { RoutePlace route => route.Header, SpecialPlace special => special.Header, _ => 0 };
        if (specials) areaMap.Specials.Add(new SpecialPlace { Header = header });
        else areaMap.Routes.Add(new RoutePlace { Header = header });
        int number = specials ? areaMap.Specials.Count : areaMap.Routes.Count;
        AreaMapChanged();
        FillAreaPlaceList(number);
        ReportEdit(areaMapEdits, () => areaMap, pokedexMapStatus);
        pokedexMapStatus.Text += specials ? " Click the map to put its marker where it belongs." : " Left-drag on the map to give it cells.";
    }

    private void RemoveLastPlace()
    {
        if (areaMap is null || !TryGetGame(out RomProject project, out _)) return;
        bool specials = areaSpecialsRadio.IsChecked == true;
        int count = specials ? areaMap.Specials.Count : areaMap.Routes.Count;
        if (count <= 1)
        {
            pokedexMapStatus.Text = "The last place of a list cannot be removed.";
            return;
        }

        // The species lists, saved or not, must not name the place.
        string? listedBy = areaSet?.Species.Select((entry, species) => (entry, species)).Skip(1)
            .SelectMany(pair => pair.entry.Lists.Select((list, index) => (pair.species, index, list)))
            .Where(item => PokedexAreaService.UsesSpecialPlaces(item.index) == specials && item.list.Areas.Contains(count))
            .Select(item => $"species {item.species} lists it ({PokedexAreaService.Categories[item.index]})").FirstOrDefault();
        listedBy ??= PokedexAreaService.FindListedBeyond(project, specials ? count - 1 : areaMap.Specials.Count, specials ? areaMap.Routes.Count : count - 1);
        if (listedBy is not null)
        {
            pokedexMapStatus.Text = $"Place {count} cannot be removed: {listedBy}. Take it off that list first.";
            return;
        }

        if (specials) areaMap.Specials.RemoveAt(count - 1);
        else areaMap.Routes.RemoveAt(count - 1);
        AreaMapChanged();
        FillAreaPlaceList(count - 1);
        ReportEdit(areaMapEdits, () => areaMap, pokedexMapStatus);
    }

    private void SaveAreaMap()
    {
        if (!TryGetGame(out RomProject project, out _) || areaMap is null || !areaMapEdits.IsUnsaved(areaMap)) return;
        try
        {
            PokedexMapService.Save(project, areaMap);
            areaMapEdits.Saved(areaMap);
            pokedexMapStatus.Text = "Saved the Pokédex area map. It reaches the ROM with Save ROM.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            LogUiError("The Pokédex area map could not be saved", ex);
            pokedexMapStatus.Text = $"The map could not be saved: {ex.Message}";
        }
    }

    #region Picture

    private int PictureColor => Math.Max(pdmSwatches.SelectedIndex, 0);

    private int BrushSize => new[] { 1, 2, 3, 4, 6, 8 }[Math.Max(pdmBrush.SelectedIndex, 0)];

    private void PushPictureUndo()
    {
        if (townPicture is null) return;
        // Rows are immutable strings, so a snapshot is only the list of them.
        townPictureUndo.Add(townPicture.Rows.ToList());
        if (townPictureUndo.Count > PictureUndoLimit) townPictureUndo.RemoveAt(0);
        townPictureRedo.Clear();
        townPictureLastEdit = DateTime.UtcNow;
        UpdateUndoButtons();
    }

    private void PictureInteracted(int x, int y, bool secondary, bool press)
    {
        if (townPicture is null) return;
        if (pdmPick.IsChecked == true)
        {
            if (press && !secondary) pdmSwatches.SelectedIndex = townPicture.Get(x, y);
            return;
        }

        int color = secondary ? 0 : PictureColor;
        if (pdmFill.IsChecked == true)
        {
            if (!press) return;
            PushPictureUndo();
            FloodFill(x, y, color);
            RefreshPictureBitmap();
            return;
        }

        if (press) PushPictureUndo();
        int size = BrushSize;
        var changed = new List<(int X, int Y)>();
        for (int dy = 0; dy < size; dy++)
        {
            for (int dx = 0; dx < size; dx++)
            {
                int px = x + dx - size / 2, py = y + dy - size / 2;
                if (px is < 0 or >= TownMapPictureService.Size || py is < 0 or >= TownMapPictureService.Size || townPicture.Get(px, py) == color) continue;
                townPicture.Set(px, py, color);
                changed.Add((px, py));
            }
        }

        WritePictureBitmap(changed);
    }

    private void FloodFill(int x, int y, int color)
    {
        if (townPicture is null) return;
        int target = townPicture.Get(x, y);
        if (target == color) return;
        char[][] pixels = townPicture.Rows.Select(row => row.ToCharArray()).ToArray();
        char from = "0123456789ABCDEF"[target], to = "0123456789ABCDEF"[color];
        var stack = new Stack<(int X, int Y)>();
        stack.Push((x, y));
        while (stack.Count > 0)
        {
            (int px, int py) = stack.Pop();
            if (px < 0 || py < 0 || px >= TownMapPictureService.Size || py >= TownMapPictureService.Size || pixels[py][px] != from) continue;
            pixels[py][px] = to;
            stack.Push((px + 1, py));
            stack.Push((px - 1, py));
            stack.Push((px, py + 1));
            stack.Push((px, py - 1));
        }

        townPicture.Rows = pixels.Select(row => new string(row)).ToList();
    }

    private void WritePictureBitmap(IReadOnlyList<(int X, int Y)> pixels)
    {
        if (areaPicture is null || townPicture is null || pixels.Count == 0) return;
        using global::Avalonia.Platform.ILockedFramebuffer framebuffer = areaPicture.Lock();
        foreach ((int x, int y) in pixels)
        {
            int color = townPicture.Get(x, y);
            RgbColor rgb = townPalette[color];
            byte[] pixel = color == 0 ? [0, 0, 0, 0] : [rgb.R, rgb.G, rgb.B, 255];
            System.Runtime.InteropServices.Marshal.Copy(pixel, 0, framebuffer.Address + y * framebuffer.RowBytes + x * 4, 4);
        }
    }

    private void RefreshPictureBitmap()
    {
        if (areaPicture is null || townPicture is null) return;
        byte[] rgba = TownMapPictureService.ToRgba(townPicture, townPalette);
        using global::Avalonia.Platform.ILockedFramebuffer framebuffer = areaPicture.Lock();
        for (int y = 0; y < TownMapPictureService.Size; y++) System.Runtime.InteropServices.Marshal.Copy(rgba, y * TownMapPictureService.Size * 4, framebuffer.Address + y * framebuffer.RowBytes, TownMapPictureService.Size * 4);
        pokedexMapCanvas.InvalidateVisual();
        areaCanvas.InvalidateVisual();
    }

    private void AfterPictureEdit()
    {
        pokedexMapCanvas.InvalidateVisual();
        areaCanvas.InvalidateVisual();
        RefreshTileBudget();
        pokedexMapStatus.Text = townPicture is not null && townPictureEdits.IsUnsaved(townPicture) ? "Town Map picture: edited, not saved yet." : "Town Map picture: matches the saved one again.";
    }

    /// <summary>Shows how many tiles the picture needs that the archive does not already have, against the room it has: the picture can only be
    /// saved while the first is within the second.</summary>
    private void RefreshTileBudget()
    {
        if (townPicture is null || !TryGetGame(out RomProject project, out _)) return;
        try
        {
            (int needed, int free) = TownMapPictureService.TileBudget(project, townPicture, townView);
            bool over = needed > free;
            pdmBudget.Text = over
                ? $"Too detailed to save: it needs {needed} new tiles and only {free} are free. Undo (Ctrl+Z) some of the changes, or use Revert Picture."
                : $"Tile room: {needed} of {free} free tiles used.";
            pdmBudget.Foreground = over ? new SolidColorBrush(Color.Parse("#FF8A80")) : ExtraMutedBrush;
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            pdmBudget.Text = ex.Message;
        }
    }

    /// <summary>Puts the picture back as the stack's top entry had it, keeping what it was for the other direction.</summary>
    private bool StepPicture(bool redo)
    {
        List<List<string>> from = redo ? townPictureRedo : townPictureUndo, to = redo ? townPictureUndo : townPictureRedo;
        if (townPicture is null || from.Count == 0) return false;
        to.Add(townPicture.Rows.ToList());
        townPicture.Rows = from[^1];
        from.RemoveAt(from.Count - 1);
        pictureUndoneLast = !redo;
        townPictureLastEdit = DateTime.UtcNow;
        RefreshPictureBitmap();
        AfterPictureEdit();
        UpdateUndoButtons();
        return true;
    }

    private void UndoPicture()
    {
        if (!StepPicture(redo: false)) pokedexMapStatus.Text = "Nothing to undo in the picture.";
    }

    private void RedoPicture()
    {
        if (!StepPicture(redo: true)) pokedexMapStatus.Text = "Nothing to redo in the picture.";
    }

    private void RevertPicture()
    {
        if (townPicture is null || !TryGetGame(out RomProject project, out _)) return;
        PushPictureUndo();
        townPicture.Rows = TownMapPictureService.Load(project, townView).Rows;
        RefreshPictureBitmap();
        AfterPictureEdit();
    }

    /// <summary>The top bar's Undo and Redo, and Ctrl+Z / Ctrl+Y: step the picture, when it is what was changed last.</summary>
    private bool StepPictureHistory(bool redo, out string description)
    {
        description = "the Town Map picture in the Pokedex Map Editor";
        if (redo ? PictureRedoCount == 0 : PictureUndoCount == 0) return false;
        if (redo ? !(pictureUndoneLast || uiRedo.Count == 0) : uiUndo.Count > 0 && uiUndo[^1].At > LatestPictureEdit) return false;

        // The picture changed last: the terrain map or the route diagram, whichever it was.
        TownPictureState? state = redo
            ? (Town.Redo.Count > 0 ? Town : townStates.Values.First(candidate => candidate.Redo.Count > 0))
            : townStates.Values.Where(candidate => candidate.Undo.Count > 0).MaxBy(candidate => candidate.LastEdit);
        if (state is null) return false;
        if (state != Town) SelectTownView(state.View);
        return StepPicture(redo);
    }

    private bool HandlePictureShortcut(global::Avalonia.Input.KeyEventArgs e)
    {
        if (currentProject is null || !pokedexMapCanvas.PixelMode || !pokedexMapCanvas.IsEffectivelyVisible || (e.KeyModifiers & global::Avalonia.Input.KeyModifiers.Control) == 0) return false;
        bool shift = (e.KeyModifiers & global::Avalonia.Input.KeyModifiers.Shift) != 0;
        if (e.Key == global::Avalonia.Input.Key.Z && !shift) UndoPicture();
        else if (e.Key == global::Avalonia.Input.Key.Y || (e.Key == global::Avalonia.Input.Key.Z && shift)) RedoPicture();
        else return false;
        return true;
    }

    private async void ImportPicture()
    {
        if (townPicture is null || await PickFileAsync("Town Map picture: a 512x512 PNG", "PNG image", "*.png") is not string path) return;
        try
        {
            byte[] rgba = BattleSpriteService.DecodeImageFileToRgba(path, out int width, out int height);
            TownMapPicture imported = TownMapPictureService.FromRgba(rgba, width, height, townPalette);
            PushPictureUndo();
            townPicture.Rows = imported.Rows;
            RefreshPictureBitmap();
            AfterPictureEdit();
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            pokedexMapStatus.Text = $"The picture could not be imported: {ex.Message}";
        }
    }

    private async void ExportPicture()
    {
        if (townPicture is null) return;
        if (await PickSavePathAsync("Export Town Map picture", "town_map.png", "PNG image", "*.png") is not string path) return;
        try
        {
            using var bitmap = CreateBitmapFromRgba(TownMapPictureService.ToRgba(townPicture, townPalette), TownMapPictureService.Size, TownMapPictureService.Size);
            bitmap?.Save(path);
            pokedexMapStatus.Text = $"Exported the picture to {path}.";
        }
        catch (IOException ex)
        {
            pokedexMapStatus.Text = $"The picture could not be exported: {ex.Message}";
        }
    }

    private void SaveTownMapPicture()
    {
        if (!TryGetGame(out RomProject project, out _)) return;
        foreach (TownPictureState state in townStates.Values)
        {
            if (state.Picture is null || !state.Edits.IsUnsaved(state.Picture)) continue;
            string name = state.View == TownMapView.Terrain ? "terrain map" : "route diagram";
            try
            {
                TownMapPictureService.Save(project, state.Picture, state.View);
                state.Edits.Saved(state.Picture);
                state.Undo.Clear();
                state.Redo.Clear();
                UpdateUndoButtons();
                if (state == Town) RefreshTileBudget();
                pokedexMapStatus.Text = $"Saved the Town Map {name}. It reaches the ROM with Save ROM.";
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException)
            {
                LogUiError($"The Town Map {name} could not be saved", ex);
                pokedexMapStatus.Text = $"The {name} could not be saved: {ex.Message} Nothing was written; undo (Ctrl+Z) or Revert Picture to go back.";
                SelectTownView(state.View);
            }
        }
    }

    #endregion
}
