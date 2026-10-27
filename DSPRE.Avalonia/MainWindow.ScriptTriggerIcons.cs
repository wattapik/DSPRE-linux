using System.Text.RegularExpressions;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using DSPRE.Core;

namespace DSPRE.Avalonia;

/// <summary>Beside the line where a script starts in the Script Editor (<c>Script 4:</c>, or <c>script(4, ...)</c> in Lua), the
/// icons of the events that run it: an overworld's sprite, a trigger's or a sign's icon. They come from the event files of the
/// headers using the script file (the Map Editor's own copies, so unsaved event edits show), and each carries a tooltip.</summary>
public partial class MainWindow
{
    private sealed record ScriptTrigger(string IconKey, string Description);

    private const int ScriptIconSize = 20;
    private const int ScriptIconsShown = 3;

    private static readonly Regex PlainScriptStart = new(@"^\s*Script\s+(\d+):\s*$", RegexOptions.Compiled);
    private static readonly Regex LuaScriptStart = new(@"^\s*script\s*\(\s*(\d+)\s*[,)]", RegexOptions.Compiled);

    private readonly Dictionary<string, Bitmap?> scriptIconBitmaps = [];
    private bool scriptIconsRefreshQueued;
    private bool scriptIconHooksAttached;

    private void AttachScriptIconHooks()
    {
        if (scriptIconHooksAttached) return;
        scriptIconHooksAttached = true;
        ScriptsTextBox.SizeChanged += (_, _) => QueueScriptIconRefresh();
        LuaTextBox.SizeChanged += (_, _) => QueueScriptIconRefresh();
    }

    /// <summary>Redraws the icons once the pending layout and text changes are in; several requests make one redraw.</summary>
    private void QueueScriptIconRefresh()
    {
        if (scriptIconsRefreshQueued) return;
        scriptIconsRefreshQueued = true;
        Dispatcher.UIThread.Post(() =>
        {
            scriptIconsRefreshQueued = false;
            RefreshScriptIcons();
        }, DispatcherPriority.Background);
    }

    private void ClearScriptIconBitmaps()
    {
        foreach (Bitmap? bitmap in scriptIconBitmaps.Values) bitmap?.Dispose();
        scriptIconBitmaps.Clear();
    }

    private void RefreshScriptIcons()
    {
        ScriptsIconCanvas.Children.Clear();
        LuaIconCanvas.Children.Clear();
        if (currentProject is null || headerStore is null || loadedScriptFileIndex < 0 || loadedScriptFileIndex >= scriptFileIds.Count) return;
        int scriptFileId = scriptFileIds[loadedScriptFileIndex];
        if (scriptFileId == GlobalLuaEntryId) return;

        Dictionary<int, List<ScriptTrigger>> triggers = FindScriptTriggers();
        if (triggers.Count == 0) return;
        if (scriptUsesLua) DrawScriptIcons(LuaTextBox, LuaIconCanvas, LuaScriptStart, triggers);
        else DrawScriptIcons(ScriptsTextBox, ScriptsIconCanvas, PlainScriptStart, triggers);
    }

    private Dictionary<int, List<ScriptTrigger>> FindScriptTriggers()
    {
        var result = new Dictionary<int, List<ScriptTrigger>>();
        var done = new HashSet<int>();
        foreach (object? item in HeadersUsingScriptList.Items)
        {
            if (item is not HeaderLink link || headerStore is null) continue;
            int eventFileId = headerStore.Load(link.Id).EventFileId;
            if (!done.Add(eventFileId)) continue;
            MapEventFile events;
            try
            {
                events = GetSharedMapEvents(eventFileId);
            }
            catch (Exception ex) when (ex is IOException or InvalidDataException or EndOfStreamException)
            {
                continue;
            }

            void Add(int number, string iconKey, string description)
            {
                if (!result.TryGetValue(number, out List<ScriptTrigger>? list)) result[number] = list = [];
                list.Add(new ScriptTrigger(iconKey, $"{description}, header {link.Id:D3} {link.InternalName}"));
            }

            string At(MapEvent ev) => $"({ev.MapPositionX + 32 * ev.MatrixPositionX}, {ev.MapPositionY + 32 * ev.MatrixPositionY})";
            for (int i = 0; i < events.Overworlds.Count; i++)
            {
                OverworldEvent ow = events.Overworlds[i];
                if (ow.IsAlias) continue;
                string key = ResolveOverworldIconKey(ow);
                Add(ow.ScriptNumber, key == PendingIconKey ? "static:overworld" : key, $"Overworld {i:D2} (sprite entry {ow.OverlayTableEntry}) at {At(ow)}");
            }

            for (int i = 0; i < events.Triggers.Count; i++)
            {
                TriggerEvent trigger = events.Triggers[i];
                Add(trigger.ScriptNumber, "static:trigger", $"Trigger {i:D2} at {At(trigger)}");
            }

            for (int i = 0; i < events.Spawnables.Count; i++)
            {
                SpawnableEvent spawnable = events.Spawnables[i];
                if (spawnable.Type == SpawnableEvent.TypeHiddenItem) continue;
                Add(spawnable.ScriptNumber, "static:sign", $"{(spawnable.Type == SpawnableEvent.TypeBoard ? "Board" : "Spawnable")} {i:D2} at {At(spawnable)}");
            }
        }

        return result;
    }

    private void DrawScriptIcons(TextBox box, Canvas canvas, Regex start, Dictionary<int, List<ScriptTrigger>> triggers)
    {
        string[] lines = (box.Text ?? "").Replace("\r\n", "\n").Split('\n');
        double lineHeight = (box.Bounds.Height - box.Padding.Top - box.Padding.Bottom) / Math.Max(1, lines.Length);
        if (box.Bounds.Height <= 0 || lineHeight <= 0) return;

        for (int line = 0; line < lines.Length; line++)
        {
            Match match = start.Match(lines[line]);
            if (!match.Success || !int.TryParse(match.Groups[1].Value, out int number) || !triggers.TryGetValue(number, out List<ScriptTrigger>? found)) continue;

            double top = box.Padding.Top + line * lineHeight + (lineHeight - ScriptIconSize) / 2;
            string tip = $"Script {number} is run by:\n" + string.Join('\n', found.Select(trigger => "  " + trigger.Description));
            List<ScriptTrigger> shown = found.GroupBy(trigger => trigger.IconKey).Select(group => group.First()).ToList();
            bool more = shown.Count > ScriptIconsShown;
            int icons = more ? ScriptIconsShown - 1 : shown.Count;
            for (int i = 0; i < icons; i++)
            {
                if (ScriptIconBitmap(shown[i].IconKey) is not { } bitmap) continue;
                var image = new Image { Source = bitmap, Width = ScriptIconSize, Height = ScriptIconSize };
                RenderOptions.SetBitmapInterpolationMode(image, global::Avalonia.Media.Imaging.BitmapInterpolationMode.MediumQuality);
                ToolTip.SetTip(image, tip);
                Canvas.SetLeft(image, 2 + i * (ScriptIconSize + 2));
                Canvas.SetTop(image, top);
                canvas.Children.Add(image);
            }

            if (more)
            {
                var label = new TextBlock { Text = $"+{shown.Count - icons}", Foreground = new SolidColorBrush(Color.Parse("#AAB7C4")), FontSize = 11 };
                ToolTip.SetTip(label, tip);
                Canvas.SetLeft(label, 2 + icons * (ScriptIconSize + 2));
                Canvas.SetTop(label, top + 3);
                canvas.Children.Add(label);
            }
        }
    }

    private Bitmap? ScriptIconBitmap(string iconKey)
    {
        if (scriptIconBitmaps.TryGetValue(iconKey, out Bitmap? cached)) return cached;
        string? path = null;
        if (iconKey.StartsWith("static:", StringComparison.Ordinal))
        {
            path = Path.Combine(AppContext.BaseDirectory, "Resources", "Event Icons", $"{iconKey["static:".Length..]}.png");
        }
        else if (iconKey.Split(':') is ["ow", string entry, string orientation] && OverworldSpriteDirectory is string directory &&
                 int.TryParse(entry, out int entryId) && int.TryParse(orientation, out int orientationId))
        {
            path = Path.Combine(directory, $"{entryId:D4}_{orientationId}.png");
        }

        Bitmap? bitmap = null;
        try
        {
            if (path is not null && File.Exists(path)) bitmap = new Bitmap(path);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Script icon {iconKey} could not be loaded: {ex.Message}");
        }

        scriptIconBitmaps[iconKey] = bitmap;
        return bitmap;
    }
}
