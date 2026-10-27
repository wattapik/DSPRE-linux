namespace DSPRE.Core;

public sealed class EggMoveEntry
{
    public int SpeciesId { get; set; }
    public List<ushort> MoveIds { get; set; } = [];
}

/// <summary>MaxTableBytes is how much room the game has for the whole list, end marker included;
/// writing more overruns whatever follows it (DP/Pt) or is never read (HGSS).</summary>
public sealed record EggMoveLimits(int MaxTableBytes, int MaxMovesPerSpecies);

/// <summary>The Core counterpart of the legacy DS_Map/Editors/EggMoveEditor.cs. The list is a flat run
/// of u16s: a value above 20000 starts a species (species ID + 20000), the values after it are that
/// species' moves, and 0xFFFF ends the list. HGSS stores it as the only file of its own NARC; DP/Pt
/// store it inside overlay 5.</summary>
public static class EggMoveService
{
    private const int SpeciesMarker = 20000;
    private const ushort EndMarker = 0xFFFF;
    private const int OverlayNumber = 5;
    // "EGG\0": a ROM patched to read egg moves from per-species files instead (legacy's "special format").
    private const int ExpandedFormatMagic = 4671301;

    public static EggMoveLimits GetLimits(RomGameMetadata metadata) =>
        // HGSS: the highest entry index read is 0x7FD (4092 bytes), plus one more species' 16 moves
        // and the end marker.
        new(metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver ? 4126 : 0xEEC, 16);

    public static int GetSizeInBytes(IEnumerable<EggMoveEntry> entries) => entries.Sum(entry => 2 + entry.MoveIds.Count * 2) + 2;

    public static List<EggMoveEntry> Load(RomProject project, RomGameMetadata metadata)
    {
        (string path, long offset) = Locate(project, metadata);
        byte[] data = File.ReadAllBytes(path);
        var entries = new List<EggMoveEntry>();
        for (long position = offset; position + 2 <= data.Length; position += 2)
        {
            ushort value = BitConverter.ToUInt16(data, (int)position);
            if (value == EndMarker) break;
            if (value > SpeciesMarker)
            {
                entries.Add(new EggMoveEntry { SpeciesId = value - SpeciesMarker });
            }
            else if (entries.Count == 0)
            {
                throw new InvalidDataException("The egg move data is malformed: it starts with a move instead of a species.");
            }
            else
            {
                entries[^1].MoveIds.Add(value);
            }
        }

        return entries;
    }

    public static void Save(RomProject project, RomGameMetadata metadata, List<EggMoveEntry> entries) =>
        EditJournal.Track(project, EditJournalDocuments.EggMoves, EditJournalDocuments.Keys(), () =>
        {
            EggMoveLimits limits = GetLimits(metadata);
            int size = GetSizeInBytes(entries);
            if (size > limits.MaxTableBytes)
            {
                throw new InvalidDataException($"The egg move list is {size} bytes but the game only has room for {limits.MaxTableBytes}; saving it would corrupt the data after it.");
            }

            foreach (EggMoveEntry entry in entries)
            {
                if (entry.SpeciesId <= 0 || entry.SpeciesId + SpeciesMarker >= EndMarker) throw new InvalidDataException($"Species {entry.SpeciesId} cannot be stored in the egg move list.");
                if (entry.MoveIds.Any(move => move > SpeciesMarker)) throw new InvalidDataException($"Species {entry.SpeciesId} has a move ID above {SpeciesMarker}, which the game would read as a species.");
            }

            (string path, long offset) = Locate(project, metadata);
            bool ownFile = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;
            using FileStream stream = new(path, FileMode.Open, FileAccess.Write);
            using BinaryWriter writer = new(stream);
            stream.Position = offset;
            foreach (EggMoveEntry entry in entries)
            {
                writer.Write((ushort)(entry.SpeciesId + SpeciesMarker));
                foreach (ushort move in entry.MoveIds) writer.Write(move);
            }

            writer.Write(EndMarker);
            if (ownFile) stream.SetLength(stream.Position);
        });

    private static (string Path, long Offset) Locate(RomProject project, RomGameMetadata metadata)
    {
        if (metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver)
        {
            string file = Path.Combine(project.EggMovesDirectory, "0000");
            // Projects opened before egg moves were unpacked at project open don't have it yet.
            if (!File.Exists(file)) new RomExtractionService().PrepareEggMoves(project, metadata);
            if (!File.Exists(file)) throw new FileNotFoundException("The egg move data file was not found.", file);
            return (file, 0);
        }

        long offset = (metadata.HeaderFormat, metadata.Language) switch
        {
            (MapHeaderFormat.DiamondPearl, RomLanguage.English) => 0x20668,
            (MapHeaderFormat.DiamondPearl, RomLanguage.Japanese) => 0x21654,
            (MapHeaderFormat.DiamondPearl, _) => 0x20620,
            (MapHeaderFormat.Platinum, RomLanguage.English) => 0x29222,
            (MapHeaderFormat.Platinum, RomLanguage.Japanese) => 0x29012,
            (MapHeaderFormat.Platinum, RomLanguage.German) => 0x2923E,
            (MapHeaderFormat.Platinum, RomLanguage.Italian) => 0x29232,
            (MapHeaderFormat.Platinum, _) => 0x2922A,
            _ => throw new ArgumentOutOfRangeException(nameof(metadata))
        };

        string overlayPath = OverlayService.GetOverlayPath(project, OverlayNumber);
        if (!File.Exists(overlayPath)) throw new FileNotFoundException($"Overlay {OverlayNumber} was not found.", overlayPath);

        using FileStream stream = File.OpenRead(overlayPath);
        if (offset + 4 > stream.Length) throw new InvalidDataException($"Overlay {OverlayNumber} is smaller than expected; it may still be compressed.");
        stream.Position = offset;
        using BinaryReader reader = new(stream);
        if (reader.ReadInt32() == ExpandedFormatMagic)
        {
            throw new NotSupportedException("This ROM uses the expanded per-species egg move format, which this editor cannot edit yet.");
        }

        return (overlayPath, offset);
    }
}
