namespace DSPRE.Core;

/// <summary>The Pickup ability's item tables. Each 10-level bracket reads a sliding window over the
/// item lists: bracket b (levels b*10+1 to b*10+10) uses common items b..b+8 and rare items b..b+1.
/// A Pokemon picks something up when random % ActivationDivisor == 0; a 0-99 roll below
/// WeightThresholds[n] then selects common slot n, and 98-99 select the rare slots.</summary>
public sealed class PickupTable
{
    public const int CommonItemCount = 18;
    public const int RareItemCount = 11;
    public const int WeightCount = 9;
    public const int CommonSlotsPerBracket = 9;
    public const int RareSlotsPerBracket = 2;
    public const int BracketCount = 10;

    public List<ushort> CommonItems { get; set; } = [];
    public List<ushort> RareItems { get; set; } = [];
    public byte ActivationDivisor { get; set; } = 10;
    public List<byte> WeightThresholds { get; set; } = [];
}

public sealed record PickupTableLayout(int OverlayNumber, long CommonItemsOffset, long RareItemsOffset, long ActivationDivisorOffset, long WeightTableOffset);

/// <summary>The Core counterpart of the legacy DS_Map/Editors/PickupTableEditor.cs; offsets are
/// RomInfo's pickup* values.</summary>
public static class PickupTableService
{
    public static PickupTableLayout GetLayout(RomGameMetadata metadata)
    {
        RomLanguage language = metadata.Language;
        bool frenchOrSpanish = language is RomLanguage.French or RomLanguage.Spanish;
        bool germanOrItalian = language is RomLanguage.German or RomLanguage.Italian;
        return metadata.HeaderFormat switch
        {
            MapHeaderFormat.DiamondPearl when language == RomLanguage.Japanese => new(11, 0x32E64, 0x32D9C, 0xADE4, 0x32D68),
            MapHeaderFormat.DiamondPearl when frenchOrSpanish => new(11, 0x308CC, 0x307F0, 0xBDBA, 0x30CF8),
            MapHeaderFormat.DiamondPearl when germanOrItalian => new(11, 0x307E0, 0x30704, 0xBCCE, 0x30C0C),
            MapHeaderFormat.DiamondPearl => new(11, 0x30764, 0x30688, 0xBCB2, 0x30B90),
            MapHeaderFormat.Platinum when language == RomLanguage.Japanese => new(16, 0x33520, 0x33444, 0xC622, 0x3395C),
            MapHeaderFormat.Platinum when frenchOrSpanish => new(16, 0x33634, 0x33558, 0xC732, 0x33A70),
            MapHeaderFormat.Platinum when germanOrItalian => new(16, 0x33548, 0x3346C, 0xC646, 0x33984),
            MapHeaderFormat.Platinum => new(16, 0x3352C, 0x33450, 0xC62A, 0x33968),
            MapHeaderFormat.HeartGoldSoulSilver when language == RomLanguage.Japanese => new(12, 0x34B00, 0x34A08, 0xC85A, 0x35148),
            MapHeaderFormat.HeartGoldSoulSilver => new(12, 0x34B44, 0x34A4C, 0xC852, 0x3518C),
            _ => throw new ArgumentOutOfRangeException(nameof(metadata))
        };
    }

    public static PickupTable Load(RomProject project, RomGameMetadata metadata)
    {
        PickupTableLayout layout = GetLayout(metadata);
        byte[] overlay = ReadOverlay(project, layout);

        var table = new PickupTable { ActivationDivisor = overlay[layout.ActivationDivisorOffset] };
        for (int i = 0; i < PickupTable.CommonItemCount; i++) table.CommonItems.Add(BitConverter.ToUInt16(overlay, (int)layout.CommonItemsOffset + i * 2));
        for (int i = 0; i < PickupTable.RareItemCount; i++) table.RareItems.Add(BitConverter.ToUInt16(overlay, (int)layout.RareItemsOffset + i * 2));
        for (int i = 0; i < PickupTable.WeightCount; i++) table.WeightThresholds.Add(overlay[layout.WeightTableOffset + i]);
        return table;
    }

    public static void Save(RomProject project, RomGameMetadata metadata, PickupTable table) =>
        EditJournal.Track(project, EditJournalDocuments.PickupTable, EditJournalDocuments.Keys(), () =>
        {
            if (table.CommonItems.Count != PickupTable.CommonItemCount || table.RareItems.Count != PickupTable.RareItemCount ||
                table.WeightThresholds.Count != PickupTable.WeightCount)
            {
                throw new InvalidDataException("The pickup table has a fixed number of items and thresholds.");
            }

            if (table.ActivationDivisor == 0) throw new InvalidDataException("The activation divisor must be between 1 and 255.");

            PickupTableLayout layout = GetLayout(metadata);
            ReadOverlay(project, layout);
            using FileStream stream = new(OverlayService.GetOverlayPath(project, layout.OverlayNumber), FileMode.Open, FileAccess.Write);
            using BinaryWriter writer = new(stream);
            stream.Position = layout.CommonItemsOffset;
            foreach (ushort item in table.CommonItems) writer.Write(item);
            stream.Position = layout.RareItemsOffset;
            foreach (ushort item in table.RareItems) writer.Write(item);
            stream.Position = layout.ActivationDivisorOffset;
            writer.Write(table.ActivationDivisor);
            stream.Position = layout.WeightTableOffset;
            foreach (byte threshold in table.WeightThresholds) writer.Write(threshold);
        });

    private static byte[] ReadOverlay(RomProject project, PickupTableLayout layout)
    {
        string path = OverlayService.GetOverlayPath(project, layout.OverlayNumber);
        if (!File.Exists(path)) throw new FileNotFoundException($"Overlay {layout.OverlayNumber} was not found.", path);

        byte[] overlay = File.ReadAllBytes(path);
        long needed = new[]
        {
            layout.CommonItemsOffset + PickupTable.CommonItemCount * 2,
            layout.RareItemsOffset + PickupTable.RareItemCount * 2,
            layout.ActivationDivisorOffset + 1,
            layout.WeightTableOffset + PickupTable.WeightCount
        }.Max();
        if (needed > overlay.Length)
        {
            throw new InvalidDataException($"Overlay {layout.OverlayNumber} is smaller than expected; it may still be compressed.");
        }

        return overlay;
    }
}
