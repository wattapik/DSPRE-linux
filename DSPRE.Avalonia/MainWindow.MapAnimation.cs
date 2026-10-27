using Avalonia.Interactivity;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>The scrolling-texture animation of a map's area (see DynamicTextureService), played on the map
/// views' terrain as the game does. Which animation is the area data's dynamic textures value; the views
/// that show several maps at once play the one most of them use.</summary>
public partial class MainWindow
{
    private readonly Dictionary<MapGlView, int> mapViewerAnimationTypes = [];
    private readonly HashSet<MapGlView> mapAnimatedViewers = [];
    private readonly Dictionary<(bool Interior, int Model), TextureAnimation?> buildingAnimations = [];
    private readonly Dictionary<(bool Interior, int Model), JointAnimation?> buildingJointAnimations = [];
    private readonly Dictionary<int, TextureAnimation?> mapAnimations = [];

    private bool IsPlatinumProject => currentProject is not null && currentProject.TryGetGameMetadata(out RomGameMetadata? game) && game is not null && game.HeaderFormat == MapHeaderFormat.Platinum;

    private int DynamicTypeOfMap(int mapId) =>
        mapAreaData.TryGetValue(mapId, out AreaDataRecord? area) ? area.DynamicTextureType : DynamicTextureService.None;

    private int DynamicTypeOfPlacements(IEnumerable<MapPlacement> placements) =>
        placements.GroupBy(placement => DynamicTypeOfMap(placement.MapId)).OrderByDescending(group => group.Count()).Select(group => group.Key).DefaultIfEmpty(DynamicTextureService.None).First();

    private bool InteriorBuildingsFor(int mapId) =>
        currentProject is not null && currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata is not null && UsesInteriorBuildings(mapId, metadata);

    /// <summary>Lets the viewer animate the buildings of the scene it is about to load: the ones whose model has a
    /// texture scroll animation that runs by itself (see BuildingAnimationService). Call before loading.</summary>
    private void ConfigureBuildingAnimations(MapGlView viewer, bool interior)
    {
        mapAnimatedViewers.Add(viewer);
        viewer.BuildingAnimationsEnabled = MapAnimateTexturesCheckBox.IsChecked == true;
        viewer.SetBuildingAnimationProvider(model =>
        {
            if (buildingAnimations.TryGetValue((interior, model), out TextureAnimation? known)) return known;
            TextureAnimation? animation = null;
            if (currentProject is not null && currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata is not null)
            {
                try
                {
                    animation = BuildingAnimationService.LoadTextureAnimation(currentProject, metadata, interior, model);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    LogUiError("Building animation could not be read", ex);
                }
            }

            return buildingAnimations[(interior, model)] = animation;
        });
        viewer.SetBuildingJointAnimationProvider(model =>
        {
            if (buildingJointAnimations.TryGetValue((interior, model), out JointAnimation? known)) return known;
            JointAnimation? animation = null;
            if (currentProject is not null && currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata is not null)
            {
                try
                {
                    animation = BuildingAnimationService.LoadJointAnimation(currentProject, metadata, interior, model);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentOutOfRangeException)
                {
                    LogUiError("Building joint animation could not be read", ex);
                }
            }

            return buildingJointAnimations[(interior, model)] = animation;
        });
    }

    private void ApplyMapAnimation(MapGlView viewer, int dynamicType)
    {
        mapAnimatedViewers.Add(viewer);
        mapViewerAnimationTypes[viewer] = dynamicType;
        SetViewerAnimation(viewer, dynamicType);
    }

    private List<(string Target, IReadOnlyList<(int Width, int Height, byte[] Rgba, int Ticks)> Frames)>? platinumFrameAnimations;

    /// <summary>All of Platinum's picture-swapping animations (water, flowers...) as decoded frames; a viewer plays the ones whose
    /// texture its map has.</summary>
    private List<(string Target, IReadOnlyList<(int Width, int Height, byte[] Rgba, int Ticks)> Frames)> PlatinumFrameAnimations()
    {
        if (platinumFrameAnimations is not null) return platinumFrameAnimations;
        var result = new List<(string, IReadOnlyList<(int, int, byte[], int)>)>();
        if (currentProject is not null && currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata is not null && FieldTextureAnimationService.IsSupported(currentProject, metadata))
        {
            string temporary = Path.Combine(Path.GetTempPath(), $"dspre-fldtanime-{Guid.NewGuid():N}.nsbtx");
            try
            {
                foreach (FieldTextureAnimationService.Animation animation in FieldTextureAnimationService.LoadAll(currentProject, metadata))
                {
                    File.WriteAllBytes(temporary, animation.Textures);
                    if (NsbtxTexturePack.Load(temporary) is not { } pack) continue;
                    var frames = new List<(int, int, byte[], int)>();
                    foreach ((int texture, int ticks) in animation.Frames)
                    {
                        if (texture >= pack.Textures.Count) continue;
                        NsbtxTextureEntry entry = pack.Textures[texture];
                        // A frame's palette is named after it ("t3_fl_b.2" - "t3_fl_b.2_pl"), else after the animation ("sea").
                        int palette = pack.Palettes.FindIndex(candidate => candidate.Name.Equals(entry.Name + "_pl", StringComparison.OrdinalIgnoreCase));
                        if (palette < 0) palette = pack.Palettes.FindIndex(candidate => candidate.Name.Equals(System.Text.RegularExpressions.Regex.Replace(entry.Name, @"\.\d+$", string.Empty), StringComparison.OrdinalIgnoreCase));
                        if (palette < 0) palette = Math.Min(texture, pack.Palettes.Count - 1);
                        if (palette < 0) continue;
                        frames.Add((entry.Width, entry.Height, NsbtxTexturePack.DecodeTexturePixels(entry, pack.Palettes[palette].Colors), ticks));
                    }

                    if (frames.Count > 0) result.Add((animation.TargetTexture, frames));
                }
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or IndexOutOfRangeException or ArgumentException)
            {
                LogUiError("Platinum texture animations could not be read", ex);
            }
            finally
            {
                if (File.Exists(temporary)) File.Delete(temporary);
            }
        }

        return platinumFrameAnimations = result;
    }

    private void SetViewerAnimation(MapGlView viewer, int dynamicType)
    {
        if (IsPlatinumProject)
        {
            viewer.SetFrameAnimations(MapAnimateTexturesCheckBox.IsChecked == true ? PlatinumFrameAnimations() : []);
            return;
        }

        TextureAnimation? animation = null;
        if (MapAnimateTexturesCheckBox.IsChecked == true && dynamicType != DynamicTextureService.None && currentProject is not null &&
            currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata is not null && DynamicTextureService.IsSupported(currentProject, metadata))
        {
            if (!mapAnimations.TryGetValue(dynamicType, out animation))
            {
                try
                {
                    mapAnimations[dynamicType] = animation = DynamicTextureService.Load(currentProject, metadata, dynamicType);
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException)
                {
                    LogUiError("Texture animation could not be read", ex);
                    mapAnimations[dynamicType] = animation = null;
                }
            }
        }

        viewer.SetTextureAnimation(animation);
    }

    private void MapAnimateTexturesCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        foreach ((MapGlView viewer, int type) in mapViewerAnimationTypes.ToList()) SetViewerAnimation(viewer, type);
        foreach (MapGlView viewer in mapAnimatedViewers) viewer.BuildingAnimationsEnabled = MapAnimateTexturesCheckBox.IsChecked == true;
    }

    private void ResetMapAnimations()
    {
        mapAnimations.Clear();
        platinumFrameAnimations = null;
        buildingAnimations.Clear();
        buildingJointAnimations.Clear();
        mapViewerAnimationTypes.Clear();
        foreach (MapGlView viewer in mapAnimatedViewers)
        {
            viewer.SetBuildingAnimationProvider(null);
            viewer.SetBuildingJointAnimationProvider(null);
        }
        mapAnimatedViewers.Clear();
    }

    /// <summary>The area data editor's preview follows the dynamic textures value as it is typed.</summary>
    private void AreaDataDynamicTexturesInput_TextChanged(object? sender, global::Avalonia.Controls.TextChangedEventArgs e)
    {
        if (loadingAreaData || currentAreaData is null || !AreaDataPreviewViewer.IsVisible || !mapAnimatedViewers.Contains(AreaDataPreviewViewer)) return;
        ApplyMapAnimation(AreaDataPreviewViewer, AreaDataFields(currentAreaData).DynamicTextureType);
    }
}
