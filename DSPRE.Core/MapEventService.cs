namespace DSPRE.Core;

/// <summary>Which of the four event kinds a placed event is - mirrors legacy's
/// DS_Map/ROMFiles/EventFile.cs Event.EventType enum exactly (same member order, since it is also the
/// on-disk serialization order within one map's event file).</summary>
public enum EventKind
{
    Spawnable,
    Overworld,
    Warp,
    Trigger
}

/// <summary>Fields every event kind shares - byte-for-byte equivalent of legacy's abstract Event base
/// class. Position is in whole 32x32-grid-cell units (0-31), the same grid MapPermissionsService uses;
/// unlike BuildingRecord there is no fractional part, since events always snap to a whole cell. On disk
/// X/Y are packed as one flat value (mapPosition + gridSize*matrixPosition) - MatrixPositionX/Y is that
/// packed value's high part, letting one event file's coordinates span a whole matrix instead of just
/// one 32x32 map, though in practice these are always 0 for a placed event on the map it belongs to.</summary>
public abstract class MapEvent
{
    public abstract EventKind Kind { get; }
    public short MapPositionX { get; set; }
    public short MapPositionY { get; set; }
    public ushort MatrixPositionX { get; set; }
    public ushort MatrixPositionY { get; set; }
}

/// <summary>Misc/board/hidden-item interactable - byte-for-byte equivalent of legacy's Spawnable class
/// (0x14/20 bytes on disk).</summary>
public sealed class SpawnableEvent : MapEvent
{
    public const ushort TypeMisc = 0;
    public const ushort TypeBoard = 1;
    public const ushort TypeHiddenItem = 2;

    public override EventKind Kind => EventKind.Spawnable;
    public ushort ScriptNumber { get; set; }
    public ushort Type { get; set; }
    public ushort Unknown2 { get; set; }
    /// <summary>16.16 fixed-point grid-cell height - divide by 65536 for the same "grid cell" unit
    /// BuildingRecord.YPosition uses (see legacy EventEditor.cs's zPosition read/write shifts).</summary>
    public int ZPosition { get; set; }
    public ushort Unknown4 { get; set; }
    public ushort Direction { get; set; }
    public ushort Unknown5 { get; set; }

    public SpawnableEvent Clone() => new()
    {
        ScriptNumber = ScriptNumber, Type = Type, Unknown2 = Unknown2, ZPosition = ZPosition,
        Unknown4 = Unknown4, Direction = Direction, Unknown5 = Unknown5,
        MapPositionX = MapPositionX, MapPositionY = MapPositionY,
        MatrixPositionX = MatrixPositionX, MatrixPositionY = MatrixPositionY
    };
}

/// <summary>An NPC/overworld sprite - byte-for-byte equivalent of legacy's Overworld class (0x20/32
/// bytes on disk).</summary>
public sealed class OverworldEvent : MapEvent
{
    public const ushort TypeNormal = 0;
    public const ushort TypeTrainer = 1;
    public const ushort TypeItem = 3;

    public override EventKind Kind => EventKind.Overworld;
    public ushort OverworldId { get; set; }
    public ushort OverlayTableEntry { get; set; } = 1;
    public ushort Movement { get; set; }
    public ushort Type { get; set; }
    public ushort Flag { get; set; }
    public ushort ScriptNumber { get; set; }
    public ushort Orientation { get; set; } = 1;
    public ushort SightRange { get; set; }
    public ushort Unknown1 { get; set; }
    public ushort Unknown2 { get; set; }
    public ushort XRange { get; set; }
    public ushort YRange { get; set; }
    /// <summary>16.16 fixed-point grid-cell height - see SpawnableEvent.ZPosition's comment.</summary>
    public int ZPosition { get; set; }

    /// <summary>A scriptNumber of 0xFFFF means this overworld is an alias of another one rather than
    /// carrying its own script - matches legacy's Overworld.isAlias().</summary>
    public bool IsAlias => ScriptNumber == 0xFFFF;

    public OverworldEvent Clone() => new()
    {
        OverworldId = OverworldId, OverlayTableEntry = OverlayTableEntry, Movement = Movement, Type = Type,
        Flag = Flag, ScriptNumber = ScriptNumber, Orientation = Orientation, SightRange = SightRange,
        Unknown1 = Unknown1, Unknown2 = Unknown2, XRange = XRange, YRange = YRange, ZPosition = ZPosition,
        MapPositionX = MapPositionX, MapPositionY = MapPositionY,
        MatrixPositionX = MatrixPositionX, MatrixPositionY = MatrixPositionY
    };
}

/// <summary>A map transition point - byte-for-byte equivalent of legacy's Warp class (0xC/12 bytes on
/// disk). Pure teleport: no script, just a destination header + warp/hook index within it.</summary>
public sealed class WarpEvent : MapEvent
{
    public override EventKind Kind => EventKind.Warp;
    public ushort DestinationHeaderId { get; set; }
    public ushort DestinationWarpIndex { get; set; }
    public uint Height { get; set; }

    public WarpEvent Clone() => new()
    {
        DestinationHeaderId = DestinationHeaderId, DestinationWarpIndex = DestinationWarpIndex, Height = Height,
        MapPositionX = MapPositionX, MapPositionY = MapPositionY,
        MatrixPositionX = MatrixPositionX, MatrixPositionY = MatrixPositionY
    };
}

/// <summary>A script trigger covering a rectangular footprint of cells - byte-for-byte equivalent of
/// legacy's Trigger class (0x10/16 bytes on disk). Unlike every other event kind, ZPosition here is a
/// plain ushort in grid-cell units already - no 16.16 fixed-point scaling (legacy's own EventEditor.cs
/// reads/writes it with no shift, unlike Spawnable/Overworld's zPosition).</summary>
public sealed class TriggerEvent : MapEvent
{
    public override EventKind Kind => EventKind.Trigger;
    public ushort ScriptNumber { get; set; }
    public ushort WidthX { get; set; } = 1;
    public ushort HeightY { get; set; } = 1;
    public ushort ZPosition { get; set; }
    public ushort ExpectedVarValue { get; set; }
    public ushort VariableWatched { get; set; }

    public TriggerEvent Clone() => new()
    {
        ScriptNumber = ScriptNumber, WidthX = WidthX, HeightY = HeightY, ZPosition = ZPosition,
        ExpectedVarValue = ExpectedVarValue, VariableWatched = VariableWatched,
        MapPositionX = MapPositionX, MapPositionY = MapPositionY,
        MatrixPositionX = MatrixPositionX, MatrixPositionY = MatrixPositionY
    };
}

/// <summary>Every event placed on one map, in the same four-list shape legacy's EventFile stores -
/// on disk this is a single flat file, four length-prefixed lists back to back in this exact order.</summary>
public sealed class MapEventFile
{
    public int Id { get; set; } = -1;
    public List<SpawnableEvent> Spawnables { get; set; } = [];
    public List<OverworldEvent> Overworlds { get; set; } = [];
    public List<WarpEvent> Warps { get; set; } = [];
    public List<TriggerEvent> Triggers { get; set; } = [];

    public bool IsEmpty => Spawnables.Count == 0 && Overworlds.Count == 0 && Warps.Count == 0 && Triggers.Count == 0;
}

/// <summary>Loads/saves per-map event files (NPCs, warps, triggers, spawnables) - see MapEventFile's
/// comment for the on-disk shape, and RomExtractionService.PrepareEvents for where these files come
/// from (not extracted by default, same "extract on first use" precedent as building models).</summary>
public static class MapEventService
{
    private const int GridSize = 32; // matches legacy's MapFile.mapSize / MapPermissionsService.GridSize

    public static string GetPath(RomProject project, int eventFileId) => Path.Combine(project.EventsDirectory, eventFileId.ToString("D4"));

    public static MapEventFile Load(RomProject project, int eventFileId)
    {
        string path = GetPath(project, eventFileId);
        if (!File.Exists(path)) return new MapEventFile { Id = eventFileId };
        return Parse(File.ReadAllBytes(path), eventFileId);
    }

    /// <summary>An event file's bytes - from the project or an exported .evt file.</summary>
    public static MapEventFile Parse(byte[] data, int eventFileId)
    {
        var file = new MapEventFile { Id = eventFileId };
        using MemoryStream stream = new(data);
        using BinaryReader reader = new(stream);

        uint spawnableCount = reader.ReadUInt32();
        for (int i = 0; i < spawnableCount; i++) file.Spawnables.Add(ReadSpawnable(reader));

        uint overworldCount = reader.ReadUInt32();
        for (int i = 0; i < overworldCount; i++) file.Overworlds.Add(ReadOverworld(reader));

        uint warpCount = reader.ReadUInt32();
        for (int i = 0; i < warpCount; i++) file.Warps.Add(ReadWarp(reader));

        uint triggerCount = reader.ReadUInt32();
        for (int i = 0; i < triggerCount; i++) file.Triggers.Add(ReadTrigger(reader));

        return file;
    }

    public static void Save(RomProject project, MapEventFile file) =>
        EditJournal.Track(project, EditJournalDocuments.MapEvent, EditJournalDocuments.Keys(("eventFile", file.Id)), () => SaveUntracked(project, file));

    private static void SaveUntracked(RomProject project, MapEventFile file)
    {
        Directory.CreateDirectory(project.EventsDirectory);
        File.WriteAllBytes(GetPath(project, file.Id), Serialize(file));
    }

    /// <summary>The event file as the game stores it (and as legacy exports it to a .evt file).</summary>
    public static byte[] Serialize(MapEventFile file)
    {
        using MemoryStream stream = new();
        using BinaryWriter writer = new(stream);

        writer.Write((uint)file.Spawnables.Count);
        foreach (SpawnableEvent ev in file.Spawnables) WriteSpawnable(writer, ev);

        writer.Write((uint)file.Overworlds.Count);
        foreach (OverworldEvent ev in file.Overworlds) WriteOverworld(writer, ev);

        writer.Write((uint)file.Warps.Count);
        foreach (WarpEvent ev in file.Warps) WriteWarp(writer, ev);

        writer.Write((uint)file.Triggers.Count);
        foreach (TriggerEvent ev in file.Triggers) WriteTrigger(writer, ev);
        writer.Flush();
        return stream.ToArray();
    }

    private static (short MapPos, ushort MatrixPos) DecomposePosition(short packed)
    {
        int value = packed;
        return ((short)(value % GridSize), (ushort)(value / GridSize));
    }

    private static short ComposePosition(short mapPosition, ushort matrixPosition) => (short)(mapPosition + GridSize * matrixPosition);

    private static SpawnableEvent ReadSpawnable(BinaryReader reader)
    {
        var ev = new SpawnableEvent { ScriptNumber = reader.ReadUInt16(), Type = reader.ReadUInt16() };
        (ev.MapPositionX, ev.MatrixPositionX) = DecomposePosition(reader.ReadInt16());
        ev.Unknown2 = reader.ReadUInt16();
        (ev.MapPositionY, ev.MatrixPositionY) = DecomposePosition(reader.ReadInt16());
        ev.ZPosition = reader.ReadInt32();
        ev.Unknown4 = reader.ReadUInt16();
        ev.Direction = reader.ReadUInt16();
        ev.Unknown5 = reader.ReadUInt16();
        return ev;
    }

    private static void WriteSpawnable(BinaryWriter writer, SpawnableEvent ev)
    {
        writer.Write(ev.ScriptNumber);
        writer.Write(ev.Type);
        writer.Write(ComposePosition(ev.MapPositionX, ev.MatrixPositionX));
        writer.Write(ev.Unknown2);
        writer.Write(ComposePosition(ev.MapPositionY, ev.MatrixPositionY));
        writer.Write(ev.ZPosition);
        writer.Write(ev.Unknown4);
        writer.Write(ev.Direction);
        writer.Write(ev.Unknown5);
    }

    private static OverworldEvent ReadOverworld(BinaryReader reader)
    {
        var ev = new OverworldEvent
        {
            OverworldId = reader.ReadUInt16(),
            OverlayTableEntry = reader.ReadUInt16(),
            Movement = reader.ReadUInt16(),
            Type = reader.ReadUInt16(),
            Flag = reader.ReadUInt16(),
            ScriptNumber = reader.ReadUInt16(),
            Orientation = reader.ReadUInt16(),
            SightRange = reader.ReadUInt16(),
            Unknown1 = reader.ReadUInt16(),
            Unknown2 = reader.ReadUInt16(),
            XRange = reader.ReadUInt16(),
            YRange = reader.ReadUInt16()
        };
        (ev.MapPositionX, ev.MatrixPositionX) = DecomposePosition(reader.ReadInt16());
        (ev.MapPositionY, ev.MatrixPositionY) = DecomposePosition(reader.ReadInt16());
        ev.ZPosition = reader.ReadInt32();
        return ev;
    }

    private static void WriteOverworld(BinaryWriter writer, OverworldEvent ev)
    {
        writer.Write(ev.OverworldId);
        writer.Write(ev.OverlayTableEntry);
        writer.Write(ev.Movement);
        writer.Write(ev.Type);
        writer.Write(ev.Flag);
        writer.Write(ev.ScriptNumber);
        writer.Write(ev.Orientation);
        writer.Write(ev.SightRange);
        writer.Write(ev.Unknown1);
        writer.Write(ev.Unknown2);
        writer.Write(ev.XRange);
        writer.Write(ev.YRange);
        writer.Write(ComposePosition(ev.MapPositionX, ev.MatrixPositionX));
        writer.Write(ComposePosition(ev.MapPositionY, ev.MatrixPositionY));
        writer.Write(ev.ZPosition);
    }

    private static WarpEvent ReadWarp(BinaryReader reader)
    {
        var ev = new WarpEvent();
        (ev.MapPositionX, ev.MatrixPositionX) = DecomposePosition(reader.ReadInt16());
        (ev.MapPositionY, ev.MatrixPositionY) = DecomposePosition(reader.ReadInt16());
        ev.DestinationHeaderId = reader.ReadUInt16();
        ev.DestinationWarpIndex = reader.ReadUInt16();
        ev.Height = reader.ReadUInt32();
        return ev;
    }

    private static void WriteWarp(BinaryWriter writer, WarpEvent ev)
    {
        writer.Write(ComposePosition(ev.MapPositionX, ev.MatrixPositionX));
        writer.Write(ComposePosition(ev.MapPositionY, ev.MatrixPositionY));
        writer.Write(ev.DestinationHeaderId);
        writer.Write(ev.DestinationWarpIndex);
        writer.Write(ev.Height);
    }

    private static TriggerEvent ReadTrigger(BinaryReader reader)
    {
        var ev = new TriggerEvent { ScriptNumber = reader.ReadUInt16() };
        (ev.MapPositionX, ev.MatrixPositionX) = DecomposePosition(reader.ReadInt16());
        (ev.MapPositionY, ev.MatrixPositionY) = DecomposePosition(reader.ReadInt16());
        ev.WidthX = reader.ReadUInt16();
        ev.HeightY = reader.ReadUInt16();
        ev.ZPosition = reader.ReadUInt16();
        ev.ExpectedVarValue = reader.ReadUInt16();
        ev.VariableWatched = reader.ReadUInt16();
        return ev;
    }

    private static void WriteTrigger(BinaryWriter writer, TriggerEvent ev)
    {
        writer.Write(ev.ScriptNumber);
        writer.Write(ComposePosition(ev.MapPositionX, ev.MatrixPositionX));
        writer.Write(ComposePosition(ev.MapPositionY, ev.MatrixPositionY));
        writer.Write(ev.WidthX);
        writer.Write(ev.HeightY);
        writer.Write(ev.ZPosition);
        writer.Write(ev.ExpectedVarValue);
        writer.Write(ev.VariableWatched);
    }

    /// <summary>Maps each map file to the header-linked event file that places events on it, walking
    /// every header's own matrix exactly like AreaDataService.ResolveMapAreaData does for AreaData IDs -
    /// a header's EventFileId is the event-file counterpart to its AreaDataId/ScriptFileId, and events
    /// are keyed by that ID, not directly by map ID, so this indirection is unavoidable.</summary>
    public static IReadOnlyDictionary<int, int> ResolveMapEventFileIds(
        RomProject project,
        Func<ushort, MapHeaderRecord> getHeader,
        int headerCount)
    {
        var result = new Dictionary<int, int>();
        if (!Directory.Exists(project.MatricesDirectory)) return result;

        for (ushort headerId = 0; headerId < headerCount; headerId++)
        {
            MapHeaderRecord header = getHeader(headerId);
            string matrixPath = Path.Combine(project.MatricesDirectory, $"{header.MatrixId:D4}");
            if (!File.Exists(matrixPath)) continue;

            (bool hasHeaders, ushort[]? cellHeaders, ushort[] cellMaps)? matrix;
            try
            {
                matrix = ReadMatrixCells(matrixPath);
            }
            catch (IOException)
            {
                continue;
            }

            if (matrix is not { } loaded) continue;

            for (int cell = 0; cell < loaded.cellMaps.Length; cell++)
            {
                ushort mapId = loaded.cellMaps[cell];
                if (mapId == 0xFFFF || result.ContainsKey(mapId)) continue;

                int eventFileId;
                if (loaded.hasHeaders)
                {
                    ushort cellHeaderId = loaded.cellHeaders![cell];
                    if (cellHeaderId >= headerCount) continue;
                    eventFileId = getHeader(cellHeaderId).EventFileId;
                }
                else
                {
                    eventFileId = header.EventFileId;
                }

                result[mapId] = eventFileId;
            }
        }

        return result;
    }

    private static (bool HasHeaders, ushort[]? CellHeaders, ushort[] CellMaps) ReadMatrixCells(string path)
    {
        using FileStream stream = File.OpenRead(path);
        using BinaryReader reader = new(stream);
        byte width = reader.ReadByte();
        byte height = reader.ReadByte();
        bool hasHeaders = reader.ReadBoolean();
        bool hasHeights = reader.ReadBoolean();
        byte nameLength = reader.ReadByte();
        reader.ReadBytes(nameLength);

        int cellCount = width * height;
        ushort[]? cellHeaders = null;
        if (hasHeaders)
        {
            cellHeaders = new ushort[cellCount];
            for (int i = 0; i < cellCount; i++) cellHeaders[i] = reader.ReadUInt16();
        }

        if (hasHeights) reader.ReadBytes(cellCount);

        ushort[] cellMaps = new ushort[cellCount];
        for (int i = 0; i < cellCount; i++) cellMaps[i] = reader.ReadUInt16();

        return (hasHeaders, cellHeaders, cellMaps);
    }
}
