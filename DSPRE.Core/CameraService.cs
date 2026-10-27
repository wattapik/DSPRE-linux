namespace DSPRE.Core;

using YamlDotNet.Serialization;

/// <summary>Resolves ARM9 overlay files and their RAM base addresses for both project formats.</summary>
public static class OverlayService
{
    private sealed class OverlayYaml
    {
        public bool table_signed { get; set; }
        public List<OverlayEntry>? overlays { get; set; }
    }

    private sealed class OverlayEntry
    {
        public int id { get; set; }
        public uint base_address { get; set; }
        public uint code_size { get; set; }
        public uint bss_size { get; set; }
        public int file_id { get; set; }
        public bool compressed { get; set; }
        public string? file_name { get; set; }
    }

    private const int EntryLength = 32;

    public static string GetOverlayPath(RomProject project, int overlayNumber) =>
        project.Format == RomProjectFormat.DsRom
            ? Path.Combine(project.RootPath, "arm9_overlays", $"ov{overlayNumber:D3}.bin")
            : Path.Combine(project.RootPath, "overlay", $"overlay_{overlayNumber:D4}.bin");

    public static string GetOverlayTablePath(RomProject project) =>
        project.Format == RomProjectFormat.DsRom
            ? Path.Combine(project.RootPath, "arm9_overlays", "overlays.yaml")
            : Path.Combine(project.RootPath, "y9.bin");

    /// <summary>Every overlay in the table, as the legacy DS_Map/Editors/OverlayEditor.cs lists them.
    /// "Compressed on disk" is inferred the same way legacy's OverlayUtils.IsCompressed does: the
    /// file is smaller than the uncompressed size the table records for it.</summary>
    public static IReadOnlyList<OverlayInfo> GetOverlays(RomProject project)
    {
        string tablePath = GetOverlayTablePath(project);
        if (!File.Exists(tablePath))
        {
            throw new FileNotFoundException("The ARM9 overlay table is missing.", tablePath);
        }

        var result = new List<OverlayInfo>();
        if (project.Format == RomProjectFormat.DsRom)
        {
            var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
            OverlayYaml? yaml = deserializer.Deserialize<OverlayYaml>(File.ReadAllText(tablePath));
            foreach (OverlayEntry entry in yaml?.overlays ?? [])
            {
                result.Add(CreateInfo(project, entry.id, entry.base_address, entry.code_size + entry.bss_size, entry.compressed));
            }

            return result;
        }

        byte[] table = File.ReadAllBytes(tablePath);
        for (int id = 0; id < table.Length / EntryLength; id++)
        {
            int entry = id * EntryLength;
            result.Add(CreateInfo(
                project, id, BitConverter.ToUInt32(table, entry + 4), BitConverter.ToUInt32(table, entry + 8), (table[entry + 31] & 1) == 1));
        }

        return result;
    }

    private static OverlayInfo CreateInfo(RomProject project, int id, uint ramAddress, uint uncompressedSize, bool markedCompressed)
    {
        string path = GetOverlayPath(project, id);
        long? fileSize = File.Exists(path) ? new FileInfo(path).Length : null;
        return new OverlayInfo(id, ramAddress, uncompressedSize, markedCompressed, fileSize, fileSize is long size && size < uncompressedSize);
    }

    public const uint Arm9RamAddress = 0x02000000;
    public const uint SyntheticOverlayRamAddress = 0x023C8000;

    /// <summary>Where a RAM address lives on disk (legacy's Address Helper): the offset into ARM9,
    /// into each overlay loaded over it (overlays share RAM, so several can match), and into the
    /// synthetic overlay at 0x023C8000.</summary>
    public static List<(string Where, uint Offset)> LocateRamAddress(RomProject project, uint address)
    {
        var found = new List<(string, uint)>();
        long arm9Size = File.Exists(project.Arm9Path) ? new FileInfo(project.Arm9Path).Length : 0;
        if (address >= Arm9RamAddress && address < Arm9RamAddress + arm9Size) found.Add(("ARM9", address - Arm9RamAddress));
        foreach (OverlayInfo overlay in GetOverlays(project))
        {
            if (address >= overlay.RamAddress && address < overlay.RamAddress + overlay.UncompressedSize) found.Add(($"Overlay {overlay.Id}", address - overlay.RamAddress));
        }

        if (address >= SyntheticOverlayRamAddress) found.Add(("Synthetic overlay", address - SyntheticOverlayRamAddress));
        return found;
    }

    public static uint GetRamAddress(RomProject project, int overlayNumber)
    {
        string tablePath = GetOverlayTablePath(project);
        if (!File.Exists(tablePath))
        {
            throw new FileNotFoundException("The ARM9 overlay table is missing.", tablePath);
        }

        if (project.Format == RomProjectFormat.DsRom)
        {
            var deserializer = new DeserializerBuilder().IgnoreUnmatchedProperties().Build();
            OverlayYaml? yaml = deserializer.Deserialize<OverlayYaml>(File.ReadAllText(tablePath));
            OverlayEntry? entry = yaml?.overlays?.FirstOrDefault(overlay => overlay.id == overlayNumber);
            if (entry is null)
            {
                throw new InvalidDataException($"Overlay {overlayNumber} is not listed in overlays.yaml.");
            }

            return entry.base_address;
        }

        using FileStream stream = File.OpenRead(tablePath);
        using BinaryReader reader = new BinaryReader(stream);
        long position = overlayNumber * EntryLength + 4;
        if (position + 4 > stream.Length)
        {
            throw new InvalidDataException($"Overlay {overlayNumber} is not present in the overlay table.");
        }

        stream.Position = position;
        return reader.ReadUInt32();
    }
}

/// <summary>MarkedCompressed is the table's own flag: whether ds-rom will compress the overlay when the
/// ROM is built, or for ndstool projects whether the game expects it compressed.</summary>
public sealed record OverlayInfo(int Id, uint RamAddress, uint UncompressedSize, bool MarkedCompressed, long? FileSize, bool CompressedOnDisk);

public sealed class GameCameraRecord
{
    public const byte Perspective = 0;
    public const byte Ortho = 1;

    public uint Distance { get; set; } = 0x29AEC1;
    public short VerticalRotation { get; set; } = 0xD62;
    public short HorizontalRotation { get; set; }
    public short ZRotation { get; set; }
    public short Unknown1 { get; set; }
    public byte PerspectiveMode { get; set; } = Perspective;
    public byte Unknown2 { get; set; }
    public ushort Fov { get; set; } = 1473;
    public uint NearClip { get; set; } = 614400;
    public uint FarClip { get; set; } = 0x384000;
    public int? XOffset { get; set; }
    public int? YOffset { get; set; }
    public int? ZOffset { get; set; }

    public bool IsOrthographic
    {
        get => PerspectiveMode == Ortho;
        set => PerspectiveMode = value ? Ortho : Perspective;
    }

    public static GameCameraRecord FromByteArray(byte[] data, bool includeOffsets)
    {
        if (data.Length != 36 && data.Length != 24)
        {
            throw new InvalidDataException("This is not a camera file.\nMake sure the file is 36 or 24 bytes long and try again.");
        }

        using MemoryStream stream = new MemoryStream(data);
        using BinaryReader reader = new BinaryReader(stream);
        var camera = new GameCameraRecord
        {
            Distance = reader.ReadUInt32(),
            VerticalRotation = reader.ReadInt16(),
            HorizontalRotation = reader.ReadInt16(),
            ZRotation = reader.ReadInt16(),
            Unknown1 = reader.ReadInt16(),
            PerspectiveMode = reader.ReadByte(),
            Unknown2 = reader.ReadByte(),
            Fov = reader.ReadUInt16(),
            NearClip = reader.ReadUInt32(),
            FarClip = reader.ReadUInt32()
        };

        if (includeOffsets && stream.Position + 12 <= stream.Length)
        {
            camera.XOffset = reader.ReadInt32();
            camera.YOffset = reader.ReadInt32();
            camera.ZOffset = reader.ReadInt32();
        }

        return camera;
    }

    public byte[] ToByteArray()
    {
        using MemoryStream stream = new MemoryStream();
        using (BinaryWriter writer = new BinaryWriter(stream, System.Text.Encoding.UTF8, true))
        {
            writer.Write(Distance);
            writer.Write(VerticalRotation);
            writer.Write(HorizontalRotation);
            writer.Write(ZRotation);
            writer.Write(Unknown1);
            writer.Write(PerspectiveMode);
            writer.Write(Unknown2);
            writer.Write(Fov);
            writer.Write(NearClip);
            writer.Write(FarClip);

            if (XOffset is not null) writer.Write(XOffset.Value);
            if (YOffset is not null) writer.Write(YOffset.Value);
            if (ZOffset is not null) writer.Write(ZOffset.Value);
        }

        return stream.ToArray();
    }
}

public sealed record CameraTableLayout(int OverlayNumber, uint[] OffsetsToRamAddress, int CameraSize, int CameraCount)
{
    public bool HasOffsets => CameraSize == 36;
}

public sealed record CameraTable(CameraTableLayout Layout, uint OverlayOffset, List<GameCameraRecord> Cameras, string? Warning);

public static class CameraService
{
    public static CameraTableLayout GetLayout(RomGameMetadata metadata)
    {
        return metadata.HeaderFormat switch
        {
            MapHeaderFormat.DiamondPearl => new CameraTableLayout(
                5,
                metadata.Language == RomLanguage.Japanese ? [0x4C50] : [0x4908],
                24,
                16),
            MapHeaderFormat.Platinum => new CameraTableLayout(5, [0x4E24], 24, 16),
            MapHeaderFormat.HeartGoldSoulSilver => new CameraTableLayout(
                1,
                metadata.Language == RomLanguage.Japanese ? [0x5324, 0x5474] : [0x532C, 0x547C],
                36,
                17),
            _ => throw new ArgumentOutOfRangeException(nameof(metadata))
        };
    }

    public static CameraTable Load(RomProject project, RomGameMetadata metadata)
    {
        CameraTableLayout layout = GetLayout(metadata);
        string overlayPath = OverlayService.GetOverlayPath(project, layout.OverlayNumber);
        if (!File.Exists(overlayPath))
        {
            throw new FileNotFoundException($"Overlay {layout.OverlayNumber} was not found.", overlayPath);
        }

        uint[] ramAddresses = new uint[layout.OffsetsToRamAddress.Length];
        using (FileStream stream = File.OpenRead(overlayPath))
        using (BinaryReader reader = new BinaryReader(stream))
        {
            for (int i = 0; i < layout.OffsetsToRamAddress.Length; i++)
            {
                long position = layout.OffsetsToRamAddress[i];
                if (position + 4 > stream.Length)
                {
                    throw new InvalidDataException(
                        $"Overlay {layout.OverlayNumber} is smaller than expected; it may still be compressed.");
                }

                stream.Position = position;
                ramAddresses[i] = reader.ReadUInt32();
            }
        }

        string? warning = null;
        for (int i = 1; i < ramAddresses.Length; i++)
        {
            if (ramAddresses[i] != ramAddresses[0])
            {
                warning = $"Value of RAM Pointer to the overlay table is different between Offset #1 and Offset #{i + 1}. The camera values might be wrong.";
            }
        }

        uint overlayRamAddress = OverlayService.GetRamAddress(project, layout.OverlayNumber);
        if (ramAddresses[0] < overlayRamAddress)
        {
            throw new InvalidDataException("The camera table pointer does not fall inside the overlay.");
        }

        uint tableOffset = ramAddresses[0] - overlayRamAddress;
        var cameras = new List<GameCameraRecord>(layout.CameraCount);
        using (FileStream stream = File.OpenRead(overlayPath))
        using (BinaryReader reader = new BinaryReader(stream))
        {
            if (tableOffset + (long)layout.CameraCount * layout.CameraSize > stream.Length)
            {
                throw new InvalidDataException("The camera table does not fit inside the overlay.");
            }

            stream.Position = tableOffset;
            for (int i = 0; i < layout.CameraCount; i++)
            {
                cameras.Add(GameCameraRecord.FromByteArray(reader.ReadBytes(layout.CameraSize), layout.HasOffsets));
            }
        }

        return new CameraTable(layout, tableOffset, cameras, warning);
    }

    public static void Save(RomProject project, CameraTable table) =>
        EditJournal.Track(project, EditJournalDocuments.Camera, EditJournalDocuments.Keys(), () => SaveUntracked(project, table));

    private static void SaveUntracked(RomProject project, CameraTable table)
    {
        string overlayPath = OverlayService.GetOverlayPath(project, table.Layout.OverlayNumber);
        WriteTable(overlayPath, table, table.OverlayOffset);
    }

    public static void ExportTable(string path, CameraTable table)
    {
        File.WriteAllBytes(path, []);
        WriteTable(path, table, 0);
    }

    private static void WriteTable(string path, CameraTable table, uint destinationOffset)
    {
        using FileStream stream = new FileStream(path, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        for (int i = 0; i < table.Cameras.Count; i++)
        {
            byte[] data = table.Cameras[i].ToByteArray();
            stream.Position = destinationOffset + (long)i * table.Layout.CameraSize;
            stream.Write(data);
        }
    }

    public static List<GameCameraRecord> ImportTable(string path, CameraTableLayout layout)
    {
        long length = new FileInfo(path).Length;
        if (length == 0 || length % layout.CameraSize != 0)
        {
            throw new InvalidDataException(
                $"This is not a camera table for this game.\nMake sure the file length is a multiple of {layout.CameraSize} and try again.");
        }

        byte[] data = File.ReadAllBytes(path);
        int count = (int)(length / layout.CameraSize);
        var cameras = new List<GameCameraRecord>(count);
        for (int i = 0; i < count; i++)
        {
            cameras.Add(GameCameraRecord.FromByteArray(
                data.Skip(i * layout.CameraSize).Take(layout.CameraSize).ToArray(),
                layout.HasOffsets));
        }

        return cameras;
    }
}
