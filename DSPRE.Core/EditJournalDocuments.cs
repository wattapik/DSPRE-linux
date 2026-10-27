using System.Text.Json;
using System.Text.Json.Nodes;

namespace DSPRE.Core;

/// <summary>Holder so the two permission grids of one map can be journaled as one document.</summary>
public sealed class MapPermissionsDocument
{
    public byte[,] Types { get; set; } = new byte[0, 0];
    public byte[,] Collisions { get; set; } = new byte[0, 0];
}

/// <summary>Every document kind the journal knows how to diff and replay, keyed by the name used in its
/// function calls (SetMapHeader, AddMapEvent, RemoveMapBuilding...).</summary>
public static class EditJournalDocuments
{
    public static JsonObject Keys(params (string Name, JsonNode? Value)[] keys)
    {
        var result = new JsonObject();
        foreach ((string name, JsonNode? value) in keys) result[name] = value;
        return result;
    }

    // Deserialize rather than GetValue: keys built in code wrap their CLR type (ushort header IDs...),
    // which GetValue<int> refuses to convert.
    private static int Int(JsonObject keys, string name) =>
        keys[name]?.Deserialize<int>() ?? throw new FormatException($"Missing key '{name}'.");

    private static bool Bool(JsonObject keys, string name) => keys[name]?.Deserialize<bool>() ?? false;

    private static TrainerGraphicSide GraphicSide(JsonObject keys) => keys["side"]?.Deserialize<string>() == "Back" ? TrainerGraphicSide.Back : TrainerGraphicSide.Front;

    private static bool IsHgss(JournalContext context) => context.Metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;

    public static readonly JournalDocument MapHeader = new()
    {
        Name = "MapHeader",
        RootType = typeof(MapHeaderRecord),
        Load = (c, k) => RequireHeaderStore(c).Load((ushort)Int(k, "header")),
        Save = (c, k, v) => RequireHeaderStore(c).Save((MapHeaderRecord)v)
    };

    public static readonly JournalDocument HeaderInternalName = new()
    {
        Name = "HeaderInternalName",
        RootType = typeof(string),
        Load = (c, k) => c.Project.GetInternalName((ushort)Int(k, "header")),
        Save = (c, k, v) => c.Project.SaveInternalName((ushort)Int(k, "header"), (string)v)
    };

    public static readonly JournalDocument Matrix = new()
    {
        Name = "Matrix",
        RootType = typeof(MatrixDocument),
        Load = (c, k) => MatrixService.Load(c.Project, Int(k, "matrix")),
        Save = (c, k, v) => MatrixService.Save(c.Project, (MatrixDocument)v)
    };

    public static readonly JournalDocument MapBuilding = new()
    {
        Name = "MapBuilding",
        RootType = typeof(List<BuildingRecord>),
        Load = (c, k) => MapBuildingService.Load(c.Project, Int(k, "map")),
        Save = (c, k, v) => MapBuildingService.Save(c.Project, Int(k, "map"), (List<BuildingRecord>)v)
    };

    public static readonly JournalDocument MapPermissions = new()
    {
        Name = "MapPermissions",
        RootType = typeof(MapPermissionsDocument),
        Load = (c, k) =>
        {
            (byte[,] types, byte[,] collisions) = MapPermissionsService.Load(c.Project, Int(k, "map"));
            return new MapPermissionsDocument { Types = types, Collisions = collisions };
        },
        Save = (c, k, v) =>
        {
            var permissions = (MapPermissionsDocument)v;
            MapPermissionsService.Save(c.Project, Int(k, "map"), permissions.Types, permissions.Collisions);
        }
    };

    public static readonly JournalDocument MapEvent = new()
    {
        Name = "MapEvent",
        RootType = typeof(MapEventFile),
        Load = (c, k) => MapEventService.Load(c.Project, Int(k, "eventFile")),
        Save = (c, k, v) => MapEventService.Save(c.Project, (MapEventFile)v)
    };

    public static readonly JournalDocument AreaData = new()
    {
        Name = "AreaData",
        RootType = typeof(AreaDataRecord),
        Load = (c, k) => AreaDataService.Load(c.Project, Int(k, "areaData"), IsHgss(c)),
        Save = (c, k, v) => AreaDataService.Save(c.Project, (AreaDataRecord)v, IsHgss(c))
    };

    public static readonly JournalDocument WildEncounters = new()
    {
        Name = "WildEncounters",
        RootType = typeof(WildEncounterFile),
        Load = (c, k) => WildEncounterService.Load(c.Project, Int(k, "encounterFile")),
        Save = (c, k, v) => WildEncounterService.Save(c.Project, (WildEncounterFile)v)
    };

    public static readonly JournalDocument TextArchive = new()
    {
        Name = "TextArchive",
        RootType = typeof(TextArchiveData),
        Load = (c, k) => TextArchiveService.Load(c.Project, Int(k, "archive"), c.Metadata),
        Save = (c, k, v) => TextArchiveService.Save(c.Project, (TextArchiveData)v)
    };

    // Trainer names live in a text archive (journaled as TextArchive), not in the trainer files, so the
    // name passed to Load is irrelevant to the diff.
    public static readonly JournalDocument Trainer = new()
    {
        Name = "Trainer",
        RootType = typeof(TrainerRecord),
        Load = (c, k) => TrainerService.Load(c.Project, Int(k, "trainer"), c.Metadata, string.Empty),
        Save = (c, k, v) => TrainerService.Save(c.Project, (TrainerRecord)v, c.Metadata)
    };

    public static readonly JournalDocument PersonalData = new()
    {
        Name = "PersonalData",
        RootType = typeof(PersonalDataRecord),
        Load = (c, k) => PersonalDataService.Load(c.Project, Int(k, "species")),
        Save = (c, k, v) => PersonalDataService.Save(c.Project, Int(k, "species"), (PersonalDataRecord)v)
    };

    public static readonly JournalDocument HatchResult = new()
    {
        Name = "HatchResult",
        RootType = typeof(int),
        Load = (c, k) => PersonalDataService.GetHatchResult(c.Project, Int(k, "species")),
        Save = (c, k, v) => PersonalDataService.SetHatchResult(c.Project, Int(k, "species"), (int)v)
    };

    public static readonly JournalDocument Learnset = new()
    {
        Name = "Learnset",
        RootType = typeof(LearnsetFile),
        Load = (c, k) => LearnsetService.Load(c.Project, Int(k, "learnset")),
        Save = (c, k, v) => LearnsetService.Save(c.Project, (LearnsetFile)v)
    };

    public static readonly JournalDocument Evolution = new()
    {
        Name = "Evolution",
        RootType = typeof(EvolutionFile),
        Load = (c, k) => EvolutionService.Load(c.Project, Int(k, "evolution")),
        Save = (c, k, v) => EvolutionService.Save(c.Project, (EvolutionFile)v)
    };

    public static readonly JournalDocument MoveData = new()
    {
        Name = "MoveData",
        RootType = typeof(MoveRecord),
        Load = (c, k) => MoveDataService.Load(c.Project, Int(k, "move")),
        Save = (c, k, v) => MoveDataService.Save(c.Project, Int(k, "move"), (MoveRecord)v)
    };

    public static readonly JournalDocument ItemData = new()
    {
        Name = "ItemData",
        RootType = typeof(ItemRecord),
        Load = (c, k) => ItemDataService.Load(c.Project, Int(k, "itemData")),
        Save = (c, k, v) => ItemDataService.Save(c.Project, Int(k, "itemData"), (ItemRecord)v)
    };

    public static readonly JournalDocument ItemTableEntry = new()
    {
        Name = "ItemTableEntry",
        RootType = typeof(ItemTableEntry),
        Load = (c, k) => ItemDataService.ReadTableEntry(c.Project, c.Metadata, Int(k, "item")),
        Save = (c, k, v) => ItemDataService.WriteTableEntry(c.Project, c.Metadata, Int(k, "item"), (ItemTableEntry)v)
    };

    public static readonly JournalDocument Trade = new()
    {
        Name = "Trade",
        RootType = typeof(TradeRecord),
        Load = (c, k) => TradeDataService.Load(c.Project, Int(k, "trade"), IsHgss(c)),
        Save = (c, k, v) => TradeDataService.Save(c.Project, Int(k, "trade"), (TradeRecord)v, IsHgss(c))
    };

    public static readonly JournalDocument FlyWarp = new()
    {
        Name = "FlyWarp",
        RootType = typeof(List<FlyWarpEntry>),
        Load = (c, k) => FlyWarpService.Load(c.Project, c.Metadata),
        Save = (c, k, v) => FlyWarpService.Save(c.Project, c.Metadata, (List<FlyWarpEntry>)v)
    };

    public static readonly JournalDocument HoneyTree = new()
    {
        Name = "HoneyTree",
        RootType = typeof(List<HoneyTreeGroup>),
        Load = (c, k) => SpecialEncounterService.LoadHoneyTree(c.Project, c.Metadata),
        Save = (c, k, v) => SpecialEncounterService.SaveHoneyTree(c.Project, c.Metadata, (List<HoneyTreeGroup>)v)
    };

    public static readonly JournalDocument GreatMarsh = new()
    {
        Name = "GreatMarsh",
        RootType = typeof(List<GreatMarshGroup>),
        Load = (c, k) => SpecialEncounterService.LoadGreatMarsh(c.Project),
        Save = (c, k, v) => SpecialEncounterService.SaveGreatMarsh(c.Project, (List<GreatMarshGroup>)v)
    };

    public static readonly JournalDocument BugContest = new()
    {
        Name = "BugContest",
        RootType = typeof(List<BugContestSet>),
        Load = (c, k) => SpecialEncounterService.LoadBugContest(c.Project),
        Save = (c, k, v) => SpecialEncounterService.SaveBugContest(c.Project, (List<BugContestSet>)v)
    };

    public static readonly JournalDocument Headbutt = new()
    {
        Name = "Headbutt",
        RootType = typeof(HeadbuttEncounterFile),
        Load = (c, k) => SpecialEncounterService.LoadHeadbutt(c.Project, Int(k, "headbutt")),
        Save = (c, k, v) => SpecialEncounterService.SaveHeadbutt(c.Project, (HeadbuttEncounterFile)v)
    };

    public static readonly JournalDocument SafariZone = new()
    {
        Name = "SafariZone",
        RootType = typeof(SafariZoneEncounterFile),
        Load = (c, k) => SpecialEncounterService.LoadSafariZone(c.Project, Int(k, "safariZone")),
        Save = (c, k, v) => SpecialEncounterService.SaveSafariZone(c.Project, (SafariZoneEncounterFile)v)
    };

    // Only the camera records are editable; the table's layout/offset come from the game version.
    public static readonly JournalDocument Camera = new()
    {
        Name = "Camera",
        RootType = typeof(List<GameCameraRecord>),
        Load = (c, k) => CameraService.Load(c.Project, c.Metadata).Cameras,
        Save = (c, k, v) =>
        {
            CameraTable table = CameraService.Load(c.Project, c.Metadata);
            CameraService.Save(c.Project, table with { Cameras = (List<GameCameraRecord>)v });
        }
    };

    public static readonly JournalDocument SpawnSettings = new()
    {
        Name = "SpawnSettings",
        RootType = typeof(SpawnSettings),
        Load = (c, k) => SpawnSettingsService.Load(c.Project, c.Metadata),
        Save = (c, k, v) => SpawnSettingsService.Save(c.Project, c.Metadata, (SpawnSettings)v)
    };

    public static readonly JournalDocument ConditionalMusic = new()
    {
        Name = "ConditionalMusic",
        RootType = typeof(List<ConditionalMusicEntry>),
        Load = (c, k) => BattleTableService.LoadConditionalMusic(c.Project, c.Metadata),
        Save = (c, k, v) => BattleTableService.SaveConditionalMusic(c.Project, c.Metadata, (List<ConditionalMusicEntry>)v)
    };

    public static readonly JournalDocument BattleEffectCombos = new()
    {
        Name = "BattleEffectCombos",
        RootType = typeof(List<BattleEffectCombo>),
        Load = (c, k) => BattleTableService.LoadEffectCombos(c.Project, c.Metadata),
        Save = (c, k, v) => BattleTableService.SaveEffectCombos(c.Project, c.Metadata, (List<BattleEffectCombo>)v)
    };

    public static readonly JournalDocument VsTrainerEffects = new()
    {
        Name = "VsTrainerEffects",
        RootType = typeof(List<BattleEffectLink>),
        Load = (c, k) => BattleTableService.LoadLinks(c.Project, c.Metadata, BattleTableKind.VsTrainer),
        Save = (c, k, v) => BattleTableService.SaveLinks(c.Project, c.Metadata, BattleTableKind.VsTrainer, (List<BattleEffectLink>)v)
    };

    public static readonly JournalDocument VsPokemonEffects = new()
    {
        Name = "VsPokemonEffects",
        RootType = typeof(List<BattleEffectLink>),
        Load = (c, k) => BattleTableService.LoadLinks(c.Project, c.Metadata, BattleTableKind.VsPokemon),
        Save = (c, k, v) => BattleTableService.SaveLinks(c.Project, c.Metadata, BattleTableKind.VsPokemon, (List<BattleEffectLink>)v)
    };

    public static readonly JournalDocument MachineMoves = new()
    {
        Name = "MachineMoves",
        RootType = typeof(List<MachineEntry>),
        Load = (c, k) => MachineMoveService.Load(c.Project, c.Metadata),
        Save = (c, k, v) => MachineMoveService.Save(c.Project, c.Metadata, (List<MachineEntry>)v)
    };

    public static readonly JournalDocument PickupTable = new()
    {
        Name = "PickupTable",
        RootType = typeof(PickupTable),
        Load = (c, k) => PickupTableService.Load(c.Project, c.Metadata),
        Save = (c, k, v) => PickupTableService.Save(c.Project, c.Metadata, (PickupTable)v)
    };

    // Only the entries are editable; the capacity is fixed by the ROM.
    public static readonly JournalDocument HiddenItems = new()
    {
        Name = "HiddenItems",
        RootType = typeof(List<HiddenItemEntry>),
        Load = (c, k) => HiddenItemService.Load(c.Project, c.Metadata).Entries,
        Save = (c, k, v) => HiddenItemService.Save(c.Project, c.Metadata, (List<HiddenItemEntry>)v)
    };

    public static readonly JournalDocument EggMoves = new()
    {
        Name = "EggMoves",
        RootType = typeof(List<EggMoveEntry>),
        Load = (c, k) => EggMoveService.Load(c.Project, c.Metadata),
        Save = (c, k, v) => EggMoveService.Save(c.Project, c.Metadata, (List<EggMoveEntry>)v)
    };

    public static readonly JournalDocument OverworldRenderState = new()
    {
        Name = "OverworldRenderState",
        RootType = typeof(OverworldRenderState),
        Load = (c, k) => OverworldSpriteService.LoadRenderState(c.Project, c.Metadata, (uint)Int(k, "entry"))
            ?? throw new InvalidDataException($"Overworld entry {Int(k, "entry")} has no render-state row."),
        Save = (c, k, v) => OverworldSpriteService.SaveRenderState(c.Project, c.Metadata, (uint)Int(k, "entry"), (OverworldRenderState)v)
    };

    public static readonly JournalDocument LevelScript = new()
    {
        Name = "LevelScript",
        RootType = typeof(LevelScriptFile),
        Load = (c, k) => LevelScriptService.Load(c.Project, Int(k, "levelScript")),
        Save = (c, k, v) => LevelScriptService.Save(c.Project, (LevelScriptFile)v, Bool(k, "padding"))
    };

    public static readonly JournalDocument BattlePalette = new()
    {
        Name = "BattlePalette",
        RootType = typeof(RgbColor[]),
        Load = (c, k) => BattleSpriteService.LoadBattlePalette(c.Project, Int(k, "species"), Bool(k, "shiny"))
            ?? throw new InvalidDataException("Battle palette is missing or unreadable."),
        Save = (c, k, v) => BattleSpriteService.SaveBattlePalette(c.Project, Int(k, "species"), Bool(k, "shiny"), (RgbColor[])v)
    };

    public static readonly JournalDocument IconPaletteChoice = new()
    {
        Name = "IconPaletteChoice",
        RootType = typeof(int),
        Load = (c, k) => PokemonIconService.GetPalette(c.Project, c.Metadata, Int(k, "icon")),
        Save = (c, k, v) => PokemonIconService.SetPalette(c.Project, c.Metadata, Int(k, "icon"), (int)v)
    };

    public static readonly JournalDocument IconSharedPalette = new()
    {
        Name = "IconSharedPalette",
        RootType = typeof(RgbColor[]),
        Load = (c, k) => PokemonIconService.LoadPalettes(c.Project)[Int(k, "palette")],
        Save = (c, k, v) => PokemonIconService.SaveSharedPalette(c.Project, Int(k, "palette"), (RgbColor[])v)
    };

    public static readonly JournalDocument SpriteAnimation = new()
    {
        Name = "SpriteAnimation",
        RootType = typeof(PokemonSpriteAnimation),
        Load = (c, k) => SpriteAnimationService.Load(c.Project, c.Metadata, Int(k, "species")),
        Save = (c, k, v) => SpriteAnimationService.Save(c.Project, c.Metadata, Int(k, "species"), (PokemonSpriteAnimation)v)
    };

    public static readonly JournalDocument TrainerAnimation = new()
    {
        Name = "TrainerAnimation",
        RootType = typeof(TrainerAnimation),
        Load = (c, k) => TrainerAnimationService.Load(c.Project, Int(k, "trainerClass"), GraphicSide(k)),
        Save = (c, k, v) => TrainerAnimationService.Save(c.Project, Int(k, "trainerClass"), (TrainerAnimation)v, GraphicSide(k))
    };

    public static readonly JournalDocument RegionalDex = new()
    {
        Name = "RegionalDex",
        RootType = typeof(List<int>),
        Load = (c, k) => PokedexService.LoadRegionalDex(c.Project, c.Metadata).Order,
        Save = (c, k, v) => PokedexService.SaveRegionalDex(c.Project, c.Metadata, (List<int>)v)
    };

    public static readonly JournalDocument TrainerMessages = new()
    {
        Name = "TrainerMessages",
        RootType = typeof(TrainerMessageTable),
        Load = (c, k) => TrainerMessageService.Load(c.Project, c.Metadata),
        Save = (c, k, v) => TrainerMessageService.Save(c.Project, c.Metadata, (TrainerMessageTable)v)
    };

    public static readonly JournalDocument Footprints = new()
    {
        Name = "Footprints",
        RootType = typeof(FootprintSet),
        Load = (c, k) => FootprintService.Load(c.Project),
        Save = (c, k, v) => FootprintService.Save(c.Project, (FootprintSet)v)
    };

    public static readonly JournalDocument PokemonShadows = new()
    {
        Name = "PokemonShadows",
        RootType = typeof(PokemonShadows),
        Load = (c, k) => PokemonShadowService.Load(c.Project),
        Save = (c, k, v) => PokemonShadowService.Save(c.Project, (PokemonShadows)v)
    };

    public static readonly JournalDocument PokedexAreas = new()
    {
        Name = "PokedexAreas",
        RootType = typeof(PokedexAreas),
        Load = (c, k) => PokedexAreaService.Load(c.Project),
        Save = (c, k, v) => PokedexAreaService.Save(c.Project, (PokedexAreas)v)
    };

    public static readonly JournalDocument PokedexAreaMap = new()
    {
        Name = "PokedexAreaMap",
        RootType = typeof(PokedexAreaMap),
        Load = (c, k) => PokedexMapService.Load(c.Project),
        Save = (c, k, v) => PokedexMapService.Save(c.Project, (PokedexAreaMap)v)
    };

    public static readonly JournalDocument TownMapPicture = new()
    {
        Name = "TownMapPicture",
        RootType = typeof(TownMapPicture),
        Load = (c, k) => TownMapPictureService.Load(c.Project, TownView(k)),
        Save = (c, k, v) => TownMapPictureService.Save(c.Project, (TownMapPicture)v, TownView(k))
    };

    public static readonly JournalDocument GrowthTables = new()
    {
        Name = "GrowthTables",
        RootType = typeof(GrowthTables),
        Load = (c, k) => GrowthTableService.Load(c.Project),
        Save = (c, k, v) => GrowthTableService.Save(c.Project, (GrowthTables)v)
    };

    public static readonly JournalDocument FrontierSets = new()
    {
        Name = "FrontierSets",
        RootType = typeof(FrontierPokemonSets),
        Load = (c, k) => FrontierService.LoadSets(c.Project, Facility(k)),
        Save = (c, k, v) => FrontierService.SaveSets(c.Project, Facility(k), (FrontierPokemonSets)v)
    };

    public static readonly JournalDocument FrontierTrainers = new()
    {
        Name = "FrontierTrainers",
        RootType = typeof(FrontierTrainers),
        Load = (c, k) => FrontierService.LoadTrainers(c.Project),
        Save = (c, k, v) => FrontierService.SaveTrainers(c.Project, (FrontierTrainers)v)
    };

    private static TownMapView TownView(JsonObject keys) => keys["view"]?.Deserialize<string>() == "Terrain" ? TownMapView.Terrain : TownMapView.Routes;

    private static FrontierFacility Facility(JsonObject keys) => Enum.Parse<FrontierFacility>(keys["facility"]?.Deserialize<string>() ?? nameof(FrontierFacility.Tower));

    public static readonly IReadOnlyList<JournalDocument> All =
    [
        MapHeader, HeaderInternalName, Matrix, MapBuilding, MapPermissions, MapEvent, AreaData, WildEncounters,
        TextArchive, Trainer, PersonalData, HatchResult, Learnset, Evolution, MoveData, ItemData, ItemTableEntry,
        Trade, FlyWarp, HoneyTree, GreatMarsh, BugContest, Headbutt, SafariZone, Camera, SpawnSettings, ConditionalMusic, BattleEffectCombos, VsTrainerEffects, VsPokemonEffects, MachineMoves, PickupTable, HiddenItems, EggMoves, OverworldRenderState, LevelScript, BattlePalette, RegionalDex, IconPaletteChoice, IconSharedPalette, SpriteAnimation, TrainerAnimation, TrainerMessages, GrowthTables, FrontierSets, FrontierTrainers, Footprints, PokemonShadows, PokedexAreas, PokedexAreaMap, TownMapPicture
    ];

    private static IMapHeaderStore RequireHeaderStore(JournalContext context) =>
        MapHeaderStoreFactory.Create(context.Project, context.Metadata)
            ?? throw new InvalidDataException("This project has no editable header table.");
}
