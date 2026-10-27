using System.Globalization;

namespace DSPRE.Core;

/// <summary>A script that starts a trade: the script file, the Script or Function holding the command,
/// and the maps (headers) that load that script file.</summary>
public sealed record TradeUse(int ScriptFile, string Section, IReadOnlyList<(int Header, string Name)> Maps);

public enum TradeOriginLanguage
{
    None = 0,
    Japanese = 1,
    English = 2,
    French = 3,
    Italian = 4,
    German = 5,
    Unused = 6,
    Spanish = 7,
    Korean = 8
}

/// <summary>Byte-exact port of the legacy TradeData record (DS_Map/ROMFiles/TradeData.cs): 20 int32
/// fields (80 bytes) on DP/Pt, plus one trailing "unknown" int32 (84 bytes total) on HGSS only.</summary>
public sealed class TradeRecord
{
    public int Species;
    public int HpIv, AtkIv, DefIv, SpeedIv, SpAtkIv, SpDefIv;
    public int Ability; // Present in the format but unused by the game.
    public int OtId;
    public int Cool, Beauty, Cute, Smart, Tough;
    public int Pid;
    public int HeldItem;
    public int OtGender;
    public int Sheen;
    public int Language;
    public int RequestedSpecies;
    public int Unknown; // HGSS only; appears unused.
}

public static class TradeDataService
{
    public static IReadOnlyList<int> GetIds(RomProject project)
    {
        if (!Directory.Exists(project.TradeDataDirectory)) return [];
        return Directory.GetFiles(project.TradeDataDirectory)
            .Select(Path.GetFileNameWithoutExtension)
            .Where(name => int.TryParse(name, out _))
            .Select(int.Parse)
            .OrderBy(id => id)
            .ToArray();
    }

    public static int GetFileCount(RomProject project) =>
        Directory.Exists(project.TradeDataDirectory) ? Directory.GetFiles(project.TradeDataDirectory).Length : 0;

    public static TradeRecord Load(RomProject project, int id, bool isHgss)
    {
        string path = Path.Combine(project.TradeDataDirectory, id.ToString("D4"));
        byte[] data = File.Exists(path) ? File.ReadAllBytes(path) : new byte[isHgss ? 84 : 80];

        using MemoryStream stream = new(data);
        using BinaryReader reader = new(stream);

        var trade = new TradeRecord
        {
            Species = ReadInt32OrZero(reader),
            HpIv = ReadInt32OrZero(reader),
            AtkIv = ReadInt32OrZero(reader),
            DefIv = ReadInt32OrZero(reader),
            SpeedIv = ReadInt32OrZero(reader),
            SpAtkIv = ReadInt32OrZero(reader),
            SpDefIv = ReadInt32OrZero(reader),
            Ability = ReadInt32OrZero(reader),
            OtId = ReadInt32OrZero(reader),
            Cool = ReadInt32OrZero(reader),
            Beauty = ReadInt32OrZero(reader),
            Cute = ReadInt32OrZero(reader),
            Smart = ReadInt32OrZero(reader),
            Tough = ReadInt32OrZero(reader),
            Pid = ReadInt32OrZero(reader),
            HeldItem = ReadInt32OrZero(reader),
            OtGender = ReadInt32OrZero(reader),
            Sheen = ReadInt32OrZero(reader),
            Language = ReadInt32OrZero(reader),
            RequestedSpecies = ReadInt32OrZero(reader),
        };

        if (isHgss)
        {
            trade.Unknown = ReadInt32OrZero(reader);
        }

        return trade;
    }

    private static int ReadInt32OrZero(BinaryReader reader) =>
        reader.BaseStream.Position + 4 <= reader.BaseStream.Length ? reader.ReadInt32() : 0;

    public static void Save(RomProject project, int id, TradeRecord trade, bool isHgss) =>
        EditJournal.Track(project, EditJournalDocuments.Trade, EditJournalDocuments.Keys(("trade", id)), () => SaveUntracked(project, id, trade, isHgss));

    private static void SaveUntracked(RomProject project, int id, TradeRecord trade, bool isHgss)
    {
        Directory.CreateDirectory(project.TradeDataDirectory);
        string path = Path.Combine(project.TradeDataDirectory, id.ToString("D4"));

        using MemoryStream stream = new();
        using (BinaryWriter writer = new(stream))
        {
            writer.Write(trade.Species);
            writer.Write(trade.HpIv);
            writer.Write(trade.AtkIv);
            writer.Write(trade.DefIv);
            writer.Write(trade.SpeedIv);
            writer.Write(trade.SpAtkIv);
            writer.Write(trade.SpDefIv);
            writer.Write(trade.Ability);
            writer.Write(trade.OtId);
            writer.Write(trade.Cool);
            writer.Write(trade.Beauty);
            writer.Write(trade.Cute);
            writer.Write(trade.Smart);
            writer.Write(trade.Tough);
            writer.Write(trade.Pid);
            writer.Write(trade.HeldItem);
            writer.Write(trade.OtGender);
            writer.Write(trade.Sheen);
            writer.Write(trade.Language);
            writer.Write(trade.RequestedSpecies);

            if (isHgss)
            {
                writer.Write(trade.Unknown);
            }
        }

        File.WriteAllBytes(path, stream.ToArray());
    }

    /// <summary>Trade nicknames and OT names share one text archive: nickname for trade N lives at
    /// message index N, and its OT name at index N + (total trade count) - ported from the legacy
    /// TradeEditor's GetMonNickname/GetOTName/SaveTextDataToFile indexing exactly.</summary>
    public static int GetTextBankArchiveId(RomGameMetadata metadata) => (metadata.HeaderFormat, metadata.Language) switch
    {
        (MapHeaderFormat.DiamondPearl, RomLanguage.Japanese) => 324,
        (MapHeaderFormat.DiamondPearl, _) => 326,
        (MapHeaderFormat.Platinum, RomLanguage.Japanese) => 369,
        (MapHeaderFormat.Platinum, _) => 370,
        (MapHeaderFormat.HeartGoldSoulSilver, RomLanguage.Japanese) => 198,
        (MapHeaderFormat.HeartGoldSoulSilver, _) => 200,
        _ => 0
    };

    /// <summary>Every place the plaintext scripts start trade <paramref name="tradeId"/>: InitTrade
    /// (StartNpcTrade in the decompilations) is the command in all three games' databases.</summary>
    public static List<TradeUse> FindUses(RomProject project, RomGameMetadata metadata, int tradeId)
    {
        var uses = new List<TradeUse>();
        if (!Directory.Exists(project.ExpandedScriptsDirectory)) return uses;

        IMapHeaderStore? headers = MapHeaderStoreFactory.Create(project, metadata);
        List<(int Id, MapHeaderRecord Header)>? allHeaders = null;
        IReadOnlyList<HeaderOption>? locations = null;
        foreach (string path in Directory.GetFiles(project.ExpandedScriptsDirectory, "*.script").Order(StringComparer.Ordinal))
        {
            if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out int scriptFile)) continue;
            string section = "";
            foreach (string raw in File.ReadLines(path))
            {
                string line = raw.Trim();
                if (line.EndsWith(':') && (line.StartsWith("Script ", StringComparison.Ordinal) || line.StartsWith("Function ", StringComparison.Ordinal) || line.StartsWith("Action ", StringComparison.Ordinal)))
                {
                    section = line[..^1];
                    continue;
                }

                string[] parts = line.Split((char[])[' ', '\t'], StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length < 2 || parts[0] is not ("InitTrade" or "StartNpcTrade") || ParseNumber(parts[1]) != tradeId) continue;

                if (headers is not null && allHeaders is null)
                {
                    allHeaders = Enumerable.Range(0, headers.Count).Select(id => (id, headers.Load((ushort)id))).ToList();
                    locations = project.GetLocationNames(metadata);
                }

                var maps = (allHeaders ?? [])
                    .Where(h => h.Header.ScriptFileId == scriptFile)
                    .Select(h => (h.Id, $"{project.GetInternalName((ushort)h.Id)} ({locations?.FirstOrDefault(option => option.Id == h.Header.LocationNameId)?.Label ?? "unknown location"})"))
                    .ToList();
                uses.Add(new TradeUse(scriptFile, section, maps));
            }
        }

        return uses;
    }

    private static int? ParseNumber(string text) =>
        text.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? int.TryParse(text[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int hex) ? hex : null
            : int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value) ? value : null;
}
