using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using DSPRE.Core;
using System.IO;
using System.Runtime.InteropServices;

namespace DSPRE.Avalonia;

/// <summary>The Battle Background Editor (view only for now): puts a background together from the tiles, screen and palette files of the
/// battle background archive (see <see cref="BattleBackgroundArchive"/>) and shows it.</summary>
public partial class MainWindow
{
    private BattleBackgroundArchive? battleBackgrounds;
    // Backdrop pictures imported but not saved yet: the three PNG paths (day, evening, night) by backdrop number, and the archive as it will be.
    private readonly Dictionary<int, string?[]> pendingBackdrops = [];
    private BattleBackgroundArchive? battleBackgroundView;
    private TextureImage? battleBackgroundImage;
    private bool loadingBattleBackgrounds;

    private void ConfigureBattleBackgrounds()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        if (!BattleBackgroundArchive.IsSupported(currentProject, metadata))
        {
            BattleBgStatus.Text = "This game's battle background archive is not supported yet (Platinum and Diamond/Pearl are).";
            return;
        }

        if (battleBackgrounds is null)
        {
            try
            {
                battleBackgrounds = BattleBackgroundArchive.Load(currentProject, metadata);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException)
            {
                LogUiError("Battle backgrounds could not be read", ex);
                BattleBgStatus.Text = $"The battle backgrounds could not be read: {ex.Message}";
                return;
            }

            loadingBattleBackgrounds = true;
            try
            {
                Fill(BattleBgTiles, BattleBackgroundArchive.MemberKind.Tiles, includeNone: false);
                Fill(BattleBgScreen, BattleBackgroundArchive.MemberKind.Screen, includeNone: true);
                Fill(BattleBgPalette, BattleBackgroundArchive.MemberKind.Palette, includeNone: true);
                BattleBgBackdrop.Items.Clear();
                BattleBgBackdrop.Items.Add("(parts below)");
                foreach (BattleBackgroundArchive.Backdrop backdrop in battleBackgrounds.Backdrops) BattleBgBackdrop.Items.Add($"Backdrop {backdrop.Number:D2}");
                BattleBgBackdrop.SelectedIndex = battleBackgrounds.Backdrops.Count > 0 ? 1 : 0;
                BattleBgScreen.SelectedIndex = 0;
                BattleBgPalette.SelectedIndex = 0;
                BattleBgTiles.SelectedIndex = 0;
            }
            finally
            {
                loadingBattleBackgrounds = false;
            }

            void Fill(ComboBox box, BattleBackgroundArchive.MemberKind kind, bool includeNone)
            {
                box.Items.Clear();
                if (includeNone) box.Items.Add("(none)");
                foreach (BattleBackgroundArchive.Member member in battleBackgrounds.Members.Where(member => member.Kind == kind)) box.Items.Add(member.Description);
            }
        }

        if (BattleBgBackdrop.SelectedIndex > 0) ApplyBattleBackdrop();
        ShowBattleBackground();
    }

    private void ApplyBattleBackdrop()
    {
        if (battleBackgrounds is null || BattleBgBackdrop.SelectedIndex <= 0 || BattleBgBackdrop.SelectedIndex > battleBackgrounds.Backdrops.Count) return;
        BattleBackgroundArchive.Backdrop backdrop = battleBackgrounds.Backdrops[BattleBgBackdrop.SelectedIndex - 1];
        loadingBattleBackgrounds = true;
        try
        {
            SelectBattleBgTiles(backdrop.Tiles);
            BattleBgScreen.SelectedIndex = 1 + battleBackgrounds.Members.Where(member => member.Kind == BattleBackgroundArchive.MemberKind.Screen).ToList().FindIndex(member => member.Index == backdrop.Screen);
            BattleBgPalette.SelectedIndex = 1 + battleBackgrounds.Members.Where(member => member.Kind == BattleBackgroundArchive.MemberKind.Palette).ToList().FindIndex(member => member.Index == backdrop.Palettes[Math.Clamp(BattleBgTime.SelectedIndex, 0, 2)]);
        }
        finally
        {
            loadingBattleBackgrounds = false;
        }
    }

    private void BattleBgBackdrop_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingBattleBackgrounds || battleBackgrounds is null) return;
        ApplyBattleBackdrop();
        RebuildBattleBackgroundView();
        ShowBattleBackground();
    }

    private int BattleBgMember(ComboBox box, BattleBackgroundArchive.MemberKind kind, bool hasNone)
    {
        int index = box.SelectedIndex - (hasNone ? 1 : 0);
        if (battleBackgrounds is null || index < 0) return -1;
        return battleBackgrounds.Members.Where(member => member.Kind == kind).ElementAtOrDefault(index)?.Index ?? -1;
    }

    private void BattleBgSelection_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!loadingBattleBackgrounds) ShowBattleBackground();
    }

    private void BattleBgScreen_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingBattleBackgrounds || battleBackgrounds is null) return;
        int screen = BattleBgMember(BattleBgScreen, BattleBackgroundArchive.MemberKind.Screen, hasNone: true);
        if (screen >= 0 && battleBackgrounds.SuggestTiles(screen) is int tiles and >= 0) SelectBattleBgTiles(tiles);
        ShowBattleBackground();
    }

    private void BattleBgSuggest_Click(object? sender, RoutedEventArgs e)
    {
        if (battleBackgrounds is null) return;
        int screen = BattleBgMember(BattleBgScreen, BattleBackgroundArchive.MemberKind.Screen, hasNone: true);
        int tiles = screen < 0 ? -1 : battleBackgrounds.SuggestTiles(screen);
        if (tiles < 0)
        {
            BattleBgStatus.Text = "Pick a screen first.";
            return;
        }

        SelectBattleBgTiles(tiles);
        ShowBattleBackground();
    }

    private void SelectBattleBgTiles(int memberIndex)
    {
        if (battleBackgrounds is null) return;
        int position = battleBackgrounds.Members.Where(member => member.Kind == BattleBackgroundArchive.MemberKind.Tiles).ToList().FindIndex(member => member.Index == memberIndex);
        if (position < 0) return;
        bool wasLoading = loadingBattleBackgrounds;
        loadingBattleBackgrounds = true;
        BattleBgTiles.SelectedIndex = position;
        loadingBattleBackgrounds = wasLoading;
    }

    private void ShowBattleBackground()
    {
        if (battleBackgrounds is null) return;
        int tiles = BattleBgMember(BattleBgTiles, BattleBackgroundArchive.MemberKind.Tiles, hasNone: false);
        if (tiles < 0) return;
        int screen = BattleBgMember(BattleBgScreen, BattleBackgroundArchive.MemberKind.Screen, hasNone: true);
        int palette = BattleBgMember(BattleBgPalette, BattleBackgroundArchive.MemberKind.Palette, hasNone: true);
        try
        {
            battleBackgroundImage = (battleBackgroundView ?? battleBackgrounds).Render(tiles, screen, palette);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentException or InvalidDataException)
        {
            LogUiError("Battle background could not be drawn", ex);
            BattleBgStatus.Text = $"That combination could not be drawn: {ex.Message}";
            return;
        }

        int zoom = Math.Max(BattleBgZoom.SelectedIndex, 0) + 1;
        var bitmap = new WriteableBitmap(new PixelSize(battleBackgroundImage.Width, battleBackgroundImage.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using (ILockedFramebuffer buffer = bitmap.Lock())
        {
            for (int y = 0; y < battleBackgroundImage.Height; y++) Marshal.Copy(battleBackgroundImage.Rgba, y * battleBackgroundImage.Width * 4, buffer.Address + y * buffer.RowBytes, battleBackgroundImage.Width * 4);
        }

        BattleBgImage.Source = bitmap;
        BattleBgImage.Width = battleBackgroundImage.Width * zoom;
        BattleBgImage.Height = battleBackgroundImage.Height * zoom;
        BattleBgImage.Stretch = Stretch.Fill;
        RenderOptions.SetBitmapInterpolationMode(BattleBgImage, BitmapInterpolationMode.None);
        BattleBgStatus.Text = $"{battleBackgroundImage.Width} x {battleBackgroundImage.Height} pixels"
            + (screen < 0 ? "; no screen, so the tiles are laid out in rows of 32." : string.Empty)
            + (palette < 0 ? " No palette, so it is drawn in grey." : string.Empty);
    }

    private async void BattleBgSavePng_Click(object? sender, RoutedEventArgs e)
    {
        if (battleBackgroundImage is null) return;
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save battle background",
            SuggestedFileName = "battle_background.png",
            DefaultExtension = "png",
            FileTypeChoices = [new FilePickerFileType("PNG image") { Patterns = ["*.png"] }]
        });
        if (file is null) return;
        var bitmap = new WriteableBitmap(new PixelSize(battleBackgroundImage.Width, battleBackgroundImage.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using (ILockedFramebuffer buffer = bitmap.Lock())
        {
            for (int y = 0; y < battleBackgroundImage.Height; y++) Marshal.Copy(battleBackgroundImage.Rgba, y * battleBackgroundImage.Width * 4, buffer.Address + y * buffer.RowBytes, battleBackgroundImage.Width * 4);
        }

        await using Stream stream = await file.OpenWriteAsync();
        bitmap.Save(stream);
        BattleBgStatus.Text = $"Saved {file.Name}.";
    }

    private int ShownBackdropNumber => BattleBgBackdrop.SelectedIndex > 0 ? BattleBgBackdrop.SelectedIndex : 0;

    /// <summary>The archive with every pending import applied, for showing.</summary>
    private void RebuildBattleBackgroundView()
    {
        battleBackgroundView = null;
        BattleBgDiscardButton.IsEnabled = ShownBackdropNumber > 0 && pendingBackdrops.ContainsKey(ShownBackdropNumber);
        if (battleBackgrounds is null || pendingBackdrops.Count == 0) return;
        var replacements = new Dictionary<int, byte[]>();
        foreach ((int number, string?[] images) in pendingBackdrops)
        {
            foreach ((int member, byte[] data) in BattleBackdropImporter.Build(battleBackgrounds, battleBackgrounds.Backdrops[number - 1], images).Replacements) replacements[member] = data;
        }

        battleBackgroundView = battleBackgrounds.WithMembers(replacements);
    }

    private async void BattleBgImport_Click(object? sender, RoutedEventArgs e)
    {
        if (battleBackgrounds is null) return;
        int number = ShownBackdropNumber;
        if (number == 0)
        {
            BattleBgStatus.Text = "Pick a backdrop first; pictures can only be imported into those.";
            return;
        }

        string time = BattleBgTime.SelectedIndex switch { 1 => "evening", 2 => "night", _ => "day" };
        string? path = await PickFileAsync($"Pick the PNG for backdrop {number:D2} in the {time} (512 x 256)", "PNG image", "*.png");
        if (path is null) return;
        string?[] images = pendingBackdrops.TryGetValue(number, out string?[]? existing) ? [.. existing] : [null, null, null];
        images[Math.Clamp(BattleBgTime.SelectedIndex, 0, 2)] = path;
        try
        {
            BattleBackdropImporter.Build(battleBackgrounds, battleBackgrounds.Backdrops[number - 1], images);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            LogUiError("Backdrop picture could not be imported", ex);
            BattleBgStatus.Text = $"That picture could not be used: {ex.Message}";
            return;
        }

        pendingBackdrops[number] = images;
        RebuildBattleBackgroundView();
        ShowBattleBackground();
        RefreshUnsavedMarkers();
        BattleBgStatus.Text = $"Backdrop {number:D2}: the {time} picture was imported. " + (images.Count(image => image is not null) < 3
            ? "The times without a picture use the first one's colours until they have one. " : string.Empty) + "Save All writes it into the game.";
    }

    private void BattleBgDiscard_Click(object? sender, RoutedEventArgs e)
    {
        if (ShownBackdropNumber == 0 || !pendingBackdrops.Remove(ShownBackdropNumber)) return;
        RebuildBattleBackgroundView();
        ShowBattleBackground();
        RefreshUnsavedMarkers();
        BattleBgStatus.Text = $"The imported pictures of backdrop {ShownBackdropNumber:D2} were dropped.";
    }

    private async void BattleBgExportAll_Click(object? sender, RoutedEventArgs e)
    {
        if (battleBackgrounds is null || ShownBackdropNumber == 0) return;
        string? folder = await PickFolderAsync("Pick the folder for the three PNGs");
        if (folder is null) return;
        BattleBackgroundArchive view = battleBackgroundView ?? battleBackgrounds;
        BattleBackgroundArchive.Backdrop backdrop = view.Backdrops[ShownBackdropNumber - 1];
        string[] names = ["day", "evening", "night"];
        for (int time = 0; time < 3; time++)
        {
            TextureImage image = view.Render(backdrop.Tiles, backdrop.Screen, backdrop.Palettes[time]);
            await using FileStream stream = File.Create(Path.Combine(folder, $"backdrop_{ShownBackdropNumber:D2}_{names[time]}.png"));
            ToBitmap(image).Save(stream);
        }

        BattleBgStatus.Text = $"Saved backdrop_{ShownBackdropNumber:D2}_day/evening/night.png to {folder}.";
    }

    private void CollectUnsavedBattleBackgroundChanges(List<UnsavedChange> changes)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        foreach ((int number, string?[] images) in pendingBackdrops.OrderBy(entry => entry.Key).ToList())
        {
            string[] names = ["day", "evening", "night"];
            string given = string.Join(", ", names.Where((_, time) => images[time] is not null));
            changes.Add(new UnsavedChange("Battle Background Editor", "Battle backdrop picture", [$"Backdrop {number:D2}: {given}"], () => SaveBackdrop(number, images, metadata), OpenDocument: false));
        }
    }

    private void SaveBackdrop(int number, string?[] images, RomGameMetadata metadata)
    {
        if (currentProject is null) return;
        try
        {
            BattleBackdropImporter.Save(currentProject, metadata, number, images);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or InvalidOperationException or ArgumentException)
        {
            LogUiError("Backdrop could not be saved", ex);
            BattleBgStatus.Text = $"Backdrop {number:D2} could not be saved: {ex.Message}";
            return;
        }

        pendingBackdrops.Remove(number);
        battleBackgrounds = BattleBackgroundArchive.Load(currentProject, metadata);
        RebuildBattleBackgroundView();
        if (BattleBackgroundPanel.IsVisible) ShowBattleBackground();
        BattleBgStatus.Text = $"Backdrop {number:D2} saved to the game's battle background archive.";
    }
}
