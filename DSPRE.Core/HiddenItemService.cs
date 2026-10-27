namespace DSPRE.Core;

/// <summary>One hidden item. ScriptId is what a spawnable refers to it by (as script 8000 + ScriptId). Extra is the byte after the
/// amount: always 0 in HeartGold/SoulSilver, 0, 1 or 2 in Platinum (2 for almost every entry); what the game does with it is not known.</summary>
public sealed class HiddenItemEntry
{
    public ushort ItemId { get; set; }
    public byte Amount { get; set; } = 1;
    public ushort ScriptId { get; set; }
    public byte Extra { get; set; }
}

public enum HiddenItemLayoutKind
{
    /// <summary>HeartGold/SoulSilver: the code compares an index with a Thumb <c>cmp rN, #count</c>; the table's length is that byte.</summary>
    ThumbImmediate,

    /// <summary>Platinum: the code loads the table's address and its entry count from a literal pool; the count is not changed, so
    /// entries past the end of the list are filled with ones that no script asks for.</summary>
    LiteralPool,
}

/// <summary>Where the hidden item table and its length are in the ARM9.</summary>
public sealed record HiddenItemLayout(HiddenItemLayoutKind Kind, int TableOffset, int Capacity, int LengthSite, int CapacitySite)
{
    public bool HasExtraByte => Kind == HiddenItemLayoutKind.LiteralPool;
}

public sealed record HiddenItemTable(List<HiddenItemEntry> Entries, int Capacity, HiddenItemLayout Layout);

/// <summary>The hidden item table: entries of 8 bytes, u16 item, u8 amount, u8 extra, 2 unused bytes, u16 script ID, looked up by script
/// ID when the player finds the item (a spawnable of type hidden item runs script 8000 + ID). The list ends at the first entry with item 0.
///
/// The table is found by its references rather than by fixed offsets, so every region of the games with the same code works: the code
/// that looks an entry up holds the table's address twice in a literal pool and the number of entries either next to the address
/// (Platinum) or as the immediate of a Thumb compare close by (HeartGold/SoulSilver, where legacy changed the first of the two compares
/// near the end of that code as the table's length). Checked against US Platinum (table 0xEA378, 257 entries) and US HeartGold (0xFA558,
/// 231); other regions and Diamond/Pearl have not been tried.</summary>
public static class HiddenItemService
{
    private const int EntrySize = 8;
    private const uint RamBase = 0x02000000;
    private const int ProbeEntries = 32;
    private const ushort Sentinel = 0xFFFF;

    public static bool IsPossible(RomGameMetadata metadata) => metadata.HeaderFormat != MapHeaderFormat.DiamondPearl;

    /// <summary>The table's place in this project's ARM9, or why it was not found.</summary>
    public static HiddenItemLayout? Locate(byte[] arm9, RomGameMetadata metadata, out string? problem)
    {
        problem = null;
        if (metadata.HeaderFormat == MapHeaderFormat.DiamondPearl)
        {
            problem = "The hidden items table has not been found in Diamond/Pearl, so it cannot be edited there.";
            return null;
        }

        HiddenItemLayout? layout = metadata.HeaderFormat == MapHeaderFormat.Platinum ? LocateLiteralPool(arm9) : LocateThumbImmediate(arm9);
        if (layout is null) problem = "The hidden items table could not be found in this ROM's ARM9 (it may still be compressed, or this version's code differs).";
        return layout;
    }

    public static HiddenItemLayout? Locate(RomProject project, RomGameMetadata metadata, out string? problem) =>
        Locate(File.ReadAllBytes(project.Arm9Path), metadata, out problem);

    private static bool LooksLikeTable(byte[] arm9, int offset, int entries)
    {
        if (offset < 0 || offset % 4 != 0 || offset + entries * EntrySize > arm9.Length) return false;
        for (int i = 0; i < entries; i++)
        {
            int at = offset + i * EntrySize;
            ushort item = BitConverter.ToUInt16(arm9, at);
            if (item == 0 || item > 2000 || arm9[at + 4] != 0 || arm9[at + 5] != 0 || BitConverter.ToUInt16(arm9, at + 6) > 4000) return false;
        }

        return true;
    }

    private static int? TableAt(byte[] arm9, int position)
    {
        uint address = BitConverter.ToUInt32(arm9, position);
        if (address < RamBase || address >= RamBase + (uint)arm9.Length) return null;
        int offset = (int)(address - RamBase);
        return LooksLikeTable(arm9, offset, ProbeEntries) ? offset : null;
    }

    private static HiddenItemLayout? LocateLiteralPool(byte[] arm9)
    {
        var found = new Dictionary<(int Offset, int Count), int>();
        for (int i = 0; i + 8 <= arm9.Length; i += 4)
        {
            if (TableAt(arm9, i) is not int offset) continue;
            uint count = BitConverter.ToUInt32(arm9, i + 4);
            if (count < ProbeEntries || count > 2000 || offset + count * EntrySize > arm9.Length) continue;
            found[(offset, (int)count)] = found.GetValueOrDefault((offset, (int)count)) + 1;
        }

        var candidates = found.Where(pair => pair.Value >= 2).Select(pair => pair.Key).ToList();
        if (candidates.Count != 1) return null;
        (int tableOffset, int capacity) = candidates[0];
        return new HiddenItemLayout(HiddenItemLayoutKind.LiteralPool, tableOffset, capacity, -1, -1);
    }

    private static HiddenItemLayout? LocateThumbImmediate(byte[] arm9)
    {
        var references = new Dictionary<int, List<int>>();
        for (int i = 0; i + 4 <= arm9.Length; i += 4)
        {
            if (TableAt(arm9, i) is int offset) (references.TryGetValue(offset, out List<int>? list) ? list : references[offset] = []).Add(i);
        }

        var candidates = references.Where(pair => pair.Value.Count >= 2).ToList();
        if (candidates.Count != 1) return null;
        int tableOffset = candidates[0].Key;

        var sites = new SortedSet<int>();
        foreach (int reference in candidates[0].Value)
        {
            for (int at = Math.Max(0, reference - 0x80); at < Math.Min(arm9.Length - 1, reference + 0x80); at += 2)
            {
                if (arm9[at + 1] is >= 0x28 and <= 0x2F && arm9[at] >= 100) sites.Add(at);
            }
        }

        // The compare in question is the one most of them agree on: the same count in at least two places.
        var byCount = sites.GroupBy(site => arm9[site]).Where(group => group.Count() >= 2).ToList();
        if (byCount.Count != 1) return null;
        List<int> chosen = byCount[0].Order().ToList();
        int lengthSite = chosen[^2], capacitySite = chosen[^1];
        int capacity = arm9[capacitySite];
        return LooksLikeTable(arm9, tableOffset, Math.Min(capacity, ProbeEntries))
            ? new HiddenItemLayout(HiddenItemLayoutKind.ThumbImmediate, tableOffset, capacity, lengthSite, capacitySite)
            : null;
    }

    public static HiddenItemTable Load(RomProject project, RomGameMetadata metadata)
    {
        byte[] arm9 = File.ReadAllBytes(project.Arm9Path);
        HiddenItemLayout layout = Locate(arm9, metadata, out string? problem) ?? throw new NotSupportedException(problem);
        int length = layout.Capacity;
        if (layout.Kind == HiddenItemLayoutKind.ThumbImmediate)
        {
            length = arm9[layout.LengthSite];
            // Same fallback as legacy for a length byte that makes no sense against the capacity.
            if (length > layout.Capacity) length = layout.Capacity;
        }

        var entries = new List<HiddenItemEntry>();
        for (int i = 0; i < length; i++)
        {
            int offset = layout.TableOffset + i * EntrySize;
            ushort itemId = BitConverter.ToUInt16(arm9, offset);
            if (itemId == 0) break;
            entries.Add(new HiddenItemEntry { ItemId = itemId, Amount = arm9[offset + 2], Extra = arm9[offset + 3], ScriptId = BitConverter.ToUInt16(arm9, offset + 6) });
        }

        return new HiddenItemTable(entries, layout.Capacity, layout);
    }

    public static void Save(RomProject project, RomGameMetadata metadata, List<HiddenItemEntry> entries) =>
        EditJournal.Track(project, EditJournalDocuments.HiddenItems, EditJournalDocuments.Keys(), () =>
        {
            HiddenItemLayout layout = Locate(project, metadata, out string? problem) ?? throw new NotSupportedException(problem);
            if (entries.Count > layout.Capacity) throw new InvalidDataException($"The hidden item table holds at most {layout.Capacity} entries.");
            if (entries.Any(entry => entry.ItemId == 0)) throw new InvalidDataException("A hidden item entry needs an item: item 0 marks the end of the table.");

            // The whole capacity is rewritten so entries past the new end are cleared, as legacy does.
            byte[] table = new byte[layout.Capacity * EntrySize];
            for (int i = 0; i < layout.Capacity; i++)
            {
                HiddenItemEntry? entry = i < entries.Count ? entries[i] : null;
                BitConverter.GetBytes(entry?.ItemId ?? (ushort)0).CopyTo(table, i * EntrySize);
                table[i * EntrySize + 2] = entry?.Amount ?? 0;
                table[i * EntrySize + 3] = entry?.Extra ?? 0;
                // Platinum keeps looking at every slot, so a free one must not carry an ID a script could ask for.
                BitConverter.GetBytes(entry?.ScriptId ?? (layout.Kind == HiddenItemLayoutKind.LiteralPool ? Sentinel : (ushort)0)).CopyTo(table, i * EntrySize + 6);
            }

            using FileStream stream = new(project.Arm9Path, FileMode.Open, FileAccess.Write);
            stream.Position = layout.TableOffset;
            stream.Write(table);
            if (layout.Kind == HiddenItemLayoutKind.ThumbImmediate)
            {
                stream.Position = layout.LengthSite;
                stream.WriteByte((byte)entries.Count);
            }
        });

    /// <summary>The smallest script ID no entry has.</summary>
    public static ushort FirstFreeScriptId(IEnumerable<HiddenItemEntry> entries)
    {
        var used = entries.Select(entry => entry.ScriptId).ToHashSet();
        ushort id = 0;
        while (used.Contains(id)) id++;
        return id;
    }
}
