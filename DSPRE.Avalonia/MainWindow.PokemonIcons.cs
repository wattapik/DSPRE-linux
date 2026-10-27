using Avalonia.Interactivity;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The Pokémon Editor's menu icon import and export - see PokemonIconService.</summary>
public partial class MainWindow
{
    private bool TrySelectedIcon(out RomGameMetadata metadata, out int iconId)
    {
        metadata = null!;
        iconId = -1;
        if (currentProject is null || pokeEditorSpeciesId < 0 || !currentProject.TryGetGameMetadata(out RomGameMetadata? resolved) || resolved is null) return false;
        if (!Directory.Exists(currentProject.MonIconsDirectory)) new RomExtractionService().PrepareSprites(currentProject, resolved);
        metadata = resolved;
        iconId = PersonalDataService.ResolveIconSpeciesId(pokeEditorSpeciesId, pokeBaseSpeciesCount);
        return File.Exists(PokemonIconService.IconPath(currentProject, iconId));
    }

    private async void PokeEditorExportIcon_Click(object? sender, RoutedEventArgs e)
    {
        if (!TrySelectedIcon(out RomGameMetadata metadata, out int iconId) || currentProject is null)
        {
            PokeEditorStatus.Text = "This Pokémon has no icon file.";
            return;
        }

        string name = PokeEditorSpeciesSelector.SelectedItem?.ToString() ?? $"icon {iconId}";
        if (await PickSavePathAsync("Export menu icon", $"{iconId:D3}_{string.Concat(name.Split(Path.GetInvalidFileNameChars()))}_icon.png", "PNG image", "*.png") is not string path) return;
        try
        {
            using var bitmap = CreateBitmapFromRgba(PokemonIconService.Render(currentProject, metadata, iconId), PokemonIconService.FrameSize, PokemonIconService.FrameSize * 2);
            bitmap?.Save(path);
            PokeEditorStatus.Text = $"Exported the icon (both frames, 32x64) to {path}.";
        }
        catch (Exception ex)
        {
            LogUiError("The icon could not be exported", ex);
            PokeEditorStatus.Text = $"The icon could not be exported: {ex.Message}";
        }
    }

    private async void PokeEditorImportIcon_Click(object? sender, RoutedEventArgs e)
    {
        if (!TrySelectedIcon(out RomGameMetadata metadata, out int iconId) || currentProject is null)
        {
            PokeEditorStatus.Text = "This Pokémon has no icon file.";
            return;
        }

        if (await PickFileAsync("Menu icon: 32x32, or 32x64 for two frames", "PNG image", "*.png") is not string path) return;
        try
        {
            PokemonIconService.ImportResult result = PokemonIconService.Import(currentProject, metadata, iconId, path);
            PokeEditorSpeciesIcon.Source = CreateBitmapFromRgba(SpriteService.GetSpeciesIconRgba(currentProject, metadata, iconId, 32, 24), 32, 24);
            int approximated = result.OpaquePixels - result.ExactColors;
            PokeEditorStatus.Text = $"Imported the icon using shared icon palette {result.Palette}." +
                (approximated > 0
                    ? $" {approximated} of {result.OpaquePixels} pixels had no exact color in it and took the nearest one: icons can only use the colors of the game's shared icon palettes."
                    : " Every color matched exactly.") +
                " It reaches the ROM with Save ROM.";
        }
        catch (Exception ex) when (ex is InvalidDataException or InvalidOperationException or IOException)
        {
            PokeEditorStatus.Text = $"The icon could not be imported: {ex.Message}";
        }
    }

    private async void PokeEditorIconPalette_Click(object? sender, RoutedEventArgs e)
    {
        if (!TrySelectedIcon(out RomGameMetadata metadata, out int iconId) || currentProject is null)
        {
            PokeEditorStatus.Text = "This Pokémon has no icon file.";
            return;
        }

        if (PokemonIconService.GetPalette(currentProject, metadata, iconId) < 0)
        {
            PokeEditorStatus.Text = "The icon palette table was not found in this ROM's ARM9, so icon palettes cannot be edited.";
            return;
        }

        string name = PokeEditorSpeciesSelector.SelectedItem?.ToString() ?? $"icon {iconId}";
        if (await new IconPaletteWindow(currentProject, metadata, iconId, name).ShowDialog<bool>(this))
        {
            PokeEditorSpeciesIcon.Source = CreateBitmapFromRgba(SpriteService.GetSpeciesIconRgba(currentProject, metadata, iconId, 32, 24), 32, 24);
            PokeEditorStatus.Text = "Saved the icon palette. It reaches the ROM with Save ROM.";
        }
    }

    private async void EditPokeSpritePalettes_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || pokeEditorSpeciesId < 0 || pokeEditorSpeciesId >= pokeBaseSpeciesCount)
        {
            PokeSpriteStatus.Text = "Battle sprites for alternate forms aren't supported by this editor yet.";
            return;
        }

        if (pokeSpriteNormalPalette is null && pokeSpriteShinyPalette is null)
        {
            PokeSpriteStatus.Text = "This Pokémon has no battle sprite palette files.";
            return;
        }

        string name = PokeEditorSpeciesSelector.SelectedItem?.ToString() ?? $"Pokémon {pokeEditorSpeciesId}";
        var window = new BattlePaletteWindow(currentProject, pokeEditorSpeciesId, name, pokeSpriteIndices.ToArray(), pokeSpriteNormalPalette, pokeSpriteShinyPalette);
        if (await window.ShowDialog<BattlePaletteWindow.SavedPalettes?>(this) is not { } saved) return;

        pokeSpriteNormalPalette = saved.Normal ?? pokeSpriteNormalPalette;
        pokeSpriteShinyPalette = saved.Shiny ?? pokeSpriteShinyPalette;
        RedrawPokeSprites();
        PokeSpriteStatus.Text = "Saved the battle sprite palettes. They reach the ROM with Save ROM.";
    }

    private async void EditPokeSpriteAnimation_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || pokeEditorSpeciesId < 0 || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        if (!SpriteAnimationService.IsSupported(metadata))
        {
            PokeSpriteStatus.Text = "Diamond/Pearl has no battle sprite animation data to edit.";
            return;
        }

        if (pokeEditorSpeciesId >= SpriteAnimationService.SpeciesCount(currentProject, metadata))
        {
            PokeSpriteStatus.Text = "Only national dex species have animation data; alternate forms are not covered.";
            return;
        }

        string name = PokeEditorSpeciesSelector.SelectedItem?.ToString() ?? $"Pokémon {pokeEditorSpeciesId}";
        try
        {
            var window = new SpriteAnimationWindow(currentProject, metadata, pokeEditorSpeciesId, name, pokeSpriteIndices.ToArray(), pokeSpriteNormalPalette);
            if (await window.ShowDialog<bool>(this)) PokeSpriteStatus.Text = "Saved the battle sprite animation. It reaches the ROM with Save ROM.";
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException)
        {
            PokeSpriteStatus.Text = $"The animation could not be read: {ex.Message}";
        }
    }
}
