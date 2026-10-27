namespace DSPRE.Core;

using System.Text;
using System.Text.Json;
using YamlDotNet.RepresentationModel;

public enum RomProjectFormat
{
    DsRom,
    Ndstool
}

public sealed class RomProject
{
    private RomProject(string rootPath, RomProjectFormat format)
    {
        RootPath = rootPath;
        Format = format;
    }

    public string RootPath { get; }
    public RomProjectFormat Format { get; }
    public string FilesPath => Path.Combine(RootPath, Format == RomProjectFormat.DsRom ? "files" : "data");
    public string Arm9Path => Path.Combine(RootPath, Format == RomProjectFormat.DsRom ? "arm9" : string.Empty, Format == RomProjectFormat.DsRom ? "arm9.bin" : "arm9.bin");
    public string InternalNamesPath => Path.Combine(FilesPath, "fielddata", "maptable", "mapname.bin");
    /// <summary>The NARC the Dynamic Header Allocation patch makes the game load headers from (legacy
    /// RomInfo's DirNames.dynamicHeaders): an unused debug NARC on Platinum, a/0/5/0 on HGSS.</summary>
    public string DynamicHeadersArchivePath(RomGameMetadata metadata) => metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver
        ? Path.Combine(FilesPath, "a", "0", "5", "0")
        : Path.Combine(FilesPath, "debug", "cb_edit", "d_test.narc");
    public string DynamicHeadersDirectory => Path.Combine(RootPath, "unpacked", "dynamicHeaders");
    public string SyntheticOverlayDirectory => Path.Combine(RootPath, "unpacked", "synthOverlay");
    public string SyntheticOverlayArchivePath(RomGameMetadata metadata) => metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver
        ? Path.Combine(FilesPath, "a", "0", "2", "8")
        : Path.Combine(FilesPath, "data", "weather_sys.narc");
    public string MatricesArchivePath => Path.Combine(FilesPath, "fielddata", "mapmatrix", "map_matrix.narc");
    public string MatricesDirectory => Path.Combine(RootPath, "unpacked", "matrices");
    public string HgssMatricesArchivePath => Path.Combine(FilesPath, "a", "0", "4", "1");
    public string ScriptsDirectory => Path.Combine(RootPath, "unpacked", "scripts");
    public string ExpandedScriptsDirectory => Path.Combine(RootPath, "expanded", "scripts");
    public string EncountersDirectory => Path.Combine(RootPath, "unpacked", "encounters");
    public string ExtendedEncountersDirectory => Path.Combine(RootPath, "unpacked", "encounterExtended");
    public string SafariZoneDirectory => Path.Combine(RootPath, "unpacked", "safariZone");
    public string HeadbuttDirectory => Path.Combine(RootPath, "unpacked", "headbutt");
    public string MapsDirectory => Path.Combine(RootPath, "unpacked", "maps");
    public string MapTexturesDirectory => Path.Combine(RootPath, "unpacked", "mapTextures");
    public string BuildingTexturesDirectory => Path.Combine(RootPath, "unpacked", "buildingTextures");
    public string BuildingConfigsDirectory => Path.Combine(RootPath, "unpacked", "buildingConfigFiles");
    public string ExteriorBuildingModelsDirectory => Path.Combine(RootPath, "unpacked", "exteriorBuildingModels");
    public string InteriorBuildingModelsDirectory => Path.Combine(RootPath, "unpacked", "interiorBuildingModels");
    public string AreaDataDirectory => Path.Combine(RootPath, "unpacked", "areaData");
    public string EventsDirectory => Path.Combine(RootPath, "unpacked", "events");
    public string MapsArchivePath => Path.Combine(FilesPath, "fielddata", "land_data", "land_data.narc");
    public string TrainerPropertiesDirectory => Path.Combine(RootPath, "unpacked", "trainerProperties");
    public string TrainerPartyDirectory => Path.Combine(RootPath, "unpacked", "trainerParty");
    public string MonIconsDirectory => Path.Combine(RootPath, "unpacked", "monIcons");
    public string TrainerGraphicsDirectory => Path.Combine(RootPath, "unpacked", "trainerGraphics");
    public string TrainerBackGraphicsDirectory => Path.Combine(RootPath, "unpacked", "trainerBackGraphics");
    public string TrainerGraphicsDirectoryFor(TrainerGraphicSide side) => side == TrainerGraphicSide.Back ? TrainerBackGraphicsDirectory : TrainerGraphicsDirectory;
    public string PersonalDataDirectory => Path.Combine(RootPath, "unpacked", "personalPokeData");
    public string MoveDataDirectory => Path.Combine(RootPath, "unpacked", "moveData");
    public string ItemDataDirectory => Path.Combine(RootPath, "unpacked", "itemData");
    public string ItemIconsDirectory => Path.Combine(RootPath, "unpacked", "itemIcons");
    public string TradeDataDirectory => Path.Combine(RootPath, "unpacked", "tradeData");
    public string LearnsetsDirectory => Path.Combine(RootPath, "unpacked", "learnsets");
    public string EvolutionsDirectory => Path.Combine(RootPath, "unpacked", "evolutions");
    public string EggMovesDirectory => Path.Combine(RootPath, "unpacked", "eggMoves");
    public string OverworldSpritesDirectory => Path.Combine(RootPath, "unpacked", "OWSprites");
    public string PokemonBattleSpritesDirectory => Path.Combine(RootPath, "unpacked", "pokemonBattleSprites");
    public string OtherPokemonBattleSpritesDirectory => Path.Combine(RootPath, "unpacked", "otherPokemonBattleSprites");

    /// <summary>Not actually a NARC despite the ".narc" extension in its filename - a flat binary table
    /// (2 bytes/species) living directly in the ROM filesystem at the same path for every game version
    /// and language, matching the legacy editor's direct FileStream access.</summary>
    public string HatchResultPath => Path.Combine(FilesPath, "poketool", "personal", "pms.narc");

    public bool TryGetGameMetadata(out RomGameMetadata? metadata)
    {
        metadata = null;
        if (!TryReadGameCode(out string? gameCode))
        {
            return false;
        }

        return RomGameMetadata.TryResolve(gameCode, out metadata);
    }

    public int GetHeaderCount()
    {
        return File.Exists(InternalNamesPath)
            ? (int)(new FileInfo(InternalNamesPath).Length / 16)
            : 0;
    }

    public string GetInternalName(ushort id)
    {
        if (!File.Exists(InternalNamesPath))
        {
            return string.Empty;
        }

        using FileStream stream = File.OpenRead(InternalNamesPath);
        long offset = id * 16L;
        if (offset < 0 || offset + 16 > stream.Length)
        {
            return string.Empty;
        }

        stream.Position = offset;
        byte[] data = new byte[16];
        stream.ReadExactly(data);
        return Encoding.UTF8.GetString(data).TrimEnd('\0', ' ');
    }

    public void SaveInternalName(ushort id, string name) =>
        EditJournal.Track(this, EditJournalDocuments.HeaderInternalName, EditJournalDocuments.Keys(("header", id)), () => SaveInternalNameUntracked(id, name));

    private void SaveInternalNameUntracked(ushort id, string name)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(InternalNamesPath)!);
        byte[] data = new byte[16];
        byte[] encoded = Encoding.UTF8.GetBytes(name ?? string.Empty);
        Array.Copy(encoded, data, Math.Min(encoded.Length, data.Length));

        using FileStream stream = new FileStream(InternalNamesPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read);
        stream.Position = id * 16L;
        stream.Write(data);
    }

    public IReadOnlyList<HeaderOption> GetLocationNames(RomGameMetadata metadata)
    {
        int archiveId = LocationNameService.ArchiveId(metadata);
        string path = Path.Combine(RootPath, "expanded", "textArchives", $"{archiveId:D4}.json");
        if (!File.Exists(path))
        {
            return [];
        }

        using JsonDocument document = JsonDocument.Parse(File.ReadAllText(path));
        if (!document.RootElement.TryGetProperty("messages", out JsonElement messages) || messages.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var result = new List<HeaderOption>();
        foreach (JsonElement message in messages.EnumerateArray())
        {
            string value = string.Empty;
            string languageKey = metadata.Language switch
            {
                RomLanguage.English => "en_US",
                RomLanguage.Spanish => "es_ES",
                RomLanguage.Italian => "it_IT",
                RomLanguage.French => "fr_FR",
                RomLanguage.German => "de_DE",
                RomLanguage.Japanese => "ja_JP",
                _ => "en_US"
            };

            if (message.TryGetProperty(languageKey, out JsonElement localized))
            {
                value = localized.ValueKind == JsonValueKind.String ? localized.GetString() ?? string.Empty : localized.ToString();
            }
            else if (message.TryGetProperty("en_US", out JsonElement english))
            {
                value = english.ValueKind == JsonValueKind.String ? english.GetString() ?? string.Empty : english.ToString();
            }
            result.Add(new HeaderOption((ushort)result.Count, value.Replace("\n", " ").Trim()));
        }

        return result;
    }

    public IReadOnlyList<MapReference> GetMapsUsingHeader(ushort headerId, RomGameMetadata metadata)
    {
        if (!Directory.Exists(MatricesDirectory))
        {
            return [];
        }

        var result = new List<MapReference>();
        foreach (string path in Directory.GetFiles(MatricesDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using FileStream stream = File.OpenRead(path);
                using BinaryReader reader = new BinaryReader(stream);
                byte width = reader.ReadByte();
                byte height = reader.ReadByte();
                bool hasHeaders = reader.ReadBoolean();
                bool hasHeights = reader.ReadBoolean();
                byte nameLength = reader.ReadByte();
                reader.ReadBytes(nameLength);
                ushort[,] headers = new ushort[height, width];
                if (hasHeaders)
                {
                    for (int y = 0; y < height; y++)
                    {
                        for (int x = 0; x < width; x++)
                        {
                            headers[y, x] = reader.ReadUInt16();
                        }
                    }
                }

                if (hasHeights)
                {
                    reader.ReadBytes(width * height);
                }

                for (int y = 0; y < height; y++)
                {
                    for (int x = 0; x < width; x++)
                    {
                        ushort mapId = reader.ReadUInt16();
                        if (hasHeaders && headers[y, x] == headerId && !result.Any(map => map.MapId == mapId))
                        {
                            result.Add(new MapReference(mapId, GetInternalName(mapId)));
                        }
                    }
                }
            }
            catch (EndOfStreamException)
            {
                // Ignore incomplete matrix files; the original editor skips invalid data while loading.
            }
        }

        return result.OrderBy(map => map.MapId).ToArray();
    }

    private bool TryReadGameCode(out string? gameCode)
    {
        gameCode = null;
        try
        {
            if (Format == RomProjectFormat.DsRom)
            {
                string headerPath = Path.Combine(RootPath, "header.yaml");
                using var reader = new StreamReader(headerPath);
                var yaml = new YamlStream();
                yaml.Load(reader);
                var mapping = yaml.Documents[0].RootNode as YamlMappingNode;
                if (mapping is null)
                {
                    return false;
                }

                foreach (var entry in mapping.Children)
                {
                    if (entry.Key is not YamlScalarNode scalarKey ||
                        entry.Value is not YamlScalarNode scalarValue)
                    {
                        continue;
                    }

                    string key = scalarKey.Value?.Trim() ?? string.Empty;
                    if (key.Equals("gamecode", StringComparison.OrdinalIgnoreCase) ||
                        key.Equals("game_code", StringComparison.OrdinalIgnoreCase))
                    {
                        gameCode = scalarValue.Value?.Trim();
                        break;
                    }
                }

                return !string.IsNullOrWhiteSpace(gameCode);
            }

            string legacyHeaderPath = Path.Combine(RootPath, "header.bin");
            using var stream = File.OpenRead(legacyHeaderPath);
            stream.Position = 0xC;
            byte[] code = new byte[4];
            stream.ReadExactly(code);
            gameCode = Encoding.UTF8.GetString(code);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (YamlDotNet.Core.YamlException)
        {
            return false;
        }
    }

    public static bool TryOpen(string? path, out RomProject? project)
    {
        project = null;
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        string rootPath;
        try
        {
            rootPath = Path.GetFullPath(path);
        }
        catch (Exception) when (path is not null)
        {
            return false;
        }

        if (!Directory.Exists(rootPath))
        {
            return false;
        }

        if (File.Exists(Path.Combine(rootPath, "header.yaml")) &&
            File.Exists(Path.Combine(rootPath, "config.yaml")))
        {
            project = new RomProject(rootPath, RomProjectFormat.DsRom);
            return true;
        }

        if (File.Exists(Path.Combine(rootPath, "header.bin")))
        {
            project = new RomProject(rootPath, RomProjectFormat.Ndstool);
            return true;
        }

        return false;
    }
}
