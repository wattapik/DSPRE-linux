namespace DSPRE.Avalonia;

/// <summary>Undo and redo of painting in the Map Editor's Permissions tab. One stroke (press, drag, release), a flood fill or
/// a Clear Grid is one step, ordered against the inputs' history (MainWindow.UiUndo) by time. The mode radio buttons and the
/// paint value only change what is shown or painted, so they are not steps.</summary>
public partial class MainWindow
{
    private sealed record PermissionState(int MapId, byte[,] Types, byte[,] Collisions, DateTime At);

    private const int PermissionHistoryLimit = 200;
    private readonly List<PermissionState> permissionUndo = [];
    private readonly List<PermissionState> permissionRedo = [];
    private PermissionState? permissionSnapshot;
    private bool permissionUndoneLast;

    private DateTime LatestPermissionEdit => permissionUndo.Count > 0 ? permissionUndo[^1].At : DateTime.MinValue;

    private PermissionState CapturePermissions() => new(permissionsMapId, (byte[,])currentPermissionTypes.Clone(), (byte[,])currentPermissionCollisions.Clone(), DateTime.UtcNow);

    /// <summary>A press: remembers the grids as they are, kept as an undo step only if the stroke changes a cell.</summary>
    private void BeginPermissionEdit() => permissionSnapshot = permissionsMapId < 0 ? null : CapturePermissions();

    private void CommitPermissionEdit()
    {
        if (permissionSnapshot is null) return;
        permissionUndo.Add(permissionSnapshot with { At = DateTime.UtcNow });
        if (permissionUndo.Count > PermissionHistoryLimit) permissionUndo.RemoveAt(0);
        permissionSnapshot = null;
        permissionRedo.Clear();
        permissionUndoneLast = false;
        UpdateUndoButtons();
    }

    private void ClearPermissionHistory()
    {
        permissionUndo.Clear();
        permissionRedo.Clear();
        permissionSnapshot = null;
        permissionUndoneLast = false;
        UpdateUndoButtons();
    }

    private bool StepPermissionHistory(bool redo, out string description)
    {
        description = string.Empty;
        List<PermissionState> from = redo ? permissionRedo : permissionUndo, to = redo ? permissionUndo : permissionRedo;
        if (from.Count == 0 || from[^1].MapId != permissionsMapId) return false;
        if (redo ? !(permissionUndoneLast || uiRedo.Count == 0) : uiUndo.Count > 0 && uiUndo[^1].At > LatestPermissionEdit) return false;

        PermissionState target = from[^1];
        from.RemoveAt(from.Count - 1);
        to.Add(CapturePermissions());
        Array.Copy(target.Types, currentPermissionTypes, target.Types.Length);
        Array.Copy(target.Collisions, currentPermissionCollisions, target.Collisions.Length);
        permissionUndoneLast = !redo;
        RefreshPermissionGridColors();
        description = $"the move permissions of map {permissionsMapId:D4} in the Map Editor";
        return true;
    }
}
