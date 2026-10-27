using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.OpenGL;
using Avalonia.OpenGL.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Rendering;
using Avalonia.Threading;
using OpenTK;
using OpenTK.Graphics;
using OpenTK.Graphics.OpenGL4;
using OpenTK.Mathematics;
using SharpGLTF.Schema2;
using SharpGLTF.Validation;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using NumericsVector3 = System.Numerics.Vector3;
using NumericsVector2 = System.Numerics.Vector2;
using NumericsVector4 = System.Numerics.Vector4;
using NumericsMatrix = System.Numerics.Matrix4x4;
using NumericsQuaternion = System.Numerics.Quaternion;
using GltfWrapMode = SharpGLTF.Schema2.TextureWrapMode;
using GlWrapMode = OpenTK.Graphics.OpenGL4.TextureWrapMode;
using StbImageSharp;
using DSPRE.Core;

namespace DSPRE.Avalonia;

// OpenGlControlBase fails Avalonia's normal composition-based hit test (a known upstream issue since
// 11.0.0-preview5 - https://github.com/AvaloniaUI/Avalonia/discussions/10879 - pointer events silently
// never reach a GL-rendered control, exactly matching "clicking the map view does nothing"), because
// hit-testing there depends on the control having registered rendered content in the composition tree
// in a way OpenGlControlBase's own render path doesn't. ICustomHitTest opts this control out of that
// broken default and answers hit-testing directly from layout bounds instead.
public sealed class MapGlView : OpenGlControlBase, ICustomHitTest
{
    private bool bindingsLoaded;
    private volatile bool renderFailed;
    // The texture set gpuTextures was uploaded from. Compared against the scene snapshot each frame
    // rather than flagged separately, so a frame can never pair a new scene with the previous scene's
    // textures (texture keys restart at zero for every scene, so the old uploads would still "match").
    private Dictionary<int, TextureData>? uploadedTextures;
    private int vertexArray;
    private int vertexBuffer;
    private int indexBuffer;
    // The scene's vertex/index data lives on the GPU until the scene changes, instead of being
    // re-sent every frame, which kept dragging the camera around slow on big maps.
    private RenderBatch[]? uploadedBatches;
    private int[] batchVertexBuffers = [];
    private int[] batchIndexBuffers = [];

    private bool UploadBatchesIfChanged(RenderBatch[] current)
    {
        if (ReferenceEquals(current, uploadedBatches)) return false;
        DeleteBatchBuffers();
        batchVertexBuffers = new int[current.Length];
        batchIndexBuffers = new int[current.Length];
        for (int i = 0; i < current.Length; i++)
        {
            batchVertexBuffers[i] = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ArrayBuffer, batchVertexBuffers[i]);
            GL.BufferData(BufferTarget.ArrayBuffer, current[i].Vertices.Length * sizeof(float), current[i].Vertices, BufferUsageHint.StaticDraw);
            batchIndexBuffers[i] = GL.GenBuffer();
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, batchIndexBuffers[i]);
            GL.BufferData(BufferTarget.ElementArrayBuffer, current[i].Indices.Length * sizeof(uint), current[i].Indices, BufferUsageHint.StaticDraw);
        }

        uploadedBatches = current;
        return true;
    }

    private void DeleteBatchBuffers()
    {
        foreach (int buffer in batchVertexBuffers) GL.DeleteBuffer(buffer);
        foreach (int buffer in batchIndexBuffers) GL.DeleteBuffer(buffer);
        batchVertexBuffers = [];
        batchIndexBuffers = [];
        uploadedBatches = null;
    }
    private int shaderProgram;
    private int indexCount;
    private float sceneScale = 1f;
    private Vector3 sceneCenter;
    private const float DefaultDistance = 8f;
    private const float DefaultYaw = 180f;
    private const float DefaultPitch = 125f;
    private volatile float distance = DefaultDistance;
    // The camera's rotation, expressed as a quaternion rather than a stored yaw/pitch pair.
    // A yaw/pitch-to-LookAt(world-up) camera has to snap its up vector to a different basis
    // whenever the view direction crosses the world-up axis (looking straight down/up), which is
    // exactly what produced the "flips over" artifact: the position moved continuously through
    // the pole, but the up vector did not. Composing rotations onto a quaternion instead means
    // "up" rotates continuously with the camera, so there is no pole and nothing to flip.
    private readonly object cameraLock = new();
    private Quaternion orientation = Quaternion.Identity;
    // The point the camera orbits and looks at. Read together with orientation/distance by
    // RenderFrame on the render thread; written from button clicks and drags on the UI thread.
    private volatile float targetX;
    private volatile float targetY;
    private volatile float targetZ;
    private Point? dragStart;
    // Set while the Permissions tab is active. The map's true vertical (height) axis is remapped to
    // render-space Z by RebuildScene's normalization pass (see the comment there), so a camera whose
    // offset is purely along render-Z is already looking straight down that axis - an exact overhead/
    // bird's-eye view - while DefaultYaw/DefaultPitch tilt away from it for the normal oblique 3D view.
    // Keeping the same yaw as the oblique default keeps left/right consistent between the two views.
    // Pitch is 180, not 0: for yaw=180 the offset/up vectors work out to
    // offset=(0,sin(pitch),-cos(pitch)) and up=(0,cos(pitch),sin(pitch)) (scaled), and the *default*
    // camera's own pitch=125 plugged into that same formula gives up's render-Y component a negative
    // sign - i.e. increasing world Z (further from the camera) reads as "down" on screen in the
    // already-correct default view. Pitch=0 flips that sign (up's render-Y becomes +1), which is
    // exactly the "map is upside down" bug: it disagreed with the default view's own sense of which
    // way is up. Pitch=180 restores a negative render-Y sign on up (matching the default) while also
    // putting the camera on the positive-height side of the scene (offset's render-Z becomes +1,
    // i.e. above the terrain looking down, not below it looking up).
    private volatile bool overheadLocked;
    private const float OverheadYaw = DefaultYaw;
    private const float OverheadPitch = 180f;
    // LoadGlb runs on the UI thread while RenderFrame runs on Avalonia's dedicated render thread.
    // batches/textures are only ever replaced wholesale (never mutated in place), and the
    // replacement is published under sceneLock so the render thread can never observe a torn
    // update from a List/Dictionary being cleared and refilled concurrently.
    private readonly object sceneLock = new();
    private RenderBatch[] batches = [];
    // Drawn separately from batches, after the main loop, with depth testing disabled - see RenderFrame.
    private RenderBatch? gridBatch;
    // The selected building's edges, drawn as lines over everything else so it can be found even when
    // it sits inside or behind other geometry.
    private RenderBatch? selectionWireframe;
    // Lines along the 32x32 tile boundaries of a single map, drawn over everything like the wireframe.
    private RenderBatch? tileGridLines;
    private bool tileGridVisible;
    private Dictionary<int, TextureData> textures = [];
    private readonly Dictionary<int, int> gpuTextures = [];
    public int TriangleCount { get; private set; }
    public int TextureCount => textures.Count;
    /// <summary>Number of placed building parts in the most recently loaded scene (0 for a plain LoadGlb).</summary>
    public int PartCount { get; private set; }

    // position(3) + texcoord(2) + color(4) + alphaCutoff(1) + selected(1)
    private const int VertexFloatStride = 11;
    private sealed record RenderBatch(float[] Vertices, uint[] Indices, int TextureKey, bool Translucent = false, bool Cutout = false);
    private static bool IsTranslucent(RenderBatch batch, Dictionary<int, TextureData> sceneTextures, Dictionary<int, (bool Partial, bool Clear)> textureAlpha)
    {
        bool anyBlended = false, anyFaded = false;
        for (int offset = 0; offset < batch.Vertices.Length; offset += VertexFloatStride)
        {
            float cutoff = batch.Vertices[offset + 9];
            if (cutoff >= 0f) continue;
            anyBlended = true;
            if (batch.Vertices[offset + 8] < 0.99f) anyFaded = true;
        }

        if (!anyBlended) return false;
        if (anyFaded) return true;
        return sceneTextures.ContainsKey(batch.TextureKey) && TextureAlpha(batch.TextureKey, sceneTextures, textureAlpha).Partial;
    }

    /// <summary>Whether a texture has semi-transparent texels, and whether it has fully clear ones.</summary>
    private static (bool Partial, bool Clear) TextureAlpha(int key, Dictionary<int, TextureData> sceneTextures, Dictionary<int, (bool Partial, bool Clear)> textureAlpha)
    {
        if (textureAlpha.TryGetValue(key, out (bool Partial, bool Clear) known)) return known;
        bool partial = false, clear = false;
        byte[] pixels = sceneTextures[key].Pixels;
        for (int index = 3; index < pixels.Length && !(partial && clear); index += 4)
        {
            byte alpha = pixels[index];
            if (alpha <= 3) clear = true;
            else if (alpha < 252) partial = true;
        }

        return textureAlpha[key] = (partial, clear);
    }

    private sealed record TextureData(byte[] Pixels, int Width, int Height, int WrapS, int WrapT);
    private sealed record RawBatch(
        List<NumericsVector3> Positions,
        List<NumericsVector2> Texcoords,
        List<NumericsVector4> Colors,
        List<uint> Indices,
        int TextureKey,
        // glTF alpha handling folded into a single per-batch scalar baked per-vertex at array-build
        // time (so it survives the later group-by-texture merge correctly): -1 = OPAQUE (ignore alpha
        // entirely), -2 = BLEND (use alpha as-is, let GL blending do the rest), >=0 = MASK cutoff
        // (discard below this, then treat as fully opaque - see t1_h01.glb's "light"/"t1_h01"
        // materials, and h_kage.glb's translucent BLEND shadow decal, for why this matters).
        float AlphaCutoff,
        // Baked per-batch like AlphaCutoff above, so it survives the texture-key merge too: a
        // highlight mode for the building editor - 0 for everything else (terrain included), 1 for the
        // currently-selected placed part (tinted white), 2 for a part that differs from its last-saved
        // state (tinted blue, and takes priority over 1 - a dirty part stays blue whether or not it's
        // also the current selection, so "unsaved" stays visible even after clicking elsewhere). Lets
        // the fragment shader tint just the relevant instances without needing per-instance draw calls.
        float HighlightMode = 0f,
        // The model material this batch was drawn with, which an area's texture animation addresses by
        // number; carried per vertex (see MaxAnimatedSlots) so it survives the texture-key merge.
        int TextureSlot = -1,
        string? MaterialName = null,
        // For a skinned building: the joint each vertex follows, and which of the scene's animated skeletons it is.
        int[]? Joints = null,
        int SkinInstance = -1);
    /// <summary>A placed instance of a converted model (e.g. one Building) within a multi-part scene.
    /// Position/RotationDegrees/Scale are in the same "native" coordinate space DecodeModel returns
    /// raw vertices in, i.e. before the terrain's own X/Z-swap-and-normalize pass is applied.</summary>
    private sealed record ScenePartSpec(string ModelPath, Vector3 Position, Vector3 RotationDegrees, Vector3 Scale);

    private sealed class SceneManifestDto
    {
        public string Terrain { get; set; } = "";
        public List<ScenePartManifestDto> Parts { get; set; } = [];
    }

    private sealed class ScenePartManifestDto
    {
        public string Model { get; set; } = "";
        public float[] Position { get; set; } = new float[3];
        public float[] RotationDegrees { get; set; } = new float[3];
        public float[] Scale { get; set; } = new float[3];
    }

    public MapGlView()
    {
        Focusable = true;
        IsHitTestVisible = true;
        AddHandler(PointerWheelChangedEvent, OnPointerWheelChanged, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerPressedEvent, OnPointerPressed, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerMovedEvent, OnPointerMoved, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerReleasedEvent, OnPointerReleased, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(PointerCaptureLostEvent, OnPointerCaptureLost, RoutingStrategies.Bubble, handledEventsToo: true);
        orientation = MakeOrientation(DefaultYaw, DefaultPitch);
    }

    // See the class-level comment: this is what actually makes the control clickable at all.
    bool ICustomHitTest.HitTest(Point point) => new Rect(Bounds.Size).Contains(point);

    public Action? RenderScene { get; set; }
    public Action<string>? RenderError { get; set; }

    // Otherwise left drag orbits the camera (or pans, with RotationEnabled off), right drag pans and the
    // wheel zooms.
    // While overheadLocked (the Permissions tab), pointer input paints the grid instead of orbiting
    // the camera: left click/drag = paint the current value, middle click = flood-fill from that cell,
    // right click = eyedropper (read the cell's value back out). Matches legacy's movPictureBox mouse
    // semantics exactly (movPictureBox_Click/_MouseMove in DS_Map/Editors/MapEditor.cs).
    public event Action? GridEditStarting;
    public event Action<int, int>? GridCellPaint;
    public event Action<int, int>? GridCellFloodFill;
    public event Action<int, int>? GridCellPick;
    private bool isPaintDragging;

    // Outside overheadLocked, left-drag orbits the camera - but a left click with (near-)zero movement
    // is treated as a building pick instead, so orbiting and building selection can share the same
    // button without a modifier key. clickCandidateStart tracks where the press started so
    // OnPointerReleased can tell a click from a drag by how far the pointer actually moved.
    public event Action<int>? BuildingPartClicked;
    /// <summary>Any click (not a drag) on the view, with where it landed - raised before the building
    /// and event pickers run.</summary>
    public event Action<Point>? ViewClicked;
    /// <summary>A second click close to the first within the double-click time.</summary>
    public event Action<Point>? ViewDoubleClicked;
    private DateTime lastClickTime;
    private Point lastClickPoint;
    private static readonly TimeSpan DoubleClickTime = TimeSpan.FromMilliseconds(450);

    /// <summary>Whether clicking picks buildings / event markers. The map editor only lets the pane
    /// being edited respond, so a click in the Buildings pane never selects an event and vice versa.</summary>
    public bool BuildingPickingEnabled { get; set; } = true;
    public bool EventPickingEnabled { get; set; } = true;
    /// <summary>Fires with the flat index into whatever list was last passed to SetEventMarkers - a
    /// click only ever resolves to one or the other in one pass: BuildingPartClicked is tried first
    /// (matches today's existing click behavior), EventMarkerClicked only if that misses.</summary>
    public event Action<int>? EventMarkerClicked;
    private Point? clickCandidateStart;
    private const double ClickMoveThreshold = 4.0;

    /// <summary>One event marker to render as a small solid-color cube in world space - deliberately not
    /// tied to any decoded model, since events (NPCs/warps/triggers/spawnables) have no 3D model of
    /// their own to place, just a position, a kind-specific fallback color, and an icon key the caller
    /// (MainWindow) chooses. ColorRgb is only ever used as the solid-color fallback while IconKey has
    /// no matching entry in eventIconTextures yet (e.g. an in-game overworld sprite still being
    /// exported asynchronously) - see RegisterEventIcon/RebuildScene's marker loop.</summary>
    public readonly record struct EventMarkerSpec(Vector3 WorldPosition, Vector3 ColorRgb, float HalfSize, string IconKey, float RotationDegrees = 0f);

    private List<EventMarkerSpec> eventMarkers = [];
    private int selectedEventIndex = -1;
    private HashSet<int> dirtyEventIndices = [];
    private bool eventsVisible = true;
    private bool partsVisible = true;
    private readonly Dictionary<string, TextureData> eventIconTextures = [];

    /// <summary>Replaces the full set of event markers to render/pick against - called every time the
    /// caller's own event list changes (map switch, add/delete, etc.), same pattern as LoadScene/
    /// LoadCompositeScene replacing the terrain wholesale rather than patching it incrementally.</summary>
    public void SetEventMarkers(IReadOnlyList<EventMarkerSpec> markers)
    {
        eventMarkers = markers.ToList();
        RebuildScene();
    }

    /// <summary>Decodes and caches a PNG (icon) under a stable key so RebuildScene's marker loop can
    /// look it up by EventMarkerSpec.IconKey - static per-kind icons (warp/trigger/sign) and per-map
    /// in-game overworld sprites both flow through the same cache, the caller (MainWindow) is the one
    /// that tells them apart via the key it chooses (e.g. "static:warp" vs "ow:12:1"). A key already in
    /// the cache is left untouched (each icon only needs decoding once, ever, for the process's
    /// lifetime). Nothing is redrawn: a marker only shows an icon once SetEventMarkers names it, so
    /// callers register every icon first and then set the markers, rebuilding the scene once.</summary>
    public bool RegisterEventIcon(string key, byte[] pngBytes)
    {
        if (eventIconTextures.ContainsKey(key)) return false;
        eventIconTextures[key] = MakeBackgroundTransparent(DecodePng(pngBytes));
        return true;
    }

    public bool HasEventIcon(string key) => eventIconTextures.ContainsKey(key);

    /// <summary>Forgets every icon whose key starts with <paramref name="keyPrefix"/>: the per-project
    /// overworld sprites, when another project is opened.</summary>
    public void RemoveEventIcons(string keyPrefix)
    {
        foreach (string key in eventIconTextures.Keys.Where(key => key.StartsWith(keyPrefix, StringComparison.Ordinal)).ToList()) eventIconTextures.Remove(key);
    }

    /// <summary>The registered icon's width in pixels, or 0 when there is none.</summary>
    public int EventIconWidth(string key) => eventIconTextures.TryGetValue(key, out TextureData? icon) ? icon.Width : 0;

    /// <summary>Some bundled icons (dawn_platinum) have no alpha channel and a solid background.
    /// Legacy's Event Editor calls Bitmap.MakeTransparent() on every overworld image, which keys out
    /// the bottom-left pixel's colour, so the same is done here.</summary>
    private static TextureData MakeBackgroundTransparent(TextureData texture)
    {
        if (texture.Width == 0 || texture.Height == 0) return texture;
        byte[] pixels = texture.Pixels;
        int corner = (texture.Height - 1) * texture.Width * 4;
        if (pixels[corner + 3] == 0) return texture;
        byte r = pixels[corner], g = pixels[corner + 1], b = pixels[corner + 2];
        for (int i = 0; i < pixels.Length; i += 4)
        {
            if (pixels[i] == r && pixels[i + 1] == g && pixels[i + 2] == b) pixels[i + 3] = 0;
        }

        return texture;
    }

    private static TextureData DecodePng(byte[] pngBytes)
    {
        ImageResult image = ImageResult.FromStream(new MemoryStream(pngBytes), ColorComponents.RedGreenBlueAlpha);
        return new TextureData(image.Data, image.Width, image.Height, (int)GlWrapMode.ClampToEdge, (int)GlWrapMode.ClampToEdge);
    }

    /// <summary>Toggles whether event markers render at all - the "Show Events" checkbox's target,
    /// independent of whether any editing pane is open.</summary>
    public void SetEventsVisible(bool visible)
    {
        if (eventsVisible == visible) return;
        eventsVisible = visible;
        RebuildScene();
    }

    /// <summary>Toggles whether placed building parts render at all - the "Show Buildings" checkbox's
    /// target, independent of whether the Buildings editing pane is open. Composite mode's own terrain
    /// still renders either way; this only ever affects parts/cachedParts and compositeMaps' Parts.</summary>
    public void SetPartsVisible(bool visible)
    {
        if (partsVisible == visible) return;
        partsVisible = visible;
        RebuildScene();
    }

    /// <summary>Highlights one event marker (by the same flat index EventMarkerClicked/SetDirtyEvents
    /// use) white, mirroring SetSelectedPart for buildings. Pass -1 to clear.</summary>
    public void SetSelectedEvent(int index)
    {
        if (selectedEventIndex == index) return;
        selectedEventIndex = index;
        RebuildScene();
    }

    /// <summary>Marks which event markers (by flat index) differ from their last-saved state, so they
    /// stay highlighted blue until saved - mirrors SetDirtyParts for buildings.</summary>
    public void SetDirtyEvents(IEnumerable<int> indices)
    {
        dirtyEventIndices = indices.ToHashSet();
        RebuildScene();
    }

    /// <summary>False keeps the camera's angle fixed (the camera editor's preview shows a game camera's
    /// own angle): dragging with the left button then pans instead of rotating.</summary>
    public bool RotationEnabled { get; set; } = true;

    // Where a pan drag (right button, or left when rotation is off) last was.
    private Point? panDragStart;

    private void OnPointerWheelChanged(object? sender, PointerWheelEventArgs e)
    {
        Zoom(-(float)e.Delta.Y * 0.5f);
        e.Handled = true;
    }

    private void OnPointerPressed(object? sender, PointerPressedEventArgs e)
    {
        PointerPointProperties props = e.GetCurrentPoint(this).Properties;
        if (overheadLocked)
        {
            Point position = e.GetPosition(this);
            if (!TryPickGridCell(position, out int col, out int row)) return;
            if (props.IsLeftButtonPressed)
            {
                isPaintDragging = true;
                e.Pointer.Capture(this);
                Focus();
                GridEditStarting?.Invoke();
                GridCellPaint?.Invoke(col, row);
            }
            else if (props.IsMiddleButtonPressed)
            {
                GridEditStarting?.Invoke();
                GridCellFloodFill?.Invoke(col, row);
            }
            else if (props.IsRightButtonPressed)
            {
                GridCellPick?.Invoke(col, row);
            }
            e.Handled = true;
            return;
        }

        if (props.IsLeftButtonPressed)
        {
            if (RotationEnabled) dragStart = e.GetPosition(this);
            else panDragStart = e.GetPosition(this);
            clickCandidateStart = e.GetPosition(this);
            e.Pointer.Capture(this);
            Focus();
            e.Handled = true;
        }
        else if (props.IsRightButtonPressed)
        {
            panDragStart = e.GetPosition(this);
            e.Pointer.Capture(this);
            Focus();
            e.Handled = true;
        }
    }

    private void OnPointerMoved(object? sender, PointerEventArgs e)
    {
        if (overheadLocked)
        {
            if (!isPaintDragging || !e.GetCurrentPoint(this).Properties.IsLeftButtonPressed) return;
            if (TryPickGridCell(e.GetPosition(this), out int col, out int row)) GridCellPaint?.Invoke(col, row);
            e.Handled = true;
            return;
        }

        PointerPointProperties moved = e.GetCurrentPoint(this).Properties;
        if (panDragStart is Point panFrom && (moved.IsRightButtonPressed || moved.IsLeftButtonPressed))
        {
            Point panTo = e.GetPosition(this);
            PanByPixels((float)(panTo.X - panFrom.X), (float)(panTo.Y - panFrom.Y));
            panDragStart = panTo;
            e.Handled = true;
            return;
        }

        if (dragStart is not Point previous || !moved.IsLeftButtonPressed) return;
        Point current = e.GetPosition(this);
        Rotate((float)(current.X - previous.X) * 0.5f, (float)(current.Y - previous.Y) * 0.5f);
        dragStart = current;
        e.Handled = true;
    }

    private void OnPointerReleased(object? sender, PointerReleasedEventArgs e)
    {
        if (!overheadLocked && clickCandidateStart is Point pressPoint && e.InitialPressMouseButton == MouseButton.Left)
        {
            Point releasePoint = e.GetPosition(this);
            double dx = releasePoint.X - pressPoint.X;
            double dy = releasePoint.Y - pressPoint.Y;
            if (dx * dx + dy * dy <= ClickMoveThreshold * ClickMoveThreshold)
            {
                ViewClicked?.Invoke(releasePoint);
                DateTime now = DateTime.UtcNow;
                bool doubleClick = now - lastClickTime <= DoubleClickTime &&
                    Math.Abs(releasePoint.X - lastClickPoint.X) <= ClickMoveThreshold * 2 &&
                    Math.Abs(releasePoint.Y - lastClickPoint.Y) <= ClickMoveThreshold * 2;
                lastClickTime = doubleClick ? default : now;
                lastClickPoint = releasePoint;
                if (doubleClick) ViewDoubleClicked?.Invoke(releasePoint);

                if (BuildingPickingEnabled && TryPickBuildingPart(releasePoint, out int partIndex))
                {
                    BuildingPartClicked?.Invoke(partIndex);
                }
                else if (EventPickingEnabled && TryPickEventMarker(releasePoint, out int eventIndex))
                {
                    EventMarkerClicked?.Invoke(eventIndex);
                }
            }
        }

        dragStart = null;
        panDragStart = null;
        clickCandidateStart = null;
        isPaintDragging = false;
        e.Pointer.Capture(null);
    }

    // Avalonia can take capture away from this control without ever raising PointerReleased first (a
    // context menu or another control stealing capture, the pointer leaving the window mid-drag on
    // some platforms, etc). Relying on PointerReleased alone to clear drag/paint state left capture -
    // and with it every click anywhere in the window, since a captured pointer routes ALL its events
    // to the capturing element regardless of where they land - stuck on this control indefinitely
    // whenever that happened, which is what made every click in the app appear to silently do nothing.
    private void OnPointerCaptureLost(object? sender, PointerCaptureLostEventArgs e)
    {
        dragStart = null;
        panDragStart = null;
        clickCandidateStart = null;
        isPaintDragging = false;
    }

    private static Quaternion MakeOrientation(float yawDegrees, float pitchDegrees) =>
        Quaternion.Normalize(
            Quaternion.FromAxisAngle(Vector3.UnitY, -MathHelper.DegreesToRadians(yawDegrees)) *
            Quaternion.FromAxisAngle(Vector3.UnitX, -MathHelper.DegreesToRadians(pitchDegrees)));

    /// <summary>Orbits the camera. deltaYaw/deltaPitch are in degrees; positive yaw turns right, positive pitch tilts down.
    /// Yaw is composed around the fixed world-up axis and pitch around the camera's own current local
    /// axis, which is the standard way to give an orbit camera a full, continuous 360-degree range on
    /// both axes without ever needing to recompute "up" from a separate absolute angle each frame.</summary>
    public void Rotate(float deltaYaw, float deltaPitch)
    {
        if (overheadLocked) return;
        Quaternion deltaYawQuat = Quaternion.FromAxisAngle(Vector3.UnitY, -MathHelper.DegreesToRadians(deltaYaw));
        Quaternion deltaPitchQuat = Quaternion.FromAxisAngle(Vector3.UnitX, -MathHelper.DegreesToRadians(deltaPitch));
        lock (cameraLock)
        {
            orientation = Quaternion.Normalize(deltaYawQuat * orientation * deltaPitchQuat);
        }
        ViewChanged?.Invoke();
        RequestNextFrameRendering();
    }

    /// <summary>Raised whenever the camera moves, for overlays drawn over the view (the permission
    /// value labels) that have to follow it.</summary>
    public event Action? ViewChanged;

    /// <summary>Moves the orbit target sideways/up in the camera's current view plane. Right is the camera's
    /// current right vector flattened to the horizontal plane (so panning stays level even while tilted);
    /// up is always world-up, matching how the horizontal Pan buttons are labelled.</summary>
    public void Pan(float deltaRight, float deltaUp)
    {
        Quaternion currentOrientation;
        lock (cameraLock) { currentOrientation = orientation; }
        Vector3 right = Vector3.Transform(Vector3.UnitX, currentOrientation);
        if (overheadLocked)
        {
            // Looking straight down at the permission grid, the screen plane is the grid plane, so
            // panning moves along the camera's own right and up directions.
            Vector3 screenUp = Vector3.Transform(Vector3.UnitY, currentOrientation);
            float overheadStep = Math.Max(distance, 1f) * 0.05f;
            targetX += (right.X * deltaRight + screenUp.X * deltaUp) * overheadStep;
            targetY += (right.Y * deltaRight + screenUp.Y * deltaUp) * overheadStep;
            targetZ += (right.Z * deltaRight + screenUp.Z * deltaUp) * overheadStep;
            ViewChanged?.Invoke();
            RequestNextFrameRendering();
            return;
        }

        Vector3 rightLevel = new(right.X, 0f, right.Z);
        // Looking straight up/down leaves no horizontal component to flatten; keep the last
        // direction rather than dividing by (near) zero.
        rightLevel = rightLevel.LengthSquared > 1e-6f ? Vector3.Normalize(rightLevel) : Vector3.UnitX;
        float step = Math.Max(distance, 1f) * 0.05f;
        targetX += rightLevel.X * deltaRight * step;
        targetZ += rightLevel.Z * deltaRight * step;
        targetY += deltaUp * step;
        ViewChanged?.Invoke();
        RequestNextFrameRendering();
    }

    /// <summary>Moves the view so the ground under the pointer follows a drag of
    /// <paramref name="dx"/>, <paramref name="dy"/> pixels: sideways along the camera's level right
    /// direction, and up/down the screen along the ground ahead of the camera.</summary>
    private void PanByPixels(float dx, float dy)
    {
        if (Bounds.Height <= 0) return;
        Quaternion currentOrientation;
        lock (cameraLock) { currentOrientation = orientation; }
        float fov = overheadLocked ? OverheadFieldOfView : fieldOfViewDegrees;
        float unitsPerPixel = 2f * (distance / 3f) * MathF.Tan(MathHelper.DegreesToRadians(fov) / 2f) / (float)Bounds.Height;

        Vector3 right = Vector3.Transform(Vector3.UnitX, currentOrientation);
        Vector3 rightLevel = new(right.X, 0f, right.Z);
        rightLevel = rightLevel.LengthSquared > 1e-6f ? Vector3.Normalize(rightLevel) : Vector3.UnitX;
        // Ahead along the ground: where the camera looks, flattened; looking straight down, the
        // screen's up direction is the way ahead instead.
        Vector3 look = -Vector3.Transform(Vector3.UnitZ, currentOrientation);
        Vector3 ahead = new(look.X, 0f, look.Z);
        if (ahead.LengthSquared < 1e-4f)
        {
            Vector3 screenUp = Vector3.Transform(Vector3.UnitY, currentOrientation);
            ahead = new Vector3(screenUp.X, 0f, screenUp.Z);
        }
        ahead = ahead.LengthSquared > 1e-6f ? Vector3.Normalize(ahead) : -Vector3.UnitZ;

        Vector3 move = (-rightLevel * dx + ahead * dy) * unitsPerPixel;
        targetX += move.X;
        targetZ += move.Z;
        ViewChanged?.Invoke();
        RequestNextFrameRendering();
    }

    private const float MinDistance = 0.3f;
    private const float MaxDistance = 100f;

    /// <summary>Positive deltaDistance zooms out, negative zooms in. Each step scales the distance by
    /// the same proportion, so zooming stays as fine close up as far away.</summary>
    public void Zoom(float deltaDistance)
    {
        // A game camera (SetGameCamera) can sit closer than MinDistance; zooming must not jump it outward.
        distance = Math.Clamp(distance * MathF.Exp(deltaDistance * 0.25f), Math.Min(MinDistance, distance), MaxDistance);
        ViewChanged?.Invoke();
        RequestNextFrameRendering();
    }

    public void ResetView()
    {
        distance = DefaultDistance;
        lock (cameraLock)
        {
            orientation = overheadLocked ? MakeOrientation(OverheadYaw, OverheadPitch) : MakeOrientation(DefaultYaw, DefaultPitch);
        }
        targetX = 0f;
        targetY = 0f;
        targetZ = 0f;
        if (overheadLocked) FitOverheadToGrid();
        ViewChanged?.Invoke();
        RequestNextFrameRendering();
    }

    private (float Distance, Quaternion Orientation, float X, float Y, float Z)? cameraBeforeOverhead;

    /// <summary>Locks/unlocks the camera to a fully overhead (top-down) position, matching legacy's
    /// SetCam2D behavior for the Move Permissions tab. While locked the camera cannot rotate, but can
    /// still be panned and zoomed; unlocking puts back the view from before the lock.</summary>
    public void SetOverheadLocked(bool locked)
    {
        if (locked == overheadLocked) return;
        if (locked)
        {
            lock (cameraLock)
            {
                cameraBeforeOverhead = (distance, orientation, targetX, targetY, targetZ);
                orientation = MakeOrientation(OverheadYaw, OverheadPitch);
            }

            overheadLocked = true;
            FitOverheadToGrid();
        }
        else if (cameraBeforeOverhead is { } previous)
        {
            lock (cameraLock)
            {
                orientation = previous.Orientation;
            }

            distance = previous.Distance;
            targetX = previous.X;
            targetY = previous.Y;
            targetZ = previous.Z;
            cameraBeforeOverhead = null;
        }

        overheadLocked = locked;
        ViewChanged?.Invoke();
        RequestNextFrameRendering();
    }

    /// <summary>Loads a single converted model with no placed parts - kept for callers that only ever
    /// have one model and no scene manifest (e.g. NSBTX/building previews elsewhere).</summary>
    public void LoadGlb(string path) => LoadSceneCore(path, []);

    /// <summary>Loads a map scene manifest (terrain.glb plus zero or more placed building parts,
    /// written by DSPRE.ScriptTool's export-map command) and assembles it into one renderable scene.</summary>
    // Scrolling-texture animations: the area's, for the terrain (slot i is model material i, following track i), and
    // one per animated building model (a slot is the material whose name a track has). A slot is numbered
    // group * MaxAnimatedSlots + track, group 0 being the terrain. Written from the UI thread, read by the render thread.
    private const int MaxAnimatedSlots = 16;
    private const int MaxAnimationGroups = 8;
    private bool animationRunning;
    private readonly object animationLock = new();
    private TextureAnimation? textureAnimation;
    private readonly Stopwatch animationClock = Stopwatch.StartNew();
    private double animationBaseFrame;
    private bool animationPaused;
    private bool buildingAnimationsEnabled = true;
    private Func<int, TextureAnimation?>? buildingAnimationProvider;
    private readonly Dictionary<int, (int Group, TextureAnimation Animation)?> buildingAnimationGroups = [];
    private readonly float[] animationOffsets = new float[MaxAnimatedSlots * MaxAnimationGroups * 2];

    // Models name a material after its texture plus a "_lm<number>" suffix ("sea_lm42" draws "sea"); an animation names the
    // texture, sometimes with a frame number ("t3_fl_b.1").
    private static readonly System.Text.RegularExpressions.Regex MaterialBaseName = new(@"_lm\d+$", System.Text.RegularExpressions.RegexOptions.Compiled);
    private static readonly System.Text.RegularExpressions.Regex FrameSuffix = new(@"\.\d+$", System.Text.RegularExpressions.RegexOptions.Compiled);

    private sealed record FrameImage(TextureData Texture, int Ticks);
    private sealed record FrameSet(string Target, IReadOnlyList<FrameImage> Frames)
    {
        public int TotalTicks => Math.Max(Frames.Sum(frame => frame.Ticks), 1);
    }

    // Platinum's texture animations: pictures that take turns standing in for the map texture of a given name.
    private IReadOnlyList<FrameSet> frameAnimations = [];
    private IReadOnlyList<FrameSet>? uploadedFrameSets;
    private readonly Dictionary<(int Set, int Frame), int> gpuFrameTextures = [];
    private Dictionary<string, int[]> materialTextureKeys = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Plays each set of frames, in turn and each for its number of game ticks (60 a second), as the texture of the
    /// model material named after <c>Target</c>; an empty list stops them. Starts from the first frames.</summary>
    public void SetFrameAnimations(IReadOnlyList<(string Target, IReadOnlyList<(int Width, int Height, byte[] Rgba, int Ticks)> Frames)> animations)
    {
        lock (animationLock)
        {
            frameAnimations = animations
                .Where(animation => animation.Frames.Count > 0)
                .Select(animation => new FrameSet(FrameSuffix.Replace(animation.Target, string.Empty),
                    animation.Frames.Select(frame => new FrameImage(new TextureData(frame.Rgba, frame.Width, frame.Height, (int)GlWrapMode.Repeat, (int)GlWrapMode.Repeat), frame.Ticks)).ToList()))
                .ToList();
            animationBaseFrame = 0;
            animationPaused = false;
            animationClock.Restart();
        }

        RequestNextFrameRendering();
    }

    /// <summary>Plays the animation on the loaded model, or stops animating with null. Starts from frame 0.</summary>
    public void SetTextureAnimation(TextureAnimation? animation)
    {
        lock (animationLock)
        {
            textureAnimation = animation;
            animationBaseFrame = 0;
            animationPaused = false;
            animationClock.Restart();
        }

        RequestNextFrameRendering();
    }

    /// <summary>Gives the animation of a building model by its number (the "building_NNNN" folder its converted
    /// model sits in), or null for none. Takes effect for scenes loaded after it is set.</summary>
    public void SetBuildingAnimationProvider(Func<int, TextureAnimation?>? provider)
    {
        lock (animationLock)
        {
            buildingAnimationProvider = provider;
            buildingAnimationGroups.Clear();
        }
    }

    /// <summary>Whether the buildings' animations play (the terrain's is set with SetTextureAnimation).</summary>
    public bool BuildingAnimationsEnabled
    {
        get { lock (animationLock) return buildingAnimationsEnabled; }
        set
        {
            lock (animationLock) buildingAnimationsEnabled = value;
            RequestNextFrameRendering();
        }
    }

    private Func<int, JointAnimation?>? buildingJointProvider;
    private readonly Dictionary<int, JointAnimation?> buildingJointAnimations = [];
    private sealed record SkinSpec(SkeletonData Skeleton, JointAnimation Animation, NumericsVector3[] Probe);
    private sealed record SkinInstance(SkeletonData Skeleton, JointAnimation Animation, NumericsMatrix Placement, NumericsMatrix PlacementInverse);
    private SkinInstance[] skinInstances = [];
    private readonly float[] boneMatrices = IdentityBones();

    private static float[] IdentityBones()
    {
        float[] matrices = new float[MaxSkinInstances * MaxSkinJoints * 16];
        for (int i = 0; i < MaxSkinInstances * MaxSkinJoints; i++) matrices[i * 16] = matrices[i * 16 + 5] = matrices[i * 16 + 10] = matrices[i * 16 + 15] = 1f;
        return matrices;
    }

    /// <summary>Gives the joint animation of a building model by its number, or null for none. Takes effect for
    /// scenes loaded after it is set.</summary>
    public void SetBuildingJointAnimationProvider(Func<int, JointAnimation?>? provider)
    {
        lock (animationLock)
        {
            buildingJointProvider = provider;
            buildingJointAnimations.Clear();
        }
    }

    private JointAnimation? BuildingJointAnimationFor(int modelId)
    {
        lock (animationLock)
        {
            if (buildingJointProvider is null) return null;
            if (!buildingJointAnimations.TryGetValue(modelId, out JointAnimation? animation)) buildingJointAnimations[modelId] = animation = buildingJointProvider(modelId);
            return animation;
        }
    }

    /// <summary>Makes a placed building follow its joint animation: its vertices are tied to one of the scene's
    /// skeleton slots, and what it needs to pose them is noted in <paramref name="specs"/>.</summary>
    private List<RawBatch> AssignBuildingSkin(List<RawBatch> batches, ScenePartSpec part, Vector3 extraOffset, List<SkinSpec> specs)
    {
        if (specs.Count >= MaxSkinInstances || BuildingFolder.Match(part.ModelPath) is not { Success: true } match) return batches;
        if (BuildingJointAnimationFor(int.Parse(match.Groups[1].Value)) is not { } animation) return batches;
        SkeletonData? skeleton;
        lock (skeletons) skeletons.TryGetValue(part.ModelPath, out skeleton);
        if (skeleton is null) return batches;

        // Where the model's origin and axes end up once placed: the same placement every vertex of this part goes through.
        var probe = new RawBatch([NumericsVector3.Zero, NumericsVector3.UnitX, NumericsVector3.UnitY, NumericsVector3.UnitZ], [], [], [], -1, -1f);
        specs.Add(new SkinSpec(skeleton, animation, PlacePart([probe], part, 0f, extraOffset)[0].Positions.ToArray()));
        int instance = specs.Count - 1;
        return batches.Select(batch => batch.Joints is null ? batch : batch with { SkinInstance = instance }).ToList();
    }

    /// <summary>Poses every animated skeleton for the current moment; true while something is moving.</summary>
    private bool UpdateBoneMatrices()
    {
        lock (animationLock)
        {
            SkinInstance[] instances = skinInstances;
            if (instances.Length == 0 || !buildingAnimationsEnabled)
            {
                Array.Copy(IdentityBones(), boneMatrices, boneMatrices.Length);
                return false;
            }

            double ticks = AnimationTicksLocked();
            for (int i = 0; i < instances.Length; i++)
            {
                SkinInstance instance = instances[i];
                SkeletonData skeleton = instance.Skeleton;
                int frame = instance.Animation.FrameCount > 0 ? (int)(ticks % instance.Animation.FrameCount) : 0;
                int count = skeleton.Parent.Length;
                var world = new NumericsMatrix?[count];
                NumericsMatrix World(int joint)
                {
                    if (world[joint] is { } known) return known;
                    JointAnimation.Pose? pose = instance.Animation.Joints.FirstOrDefault(curves => curves.Index == joint) is { } curves ? instance.Animation.PoseOf(curves, frame) : null;
                    NumericsMatrix local = NumericsMatrix.CreateScale(pose?.Scale ?? skeleton.RestScale[joint]) *
                        (pose?.Rotation ?? NumericsMatrix.CreateFromQuaternion(skeleton.RestRotation[joint])) *
                        NumericsMatrix.CreateTranslation(pose?.Translation ?? skeleton.RestTranslation[joint]);
                    int parent = skeleton.Parent[joint];
                    NumericsMatrix result = parent >= 0 && parent != joint ? local * World(parent) : local;
                    world[joint] = result;
                    return result;
                }

                for (int joint = 0; joint < count && joint < MaxSkinJoints; joint++)
                {
                    // The vertices are already placed, so the pose is applied inside the placement.
                    NumericsMatrix skin = instance.PlacementInverse * (skeleton.InverseBind[joint] * World(joint)) * instance.Placement;
                    int at = (i * MaxSkinJoints + joint) * 16;
                    boneMatrices[at] = skin.M11; boneMatrices[at + 1] = skin.M12; boneMatrices[at + 2] = skin.M13; boneMatrices[at + 3] = skin.M14;
                    boneMatrices[at + 4] = skin.M21; boneMatrices[at + 5] = skin.M22; boneMatrices[at + 6] = skin.M23; boneMatrices[at + 7] = skin.M24;
                    boneMatrices[at + 8] = skin.M31; boneMatrices[at + 9] = skin.M32; boneMatrices[at + 10] = skin.M33; boneMatrices[at + 11] = skin.M34;
                    boneMatrices[at + 12] = skin.M41; boneMatrices[at + 13] = skin.M42; boneMatrices[at + 14] = skin.M43; boneMatrices[at + 15] = skin.M44;
                }
            }

            return !animationPaused;
        }
    }

    public bool AnimationPaused
    {
        get { lock (animationLock) return animationPaused; }
        set
        {
            lock (animationLock)
            {
                if (animationPaused == value) return;
                animationBaseFrame = AnimationTicksLocked();
                animationPaused = value;
                animationClock.Restart();
            }

            RequestNextFrameRendering();
        }
    }

    /// <summary>The frame the terrain's animation is on (0 up to its length), or 0 with none.</summary>
    public double AnimationFrame
    {
        get { lock (animationLock) return textureAnimation is { FrameCount: > 0 } animation ? AnimationTicksLocked() % animation.FrameCount : 0; }
        set
        {
            lock (animationLock)
            {
                if (textureAnimation is null) return;
                animationBaseFrame = ((value % textureAnimation.FrameCount) + textureAnimation.FrameCount) % textureAnimation.FrameCount;
                animationClock.Restart();
            }

            RequestNextFrameRendering();
        }
    }

    // Game ticks since the animations started, not yet wrapped to any one animation's length.
    private double AnimationTicksLocked() => animationBaseFrame + (animationPaused ? 0 : animationClock.Elapsed.TotalSeconds * TextureAnimation.FramesPerSecond);

    /// <summary>The group and animation of a building model, allocated the first time it is asked for; null when it
    /// has none or all the groups are taken.</summary>
    private (int Group, TextureAnimation Animation)? BuildingAnimationFor(int modelId)
    {
        lock (animationLock)
        {
            if (buildingAnimationProvider is null) return null;
            if (buildingAnimationGroups.TryGetValue(modelId, out (int, TextureAnimation)? known)) return known;
            TextureAnimation? animation = buildingAnimationProvider(modelId);
            (int, TextureAnimation)? entry = animation is not null && buildingAnimationGroups.Values.Count(group => group is not null) < MaxAnimationGroups - 1
                ? (buildingAnimationGroups.Values.Count(group => group is not null) + 1, animation)
                : null;
            buildingAnimationGroups[modelId] = entry;
            return entry;
        }
    }

    private static readonly System.Text.RegularExpressions.Regex BuildingFolder = new(@"building_(\d+)[\\/]", System.Text.RegularExpressions.RegexOptions.Compiled);

    /// <summary>Points the batches of a placed building at its animation's slots, matching tracks to materials by name.</summary>
    private List<RawBatch> AssignBuildingAnimation(List<RawBatch> batches, string modelPath)
    {
        if (BuildingFolder.Match(modelPath) is not { Success: true } match || BuildingAnimationFor(int.Parse(match.Groups[1].Value)) is not ({ } group, { } animation)) return batches;
        return batches.Select(batch =>
        {
            int track = animation.Tracks.ToList().FindIndex(candidate => candidate.Name.Equals(batch.MaterialName, StringComparison.OrdinalIgnoreCase));
            return track >= 0 && track < MaxAnimatedSlots ? batch with { TextureSlot = group * MaxAnimatedSlots + track } : batch;
        }).ToList();
    }

    /// <summary>Fills the per-slot texture shifts for the current moment; true while something is animating.</summary>
    private bool UpdateAnimationOffsets()
    {
        lock (animationLock)
        {
            Array.Clear(animationOffsets);
            bool any = false;
            double ticks = AnimationTicksLocked();
            void Fill(int group, TextureAnimation animation)
            {
                any = true;
                int frame = animation.FrameCount > 0 ? (int)(ticks % animation.FrameCount) : 0;
                for (int track = 0; track < animation.Tracks.Count && track < MaxAnimatedSlots; track++)
                {
                    (float u, float v) = animation.Tracks[track].At(frame);
                    // The animation shifts the picture right by u and down by -v; sampling goes the other way.
                    int slot = group * MaxAnimatedSlots + track;
                    animationOffsets[slot * 2] = -u;
                    animationOffsets[slot * 2 + 1] = v;
                }
            }

            if (textureAnimation is not null) Fill(0, textureAnimation);
            if (frameAnimations.Count > 0) any = true;
            if (buildingAnimationsEnabled)
            {
                foreach ((int group, TextureAnimation animation) in buildingAnimationGroups.Values.OfType<(int Group, TextureAnimation Animation)>()) Fill(group, animation);
            }

            return any && !animationPaused;
        }
    }

    /// <summary>Shows nothing: drops the loaded map, buildings and markers, as when another project is
    /// opened and no map of the old one may stay on screen. A change of map keeps the icons, which the
    /// markers drawn next still use.</summary>
    public void ClearScene(bool keepEventIcons = false)
    {
        cachedTerrain = null;
        terrainHeightIndex = null;
        cachedPartFiles.Clear();
        cachedParts = [];
        compositeMaps = [];
        compositePartIndexMap = [];
        eventMarkers = [];
        lock (sceneLock)
        {
            batches = [];
            textures = [];
            indexCount = 0;
            gridBatch = null;
            selectionWireframe = null;
            tileGridLines = null;
        }
        TriangleCount = 0;
        PartCount = 0;
        if (!keepEventIcons) RemoveEventIcons("ow:");
        RequestNextFrameRendering();
    }

    public void LoadScene(string manifestPath)
    {
        if (!File.Exists(manifestPath))
        {
            throw new FileNotFoundException("The map scene manifest was not produced.", manifestPath);
        }

        string sceneDirectory = Path.GetDirectoryName(manifestPath)
            ?? throw new InvalidDataException("The map scene manifest has no containing directory.");
        SceneManifestDto manifest = JsonSerializer.Deserialize<SceneManifestDto>(File.ReadAllText(manifestPath))
            ?? throw new InvalidDataException("The map scene manifest could not be parsed.");

        List<ScenePartSpec> parts = manifest.Parts.Select(part => new ScenePartSpec(
            Path.Combine(sceneDirectory, part.Model),
            new Vector3(part.Position[0], part.Position[1], part.Position[2]),
            new Vector3(part.RotationDegrees[0], part.RotationDegrees[1], part.RotationDegrees[2]),
            new Vector3(part.Scale[0], part.Scale[1], part.Scale[2]))).ToList();
        LoadSceneCore(Path.Combine(sceneDirectory, manifest.Terrain), parts);
    }

    /// <summary>One map's decoded terrain, its own placed-building parts, and its world-space Offset -
    /// everything RebuildScene needs to place it relative to the other maps sharing the scene without
    /// re-reading anything from disk. Parts stay a mutable List (not further wrapped) so
    /// UpdatePartTransform can edit one building's placement in place for live-preview, exactly like the
    /// single-map path's cachedParts does.</summary>
    private sealed record CompositeMapScene(
        List<RawBatch> TerrainBatches,
        Dictionary<int, TextureData> TerrainTextures,
        List<ScenePartSpec> Parts,
        Dictionary<string, (List<RawBatch> Batches, Dictionary<int, TextureData> Textures)> PartFiles,
        Vector3 Offset);
    private List<CompositeMapScene> compositeMaps = [];

    /// <summary>Flattens every composite map's own placed parts into one index space, in map order then
    /// local-part order, so TryPickBuildingPart/SetSelectedPart/SetDirtyParts/UpdatePartTransform can
    /// address "the Nth building across the whole composite" with the same plain int index they already
    /// use for a single map's cachedParts - MainWindow's building list is built in this exact same
    /// order (map order matching LoadCompositeMapsAsync's placements, then that map's own building
    /// order), so a flat index means the same building on both sides without any lookup table needed
    /// beyond this one.</summary>
    private List<(int MapIndex, int LocalIndex)> compositePartIndexMap = [];

    /// <summary>Loads several maps' terrain plus their own placed-building parts into one combined
    /// scene, each shifted by its own world-space Offset before merging - the caller (MainWindow's
    /// Overworld Map Editor tab) computes that offset from the maps' relative row/column in a shared
    /// matrix, since that is what "maps loaded together in-game" actually means for these ROMs: adjacent
    /// matrix cells are adjacent map tiles in world space.</summary>
    public void LoadCompositeScene(IReadOnlyList<(string ManifestPath, Vector3 Offset)> maps, bool includeBuildings = true)
    {
        var scenes = new List<CompositeMapScene>();
        foreach ((string manifestPath, Vector3 offset) in maps)
        {
            if (DecodeCompositeMap(manifestPath, offset, includeBuildings) is { } scene) scenes.Add(scene);
        }

        if (scenes.Count == 0)
        {
            throw new InvalidDataException("None of the linked maps' converted models could be loaded.");
        }

        cachedTerrain = null;
        terrainHeightIndex = null;
        cachedPartFiles.Clear();
        cachedParts = [];
        compositeMaps = scenes;

        compositePartIndexMap = [];
        for (int mapIndex = 0; mapIndex < scenes.Count; mapIndex++)
        {
            for (int localIndex = 0; localIndex < scenes[mapIndex].Parts.Count; localIndex++)
            {
                compositePartIndexMap.Add((mapIndex, localIndex));
            }
        }

        RebuildScene();
    }

    private static CompositeMapScene? DecodeCompositeMap(string manifestPath, Vector3 offset, bool includeBuildings)
    {
        if (!File.Exists(manifestPath)) return null;
        string? sceneDirectory = Path.GetDirectoryName(manifestPath);
        if (sceneDirectory is null) return null;
        SceneManifestDto? manifest;
        try
        {
            manifest = JsonSerializer.Deserialize<SceneManifestDto>(File.ReadAllText(manifestPath));
        }
        catch (JsonException)
        {
            return null;
        }
        if (manifest is null) return null;

        string terrainPath = Path.Combine(sceneDirectory, manifest.Terrain);
        if (!File.Exists(terrainPath)) return null;
        (List<RawBatch> terrainBatches, Dictionary<int, TextureData> terrainTextures) = DecodeModel(terrainPath, animatedTerrain: true);

        List<ScenePartSpec> mapParts = (includeBuildings ? manifest.Parts : []).Select(part => new ScenePartSpec(
            Path.Combine(sceneDirectory, part.Model),
            new Vector3(part.Position[0], part.Position[1], part.Position[2]),
            new Vector3(part.RotationDegrees[0], part.RotationDegrees[1], part.RotationDegrees[2]),
            new Vector3(part.Scale[0], part.Scale[1], part.Scale[2]))).ToList();

        var partFiles = new Dictionary<string, (List<RawBatch> Batches, Dictionary<int, TextureData> Textures)>();
        foreach (ScenePartSpec part in mapParts)
        {
            if (!File.Exists(part.ModelPath) || partFiles.ContainsKey(part.ModelPath)) continue;
            partFiles[part.ModelPath] = DecodeModel(part.ModelPath, skinned: true);
        }

        return new CompositeMapScene(terrainBatches, terrainTextures, mapParts, partFiles, offset);
    }

    private void RebuildCompositePartIndex()
    {
        compositePartIndexMap = [];
        for (int mapIndex = 0; mapIndex < compositeMaps.Count; mapIndex++)
        {
            for (int localIndex = 0; localIndex < compositeMaps[mapIndex].Parts.Count; localIndex++)
            {
                compositePartIndexMap.Add((mapIndex, localIndex));
            }
        }
    }

    /// <summary>Swaps, adds or (with a null manifest) removes the one map drawn at the given offset in a loaded
    /// composite scene; every other map keeps its decoded data. Returns false when no composite is loaded.</summary>
    public bool ReplaceCompositeMap(Vector3 offset, string? manifestPath, bool includeBuildings)
    {
        if (compositeMaps.Count == 0) return false;
        CompositeMapScene? scene = manifestPath is null ? null : DecodeCompositeMap(manifestPath, offset, includeBuildings);
        if (manifestPath is not null && scene is null) return false;

        var maps = compositeMaps.Where(map => map.Offset != offset).ToList();
        if (scene is not null) maps.Add(scene);
        if (maps.Count == 0) return false;

        compositeMaps = maps;
        terrainHeightIndex = null;
        RebuildCompositePartIndex();
        RebuildScene();
        return true;
    }

    /// <summary>Decodes one converted glTF file into raw (untransformed, un-normalized) batches, exactly
    /// as apicula/SharpGLTF produced them - the caller is responsible for placement and normalization.</summary>
    /// <param name="animatedTerrain">The model is a map's terrain, whose materials an area's texture animation addresses
    /// by number; a building's materials are not animated by it.</param>
    /// <param name="skinned">The model is a building, whose vertices' joints and skeleton are kept for its joint animation.</param>
    private static (List<RawBatch> Batches, Dictionary<int, TextureData> Textures) DecodeModel(string path, bool animatedTerrain = false, bool skinned = false)
    {
        var textures = new Dictionary<int, TextureData>();
        // apicula-exported glb files occasionally emit slightly non-unit-length normals (quantization
        // artifacts), which SharpGLTF's default Strict validation rejects outright; TryFix lets it
        // auto-repair those instead of throwing.
        ModelRoot model = ModelRoot.Load(path, new ReadSettings { Validation = ValidationMode.TryFix });
        var positions = new List<NumericsVector3>();
        var rawBatches = new List<RawBatch>();
        foreach (Mesh mesh in model.LogicalMeshes)
        {
            foreach (MeshPrimitive primitive in mesh.Primitives)
            {
                var batchPositions = new List<NumericsVector3>();
                var batchTexcoords = new List<NumericsVector2>();
                var batchColors = new List<NumericsVector4>();
                var batchIndices = new List<uint>();
                MaterialChannel? baseColor = primitive.Material?.FindChannel("BaseColor");
                int textureKey = baseColor?.Texture?.PrimaryImage?.LogicalIndex ?? -1;
                if (textureKey >= 0 && !textures.ContainsKey(textureKey) && baseColor?.Texture?.PrimaryImage?.Content.Content is ReadOnlyMemory<byte> encodedTexture)
                {
                    ImageResult image = ImageResult.FromStream(new MemoryStream(encodedTexture.ToArray()), ColorComponents.RedGreenBlueAlpha);
                    TextureSampler? sampler = baseColor?.Texture?.Sampler;
                    textures[textureKey] = new TextureData(
                        image.Data,
                        image.Width,
                        image.Height,
                        ToGlWrapMode(sampler?.WrapS),
                        ToGlWrapMode(sampler?.WrapT));
                }

                // baseColorFactor tints/fades the whole material (e.g. a translucent shadow decal at
                // 29% opacity) and is folded straight into each vertex's color below rather than kept
                // as a separate per-batch uniform, so it survives the group-by-texture merge later
                // even when two merged batches came from materials with different factors.
                NumericsVector4 baseColorFactor = baseColor?.Parameter ?? NumericsVector4.One;
                AlphaMode alphaMode = primitive.Material?.Alpha ?? AlphaMode.OPAQUE;
                float alphaCutoff = alphaMode switch
                {
                    AlphaMode.MASK => primitive.Material?.AlphaCutoff ?? 0.5f,
                    AlphaMode.BLEND => -2f,
                    _ => -1f
                };

                // Read straight off each accessor via the Schema2 API rather than through
                // SharpGLTF.Runtime's MeshDecoder/IMeshPrimitiveDecoder convenience layer: that layer
                // only decodes TEXCOORD_0 correctly for the first primitive of a mesh when several
                // primitives share one accessor - which every apicula-exported model does (one shared
                // vertex pool per mesh, sliced per material by each primitive's own index buffer) -
                // silently returning (0,0) for every vertex of every later primitive. That is exactly
                // what produced the flat single-texel-color ground and "TV static" building textures.
                Accessor? positionAccessor = primitive.GetVertexAccessor("POSITION");
                if (positionAccessor is null) continue;
                IList<NumericsVector3> primPositions = positionAccessor.AsVector3Array();
                IList<NumericsVector2>? primTexcoords = primitive.GetVertexAccessor("TEXCOORD_0")?.AsVector2Array();
                // COLOR_0 is a per-vertex baked-lighting/shadow tint DS models rely on being
                // multiplied over the base texture; skipping it was rendering raw, untinted
                // texture data (visible as stray wrong-colored patches on shadow/detail materials).
                IList<NumericsVector4>? primColors = primitive.GetVertexAccessor("COLOR_0")?.AsColorArray(1f);
                int[]? primJoints = null;
                if (skinned && primitive.GetVertexAccessor("JOINTS_0")?.AsVector4Array() is { } jointIndices)
                {
                    // Each vertex follows the joint that weighs most on it; DS models rarely blend two.
                    IList<NumericsVector4>? weights = primitive.GetVertexAccessor("WEIGHTS_0")?.AsVector4Array();
                    primJoints = new int[primPositions.Count];
                    for (int i = 0; i < primJoints.Length; i++)
                    {
                        NumericsVector4 w = weights is not null ? weights[i] : new NumericsVector4(1f, 0f, 0f, 0f);
                        int best = w.X >= w.Y && w.X >= w.Z && w.X >= w.W ? 0 : w.Y >= w.Z && w.Y >= w.W ? 1 : w.Z >= w.W ? 2 : 3;
                        primJoints[i] = (int)(best switch { 0 => jointIndices[i].X, 1 => jointIndices[i].Y, 2 => jointIndices[i].Z, _ => jointIndices[i].W });
                    }
                }

                for (int i = 0; i < primPositions.Count; i++)
                {
                    NumericsVector3 position = primPositions[i];
                    positions.Add(position);
                    batchPositions.Add(position);
                    batchTexcoords.Add(primTexcoords is not null ? primTexcoords[i] : NumericsVector2.Zero);
                    NumericsVector4 vertexColor = primColors is not null ? primColors[i] : NumericsVector4.One;
                    batchColors.Add(vertexColor * baseColorFactor);
                }
                foreach ((int A, int B, int C) triangle in primitive.GetTriangleIndices())
                {
                    batchIndices.Add((uint)triangle.A);
                    batchIndices.Add((uint)triangle.B);
                    batchIndices.Add((uint)triangle.C);
                }
                rawBatches.Add(new RawBatch(batchPositions, batchTexcoords, batchColors, batchIndices, textureKey, alphaCutoff, 0f, animatedTerrain && primitive.Material?.LogicalIndex is int materialIndex && materialIndex < MaxAnimatedSlots ? materialIndex : -1, primitive.Material?.Name, primJoints));
            }
        }

        if (positions.Count == 0 || rawBatches.All(batch => batch.Indices.Count == 0))
        {
            throw new InvalidDataException($"The converted model '{Path.GetFileName(path)}' contains no renderable triangles.");
        }

        if (skinned && model.LogicalSkins.Count > 0)
        {
            lock (skeletons) skeletons[path] = BuildSkeleton(model.LogicalSkins[0]);
        }

        return (rawBatches, textures);
    }

    private const int MaxSkinInstances = 4;
    private const int MaxSkinJoints = 16;

    /// <summary>A building model's joints: how each hangs off the one above it, how it sits at rest, and the matrix
    /// that takes the model's vertices into its space.</summary>
    private sealed record SkeletonData(int[] Parent, NumericsVector3[] RestTranslation, NumericsQuaternion[] RestRotation, NumericsVector3[] RestScale, NumericsMatrix[] InverseBind);

    private static readonly Dictionary<string, SkeletonData?> skeletons = [];

    private static SkeletonData? BuildSkeleton(Skin skin)
    {
        int count = skin.JointsCount;
        if (count == 0 || count > MaxSkinJoints) return null;
        var nodes = new Node[count];
        var inverseBind = new NumericsMatrix[count];
        for (int i = 0; i < count; i++) (nodes[i], inverseBind[i]) = skin.GetJoint(i);
        var parent = new int[count];
        var translation = new NumericsVector3[count];
        var rotation = new NumericsQuaternion[count];
        var scale = new NumericsVector3[count];
        for (int i = 0; i < count; i++)
        {
            parent[i] = Array.IndexOf(nodes, nodes[i].VisualParent);
            if (!NumericsMatrix.Decompose(nodes[i].LocalMatrix, out scale[i], out rotation[i], out translation[i])) return null;
        }

        return new SkeletonData(parent, translation, rotation, scale, inverseBind);
    }

    // Cached raw (decoded-from-disk, untransformed) data from the most recent LoadScene call, kept
    // around so UpdatePartTransform can redo just the placement/normalize/upload steps below in
    // response to a live building-editor drag - no file IO, JSON parsing, or apicula round-trip
    // needed for a transform-only change, which is what makes it fast enough to feel real-time.
    private (List<RawBatch> Batches, Dictionary<int, TextureData> Textures)? cachedTerrain;

    private const float HeightBucketSize = 16f;
    private Dictionary<(int, int), List<(NumericsVector3 A, NumericsVector3 B, NumericsVector3 C)>>? terrainHeightIndex;

    /// <summary>The height of the loaded terrain's surface at a world X/Z point. Where several surfaces
    /// lie over the point (a bridge over a path, tree tops over grass), the one closest to
    /// <paramref name="preferredY"/> is used; with no terrain there, <paramref name="preferredY"/>
    /// itself comes back.</summary>
    public float TerrainHeightAt(float worldX, float worldZ, float preferredY)
    {
        if (cachedTerrain is null && compositeMaps.Count == 0) return preferredY;
        terrainHeightIndex ??= cachedTerrain is not null
            ? BuildTerrainHeightIndex([(cachedTerrain.Value.Batches, NumericsVector3.Zero)])
            : BuildTerrainHeightIndex(compositeMaps.Select(map => (map.TerrainBatches, new NumericsVector3(map.Offset.X, map.Offset.Y, map.Offset.Z))).ToList());
        if (!terrainHeightIndex.TryGetValue(((int)MathF.Floor(worldX / HeightBucketSize), (int)MathF.Floor(worldZ / HeightBucketSize)), out var triangles)) return preferredY;

        float best = preferredY, bestDistance = float.MaxValue;
        foreach ((NumericsVector3 a, NumericsVector3 b, NumericsVector3 c) in triangles)
        {
            float det = (b.Z - c.Z) * (a.X - c.X) + (c.X - b.X) * (a.Z - c.Z);
            if (MathF.Abs(det) < 1e-6f) continue;
            float u = ((b.Z - c.Z) * (worldX - c.X) + (c.X - b.X) * (worldZ - c.Z)) / det;
            float v = ((c.Z - a.Z) * (worldX - c.X) + (a.X - c.X) * (worldZ - c.Z)) / det;
            float w = 1f - u - v;
            const float Edge = -1e-4f;
            if (u < Edge || v < Edge || w < Edge) continue;
            float height = u * a.Y + v * b.Y + w * c.Y;
            float distance = MathF.Abs(height - preferredY);
            if (distance < bestDistance)
            {
                bestDistance = distance;
                best = height;
            }
        }

        return best;
    }

    /// <summary>Terrain triangles bucketed by world X/Z; in a multi-map scene each map's terrain is
    /// shifted by its own offset first, the same placement RebuildScene gives it.</summary>
    private static Dictionary<(int, int), List<(NumericsVector3, NumericsVector3, NumericsVector3)>> BuildTerrainHeightIndex(List<(List<RawBatch> Batches, NumericsVector3 Offset)> terrains)
    {
        var index = new Dictionary<(int, int), List<(NumericsVector3, NumericsVector3, NumericsVector3)>>();
        foreach ((List<RawBatch> batches, NumericsVector3 offset) in terrains)
        foreach (RawBatch batch in batches)
        {
            for (int k = 0; k + 2 < batch.Indices.Count; k += 3)
            {
                NumericsVector3 a = batch.Positions[(int)batch.Indices[k]] + offset;
                NumericsVector3 b = batch.Positions[(int)batch.Indices[k + 1]] + offset;
                NumericsVector3 c = batch.Positions[(int)batch.Indices[k + 2]] + offset;
                int minX = (int)MathF.Floor(MathF.Min(a.X, MathF.Min(b.X, c.X)) / HeightBucketSize);
                int maxX = (int)MathF.Floor(MathF.Max(a.X, MathF.Max(b.X, c.X)) / HeightBucketSize);
                int minZ = (int)MathF.Floor(MathF.Min(a.Z, MathF.Min(b.Z, c.Z)) / HeightBucketSize);
                int maxZ = (int)MathF.Floor(MathF.Max(a.Z, MathF.Max(b.Z, c.Z)) / HeightBucketSize);
                for (int x = minX; x <= maxX; x++)
                {
                    for (int z = minZ; z <= maxZ; z++)
                    {
                        if (!index.TryGetValue((x, z), out var list)) index[(x, z)] = list = [];
                        list.Add((a, b, c));
                    }
                }
            }
        }

        return index;
    }
    private readonly Dictionary<string, (List<RawBatch> Batches, Dictionary<int, TextureData> Textures)> cachedPartFiles = [];
    private List<ScenePartSpec> cachedParts = [];

    /// <summary>Decodes the terrain plus every placed part from disk, caches the raw decode, then
    /// places/normalizes/uploads via RebuildScene. Placement math mirrors ScaleTranslateRotateBuilding
    /// in the legacy WinForms renderer (DS_Map/Rendering/LegacyMap3DRenderer.cs): rotate X, then Y,
    /// then Z, then scale, then translate.</summary>
    private void LoadSceneCore(string terrainPath, IReadOnlyList<ScenePartSpec> parts)
    {
        if (!File.Exists(terrainPath))
        {
            throw new FileNotFoundException("The converted map model was not produced.", terrainPath);
        }

        compositeMaps = [];
        compositePartIndexMap = [];
        // Event markers are set independently via SetEventMarkers (never decoded from the scene
        // manifest itself, unlike buildings) - clearing the previous map's here avoids a stale marker
        // set/selection briefly surviving into a newly loaded map before the caller's own post-load
        // SetEventMarkers call replaces it.
        eventMarkers = [];
        selectedEventIndex = -1;
        dirtyEventIndices = [];
        cachedTerrain = DecodeModel(terrainPath, animatedTerrain: true);
        terrainHeightIndex = null;
        cachedPartFiles.Clear();
        foreach (ScenePartSpec part in parts)
        {
            if (!File.Exists(part.ModelPath) || cachedPartFiles.ContainsKey(part.ModelPath))
            {
                continue;
            }

            cachedPartFiles[part.ModelPath] = DecodeModel(part.ModelPath, skinned: true);
        }

        cachedParts = parts.ToList();
        RebuildScene();
    }

    /// <summary>Swaps the single-map scene's placed buildings for the given ones, keeping the decoded terrain
    /// and every building model already decoded; only model files not seen before are read. Returns false
    /// (leaving the scene as it was) when no single-map scene is loaded or a model file is missing.</summary>
    public bool ReplaceParts(IReadOnlyList<(string ModelPath, Vector3 Position, Vector3 RotationDegrees, Vector3 Scale)> parts)
    {
        if (cachedTerrain is null || compositeMaps.Count > 0) return false;
        if (parts.Any(part => !File.Exists(part.ModelPath))) return false;
        foreach ((string modelPath, _, _, _) in parts)
        {
            if (!cachedPartFiles.ContainsKey(modelPath)) cachedPartFiles[modelPath] = DecodeModel(modelPath, skinned: true);
        }

        cachedParts = parts.Select(part => new ScenePartSpec(part.ModelPath, part.Position, part.RotationDegrees, part.Scale)).ToList();
        RebuildScene();
        return true;
    }

    /// <summary>Updates one placed part's transform (position/rotation/scale) in place and re-places/
    /// re-normalizes/re-uploads the whole cached scene - a live-editing counterpart to LoadSceneCore/
    /// LoadCompositeScene that never touches disk, for the building editor's position/rotation/scale
    /// fields to call on every value change instead of requiring an explicit save + full re-export to
    /// preview a move. partIndex is a flat index: MainWindow's own building index in single-map mode, or
    /// the same flat index compositePartIndexMap/TryPickBuildingPart use in composite mode. No-ops if no
    /// scene is loaded yet or the index is out of range (e.g. a part whose model failed to resolve
    /// during export, which LoadSceneCore/LoadCompositeScene silently skip too).</summary>
    public void UpdatePartTransform(int partIndex, Vector3 position, Vector3 rotationDegrees, Vector3 scale)
    {
        if (compositeMaps.Count > 0)
        {
            if (partIndex < 0 || partIndex >= compositePartIndexMap.Count) return;
            (int mapIndex, int localIndex) = compositePartIndexMap[partIndex];
            List<ScenePartSpec> mapParts = compositeMaps[mapIndex].Parts;
            mapParts[localIndex] = mapParts[localIndex] with { Position = position, RotationDegrees = rotationDegrees, Scale = scale };
            RebuildScene();
            return;
        }

        if (cachedTerrain is null || partIndex < 0 || partIndex >= cachedParts.Count)
        {
            return;
        }

        cachedParts[partIndex] = cachedParts[partIndex] with { Position = position, RotationDegrees = rotationDegrees, Scale = scale };
        RebuildScene();
    }

    /// <summary>Moves the orbit target onto a placed part (single-map scenes), keeping the current
    /// orientation and zoom.</summary>
    public void FocusOnPart(int partIndex)
    {
        if (cachedTerrain is null || partIndex < 0 || partIndex >= cachedParts.Count || sceneScale <= 0.0001f) return;

        Vector3 position = cachedParts[partIndex].Position;
        float scale = sceneScale / 2f;
        targetX = -(position.X - sceneCenter.X) * scale;
        targetY = (position.Z - sceneCenter.Z) * scale;
        targetZ = (position.Y - sceneCenter.Y) * scale;
        ViewChanged?.Invoke();
        RequestNextFrameRendering();
    }

    /// <summary>Moves the orbit target over a ground point of the scene's raw world coordinates, keeping the
    /// current orientation and zoom.</summary>
    public void FocusOnGroundPoint(float worldX, float worldZ)
    {
        if (sceneScale <= 0.0001f) return;
        float scale = sceneScale / 2f;
        targetX = -(worldX - sceneCenter.X) * scale;
        targetY = (worldZ - sceneCenter.Z) * scale;
        ViewChanged?.Invoke();
        RequestNextFrameRendering();
    }

    private int selectedPartIndex = -1;

    /// <summary>Highlights one placed part (by the same index UpdatePartTransform/the scene manifest
    /// use) so the building editor's current selection is visible in the 3D view, not just in the
    /// sidebar list. Pass -1 to clear the highlight. Persists across UpdatePartTransform calls (it is
    /// only reset by loading a new scene) since it is a plain field, not scene-derived state.</summary>
    public void SetSelectedPart(int partIndex)
    {
        if (selectedPartIndex == partIndex)
        {
            return;
        }

        selectedPartIndex = partIndex;
        RebuildScene();
    }

    private HashSet<int> dirtyPartIndices = [];

    /// <summary>Marks which placed parts (by the same index UpdatePartTransform/SetSelectedPart use)
    /// differ from their last-saved state, so they can be highlighted in the 3D view regardless of
    /// selection until saved. Replaces the whole set each call (not incremental) since the caller
    /// already knows the full current dirty set from its own saved-vs-current comparison.</summary>
    public void SetDirtyParts(IEnumerable<int> partIndices)
    {
        dirtyPartIndices = partIndices.ToHashSet();
        RebuildScene();
    }

    /// <summary>ARGB color for one permission-grid cell, in the same 0-255 channel range as
    /// System.Drawing.Color.FromArgb - kept renderer-agnostic (no DSPRE.Core.MapPermissionPalette
    /// dependency here) so MapGlView only ever draws colors it is handed, not permission semantics.</summary>
    public readonly record struct GridCellColor(byte A, byte R, byte G, byte B);

    // World units per permission-grid cell, matching the placed-building position convention
    // (DSPRE.ScriptTool/Program.cs's unitsPerGridCell) - both buildings and the permission grid share
    // the same per-map world coordinate space, so reusing this constant keeps grid cells aligned with
    // the terrain and any placed buildings without needing a second, independently-tuned scale.
    private const float UnitsPerGridCell = 16f;
    private const int GridSize = 32;
    // The permission grid is always exactly GridSize*UnitsPerGridCell (512) world units square - see
    // https://ds-pokemon-hacking.github.io/docs/generation-iv/guides/mapping/, which documents a fixed
    // 32x32 grid where "each grid square is one unit wide... a map is nominally 32 units wide by 32
    // units deep", one unit being 16 world units. A map's *terrain mesh*, in contrast, routinely
    // extends beyond that boundary (the same doc: "Maps may extend beyond the 32x32 boundary visually,
    // but the data from levels of hierarchy below the Map level cannot") for connections to neighboring
    // maps, and how much varies per map - there is no universal terrain-to-grid scale factor to find,
    // because the terrain isn't supposed to exactly match the grid's footprint in the first place. Any
    // border padding making the terrain look larger than the grid is therefore expected, not a bug.
    private const float GridWorldExtent = GridSize * UnitsPerGridCell;
    private GridCellColor[,]? permissionGridColors;
    // The normalized render-space Z (see RebuildScene's normalization pass) the grid quads were most
    // recently placed at - TryPickGridCell intersects a pointer ray against this same plane, so a hit
    // always lands exactly where the grid was actually drawn.
    private float gridPlaneRenderZ;

    /// <summary>Shows/updates/hides the permission-grid overlay (32x32 cells, colors supplied by the
    /// caller). Pass null to hide it. Triggers a scene rebuild so the grid renders as real 3D geometry
    /// in the same coordinate space as the terrain - this guarantees pixel-exact alignment with
    /// whatever the camera actually renders, since both go through the identical transform.</summary>
    public void SetPermissionGrid(GridCellColor[,]? cellColors)
    {
        permissionGridColors = cellColors;
        if (cachedTerrain is null && compositeMaps.Count == 0) return;
        // Only the overlay changes while painting; a full rebuild would re-upload every texture on each stroke.
        RenderBatch? newGrid;
        lock (sceneLock)
        {
            NumericsVector3 center = new(sceneCenter.X, sceneCenter.Y, sceneCenter.Z);
            newGrid = cellColors is null || batches is null ? null : BuildPermissionGridBatch(cellColors, center, 4f / sceneScale);
            gridBatch = newGrid;
        }
        RequestNextFrameRendering();
    }

    /// <summary>Shows or hides lines along the map's 32x32 tile boundaries, the same cells the
    /// permission grid and buildings are placed on.</summary>
    public void SetTileGridVisible(bool visible)
    {
        if (visible == tileGridVisible) return;
        tileGridVisible = visible;
        if (cachedTerrain is not null) RebuildScene();
    }

    /// <summary>Casts a ray from a pointer position (in this control's local coordinates) through the
    /// current camera and intersects it with the plane the permission grid is drawn on, returning the
    /// grid cell (col, row) hit, if any. Uses the exact same view/projection construction as
    /// RenderFrame, so results are consistent with what is on screen regardless of camera state.</summary>
    public bool TryPickGridCell(Point viewportPoint, out int col, out int row)
    {
        col = row = -1;
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || sceneScale <= 0.0001f) return false;

        Quaternion currentOrientation;
        lock (cameraLock) { currentOrientation = orientation; }
        Vector3 target = new(targetX, targetY, targetZ);
        Vector3 offset = Vector3.Transform(Vector3.UnitZ, currentOrientation) * (distance / 3f);
        Vector3 up = Vector3.Transform(Vector3.UnitY, currentOrientation);
        Vector3 camera = target + offset;
        Matrix4 view = Matrix4.LookAt(camera, target, up);
        float aspect = (float)Bounds.Width / Math.Max(1f, (float)Bounds.Height);
        Matrix4 projection = CreateProjection(aspect);
        Matrix4 viewProjection = view * projection;
        Matrix4 inverse = Matrix4.Invert(viewProjection);

        float ndcX = (float)(viewportPoint.X / Bounds.Width) * 2f - 1f;
        float ndcY = 1f - (float)(viewportPoint.Y / Bounds.Height) * 2f;
        Vector4 nearPoint = new Vector4(ndcX, ndcY, -1f, 1f) * inverse;
        Vector4 farPoint = new Vector4(ndcX, ndcY, 1f, 1f) * inverse;
        if (Math.Abs(nearPoint.W) < 1e-8f || Math.Abs(farPoint.W) < 1e-8f) return false;
        Vector3 rayOrigin = nearPoint.Xyz / nearPoint.W;
        Vector3 rayEnd = farPoint.Xyz / farPoint.W;
        Vector3 rayDir = rayEnd - rayOrigin;
        if (Math.Abs(rayDir.Z) < 1e-8f) return false;

        float t = (gridPlaneRenderZ - rayOrigin.Z) / rayDir.Z;
        if (t < 0f) return false;
        Vector3 hit = rayOrigin + rayDir * t;

        float extent = 4f / sceneScale;
        float worldX = sceneCenter.X - hit.X * extent / 2f;
        float worldZ = sceneCenter.Z + hit.Y * extent / 2f;
        int hitCol = (int)MathF.Floor(worldX / UnitsPerGridCell) + GridSize / 2;
        int hitRow = (int)MathF.Floor(worldZ / UnitsPerGridCell) + GridSize / 2;
        if (hitCol < 0 || hitCol >= GridSize || hitRow < 0 || hitRow >= GridSize) return false;

        col = hitCol;
        row = hitRow;
        return true;
    }

    private volatile float fieldOfViewDegrees = 45f;
    private volatile bool orthographicProjection;

    /// <summary>Centres the overhead view on the 32x32 permission grid and zooms so the whole grid
    /// just fits.</summary>
    private void FitOverheadToGrid()
    {
        float scale = sceneScale / 2f;
        float span = GridSize * UnitsPerGridCell * scale;
        float aspect = Bounds.Width > 0 && Bounds.Height > 0 ? (float)(Bounds.Width / Bounds.Height) : 1f;
        float height = span * 1.04f / Math.Min(1f, aspect);
        float halfFov = MathHelper.DegreesToRadians(OverheadFieldOfView) / 2f;
        distance = Math.Clamp(3f * height / (2f * MathF.Tan(halfFov)), MinDistance, MaxDistance);
        // The grid is centred on world X/Z 0; render X is mirrored and render Y is world Z.
        targetX = sceneCenter.X * scale;
        targetY = -sceneCenter.Z * scale;
        targetZ = 0f;
    }

    private const float OverheadFieldOfView = 45f;
    private static readonly Vector3 UntexturedColor = new(0.72f, 0.78f, 0.84f);

    private Matrix4 CreateProjection(float aspect)
    {
        // The permissions view is a true top-down 2D view: orthographic, so every tile lines up with
        // its grid cell whatever its height or distance from the centre, and near/far span the whole
        // scene so nothing above the camera plane is clipped.
        if (overheadLocked)
        {
            float overheadHeight = 2f * (distance / 3f) * MathF.Tan(MathHelper.DegreesToRadians(OverheadFieldOfView) / 2f);
            return Matrix4.CreateOrthographic(overheadHeight * aspect, overheadHeight, -50f, 50f);
        }

        if (!orthographicProjection)
        {
            return Matrix4.CreatePerspectiveFieldOfView(MathHelper.DegreesToRadians(fieldOfViewDegrees), aspect, 0.05f, 50f);
        }

        // An orthographic game camera still has a field of view: it sets how much of the map the view
        // spans at the camera's distance.
        float height = 2f * (distance / 3f) * MathF.Tan(MathHelper.DegreesToRadians(fieldOfViewDegrees) / 2f);
        return Matrix4.CreateOrthographic(height * aspect, height, 0.05f, 50f);
    }

    /// <summary>Puts the camera where one of the game's field cameras would be: looking down at the
    /// scene centre from <paramref name="pitchDownDegrees"/> above the horizon, turned by
    /// <paramref name="yawDegrees"/>, <paramref name="distance"/> world units away, with the given
    /// full vertical field of view. Call after the scene is loaded, since the distance is converted
    /// with the loaded scene's scale.</summary>
    public void SetGameCamera(float pitchDownDegrees, float yawDegrees, float distance, float fieldOfView, bool orthographic)
    {
        // MakeOrientation's pitch 90 looks along the ground and 180 straight down (see OverheadPitch).
        float pitch = 90f + Math.Clamp(pitchDownDegrees, 0f, 90f);
        lock (cameraLock)
        {
            orientation = MakeOrientation(DefaultYaw + yawDegrees, pitch);
        }

        // Render space is the scene scaled by sceneScale / 2, and the camera sits distance / 3 from its target.
        this.distance = Math.Max(0.01f, 3f * distance * sceneScale / 2f);
        fieldOfViewDegrees = Math.Clamp(fieldOfView, 1f, 120f);
        orthographicProjection = orthographic;
        targetX = targetY = targetZ = 0f;
        ViewChanged?.Invoke();
        RequestNextFrameRendering();
    }

    /// <summary>The camera's view * projection for the current viewport, shared by the world-space
    /// picking and projection helpers below.</summary>
    private Matrix4 CurrentViewProjection()
    {
        Quaternion currentOrientation;
        lock (cameraLock) { currentOrientation = orientation; }
        Vector3 target = new(targetX, targetY, targetZ);
        Vector3 offset = Vector3.Transform(Vector3.UnitZ, currentOrientation) * (distance / 3f);
        Vector3 up = Vector3.Transform(Vector3.UnitY, currentOrientation);
        Matrix4 view = Matrix4.LookAt(target + offset, target, up);
        float aspect = (float)Bounds.Width / Math.Max(1f, (float)Bounds.Height);
        Matrix4 projection = CreateProjection(aspect);
        return view * projection;
    }

    /// <summary>The height, in the loaded scene's own world units, that <see cref="TryPickGround"/>
    /// picks on and overlays are best drawn at: the middle of the scene's height range.</summary>
    public float GroundHeight => sceneCenter.Y;

    /// <summary>Where a viewport point lands on the horizontal plane at <see cref="GroundHeight"/>, in
    /// the scene's world coordinates (the same X/Z a composite scene places each map's offset in).</summary>
    public bool TryPickGround(Point viewportPoint, out float worldX, out float worldZ)
    {
        worldX = worldZ = 0f;
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || sceneScale <= 0.0001f) return false;

        Matrix4 inverse = Matrix4.Invert(CurrentViewProjection());
        float ndcX = (float)(viewportPoint.X / Bounds.Width) * 2f - 1f;
        float ndcY = 1f - (float)(viewportPoint.Y / Bounds.Height) * 2f;
        Vector4 nearPoint = new Vector4(ndcX, ndcY, -1f, 1f) * inverse;
        Vector4 farPoint = new Vector4(ndcX, ndcY, 1f, 1f) * inverse;
        if (Math.Abs(nearPoint.W) < 1e-8f || Math.Abs(farPoint.W) < 1e-8f) return false;
        Vector3 rayOrigin = nearPoint.Xyz / nearPoint.W;
        Vector3 rayDir = farPoint.Xyz / farPoint.W - rayOrigin;
        // The renderer puts world height on its Z axis, centred on the scene, so the ground plane is Z = 0.
        if (Math.Abs(rayDir.Z) < 1e-8f) return false;
        float t = -rayOrigin.Z / rayDir.Z;
        if (t < 0f) return false;
        Vector3 hit = rayOrigin + rayDir * t;

        float extent = 4f / sceneScale;
        worldX = sceneCenter.X - hit.X * extent / 2f;
        worldZ = sceneCenter.Z + hit.Y * extent / 2f;
        return true;
    }

    /// <summary>Projects a point in the scene's world coordinates to this control's viewport.</summary>
    public bool TryProjectWorldPoint(float worldX, float worldY, float worldZ, out Point viewportPoint)
    {
        viewportPoint = default;
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || sceneScale <= 0.0001f) return false;

        float safeExtent = Math.Max(4f / sceneScale, 0.0001f);
        Vector4 render = new(
            -(worldX - sceneCenter.X) * 2f / safeExtent,
            (worldZ - sceneCenter.Z) * 2f / safeExtent,
            (worldY - sceneCenter.Y) * 2f / safeExtent,
            1f);
        Vector4 clip = render * CurrentViewProjection();
        if (clip.W <= 0.0001f) return false;

        viewportPoint = new Point(
            (clip.X / clip.W * 0.5 + 0.5) * Bounds.Width,
            (1.0 - (clip.Y / clip.W * 0.5 + 0.5)) * Bounds.Height);
        return true;
    }

    /// <summary>Projects a grid cell's center (col, row) to this control's local viewport coordinates -
    /// the forward counterpart to TryPickGridCell, using the exact same view/projection construction so
    /// results land exactly where TryPickGridCell would pick that same cell back up from. Used to
    /// position on-screen overlays (e.g. cell value labels) that need to track the locked overhead
    /// camera without duplicating its own separate projection math.</summary>
    public bool TryProjectGridCellCenter(int col, int row, out Point viewportPoint)
    {
        viewportPoint = default;
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || sceneScale <= 0.0001f) return false;

        Quaternion currentOrientation;
        lock (cameraLock) { currentOrientation = orientation; }
        Vector3 target = new(targetX, targetY, targetZ);
        Vector3 offset = Vector3.Transform(Vector3.UnitZ, currentOrientation) * (distance / 3f);
        Vector3 up = Vector3.Transform(Vector3.UnitY, currentOrientation);
        Vector3 camera = target + offset;
        Matrix4 view = Matrix4.LookAt(camera, target, up);
        float aspect = (float)Bounds.Width / Math.Max(1f, (float)Bounds.Height);
        Matrix4 projection = CreateProjection(aspect);
        Matrix4 viewProjection = view * projection;

        // Same world coordinate convention as the grid quads themselves (RebuildScene): index 0 is
        // offset by half the grid's total span, cell center is a half-cell further from that corner.
        float worldX = (col - GridSize / 2 + 0.5f) * UnitsPerGridCell;
        float worldZ = (row - GridSize / 2 + 0.5f) * UnitsPerGridCell;
        float worldY = sceneCenter.Y;

        float extent = 4f / sceneScale;
        float safeExtent = Math.Max(extent, 0.0001f);
        float renderX = -(worldX - sceneCenter.X) * 2f / safeExtent;
        float renderY = (worldZ - sceneCenter.Z) * 2f / safeExtent;
        float renderZ = (worldY - sceneCenter.Y) * 2f / safeExtent;

        Vector4 clip = new Vector4(renderX, renderY, renderZ, 1f) * viewProjection;
        if (clip.W <= 0.0001f) return false;

        float ndcX = clip.X / clip.W;
        float ndcY = clip.Y / clip.W;
        double x = (ndcX * 0.5 + 0.5) * Bounds.Width;
        double y = (1.0 - (ndcY * 0.5 + 0.5)) * Bounds.Height;
        viewportPoint = new Point(x, y);
        return true;
    }

    /// <summary>The number of addressable placed parts in whichever flat index space is currently
    /// active - composite mode's compositePartIndexMap, or single-map mode's cachedParts.</summary>
    private int ActivePartCount => compositeMaps.Count > 0 ? compositePartIndexMap.Count : cachedParts.Count;

    /// <summary>Resolves a flat active-part index (see ActivePartCount) to its placement spec, decoded
    /// model, and the extra world-space offset its own map contributes (zero in single-map mode) - the
    /// shared lookup TryPickBuildingPart and RebuildScene's part loop both build on so picking and
    /// rendering always agree on which building is where.</summary>
    private bool TryGetActivePart(int flatIndex, out ScenePartSpec part, out (List<RawBatch> Batches, Dictionary<int, TextureData> Textures) decoded, out Vector3 extraOffset)
    {
        part = default!;
        decoded = default;
        extraOffset = Vector3.Zero;

        if (compositeMaps.Count > 0)
        {
            if (flatIndex < 0 || flatIndex >= compositePartIndexMap.Count) return false;
            (int mapIndex, int localIndex) = compositePartIndexMap[flatIndex];
            CompositeMapScene scene = compositeMaps[mapIndex];
            part = scene.Parts[localIndex];
            extraOffset = scene.Offset;
            return scene.PartFiles.TryGetValue(part.ModelPath, out decoded);
        }

        if (flatIndex < 0 || flatIndex >= cachedParts.Count) return false;
        part = cachedParts[flatIndex];
        return cachedPartFiles.TryGetValue(part.ModelPath, out decoded);
    }

    /// <summary>Picks the closest placed building part (by the same index UpdatePartTransform/
    /// SetSelectedPart use) under a pointer position, via ray/bounding-sphere intersection - a bounding
    /// sphere per part (from that part's own decoded model's local extent, transformed by its placed
    /// position/rotation/scale) rather than exact per-triangle geometry, which is simple, cheap enough
    /// to compute on every click, and plenty precise for buildings that don't tightly overlap.</summary>
    /// <summary>Builds the same camera ray (in normalized render space) TryPickBuildingPart and
    /// TryPickEventMarker both intersect their own bounding spheres against - factored out so the two
    /// picking paths can never disagree about where a click actually points.</summary>
    private bool TryBuildPickingRay(Point viewportPoint, out Vector3 rayOrigin, out Vector3 rayDir, out float safeExtent)
    {
        rayOrigin = default;
        rayDir = default;
        safeExtent = 0.0001f;
        if (Bounds.Width <= 0 || Bounds.Height <= 0 || sceneScale <= 0.0001f) return false;

        Quaternion currentOrientation;
        lock (cameraLock) { currentOrientation = orientation; }
        Vector3 target = new(targetX, targetY, targetZ);
        Vector3 offset = Vector3.Transform(Vector3.UnitZ, currentOrientation) * (distance / 3f);
        Vector3 up = Vector3.Transform(Vector3.UnitY, currentOrientation);
        Vector3 camera = target + offset;
        Matrix4 view = Matrix4.LookAt(camera, target, up);
        float aspect = (float)Bounds.Width / Math.Max(1f, (float)Bounds.Height);
        Matrix4 projection = CreateProjection(aspect);
        Matrix4 viewProjection = view * projection;
        Matrix4 inverse = Matrix4.Invert(viewProjection);

        float ndcX = (float)(viewportPoint.X / Bounds.Width) * 2f - 1f;
        float ndcY = 1f - (float)(viewportPoint.Y / Bounds.Height) * 2f;
        Vector4 nearPoint = new Vector4(ndcX, ndcY, -1f, 1f) * inverse;
        Vector4 farPoint = new Vector4(ndcX, ndcY, 1f, 1f) * inverse;
        if (Math.Abs(nearPoint.W) < 1e-8f || Math.Abs(farPoint.W) < 1e-8f) return false;
        rayOrigin = nearPoint.Xyz / nearPoint.W;
        rayDir = Vector3.Normalize(farPoint.Xyz / farPoint.W - rayOrigin);
        safeExtent = Math.Max(4f / sceneScale, 0.0001f);
        return true;
    }

    /// <summary>Ray/bounding-sphere intersection against a single sphere already in normalized render
    /// space - the shared innermost test both TryPickBuildingPart and TryPickEventMarker reduce their
    /// own per-candidate math down to.</summary>
    private static bool TryIntersectSphere(Vector3 rayOrigin, Vector3 rayDir, Vector3 sphereCenter, float sphereRadius, out float t)
    {
        t = float.MaxValue;
        Vector3 oc = rayOrigin - sphereCenter;
        float b = Vector3.Dot(oc, rayDir);
        float c = Vector3.Dot(oc, oc) - sphereRadius * sphereRadius;
        float discriminant = b * b - c;
        if (discriminant < 0f) return false;
        float sqrtDiscriminant = MathF.Sqrt(discriminant);
        t = -b - sqrtDiscriminant;
        if (t < 0f) t = -b + sqrtDiscriminant;
        return t >= 0f;
    }

    public bool TryPickBuildingPart(Point viewportPoint, out int partIndex)
    {
        partIndex = -1;
        if (ActivePartCount == 0 || !TryBuildPickingRay(viewportPoint, out Vector3 rayOrigin, out Vector3 rayDir, out float safeExtent)) return false;

        // Bounding spheres only narrow the candidates: a door's sphere lies inside its building's, so
        // the actual triangles decide which part is under the pointer. Where two parts are hit at
        // (almost) the same depth - a door set flush into a wall - the smaller one wins.
        float closestT = float.MaxValue, closestRadius = float.MaxValue;
        int sphereOnlyIndex = -1;
        float sphereOnlyRadius = float.MaxValue;
        const float SameDepth = 0.004f;

        for (int i = 0; i < ActivePartCount; i++)
        {
            if (!TryGetActivePart(i, out ScenePartSpec part, out (List<RawBatch> Batches, Dictionary<int, TextureData> Textures) decoded, out Vector3 extraOffset))
            {
                continue;
            }

            (NumericsVector3 localCenter, float localRadius) = ComputeLocalBounds(decoded.Batches);
            if (localRadius <= 0f) continue;

            Quaternion rotation =
                Quaternion.FromAxisAngle(Vector3.UnitZ, MathHelper.DegreesToRadians(part.RotationDegrees.Z)) *
                Quaternion.FromAxisAngle(Vector3.UnitY, MathHelper.DegreesToRadians(part.RotationDegrees.Y)) *
                Quaternion.FromAxisAngle(Vector3.UnitX, MathHelper.DegreesToRadians(part.RotationDegrees.X));
            Vector3 ToRender(NumericsVector3 local)
            {
                Vector3 world = Vector3.Transform(new Vector3(local.X, local.Y, local.Z) * part.Scale, rotation) + part.Position + extraOffset;
                return new Vector3(
                    -(world.X - sceneCenter.X) * 2f / safeExtent,
                    (world.Z - sceneCenter.Z) * 2f / safeExtent,
                    (world.Y - sceneCenter.Y) * 2f / safeExtent);
            }

            float maxScale = Math.Max(part.Scale.X, Math.Max(part.Scale.Y, part.Scale.Z));
            float sphereRadius = localRadius * maxScale * 2f / safeExtent;
            if (!TryIntersectSphere(rayOrigin, rayDir, ToRender(localCenter), sphereRadius, out _)) continue;
            if (sphereRadius < sphereOnlyRadius)
            {
                sphereOnlyRadius = sphereRadius;
                sphereOnlyIndex = i;
            }

            float partT = float.MaxValue;
            foreach (RawBatch batch in decoded.Batches)
            {
                for (int k = 0; k + 2 < batch.Indices.Count; k += 3)
                {
                    if (TryIntersectTriangle(rayOrigin, rayDir,
                            ToRender(batch.Positions[(int)batch.Indices[k]]),
                            ToRender(batch.Positions[(int)batch.Indices[k + 1]]),
                            ToRender(batch.Positions[(int)batch.Indices[k + 2]]), out float t) && t < partT)
                    {
                        partT = t;
                    }
                }
            }

            if (partT == float.MaxValue) continue;
            bool sameDepth = Math.Abs(partT - closestT) <= SameDepth;
            if (sameDepth ? sphereRadius < closestRadius : partT < closestT)
            {
                closestT = Math.Min(partT, closestT);
                closestRadius = sphereRadius;
                partIndex = i;
            }
        }

        // Nothing's surface was hit (e.g. a click in a gap of an open model): fall back to the
        // smallest part whose bounds the pointer is within.
        if (partIndex < 0) partIndex = sphereOnlyIndex;
        return partIndex >= 0;
    }

    /// <summary>Möller-Trumbore ray/triangle intersection, both faces.</summary>
    private static bool TryIntersectTriangle(Vector3 origin, Vector3 dir, Vector3 a, Vector3 b, Vector3 c, out float t)
    {
        t = 0f;
        Vector3 edge1 = b - a, edge2 = c - a;
        Vector3 p = Vector3.Cross(dir, edge2);
        float det = Vector3.Dot(edge1, p);
        if (Math.Abs(det) < 1e-9f) return false;
        float inverse = 1f / det;
        Vector3 s = origin - a;
        float u = Vector3.Dot(s, p) * inverse;
        if (u < 0f || u > 1f) return false;
        Vector3 q = Vector3.Cross(s, edge1);
        float v = Vector3.Dot(dir, q) * inverse;
        if (v < 0f || u + v > 1f) return false;
        t = Vector3.Dot(edge2, q) * inverse;
        return t > 0f;
    }

    /// <summary>Picks the closest event marker under a pointer position - same ray/bounding-sphere
    /// approach as TryPickBuildingPart, but against each marker's own known fixed HalfSize instead of a
    /// decoded model's bounds, since a marker has no model to measure.</summary>
    public bool TryPickEventMarker(Point viewportPoint, out int eventIndex)
    {
        eventIndex = -1;
        if (!eventsVisible || eventMarkers.Count == 0 || !TryBuildPickingRay(viewportPoint, out Vector3 rayOrigin, out Vector3 rayDir, out float safeExtent)) return false;

        float closestT = float.MaxValue;
        for (int i = 0; i < eventMarkers.Count; i++)
        {
            EventMarkerSpec marker = eventMarkers[i];
            Vector3 sphereCenter = new(
                -(marker.WorldPosition.X - sceneCenter.X) * 2f / safeExtent,
                (marker.WorldPosition.Z - sceneCenter.Z) * 2f / safeExtent,
                (marker.WorldPosition.Y - sceneCenter.Y) * 2f / safeExtent);
            // A cube's half-diagonal (halfSize*sqrt(3)) so the pickable sphere fully encloses the
            // rendered cube's corners, matching how TryPickBuildingPart's sphere encloses its model.
            float sphereRadius = marker.HalfSize * 1.7320508f * 2f / safeExtent;

            if (!TryIntersectSphere(rayOrigin, rayDir, sphereCenter, sphereRadius, out float t) || t >= closestT) continue;

            closestT = t;
            eventIndex = i;
        }

        return eventIndex >= 0;
    }

    /// <summary>The center and radius of a bounding sphere enclosing every vertex in the given batches,
    /// in whatever local coordinate space those batches are already in.</summary>
    private static (NumericsVector3 Center, float Radius) ComputeLocalBounds(List<RawBatch> batches)
    {
        NumericsVector3 min = new(float.MaxValue, float.MaxValue, float.MaxValue);
        NumericsVector3 max = new(float.MinValue, float.MinValue, float.MinValue);
        bool any = false;
        foreach (RawBatch batch in batches)
        {
            foreach (NumericsVector3 position in batch.Positions)
            {
                min = NumericsVector3.Min(min, position);
                max = NumericsVector3.Max(max, position);
                any = true;
            }
        }

        if (!any) return (NumericsVector3.Zero, 0f);
        NumericsVector3 center = (min + max) * 0.5f;
        float radius = NumericsVector3.Distance(min, max) * 0.5f;
        return (center, radius);
    }

    /// <summary>Texture key event marker cubes are tagged with - negative, like the permission grid's
    /// own -100, so AddFile/the render loop treat them as solid-color geometry with nothing to bind
    /// (see RebuildScene's "if (cachedTerrain is null..." AddFile helper: negative keys never match
    /// anything in newTextures, so hasTexture comes back false at draw time). Distinct from the grid's
    /// -100 purely so the two are easy to tell apart while debugging, not because the renderer cares.</summary>
    private const int EventMarkerTextureKey = -101;
    private static readonly Dictionary<int, TextureData> EmptyTextureData = [];

    /// <summary>Builds a small axis-aligned solid-color cube (8 shared vertices, no normals needed since
    /// this renderer has no lighting) centered at a world position - the whole geometry for one event
    /// marker. No backface culling is enabled anywhere in this renderer, so winding order doesn't need
    /// to be exact for the cube to look right from any angle.</summary>
    private static RawBatch BuildEventMarkerBatch(NumericsVector3 center, NumericsVector4 colorRgba, float halfSize, float highlightMode)
    {
        var positions = new List<NumericsVector3>
        {
            center + new NumericsVector3(-halfSize, -halfSize, -halfSize),
            center + new NumericsVector3(halfSize, -halfSize, -halfSize),
            center + new NumericsVector3(halfSize, halfSize, -halfSize),
            center + new NumericsVector3(-halfSize, halfSize, -halfSize),
            center + new NumericsVector3(-halfSize, -halfSize, halfSize),
            center + new NumericsVector3(halfSize, -halfSize, halfSize),
            center + new NumericsVector3(halfSize, halfSize, halfSize),
            center + new NumericsVector3(-halfSize, halfSize, halfSize),
        };
        var texcoords = Enumerable.Repeat(NumericsVector2.Zero, 8).ToList();
        var colors = Enumerable.Repeat(colorRgba, 8).ToList();
        var indices = new List<uint>
        {
            0, 1, 2, 0, 2, 3, // -Z
            4, 6, 5, 4, 7, 6, // +Z
            0, 4, 5, 0, 5, 1, // -Y
            3, 2, 6, 3, 6, 7, // +Y
            0, 3, 7, 0, 7, 4, // -X
            1, 5, 6, 1, 6, 2, // +X
        };
        return new RawBatch(positions, texcoords, colors, indices, EventMarkerTextureKey, AlphaCutoff: -1f, HighlightMode: highlightMode);
    }

    /// <summary>Builds a flat, ground-plane quad (a "decal" lying in the XZ plane, facing straight up)
    /// textured with an event's icon - used instead of BuildEventMarkerBatch's solid cube whenever the
    /// marker's IconKey has a matching entry in eventIconTextures. A textured decal was chosen over a
    /// true camera-facing billboard because this renderer bakes vertex positions once per RebuildScene
    /// rather than per-frame (the camera orbits by recomputing the view matrix only, see Rotate/Pan/
    /// Zoom), so a billboard's orientation could only be kept accurate by re-baking every frame or by
    /// adding a dedicated billboard vertex attribute/shader path - a flat decal instead stays legible
    /// from directly overhead (matching the Permissions tab's locked top-down camera and legacy's own
    /// purely top-down 2D Event Editor) and from every azimuth angle, at the cost of foreshortening at
    /// shallow oblique angles. AlphaCutoff=0.5f makes the icon's transparent background actually
    /// transparent instead of drawing as an opaque square (see the fragment shader's MASK path).</summary>
    private static RawBatch BuildEventMarkerIconBatch(NumericsVector3 center, float halfSize, float highlightMode, float rotationDegrees)
    {
        // Rotation is around the world X axis (a horizontal axis lying in the quad's own plane), tilting
        // the decal from lying flat on the ground (0 degrees, the original XZ-plane quad) up onto the
        // vertical Y axis (90 degrees, standing upright like the in-game overworld sprite it depicts) -
        // "on" the vertical axis, not spinning around it. At 0 degrees this is the same
        // (x,0,-h)/(x,0,h) flat quad as before; increasing angle swings the +/-Z corners up into +/-Y.
        float radians = rotationDegrees * (MathF.PI / 180f);
        float cos = MathF.Cos(radians), sin = MathF.Sin(radians);
        NumericsVector3 Rotate(float x, float z) => center + new NumericsVector3(x, -z * sin, z * cos);
        var positions = new List<NumericsVector3>
        {
            Rotate(-halfSize, -halfSize),
            Rotate(halfSize, -halfSize),
            Rotate(halfSize, halfSize),
            Rotate(-halfSize, halfSize),
        };
        // V is flipped (1-V) relative to the "naive" mapping - confirmed empirically (icons rendered
        // upside down without this) rather than from the documented V=0-is-top convention other
        // textures in this renderer follow, so this decal's own winding must disagree with that
        // somewhere. If icons ever look upside down again, this is the first place to flip back.
        var texcoords = new List<NumericsVector2>
        {
            new(1f, 0f),
            new(0f, 0f),
            new(0f, 1f),
            new(1f, 1f),
        };
        var colors = Enumerable.Repeat(NumericsVector4.One, 4).ToList();
        var indices = new List<uint> { 0, 1, 2, 0, 2, 3 };
        return new RawBatch(positions, texcoords, colors, indices, TextureKey: 0, AlphaCutoff: 0.5f, HighlightMode: highlightMode);
    }

    /// <summary>Rotates (Z, then Y, then X), scales, and translates a decoded model's raw batches by
    /// part's placement - the same order LegacyMap3DRenderer.ScaleTranslateRotateBuilding uses - then
    /// adds a further flat world-space extraOffset on top (zero in single-map mode; a composite map's
    /// own matrix-derived position in composite mode, applied after the part's own local placement
    /// since it shifts the whole already-placed building, not its local geometry).</summary>
    private static List<RawBatch> PlacePart(List<RawBatch> batches, ScenePartSpec part, float highlightMode, Vector3 extraOffset)
    {
        Quaternion rotation =
            Quaternion.FromAxisAngle(Vector3.UnitZ, MathHelper.DegreesToRadians(part.RotationDegrees.Z)) *
            Quaternion.FromAxisAngle(Vector3.UnitY, MathHelper.DegreesToRadians(part.RotationDegrees.Y)) *
            Quaternion.FromAxisAngle(Vector3.UnitX, MathHelper.DegreesToRadians(part.RotationDegrees.X));

        return batches.Select(batch => batch with
        {
            Positions = batch.Positions.Select(p =>
            {
                Vector3 local = new(p.X, p.Y, p.Z);
                Vector3 world = Vector3.Transform(local * part.Scale, rotation) + part.Position + extraOffset;
                return new NumericsVector3(world.X, world.Y, world.Z);
            }).ToList(),
            HighlightMode = highlightMode
        }).ToList();
    }

    private void RebuildScene()
    {
        if (cachedTerrain is null && compositeMaps.Count == 0)
        {
            return;
        }

        // Build the entire new scene into locals first. Nothing here touches the batches/textures
        // fields, so a render frame in flight on the render thread keeps drawing the old scene
        // undisturbed until the finished result is published below.
        var newTextures = new Dictionary<int, TextureData>();
        var combinedBatches = new List<RawBatch>();
        var allPositions = new List<NumericsVector3>();
        // Only terrain/building positions drive the auto-fit center/extent below - event markers are
        // an overlay, not scene content, and must never affect it. They used to be pooled into the same
        // allPositions list the fit was computed from, so simply floating a marker higher (or an
        // overworld sprite icon's flat quad replacing a taller solid-cube fallback) shifted the whole
        // scene's Y extent and made EVERYTHING visibly jump/rescale on every marker-only change - e.g.
        // every edit (RefreshEventMarkers) or background overworld-sprite resolution (RegisterEventIcon)
        // rebuilding the scene, which read as buildings/terrain "flashing" even though nothing about
        // them changed.
        var fitPositions = new List<NumericsVector3>();
        int textureKeyOffset = 0;

        void AddFile(List<RawBatch> rawBatches, Dictionary<int, TextureData> fileTextures, bool includeInFit = true)
        {
            // Texture keys are local to each converted file (they restart at zero every time), so
            // files sharing one scene need their key ranges shifted apart before merging.
            // A material whose image is missing still carries its key, so the range has to cover the
            // batches' keys too, or the next file's texture would land on it.
            int offset = textureKeyOffset;
            int highestKey = Math.Max(fileTextures.Count > 0 ? fileTextures.Keys.Max() : -1, rawBatches.Count > 0 ? rawBatches.Max(batch => batch.TextureKey) : -1);
            textureKeyOffset += highestKey + 1;
            foreach ((int key, TextureData data) in fileTextures) newTextures[key + offset] = data;
            foreach (RawBatch batch in rawBatches)
            {
                allPositions.AddRange(batch.Positions);
                if (includeInFit) fitPositions.AddRange(batch.Positions);
                combinedBatches.Add(batch.TextureKey < 0 ? batch : batch with { TextureKey = batch.TextureKey + offset });
            }
        }

        if (cachedTerrain is not null)
        {
            AddFile(cachedTerrain.Value.Batches, cachedTerrain.Value.Textures);
        }

        foreach (CompositeMapScene scene in compositeMaps)
        {
            NumericsVector3 mapOffset = new(scene.Offset.X, scene.Offset.Y, scene.Offset.Z);
            List<RawBatch> offsetTerrainBatches = scene.TerrainBatches
                .Select(batch => batch with { Positions = batch.Positions.Select(p => p + mapOffset).ToList() })
                .ToList();
            AddFile(offsetTerrainBatches, scene.TerrainTextures);
        }

        int placedPartCount = 0;
        var skinSpecs = new List<SkinSpec>();
        List<RawBatch> selectedPartBatches = [];
        for (int partIndex = 0; partIndex < ActivePartCount; partIndex++)
        {
            if (!TryGetActivePart(partIndex, out ScenePartSpec part, out (List<RawBatch> Batches, Dictionary<int, TextureData> Textures) decoded, out Vector3 extraOffset))
            {
                continue;
            }

            placedPartCount++;
            if (!partsVisible) continue;
            float highlightMode = dirtyPartIndices.Contains(partIndex) ? 2f : partIndex == selectedPartIndex ? 1f : 0f;
            // The same building model can be placed multiple times with different transforms, so the
            // raw decode is cached per file but each instance gets its own freshly-placed copy.
            List<RawBatch> placed = AssignBuildingSkin(AssignBuildingAnimation(PlacePart(decoded.Batches, part, highlightMode, extraOffset), part.ModelPath), part, extraOffset, skinSpecs);
            if (partIndex == selectedPartIndex) selectedPartBatches = placed;
            AddFile(placed, decoded.Textures);
        }

        if (eventsVisible)
        {
            for (int eventIndex = 0; eventIndex < eventMarkers.Count; eventIndex++)
            {
                EventMarkerSpec marker = eventMarkers[eventIndex];
                float highlightMode = dirtyEventIndices.Contains(eventIndex) ? 2f : eventIndex == selectedEventIndex ? 1f : 0f;
                NumericsVector3 markerCenter = new(marker.WorldPosition.X, marker.WorldPosition.Y, marker.WorldPosition.Z);
                if (eventIconTextures.TryGetValue(marker.IconKey, out TextureData? iconTexture))
                {
                    AddFile([BuildEventMarkerIconBatch(markerCenter, marker.HalfSize, highlightMode, marker.RotationDegrees)], new Dictionary<int, TextureData> { [0] = iconTexture }, includeInFit: false);
                }
                else
                {
                    NumericsVector4 markerColor = new(marker.ColorRgb.X, marker.ColorRgb.Y, marker.ColorRgb.Z, 1f);
                    AddFile([BuildEventMarkerBatch(markerCenter, markerColor, marker.HalfSize, highlightMode)], EmptyTextureData, includeInFit: false);
                }
            }
        }

        if (allPositions.Count == 0)
        {
            throw new InvalidDataException("The converted map model contains no renderable triangles.");
        }

        // fitPositions excludes event markers (see AddFile's includeInFit) - fall back to allPositions
        // only for the degenerate case of a scene made up entirely of markers with no terrain/buildings.
        List<NumericsVector3> fitSource = fitPositions.Count > 0 ? fitPositions : allPositions;
        NumericsVector3 min = fitSource.Aggregate((a, b) => NumericsVector3.Min(a, b));
        NumericsVector3 max = fitSource.Aggregate((a, b) => NumericsVector3.Max(a, b));
        NumericsVector3 center;
        float extent;
        if (overheadLocked)
        {
            // Normalizing against the terrain's own bounding box (the else branch below) scales
            // differently per map, because terrain meshes routinely extend past the permission grid's
            // boundary by a varying amount (border/connection-tile padding - see GridWorldExtent's
            // comment) - normalizing the whole scene against that instead of the terrain's own extent
            // would have made the grid appear a different size relative to the terrain on every map,
            // since the two were never meant to occupy the same footprint to begin with. The grid's
            // own size is fixed and known, so it - not the terrain - is the correct scale reference
            // while it's the thing on screen the camera is locked to show.
            center = new NumericsVector3(0f, (min.Y + max.Y) * 0.5f, 0f);
            extent = GridWorldExtent;
        }
        else
        {
            center = (min + max) * 0.5f;
            extent = Math.Max(Math.Max(max.X - min.X, max.Y - min.Y), max.Z - min.Z);
        }
        Vector3 newSceneCenter = new(center.X, center.Y, center.Z);
        float newSceneScale = extent > 0.0001f ? 4f / extent : 1f;
        // Start with a top-down normalized pass: DS maps lie primarily in X/Z,
        // and this avoids hiding valid geometry behind camera convention differences.
        // Swapping Y and Z to put Z (depth) on the up axis is a single-axis-pair transposition,
        // which flips handedness - visually a mirror image, not a rotation. Negating X here restores
        // proper handedness (determinant back to +1) without touching which axis renders as "up".
        // Each animated building's placement as a matrix into the finished scene's coordinates, found by sending its
        // origin and axes through the same steps its vertices take below.
        float skinInverseExtent = 2f / Math.Max(extent, 0.0001f);
        NumericsVector3 Scene(NumericsVector3 p) => new(-(p.X - center.X) * skinInverseExtent, (p.Z - center.Z) * skinInverseExtent, (p.Y - center.Y) * skinInverseExtent);
        var newSkinInstances = new List<SkinInstance>();
        foreach (SkinSpec spec in skinSpecs)
        {
            NumericsVector3 origin = Scene(spec.Probe[0]);
            NumericsVector3 x = Scene(spec.Probe[1]) - origin, y = Scene(spec.Probe[2]) - origin, z = Scene(spec.Probe[3]) - origin;
            var placement = new NumericsMatrix(x.X, x.Y, x.Z, 0, y.X, y.Y, y.Z, 0, z.X, z.Y, z.Z, 0, origin.X, origin.Y, origin.Z, 1);
            newSkinInstances.Add(NumericsMatrix.Invert(placement, out NumericsMatrix inverse)
                ? new SkinInstance(spec.Skeleton, spec.Animation, placement, inverse)
                : new SkinInstance(spec.Skeleton, new JointAnimation { Name = string.Empty, FrameCount = 1, Joints = [] }, NumericsMatrix.Identity, NumericsMatrix.Identity));
        }

        lock (animationLock) skinInstances = newSkinInstances.ToArray();

        var newBatches = new List<RenderBatch>();
        int newIndexCount = 0;
        foreach (RawBatch raw in combinedBatches)
        {
            // Filled directly rather than through LINQ: this runs for every vertex on every rebuild,
            // and the scene is rebuilt several times while a map loads (markers, selection, icons).
            float inverseExtent = 2f / Math.Max(extent, 0.0001f);
            float[] batchVertices = new float[raw.Positions.Count * VertexFloatStride];
            for (int index = 0, offset = 0; index < raw.Positions.Count; index++, offset += VertexFloatStride)
            {
                NumericsVector3 position = raw.Positions[index];
                batchVertices[offset] = -(position.X - center.X) * inverseExtent;
                batchVertices[offset + 1] = (position.Z - center.Z) * inverseExtent;
                batchVertices[offset + 2] = (position.Y - center.Y) * inverseExtent;
                // glTF's UV origin is the image's top-left and StbImageSharp decodes rows top-down, so
                // V is used as-is (a 1-V flip here sampled every texture upside down).
                batchVertices[offset + 3] = raw.Texcoords[index].X;
                batchVertices[offset + 4] = raw.Texcoords[index].Y;
                batchVertices[offset + 5] = raw.Colors[index].X;
                batchVertices[offset + 6] = raw.Colors[index].Y;
                batchVertices[offset + 7] = raw.Colors[index].Z;
                batchVertices[offset + 8] = raw.Colors[index].W;
                batchVertices[offset + 9] = raw.AlphaCutoff;
                // The texture slot rides in the highlight value (mode + 10 * (slot + 1)); the vertex shader splits them again.
                // A skinned building's joint rides there too, in the thousands, as 10000 * (skeleton slot + 1).
                float skinSlot = raw.Joints is not null && raw.SkinInstance >= 0 ? 10000f * (raw.SkinInstance * MaxSkinJoints + Math.Min(raw.Joints[index], MaxSkinJoints - 1) + 1) : 0f;
                batchVertices[offset + 10] = raw.HighlightMode + (raw.TextureSlot is >= 0 and < MaxAnimatedSlots * MaxAnimationGroups ? 10f * (raw.TextureSlot + 1) : 0f) + skinSlot;
            }
            newBatches.Add(new RenderBatch(batchVertices, raw.Indices.ToArray(), raw.TextureKey));
            newIndexCount += raw.Indices.Count;
        }

        RenderBatch? newSelectionWireframe = BuildWireframe(selectedPartBatches, center, 2f / Math.Max(extent, 0.0001f));

        RenderBatch? newTileGridLines = null;
        if (tileGridVisible)
        {
            // Same cells and render transform as the permission grid below; drawn with depth testing
            // off, so the lines show over hills and buildings alike.
            float safeExtent = Math.Max(extent, 0.0001f);
            var lineVertices = new List<float>();
            var lineIndices = new List<uint>();
            float half = GridSize / 2f * UnitsPerGridCell;
            void AddLine(float worldX0, float worldZ0, float worldX1, float worldZ1)
            {
                uint baseIndex = (uint)(lineVertices.Count / VertexFloatStride);
                foreach ((float x, float z) in new[] { (worldX0, worldZ0), (worldX1, worldZ1) })
                {
                    lineVertices.AddRange([-(x - center.X) * 2f / safeExtent, (z - center.Z) * 2f / safeExtent, 0f, 0f, 0f, 1f, 1f, 1f, 0.45f, -2f, 0f]);
                }
                lineIndices.AddRange([baseIndex, baseIndex + 1]);
            }
            for (int i = 0; i <= GridSize; i++)
            {
                float offset = i * UnitsPerGridCell - half;
                AddLine(offset, -half, offset, half);
                AddLine(-half, offset, half, offset);
            }
            newTileGridLines = new RenderBatch(lineVertices.ToArray(), lineIndices.ToArray(), -100);
        }

        RenderBatch? newGridBatch = permissionGridColors is { } gridColors ? BuildPermissionGridBatch(gridColors, center, extent) : null;
        // One draw call per texture: merging everything into a single batch would
        // discard the per-material texture assignment.
        if (newBatches.Count > 1)
        {
            var merged = new List<RenderBatch>();
            foreach (IGrouping<int, RenderBatch> group in newBatches.GroupBy(batch => batch.TextureKey))
            {
                var combinedVertices = new List<float>();
                var combinedIndices = new List<uint>();
                foreach (RenderBatch batch in group)
                {
                    uint baseVertex = (uint)(combinedVertices.Count / VertexFloatStride);
                    combinedVertices.AddRange(batch.Vertices);
                    combinedIndices.AddRange(batch.Indices.Select(index => index + baseVertex));
                }

                merged.Add(new RenderBatch(combinedVertices.ToArray(), combinedIndices.ToArray(), group.Key));
            }

            newBatches = merged;
            newIndexCount = merged.Sum(batch => batch.Indices.Length);
        }
        // Semi-transparent surfaces (shadows, water) are drawn after everything opaque and without
        // writing depth, so whatever lies beneath them has already been drawn and still shows through
        // regardless of the order the model lists its materials in.
        var textureAlpha = new Dictionary<int, (bool Partial, bool Clear)>();
        RenderBatch[] newBatchArray = newBatches
            .Select(batch => batch with
            {
                Translucent = IsTranslucent(batch, newTextures, textureAlpha),
                Cutout = newTextures.ContainsKey(batch.TextureKey) && TextureAlpha(batch.TextureKey, newTextures, textureAlpha).Clear
            })
            .OrderBy(batch => batch.Translucent)
            .ToArray();
        ValidateBatches(newBatchArray);

        // Publish the finished scene atomically. RenderFrame only ever reads batches/textures/
        // indexCount as a matched snapshot taken under the same lock, so it never sees a mix of
        // old and new data.
        Dictionary<string, int[]> newMaterialKeys = combinedBatches
            .Where(batch => batch.TextureKey >= 0 && !string.IsNullOrEmpty(batch.MaterialName))
            .GroupBy(batch => MaterialBaseName.Replace(batch.MaterialName!, string.Empty), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.Select(batch => batch.TextureKey).Distinct().ToArray(), StringComparer.OrdinalIgnoreCase);
        lock (sceneLock)
        {
            materialTextureKeys = newMaterialKeys;
            batches = newBatchArray;
            textures = newTextures;
            indexCount = newIndexCount;
            sceneCenter = newSceneCenter;
            sceneScale = newSceneScale;
            gridBatch = newGridBatch;
            selectionWireframe = newSelectionWireframe;
            tileGridLines = newTileGridLines;
        }
        TriangleCount = newIndexCount / 3;
        PartCount = placedPartCount;
        renderFailed = false;
        RequestNextFrameRendering();
    }

    private RenderBatch? BuildPermissionGridBatch(GridCellColor[,] gridColors, NumericsVector3 center, float extent)
    {
        RenderBatch? newGridBatch = null;
            // Which side of the terrain counts as "closer to the camera" flips with camera
            // orientation, so rather than chase that sign per camera mode, the grid is drawn in its
            // own pass (see RenderFrame) with depth testing off - it always ends up on top regardless
            // of where it sits relative to the terrain. It still needs *a* Z coordinate to satisfy the
            // vertex format; the terrain's own mid-height keeps it a reasonable, bounded value.
            float safeExtent = Math.Max(extent, 0.0001f);
            // Placed at the scene's own mid-height (render-Z 0 by definition, since center is the
            // normalization origin) - depth testing is off for this pass (see below), so the exact
            // height doesn't affect visibility, only satisfies the vertex format.
            const float gridRenderZ = 0f;
            gridPlaneRenderZ = gridRenderZ;

            var gridVertices = new List<float>();
            var gridIndices = new List<uint>();
            for (int row = 0; row < GridSize; row++)
            {
                for (int col = 0; col < GridSize; col++)
                {
                    GridCellColor cellColor = gridColors[row, col];
                    if (cellColor.A == 0) continue;

                    // The 32x32 grid's array indices are unsigned (0-31), but the terrain/building
                    // coordinate space they must line up with is signed and centered near the map's
                    // middle (BuildingRecord.XPosition/ZPosition are signed - see MapBuildingService),
                    // so index 0 is not world 0; it's offset by half the grid's total span.
                    float worldX0 = (col - GridSize / 2) * UnitsPerGridCell;
                    float worldX1 = worldX0 + UnitsPerGridCell;
                    float worldZ0 = (row - GridSize / 2) * UnitsPerGridCell;
                    float worldZ1 = worldZ0 + UnitsPerGridCell;

                    float renderX0 = -(worldX0 - center.X) * 2f / safeExtent;
                    float renderX1 = -(worldX1 - center.X) * 2f / safeExtent;
                    float renderY0 = (worldZ0 - center.Z) * 2f / safeExtent;
                    float renderY1 = (worldZ1 - center.Z) * 2f / safeExtent;

                    float r = cellColor.R / 255f, g = cellColor.G / 255f, b = cellColor.B / 255f, a = cellColor.A / 255f;
                    uint baseIndex = (uint)(gridVertices.Count / VertexFloatStride);
                    void AddVertex(float x, float y) => gridVertices.AddRange([x, y, gridRenderZ, 0f, 0f, r, g, b, a, -2f, 0f]);
                    AddVertex(renderX0, renderY0);
                    AddVertex(renderX1, renderY0);
                    AddVertex(renderX1, renderY1);
                    AddVertex(renderX0, renderY1);
                    gridIndices.AddRange([baseIndex, baseIndex + 1, baseIndex + 2, baseIndex, baseIndex + 2, baseIndex + 3]);
                }
            }

            if (gridVertices.Count > 0)
            {
                newGridBatch = new RenderBatch(gridVertices.ToArray(), gridIndices.ToArray(), -100);
            }

        return newGridBatch;
    }

    private static readonly NumericsVector4 WireframeColor = new(1f, 0.85f, 0.2f, 1f);

    /// <summary>Line segments along every triangle edge of the given (already placed) batches, in render
    /// space. Shared edges are emitted once.</summary>
    private static RenderBatch? BuildWireframe(List<RawBatch> rawBatches, NumericsVector3 center, float inverseExtent)
    {
        var vertices = new List<float>();
        var indices = new List<uint>();
        foreach (RawBatch raw in rawBatches)
        {
            uint baseIndex = (uint)(vertices.Count / VertexFloatStride);
            foreach (NumericsVector3 position in raw.Positions)
            {
                // Same normalization as RebuildScene's vertices; vCutoff -2 draws the vertex colour as is.
                vertices.AddRange([
                    -(position.X - center.X) * inverseExtent, (position.Z - center.Z) * inverseExtent, (position.Y - center.Y) * inverseExtent,
                    0f, 0f, WireframeColor.X, WireframeColor.Y, WireframeColor.Z, WireframeColor.W, -2f, 0f]);
            }

            var edges = new HashSet<(uint, uint)>();
            for (int k = 0; k + 2 < raw.Indices.Count; k += 3)
            {
                foreach ((uint a, uint b) in new[] { (raw.Indices[k], raw.Indices[k + 1]), (raw.Indices[k + 1], raw.Indices[k + 2]), (raw.Indices[k + 2], raw.Indices[k]) })
                {
                    if (!edges.Add((Math.Min(a, b), Math.Max(a, b)))) continue;
                    indices.Add(baseIndex + a);
                    indices.Add(baseIndex + b);
                }
            }
        }

        return indices.Count == 0 ? null : new RenderBatch(vertices.ToArray(), indices.ToArray(), -101);
    }

    /// <summary>An index past the vertex array makes the driver read unmapped memory, which kills the process.</summary>
    private static void ValidateBatches(RenderBatch[] batches)
    {
        foreach (RenderBatch batch in batches)
        {
            int vertexCount = batch.Vertices.Length / VertexFloatStride;
            if (batch.Indices.Length % 3 != 0)
            {
                throw new InvalidDataException($"The converted map model has {batch.Indices.Length} indices, which is not a whole number of triangles.");
            }

            foreach (uint index in batch.Indices)
            {
                if (index >= vertexCount)
                {
                    throw new InvalidDataException(
                        $"The converted map model references vertex {index} but only {vertexCount} vertices were decoded.");
                }
            }
        }
    }

    protected override void OnOpenGlInit(GlInterface gl)
    {
        try
        {
            // OpenGL object names belong to one context. Avalonia may recreate
            // the context when the window is reattached, so never reuse them.
            gpuTextures.Clear();
            uploadedTextures = null;
            batchVertexBuffers = [];
            batchIndexBuffers = [];
            uploadedBatches = null;
            GL.LoadBindings(new AvaloniaBindingsContext(gl));
            shaderProgram = CreateShaderProgram();
            vertexArray = GL.GenVertexArray(); vertexBuffer = GL.GenBuffer(); indexBuffer = GL.GenBuffer();
            bindingsLoaded = true;
        }
        catch (Exception ex)
        {
            RenderError?.Invoke($"OpenGL initialization failed: {ex.Message}");
        }
    }

    // Animations are drawn at most this often. Their data is stepped 60 times a second, but scrolling water and a
    // turning sign read fine at half that, and asking for a frame the moment the last one is done would
    // keep the GPU and a CPU core busy for nothing.
    private const double AnimationFramesPerSecond = 15;
    private readonly Stopwatch animationFrameClock = Stopwatch.StartNew();
    private bool animationFramePending;

    private void ScheduleNextAnimationFrame()
    {
        TimeSpan interval = TimeSpan.FromSeconds(1 / AnimationFramesPerSecond);
        TimeSpan elapsed = animationFrameClock.Elapsed;
        animationFrameClock.Restart();
        if (elapsed >= interval)
        {
            RequestNextFrameRendering();
            return;
        }

        // Rendering finished early: wait out the rest of the interval before asking again.
        if (animationFramePending) return;
        animationFramePending = true;
        TimeSpan wait = interval - elapsed;
        Dispatcher.UIThread.Post(() => DispatcherTimer.RunOnce(() =>
        {
            animationFramePending = false;
            RequestNextFrameRendering();
        }, wait));
    }

    protected override void OnOpenGlRender(GlInterface gl, int fb)
    {
        if (!bindingsLoaded || renderFailed) return;
        try
        {
            RenderFrame(fb);
            if (animationRunning) ScheduleNextAnimationFrame();
        }
        catch (Exception ex)
        {
            renderFailed = true;
            RenderError?.Invoke($"OpenGL render failed: {ex.Message}");
        }
    }

    // A one-shot callback set by CaptureFrame and consumed by the next RenderFrame call, used to grab
    // a static screenshot of the just-rendered frame (via GL.ReadPixels) for the Permissions tab's
    // 2D grid overlay background - the same "screenshot the 3D view, then paint a flat grid on top of
    // it" approach legacy uses, chosen over drawing the grid as 3D world-space geometry so the overlay
    // is guaranteed pixel-aligned with whatever the camera actually rendered rather than depending on
    // separately-derived world-to-screen math staying in sync with it.
    private Action<WriteableBitmap?>? pendingCapture;

    /// <summary>Requests a screenshot of the next rendered frame. The callback runs on the UI thread.
    /// Typically called right after SetOverheadLocked(true) so the captured frame matches the locked
    /// camera position.</summary>
    public void CaptureFrame(Action<WriteableBitmap?> onCaptured)
    {
        pendingCapture = onCaptured;
        RequestNextFrameRendering();
    }

    private void CaptureIfRequested()
    {
        if (pendingCapture is not { } capture) return;
        pendingCapture = null;

        int width = FramebufferSize.Width;
        int height = FramebufferSize.Height;
        try
        {
            byte[] pixels = new byte[width * height * 4];
            GL.ReadPixels(0, 0, width, height, OpenTK.Graphics.OpenGL4.PixelFormat.Rgba, PixelType.UnsignedByte, pixels);

            var bitmap = new WriteableBitmap(
                new PixelSize(width, height),
                new Vector(96, 96),
                global::Avalonia.Platform.PixelFormat.Rgba8888,
                global::Avalonia.Platform.AlphaFormat.Opaque);
            using (global::Avalonia.Platform.ILockedFramebuffer locked = bitmap.Lock())
            {
                int rowBytes = width * 4;
                for (int row = 0; row < height; row++)
                {
                    // GL's row 0 is the bottom of the image; bitmap row 0 is the top.
                    int sourceRow = height - 1 - row;
                    Marshal.Copy(pixels, sourceRow * rowBytes, IntPtr.Add(locked.Address, row * locked.RowBytes), rowBytes);
                }
            }

            Dispatcher.UIThread.Post(() => capture(bitmap));
        }
        catch (Exception)
        {
            Dispatcher.UIThread.Post(() => capture(null));
        }
    }

    /// <summary>The GL framebuffer is in device pixels; Bounds is in layout units, which differ under
    /// display scaling.</summary>
    private PixelSize FramebufferSize
    {
        get
        {
            double scaling = TopLevel.GetTopLevel(this)?.RenderScaling ?? 1.0;
            return new PixelSize(Math.Max(1, (int)Math.Round(Bounds.Width * scaling)), Math.Max(1, (int)Math.Round(Bounds.Height * scaling)));
        }
    }

    private void RenderFrame(int fb)
    {
        // Snapshot the scene published by LoadGlb once, under the lock, so every GL call below
        // this point draws a single self-consistent scene even if the UI thread is mid-LoadGlb
        // for the next texture pack right now.
        RenderBatch[] localBatches;
        Dictionary<int, TextureData> localTextures;
        int localIndexCount;
        RenderBatch? localGridBatch;
        RenderBatch? localWireframe;
        RenderBatch? localTileGrid;
        lock (sceneLock)
        {
            localTileGrid = tileGridLines;
            localBatches = batches;
            localTextures = textures;
            localIndexCount = indexCount;
            localGridBatch = gridBatch;
            localWireframe = selectionWireframe;
        }

        GL.BindFramebuffer(FramebufferTarget.Framebuffer, fb);
        // The context can be shared with Avalonia's own drawing, which leaves its state behind: a
        // scissor rectangle from redrawing one checkbox clipped the next frame's clear and draws to
        // that rectangle, so most of the view kept showing an older frame (buildings missing, one
        // half drawn). Everything a frame relies on is set here instead of assumed.
        GL.Disable(EnableCap.ScissorTest);
        GL.Disable(EnableCap.StencilTest);
        GL.Disable(EnableCap.CullFace);
        GL.Disable(EnableCap.PolygonOffsetFill);
        GL.ColorMask(true, true, true, true);
        GL.DepthMask(true);
        GL.DepthRange(0.0, 1.0);
        PixelSize framebufferSize = FramebufferSize;
        GL.Viewport(0, 0, framebufferSize.Width, framebufferSize.Height);
        GL.Enable(EnableCap.DepthTest); GL.ClearColor(0.20f, 0.20f, 0.20f, 1f);
        GL.Enable(EnableCap.Blend);
        GL.BlendFunc(BlendingFactor.SrcAlpha, BlendingFactor.OneMinusSrcAlpha);
        GL.DepthFunc(DepthFunction.Lequal);
        GL.PolygonOffset(-1f, -2f);
        GL.Clear(ClearBufferMask.ColorBufferBit | ClearBufferMask.DepthBufferBit);
        if (localBatches.Length == 0 || localIndexCount == 0) { RenderScene?.Invoke(); CaptureIfRequested(); return; }

        // Texture keys restart at zero for every model, so cached uploads cannot be reused.
        if (!ReferenceEquals(localTextures, uploadedTextures))
        {
            foreach (int stale in gpuTextures.Values) GL.DeleteTexture(stale);
            gpuTextures.Clear();
            uploadedTextures = localTextures;
        }

        // Clear anything the previous frame or another control left pending so it is
        // not misattributed to this draw.
        while (GL.GetError() != ErrorCode.NoError) { }

        GL.BindVertexArray(vertexArray);
        GL.UseProgram(shaderProgram);
        // Only the denominator needs a floor (avoids a div-by-zero/blowup at near-zero height); the
        // ratio itself must NOT be floored to 1 or every portrait-shaped (taller-than-wide) viewport
        // would render with a forced-square aspect, stretching the model to fill the actual wider-
        // or-narrower viewport it's then drawn into.
        float aspect = (float)Bounds.Width / Math.Max(1f, (float)Bounds.Height);
        Quaternion currentOrientation;
        lock (cameraLock) { currentOrientation = orientation; }
        Vector3 target = new(targetX, targetY, targetZ);
        // Both the offset and "up" are derived from the same rotation, so they stay orthogonal and
        // rotate together continuously - there is no pole where LookAt could degenerate or where
        // "up" would need to jump to a different reference, unlike a fixed-world-up yaw/pitch camera.
        Vector3 offset = Vector3.Transform(Vector3.UnitZ, currentOrientation) * (distance / 3f);
        Vector3 up = Vector3.Transform(Vector3.UnitY, currentOrientation);
        Vector3 camera = target + offset;
        Matrix4 model = Matrix4.Identity;
        Matrix4 view = Matrix4.LookAt(camera, target, up);
        // The scene is normalized to roughly a 2-unit span (see the *2/extent normalization in
        // LoadSceneCore) and the camera orbits between ~0.1 and ~33 units from it (Zoom clamps
        // distance to [0.3,100], camera offset is distance/3). A near/far of 0.01/100 - a 10,000:1
        // ratio - wastes almost the entire depth buffer's precision far closer than anything ever
        // gets drawn, leaving razor-thin precision where the geometry actually sits; that shows up as
        // z-fighting (surfaces flickering over which is "in front") that visibly shifts as the camera
        // moves, which is exactly what panning/zooming and seeing the artifacts change was reporting.
        Matrix4 projection = CreateProjection(aspect);
        GL.UniformMatrix4(GL.GetUniformLocation(shaderProgram, "uModel"), false, ref model);
        GL.UniformMatrix4(GL.GetUniformLocation(shaderProgram, "uView"), false, ref view);
        GL.UniformMatrix4(GL.GetUniformLocation(shaderProgram, "uProjection"), false, ref projection);
        animationRunning = UpdateAnimationOffsets() | UpdateBoneMatrices();
        IReadOnlyList<FrameSet> localFrames;
        double frameTicks;
        lock (animationLock)
        {
            localFrames = frameAnimations;
            frameTicks = AnimationTicksLocked();
        }

        if (!ReferenceEquals(localFrames, uploadedFrameSets))
        {
            foreach (int stale in gpuFrameTextures.Values) GL.DeleteTexture(stale);
            gpuFrameTextures.Clear();
            uploadedFrameSets = localFrames;
        }

        // Which picture of which animation stands in for each animated texture right now.
        Dictionary<int, (int Set, int Frame)>? animatedKeys = null;
        if (localFrames.Count > 0)
        {
            animatedKeys = [];
            lock (sceneLock)
            {
                for (int set = 0; set < localFrames.Count; set++)
                {
                    if (!materialTextureKeys.TryGetValue(localFrames[set].Target, out int[]? keys)) continue;
                    double tick = frameTicks % localFrames[set].TotalTicks;
                    int frame = 0;
                    for (; frame < localFrames[set].Frames.Count - 1 && tick >= localFrames[set].Frames[frame].Ticks; frame++) tick -= localFrames[set].Frames[frame].Ticks;
                    foreach (int key in keys) animatedKeys[key] = (set, frame);
                }
            }
        }

        GL.UniformMatrix4(GL.GetUniformLocation(shaderProgram, "uBones"), MaxSkinInstances * MaxSkinJoints, false, boneMatrices);
        GL.Uniform2(GL.GetUniformLocation(shaderProgram, "uUvOffsets"), MaxAnimatedSlots * MaxAnimationGroups, animationOffsets);

        bool uploaded = UploadBatchesIfChanged(localBatches);
        for (int batchIndex = 0; batchIndex < localBatches.Length; batchIndex++)
        {
            RenderBatch batch = localBatches[batchIndex];
            GL.BindBuffer(BufferTarget.ArrayBuffer, batchVertexBuffers[batchIndex]);
            GL.BindBuffer(BufferTarget.ElementArrayBuffer, batchIndexBuffers[batchIndex]);

            // Attribute pointers must be declared while the vertex buffer is bound,
            // otherwise the offsets are treated as client pointers and the driver faults.
            int stride = VertexFloatStride * sizeof(float);
            GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0); GL.EnableVertexAttribArray(0);
            GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float)); GL.EnableVertexAttribArray(1);
            GL.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, 5 * sizeof(float)); GL.EnableVertexAttribArray(2);
            GL.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, 9 * sizeof(float)); GL.EnableVertexAttribArray(3);
            GL.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, stride, 10 * sizeof(float)); GL.EnableVertexAttribArray(4);

            TextureData? texture = null;
            bool hasTexture = localTextures.TryGetValue(batch.TextureKey, out texture);
            if (hasTexture)
            {
                if (!gpuTextures.TryGetValue(batch.TextureKey, out int gpuTexture))
                {
                    gpuTexture = UploadTexture(texture!);
                    uploaded = true;
                    gpuTextures[batch.TextureKey] = gpuTexture;
                }
                if (animatedKeys is not null && animatedKeys.TryGetValue(batch.TextureKey, out (int Set, int Frame) shown))
                {
                    if (!gpuFrameTextures.TryGetValue(shown, out int frameTexture))
                    {
                        frameTexture = UploadTexture(localFrames[shown.Set].Frames[shown.Frame].Texture with { WrapS = texture!.WrapS, WrapT = texture.WrapT });
                        gpuFrameTextures[shown] = frameTexture;
                    }

                    gpuTexture = frameTexture;
                }

                GL.ActiveTexture(TextureUnit.Texture0);
                GL.BindTexture(TextureTarget.Texture2D, gpuTexture);
                GL.Uniform1(GL.GetUniformLocation(shaderProgram, "uTexture"), 0);
            }

            GL.Uniform1(GL.GetUniformLocation(shaderProgram, "uHasTexture"), hasTexture ? 1 : 0);
            // A material whose texture the chosen pack lacks is drawn white on the DS (just its vertex
            // colours), so only a scene drawn with no textures at all ("Untextured") keeps the grey
            // that makes its shapes readable. Event marker cubes (negative keys) keep their tint.
            bool missingTexture = !hasTexture && batch.TextureKey >= 0 && localTextures.Count > 0;
            GL.Uniform3(GL.GetUniformLocation(shaderProgram, "uColor"), missingTexture ? Vector3.One : UntexturedColor);
            GL.DepthMask(!batch.Translucent);
            // Cut-out textures (ledges, path edges, flowers) lie exactly on the ground they decorate.
            // Nudging them towards the camera lets them win that tie consistently instead of the two
            // surfaces z-fighting, which flickers as the camera moves.
            if (batch.Cutout) GL.Enable(EnableCap.PolygonOffsetFill); else GL.Disable(EnableCap.PolygonOffsetFill);
            GL.DrawElements(OpenTK.Graphics.OpenGL4.PrimitiveType.Triangles, batch.Indices.Length, DrawElementsType.UnsignedInt, 0);
        }

        GL.DepthMask(true);
        GL.Disable(EnableCap.PolygonOffsetFill);

        if (localTileGrid is { } tileGrid)
        {
            GL.Disable(EnableCap.DepthTest);
            DrawOverlayBatch(tileGrid, OpenTK.Graphics.OpenGL4.PrimitiveType.Lines);
            GL.Enable(EnableCap.DepthTest);
        }

        if (localWireframe is { } wireframe)
        {
            GL.Disable(EnableCap.DepthTest);
            DrawOverlayBatch(wireframe, OpenTK.Graphics.OpenGL4.PrimitiveType.Lines);
            GL.Enable(EnableCap.DepthTest);
        }

        if (localGridBatch is { } grid)
        {
            // Drawn last with depth testing off, so the permission-grid overlay always renders on top
            // of the terrain/buildings regardless of camera orientation - see the comment on
            // permissionGridColors in RebuildScene for why this is simpler than picking a world
            // position guaranteed to win the depth test from every camera angle.
            GL.Disable(EnableCap.DepthTest);
            DrawOverlayBatch(grid, OpenTK.Graphics.OpenGL4.PrimitiveType.Triangles);
            GL.Enable(EnableCap.DepthTest);
        }
        ErrorCode error = GL.GetError();
        if (error != ErrorCode.NoError) RenderError?.Invoke($"OpenGL render error: {error}");
        GL.BindVertexArray(0);
        // The frame is handed to the compositor as soon as this returns, and some drivers (NVIDIA)
        // let it be shown before the GPU has finished drawing it. A frame that just uploaded the
        // whole scene takes long enough that buildings showed up missing or half drawn - and stayed
        // that way until the camera moved, because nothing asked for another frame. Waiting for the
        // GPU here, and following an upload with one more plain frame, keeps every shown frame whole.
        GL.Finish();
        CaptureIfRequested();
        if (uploaded) RequestNextFrameRendering();
    }

    /// <summary>Draws a small untextured batch that changes with the scene (the permission grid, the
    /// selection wireframe) through the shared streaming buffers instead of the scene's own.</summary>
    private void DrawOverlayBatch(RenderBatch batch, OpenTK.Graphics.OpenGL4.PrimitiveType primitive)
    {
        GL.BindBuffer(BufferTarget.ArrayBuffer, vertexBuffer);
        GL.BufferData(BufferTarget.ArrayBuffer, batch.Vertices.Length * sizeof(float), batch.Vertices, BufferUsageHint.StreamDraw);
        GL.BindBuffer(BufferTarget.ElementArrayBuffer, indexBuffer);
        GL.BufferData(BufferTarget.ElementArrayBuffer, batch.Indices.Length * sizeof(uint), batch.Indices, BufferUsageHint.StreamDraw);

        int stride = VertexFloatStride * sizeof(float);
        GL.VertexAttribPointer(0, 3, VertexAttribPointerType.Float, false, stride, 0); GL.EnableVertexAttribArray(0);
        GL.VertexAttribPointer(1, 2, VertexAttribPointerType.Float, false, stride, 3 * sizeof(float)); GL.EnableVertexAttribArray(1);
        GL.VertexAttribPointer(2, 4, VertexAttribPointerType.Float, false, stride, 5 * sizeof(float)); GL.EnableVertexAttribArray(2);
        GL.VertexAttribPointer(3, 1, VertexAttribPointerType.Float, false, stride, 9 * sizeof(float)); GL.EnableVertexAttribArray(3);
        GL.VertexAttribPointer(4, 1, VertexAttribPointerType.Float, false, stride, 10 * sizeof(float)); GL.EnableVertexAttribArray(4);

        GL.Uniform1(GL.GetUniformLocation(shaderProgram, "uHasTexture"), 0);
        GL.DrawElements(primitive, batch.Indices.Length, DrawElementsType.UnsignedInt, 0);
    }

    /// <summary>The array overload of TexImage2D faults on some Linux drivers, so pin the pixels and pass the address.</summary>
    private static int UploadTexture(TextureData texture)
    {
        int gpuTexture = GL.GenTexture();
        GL.BindTexture(TextureTarget.Texture2D, gpuTexture);
        GL.PixelStore(PixelStoreParameter.UnpackAlignment, 1);
        // Plain nearest filtering on both axes, no mipmaps - matches the legacy WinForms renderer
        // (NSBMDGlRenderer: GL_NEAREST/GL_NEAREST, no glGenerateMipmap) exactly. A NearestMipmapNearest
        // minification filter was tried here to fix distant shimmer, but box-filtered mip averaging is
        // fatal to these particular textures: small repeat-tiled ground tiles (ngrass.png etc.) collapse
        // into a single averaged blob color once a coarse mip is sampled, and MASK-alpha cutout sprites
        // (trees, building window/shadow decals) have their sharp opaque/transparent edges blurred into
        // mid-range alpha that then falls below the cutoff almost everywhere - discarding the whole
        // sprite. That is worse than the shimmer it was meant to fix, so plain nearest wins here.
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMinFilter, (int)TextureMinFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureMagFilter, (int)TextureMagFilter.Nearest);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapS, texture.WrapS);
        GL.TexParameter(TextureTarget.Texture2D, TextureParameterName.TextureWrapT, texture.WrapT);

        GCHandle handle = GCHandle.Alloc(texture.Pixels, GCHandleType.Pinned);
        try
        {
            GL.TexImage2D(
                TextureTarget.Texture2D, 0, PixelInternalFormat.Rgba,
                texture.Width, texture.Height, 0,
                PixelFormat.Rgba, PixelType.UnsignedByte,
                handle.AddrOfPinnedObject());
        }
        finally
        {
            handle.Free();
        }

        return gpuTexture;
    }

    private static int ToGlWrapMode(GltfWrapMode? mode) => mode switch
    {
        GltfWrapMode.CLAMP_TO_EDGE => (int)GlWrapMode.ClampToEdge,
        GltfWrapMode.MIRRORED_REPEAT => (int)GlWrapMode.MirroredRepeat,
        _ => (int)GlWrapMode.Repeat
    };

    protected override void OnOpenGlDeinit(GlInterface gl)
    {
        if (!bindingsLoaded) return;
        foreach (int texture in gpuTextures.Values) GL.DeleteTexture(texture);
        gpuTextures.Clear();
        foreach (int texture in gpuFrameTextures.Values) GL.DeleteTexture(texture);
        gpuFrameTextures.Clear();
        uploadedTextures = null;
        DeleteBatchBuffers();
        GL.DeleteBuffer(vertexBuffer); GL.DeleteBuffer(indexBuffer); GL.DeleteVertexArray(vertexArray); GL.DeleteProgram(shaderProgram); bindingsLoaded = false;
    }

    private static int CreateShaderProgram()
    {
        string shadingLanguage = GL.GetString(StringName.ShadingLanguageVersion) ?? string.Empty;
        bool es = shadingLanguage.Contains("ES", StringComparison.OrdinalIgnoreCase);
        string version = es ? "#version 300 es\nprecision mediump float;\n" : "#version 330 core\n";
        // The vertex shader decodes slot numbers in the thousands out of one float, which needs full precision.
        string vertexVersion = es ? "#version 300 es\nprecision highp float;\n" : version;
        int vertex = CompileShader(ShaderType.VertexShader, vertexVersion +
            "layout(location=0) in vec3 aPosition; layout(location=1) in vec2 aTexCoord; layout(location=2) in vec4 aColor; layout(location=3) in float aCutoff; layout(location=4) in float aHighlight;" +
            "out vec2 vTexCoord; out vec4 vColor; out float vCutoff; out float vHighlight;" +
            "uniform mat4 uModel; uniform mat4 uView; uniform mat4 uProjection; uniform vec2 uUvOffsets[128]; uniform mat4 uBones[64];" +
            "void main(){" +
            // The highlight value also carries a texture slot (tens) and a skeleton slot (ten-thousands).
            "  float skinStep=floor(aHighlight/10000.0+0.001);" +
            "  float rest=aHighlight-10000.0*skinStep;" +
            "  float slotStep=floor(rest/10.0+0.001);" +
            "  vec2 shift=vec2(0.0); if(slotStep>=1.0){ shift=uUvOffsets[int(slotStep+0.5)-1]; }" +
            "  vec4 pos=vec4(aPosition,1.0); if(skinStep>=1.0){ pos=uBones[int(skinStep+0.5)-1]*pos; }" +
            "  vTexCoord=aTexCoord+shift; vColor=aColor; vCutoff=aCutoff; vHighlight=rest-10.0*slotStep; gl_Position=uProjection*uView*uModel*pos;} ");
        int fragment = CompileShader(ShaderType.FragmentShader, version +
            "in vec2 vTexCoord; in vec4 vColor; in float vCutoff; in float vHighlight; out vec4 FragColor; uniform vec3 uColor; uniform sampler2D uTexture; uniform int uHasTexture;" +
            "void main(){" +
            "  if(uHasTexture==1){" +
            "    vec4 t=texture(uTexture,vTexCoord) * vColor;" +
            // MASK (vCutoff in [0,1]): discard below the cutoff, then treat surviving pixels as fully
            // opaque per the glTF spec. OPAQUE/BLEND (vCutoff<0): use the sampled alpha as-is rather
            // than forcing it to 1 - apicula's OPAQUE/BLEND classification describes its own texture
            // data, not necessarily the DS-format-correct pixels DSPRE.ScriptTool's legacy decoder
            // regenerates over top of it, which use a transparent (alpha=0) placeholder for texels the
            // DS format marks as not drawn. Forcing alpha=1 on an "OPAQUE" material was overriding that
            // placeholder to fully visible, showing its throwaway RGB (opaque red) as solid blotches -
            // worse zoomed in simply because each texel covers more screen pixels.
            "    if(vCutoff>=0.0){ if(t.a<vCutoff) discard; t.a=1.0; }" +
            // Fully transparent texels (the gaps around a ledge or fence, the DS "not drawn" colour)
            // must not write depth either, or they hide whatever is drawn behind them afterwards and
            // the background shows through instead.
            "    else if(t.a<0.02) discard;" +
            "    FragColor=t;" +
            // Used for both plain untextured model geometry (forced fully opaque, as before) and the
            // permission-grid overlay's flat colored quads (which rely on vColor.a for their
            // semi-transparent look, matching legacy's painter colors) - vCutoff<-1.5 flags the latter.
            "  } else if (vCutoff < -1.5) { FragColor=vColor; }" +
            "  else { FragColor=vec4(uColor,1.0)*vec4(vColor.rgb,1.0); }" +
            // The building editor's currently-selected placed part gets blended toward white so it
            // reads as highlighted in the 3D view, matching its highlighted row in the sidebar list.
            // A part with unsaved edits gets blended toward blue instead, and that takes priority over
            // the selection tint (a dirty part stays visibly blue even after selecting a different one,
            // so "you still have unsaved changes here" doesn't disappear the moment focus moves on).
            "  if(vHighlight>1.5){ FragColor.rgb=mix(FragColor.rgb,vec3(0.25,0.55,1.0),0.55); }" +
            "  else if(vHighlight>0.5){ FragColor.rgb=mix(FragColor.rgb,vec3(1.0),0.55); }" +
            "} ");
        int program = GL.CreateProgram(); GL.AttachShader(program, vertex); GL.AttachShader(program, fragment); GL.LinkProgram(program);
        GL.GetProgram(program, GetProgramParameterName.LinkStatus, out int linked);
        if (linked == 0) throw new InvalidOperationException($"Shader link failed: {GL.GetProgramInfoLog(program)}");
        GL.DeleteShader(vertex); GL.DeleteShader(fragment); return program;
    }

    private static int CompileShader(ShaderType type, string source)
    {
        int shader = GL.CreateShader(type); GL.ShaderSource(shader, source); GL.CompileShader(shader);
        GL.GetShader(shader, ShaderParameter.CompileStatus, out int compiled);
        if (compiled == 0) throw new InvalidOperationException($"{type} compile failed: {GL.GetShaderInfoLog(shader)}");
        return shader;
    }

    private sealed class AvaloniaBindingsContext : IBindingsContext
    {
        private readonly GlInterface gl;
        public AvaloniaBindingsContext(GlInterface gl) => this.gl = gl;
        public IntPtr GetProcAddress(string procName) => gl.GetProcAddress(procName);
    }
}
