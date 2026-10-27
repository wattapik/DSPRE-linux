using Avalonia.Data.Converters;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using System.Globalization;

namespace DSPRE.Avalonia;

/// <summary>The icon each editor had in the legacy app (its main tab strip or toolbar button),
/// shown next to the editor's name in the sidebar and page title. Editors that legacy only reached
/// through a plain menu entry use the legacy graphic closest to what they edit; the few with no
/// fitting graphic have none.</summary>
public sealed class EditorIcons : IValueConverter
{
    private static readonly Dictionary<string, string> FileByEditor = new()
    {
        ["Project Overview"] = "app",
        ["Map Editor"] = "map_editor",
        ["Map Creator"] = "tileset_editor",
        ["Camera Editor"] = "cameraIcon",
        ["Spawn Editor"] = "dawnSpawn",
        ["Fly Warp Editor"] = "warpGo",
        ["Texture Editor"] = "tileset_editor",
        ["Building Editor"] = "buildingEditorButton",
        ["Overworld Graphic Editor"] = "goToOW",
        ["Trainer Graphic Editor"] = "trainer_editor",
        ["Battle Background Editor"] = "tileset_editor",
        ["Music Editor"] = "text_editor",
        ["Instrument Editor"] = "text_editor",
        ["Pokemon Editor"] = "pokemon_editor_icon",
        ["Move Editor"] = "move_editor_icon",
        ["Item Editor"] = "item_editor_icon",
        ["Item Table Editor"] = "IconItem",
        ["Trainer Editor"] = "trainer_editor",
        ["Trade Editor"] = "trade_editor_icon",
        ["Wild Pokemon Encounter Editor"] = "wildEditorButton",
        ["Table Editor"] = "tableEditor",
        ["Script Editor"] = "script_editor",
        ["Script Commands"] = "scriptDBIcon",
        ["Variable and Flag Editor"] = "scriptDBIcon",
        ["Level Script Editor"] = "script_editor",
        ["Text Editor"] = "text_editor",
        ["ROM Toolbox"] = "exploreKit",
        ["Utilities"] = "narcToFolderIcon",
        ["Journal Reader"] = "text_editor",
        ["Settings"] = "scriptDBIcon",
        ["Research Helper"] = "exploreKit",
        ["Character Map"] = "text_editor",
        ["About"] = "app"
    };

    public static void Add(string editor, string file) => FileByEditor[editor] = file;

    private static readonly Dictionary<string, Bitmap> Loaded = [];

    public static Bitmap? For(string? editor)
    {
        if (editor is null || !FileByEditor.TryGetValue(editor, out string? file)) return null;
        if (!Loaded.TryGetValue(file, out Bitmap? bitmap))
        {
            using Stream stream = AssetLoader.Open(new Uri($"avares://DSPRE.Avalonia/Assets/EditorIcons/{file}.png"));
            bitmap = new Bitmap(stream);
            Loaded[file] = bitmap;
        }

        return bitmap;
    }

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => For(value as string);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => throw new NotSupportedException();
}
