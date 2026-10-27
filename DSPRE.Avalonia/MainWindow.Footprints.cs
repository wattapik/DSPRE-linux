using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Pokémon Editor's Footprint tab: the open species' 16x16 Pokédex footprint, painted on a grid or imported from a PNG (see
/// FootprintService). The footprints of every species are kept whole in memory and tracked like the other table editors; written by the
/// tab's Save button or Save All.</summary>
public partial class MainWindow
{
    private readonly EditTracker footprintEdits = new();
    private FootprintSet? footprintSet;
    private RgbColor footprintColor = new(0, 0, 0);
    private int footprintSpecies = -1;
    private readonly FootprintCanvas footprintCanvas = new();
    private readonly Image footprintPreview = new() { Width = 48, Height = 48, Stretch = Stretch.Fill };
    private readonly ComboBox footprintCopyFrom = new() { Name = "FootprintCopyFrom", Width = 240 };
    private readonly RadioButton footprintDrawRadio = new() { Name = "FootprintDrawRadio", Content = "Draw", GroupName = "FootprintBrush", IsChecked = true };
    private readonly RadioButton footprintEraseRadio = new() { Name = "FootprintEraseRadio", Content = "Erase", GroupName = "FootprintBrush" };
    private readonly TextBlock footprintTitle = new() { FontWeight = FontWeight.SemiBold, FontSize = 15 };
    private readonly TextBlock footprintStatus = new() { Foreground = ExtraMutedBrush, TextWrapping = TextWrapping.Wrap };
    private StackPanel? footprintControls;
    private TabItem? footprintTab;

    private void AddFootprintTab()
    {
        footprintCanvas.Edited += AfterFootprintEdit;
        footprintDrawRadio.IsCheckedChanged += (_, _) => footprintCanvas.EraseMode = footprintEraseRadio.IsChecked == true;
        footprintEraseRadio.IsCheckedChanged += (_, _) => footprintCanvas.EraseMode = footprintEraseRadio.IsChecked == true;

        Button Tool(string text, string tip, Action action) => ActionButton(text, tip, (_, _) => action());
        var tools = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                Margined(Tool("Clear", "Erase the whole footprint", () => ChangeFootprint(rows => Enumerable.Repeat(new string('.', FootprintService.Size), FootprintService.Size).ToList()))),
                Margined(Tool("Mirror", "Flip left to right", () => ChangeFootprint(rows => rows.Select(row => new string(row.Reverse().ToArray())).ToList()))),
                Margined(Tool("Flip Up/Down", "Flip top to bottom", () => ChangeFootprint(rows => Enumerable.Reverse(rows).ToList()))),
                Margined(Tool("Left", "Move one pixel left (what leaves the grid is lost)", () => ChangeFootprint(rows => rows.Select(row => row[1..] + ".").ToList()))),
                Margined(Tool("Right", "Move one pixel right", () => ChangeFootprint(rows => rows.Select(row => "." + row[..^1]).ToList()))),
                Margined(Tool("Up", "Move one pixel up", () => ChangeFootprint(rows => rows.Skip(1).Append(new string('.', FootprintService.Size)).ToList()))),
                Margined(Tool("Down", "Move one pixel down", () => ChangeFootprint(rows => rows.Take(rows.Count - 1).Prepend(new string('.', FootprintService.Size)).ToList())))
            }
        };
        var files = new WrapPanel
        {
            Orientation = Orientation.Horizontal,
            Children =
            {
                Margined(Tool("Import PNG...", "Replace the footprint with a 16x16 PNG: pixels at least half opaque become the footprint, the rest see-through", ImportFootprint)),
                Margined(Tool("Export PNG...", "Save the footprint as a 16x16 PNG", ExportFootprint)),
                Margined(Tool("Save Footprints", "Write the footprints of every species to the project", SaveFootprints))
            }
        };
        var copy = new StackPanel
        {
            Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Bottom,
            Children = { Field("Copy from", footprintCopyFrom), Tool("Copy", "Replace this footprint with the one picked", CopyFootprint) }
        };
        footprintControls = new StackPanel
        {
            Spacing = 10,
            Children =
            {
                files,
                new TextBlock { Text = "Left button paints, right button erases. The grid is 16x16 and is drawn as two halves, the lines at the middle show where. A footprint is one ink; the game shows it in black.", Foreground = ExtraMutedBrush, FontSize = 11, TextWrapping = TextWrapping.Wrap },
                new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Children = { footprintDrawRadio, footprintEraseRadio } },
                new StackPanel
                {
                    Orientation = Orientation.Horizontal, Spacing = 24,
                    Children =
                    {
                        new Border { BorderBrush = new SolidColorBrush(Color.Parse("#33414D")), BorderThickness = new Thickness(1), Child = footprintCanvas, HorizontalAlignment = HorizontalAlignment.Left },
                        Field("Actual size (x3)", new Border { Background = new SolidColorBrush(Color.Parse("#E8EEF3")), Child = footprintPreview, HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top })
                    }
                },
                tools, copy
            }
        };
        var content = new StackPanel { Spacing = 10, Margin = new Thickness(0, 8, 0, 0), Children = { footprintTitle, footprintControls, footprintStatus } };
        footprintTab = new TabItem { Header = "Footprint", Content = new ScrollViewer { Content = content, VerticalScrollBarVisibility = global::Avalonia.Controls.Primitives.ScrollBarVisibility.Auto } };
        PokemonEditorTabs.Items.Insert(PokemonEditorTabs.Items.IndexOf(PokemonSpritesTab) + 1, footprintTab);
    }

    private static Control Margined(Button button)
    {
        button.Margin = new Thickness(0, 0, 8, 8);
        return button;
    }

    /// <summary>Shows the footprint of the species the Pokémon Editor has open, reading the footprints the first time they are needed.</summary>
    private void ShowFootprintForSpecies(int species)
    {
        footprintSpecies = species;
        if (footprintControls is null) return;
        if (!TryGetGame(out RomProject project, out RomGameMetadata metadata) || !FootprintService.IsSupported(project, metadata))
        {
            ShowNoFootprint("Footprints are only supported for Platinum.");
            return;
        }

        try
        {
            if (footprintSet is null)
            {
                footprintSet = FootprintService.Load(project);
                footprintEdits.Loaded(footprintSet, "Footprints");
                footprintColor = FootprintService.Color(project);
                SetItems(footprintCopyFrom, Enumerable.Range(0, footprintSet.Footprints.Count).Select(FootprintLabel));
                footprintCopyFrom.SelectedIndex = Math.Min(1, footprintCopyFrom.Items.Count - 1);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or FileNotFoundException)
        {
            LogUiError("Footprints could not be loaded", ex);
            ShowNoFootprint(ex.Message);
            return;
        }

        if (species < 0 || species >= footprintSet.Footprints.Count)
        {
            ShowNoFootprint("This entry has no footprint of its own: a form is shown with its species.");
            return;
        }

        footprintControls.IsVisible = true;
        footprintCanvas.Footprint = footprintSet.Footprints[species];
        footprintTitle.Text = $"Footprint of {FootprintLabel(species)}";
        RefreshFootprintPreview();
    }

    private void ShowNoFootprint(string reason)
    {
        footprintControls!.IsVisible = false;
        footprintCanvas.Footprint = null;
        footprintTitle.Text = reason;
    }

    private bool FootprintIsBlank(int species) => footprintSet!.Footprints[species].Rows.All(row => row.All(c => c == '.'));

    private string FootprintLabel(int species) =>
        $"[{species:D3}] {(species == 0 ? "(placeholder)" : species < PokeEditorSpeciesNames.Count ? PokeEditorSpeciesNames[species] : $"#{species}")}{(footprintSet is not null && FootprintIsBlank(species) ? "  - none" : string.Empty)}";

    private Footprint? SelectedFootprint =>
        footprintSet is not null && footprintSpecies >= 0 && footprintSpecies < footprintSet.Footprints.Count ? footprintSet.Footprints[footprintSpecies] : null;

    private void RefreshFootprintPreview()
    {
        footprintPreview.Source = SelectedFootprint is { } footprint
            ? CreateBitmapFromRgba(FootprintService.ToRgba(footprint, footprintColor), FootprintService.Size, FootprintService.Size)
            : null;
    }

    private void AfterFootprintEdit()
    {
        RefreshFootprintPreview();
        footprintTitle.Text = $"Footprint of {FootprintLabel(footprintSpecies)}";
        ReportEdit(footprintEdits, () => footprintSet, footprintStatus);
    }

    private void ChangeFootprint(Func<List<string>, List<string>> change)
    {
        if (SelectedFootprint is not { } footprint) return;
        footprint.Rows = change(footprint.Rows);
        footprintCanvas.Footprint = footprint;
        AfterFootprintEdit();
    }

    private void CopyFootprint()
    {
        if (footprintSet is null || footprintCopyFrom.SelectedIndex < 0) return;
        List<string> rows = footprintSet.Footprints[footprintCopyFrom.SelectedIndex].Rows.ToList();
        ChangeFootprint(_ => rows);
    }

    private async void ImportFootprint()
    {
        if (SelectedFootprint is null || await PickFileAsync("Footprint: a 16x16 PNG", "PNG image", "*.png") is not string path) return;
        try
        {
            byte[] rgba = BattleSpriteService.DecodeImageFileToRgba(path, out int width, out int height);
            List<string> rows = FootprintService.FromRgba(rgba, width, height).Rows;
            ChangeFootprint(_ => rows);
        }
        catch (Exception ex) when (ex is InvalidDataException or IOException)
        {
            footprintStatus.Text = $"The footprint could not be imported: {ex.Message}";
        }
    }

    private async void ExportFootprint()
    {
        if (SelectedFootprint is not { } footprint) return;
        if (await PickSavePathAsync("Export footprint", $"footprint_{footprintSpecies:D3}.png", "PNG image", "*.png") is not string path) return;
        try
        {
            using var bitmap = CreateBitmapFromRgba(FootprintService.ToRgba(footprint, footprintColor), FootprintService.Size, FootprintService.Size);
            bitmap?.Save(path);
            footprintStatus.Text = $"Exported footprint {footprintSpecies} to {path}.";
        }
        catch (IOException ex)
        {
            footprintStatus.Text = $"The footprint could not be exported: {ex.Message}";
        }
    }

    private void SaveFootprints()
    {
        if (!TryGetGame(out RomProject project, out _) || footprintSet is null || !footprintEdits.IsUnsaved(footprintSet)) return;
        try
        {
            FootprintService.Save(project, footprintSet);
            footprintEdits.Saved(footprintSet);
            footprintStatus.Text = "Saved the footprints. They reach the ROM with Save ROM.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            LogUiError("Footprints could not be saved", ex);
            footprintStatus.Text = $"The footprints could not be saved: {ex.Message}";
        }
    }
}
