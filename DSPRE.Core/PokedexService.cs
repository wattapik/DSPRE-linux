using System.Buffers.Binary;
using System.Text;

namespace DSPRE.Core;

/// <summary>A species' name and Pokédex text. Category and Entry are null where the game has none
/// (alternate forms, eggs) or the ROM's text banks are not known.</summary>
public sealed record PokedexText(string Name, string? Category, string? Entry);

/// <summary>Species names, Pokédex text and regional Pokédex order. Bank numbers and file layouts come
/// from the pret decompilations (pokeplatinum generated/text_banks.txt and pokedex_text.c,
/// pokediamond/pokeheartgold msgdata) and were checked against Platinum and HeartGold ROMs.</summary>
public static class PokedexService
{
    /// <summary>The longest name the game's name buffers hold.</summary>
    public const int MaxNameLength = 10;

    // The game reads at most 256 characters of a dex entry (pokedex_text.c LoadMessage).
    private const int MaxEntryLength = 255;

    /// <param name="Entries">Dex entry banks, this version's own first: DP and HGSS keep one per
    /// version and the other version's goes unused.</param>
    /// <param name="ForeignName">The name as shown, numbered or not, when another language's dex views
    /// this one's entries.</param>
    /// <param name="ForeignCategory">The category as shown by another language's dex.</param>
    private sealed record TextBanks(int NamesWithArticles, int[] Entries, int Category, int ForeignName, int ForeignCategory);

    private static TextBanks? GetTextBanks(RomGameMetadata metadata) => metadata.HeaderFormat switch
    {
        MapHeaderFormat.Platinum => new TextBanks(413, [706], 711, 712, 718),
        MapHeaderFormat.DiamondPearl when metadata.Language != RomLanguage.Japanese =>
            new TextBanks(363, metadata.GameCode.StartsWith("APA") ? [616, 615] : [615, 616], 621, 622, 623),
        MapHeaderFormat.HeartGoldSoulSilver when metadata.Language != RomLanguage.Japanese =>
            new TextBanks(238, metadata.GameCode.StartsWith("IPG") ? [804, 803] : [803, 804], 816, 817, 823),
        _ => null
    };

    public static PokedexText LoadText(RomProject project, RomGameMetadata metadata, int species)
    {
        List<string> names = TextArchiveService.Load(project, TrainerService.GetPokemonNamesArchiveId(metadata), metadata).Messages;
        string name = species < names.Count ? names[species] : "";
        if (GetTextBanks(metadata) is not TextBanks banks || species < 1) return new PokedexText(name, null, null);

        List<string> categories = TextArchiveService.Load(project, banks.Category, metadata).Messages;
        List<string> entries = TextArchiveService.Load(project, banks.Entries[0], metadata).Messages;
        return new PokedexText(name,
            species < categories.Count ? categories[species] : null,
            species < entries.Count ? FromMessage(entries[species]) : null);
    }

    /// <summary>Writes the species' name, category and entry, keeping the other banks that repeat the
    /// name or category (with articles, for foreign dexes) in step where they still show the old one.
    /// <paramref name="saveNames"/> saves the names archive, so the caller can also rename the species'
    /// script constant.</summary>
    public static void SaveText(RomProject project, RomGameMetadata metadata, int species, PokedexText text, Action<TextArchiveData> saveNames)
    {
        string name = text.Name.Trim();
        if (name.Length == 0) throw new InvalidDataException("The name is empty.");
        if (name.Length > MaxNameLength) throw new InvalidDataException($"The name is {name.Length} characters; the game holds at most {MaxNameLength}.");
        if (text.Entry is not null && ToMessage(text.Entry).Length > MaxEntryLength) throw new InvalidDataException($"The entry is longer than the {MaxEntryLength} characters the game reads.");

        PokedexText old = LoadText(project, metadata, species);
        TextBanks? banks = GetTextBanks(metadata);
        if (name != old.Name)
        {
            TextArchiveData names = TextArchiveService.Load(project, TrainerService.GetPokemonNamesArchiveId(metadata), metadata);
            if (species >= names.Messages.Count) throw new InvalidDataException($"The names list has no entry {species}.");
            names.Messages[species] = name;
            saveNames(names);
            if (banks is not null && old.Name.Length > 0)
            {
                Update(project, metadata, banks.NamesWithArticles, species, message => message.Replace(old.Name, name, StringComparison.Ordinal));
                Update(project, metadata, banks.ForeignName, species, message => message.Replace(old.Name, name, StringComparison.Ordinal));
            }
        }

        if (banks is null) return;
        if (text.Category is not null && old.Category is not null && text.Category != old.Category)
        {
            Update(project, metadata, banks.Category, species, _ => text.Category);
            Update(project, metadata, banks.ForeignCategory, species, message => message == old.Category ? text.Category : message);
        }

        if (text.Entry is not null && old.Entry is not null && text.Entry != old.Entry)
        {
            foreach (int bank in banks.Entries) Update(project, metadata, bank, species, _ => ToMessage(text.Entry));
        }
    }

    private static void Update(RomProject project, RomGameMetadata metadata, int bank, int index, Func<string, string> change)
    {
        if (!File.Exists(TextArchiveService.GetJsonPath(project, bank))) return;
        TextArchiveData archive = TextArchiveService.Load(project, bank, metadata);
        if (index >= archive.Messages.Count) return;
        string changed = change(archive.Messages[index]);
        if (changed == archive.Messages[index]) return;
        archive.Messages[index] = changed;
        TextArchiveService.Save(project, archive);
    }

    // Line breaks are stored as the two characters \n.
    private static string FromMessage(string message) => message.Replace("\\n", "\n");

    private static string ToMessage(string text) => text.Replace("\r\n", "\n").Replace("\n", "\\n");

    /// <summary>The regional dex: Order[i] is the species numbered i + 1. Species 1 to NationalCount
    /// can be in it.</summary>
    public sealed record RegionalDex(string Region, List<int> Order, int NationalCount);

    // zukan_data member 11 + n is sort list n (pokedex_sort.c NUMSTATFILES, PDSI_*); list 1 is the
    // regional dex in order.
    private const int RegionalListMember = 12;

    /// <param name="NationalToRegional">One u16 per national number, 0 when not in the regional dex.</param>
    /// <param name="RegionalToNational">Index 0 a placeholder, then one u16 per regional number;
    /// HGSS has no such file.</param>
    /// <param name="Lists">zukan_data and its Giratina Origin Forme copy; the Pokédex screen lists from
    /// whichever matches Giratina's current forme.</param>
    private sealed record DexFiles(string Region, string NationalToRegional, string? RegionalToNational, string[] Lists);

    private static DexFiles GetDexFiles(RomProject project, RomGameMetadata metadata)
    {
        string In(params string[] parts) => Path.Combine([project.FilesPath, .. parts]);
        string zukan = In("application", "zukanlist", "zkn_data", "zukan_data.narc");
        return metadata.HeaderFormat switch
        {
            MapHeaderFormat.DiamondPearl => new DexFiles("Sinnoh", In("poketool", "pokezukan.narc"), In("poketool", "shinzukan.narc"), [zukan]),
            MapHeaderFormat.Platinum => new DexFiles("Sinnoh", In("poketool", "pl_pokezukan.narc"), In("poketool", "shinzukan.narc"),
                [zukan, In("application", "zukanlist", "zkn_data", "zukan_data_gira.narc")]),
            _ => new DexFiles("Johto", In("a", "1", "3", "8"), null, [In("a", "0", "7", "4"), In("a", "2", "1", "4")])
        };
    }

    public static RegionalDex LoadRegionalDex(RomProject project, RomGameMetadata metadata)
    {
        DexFiles files = GetDexFiles(project, metadata);
        return new RegionalDex(files.Region, ReadU16s(files.Lists[0], RegionalListMember).Select(v => (int)v).ToList(),
            ReadU16s(files.NationalToRegional, 0).Length - 1);
    }

    /// <summary>Rewrites the regional dex order. The game's code fixes how many species the regional dex
    /// holds, so the order must keep the same count: it can be reshuffled and species swapped in and out,
    /// but not lengthened or shortened.</summary>
    public static void SaveRegionalDex(RomProject project, RomGameMetadata metadata, IReadOnlyList<int> order) =>
        EditJournal.Track(project, EditJournalDocuments.RegionalDex, EditJournalDocuments.Keys(), () => SaveRegionalDexUntracked(project, metadata, order));

    private static void SaveRegionalDexUntracked(RomProject project, RomGameMetadata metadata, IReadOnlyList<int> order)
    {
        DexFiles files = GetDexFiles(project, metadata);
        ushort[] current = ReadU16s(files.Lists[0], RegionalListMember);
        ushort[] nationalToRegional = ReadU16s(files.NationalToRegional, 0);
        if (order.Count != current.Length) throw new InvalidDataException($"The {files.Region} dex holds {current.Length} Pokémon; the new order has {order.Count}.");
        if (order.Any(species => species < 1 || species >= nationalToRegional.Length)) throw new InvalidDataException($"Only national dex Pokémon (1-{nationalToRegional.Length - 1}) can be in the {files.Region} dex.");
        if (order.Distinct().Count() != order.Count) throw new InvalidDataException($"A Pokémon appears twice in the {files.Region} dex.");

        ushort[] list = order.Select(species => (ushort)species).ToArray();
        // Each copy must still hold the list being replaced; one that differs was changed by something
        // else, and overwriting it could break that change.
        foreach (string path in files.Lists.Skip(1))
        {
            if (File.Exists(path) && !ReadU16s(path, RegionalListMember).SequenceEqual(current))
            {
                throw new InvalidDataException($"{Path.GetRelativePath(project.FilesPath, path)} has a different {files.Region} dex list from {Path.GetRelativePath(project.FilesPath, files.Lists[0])}, so it is not clear which to edit.");
            }
        }

        Array.Clear(nationalToRegional);
        for (int i = 0; i < list.Length; i++) nationalToRegional[list[i]] = (ushort)(i + 1);

        ushort[]? regionalToNational = null;
        if (files.RegionalToNational is not null)
        {
            regionalToNational = ReadU16s(files.RegionalToNational, 0);
            if (regionalToNational.Length != list.Length + 1) throw new InvalidDataException($"{Path.GetFileName(files.RegionalToNational)} does not match the {files.Region} dex list.");
            list.CopyTo(regionalToNational, 1);
        }

        foreach (string path in files.Lists.Where(File.Exists)) WriteU16s(path, RegionalListMember, list);
        WriteU16s(files.NationalToRegional, 0, nationalToRegional);
        if (regionalToNational is not null) WriteU16s(files.RegionalToNational!, 0, regionalToNational);
    }

    private static ushort[] ReadU16s(string narcPath, int member)
    {
        byte[] data = NarcFile.ReadMember(narcPath, member);
        ushort[] values = new ushort[data.Length / 2];
        for (int i = 0; i < values.Length; i++) values[i] = BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(i * 2));
        return values;
    }

    private static void WriteU16s(string narcPath, int member, ushort[] values)
    {
        byte[] data = new byte[values.Length * 2];
        for (int i = 0; i < values.Length; i++) BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(i * 2), values[i]);
        NarcFile.WriteMember(narcPath, member, 0, data);
    }
}
