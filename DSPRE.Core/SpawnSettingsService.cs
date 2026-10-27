namespace DSPRE.Core;

/// <summary>Where a new game starts and how much money the player starts with. GlobalX/GlobalY are
/// in tiles across the header's whole matrix: matrix cell * 32 + tile within that cell's map.</summary>
public sealed class SpawnSettings
{
    public const int MapTiles = 32;

    public ushort HeaderId { get; set; }
    public ushort GlobalX { get; set; }
    public ushort GlobalY { get; set; }
    /// <summary>0 = up, 1 = down, 2 = left, 3 = right.</summary>
    public ushort Direction { get; set; }
    public uint InitialMoney { get; set; }
}

public sealed record SpawnSettingsLayout(long Arm9Offset, int MoneyOverlayNumber, long MoneyOverlayOffset);

/// <summary>The Core counterpart of the legacy DS_Map/Editors/SpawnEditor.cs: the spawn record lives
/// in the ARM9 (offsets from RomInfo.arm9spawnOffset) and the starting money in an overlay
/// (RomInfo.initialMoneyOverlayNumber/Offset).</summary>
public static class SpawnSettingsService
{
    private const int GlobalXOffset = 8;
    private const int GlobalYOffset = 12;
    private const int DirectionOffset = 16;
    private const int SpawnRecordLength = 18;

    public static SpawnSettingsLayout GetLayout(RomGameMetadata metadata)
    {
        RomLanguage language = metadata.Language;
        return metadata.HeaderFormat switch
        {
            MapHeaderFormat.DiamondPearl => new SpawnSettingsLayout(language switch
            {
                RomLanguage.English => 0xF2B9C,
                RomLanguage.Spanish => 0xF2BE8,
                RomLanguage.Italian => 0xF2B50,
                RomLanguage.French => 0xF2BDC,
                RomLanguage.German => 0xF2BAC,
                _ => 0xF4B48
            }, 52, 0x1E4),
            MapHeaderFormat.Platinum => new SpawnSettingsLayout(language switch
            {
                RomLanguage.English => 0xEA12C,
                RomLanguage.Spanish => 0xEA1C0,
                RomLanguage.Italian => 0xEA148,
                RomLanguage.French => 0xEA1B4,
                RomLanguage.German => 0xEA184,
                _ => 0xE9800
            }, 57, 0x1EC),
            MapHeaderFormat.HeartGoldSoulSilver => new SpawnSettingsLayout(language switch
            {
                RomLanguage.English => 0xFA17C,
                RomLanguage.Spanish => metadata.GameCode.StartsWith("IPK", StringComparison.OrdinalIgnoreCase) ? 0xFA164 : 0xFA16C,
                RomLanguage.Italian => 0xFA0F4,
                RomLanguage.French => 0xFA160,
                RomLanguage.German => 0xFA130,
                _ => 0xF992C
            }, 36, 0x2FC),
            _ => throw new ArgumentOutOfRangeException(nameof(metadata))
        };
    }

    public static SpawnSettings Load(RomProject project, RomGameMetadata metadata)
    {
        SpawnSettingsLayout layout = GetLayout(metadata);
        byte[] spawn = ReadAt(project.Arm9Path, layout.Arm9Offset, SpawnRecordLength, "The ARM9");
        byte[] money = ReadAt(
            OverlayService.GetOverlayPath(project, layout.MoneyOverlayNumber), layout.MoneyOverlayOffset, 4,
            $"Overlay {layout.MoneyOverlayNumber}");

        return new SpawnSettings
        {
            HeaderId = BitConverter.ToUInt16(spawn, 0),
            GlobalX = BitConverter.ToUInt16(spawn, GlobalXOffset),
            GlobalY = BitConverter.ToUInt16(spawn, GlobalYOffset),
            Direction = BitConverter.ToUInt16(spawn, DirectionOffset),
            InitialMoney = BitConverter.ToUInt32(money, 0)
        };
    }

    public static void Save(RomProject project, RomGameMetadata metadata, SpawnSettings settings) =>
        EditJournal.Track(project, EditJournalDocuments.SpawnSettings, EditJournalDocuments.Keys(), () =>
        {
            SpawnSettingsLayout layout = GetLayout(metadata);
            // Validates both files are long enough before either is touched.
            ReadAt(project.Arm9Path, layout.Arm9Offset, SpawnRecordLength, "The ARM9");
            string overlayPath = OverlayService.GetOverlayPath(project, layout.MoneyOverlayNumber);
            ReadAt(overlayPath, layout.MoneyOverlayOffset, 4, $"Overlay {layout.MoneyOverlayNumber}");

            using (FileStream arm9 = new(project.Arm9Path, FileMode.Open, FileAccess.Write))
            {
                WriteAt(arm9, layout.Arm9Offset, BitConverter.GetBytes(settings.HeaderId));
                WriteAt(arm9, layout.Arm9Offset + GlobalXOffset, BitConverter.GetBytes(settings.GlobalX));
                WriteAt(arm9, layout.Arm9Offset + GlobalYOffset, BitConverter.GetBytes(settings.GlobalY));
                WriteAt(arm9, layout.Arm9Offset + DirectionOffset, BitConverter.GetBytes(settings.Direction));
            }

            using FileStream overlay = new(overlayPath, FileMode.Open, FileAccess.Write);
            WriteAt(overlay, layout.MoneyOverlayOffset, BitConverter.GetBytes(settings.InitialMoney));
        });

    private static byte[] ReadAt(string path, long offset, int length, string description)
    {
        if (!File.Exists(path)) throw new FileNotFoundException($"{description} file was not found.", path);

        using FileStream stream = File.OpenRead(path);
        if (offset + length > stream.Length)
        {
            throw new InvalidDataException($"{description} is smaller than expected; it may still be compressed.");
        }

        stream.Position = offset;
        byte[] data = new byte[length];
        stream.ReadExactly(data);
        return data;
    }

    private static void WriteAt(FileStream stream, long offset, byte[] data)
    {
        stream.Position = offset;
        stream.Write(data);
    }
}
