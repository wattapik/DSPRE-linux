using Avalonia;
using DSPRE.Core;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform;
using Avalonia.Platform.Storage;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.Input;
using Avalonia.Controls.Presenters;
using Avalonia.Media.TextFormatting;
using Avalonia.VisualTree;
using System.Text.Json.Nodes;
using System.Collections.ObjectModel;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;

namespace DSPRE.Avalonia;

public partial class MainWindow : Window
{
    private IMapHeaderStore? headerStore;
    private MapHeaderRecord? selectedHeader;
    private RomProject? currentProject;
    private string? sourceRomPath;
    private readonly List<int> scriptFileIds = [];
    private bool loadingScript;
    private bool loadingText;
    private bool textDirty;
    private TextArchiveData? currentTextArchive;
    private TextArchiveData? currentScriptTextArchive;
    private bool updatingScriptText;
    private readonly List<int> textArchiveIds = [];
    private readonly List<int> wildEncounterIds = [];
    private readonly List<WildEncounterSelectorEntry> wildEncounterEntries = [];
    public ObservableCollection<WildEncounterRowView> GrassGroundEncounterRows { get; } = [];
    public ObservableCollection<WildEncounterRowView> WaterEncounterRows { get; } = [];
    private static readonly HashSet<string> WaterEncounterCategories = ["Surf", "Surf Swarm", "Old Rod", "Good Rod", "Super Rod", "Night Fishing", "Rod Swarm"];
    public ObservableCollection<string> WildPokemonNames { get; } = [];
    private WildEncounterFile? currentWildEncounter;
    private bool loadingWildEncounter;
    private MatrixDocument? currentMatrix;
    private bool loadingMatrix;
    private readonly List<MapViewEntry> mapEntries = [];
    private IReadOnlyDictionary<int, AreaDataRecord> mapAreaData = new Dictionary<int, AreaDataRecord>();
    private IReadOnlyDictionary<int, int> mapEventFileIds = new Dictionary<int, int>();
    private bool loadingMapEditor;
    private bool settingMapTexturePacks;
    // Bumped by every map load; a load that finds it has changed by the time its model is ready was
    // overtaken by a newer one and must not replace that one's scene.
    private int mapLoadGeneration;
    private static readonly SemaphoreSlim MapModelExportGate = new(1, 1);
    // The same map's buildings can be open in the Single Map Editor tab and as part of an Overworld/
    // Indoor composite at the same time (e.g. drilling into one map from a composite's building list,
    // or just switching tabs). All three editors share these two caches - keyed by map ID, loaded from
    // disk once per session - rather than each independently loading/mutating its own private copy, so
    // an edit made from any one of them is immediately visible from the others too, and saving from one
    // can never silently clobber unsaved edits sitting in another (see GetSharedMapBuildings).
    private readonly Dictionary<ushort, List<BuildingRecord>> sharedMapBuildings = [];
    // Snapshot of a map's buildings as of its last load/save, used to detect which buildings have
    // unsaved edits (see RefreshBuildingDirtyHighlight and friends) - deep-cloned so later mutations to
    // sharedMapBuildings' own records don't also change this snapshot out from under the comparison.
    private readonly Dictionary<ushort, List<BuildingRecord>> sharedMapSavedBuildings = [];

    /// <summary>The one shared, mutable building list for a map - loaded from disk the first time any
    /// editor asks for this map in the current session, and reused (not reloaded) after that so
    /// whichever editor(s) touch it next see the exact same in-memory state, edits included.</summary>
    private List<BuildingRecord> GetSharedMapBuildings(ushort mapId)
    {
        if (!sharedMapBuildings.TryGetValue(mapId, out List<BuildingRecord>? buildings))
        {
            buildings = currentProject is null ? [] : MapBuildingService.Load(currentProject, mapId).ToList();
            sharedMapBuildings[mapId] = buildings;
            sharedMapSavedBuildings[mapId] = buildings.Select(building => building.Snapshot()).ToList();
        }

        return buildings;
    }

    private List<BuildingRecord> currentBuildings = [];
    // Snapshot of currentBuildings as of the last load/save, used to detect which buildings have
    // unsaved edits (see RefreshBuildingDirtyHighlight) - deep-cloned so later mutations to
    // currentBuildings' own records don't also change this snapshot out from under the comparison.
    private List<BuildingRecord> savedBuildings = [];
    private List<(int Id, string Name)> allBuildingModels = [];
    private List<(int Id, string Name)> currentBuildingModels = [];
    private bool loadingBuildingEditor;
    private byte[,] currentPermissionTypes = new byte[MapPermissionsService.GridSize, MapPermissionsService.GridSize];
    private byte[,] currentPermissionCollisions = new byte[MapPermissionsService.GridSize, MapPermissionsService.GridSize];
    private bool permissionsShowType;
    private byte permissionsPaintValue;
    private byte permissionsTransparency = MapPermissionPalette.DefaultTransparency;
    private bool loadingPermissionsEditor;
    public ObservableCollection<TextMessageRow> TextMessages { get; } = [];
    private readonly List<HeaderListEntry> allHeaders = [];
    private readonly List<string> nsbtxPackPaths = [];
    private NsbtxTexturePack? currentNsbtxPack;
    private string? currentNsbtxPackPath;
    private bool loadingNsbtx;
    private readonly List<int> areaDataIds = [];
    // Which area data files exist and which headers name each one - the Texture Packs and Area Data
    // tabs both explain themselves in terms of these, so they are read once when the editor opens.
    private readonly Dictionary<int, AreaDataRecord> areaDataRecords = [];
    private readonly Dictionary<int, List<HeaderListEntry>> headersByAreaData = [];
    private readonly List<int> areaDataPreviewMapIds = [];
    private int areaDataPreviewRequest;
    private AreaDataRecord? currentAreaData;
    private bool loadingAreaData;
    private readonly List<int> levelScriptIds = [];
    private LevelScriptFile? currentLevelScript;
    private bool loadingLevelScript;
    private bool levelScriptDisplayInHex;
    public ObservableCollection<CameraRow> CameraRowItems { get; } = [];
    private CameraTable? currentCameraTable;
    public ObservableCollection<SpeciesSlotRow> HoneyTreeSlots { get; } = [];
    public ObservableCollection<SpeciesSlotRow> GreatMarshSlots { get; } = [];
    public ObservableCollection<BugContestSlotRow> BugContestSlots { get; } = [];
    public ObservableCollection<HeadbuttSlotRow> HeadbuttSlots { get; } = [];
    public ObservableCollection<SafariSlotRow> SafariZoneSlots { get; } = [];
    private List<HoneyTreeGroup>? honeyTreeGroups;
    private List<GreatMarshGroup>? greatMarshGroups;
    private List<BugContestSet>? bugContestSets;
    private HeadbuttEncounterFile? currentHeadbuttFile;
    private SafariZoneEncounterFile? currentSafariZoneFile;
    private readonly List<int> headbuttIds = [];
    private readonly List<int> safariZoneIds = [];
    private bool loadingSpecialEncounters;
    private readonly List<int> trainerIds = [];
    private readonly List<int> filteredTrainerIds = [];
    private readonly Dictionary<int, string> trainerNamesCache = [];
    private TrainerRecord? currentTrainer;
    private bool loadingTrainer;
    private bool showTrainerBallSealsAndForms;
    private bool showTrainerGenderAndAbility;
    public ObservableCollection<string> TrainerClassNames { get; } = [];
    public ObservableCollection<string> TrainerPokemonNames { get; } = [];
    public ObservableCollection<string> TrainerItemNames { get; } = [];
    public ObservableCollection<string> TrainerMoveNames { get; } = [];
    public ObservableCollection<PartyMemberRow> TrainerPartyRowItems { get; } = [];
    private NdsTileImage? trainerClassSpriteTiles;
    private RgbColor[][]? trainerClassSpritePalettes;
    private NdsCellBank? trainerClassSpriteCells;
    private int trainerClassSpriteFrameCount;
    private int trainerClassSpriteFrame;
    private DispatcherTimer? trainerClassSpriteTimer;
    public bool ShowTrainerBallSealsAndForms => showTrainerBallSealsAndForms;
    public bool ShowTrainerGenderAndAbility => showTrainerGenderAndAbility;

    private bool loadingPokeEditor;
    private int pokeEditorSpeciesId = -1;
    // The hatch result and battle sprites are saved with the species but kept outside its tracked
    // records: the shown hatch result as loaded or last saved, and whether a sprite was imported since.
    private int pokeSavedHatchResult = -1;
    private bool pokeSpritesUnsaved;
    private PersonalDataRecord? currentPersonalData;
    private LearnsetFile? currentLearnset;
    private EvolutionFile? currentEvolutionFile;
    private string[] pokeTypeNames = [];
    private string[] pokeAbilityNames = [];
    private string[] pokeItemNames = [];
    private string[] pokeMoveNames = [];
    private string[] pokeMachineLabels = [];
    private int pokeLearnsetEditingIndex = -1;
    private int pokeBaseSpeciesCount;
    private readonly byte[]?[] pokeSpriteIndices = new byte[4][];
    // The image each slot was last imported from (and which palette it was matched against), so the
    // save can journal the import by file rather than by pixel data.
    private readonly (string Path, bool Shiny)?[] pokeSpriteSources = new (string, bool)?[4];
    private RgbColor[]? pokeSpriteNormalPalette;
    private RgbColor[]? pokeSpriteShinyPalette;
    private bool pokeSpriteIsDiamondPearl;

    private bool loadingMoveEditor;
    private int currentMoveId = -1;
    private MoveRecord? currentMoveData;
    private string[] moveEditorTypeNames = [];
    private string[] moveEditorMoveNames = [];
    private string[] moveEditorDescriptions = [];
    // The open archives the names and descriptions are edited in (shared with the Text Editor), and
    // their text as last loaded or saved, to tell whether they need saving.
    private TextArchiveData? moveNamesArchive;
    private TextArchiveData? moveDescriptionsArchive;
    private List<string> savedMoveNames = [];
    private List<string> savedMoveDescriptions = [];

    private bool loadingItemEditor;
    private int currentItemId = -1;
    private ItemTableEntry currentItemTableEntry = new(0, 0, 0, 0);
    private ItemRecord? currentItemData;
    private string[] itemEditorNames = [];
    private TextArchiveData? itemNamesArchive;
    private List<string> savedItemNames = [];
    private string[] itemEditorDescriptions = [];
    private TextArchiveData? itemDescriptionsArchive;
    private List<string> savedItemDescriptions = [];

    private bool loadingTradeEditor;
    private int currentTradeId = -1;
    private TradeRecord? currentTradeData;
    private TextArchiveData? currentTradeArchive;
    private bool tradeEditorIsHgss;

    public ObservableCollection<string> FlyWarpHeaderNames { get; } = [];
    public ObservableCollection<string> TableEditorHeaderNames { get; } = [];
    public ObservableCollection<string> TableEditorTrainerClassNames { get; } = [];
    public ObservableCollection<string> TableEditorPokemonNames { get; } = [];
    public ObservableCollection<string> TableEditorComboNames { get; } = [];
    public ObservableCollection<string> ItemTableItemNames { get; } = [];

    public sealed class PickupItemRow(string label, List<ushort> items, int index)
    {
        public string Label => label;
        public int ItemIndex { get => items[index]; set { if (value >= 0) items[index] = (ushort)value; } }
    }

    public sealed class PickupWeightRow(string label, List<byte> thresholds, int index, Action changed)
    {
        public string Label => label;

        public int Threshold
        {
            get => thresholds[index];
            set
            {
                byte clamped = (byte)Math.Clamp(value, 0, 100);
                if (clamped == thresholds[index]) return;
                thresholds[index] = clamped;
                changed();
            }
        }
    }

    // A ComboBox reports -1 while its item list is being (re)filled; the setters below ignore that
    // so a transient empty selection never overwrites the stored ID.
    public sealed class TableComboRow(int index, BattleEffectCombo combo)
    {
        public string Label => $"Combo {index:D2}";
        public int VsGraphic { get => combo.VsGraphic; set => combo.VsGraphic = (ushort)Math.Clamp(value, 0, ushort.MaxValue); }
        public int BattleMusic { get => combo.BattleMusic; set => combo.BattleMusic = (ushort)Math.Clamp(value, 0, ushort.MaxValue); }
    }

    public sealed class TableLinkRow(int index, BattleEffectLink link, Func<int, IImage?> loadPicture) : System.ComponentModel.INotifyPropertyChanged
    {
        private IImage? picture;
        private bool pictureLoaded;

        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public string Label => $"Entry {index:D2}";

        /// <summary>The trainer class's sprite or the Pokemon's icon, decoded the first time the row is shown.</summary>
        public IImage? Picture
        {
            get
            {
                if (!pictureLoaded)
                {
                    pictureLoaded = true;
                    picture = loadPicture(link.SubjectId);
                }

                return picture;
            }
        }

        public int SubjectIndex
        {
            get => link.SubjectId;
            set
            {
                if (value < 0 || value == link.SubjectId) return;
                link.SubjectId = (ushort)value;
                pictureLoaded = false;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Picture)));
            }
        }

        public int ComboIndex { get => link.ComboId; set { if (value >= 0) link.ComboId = (ushort)value; } }
    }

    public sealed class TableConditionalMusicRow(int index, ConditionalMusicEntry entry)
    {
        public string Label => $"Entry {index:D2}";
        public int HeaderIndex { get => entry.HeaderId; set { if (value >= 0) entry.HeaderId = (ushort)value; } }
        public int Flag { get => entry.Flag; set => entry.Flag = (ushort)Math.Clamp(value, 0, ushort.MaxValue); }
        public int MusicId { get => entry.MusicId; set => entry.MusicId = (ushort)Math.Clamp(value, 0, ushort.MaxValue); }
    }
    public ObservableCollection<FlyWarpRow> FlyWarpRowItems { get; } = [];
    private List<FlyWarpEntry> flyWarpEntries = [];

    public sealed class FlyWarpRow(int index, FlyWarpEntry entry, bool isHgss, Func<int, string> locationOfHeader)
    {
        public int Index => index;
        public string SlotLabel => $"Slot {index} - {locationOfHeader(entry.HeaderIdFly)}";
        public bool IsHgss { get; } = isHgss;
        public bool IsDpPt => !isHgss;
        public int LocalMax => isHgss ? 255 : 65535;

        public int HeaderIdGameOver { get => entry.HeaderIdGameOver; set => entry.HeaderIdGameOver = (ushort)Math.Max(0, value); }
        public int LocalX { get => entry.LocalX; set => entry.LocalX = value; }
        public int LocalY { get => entry.LocalY; set => entry.LocalY = value; }
        public int HeaderIdFly { get => entry.HeaderIdFly; set => entry.HeaderIdFly = (ushort)Math.Max(0, value); }
        public int GlobalX { get => entry.GlobalX; set => entry.GlobalX = value; }
        public int GlobalY { get => entry.GlobalY; set => entry.GlobalY = value; }
        public bool IsTeleportPos { get => entry.IsTeleportPos; set => entry.IsTeleportPos = value; }
        public bool UnlockOnMapEntry { get => entry.UnlockOnMapEntry; set => entry.UnlockOnMapEntry = value; }
        public int UnlockId { get => entry.UnlockId; set => entry.UnlockId = (ushort)Math.Max(0, value); }
        public int FlagIdx { get => entry.FlagIdx; set => entry.FlagIdx = (byte)Math.Clamp(value, 0, 255); }
        public bool IsBlackoutSpawn { get => entry.IsBlackoutSpawn; set => entry.IsBlackoutSpawn = value; }
        public bool IsFlyPoint { get => entry.IsFlyPoint; set => entry.IsFlyPoint = value; }
        public int HeaderIdUnlockWarp { get => entry.HeaderIdUnlockWarp; set => entry.HeaderIdUnlockWarp = (ushort)Math.Max(0, value); }
        public int GlobalXUnlock { get => entry.GlobalXUnlock; set => entry.GlobalXUnlock = value; }
        public int GlobalYUnlock { get => entry.GlobalYUnlock; set => entry.GlobalYUnlock = value; }
    }
    public ObservableCollection<string> PokeEditorSpeciesNames { get; } = [];
    public ObservableCollection<string> EvolutionMethodNames { get; } = [];
    public ObservableCollection<EvolutionSlotRow> EvolutionSlotRows { get; } = [];

    /// <summary>Wraps one of an EvolutionFile's 7 fixed Core slots for data-binding: Method/Param/Target
    /// forward straight through, while DescriptionText/ParamEnabled/ParamMaximum/TargetEnabled are
    /// derived - ported from EvolutionsEditor.UpdateDescriptionLabel's method-&gt;meaning switch. Method
    /// changes need to refresh all four derived properties at once, hence RaiseAllChanged instead of a
    /// single-property setter (there is no natural "property changed" moment for a plain enum field).</summary>
    public sealed class EvolutionSlotRow(EvolutionSlot slot, string[] itemNames, string[] moveNames, string[] pokemonNames) : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public int MethodIndex
        {
            get => (int)slot.Method;
            set => slot.Method = (EvolutionMethod)value;
        }

        public int Param
        {
            get => slot.Param;
            set => slot.Param = (short)value;
        }

        public int Target
        {
            get => slot.Target;
            set => slot.Target = (short)value;
        }

        private static EvolutionParamMeaning MeaningFor(EvolutionMethod method) =>
            EvolutionService.ParamMeanings.GetValueOrDefault(method, EvolutionParamMeaning.Ignored);

        public string DescriptionText
        {
            get
            {
                if (slot.Method == EvolutionMethod.None) return string.Empty;
                return MeaningFor(slot.Method) switch
                {
                    EvolutionParamMeaning.FromLevel => "From Level",
                    EvolutionParamMeaning.ItemName => Param >= 0 && Param < itemNames.Length ? $"({itemNames[Param]})" : string.Empty,
                    EvolutionParamMeaning.MoveName => Param >= 0 && Param < moveNames.Length ? $"({moveNames[Param]})" : string.Empty,
                    EvolutionParamMeaning.PokemonName => Param >= 0 && Param < pokemonNames.Length ? $"({pokemonNames[Param]})" : string.Empty,
                    EvolutionParamMeaning.BeautyValue => "Beauty >=",
                    _ => string.Empty
                };
            }
        }

        public bool ParamEnabled => slot.Method != EvolutionMethod.None && MeaningFor(slot.Method) != EvolutionParamMeaning.Ignored;
        public bool TargetEnabled => slot.Method != EvolutionMethod.None;

        public int ParamMaximum => MeaningFor(slot.Method) switch
        {
            EvolutionParamMeaning.FromLevel => 100,
            EvolutionParamMeaning.ItemName => Math.Max(0, itemNames.Length - 1),
            EvolutionParamMeaning.MoveName => Math.Max(0, moveNames.Length - 1),
            EvolutionParamMeaning.PokemonName => Math.Max(0, pokemonNames.Length - 1),
            EvolutionParamMeaning.BeautyValue => 255,
            _ => 0
        };

        public void RaiseAllChanged() => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(string.Empty));
    }

    /// <summary>One party slot in the Trainer Editor's party grid. Species/HeldItem/Move1-4 are name-list
    /// indexes (index 0 is each list's "None" entry, matching the Wild Encounter editor's convention);
    /// -1 means "nothing selected". GenderIndex/AbilityIndex are simple 0=Default/1/2 selections that get
    /// translated to/from PartyPokemonRecord.GenderAndAbility's flag bits on save/load.</summary>
    public sealed class PartyMemberRow : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public required int Index { get; init; }
        public string SlotLabel => $"Slot {Index + 1}";
        public int Species { get; set; } = -1;
        public int Level { get; set; }
        public int Difficulty { get; set; }
        public int BallSeals { get; set; }
        public int Form { get; set; }
        public int GenderIndex { get; set; }
        public int AbilityIndex { get; set; }
        public int HeldItem { get; set; } = -1;
        public int Move1 { get; set; } = -1;
        public int Move2 { get; set; } = -1;
        public int Move3 { get; set; } = -1;
        public int Move4 { get; set; } = -1;

        private IImage? icon;
        /// <summary>The only property here that needs live UI updates outside a full row rebuild (the
        /// species icon, refreshed whenever the species combo changes), hence being the one property
        /// on this otherwise plain data row that raises PropertyChanged.</summary>
        public IImage? Icon
        {
            get => icon;
            set
            {
                icon = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon)));
            }
        }
    }

    private sealed record HeaderListEntry(ushort Id, string InternalName, ushort LocationId, string LocationName)
    {
        public override string ToString() => $"{Id:D3} - {InternalName} - [{LocationId:D3}] {LocationName}";
    }

    private sealed record HeaderLink(ushort Id, string InternalName, string LocationName, ushort TextArchiveId)
    {
        public override string ToString() => $"{Id:D3} - {InternalName} - {LocationName} (Text {TextArchiveId:D4})";
    }

    private sealed record WildEncounterSelectorEntry(int Id, string HeaderName)
    {
        public override string ToString() => $"{Id:D4} - {HeaderName}";
    }

    private sealed record TrainerSelectorEntry(int Id, string Name)
    {
        public override string ToString() => $"{Id:D3} - {Name}";
    }

    private sealed class NsbtxPaletteEntry
    {
        public int Index { get; init; }
        public string Name { get; init; } = string.Empty;
        public int Length { get; init; }
        public uint[] Colors { get; init; } = [];
    }

    private sealed class NsbtxTextureEntry
    {
        public int Index { get; init; }
        public string Name { get; init; } = string.Empty;
        public int Width { get; init; }
        public int Height { get; init; }
        public int Format { get; init; }
        public bool ColorZeroTransparent { get; init; }
        public int PaletteIndex { get; init; }
        public byte[] Source { get; init; } = [];
        public byte[] SpData { get; init; } = [];
    }

    private sealed class NsbtxTexturePack
    {
        public string FilePath { get; }
        public string FileName => Path.GetFileName(FilePath);
        public List<NsbtxTextureEntry> Textures { get; } = [];
        public List<NsbtxPaletteEntry> Palettes { get; } = [];

        private NsbtxTexturePack(string filePath)
        {
            FilePath = filePath;
        }

        public static NsbtxTexturePack? Load(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            {
                return null;
            }

            try
            {
                using FileStream stream = File.OpenRead(filePath);
                using BinaryReader reader = new BinaryReader(stream);
                if (reader.ReadChars(4).SequenceEqual("BTX0".ToCharArray()) == false)
                {
                    return null;
                }

                stream.Position = 0;
                using MemoryStream memory = new MemoryStream();
                using BinaryWriter writer = new BinaryWriter(memory);
                writer.Write(reader.ReadBytes((int)stream.Length));
                var pack = new NsbtxTexturePack(filePath);
                if (!TryParsePack(pack, memory.ToArray()))
                {
                    return null;
                }

                return pack;
            }
            catch
            {
                return null;
            }
        }

        private static bool TryParsePack(NsbtxTexturePack pack, byte[] data)
        {
            try
            {
                using MemoryStream stream = new MemoryStream(data);
                using BinaryReader reader = new BinaryReader(stream);
                if (new string(reader.ReadChars(4)) != "BTX0")
                {
                    return false;
                }

                reader.ReadUInt16();
                reader.ReadUInt16();
                int fileSize = reader.ReadInt32();
                short headerSize = reader.ReadInt16();
                short sectionCount = reader.ReadInt16();
                long[] sectionOffsets = new long[sectionCount];
                for (int i = 0; i < sectionCount; i++)
                {
                    sectionOffsets[i] = reader.ReadUInt32();
                }

                if (sectionCount <= 0)
                {
                    return false;
                }

                string sectionId = new string(reader.ReadChars(4));
                if (sectionId != "TEX0")
                {
                    return false;
                }

                int sectionSize = reader.ReadInt32();
                int textureUnknown = reader.ReadInt32();
                short textureDataSize = (short)(reader.ReadInt16() << 3);
                short textureDictionaryOffset = reader.ReadInt16();
                short textureUnknown2 = reader.ReadInt16();
                reader.ReadUInt16();
                int textureDataOffset = reader.ReadInt32();

                int compressedTextureUnknown = reader.ReadInt32();
                short compressedTextureDataSize = (short)(reader.ReadInt16() << 3);
                short compressedTextureDictionaryOffset = reader.ReadInt16();
                short compressedTextureUnknown2 = reader.ReadInt16();
                reader.ReadUInt16();
                int compressedTextureDataOffset = reader.ReadInt32();
                int compressedTexturePaletteIndex = reader.ReadInt32();

                int paletteUnknown = reader.ReadInt32();
                short paletteDataSize = (short)(reader.ReadInt16() << 3);
                short paletteUnknown2 = reader.ReadInt16();
                short paletteDictionaryOffset = reader.ReadInt16();
                reader.ReadUInt16();
                int paletteDataOffset = reader.ReadInt32();

                byte dummy = reader.ReadByte();
                byte textureCount = reader.ReadByte();
                short textureSectionSize = reader.ReadInt16();
                short textureUnknownBlockHeaderSize = reader.ReadInt16();
                short textureUnknownBlockSectionSize = reader.ReadInt16();
                int textureUnknownConstant = reader.ReadInt32();
                for (int i = 0; i < textureCount; i++)
                {
                    reader.ReadInt16();
                    reader.ReadInt16();
                }

                short textureInfoHeaderSize = reader.ReadInt16();
                short textureInfoDataSize = reader.ReadInt16();
                var textureInfos = new List<(short Offset, short Parameters, byte Width, byte Unknown1, byte Height, byte Unknown2)>();
                for (int i = 0; i < textureCount; i++)
                {
                    short textureOffset = (short)(reader.ReadInt16() << 3);
                    short parameters = reader.ReadInt16();
                    byte widthValue = reader.ReadByte();
                    byte unknown1 = reader.ReadByte();
                    byte heightValue = reader.ReadByte();
                    byte unknown2 = reader.ReadByte();
                    textureInfos.Add((textureOffset, parameters, widthValue, unknown1, heightValue, unknown2));
                }

                var textureNames = new List<string>();
                for (int i = 0; i < textureCount; i++)
                {
                    textureNames.Add(new string(reader.ReadChars(16)).Replace("\0", string.Empty).TrimEnd());
                }

                byte paletteDummy = reader.ReadByte();
                byte paletteCount = reader.ReadByte();
                short paletteSectionSize = reader.ReadInt16();
                short paletteUnknownBlockHeaderSize = reader.ReadInt16();
                short paletteUnknownBlockSectionSize = reader.ReadInt16();
                int paletteUnknownConstant = reader.ReadInt32();
                for (int i = 0; i < paletteCount; i++)
                {
                    reader.ReadInt16();
                    reader.ReadInt16();
                }

                short paletteInfoHeaderSize = reader.ReadInt16();
                short paletteInfoDataSize = reader.ReadInt16();
                var paletteOffsets = new List<short>();
                for (int i = 0; i < paletteCount; i++)
                {
                    short paletteOffset = (short)(reader.ReadInt16() << 3);
                    short color0 = reader.ReadInt16();
                    paletteOffsets.Add(paletteOffset);
                }

                var paletteNames = new List<string>();
                for (int i = 0; i < paletteCount; i++)
                {
                    paletteNames.Add(new string(reader.ReadChars(16)).Replace("\0", string.Empty).TrimEnd());
                }

                long texDataBase = sectionOffsets[0] + textureDataOffset;
                long compTexDataBase = sectionOffsets[0] + compressedTextureDataOffset;
                long compTexIndexBase = sectionOffsets[0] + compressedTexturePaletteIndex;
                long paletteDataBase = sectionOffsets[0] + paletteDataOffset;
                for (int i = 0; i < textureCount; i++)
                {
                    var info = textureInfos[i];
                    int format = (info.Parameters >> 10) & 7;
                    int width = 8 << ((info.Parameters >> 4) & 7);
                    int height = 8 << ((info.Parameters >> 7) & 7);
                    if (width == 0) width = 256;
                    if (height == 0) height = 256;
                    bool isTexel4x4 = format == 5;
                    long imageOffset = (isTexel4x4 ? compTexDataBase : texDataBase) + info.Offset;
                    int depth = format switch
                    {
                        1 => 8,
                        2 => 2,
                        3 => 4,
                        4 => 8,
                        5 => 2,
                        6 => 8,
                        7 => 16,
                        _ => 0
                    };
                    int imageSize = width * height * depth / 8;
                    if (imageSize <= 0)
                    {
                        imageSize = 0;
                    }

                    if (imageOffset < 0 || imageOffset + imageSize > data.Length)
                    {
                        continue;
                    }

                    stream.Position = imageOffset;
                    byte[] pixels = reader.ReadBytes(imageSize);

                    byte[] spData = [];
                    if (isTexel4x4)
                    {
                        long spOffset = compTexIndexBase + info.Offset / 2;
                        int spSize = imageSize / 2;
                        if (spOffset >= 0 && spOffset + spSize <= data.Length)
                        {
                            stream.Position = spOffset;
                            spData = reader.ReadBytes(spSize);
                        }
                    }

                    pack.Textures.Add(new NsbtxTextureEntry
                    {
                        Index = i,
                        Name = textureNames[i],
                        Width = width,
                        Height = height,
                        Format = format,
                        ColorZeroTransparent = (info.Parameters & (1 << 13)) != 0,
                        PaletteIndex = 0,
                        Source = pixels,
                        SpData = spData
                    });
                }

                for (int i = 0; i < paletteCount; i++)
                {
                    long palOffset = paletteDataBase + paletteOffsets[i];
                    int paletteLength = i + 1 < paletteCount ? paletteOffsets[i + 1] - paletteOffsets[i] : 0;
                    if (paletteLength <= 0)
                    {
                        paletteLength = 512;
                    }
                    stream.Position = palOffset;
                    byte[] paletteData = reader.ReadBytes(Math.Min(paletteLength, 1024));
                    uint[] colors = new uint[paletteData.Length / 2];
                    for (int colorIndex = 0; colorIndex < colors.Length; colorIndex++)
                    {
                        ushort color = BitConverter.ToUInt16(paletteData, colorIndex * 2);
                        colors[colorIndex] = ColorFromBgr555(color);
                    }
                    pack.Palettes.Add(new NsbtxPaletteEntry
                    {
                        Index = i,
                        Name = paletteNames[i],
                        Length = paletteData.Length,
                        Colors = colors
                    });
                }

                if (pack.Textures.Count == 0 && pack.Palettes.Count == 0)
                {
                    return false;
                }

                return true;
            }
            catch
            {
                return false;
            }
        }

        private static uint ColorFromBgr555(ushort color)
        {
            byte red = (byte)((color & 0x1F) << 3);
            byte green = (byte)(((color >> 5) & 0x1F) << 3);
            byte blue = (byte)(((color >> 10) & 0x1F) << 3);
            return (uint)(255 << 24 | red << 16 | green << 8 | blue);
        }

        internal static byte[] DecodeTexturePixels(NsbtxTextureEntry texture, uint[] palette)
        {
            if (texture.Format == 5)
            {
                return DecodeTexel4x4(texture, palette);
            }

            int pixelCount = texture.Width * texture.Height;
            var output = new byte[pixelCount * 4];
            for (int i = 0; i < pixelCount; i++)
            {
                int paletteIndex;
                byte alpha = 255;
                switch (texture.Format)
                {
                    case 1:
                        paletteIndex = texture.Source[i] & 0x1F;
                        alpha = (byte)((texture.Source[i] >> 5) is 0 ? 0 : ((texture.Source[i] >> 5) + 1) * 32 - 1);
                        break;
                    case 2:
                        paletteIndex = (texture.Source[i / 4] >> ((i % 4) * 2)) & 0x3;
                        break;
                    case 3:
                        paletteIndex = (texture.Source[i / 2] >> ((i % 2) * 4)) & 0xF;
                        break;
                    case 4:
                        paletteIndex = texture.Source[i];
                        break;
                    case 6:
                        paletteIndex = texture.Source[i] & 0x7;
                        alpha = (byte)((texture.Source[i] >> 3) is 0 ? 0 : ((texture.Source[i] >> 3) + 1) * 8 - 1);
                        break;
                    case 7:
                        uint direct = ColorFromBgr555(BitConverter.ToUInt16(texture.Source, i * 2));
                        output[i * 4] = (byte)(direct >> 16);
                        output[i * 4 + 1] = (byte)(direct >> 8);
                        output[i * 4 + 2] = (byte)direct;
                        output[i * 4 + 3] = (BitConverter.ToUInt16(texture.Source, i * 2) & 0x8000) != 0 ? (byte)255 : (byte)0;
                        continue;
                    default:
                        continue;
                }

                if (paletteIndex >= palette.Length)
                {
                    continue;
                }

                uint color = palette[paletteIndex];
                output[i * 4] = (byte)(color >> 16);
                output[i * 4 + 1] = (byte)(color >> 8);
                output[i * 4 + 2] = (byte)color;
                output[i * 4 + 3] = texture.ColorZeroTransparent && paletteIndex == 0 ? (byte)0 : alpha;
            }

            return output;
        }

        private static byte[] DecodeTexel4x4(NsbtxTextureEntry texture, uint[] palette)
        {
            var output = new byte[texture.Width * texture.Height * 4];
            int blocksX = texture.Width / 4;
            int blocksY = texture.Height / 4;

            for (int blockY = 0; blockY < blocksY; blockY++)
            {
                for (int blockX = 0; blockX < blocksX; blockX++)
                {
                    int blockIndex = blockY * blocksX + blockX;
                    if (blockIndex * 4 + 4 > texture.Source.Length || blockIndex * 2 + 2 > texture.SpData.Length)
                    {
                        continue;
                    }

                    uint texels = BitConverter.ToUInt32(texture.Source, blockIndex * 4);
                    ushort index = BitConverter.ToUInt16(texture.SpData, blockIndex * 2);
                    int paletteBase = (index & 0x3FFF) << 1;
                    int mode = (index >> 14) & 3;

                    for (int row = 0; row < 4; row++)
                    {
                        for (int column = 0; column < 4; column++)
                        {
                            int texel = (int)((texels >> ((row * 4 + column) * 2)) & 3);
                            uint color = 0;
                            bool opaque = true;

                            switch (mode)
                            {
                                case 0:
                                    if (texel == 3) { opaque = false; break; }
                                    color = PaletteColor(palette, paletteBase + texel);
                                    break;
                                case 2:
                                    color = PaletteColor(palette, paletteBase + texel);
                                    break;
                                case 1:
                                    if (texel < 2) { color = PaletteColor(palette, paletteBase + texel); }
                                    else if (texel == 2) { color = BlendColors(PaletteColor(palette, paletteBase), PaletteColor(palette, paletteBase + 1), 1, 1); }
                                    else { opaque = false; }
                                    break;
                                case 3:
                                    if (texel < 2) { color = PaletteColor(palette, paletteBase + texel); }
                                    else if (texel == 2) { color = BlendColors(PaletteColor(palette, paletteBase), PaletteColor(palette, paletteBase + 1), 5, 3); }
                                    else { color = BlendColors(PaletteColor(palette, paletteBase), PaletteColor(palette, paletteBase + 1), 3, 5); }
                                    break;
                            }

                            int x = blockX * 4 + column;
                            int y = blockY * 4 + row;
                            int target = (y * texture.Width + x) * 4;
                            output[target] = (byte)(color >> 16);
                            output[target + 1] = (byte)(color >> 8);
                            output[target + 2] = (byte)color;
                            output[target + 3] = opaque ? (byte)255 : (byte)0;
                        }
                    }
                }
            }

            return output;
        }

        private static uint PaletteColor(uint[] palette, int index) =>
            index >= 0 && index < palette.Length ? palette[index] : 0xFF000000;

        private static uint BlendColors(uint first, uint second, int firstWeight, int secondWeight)
        {
            int total = firstWeight + secondWeight;
            byte red = (byte)((((first >> 16) & 0xFF) * firstWeight + ((second >> 16) & 0xFF) * secondWeight) / total);
            byte green = (byte)((((first >> 8) & 0xFF) * firstWeight + ((second >> 8) & 0xFF) * secondWeight) / total);
            byte blue = (byte)(((first & 0xFF) * firstWeight + (second & 0xFF) * secondWeight) / total);
            return (uint)(255 << 24 | red << 16 | green << 8 | blue);
        }

        public Bitmap Render(int textureIndex, int paletteIndex, double scale)
        {
            if (textureIndex < 0 || textureIndex >= Textures.Count)
            {
                return new WriteableBitmap(new PixelSize(1, 1), new Vector(96, 96), global::Avalonia.Platform.PixelFormat.Bgra8888, global::Avalonia.Platform.AlphaFormat.Premul);
            }

            var texture = Textures[textureIndex];
            uint[] palette = paletteIndex >= 0 && paletteIndex < Palettes.Count ? Palettes[paletteIndex].Colors : [];
            byte[] texturePixels = DecodeTexturePixels(texture, palette);
            int width = texture.Width;
            int height = texture.Height;
            if (scale > 1.0)
            {
                width = (int)Math.Round(texture.Width * scale);
                height = (int)Math.Round(texture.Height * scale);
            }

            var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), global::Avalonia.Platform.PixelFormat.Bgra8888, global::Avalonia.Platform.AlphaFormat.Premul);
            using var lockHandle = bitmap.Lock();

            int stride = width * 4;
            byte[] pixels = new byte[stride * height];
            for (int y = 0; y < height; y++)
            {
                int sourceY = (int)Math.Floor((double)y / Math.Max(1.0, scale));
                if (sourceY >= texture.Height)
                {
                    sourceY = texture.Height - 1;
                }

                for (int x = 0; x < width; x++)
                {
                    int sourceX = (int)Math.Floor((double)x / Math.Max(1.0, scale));
                    if (sourceX >= texture.Width)
                    {
                        sourceX = texture.Width - 1;
                    }

                    int sourceIndex = (sourceY * texture.Width + sourceX) * 4;
                    int targetIndex = (y * width + x) * 4;
                    pixels[targetIndex + 0] = texturePixels[sourceIndex + 2];
                    pixels[targetIndex + 1] = texturePixels[sourceIndex + 1];
                    pixels[targetIndex + 2] = texturePixels[sourceIndex + 0];
                    pixels[targetIndex + 3] = texturePixels[sourceIndex + 3];
                }
            }

            Marshal.Copy(pixels, 0, lockHandle.Address, pixels.Length);
            return bitmap;
        }
    }

    public abstract class SpeciesIconRowBase : System.ComponentModel.INotifyPropertyChanged
    {
        public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

        public abstract int Species { get; set; }

        private IImage? icon;
        public IImage? Icon
        {
            get => icon;
            set
            {
                icon = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Icon)));
            }
        }
    }

    public sealed class SpeciesSlotRow(string label, Func<ushort> get, Action<ushort> set) : SpeciesIconRowBase
    {
        public string Label { get; } = label;

        public override int Species
        {
            get => get();
            set { if (value >= 0) set((ushort)value); }
        }
    }

    public sealed class BugContestSlotRow(string label, BugContestEncounter encounter) : SpeciesIconRowBase
    {
        public string Label { get; } = label;

        public override int Species
        {
            get => encounter.Species;
            set { if (value >= 0) encounter.Species = (ushort)value; }
        }

        public string MinLevel
        {
            get => encounter.MinLevel.ToString();
            set { if (byte.TryParse(value, out byte parsed)) encounter.MinLevel = parsed; }
        }

        public string MaxLevel
        {
            get => encounter.MaxLevel.ToString();
            set { if (byte.TryParse(value, out byte parsed)) encounter.MaxLevel = parsed; }
        }

        public string Rate
        {
            get => encounter.Rate.ToString();
            set { if (byte.TryParse(value, out byte parsed)) encounter.Rate = parsed; }
        }

        public string Score
        {
            get => encounter.Score.ToString();
            set { if (byte.TryParse(value, out byte parsed)) encounter.Score = parsed; }
        }
    }

    public sealed class HeadbuttSlotRow(string label, HeadbuttEncounter encounter) : SpeciesIconRowBase
    {
        public string Label { get; } = label;

        public override int Species
        {
            get => encounter.PokemonId;
            set { if (value >= 0) encounter.PokemonId = (ushort)value; }
        }

        public string MinLevel
        {
            get => encounter.MinLevel.ToString();
            set { if (byte.TryParse(value, out byte parsed)) encounter.MinLevel = parsed; }
        }

        public string MaxLevel
        {
            get => encounter.MaxLevel.ToString();
            set { if (byte.TryParse(value, out byte parsed)) encounter.MaxLevel = parsed; }
        }
    }

    public sealed class SafariSlotRow(string label, SafariZoneEncounter encounter) : SpeciesIconRowBase
    {
        public string Label { get; } = label;

        public override int Species
        {
            get => encounter.PokemonId;
            set { if (value >= 0) encounter.PokemonId = (ushort)value; }
        }

        public string Level
        {
            get => encounter.Level.ToString();
            set { if (byte.TryParse(value, out byte parsed)) encounter.Level = parsed; }
        }
    }

    public sealed class WildEncounterRowView(WildEncounterRow row) : SpeciesIconRowBase
    {
        public string Category => row.Category;
        public int Slot => row.Slot;
        public bool IsCategoryStart => row.IsCategoryStart;
        public string RateLabel => row.RatePercent is int pct ? $"{pct}%" : string.Empty;

        public override int Species
        {
            get => row.Species;
            set { if (value >= 0) row.Species = value; }
        }

        public string MinLevel
        {
            get => row.MinLevel.ToString();
            set { if (int.TryParse(value, out int parsed)) row.MinLevel = parsed; }
        }

        public bool HasMinLevel => row.HasMinLevel;
        public bool HasMaxLevel => row.HasMaxLevel;

        public string MaxLevel
        {
            get => row.MaxLevel.ToString();
            set { if (int.TryParse(value, out int parsed)) row.MaxLevel = parsed; }
        }
    }

    public sealed class CameraRow
    {
        private readonly GameCameraRecord camera;

        public CameraRow(int index, GameCameraRecord camera, bool hasOffsets, string name)
        {
            Index = index;
            Name = name;
            this.camera = camera;
            HasOffsets = hasOffsets;
        }

        public int Index { get; }
        /// <summary>The name the Header Editor's Camera dropdown shows for this camera; display only.</summary>
        public string Name { get; }
        public bool HasOffsets { get; }
        public GameCameraRecord Camera => camera;

        public string Distance
        {
            get => camera.Distance.ToString();
            set { if (uint.TryParse(value, out uint parsed)) camera.Distance = parsed; }
        }

        public string VerticalRotation
        {
            get => camera.VerticalRotation.ToString();
            set { if (short.TryParse(value, out short parsed)) camera.VerticalRotation = parsed; }
        }

        public string HorizontalRotation
        {
            get => camera.HorizontalRotation.ToString();
            set { if (short.TryParse(value, out short parsed)) camera.HorizontalRotation = parsed; }
        }

        public string ZRotation
        {
            get => camera.ZRotation.ToString();
            set { if (short.TryParse(value, out short parsed)) camera.ZRotation = parsed; }
        }

        public bool IsOrthographic
        {
            get => camera.IsOrthographic;
            set => camera.IsOrthographic = value;
        }

        public string Fov
        {
            get => camera.Fov.ToString();
            set { if (ushort.TryParse(value, out ushort parsed)) camera.Fov = parsed; }
        }

        public string NearClip
        {
            get => camera.NearClip.ToString();
            set { if (uint.TryParse(value, out uint parsed)) camera.NearClip = parsed; }
        }

        public string FarClip
        {
            get => camera.FarClip.ToString();
            set { if (uint.TryParse(value, out uint parsed)) camera.FarClip = parsed; }
        }

        public string XOffset
        {
            get => camera.XOffset?.ToString() ?? "0";
            set { if (int.TryParse(value, out int parsed)) camera.XOffset = parsed; }
        }

        public string YOffset
        {
            get => camera.YOffset?.ToString() ?? "0";
            set { if (int.TryParse(value, out int parsed)) camera.YOffset = parsed; }
        }

        public string ZOffset
        {
            get => camera.ZOffset?.ToString() ?? "0";
            set { if (int.TryParse(value, out int parsed)) camera.ZOffset = parsed; }
        }

        public string GetStoredText(string field) => field switch
        {
            nameof(Distance) => Distance,
            nameof(VerticalRotation) => VerticalRotation,
            nameof(HorizontalRotation) => HorizontalRotation,
            nameof(ZRotation) => ZRotation,
            nameof(Fov) => Fov,
            nameof(NearClip) => NearClip,
            nameof(FarClip) => FarClip,
            nameof(XOffset) => XOffset,
            nameof(YOffset) => YOffset,
            nameof(ZOffset) => ZOffset,
            _ => string.Empty
        };
    }

    public sealed class TextMessageRow
    {
        public TextMessageRow(int index, string text)
        {
            Index = index;
            Text = text;
        }

        public int Index { get; }
        public string Text { get; set; }
        public string Label => MainWindow.TextLineLabel(Index);
    }

    private static string TextLineLabel(int index) => index.ToString();

    public MainWindow()
    {
        InitializeComponent();
        BuildExtraEditors();
        DataContext = this;
        WatchStatusTextBlocks();
        AttachScriptCommandTooltips();
        AttachCameraPreview();
        AttachFlyWarpPreview();
        StartUnsavedMarkers();
        AttachInputMarks();
        HookJournalCommits();
        StatusLog.Info("DSPRE", "DSPRE Reloaded started.");
        Opened += (_, _) => _ = OpenStartupProjectAsync();
        if (CharMapStore.IsCustomOutdated(ToolsDirectory))
        {
            StatusLog.Warning("Character Map", "Your custom character map was made from an older default map. Use Rebase on Default on the Character Map page to pick up the default's changes.");
        }
        PageTitleIcon.Source = EditorIcons.For(PageTitle.Text);
        AddHandler(KeyDownEvent, SwallowInputWhileLoading, RoutingStrategies.Tunnel);
        KeyDown += (_, e) =>
        {
            if (e.Key == Key.F && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Shift))
            {
                e.Handled = true;
                OpenProjectSearch();
            }
            else if (HandlePictureShortcut(e))
            {
                e.Handled = true;
            }
        };
        // A highlight in a message's text box changes on mouse release, on key release and when the
        // box takes focus; none of those reach the list as a selection change.
        TextMessagesList.AddHandler(PointerReleasedEvent, (_, _) => UpdateTextPreview(), RoutingStrategies.Bubble, handledEventsToo: true);
        TextMessagesList.AddHandler(KeyUpEvent, (_, _) => UpdateTextPreview(), RoutingStrategies.Bubble, handledEventsToo: true);
        TextMessagesList.AddHandler(GotFocusEvent, (_, _) => UpdateTextPreview(), RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(TextInputEvent, SwallowInputWhileLoading, RoutingStrategies.Tunnel);
        AddHandler(NumericUpDown.ValueChangedEvent, RememberClearedNumericBox, RoutingStrategies.Bubble, handledEventsToo: true);
        AddHandler(LostFocusEvent, RestoreClearedNumericBox, RoutingStrategies.Bubble, handledEventsToo: true);
        MapViewer.RenderError = MapViewer_RenderError;
        MapViewer.GridEditStarting += BeginPermissionEdit;
        MapViewer.GridCellPaint += (col, row) => PaintPermissionCell(col, row);
        MapViewer.GridCellFloodFill += (col, row) => FloodFillPermissionCell(col, row);
        MapViewer.GridCellPick += (col, row) => PickPermissionCell(col, row);
        MapViewer.BuildingPartClicked += partIndex => SelectBuildingPart(partIndex);
        MapViewer.EventMarkerClicked += eventIndex => SelectEventMarker(eventIndex);
        PermissionLabels.Target = MapViewer;
        MapViewer.ViewChanged += () => PermissionLabels.InvalidateVisual();
        MatrixOverlay.Target = IndoorMapViewer;
        IndoorMapViewer.ViewChanged += () => MatrixOverlay.InvalidateVisual();
        IndoorMapViewer.ViewClicked += IndoorMapViewer_ViewClicked;
        HeaderMapsOverlay.Target = OverworldMapViewer;
        OverworldMapViewer.ViewChanged += () => HeaderMapsOverlay.InvalidateVisual();
        // The multi-map views only choose which map to edit; buildings and events are edited in the
        // Single Map view, so double-clicking a map there opens it.
        OverworldMapViewer.BuildingPickingEnabled = OverworldMapViewer.EventPickingEnabled = false;
        IndoorMapViewer.BuildingPickingEnabled = IndoorMapViewer.EventPickingEnabled = false;
        OverworldMapViewer.ViewDoubleClicked += point => OpenMapAtPoint(OverworldMapViewer, HeaderMapsOverlay, currentOverworldPlacements, point);
        IndoorMapViewer.ViewDoubleClicked += point => OpenMapAtPoint(IndoorMapViewer, MatrixOverlay, currentIndoorPlacements, point);
        MapModelViewer.RenderError = message =>
        {
            MapModelStatus.Text = message;
            MapModelViewerPlaceholder.Text = message;
            MapModelViewerPlaceholder.IsVisible = true;
        };
        BldModelViewer.RenderError = message =>
        {
            BldModelStatus.Text = message;
            BldModelViewerPlaceholder.Text = message;
            BldModelViewerPlaceholder.IsVisible = true;
        };
        OverworldMapViewer.RenderError = MapViewer_RenderError;
        IndoorMapViewer.RenderError = MapViewer_RenderError;
    }

    // The value each number box had before it was cleared.
    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<NumericUpDown, System.Runtime.CompilerServices.StrongBox<decimal>> clearedNumericValues = [];

    private static void RememberClearedNumericBox(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (e.Source is NumericUpDown box && e.NewValue is null && e.OldValue is decimal old) clearedNumericValues.AddOrUpdate(box, new System.Runtime.CompilerServices.StrongBox<decimal>(old));
    }

    /// <summary>A number box left empty keeps its value (bound ones through NumericValueConverter); put
    /// that value back in the box once it is left, rather than a blank box that looks like zero.</summary>
    private static void RestoreClearedNumericBox(object? sender, RoutedEventArgs e)
    {
        if ((e.Source as Visual)?.FindAncestorOfType<NumericUpDown>(includeSelf: true) is not { Value: null } box) return;
        if (clearedNumericValues.TryGetValue(box, out System.Runtime.CompilerServices.StrongBox<decimal>? previous)) box.Value = previous.Value;
    }

    private async void OpenRom_Click(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmLeavingProjectAsync()) return;
        StatusText.Text = "Opening ROM picker...";
        try
        {
            var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storageProvider is null)
            {
                Console.Error.WriteLine("[DSPRE] The file picker is not available for the active window.");
                StatusText.Text = "The file picker is not available for this window.";
                return;
            }

            var files = await storageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                AllowMultiple = false,
                Title = "Open Nintendo DS ROM",
                FileTypeFilter =
                [
                    new FilePickerFileType("Nintendo DS ROM") { Patterns = ["*.nds"] },
                    FilePickerFileTypes.All
                ]
            });

            if (files.Count != 1)
            {
                Console.Error.WriteLine("[DSPRE] ROM selection returned no file.");
                StatusText.Text = "No ROM selected. Enter a path and choose Use Path if the file picker is unavailable.";
                return;
            }

            await ExtractRomAsync(files[0].Path.LocalPath);
        }
        catch (Exception ex)
        {
            LogUiError("ROM picker or extraction failed", ex);
            StatusText.Text = $"ROM picker or extraction failed: {ex.Message}";
        }
    }

    private async Task ExtractRomAsync(string? romPath)
    {
        if (string.IsNullOrWhiteSpace(romPath))
        {
            Console.Error.WriteLine("[DSPRE] ROM path was empty.");
            StatusText.Text = "Enter the full path to an .nds ROM.";
            return;
        }

        romPath = romPath.Trim();
        if (!File.Exists(romPath))
        {
            Console.Error.WriteLine($"[DSPRE] ROM file not found: {romPath}");
            StatusText.Text = $"ROM file not found: {romPath}";
            return;
        }

        try
        {
            if (!RomExtractionService.LooksLikeNintendoDsRom(romPath))
            {
                Console.Error.WriteLine($"[DSPRE] Not a Nintendo DS ROM: {romPath}");
                StatusText.Text = $"{Path.GetFileName(romPath)} is not a Nintendo DS ROM (its header has no file table). Pick the .nds file, not a save or other file.";
                return;
            }
        }
        catch (Exception ex)
        {
            LogUiError("ROM could not be read", ex);
            StatusText.Text = $"ROM could not be read: {ex.Message}";
            return;
        }

        sourceRomPath = Path.GetFullPath(romPath);
        ResetOverviewLoadStatus();

        string outputDirectory = Path.Combine(
            Path.GetDirectoryName(romPath) ?? Environment.CurrentDirectory,
            Path.GetFileNameWithoutExtension(romPath) + "_DSPRE_contents");
        string toolsDirectory = Path.Combine(AppContext.BaseDirectory, "Tools");

        // An earlier extraction of this ROM is reused: it is where the editors saved their changes, and
        // skipping extraction and unpacking is most of what makes reopening fast.
        if (RomProject.TryOpen(outputDirectory, out RomProject? existing) && existing is not null)
        {
            RomSourceStamp current = RomSourceStamp.FromRom(romPath);
            bool sameRom = RomSourceStamp.Read(outputDirectory)?.Matches(current) == true;
            string? choice = sameRom ? "open" : await new ChoiceDialog(
                "Contents folder already exists",
                $"{Path.GetFileName(outputDirectory)} already exists next to this ROM, but the ROM has changed since the folder was extracted from it (or the folder was made by another tool).\n\n" +
                "Open the existing folder to keep working on the edits in it, or extract the ROM again, which deletes the folder and everything in it.",
                ("open", "Open Existing Folder"), ("extract", "Extract Again"), ("cancel", "Cancel")).ShowDialog<string?>(this);

            if (choice == "open")
            {
                StatusText.Text = $"Opening the existing contents folder {Path.GetFileName(outputDirectory)}...";
                if (!sameRom) current.Write(outputDirectory);
                BeginBlockingLoad();
                try
                {
                    await PrepareAndShowProjectAsync(existing, sourceRomPath);
                }
                finally
                {
                    EndBlockingLoad();
                }

                return;
            }

            if (choice != "extract")
            {
                StatusText.Text = "Opening the ROM was cancelled.";
                return;
            }

            try
            {
                Directory.Delete(outputDirectory, recursive: true);
                StatusLog.Info("DSPRE", $"Deleted {outputDirectory} to extract the ROM again.");
            }
            catch (Exception ex)
            {
                LogUiError("The old contents folder could not be deleted", ex);
                StatusText.Text = $"The old contents folder could not be deleted: {ex.Message}";
                return;
            }
        }

        StatusText.Text = "Extracting ROM...";
        BeginBlockingLoad();
        try
        {
            var result = await new RomExtractionService().ExtractAsync(
                romPath,
                outputDirectory,
                RomProjectFormat.DsRom,
                toolsDirectory);

            if (!result.Succeeded)
            {
                Console.Error.WriteLine($"[DSPRE] ROM extraction failed with exit code {result.ExitCode}.");
                if (!string.IsNullOrWhiteSpace(result.StandardOutput))
                {
                    Console.Error.WriteLine($"[DSPRE] Tool output: {result.StandardOutput.Trim()}");
                }
                if (!string.IsNullOrWhiteSpace(result.StandardError))
                {
                    Console.Error.WriteLine($"[DSPRE] Tool error: {result.StandardError.Trim()}");
                }
                StatusText.Text = string.IsNullOrWhiteSpace(result.StandardError)
                    ? $"ROM extraction failed with exit code {result.ExitCode}."
                    : $"ROM extraction failed: {result.StandardError.Trim()}";
                return;
            }

            if (RomProject.TryOpen(outputDirectory, out var project))
            {
                RomSourceStamp.FromRom(romPath).Write(outputDirectory);
                await PrepareAndShowProjectAsync(project!, sourceRomPath);
            }
            else
            {
                StatusText.Text = "Extraction completed, but the output is not a recognized ROM project.";
            }
        }
        catch (Exception ex)
        {
            LogUiError("ROM extraction could not start", ex);
            StatusText.Text = $"ROM extraction could not start: {ex.Message}";
        }
        finally
        {
            EndBlockingLoad();
        }
    }

    private async void OpenProject_Click(object? sender, RoutedEventArgs e)
    {
        if (!await ConfirmLeavingProjectAsync()) return;
        StatusText.Text = "Opening project picker...";
        try
        {
            var storageProvider = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storageProvider is null)
            {
                Console.Error.WriteLine("[DSPRE] The folder picker is not available for the active window.");
                StatusText.Text = "The folder picker is not available for this window.";
                return;
            }

            var folders = await storageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
            {
                AllowMultiple = false,
                Title = "Open extracted ROM project"
            });

            if (folders.Count == 1 && !await OpenProjectFolderAsync(folders[0].Path.LocalPath))
            {
                StatusText.Text = "The selected folder is not a recognized ROM project.";
            }
            else
            {
                StatusText.Text = "Project selection cancelled.";
            }
        }
        catch (Exception ex)
        {
            LogUiError("Project picker failed", ex);
            StatusText.Text = $"Project picker failed: {ex.Message}";
        }
    }

    private void ShowProjectStatus(RomProject project)
    {
        ConfigureHeaderStore(project);
        // The open editor still shows the last project; reopening it from the overview reloads it.
        EditorList.SelectedIndex = 0;
        if (project.TryGetGameMetadata(out var metadata))
        {
            StatusText.Text = $"Loaded {metadata!.GameCode} ({metadata.HeaderFormat}) - {project.GetHeaderCount()} headers";
            OverviewTitle.Text = $"{metadata.GameCode} project loaded";
            OverviewDescription.Text = "The ROM project is ready. Select an editor from the left panel.";
            OverviewLoadPrompt.IsVisible = false;
            OverviewDocsCard.IsVisible = true;
        }
        else
        {
            StatusText.Text = $"Loaded {project.Format} project, but game metadata could not be resolved.";
            OverviewTitle.Text = "ROM project loaded";
            OverviewDescription.Text = "The project loaded, but game metadata could not be resolved.";
            OverviewLoadPrompt.IsVisible = false;
        }
    }

    private int blockingLoadCount;
    private DispatcherTimer? loadingOverlayCardTimer;

    // The UI thread keeps running so status text and the progress bar repaint, but all input is
    // blocked (pointer by the overlay, keyboard by SwallowInputWhileLoading) so nothing can start a
    // second load on top of the one in flight. Counted because loads nest: selecting a map changes
    // the texture selectors, which start their own load. With showImmediately false the overlay still
    // blocks input at once but only becomes visible if the load is slow, so cached map switches
    // don't flash.
    private void BeginBlockingLoad(string title = "Loading ROM project...", bool showImmediately = true)
    {
        if (blockingLoadCount++ > 0) return;
        LoadingOverlayTitle.Text = title;
        LoadingOverlayProgress.IsIndeterminate = true;
        LoadingOverlayStep.IsVisible = false;
        LoadingOverlayMessage.IsVisible = showImmediately;
        LoadingOverlayCard.IsVisible = showImmediately;
        LoadingOverlay.IsVisible = true;
        if (showImmediately) return;

        loadingOverlayCardTimer ??= CreateLoadingOverlayCardTimer();
        loadingOverlayCardTimer.Start();
    }

    private void EndBlockingLoad()
    {
        if (blockingLoadCount == 0 || --blockingLoadCount > 0) return;
        loadingOverlayCardTimer?.Stop();
        LoadingOverlay.IsVisible = false;
        LoadingOverlayCard.IsVisible = false;
    }

    /// <summary>Shows what a blocking load is doing and how far along it is; a null fraction leaves
    /// the bar indeterminate.</summary>
    private void ReportLoadProgress(string step, double? fraction)
    {
        if (blockingLoadCount == 0) return;
        LoadingOverlayStep.Text = step;
        LoadingOverlayStep.IsVisible = true;
        LoadingOverlayProgress.IsIndeterminate = fraction is null;
        if (fraction is double value) LoadingOverlayProgress.Value = Math.Clamp(value, 0, 1);
    }

    /// <summary>Lets the overlay repaint before a long synchronous step (building a 3D scene) runs.</summary>
    private static Task YieldToRenderAsync() =>
        Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Background).GetTask();

    private DispatcherTimer CreateLoadingOverlayCardTimer()
    {
        var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(300) };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            if (blockingLoadCount > 0) LoadingOverlayCard.IsVisible = true;
        };
        return timer;
    }

    private void SwallowInputWhileLoading(object? sender, RoutedEventArgs e)
    {
        if (blockingLoadCount > 0) e.Handled = true;
    }

    private void ResetOverviewLoadStatus()
    {
        OverviewTitle.Text = "Loading ROM project...";
        OverviewDescription.Text = "Preparing extracted data for editing.";
        OverviewLoadPrompt.IsVisible = false;
        OverviewArm9Status.Text = "ARM9: LOADING...";
        OverviewHeaderStatus.Text = "HEADERS: LOADING...";
        OverviewTextStatus.Text = "TEXT ARCHIVES: LOADING...";
        OverviewMatrixStatus.Text = "MATRICES: LOADING...";
        OverviewScriptStatus.Text = "SCRIPTS: LOADING...";
        OverviewLevelScriptStatus.Text = "LEVEL SCRIPTS: LOADING...";
        OverviewMapStatus.Text = "MAPS: LOADING...";
        OverviewTilesetStatus.Text = "TEXTURE PACKS: LOADING...";
        OverviewAreaDataStatus.Text = "AREA DATA: LOADING...";
        OverviewCameraStatus.Text = "CAMERAS: LOADING...";
        OverviewEncounterStatus.Text = "WILD ENCOUNTERS: LOADING...";
        OverviewSpecialEncounterStatus.Text = "SPECIAL ENCOUNTERS: LOADING...";
    }

    private async Task PrepareAndShowProjectAsync(RomProject project, string? baseRomPath = null)
    {
        try
        {
            EditJournal.Close();
            HookUndoHistory();
            string toolsDirectory = Path.Combine(AppContext.BaseDirectory, "Tools");
            ProjectPreparationResult prepared = await Task.Run(() => RomProjectPipeline.PrepareAsync(
                project,
                toolsDirectory,
                status => global::Avalonia.Threading.Dispatcher.UIThread.Post(() => StatusText.Text = status),
                deferCompleteScriptExport: true));
            if (prepared.Metadata is not null && prepared.ScriptExport is null) _ = RecheckScriptExportAsync(project, prepared.Metadata);
            if (!prepared.Arm9.Succeeded)
            {
                Console.Error.WriteLine($"[DSPRE] ARM9 preparation failed with exit code {prepared.Arm9.ExitCode}.");
                Console.Error.WriteLine(prepared.Arm9.StandardError);
                StatusText.Text = "ARM9 preparation failed; headers may not be editable in-game.";
                OverviewArm9Status.Text = "ARM9: FAILED";
                return;
            }

            OverviewArm9Status.Text = "ARM9: READY";

            if (prepared.Metadata is RomGameMetadata metadata)
            {
                OverviewHeaderStatus.Text = $"{project.GetHeaderCount()}/{project.GetHeaderCount()} HEADERS LOADED";
                if (prepared.TextArchives is { Succeeded: false } textResult)
                {
                    Console.Error.WriteLine($"[DSPRE] Text archive preparation failed with exit code {textResult.ExitCode}.");
                    Console.Error.WriteLine(textResult.StandardError);
                    OverviewTextStatus.Text = "TEXT ARCHIVES: FAILED";
                }
                else
                {
                    int textCount = TextArchiveService.GetArchiveIds(project).Count;
                    OverviewTextStatus.Text = $"ALL {textCount} TEXT ARCHIVES LOADED";
                }
                int matrixCount = Directory.Exists(project.MatricesDirectory)
                    ? Directory.GetFiles(project.MatricesDirectory, "*", SearchOption.TopDirectoryOnly).Length
                    : 0;
                OverviewMatrixStatus.Text = $"ALL {matrixCount} MATRICES LOADED";
                if (prepared.ScriptExport is { Succeeded: false } scriptExport)
                {
                    Console.Error.WriteLine($"[DSPRE] Script export failed with exit code {scriptExport.ExitCode}.");
                    Console.Error.WriteLine(scriptExport.StandardError);
                    OverviewScriptStatus.Text = "SCRIPTS: FAILED";
                }
                else
                {
                    int scriptCount = Directory.Exists(project.ExpandedScriptsDirectory)
                        ? Directory.GetFiles(project.ExpandedScriptsDirectory, "*.script", SearchOption.TopDirectoryOnly).Length
                        : 0;
                    OverviewScriptStatus.Text = $"ALL {scriptCount} SCRIPTS LOADED";
                }

                UpdateOverviewDataStatus(project, metadata);
                EditJournal.Open(project, metadata, baseRomPath);
                HookUndoHistory();
            }

            ShowProjectStatus(project);
        }
        catch (Exception ex)
        {
            LogUiError("ARM9 preparation failed", ex);
            StatusText.Text = $"ARM9 preparation failed: {ex.Message}";
        }
    }

    /// <summary>Every script was already exported, so the project opened without waiting for the export
    /// bridge; it still runs afterwards because it is what notices a changed command database (and
    /// re-exports the scripts to match).</summary>
    private async Task RecheckScriptExportAsync(RomProject project, RomGameMetadata metadata)
    {
        try
        {
            ToolResult result = await new RomExtractionService().ExportScriptsAsync(project, metadata, Path.Combine(AppContext.BaseDirectory, "Tools"));
            if (result.Succeeded) StatusLog.Info("Scripts", "Script export is up to date with the command database.");
            else StatusLog.Warning("Scripts", $"Checking the script export failed with exit code {result.ExitCode}: {result.StandardError.Trim()}");
        }
        catch (Exception ex)
        {
            LogUiError("Checking the script export failed", ex);
        }
    }

    private void UpdateOverviewDataStatus(RomProject project, RomGameMetadata metadata)
    {
        int scriptFileCount = CountFiles(project.ScriptsDirectory);
        int levelScriptCount = LevelScriptService.GetIds(project).Count;
        OverviewLevelScriptStatus.Text = scriptFileCount > 0
            ? $"{levelScriptCount} LEVEL SCRIPTS FOUND IN {scriptFileCount} SCRIPT FILES"
            : "LEVEL SCRIPTS: NONE FOUND";

        int mapCount = CountFiles(project.MapsDirectory);
        OverviewMapStatus.Text = mapCount > 0 ? $"ALL {mapCount} MAPS LOADED" : "MAPS: NONE FOUND";

        int mapTextures = CountFiles(project.MapTexturesDirectory);
        int buildingTextures = CountFiles(project.BuildingTexturesDirectory);
        OverviewTilesetStatus.Text = mapTextures + buildingTextures > 0
            ? $"{mapTextures} MAP AND {buildingTextures} BUILDING TEXTURE PACKS LOADED"
            : "TEXTURE PACKS: NONE FOUND";

        int areaDataCount = CountFiles(project.AreaDataDirectory);
        OverviewAreaDataStatus.Text = areaDataCount > 0
            ? $"ALL {areaDataCount} AREA DATA FILES LOADED"
            : "AREA DATA: NONE FOUND";

        try
        {
            CameraTableLayout layout = CameraService.GetLayout(metadata);
            CameraTable cameraTable = CameraService.Load(project, metadata);
            OverviewCameraStatus.Text =
                $"ALL {cameraTable.Cameras.Count} CAMERAS LOADED FROM OVERLAY {layout.OverlayNumber}";
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Camera table could not be read for the overview: {ex.Message}");
            OverviewCameraStatus.Text = "CAMERAS: UNAVAILABLE";
        }

        int encounterCount = WildEncounterService.GetFileIds(project).Count;
        OverviewEncounterStatus.Text = encounterCount > 0
            ? $"ALL {encounterCount} WILD ENCOUNTER FILES LOADED"
            : "WILD ENCOUNTERS: NONE FOUND";

        OverviewSpecialEncounterStatus.Text = DescribeSpecialEncounters(project, metadata);
    }

    private static string DescribeSpecialEncounters(RomProject project, RomGameMetadata metadata)
    {
        try
        {
            if (SpecialEncounterService.SupportsHgssFeatures(metadata))
            {
                int headbutt = SpecialEncounterService.GetHeadbuttIds(project).Count;
                int safari = SpecialEncounterService.GetSafariZoneIds(project).Count;
                int bugContestSlots = File.Exists(SpecialEncounterService.GetBugContestPath(project))
                    ? SpecialEncounterService.LoadBugContest(project).Sum(set => set.Encounters.Count)
                    : 0;
                return $"SPECIAL ENCOUNTERS: {headbutt} HEADBUTT FILES, {safari} SAFARI ZONES, {bugContestSlots} BUG CONTEST SLOTS LOADED";
            }

            if (SpecialEncounterService.SupportsHoneyTree(metadata))
            {
                int honeySlots = SpecialEncounterService.LoadHoneyTree(project, metadata).Sum(group => group.Encounters.Count);
                int marshSlots = SpecialEncounterService.LoadGreatMarsh(project).Sum(group => group.Encounters.Count);
                return $"SPECIAL ENCOUNTERS: {honeySlots} HONEY TREE SLOTS AND {marshSlots} GREAT MARSH SLOTS LOADED";
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Special encounters could not be read for the overview: {ex.Message}");
        }

        return "SPECIAL ENCOUNTERS: UNAVAILABLE";
    }

    private static int CountFiles(string directory) =>
        Directory.Exists(directory) ? Directory.GetFiles(directory, "*", SearchOption.TopDirectoryOnly).Length : 0;

    private void ConfigureHeaderStore(RomProject project)
    {
        currentProject = project;
        ResetOverworldSpriteCaches();
        PrefetchOverworldSprites(project);
        LoadScriptNamesQuietly();
        ResetProjectCaches();
        ResetEditorsForProject();
        headerStore = null;
        mapLocationNameIds = null;
        matrixLocationNameIds = null;
        buildingPalette?.Close();
        projectSearchWindow?.Close();
        buildingPaletteContent = null;
        openTextArchives.Clear();
        scriptTextArchiveOverride = null;
        wildEncounterIds.Clear();
        trainerIds.Clear();
        headerSearchWindow?.Close();
        spawnEditorDirty = false;
        selectedHeader = null;
        allHeaders.Clear();
        HeaderSelector.Items.Clear();

        if (!project.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            return;
        }

        headerStore = MapHeaderStoreFactory.Create(project, metadata);
        ResetMapViewHeaders();
        if (headerStore is null)
        {
            return;
        }

        int headerCount = headerStore.Count;

        HeaderEditorInfo.Text = $"{metadata.GameCode} - {metadata.HeaderFormat} - {headerCount} headers";
        bool isHgss = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;
        FollowModeLabel.IsVisible = isHgss;
        FollowModeInput.IsVisible = isHgss;
        LocationTypeLabel.IsVisible = isHgss;
        LocationTypeInput.IsVisible = isHgss;
        WorldMapXLabel.IsVisible = isHgss;
        WorldMapXInput.IsVisible = isHgss;
        WorldMapYLabel.IsVisible = isHgss;
        WorldMapYInput.IsVisible = isHgss;
        Unknown0Label.IsVisible = isHgss;
        Unknown0Input.IsVisible = isHgss;
        KantoFlagInput.IsVisible = isHgss;
        var referencedMusicIds = new HashSet<ushort>();
        for (ushort headerId = 0; headerId < headerCount; headerId++)
        {
            MapHeaderRecord musicHeader = headerStore.Load(headerId);
            referencedMusicIds.Add(musicHeader.MusicDayId);
            referencedMusicIds.Add(musicHeader.MusicNightId);
        }
        IReadOnlyList<HeaderOption> musicOptions = MusicCatalogService.GetMusic(project, metadata, referencedMusicIds);
        SetOptions(MusicDayInput, musicOptions);
        SetOptions(MusicNightInput, musicOptions);
        SetOptions(WeatherInput, HeaderDisplayCatalog.GetWeather(metadata.HeaderFormat));
        SetOptions(CameraInput, HeaderDisplayCatalog.GetCameraAngles(metadata.HeaderFormat));
        SetOptions(LocationSpecifierInput, HeaderDisplayCatalog.GetAreaSettings(metadata.HeaderFormat));
        SetOptions(AreaIconInput, GetAreaIcons(metadata.HeaderFormat));
        IReadOnlyList<HeaderOption> locationOptions = project.GetLocationNames(metadata);
        SetOptions(LocationInput, locationOptions.Count > 0 ? locationOptions : [new HeaderOption(0, "Location names not exported")]);

        for (int i = 0; i < headerCount; i++)
        {
            string name = project.GetInternalName((ushort)i);
            MapHeaderRecord header = headerStore.Load((ushort)i);
            string location = locationOptions.FirstOrDefault(option => option.Id == header.LocationNameId)?.Label ?? "Unknown location";
            allHeaders.Add(new HeaderListEntry((ushort)i, name, header.LocationNameId, location));
        }

        RefreshHeaderSelector();
        HeaderSelector.SelectedIndex = 0;
        UpdateHeaderCountButtons();
    }

    private void HeaderSearchInput_TextChanged(object? sender, TextChangedEventArgs e)
    {
        RefreshHeaderSelector();
    }

    private HeaderSearchWindow? headerSearchWindow;

    private void OpenHeaderSearch_Click(object? sender, RoutedEventArgs e)
    {
        if (headerStore is null) return;
        if (headerSearchWindow is not null)
        {
            headerSearchWindow.Activate();
            return;
        }

        IMapHeaderStore store = headerStore;
        headerSearchWindow = new HeaderSearchWindow(
            allHeaders.Select(header => new HeaderSearchWindow.HeaderEntry(header.Id, header.InternalName, header.ToString())).ToList(),
            store.Load,
            SelectHeaderFromSearch);
        headerSearchWindow.Closed += (_, _) => headerSearchWindow = null;
        headerSearchWindow.Show(this);
    }

    private void SelectHeaderFromSearch(ushort headerId)
    {
        if (blockingLoadCount > 0) return;
        ClearHeaderFilters();
        for (int i = 0; i < HeaderSelector.Items.Count; i++)
        {
            if (HeaderSelector.Items[i] is HeaderListEntry entry && entry.Id == headerId)
            {
                HeaderSelector.SelectedIndex = i;
                return;
            }
        }
    }

    private void ClearHeaderFilters() => HeaderSearchInput.Text = string.Empty;

    private void RefreshHeaderSelector()
    {
        ushort? selectedId = (HeaderSelector.SelectedItem as HeaderListEntry)?.Id;
        string query = HeaderSearchInput?.Text?.Trim() ?? string.Empty;

        HeaderSelector.Items.Clear();
        foreach (HeaderListEntry header in allHeaders)
        {
            if (query.Length == 0 ||
                header.InternalName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                header.LocationId.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) ||
                header.LocationName.Contains(query, StringComparison.OrdinalIgnoreCase))
            {
                HeaderSelector.Items.Add(header);
            }
        }

        if (HeaderSelector.Items.Count == 0)
        {
            HeaderSelector.SelectedIndex = -1;
            StatusText.Text = $"No headers match '{query}'.";
            return;
        }

        int selectedIndex = selectedId is null
            ? 0
            : Enumerable.Range(0, HeaderSelector.Items.Count)
                .FirstOrDefault(index => (HeaderSelector.Items[index] as HeaderListEntry)?.Id == selectedId.Value, 0);
        HeaderSelector.SelectedIndex = selectedIndex;
    }

    private void EditorList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (PageTitle is null || OverviewPanel is null)
        {
            return;
        }

        if (EditorList.SelectedItem is ListBoxItem item && item.Content is string title)
        {
            // Section captions are not editors; keyboard navigation can still land on one.
            if (item.Classes.Contains("section")) return;

            RestoreSharedEditorPanels();
            // An editor left with unsaved changes is shown as it was left; configuring it would reload its
            // documents from disk over the edits.
            bool keepEdits = currentProject is not null && EditorsWithUnsavedChanges().Contains(title);
            PageTitle.Text = title;
            PageTitleIcon.Source = EditorIcons.For(title);
            if (currentProject is not null) StatusText.Text = $"Opened {title}.";
            bool showHeaderEditor = title == "Map Editor" && headerStore is not null;
            bool showScriptEditor = title == "Script Editor" && currentProject is not null;
            bool showScriptCommandDatabase = title == "Script Commands" && currentProject is not null;
            bool showVariableFlagEditor = title == "Variable and Flag Editor" && currentProject is not null;
            bool showLevelScriptEditor = title == "Level Script Editor" && currentProject is not null;
            bool showTextEditor = title == "Text Editor" && currentProject is not null;
            bool showWildEncounterEditor = title == "Wild Pokemon Encounter Editor" && currentProject is not null;
            bool showNsbtxEditor = title == "Texture Editor" && currentProject is not null;
            bool showBuildingModelEditor = title == "Building Editor" && currentProject is not null;
            bool showMapModelEditor = title == "Map Model Editor" && currentProject is not null;
            bool showMapCreator = title == "Map Creator" && currentProject is not null;
            bool showCameraEditor = title == "Camera Editor" && currentProject is not null;
            bool showSpecialEncounters = title == "Special Encounters" && currentProject is not null;
            bool showTrainerEditor = title == "Trainer Editor" && currentProject is not null;
            // Maps or scripts may have changed since the trainers' uses were last looked up.
            if (showTrainerEditor) trainerUses = null;
            bool showPokemonEditor = title == "Pokemon Editor" && currentProject is not null;
            bool showMoveEditor = title == "Move Editor" && currentProject is not null;
            bool showItemEditor = title == "Item Editor" && currentProject is not null;
            bool showTradeEditor = title == "Trade Editor" && currentProject is not null;
            bool showFlyWarpEditor = title == "Fly Warp Editor" && currentProject is not null;
            bool showSpawnEditor = title == "Spawn Editor" && currentProject is not null;
            bool showOverlayEditor = title == "Overlay Editor" && currentProject is not null;
            bool showTableEditor = title == "Table Editor" && currentProject is not null;
            bool showItemTableEditor = title == "Item Table Editor" && currentProject is not null;
            bool showTmEditor = title == "TM Editor" && currentProject is not null;
            bool showOverworldGraphicEditor = title == "Overworld Graphic Editor" && currentProject is not null;
            bool showTrainerGraphicEditor = title == "Trainer Graphic Editor" && currentProject is not null;
            bool showBattleBackground = title == "Battle Background Editor" && currentProject is not null;
            bool showMusicEditor = title == "Music Editor" && currentProject is not null;
            bool showInstrumentEditor = title == "Instrument Editor" && currentProject is not null;
            bool showExtraEditor = IsExtraEditor(title) && currentProject is not null;
            bool showRomToolbox = title == "ROM Toolbox" && currentProject is not null;
            // The utilities work on files the user picks, so they need no project.
            bool showUtilities = title == "Utilities";
            bool showAbout = title == "About";
            bool showSettings = title == "Settings";
            bool showCharMap = title == "Character Map";
            bool showResearch = title == "Research Helper" && currentProject is not null;
            bool showJournalReader = title == "Journal Reader" && currentProject is not null;
            bool showGit = title == "Git" && currentProject is not null;
            OverviewPanel.IsVisible = !showGit && !showJournalReader && !showResearch && !showCharMap && !showSettings && !showAbout && !showRomToolbox && !showUtilities && !showHeaderEditor && !showScriptEditor && !showScriptCommandDatabase && !showVariableFlagEditor && !showLevelScriptEditor && !showTextEditor && !showWildEncounterEditor && !showNsbtxEditor && !showBuildingModelEditor && !showMapModelEditor && !showMapCreator && !showCameraEditor && !showSpecialEncounters && !showTrainerEditor && !showPokemonEditor && !showMoveEditor && !showItemEditor && !showTradeEditor && !showFlyWarpEditor && !showSpawnEditor && !showOverlayEditor && !showTableEditor && !showItemTableEditor && !showTmEditor && !showOverworldGraphicEditor && !showTrainerGraphicEditor && !showBattleBackground && !showMusicEditor && !showInstrumentEditor && !showExtraEditor;
            HeaderMapEditorPanel.IsVisible = showHeaderEditor;
            bool scriptEditorOpened = showScriptEditor && !ScriptEditorPanel.IsVisible;
            ScriptEditorPanel.IsVisible = showScriptEditor;
            if (scriptEditorOpened) RefreshScriptMessages();
            ScriptCommandDatabasePanel.IsVisible = showScriptCommandDatabase;
            VariableFlagEditorPanel.IsVisible = showVariableFlagEditor;
            LevelScriptEditorPanel.IsVisible = showLevelScriptEditor;
            TextEditorPanel.IsVisible = showTextEditor;
            WildEncounterEditorPanel.IsVisible = showWildEncounterEditor;
            NsbtxEditorPanel.IsVisible = showNsbtxEditor;
            BuildingModelEditorPanel.IsVisible = showBuildingModelEditor;
            MapModelEditorPanel.IsVisible = showMapModelEditor;
            MapCreatorPanel.IsVisible = showMapCreator;
            CameraEditorPanel.IsVisible = showCameraEditor;
            SpecialEncounterEditorPanel.IsVisible = showSpecialEncounters;
            TrainerEditorPanel.IsVisible = showTrainerEditor;
            PokemonEditorPanel.IsVisible = showPokemonEditor;
            MoveEditorPanel.IsVisible = showMoveEditor;
            ItemEditorPanel.IsVisible = showItemEditor;
            TradeEditorPanel.IsVisible = showTradeEditor;
            FlyWarpEditorPanel.IsVisible = showFlyWarpEditor;
            SpawnEditorPanel.IsVisible = showSpawnEditor;
            OverlayEditorPanel.IsVisible = showOverlayEditor;
            TableEditorPanel.IsVisible = showTableEditor;
            ItemTableEditorPanel.IsVisible = showItemTableEditor;
            TmEditorPanel.IsVisible = showTmEditor;
            OverworldGraphicEditorPanel.IsVisible = showOverworldGraphicEditor;
            TrainerGraphicEditorPanel.IsVisible = showTrainerGraphicEditor;
            BattleBackgroundPanel.IsVisible = showBattleBackground;
            MusicEditorPanel.IsVisible = showMusicEditor;
            InstrumentEditorPanel.IsVisible = showInstrumentEditor;
            ShowExtraEditor(title, showExtraEditor);
            if (!showMusicEditor && !showInstrumentEditor) AudioPlayer.Stop();
            if (!showTrainerGraphicEditor) StopTrainerGraphicAnimation();
            RomToolboxPanel.IsVisible = showRomToolbox;
            UtilitiesPanel.IsVisible = showUtilities;
            AboutPanel.IsVisible = showAbout;
            SettingsPanel.IsVisible = showSettings;
            CharMapPanel.IsVisible = showCharMap;
            ResearchPanel.IsVisible = showResearch;
            if (showResearch) LoadResearchPage();
            if (showCharMap) LoadCharMapPage();
            if (showSettings && !keepEdits) LoadSettingsPage();
            JournalReaderPanel.IsVisible = showJournalReader;
            if (showJournalReader) LoadJournalReader();
            GitPanel.IsVisible = showGit;
            if (showGit) ConfigureGitPane();
            if (showAbout) AboutVersionText.Text = $"Version {typeof(MainWindow).Assembly.GetName().Version}";
            SetMapViewChrome(showHeaderEditor);
            if (keepEdits)
            {
                // Edits staged from another editor (a Map Creator build) leave this one never filled in.
                if (showMapModelEditor && MapModelList.Items.Count == 0) ConfigureMapModelEditor();
                if (showMapCreator && MapCreatorTileList is not null && MapCreatorLayerSelector.Items.Count == 0) ConfigureMapCreator();
                StatusText.Text = $"{title} still has your unsaved changes.";
            }
            else if (showHeaderEditor)
            {
                LoadSelectedHeader();
            }
            else if (showScriptEditor)
            {
                // Opened on its own, a script's messages come from the first header that uses it.
                scriptTextArchiveOverride = null;
                ConfigureScriptEditor();
            }
            else if (showScriptCommandDatabase)
            {
                ConfigureScriptCommandDatabase();
            }
            else if (showVariableFlagEditor)
            {
                ConfigureVariableFlagEditor();
            }
            else if (showLevelScriptEditor)
            {
                ConfigureLevelScriptEditor();
            }
            else if (showTextEditor)
            {
                ConfigureTextEditor();
            }
            else if (showWildEncounterEditor)
            {
                ConfigureWildEncounterEditor();
            }
            else if (showNsbtxEditor)
            {
                ConfigureNsbtxEditor();
            }
            else if (showBuildingModelEditor)
            {
                ConfigureBuildingModelEditor();
            }
            else if (showMapModelEditor)
            {
                ConfigureMapModelEditor();
            }
            else if (showMapCreator)
            {
                ConfigureMapCreator();
            }
            else if (showCameraEditor)
            {
                ConfigureCameraEditor();
                ConfigureCameraPreviewMaps();
            }
            else if (showSpecialEncounters)
            {
                ConfigureSpecialEncounterEditor();
            }
            else if (showTrainerEditor)
            {
                ConfigureTrainerEditor();
            }
            else if (showPokemonEditor)
            {
                ConfigurePokemonEditor();
                ConfigureEggMoveEditor();
                ShowEggMovesForSelectedSpecies();
            }
            else if (showMoveEditor)
            {
                ConfigureMoveEditor();
            }
            else if (showItemEditor)
            {
                ConfigureItemEditor();
            }
            else if (showTradeEditor)
            {
                ConfigureTradeEditor();
            }
            else if (showFlyWarpEditor)
            {
                ConfigureFlyWarpEditor();
            }
            else if (showSpawnEditor)
            {
                ConfigureSpawnEditor();
            }
            else if (showOverlayEditor)
            {
                ConfigureOverlayEditor();
            }
            else if (showTableEditor)
            {
                ConfigureTableEditor();
            }
            else if (showItemTableEditor)
            {
                ConfigureItemTableEditor();
            }
            else if (showTmEditor)
            {
                ConfigureTmEditor();
            }
            else if (showOverworldGraphicEditor)
            {
                ConfigureOverworldGraphicEditor();
            }
            else if (showMusicEditor)
            {
                ConfigureMusicEditor();
            }
            else if (showInstrumentEditor)
            {
                ConfigureInstrumentEditor();
            }
            else if (showExtraEditor)
            {
                ConfigureExtraEditor(title);
            }
            else if (showBattleBackground)
            {
                ConfigureBattleBackgrounds();
            }
            else if (showTrainerGraphicEditor)
            {
                ConfigureTrainerGraphicEditor();
            }
            else if (showRomToolbox)
            {
                _ = RefreshRomToolboxAsync();
            }
        }
    }

    private void ConfigureTextEditor()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            return;
        }

        RefreshTextArchiveNames();
        textArchiveIds.Clear();
        TextArchiveSelector.Items.Clear();
        textArchiveIds.AddRange(TextArchiveService.GetArchiveIds(currentProject));
        foreach (int id in textArchiveIds)
        {
            TextArchiveSelector.Items.Add(TextArchiveLabel(id));
        }

        if (TextArchiveSelector.Items.Count > 0)
        {
            TextArchiveSelector.SelectedIndex = 0;
        }
        else
        {
            TextEditorStatus.Text = "No decoded text archives found.";
        }
    }

    private void ConfigureWildEncounterEditor()
    {
        if (currentProject is null) return;
        WildPokemonNames.Clear();
        if (currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata is not null)
        {
            int namesArchiveId = metadata.HeaderFormat switch
            {
                MapHeaderFormat.DiamondPearl => 362,
                MapHeaderFormat.Platinum => 412,
                MapHeaderFormat.HeartGoldSoulSilver => 237,
                _ => 412
            };
            try
            {
                TextArchiveData namesArchive = TextArchiveService.Load(currentProject, namesArchiveId, metadata);
                foreach (string name in namesArchive.Messages) WildPokemonNames.Add(name);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DSPRE] Pokemon names could not be loaded: {ex.Message}");
            }
        }
        wildEncounterIds.Clear();
        wildEncounterEntries.Clear();
        WildEncounterSelector.Items.Clear();
        wildEncounterIds.AddRange(WildEncounterService.GetFileIds(currentProject));
        IReadOnlyList<HeaderOption> locationNames = metadata is not null
            ? currentProject.GetLocationNames(metadata)
            : [];
        foreach (int id in wildEncounterIds)
        {
            var headerNames = new List<string>();
            if (headerStore is not null)
            {
                for (ushort headerId = 0; headerId < currentProject.GetHeaderCount(); headerId++)
                {
                    try
                    {
                        MapHeaderRecord header = headerStore.Load(headerId);
                        if (header.WildPokemonId == id)
                        {
                            string name = currentProject.GetInternalName(headerId);
                            if (!string.IsNullOrWhiteSpace(name))
                            {
                                string location = locationNames.FirstOrDefault(option => option.Id == header.LocationNameId)?.Label
                                    ?? "Unknown location";
                                headerNames.Add($"{name} [{location}]");
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine($"[DSPRE] Header {headerId:D4} could not be checked for encounter {id:D4}: {ex.Message}");
                    }
                }
            }

            string displayName = headerNames.Count == 0 ? "Unused" : string.Join(" + ", headerNames.Distinct());
            var entry = new WildEncounterSelectorEntry(id, displayName);
            wildEncounterEntries.Add(entry);
            WildEncounterSelector.Items.Add(entry);
        }

        if (WildEncounterSelector.Items.Count > 0)
        {
            WildEncounterSelector.SelectedIndex = 0;
        }
        else
        {
            WildEncounterStatus.Text = "No unpacked wild encounter files found.";
        }
    }

    /// <summary>One map placed in a matrix cell, with that cell's own Row/Column - the position that
    /// "maps loaded together in-game" actually comes from for these ROMs: adjacent matrix cells are
    /// adjacent map tiles in world space (see LoadCompositeMapsAsync).</summary>
    private sealed record MapPlacement(ushort MapId, string Name, int Row, int Column);

    /// <summary>Every map placed anywhere in the given matrix, regardless of which header owns each
    /// cell - used by the Whole Matrix view's composite loading (MapMatrixSelector_SelectionChanged/
    /// LoadIndoorCompositeMapsAsync), since indoor matrices are not shared across headers the way matrix
    /// 0 is (see GetHeaderMapPlacements's comment) and so never need that per-header filter.</summary>
    private List<MapPlacement> GetMatrixPlacements(int matrixId)
    {
        if (currentProject is null) return [];
        try
        {
            return GetMatrixPlacements(MatrixService.Load(currentProject, matrixId));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Matrix {matrixId:D4} could not be read for map placement: {ex.Message}");
            return [];
        }
    }

    private List<MapPlacement> GetMatrixPlacements(MatrixDocument matrix)
    {
        var result = new List<MapPlacement>();
        if (currentProject is null) return result;
        for (int row = 0; row < matrix.Height; row++)
        {
            for (int col = 0; col < matrix.Width; col++)
            {
                ushort mapId = matrix.Maps[row, col];
                if (mapId == MatrixDocument.EmptyMap) continue;
                result.Add(new MapPlacement(mapId, currentProject.GetInternalName(mapId), row, col));
            }
        }

        return result;
    }

    /// <summary>Every map placed in a cell that this specific header owns within matrix 0, the shared
    /// overworld matrix (see LoadSelectedHeader's routing): many different headers each own a handful of
    /// cells scattered across that one huge matrix, one per overworld location, so rendering "this
    /// header's own maps" means filtering by cell ownership (Headers[row,col] == header.Id) rather than
    /// every cell in the matrix - loading the *whole* shared matrix at once is what made this laggy
    /// before. Falls back to every placed map when the matrix has no Headers section at all (an
    /// unambiguous solo matrix, the same fallback AreaDataService.ResolveMapAreaData documents).</summary>
    private List<MapPlacement> GetHeaderMapPlacements(MapHeaderRecord header)
    {
        var result = new List<MapPlacement>();
        if (currentProject is null) return result;
        try
        {
            MatrixDocument matrix = MatrixService.Load(currentProject, header.MatrixId);
            for (int row = 0; row < matrix.Height; row++)
            {
                for (int col = 0; col < matrix.Width; col++)
                {
                    ushort mapId = matrix.Maps[row, col];
                    if (mapId == MatrixDocument.EmptyMap) continue;
                    if (matrix.HasHeadersSection && matrix.Headers[row, col] != header.Id) continue;
                    result.Add(new MapPlacement(mapId, currentProject.GetInternalName(mapId), row, col));
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Matrix {header.MatrixId:D4} could not be read for header {header.Id:D4}'s own map placement: {ex.Message}");
        }

        return result;
    }

    private sealed record MatrixListEntry(int Id, string Label)
    {
        public override string ToString() => Label;
    }

    private readonly List<MatrixListEntry> allMapMatrices = [];

    /// <summary>Populates MapMatrixSelector once, lazily, with every matrix in the project - each
    /// labeled with a representative location name (see ResolveMatrixLocationLabel) rather than just
    /// its raw ID, since "which matrix is this" is otherwise meaningless to a user browsing by place.</summary>
    private void EnsureMatrixSelectorLoaded()
    {
        if (allMapMatrices.Count > 0 || currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        foreach (int id in MatrixService.GetIds(currentProject))
        {
            try
            {
                MatrixDocument matrix = MatrixService.Load(currentProject, id);
                string? location = ResolveMatrixLocationName(matrix, metadata);
                string label = location is null
                    ? $"{id:D4} - {matrix.Name} ({matrix.Width}x{matrix.Height})"
                    : $"{id:D4} - {location} - {matrix.Name} ({matrix.Width}x{matrix.Height})";
                allMapMatrices.Add(new MatrixListEntry(id, label));
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DSPRE] Matrix {id:D4} could not be read for the matrix selector: {ex.Message}");
            }
        }

        FilterMatrixSelector();
    }

    private void MatrixSearchInput_TextChanged(object? sender, TextChangedEventArgs e) => FilterMatrixSelector();

    /// <summary>Lists the matrices whose id, name or location contains the search text. The matrix on
    /// show stays listed even when it does not match, so filtering never switches what is displayed.</summary>
    private void FilterMatrixSelector()
    {
        if (MapMatrixSelector is null) return;
        string query = MatrixSearchInput.Text?.Trim() ?? string.Empty;
        object? selected = MapMatrixSelector.SelectedItem;
        switchingEditedMatrix = true;
        try
        {
            MapMatrixSelector.Items.Clear();
            foreach (MatrixListEntry entry in allMapMatrices)
            {
                if (query.Length == 0 || ReferenceEquals(entry, selected) || entry.Label.Contains(query, StringComparison.OrdinalIgnoreCase))
                {
                    MapMatrixSelector.Items.Add(entry);
                }
            }

            MapMatrixSelector.SelectedItem = selected;
        }
        finally
        {
            switchingEditedMatrix = false;
        }

        if (query.Length > 0 && MapMatrixSelector.ItemCount > 0 && MatrixSearchInput.IsFocused) MapMatrixSelector.IsDropDownOpen = true;
    }

    /// <summary>A matrix has no location name of its own - this borrows the location name of the first
    /// header referenced by any of its non-empty cells, since every map sharing a matrix is, in
    /// practice, the same in-game place (see GetMatrixPlacements). Null if no header resolves or none
    /// has a location name set.</summary>
    private string? ResolveMatrixLocationName(MatrixDocument matrix, RomGameMetadata metadata)
    {
        if (currentProject is null || ResolveMatrixLocationNameId(matrix) is not ushort locationNameId) return null;
        string? label = currentProject.GetLocationNames(metadata).FirstOrDefault(option => option.Id == locationNameId)?.Label;
        return string.IsNullOrWhiteSpace(label) ? null : label;
    }

    /// <summary>The LocationNameId of the first header referenced by any of the matrix's non-empty
    /// cells, or null if it has no Headers section or none resolves - the grouping key behind
    /// ResolveMatrixLocationLabel's borrowed display name.</summary>
    private ushort? ResolveMatrixLocationNameId(MatrixDocument matrix)
    {
        if (headerStore is null) return null;
        if (!matrix.HasHeadersSection)
        {
            EnsureLocationLookupsLoaded();
            return matrixLocationNameIds!.TryGetValue(matrix.Id, out ushort ownerLocation) ? ownerLocation : null;
        }

        for (int row = 0; row < matrix.Height; row++)
        {
            for (int col = 0; col < matrix.Width; col++)
            {
                if (matrix.Maps[row, col] == MatrixDocument.EmptyMap) continue;
                try
                {
                    return headerStore.Load(matrix.Headers[row, col]).LocationNameId;
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[DSPRE] Header {matrix.Headers[row, col]:D4} could not be read for matrix {matrix.Id:D4}'s location: {ex.Message}");
                }
            }
        }

        return null;
    }

    private Dictionary<int, ushort>? mapLocationNameIds;
    private Dictionary<int, ushort>? matrixLocationNameIds;
    private Dictionary<int, ushort> mapHeaderIds = [];

    /// <summary>Resolves which header owns each map file and each headerless matrix, the same way
    /// AreaDataService.ResolveMapAreaData does (itself mirroring the legacy Event Editor): a cell's own
    /// header is authoritative when the matrix has a Headers section, otherwise every map in the matrix
    /// belongs to the header that references that matrix through its MatrixId.</summary>
    private void EnsureLocationLookupsLoaded()
    {
        if (mapLocationNameIds is not null && matrixLocationNameIds is not null) return;
        mapLocationNameIds = [];
        matrixLocationNameIds = [];
        mapHeaderIds = [];
        if (currentProject is null || headerStore is null) return;

        int headerCount = headerStore.Count;
        var headers = new MapHeaderRecord[headerCount];
        for (int i = 0; i < headerCount; i++) headers[i] = headerStore.Load((ushort)i);

        var matrices = new Dictionary<int, MatrixDocument?>();
        foreach (MapHeaderRecord header in headers)
        {
            if (!matrices.TryGetValue(header.MatrixId, out MatrixDocument? matrix))
            {
                try
                {
                    matrix = MatrixService.Load(currentProject, header.MatrixId);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[DSPRE] Matrix {header.MatrixId:D4} could not be read for location names: {ex.Message}");
                    matrix = null;
                }

                matrices[header.MatrixId] = matrix;
            }

            if (matrix is null) continue;
            if (!matrix.HasHeadersSection) matrixLocationNameIds.TryAdd(matrix.Id, header.LocationNameId);

            for (int row = 0; row < matrix.Height; row++)
            {
                for (int col = 0; col < matrix.Width; col++)
                {
                    ushort mapId = matrix.Maps[row, col];
                    if (mapId == MatrixDocument.EmptyMap || mapLocationNameIds.ContainsKey(mapId)) continue;
                    if (!matrix.HasHeadersSection)
                    {
                        mapLocationNameIds[mapId] = header.LocationNameId;
                        mapHeaderIds[mapId] = header.Id;
                    }
                    else if (matrix.Headers[row, col] < headerCount)
                    {
                        mapLocationNameIds[mapId] = headers[matrix.Headers[row, col]].LocationNameId;
                        mapHeaderIds[mapId] = matrix.Headers[row, col];
                    }
                }
            }
        }
    }

    /// <summary>World units per matrix cell/map tile - matches MapGlView's own GridWorldExtent (32 cells
    /// of 16 world units each), i.e. the nominal, always-consistent spacing between two adjacent matrix
    /// cells' maps, independent of how far any one map's terrain mesh happens to extend past its own
    /// 32x32 footprint for cross-map visual blending (see MapGlView.GridWorldExtent's own comment).</summary>
    private const float MatrixCellWorldSize = 512f;

    /// <summary>The shared overworld matrix's ID - see LoadSelectedHeader's routing comment. Every
    /// outdoor location's header references this same matrix (owning some subset of its cells); every
    /// indoor/standalone location uses its own separate, non-zero matrix instead.</summary>
    private const int OverworldMatrixId = 0;

    /// <summary>Deletes each given map's cached converted-model directory (expanded/map_models/{id:D4}/,
    /// every texture/building-pack variant of it), forcing the next composite load to re-export it
    /// instead of silently reusing a stale scene.json. LoadCompositeMapsAsync/LoadIndoorCompositeMapsAsync
    /// skip re-exporting a map whose model directory already exists (see their own comments on why),
    /// which is correct right up until that map's own buildings change - after a save, callers must
    /// invalidate the maps they just wrote or the freshly-added/edited building would never appear in
    /// the reloaded scene, no matter how many times the view is reset or reloaded.</summary>
    private void InvalidateCachedMapModels(IEnumerable<ushort> mapIds)
    {
        if (currentProject is null) return;
        foreach (ushort mapId in mapIds)
        {
            string mapModelDirectory = Path.Combine(currentProject.RootPath, "expanded", "map_models", mapId.ToString("D4"));
            if (Directory.Exists(mapModelDirectory))
            {
                Directory.Delete(mapModelDirectory, recursive: true);
            }
        }
    }

    private bool loadingOverworldSelectors;
    private List<MapPlacement> currentOverworldPlacements = [];

    /// <summary>Populates OverworldTextureSelector/OverworldBuildingTextureSelector once, lazily - each
    /// gets an "Auto (per-map default)" entry (index 0, meaning every map in the composite keeps
    /// resolving its own best-fit pack via GetDefaultTexturePack/GetDefaultBuildingTexturePack, same as
    /// before this override existed) ahead of the usual "Untextured" + numbered packs list.</summary>
    private void EnsureOverworldTextureSelectorsLoaded()
    {
        if (OverworldTextureSelector.Items.Count > 0 || currentProject is null) return;
        loadingOverworldSelectors = true;
        try
        {
            OverworldTextureSelector.Items.Add("Untextured");
            int texturePackCount = Directory.Exists(currentProject.MapTexturesDirectory)
                ? Directory.GetFiles(currentProject.MapTexturesDirectory).Length
                : 0;
            for (int i = 0; i < texturePackCount; i++) OverworldTextureSelector.Items.Add($"Map Texture Pack [{i:D2}]");

            OverworldBuildingTextureSelector.Items.Add("Untextured");
            int buildingTexturePackCount = Directory.Exists(currentProject.BuildingTexturesDirectory)
                ? Directory.GetFiles(currentProject.BuildingTexturesDirectory).Length
                : 0;
            for (int i = 0; i < buildingTexturePackCount; i++) OverworldBuildingTextureSelector.Items.Add($"Building Texture Pack [{i:D2}]");

            OverworldTextureSelector.SelectedIndex = 0;
            OverworldBuildingTextureSelector.SelectedIndex = 0;
        }
        finally
        {
            loadingOverworldSelectors = false;
        }
    }

    /// <summary>Points a multi-map view's texture selectors at the packs the header's area data uses,
    /// without triggering a reload - the caller loads the view right after.</summary>
    private void ApplyHeaderTexturePacks(MapHeaderRecord header, bool wholeMatrix)
    {
        if (currentProject is null) return;
        ComboBox textures = wholeMatrix ? IndoorTextureSelector : OverworldTextureSelector;
        ComboBox buildings = wholeMatrix ? IndoorBuildingTextureSelector : OverworldBuildingTextureSelector;
        if (wholeMatrix) EnsureIndoorTextureSelectorsLoaded();
        else EnsureOverworldTextureSelectorsLoaded();

        if (wholeMatrix)
        {
            loadingIndoorSelectors = true;
            try
            {
                textures.SelectedIndex = 0;
                buildings.SelectedIndex = 0;
            }
            finally
            {
                loadingIndoorSelectors = false;
            }

            return;
        }

        AreaDataRecord areaData;
        try
        {
            areaData = AreaDataService.Load(currentProject, header.AreaDataId, IsHeartGoldSoulSilver());
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Area data {header.AreaDataId} of header {header.Id} could not be read for its texture packs: {ex.Message}");
            return;
        }

        if (wholeMatrix) loadingIndoorSelectors = true;
        else loadingOverworldSelectors = true;
        try
        {
            if (areaData.MapTileset + 1 < textures.ItemCount) textures.SelectedIndex = areaData.MapTileset + 1;
            if (areaData.BuildingsTileset + 1 < buildings.ItemCount) buildings.SelectedIndex = areaData.BuildingsTileset + 1;
        }
        finally
        {
            if (wholeMatrix) loadingIndoorSelectors = false;
            else loadingOverworldSelectors = false;
        }
    }

    private void OverworldTextureSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ReloadCurrentOverworldComposite();
    private void OverworldBuildingTextureSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ReloadCurrentOverworldComposite();

    private void ReloadCurrentOverworldComposite()
    {
        if (loadingOverworldSelectors || currentOverworldPlacements.Count == 0) return;
        _ = LoadCompositeMapsAsync(currentOverworldPlacements);
    }

    private const int MapsPerBridgeLaunch = 20;

    /// <summary>Converts the placements' maps whose cached models are stale and returns each map's scene
    /// placed in its matrix cell, plus the maps that failed. Maps go to the bridge in batches
    /// (RomExtractionService.ExportMapModelsAsync): launching it under wine costs several times what
    /// converting one map does. Batches run one after another, never side by side, since every bridge
    /// process writes the same %AppData%\DSPRE\Logs\application.log on startup and two at once crash.</summary>
    private async Task<(List<(string ManifestPath, OpenTK.Mathematics.Vector3 Offset)> Loaded, List<string> Failed)> ConvertPlacementsAsync(
        IReadOnlyList<MapPlacement> placements, RomGameMetadata metadata, int? textureOverride, int? buildingTextureOverride)
    {
        var scenes = new List<(MapPlacement Placement, string ScenePath, int TexturePack, int BuildingPack)>();
        var jobs = new Dictionary<string, RomExtractionService.MapModelJob>();
        foreach (MapPlacement placement in placements)
        {
            MapViewEntry? entry = mapEntries.FirstOrDefault(map => map.Id == placement.MapId);
            int texturePackId = textureOverride ?? (entry is not null ? GetDefaultTexturePack(entry, metadata) : -1);
            int buildingTexturePackId = buildingTextureOverride ?? (entry is not null ? GetDefaultBuildingTexturePack(entry) : 0);
            bool interiorBuildings = UsesInteriorBuildings(placement.MapId, metadata);
            string modelDirectory = MapModelCache.GetModelDirectory(currentProject!, placement.MapId, texturePackId, buildingTexturePackId, interiorBuildings);
            string scenePath = Path.Combine(modelDirectory, "scene.json");
            scenes.Add((placement, scenePath, texturePackId, buildingTexturePackId));
            if (!MapModelCache.IsFresh(currentProject!, placement.MapId, texturePackId, buildingTexturePackId, scenePath))
            {
                Directory.CreateDirectory(modelDirectory);
                jobs.TryAdd(modelDirectory, new RomExtractionService.MapModelJob(placement.MapId, modelDirectory, texturePackId, buildingTexturePackId, interiorBuildings));
            }
        }

        List<RomExtractionService.MapModelJob> pending = jobs.Values.ToList();
        int reused = scenes.Select(scene => scene.ScenePath).Distinct().Count() - pending.Count;
        string reusedNote = reused > 0 ? $"; {reused} already converted" : "";
        for (int start = 0; start < pending.Count; start += MapsPerBridgeLaunch)
        {
            List<RomExtractionService.MapModelJob> batch = pending.Skip(start).Take(MapsPerBridgeLaunch).ToList();
            ReportLoadProgress($"Converting maps {start + 1}-{start + batch.Count} of {pending.Count}{reusedNote}...", start / (pending.Count + 1.0));
            await new RomExtractionService().ExportMapModelsAsync(currentProject!, metadata, batch, Path.Combine(AppContext.BaseDirectory, "Tools"));
        }

        var loaded = new List<(string ManifestPath, OpenTK.Mathematics.Vector3 Offset)>();
        var failed = new List<string>();
        foreach ((MapPlacement placement, string scenePath, int texturePack, int buildingPack) in scenes)
        {
            if (MapModelCache.IsFresh(currentProject!, placement.MapId, texturePack, buildingPack, scenePath))
            {
                loaded.Add((scenePath, new OpenTK.Mathematics.Vector3(placement.Column * MatrixCellWorldSize, 0f, placement.Row * MatrixCellWorldSize)));
            }
            else
            {
                failed.Add(placement.MapId.ToString("D4"));
            }
        }

        return (loaded, failed.Distinct().ToList());
    }

    /// <summary>Converts and renders the given placements (always a header's own cells within matrix 0 -
    /// see GetHeaderMapPlacements/LoadSelectedHeader's routing, never every cell in that shared matrix)
    /// together, positioned by their own Row/Column (see MapPlacement/MatrixCellWorldSize), into
    /// OverworldMapViewer, including each map's own placed buildings (for viewing only; buildings are edited
    /// in the Single Map view). The texture/building pack selectors' current choice, if not left on
    /// "Auto", overrides every map's own default pack uniformly. Maps already converted with the same
    /// packs are reused; the rest convert in batches (see ConvertPlacementsAsync).</summary>
    private async Task LoadCompositeMapsAsync(IReadOnlyList<MapPlacement> placements)
    {
        BeginBlockingLoad("Loading maps...", showImmediately: false);
        try
        {
            await LoadCompositeMapsAsyncCore(placements);
        }
        finally
        {
            EndBlockingLoad();
        }
    }

    private async Task LoadCompositeMapsAsyncCore(IReadOnlyList<MapPlacement> placements)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;

        currentOverworldPlacements = placements.ToList();
        OverworldMapViewer.ClearScene(keepEventIcons: true);
        EnsureMapEditorDataLoaded();
        EnsureOverworldTextureSelectorsLoaded();
        // Every map in the view is drawn with the same pair of packs: the ones the selectors show,
        // which start out as the header's own (see ApplyHeaderTexturePacks).
        int? textureOverride = Math.Max(0, OverworldTextureSelector.SelectedIndex) - 1;
        int? buildingTextureOverride = Math.Max(0, OverworldBuildingTextureSelector.SelectedIndex) - 1;

        OverworldViewerStatus.Text = $"Converting {placements.Count} overworld maps...";
        try
        {
            (List<(string ManifestPath, OpenTK.Mathematics.Vector3 Offset)> loaded, List<string> failed) =
                await ConvertPlacementsAsync(placements, metadata, textureOverride, buildingTextureOverride);

            if (loaded.Count == 0)
            {
                OverworldViewerStatus.Text = "None of this matrix's maps could be converted.";
                return;
            }

            ReportLoadProgress($"Building the 3D scene of {loaded.Count} maps...", placements.Count / (placements.Count + 1.0));
            await YieldToRenderAsync();
            ConfigureBuildingAnimations(OverworldMapViewer, interior: false);
            OverworldMapViewer.LoadCompositeScene(loaded, !IsLargePlacementSet(placements));
            ApplyMapAnimation(OverworldMapViewer, DynamicTypeOfPlacements(currentOverworldPlacements));
            RefreshCompositeEventMarkers(OverworldMapViewer, currentOverworldPlacements, headerView: true);
            HeaderMapsOverlay.SetCells(PlacementCells(currentOverworldPlacements));
            OverworldMapViewerPlaceholder.IsVisible = false;
            string failedNote = failed.Count > 0 ? $" ({failed.Count} failed to convert: {string.Join(", ", failed)})" : "";
            string buildingLabel = OverworldMapViewer.PartCount == 1 ? "1 building" : $"{OverworldMapViewer.PartCount} buildings";
            OverworldViewerStatus.Text = $"Loaded {loaded.Count} overworld maps ({OverworldMapViewer.TriangleCount:N0} triangles, {buildingLabel}).{failedNote}";
        }
        catch (Exception ex)
        {
            LogUiError("Overworld maps could not be loaded", ex);
            OverworldViewerStatus.Text = ex.Message;
        }
    }

    private bool loadingIndoorSelectors;
    private List<MapPlacement> currentIndoorPlacements = [];

    /// <summary>Populates IndoorTextureSelector/IndoorBuildingTextureSelector once, lazily - each
    /// gets an "Auto (per-map default)" entry (index 0, meaning every map in the composite keeps
    /// resolving its own best-fit pack via GetDefaultTexturePack/GetDefaultBuildingTexturePack, same as
    /// before this override existed) ahead of the usual "Untextured" + numbered packs list.</summary>
    private void EnsureIndoorTextureSelectorsLoaded()
    {
        if (IndoorTextureSelector.Items.Count > 0 || currentProject is null) return;
        loadingIndoorSelectors = true;
        try
        {
            IndoorTextureSelector.Items.Add("Auto (per-map)");
            IndoorTextureSelector.Items.Add("Untextured");
            int texturePackCount = Directory.Exists(currentProject.MapTexturesDirectory)
                ? Directory.GetFiles(currentProject.MapTexturesDirectory).Length
                : 0;
            for (int i = 0; i < texturePackCount; i++) IndoorTextureSelector.Items.Add($"Map Texture Pack [{i:D2}]");

            IndoorBuildingTextureSelector.Items.Add("Auto (per-map)");
            IndoorBuildingTextureSelector.Items.Add("Untextured");
            int buildingTexturePackCount = Directory.Exists(currentProject.BuildingTexturesDirectory)
                ? Directory.GetFiles(currentProject.BuildingTexturesDirectory).Length
                : 0;
            for (int i = 0; i < buildingTexturePackCount; i++) IndoorBuildingTextureSelector.Items.Add($"Building Texture Pack [{i:D2}]");

            IndoorTextureSelector.SelectedIndex = 0;
            IndoorBuildingTextureSelector.SelectedIndex = 0;
        }
        finally
        {
            loadingIndoorSelectors = false;
        }
    }

    private void IndoorTextureSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ReloadCurrentIndoorComposite();
    private void IndoorBuildingTextureSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e) => ReloadCurrentIndoorComposite();

    private void ReloadCurrentIndoorComposite()
    {
        if (loadingIndoorSelectors || currentIndoorPlacements.Count == 0) return;
        _ = LoadIndoorCompositeMapsAsync(currentIndoorPlacements);
    }

    /// <summary>Converts and renders the given placements (every map placed in one (non-overworld) matrix -
    /// see GetMatrixPlacements) - not filtered by header, since indoor matrices are not shared the way matrix 0 is
    /// together, positioned by their own Row/Column (see MapPlacement/MatrixCellWorldSize), into
    /// IndoorMapViewer, including each map's own placed buildings (for viewing only; buildings are edited
    /// in the Single Map view). The texture/building pack selectors' current choice, if not left on
    /// "Auto", overrides every map's own default pack uniformly. Maps already converted with the same
    /// packs are reused; the rest convert in batches (see ConvertPlacementsAsync).</summary>
    private async Task LoadIndoorCompositeMapsAsync(IReadOnlyList<MapPlacement> placements)
    {
        BeginBlockingLoad("Loading maps...", showImmediately: false);
        try
        {
            await LoadIndoorCompositeMapsAsyncCore(placements);
        }
        finally
        {
            EndBlockingLoad();
        }
    }

    private async Task LoadIndoorCompositeMapsAsyncCore(IReadOnlyList<MapPlacement> placements)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;

        currentIndoorPlacements = placements.ToList();
        IndoorMapViewer.ClearScene(keepEventIcons: true);
        EnsureMapEditorDataLoaded();
        EnsureIndoorTextureSelectorsLoaded();
        // Every map in the view is drawn with the same pair of packs: the ones the selectors show,
        // which start out as the header's own (see ApplyHeaderTexturePacks).
        int? textureOverride = IndoorTextureSelector.SelectedIndex > 0 ? IndoorTextureSelector.SelectedIndex - 2 : null;
        int? buildingTextureOverride = IndoorBuildingTextureSelector.SelectedIndex > 0 ? IndoorBuildingTextureSelector.SelectedIndex - 2 : null;

        IndoorViewerStatus.Text = $"Converting {placements.Count} indoor maps...";
        try
        {
            (List<(string ManifestPath, OpenTK.Mathematics.Vector3 Offset)> loaded, List<string> failed) =
                await ConvertPlacementsAsync(placements, metadata, textureOverride, buildingTextureOverride);

            if (loaded.Count == 0)
            {
                IndoorViewerStatus.Text = "None of this matrix's maps could be converted.";
                return;
            }

            IndoorMapViewer.IsVisible = true;
            ReportLoadProgress($"Building the 3D scene of {loaded.Count} maps...", placements.Count / (placements.Count + 1.0));
            await YieldToRenderAsync();
            ConfigureBuildingAnimations(IndoorMapViewer, interior: true);
            IndoorMapViewer.LoadCompositeScene(loaded, !IsLargePlacementSet(placements));
            ApplyMapAnimation(IndoorMapViewer, DynamicTypeOfPlacements(currentIndoorPlacements));
            RefreshCompositeEventMarkers(IndoorMapViewer, currentIndoorPlacements, headerView: false);
            if (!IsEditingMatrix) MatrixOverlay.SetCells(PlacementCells(currentIndoorPlacements));
            IndoorMapViewerPlaceholder.IsVisible = false;
            string failedNote = failed.Count > 0 ? $" ({failed.Count} failed to convert: {string.Join(", ", failed)})" : "";
            string buildingLabel = IndoorMapViewer.PartCount == 1 ? "1 building" : $"{IndoorMapViewer.PartCount} buildings";
            IndoorViewerStatus.Text = $"Loaded {loaded.Count} indoor maps ({IndoorMapViewer.TriangleCount:N0} triangles, {buildingLabel}).{failedNote}";
        }
        catch (Exception ex)
        {
            LogUiError("Indoor maps could not be loaded", ex);
            IndoorViewerStatus.Text = ex.Message;
        }
    }

    /// <summary>Loads every map placed in the selected (non-overworld) matrix into the Matrix Map
    /// Editor tab, positioned by their own Row/Column just like the Overworld tab does for a header's
    /// own cells - a 1x1 matrix just renders as a single map through the same composite pipeline, no
    /// special-casing needed. Indoor matrices are not shared across many headers the way matrix 0 is
    /// (see GetHeaderMapPlacements's comment), so this deliberately does not filter by header.</summary>
    private async void MapMatrixSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (switchingEditedMatrix || MapMatrixSelector.SelectedItem is not MatrixListEntry entry || currentProject is null) return;
        if (IsEditingMatrix && currentMatrix is not null && currentMatrix.Id != entry.Id)
        {
            if (!await ConfirmLeavingEditedMatrixAsync())
            {
                // Put the selector back on the matrix being edited.
                switchingEditedMatrix = true;
                MapMatrixSelector.SelectedItem = allMapMatrices.FirstOrDefault(item => item.Id == currentMatrix.Id);
                switchingEditedMatrix = false;
                return;
            }

            LoadMatrixForEditing(entry.Id);
        }

        SyncHeaderToMatrix(entry.Id);
        if (selectedHeader is not null) ApplyHeaderTexturePacks(selectedHeader, wholeMatrix: true);
        ShowMatrixComposite(IsEditingMatrix && currentMatrix is not null ? GetMatrixPlacements(currentMatrix) : GetMatrixPlacements(entry.Id), entry.Id);
    }

    private void ShowMatrixComposite(List<MapPlacement> placements, int matrixId)
    {
        shownWholeMatrixId = matrixId;
        if (placements.Count == 0)
        {
            currentIndoorPlacements = [];
            if (!IsEditingMatrix) MatrixOverlay.SetCells([]);
            IndoorMapViewer.IsVisible = false;
            IndoorMapViewerPlaceholder.Text = IsEditingMatrix ? "This matrix has no maps yet. Pick a square by row and column in the pane to place one." : "This matrix has no maps placed in it.";
            IndoorMapViewerPlaceholder.IsVisible = true;
            IndoorViewerStatus.Text = $"Matrix {matrixId:D4} has no maps placed in it.";
            return;
        }

        IndoorMapViewer.IsVisible = true;
        _ = LoadIndoorCompositeMapsAsync(placements);
    }

    #region Matrix editing

    private bool switchingEditedMatrix;
    private bool loadingMatrixPane;
    private bool IsEditingMatrix => EditMatrixToggle.IsChecked == true;

    /// <summary>Edit Matrix turns the Whole Matrix view into the matrix editor: the squares are drawn
    /// over the maps, and clicking one edits its map, header and altitude in the pane. It replaces the
    /// old number-grid Matrix Editor page.</summary>
    private async void EditMatrixToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (IsEditingMatrix)
        {
            // Editing needs every square, not only the header's own maps.
            if (matrixHeaderScope) SetMatrixScope(headerScope: false);
            if (MapMatrixSelector.SelectedItem is not MatrixListEntry entry)
            {
                EditMatrixToggle.IsChecked = false;
                IndoorViewerStatus.Text = "Pick a matrix to edit first.";
                return;
            }

            if (currentMatrix?.Id != entry.Id) LoadMatrixForEditing(entry.Id);
            currentMapPane = MapPane.Matrix;
            if (MapViewHidePaneToggle.IsChecked == true)
            {
                MapViewHidePaneToggle.IsChecked = false;
                ApplyMapViewSplit();
            }

            ApplyMapPane();
            ShowMatrixComposite(GetMatrixPlacements(currentMatrix!), currentMatrix!.Id);
            return;
        }

        if (!await ConfirmLeavingEditedMatrixAsync())
        {
            EditMatrixToggle.IsChecked = true;
            return;
        }

        if (currentMapPane == MapPane.Matrix) currentMapPane = MapPane.Header;
        ApplyMapPane();
        if (MapMatrixSelector.SelectedItem is MatrixListEntry shown) ShowMatrixComposite(GetMatrixPlacements(shown.Id), shown.Id);
    }

    /// <summary>True when there is nothing unsaved in the matrix being edited, or the user chose what to
    /// do with it: save it, or throw the changes away.</summary>
    /// <summary>Saves a matrix and moves the events of maps that changed square with them (see <see cref="MatrixEventShiftService"/>); returns a note for the status line.</summary>
    private string SaveMatrixWithEvents(MatrixDocument matrix)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            MatrixService.Save(currentProject!, matrix);
            return string.Empty;
        }

        MatrixEventShiftService.Result result = MatrixEventShiftService.Save(currentProject, metadata, matrix);
        if (result.Moves.Count == 0) return string.Empty;
        string maps = string.Join(", ", result.Moves.Take(4).Select(move => $"{move.MapId:D4} ({move.FromColumn},{move.FromRow}) to ({move.ToColumn},{move.ToRow})"));
        string skipped = result.SkippedMoves > 0 ? $" {result.SkippedMoves} map(s) had no header to take events from." : string.Empty;
        return $" Moved {result.EventsMoved} event(s) with the map(s) that changed square: {maps}{(result.Moves.Count > 4 ? ", ..." : string.Empty)}.{skipped}";
    }

    private async Task<bool> ConfirmLeavingEditedMatrixAsync()
    {
        if (currentProject is null || currentMatrix is null || !matrixEdits.IsUnsaved(currentMatrix)) return true;
        string? choice = await new ChoiceDialog(
            "Unsaved matrix changes",
            $"Matrix {currentMatrix.Id:D4} has changes that are not saved.",
            ("save", "Save"), ("discard", "Discard Changes"), ("cancel", "Cancel")).ShowDialog<string?>(this);
        if (choice == "save")
        {
            string moved = SaveMatrixWithEvents(currentMatrix);
            matrixEdits.Saved(currentMatrix);
            StatusText.Text = $"Saved Matrix {currentMatrix.Id:D4}.{moved}";
            return true;
        }

        if (choice == "discard")
        {
            StatusText.Text = $"Discarded the unsaved changes to Matrix {currentMatrix.Id:D4}.";
            currentMatrix = null;
            return true;
        }

        return false;
    }

    private void LoadMatrixForEditing(int matrixId)
    {
        if (currentProject is null) return;
        currentMatrix = MatrixService.Load(currentProject, matrixId);
        matrixEdits.Loaded(currentMatrix, $"Matrix {currentMatrix.Id:D4}");
        MatrixOverlay.Select(0, 0);
        RefreshMatrixPane();
    }

    private void RefreshMatrixPane()
    {
        if (currentMatrix is null) return;
        EnsureMapEditorDataLoaded();
        loadingMatrixPane = true;
        try
        {
            MatrixPaneTitle.Text = $"Matrix {currentMatrix.Id:D4} - {currentMatrix.Width} x {currentMatrix.Height}";
            MatrixPaneName.Text = currentMatrix.Name;
            MatrixPaneWidth.Value = currentMatrix.Width;
            MatrixPaneHeight.Value = currentMatrix.Height;
            MatrixPaneHeadersSection.IsChecked = currentMatrix.HasHeadersSection;
            MatrixPaneAltitudesSection.IsChecked = currentMatrix.HasAltitudesSection;
            MatrixCellRow.Maximum = currentMatrix.Height - 1;
            MatrixCellColumn.Maximum = currentMatrix.Width - 1;

            if (matrixCellMapFilledFor != mapEntries.Count) FillMatrixCellMapChoices(MatrixDocument.EmptyMap);

            if (MatrixCellHeader.ItemCount != allHeaders.Count)
            {
                MatrixCellHeader.Items.Clear();
                foreach (HeaderListEntry header in allHeaders) MatrixCellHeader.Items.Add(header.ToString());
            }

            MatrixOverlay.SetMatrix(currentMatrix.Height, currentMatrix.Width, DescribeMatrixCell);
        }
        finally
        {
            loadingMatrixPane = false;
        }

        (int row, int column) = MatrixOverlay.Selected ?? (0, 0);
        SelectMatrixCell(Math.Min(row, currentMatrix.Height - 1), Math.Min(column, currentMatrix.Width - 1));
    }

    private string DescribeMatrixCell(int row, int column)
    {
        if (currentMatrix is null || row >= currentMatrix.Height || column >= currentMatrix.Width) return string.Empty;
        ushort map = currentMatrix.Maps[row, column];
        string text = map == MatrixDocument.EmptyMap ? "----" : map.ToString("D4");
        if (currentMatrix.HasHeadersSection) text += $"\nH{currentMatrix.Headers[row, column]:D3}";
        if (currentMatrix.HasAltitudesSection) text += $"\nA{currentMatrix.Altitudes[row, column]}";
        return text;
    }

    private readonly List<ushort> matrixCellMapChoices = [];
    private int matrixCellMapFilledFor = -1;

    /// <summary>Fills the square's map list with the maps matching the pane's search text (the empty choice and
    /// the given map always stay), without raising a selection change.</summary>
    private void FillMatrixCellMapChoices(ushort keep)
    {
        string query = MatrixPaneSearchInput.Text?.Trim() ?? string.Empty;
        bool previouslyLoading = loadingMatrixPane;
        loadingMatrixPane = true;
        try
        {
            matrixCellMapChoices.Clear();
            MatrixCellMap.Items.Clear();
            matrixCellMapChoices.Add(MatrixDocument.EmptyMap);
            MatrixCellMap.Items.Add("(empty)");
            foreach (MapViewEntry map in mapEntries)
            {
                string label = map.ToString();
                if (query.Length > 0 && map.Id != keep && !label.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                matrixCellMapChoices.Add((ushort)map.Id);
                MatrixCellMap.Items.Add(label);
            }

            matrixCellMapFilledFor = mapEntries.Count;
            MatrixCellMap.SelectedIndex = Math.Max(0, matrixCellMapChoices.IndexOf(keep));
        }
        finally
        {
            loadingMatrixPane = previouslyLoading;
        }
    }

    private void MatrixPaneSearchInput_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (MatrixCellMap is null || currentMatrix is null) return;
        ushort keep = SelectedMatrixCell is { } cell ? currentMatrix.Maps[cell.Row, cell.Column] : MatrixDocument.EmptyMap;
        FillMatrixCellMapChoices(keep);
        MatrixPaneSearchCount.Text = MatrixPaneSearchInput.Text?.Trim().Length > 0 ? $"{matrixCellMapChoices.Count - 1} maps" : string.Empty;
        if (MatrixPaneSearchInput.Text?.Trim().Length > 0) MatrixCellMap.IsDropDownOpen = true;
    }

    private List<MapPlacement> MatrixMapSearchMatches(TextBox box)
    {
        string query = box.Text?.Trim() ?? string.Empty;
        if (query.Length == 0) return [];
        EnsureMapEditorDataLoaded();
        return currentIndoorPlacements
            .Where(placement => placement.MapId.ToString("D4").Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                placement.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                                mapEntries.FirstOrDefault(entry => entry.Id == placement.MapId)?.ToString().Contains(query, StringComparison.OrdinalIgnoreCase) == true)
            .OrderBy(placement => placement.Row).ThenBy(placement => placement.Column)
            .ToList();
    }

    private void ShowMatrixMapSearchCount(TextBox box)
    {
        int count = MatrixMapSearchMatches(box).Count;
        string text = box.Text?.Trim().Length > 0 ? $"{count} square{(count == 1 ? "" : "s")}" : string.Empty;
        if (MatrixMapSearchCount is not null) MatrixMapSearchCount.Text = text;
    }

    private void MatrixMapSearchInput_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (sender is TextBox box) ShowMatrixMapSearchCount(box);
    }

    private void MatrixMapSearchInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || sender is not TextBox box) return;
        e.Handled = true;
        List<MapPlacement> matches = MatrixMapSearchMatches(box);
        if (matches.Count == 0)
        {
            IndoorViewerStatus.Text = $"No map in this matrix matches \"{box.Text?.Trim()}\".";
            return;
        }

        (int Row, int Column)? current = MatrixOverlay.Selected;
        int after = current is { } cell ? cell.Row * 100000 + cell.Column : -1;
        MapPlacement next = matches.FirstOrDefault(placement => placement.Row * 100000 + placement.Column > after, matches[0]);
        string position = $"{matches.IndexOf(next) + 1} of {matches.Count}";
        MatrixMapSearchCount.Text = position;
        if (IsEditingMatrix) SelectMatrixCell(next.Row, next.Column);
        else MatrixOverlay.Select(next.Row, next.Column);
        IndoorMapViewer.FocusOnGroundPoint(next.Column * MatrixGridOverlay.CellSize, next.Row * MatrixGridOverlay.CellSize);
        IndoorViewerStatus.Text = $"Map {next.MapId:D4} - {next.Name} at row {next.Row}, column {next.Column}.";
    }

    private void SelectMatrixCell(int row, int column)
    {
        if (currentMatrix is null || row < 0 || column < 0 || row >= currentMatrix.Height || column >= currentMatrix.Width) return;
        MatrixOverlay.Select(row, column);
        if (currentMatrix.HasHeadersSection && currentMatrix.Maps[row, column] != MatrixDocument.EmptyMap) SelectHeaderQuietly(currentMatrix.Headers[row, column]);
        loadingMatrixPane = true;
        try
        {
            MatrixCellRow.Value = row;
            MatrixCellColumn.Value = column;
            ushort map = currentMatrix.Maps[row, column];
            int mapIndex = matrixCellMapChoices.IndexOf(map);
            if (mapIndex < 0)
            {
                FillMatrixCellMapChoices(map);
                mapIndex = matrixCellMapChoices.IndexOf(map);
            }

            MatrixCellMap.SelectedIndex = mapIndex;
            MatrixCellHeader.IsEnabled = currentMatrix.HasHeadersSection;
            ushort header = currentMatrix.Headers[row, column];
            MatrixCellHeader.SelectedIndex = currentMatrix.HasHeadersSection && header < MatrixCellHeader.ItemCount ? header : -1;
            MatrixCellAltitude.IsEnabled = currentMatrix.HasAltitudesSection;
            MatrixCellAltitude.Value = currentMatrix.Altitudes[row, column];
        }
        finally
        {
            loadingMatrixPane = false;
        }
    }

    private (int Row, int Column)? SelectedMatrixCell =>
        currentMatrix is not null && MatrixOverlay.Selected is { } cell && cell.Row < currentMatrix.Height && cell.Column < currentMatrix.Width ? cell : null;

    private void IndoorMapViewer_ViewClicked(Point point)
    {
        if (!IsEditingMatrix || currentMatrix is null) return;
        if (IndoorMapViewer.TryPickGround(point, out float worldX, out float worldZ) &&
            MatrixOverlay.TryGetCell(worldX, worldZ, out int row, out int column))
        {
            SelectMatrixCell(row, column);
        }
    }

    private void MatrixCellPosition_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (loadingMatrixPane) return;
        SelectMatrixCell((int)(MatrixCellRow.Value ?? 0), (int)(MatrixCellColumn.Value ?? 0));
    }

    private void MatrixCellMap_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingMatrixPane || currentMatrix is null || SelectedMatrixCell is not { } cell || MatrixCellMap.SelectedIndex < 0) return;
        ushort map = matrixCellMapChoices[MatrixCellMap.SelectedIndex];
        if (currentMatrix.Maps[cell.Row, cell.Column] == map) return;
        currentMatrix.Maps[cell.Row, cell.Column] = map;
        MatrixEdited();
        // A different map in a square changes what the view has to draw: only that square.
        _ = UpdateMatrixSquareAsync(cell.Row, cell.Column);
    }

    private readonly SemaphoreSlim matrixSquareGate = new(1, 1);

    private async Task UpdateMatrixSquareAsync(int row, int column)
    {
        await matrixSquareGate.WaitAsync();
        try
        {
            if (currentMatrix is null || currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
            List<MapPlacement> previous = currentIndoorPlacements;
            List<MapPlacement> placements = GetMatrixPlacements(currentMatrix);
            MapPlacement? placement = placements.FirstOrDefault(item => item.Row == row && item.Column == column);
            var offset = new OpenTK.Mathematics.Vector3(column * MatrixCellWorldSize, 0f, row * MatrixCellWorldSize);
            bool buildings = !IsLargePlacementSet(placements);
            bool replaced = false;
            if (previous.Count > 0 && placements.Count > 0 && IsLargePlacementSet(previous) == !buildings && currentMatrix.Id == shownWholeMatrixId)
            {
                string? manifest = null;
                if (placement is not null)
                {
                    int? textureOverride = IndoorTextureSelector.SelectedIndex > 0 ? IndoorTextureSelector.SelectedIndex - 2 : null;
                    int? buildingTextureOverride = IndoorBuildingTextureSelector.SelectedIndex > 0 ? IndoorBuildingTextureSelector.SelectedIndex - 2 : null;
                    IndoorViewerStatus.Text = $"Converting map {placement.MapId:D4}...";
                    var (loaded, _) = await ConvertPlacementsAsync([placement], metadata, textureOverride, buildingTextureOverride);
                    manifest = loaded.Count > 0 ? loaded[0].ManifestPath : null;
                }

                if (placement is null || manifest is not null)
                {
                    ConfigureBuildingAnimations(IndoorMapViewer, interior: true);
                    replaced = IndoorMapViewer.ReplaceCompositeMap(offset, manifest, buildings);
                }
            }

            if (!replaced)
            {
                ShowMatrixComposite(placements, currentMatrix.Id);
                return;
            }

            currentIndoorPlacements = placements;
            ApplyMapAnimation(IndoorMapViewer, DynamicTypeOfPlacements(placements));
            RefreshCompositeEventMarkers(IndoorMapViewer, placements, headerView: false);
            IndoorViewerStatus.Text = placement is null ? $"Cleared square ({row}, {column})." : $"Square ({row}, {column}) now shows map {placement.MapId:D4}.";
        }
        catch (Exception ex)
        {
            LogUiError("Matrix square could not be redrawn", ex);
            IndoorViewerStatus.Text = ex.Message;
        }
        finally
        {
            matrixSquareGate.Release();
        }
    }

    private void MatrixCellHeader_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingMatrixPane || currentMatrix is null || SelectedMatrixCell is not { } cell || MatrixCellHeader.SelectedIndex < 0) return;
        currentMatrix.Headers[cell.Row, cell.Column] = (ushort)MatrixCellHeader.SelectedIndex;
        MatrixEdited();
    }

    private void MatrixCellAltitude_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (loadingMatrixPane || currentMatrix is null || SelectedMatrixCell is not { } cell) return;
        currentMatrix.Altitudes[cell.Row, cell.Column] = (byte)Math.Clamp((int)(MatrixCellAltitude.Value ?? 0), 0, 255);
        MatrixEdited();
    }

    private void MatrixPaneName_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (loadingMatrixPane || currentMatrix is null) return;
        currentMatrix.Name = MatrixPaneName.Text ?? string.Empty;
        MatrixEdited();
    }

    private void MatrixPaneSection_Click(object? sender, RoutedEventArgs e)
    {
        if (loadingMatrixPane || currentMatrix is null) return;
        currentMatrix.HasHeadersSection = MatrixPaneHeadersSection.IsChecked == true;
        currentMatrix.HasAltitudesSection = MatrixPaneAltitudesSection.IsChecked == true;
        MatrixEdited();
        RefreshMatrixPane();
    }

    private void MatrixPaneResize_Click(object? sender, RoutedEventArgs e)
    {
        if (currentMatrix is null) return;
        int width = (int)(MatrixPaneWidth.Value ?? currentMatrix.Width);
        int height = (int)(MatrixPaneHeight.Value ?? currentMatrix.Height);
        if (width == currentMatrix.Width && height == currentMatrix.Height) return;
        currentMatrix.Resize(height, width);
        MatrixEdited();
        RefreshMatrixPane();
        ShowMatrixComposite(GetMatrixPlacements(currentMatrix), currentMatrix.Id);
    }

    private void MatrixEdited()
    {
        MatrixOverlay.InvalidateVisual();
        ReportEdit(matrixEdits, () => currentMatrix, MatrixPaneStatus);
    }

    /// <summary>After adding or removing a matrix the selector's list is rebuilt from disk.</summary>
    private void ReloadMatrixSelector(int selectId)
    {
        switchingEditedMatrix = true;
        try
        {
            allMapMatrices.Clear();
            MapMatrixSelector.Items.Clear();
            EnsureMatrixSelectorLoaded();
            MapMatrixSelector.SelectedItem = allMapMatrices.FirstOrDefault(entry => entry.Id == selectId) ?? allMapMatrices.FirstOrDefault();
        }
        finally
        {
            switchingEditedMatrix = false;
        }

        if (MapMatrixSelector.SelectedItem is MatrixListEntry entry)
        {
            LoadMatrixForEditing(entry.Id);
            ShowMatrixComposite(GetMatrixPlacements(currentMatrix!), entry.Id);
        }
    }

    private async void MatrixPaneAdd_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || !await ConfirmLeavingEditedMatrixAsync()) return;
        int id = MatrixService.Add(currentProject);
        ReloadMatrixSelector(id);
        MatrixPaneStatus.Text = $"Added Matrix {id:D4}.";
    }

    private async void MatrixPaneCompleteTextures_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentMatrix is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        if (!await ConfirmLeavingEditedMatrixAsync()) return;
        if (currentMatrix is null) return;
        try
        {
            MatrixTexturePackService.Completion result = MatrixTexturePackService.Complete(currentProject, metadata, currentMatrix.Id);
            string added = result.AddedTextures.Count + result.AddedPalettes.Count == 0
                ? "Nothing to add: every texture the maps in this matrix use is already in its pack."
                : $"Added {result.AddedTextures.Count} texture(s) and {result.AddedPalettes.Count} palette(s) ({string.Join(", ", result.AddedTextures)}) to Map Texture Pack [{result.PackId:D2}] for map(s) {string.Join(", ", result.MapsServed.Select(map => map.ToString("D4")))}; the pack is now {result.PackBytes:N0} bytes.";
            string conflicts = result.Conflicts.Count > 0 ? $" Kept the pack's own for: {string.Join(", ", result.Conflicts)}." : string.Empty;
            string missing = result.StillMissing.Count > 0 ? $" Still missing: {string.Join("; ", result.StillMissing)}." : string.Empty;
            MatrixPaneStatus.Text = added + conflicts + missing;
            InvalidateCachedMapModels(Enumerable.Range(0, currentMatrix.Height).SelectMany(row => Enumerable.Range(0, currentMatrix.Width).Select(column => currentMatrix.Maps[row, column])).Where(map => map != MatrixDocument.EmptyMap).Distinct().ToArray());
            if (MapMatrixSelector.SelectedItem is MatrixListEntry shown) ShowMatrixComposite(GetMatrixPlacements(shown.Id), shown.Id);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or InvalidDataException)
        {
            LogUiError("The matrix's texture pack could not be completed", ex);
            MatrixPaneStatus.Text = $"The matrix's texture pack could not be completed: {ex.Message}";
        }
    }

    private async void MatrixPaneRemoveLast_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || !await ConfirmLeavingEditedMatrixAsync()) return;
        if (!MatrixService.RemoveLast(currentProject))
        {
            MatrixPaneStatus.Text = "At least one matrix must be kept.";
            return;
        }

        ReloadMatrixSelector(currentMatrix?.Id ?? 0);
        MatrixPaneStatus.Text = "Removed the last matrix.";
    }

    private async void MatrixPaneImport_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentMatrix is null) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Matrix") { Patterns = ["*.mtx", "*"] }]
        });
        if (files.Count != 1) return;

        try
        {
            // Replaces the matrix being edited in memory; Save Map writes it.
            currentMatrix = MatrixService.LoadPath(files[0].Path.LocalPath, currentMatrix.Id);
            MatrixEdited();
            RefreshMatrixPane();
            ShowMatrixComposite(GetMatrixPlacements(currentMatrix), currentMatrix.Id);
        }
        catch (Exception ex)
        {
            LogUiError("Matrix could not be imported", ex);
            MatrixPaneStatus.Text = ex.Message;
        }
    }

    private async void MatrixPaneExport_Click(object? sender, RoutedEventArgs e)
    {
        if (currentMatrix is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"Matrix {currentMatrix.Id:D4}.mtx",
            FileTypeChoices = [new FilePickerFileType("Matrix") { Patterns = ["*.mtx"] }]
        });
        if (file is null) return;
        File.WriteAllBytes(file.Path.LocalPath, currentMatrix.ToByteArray());
        MatrixPaneStatus.Text = $"Exported Matrix {currentMatrix.Id:D4}.";
    }

    #endregion

    /// <summary>Populates MapSelector's flat map file list once, lazily, the first time the merged
    /// Header/Map page is visited - LoadSelectedHeader needs mapEntries/MapSelector.Items already
    /// populated before it can jump MapSelector to whichever map the selected header links to.</summary>
    private void EnsureMapEditorDataLoaded()
    {
        if (mapEntries.Count > 0) return;
        ConfigureMapEditor(autoSelectFirst: false);
    }

    private void ConfigureMapEditor(bool autoSelectFirst = true)
    {
        if (currentProject is null) return;
        mapEntries.Clear();
        MapSelector.Items.Clear();
        if (!currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            MapViewerStatus.Text = "Game metadata is unavailable; maps cannot be identified.";
            return;
        }

        loadingMapEditor = true;
        try
        {
            MapTextureSelector.Items.Clear();
            MapTextureSelector.Items.Add("Untextured");
            int texturePackCount = Directory.Exists(currentProject.MapTexturesDirectory)
                ? Directory.GetFiles(currentProject.MapTexturesDirectory).Length
                : 0;
            for (int i = 0; i < texturePackCount; i++)
            {
                MapTextureSelector.Items.Add($"Map Texture Pack [{i:D2}]");
            }

            MapBuildingTextureSelector.Items.Clear();
            MapBuildingTextureSelector.Items.Add("Untextured");
            int buildingTexturePackCount = Directory.Exists(currentProject.BuildingTexturesDirectory)
                ? Directory.GetFiles(currentProject.BuildingTexturesDirectory).Length
                : 0;
            for (int i = 0; i < buildingTexturePackCount; i++)
            {
                MapBuildingTextureSelector.Items.Add($"Building Texture Pack [{i:D2}]");
            }

            BuildingInteriorExteriorPanel.IsVisible = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;

            mapAreaData = headerStore is not null
                ? AreaDataService.ResolveMapAreaData(
                    currentProject,
                    headerStore.Load,
                    currentProject.GetHeaderCount(),
                    metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver)
                : new Dictionary<int, AreaDataRecord>();

            mapEventFileIds = headerStore is not null
                ? MapEventService.ResolveMapEventFileIds(currentProject, headerStore.Load, currentProject.GetHeaderCount())
                : new Dictionary<int, int>();

            EnsureLocationLookupsLoaded();
            Dictionary<ushort, string> locationLabels = currentProject.GetLocationNames(metadata)
                .GroupBy(option => option.Id)
                .ToDictionary(group => (ushort)group.Key, group => group.First().Label);
            mapEntries.AddRange(MapViewService.GetMaps(currentProject, metadata).Select(map =>
                mapLocationNameIds!.TryGetValue(map.Id, out ushort locationNameId)
                && locationLabels.TryGetValue(locationNameId, out string? location)
                && !string.IsNullOrWhiteSpace(location)
                    ? map with { LocationName = location }
                    : map));
            foreach (MapViewEntry map in mapEntries) MapSelector.Items.Add(map);
        }
        finally
        {
            loadingMapEditor = false;
        }

        if (MapSelector.Items.Count > 0)
        {
            if (autoSelectFirst) MapSelector.SelectedIndex = 0;
        }
        else
        {
            MapViewerStatus.Text = "No extracted map files found.";
        }
    }

    /// <summary>Falls back to the pack the original editor preselects when a map has no area data.</summary>
    private int GetDefaultTexturePack(MapViewEntry map, RomGameMetadata metadata) =>
        mapAreaData.TryGetValue(map.Id, out AreaDataRecord? record)
            ? record.MapTileset
            : metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver ? 2 : 6;

    /// <summary>Falls back to pack 0, the original editor's own default building texture selection
    /// (buildTextureComboBox.SelectedIndex = 1 in DS_Map/Editors/MapEditor.cs), when a map has no
    /// area data.</summary>
    private int GetDefaultBuildingTexturePack(MapViewEntry map) =>
        mapAreaData.TryGetValue(map.Id, out AreaDataRecord? record) ? record.BuildingsTileset : 0;

    /// <summary>HGSS indoor maps (area type 0) place interior building models, as the legacy Event
    /// Editor resolves it; DP and Platinum use the exterior set everywhere.</summary>
    private bool UsesInteriorBuildings(int mapId, RomGameMetadata metadata) =>
        metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver &&
        mapAreaData.TryGetValue(mapId, out AreaDataRecord? record) && record.AreaType == AreaDataRecord.TypeIndoor;

    private List<int> MapSearchMatches()
    {
        string query = MapSearchInput.Text?.Trim() ?? string.Empty;
        if (query.Length == 0) return [];
        return Enumerable.Range(0, mapEntries.Count)
            .Where(index => mapEntries[index].ToString().Contains(query, StringComparison.OrdinalIgnoreCase))
            .ToList();
    }

    private void MapSearchInput_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (MapSearchCount is null) return;
        EnsureMapEditorDataLoaded();
        List<int> matches = MapSearchMatches();
        MapSearchCount.Text = MapSearchInput.Text?.Trim().Length > 0 ? $"{matches.Count} match{(matches.Count == 1 ? "" : "es")}" : string.Empty;
    }

    private void MapSearchInput_KeyDown(object? sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        e.Handled = true;
        JumpToNextMapSearchMatch();
    }

    private void MapSearchNext_Click(object? sender, RoutedEventArgs e) => JumpToNextMapSearchMatch();

    /// <summary>Selecting a map loads it, so the search only moves the selection on request (Enter or
    /// the arrow), to the next match after the current map, wrapping around.</summary>
    private void JumpToNextMapSearchMatch()
    {
        EnsureMapEditorDataLoaded();
        List<int> matches = MapSearchMatches();
        if (matches.Count == 0)
        {
            MapViewerStatus.Text = $"No map matches \"{MapSearchInput.Text?.Trim()}\".";
            return;
        }

        int next = matches.FirstOrDefault(index => index > MapSelector.SelectedIndex, matches[0]);
        MapSearchCount.Text = $"{matches.IndexOf(next) + 1} of {matches.Count}";
        MapSelector.SelectedIndex = next;
    }

    private void MapTextureSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingMapEditor || settingMapTexturePacks) return;
        LoadSelectedMap();
    }

    private async void MapBuildingTextureSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingMapEditor || settingMapTexturePacks) return;
        LoadSelectedMap();

        // Which models render as themselves vs fall back to dmybox00 (see RefreshBuildingModelList)
        // depends on the building texture pack just picked, so the Model combo's filtered contents
        // need to catch up too.
        if (!loadingBuildingEditor)
        {
            await RefreshBuildingModelList();
            SyncModelSelectorToSelectedBuilding();
        }
    }

    /// <summary>Re-points BuildingModelSelector's selection at the currently-selected building's actual
    /// model ID, looked up by ID (not combo position) since currentBuildingModels may be filtered. Call
    /// after anything that repopulates the combo without the user having picked a new model themselves
    /// - guarded by loadingBuildingEditor so the resync isn't mistaken for a real selection and doesn't
    /// rewrite the building's model or trigger a re-export.</summary>
    private void SyncModelSelectorToSelectedBuilding()
    {
        BuildingRecord? building = SelectedBuilding;
        if (building is null) return;

        loadingBuildingEditor = true;
        try
        {
            BuildingModelSelector.SelectedIndex = currentBuildingModels.FindIndex(model => model.Id == (int)building.ModelId);
        }
        finally
        {
            loadingBuildingEditor = false;
        }
    }

    private void ConfigureNsbtxEditor()
    {
        if (currentProject is null)
        {
            NsbtxStatusText.Text = "No ROM project is open.";
            return;
        }

        NsbtxTexturePacksList.Items.Clear();
        NsbtxTextureGrid.Children.Clear();
        NsbtxPaletteGrid.Children.Clear();
        NsbtxStatusText.Text = "Loading texture packs...";
        nsbtxMapModelPairs = null;
        nsbtxBuildingModelPairs = null;

        string tilesetDirectory = GetNsbtxTilesetDirectory();
        nsbtxPackPaths.Clear();

        if (Directory.Exists(tilesetDirectory))
        {
            foreach (string file in Directory.GetFiles(tilesetDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                nsbtxPackPaths.Add(file);
            }
        }

        LoadAreaDataUsage();
        foreach (string packPath in nsbtxPackPaths)
        {
            int packId = int.TryParse(Path.GetFileName(packPath), out int id) ? id : -1;
            int users = AreaDataUsingPack(packId).Count;
            NsbtxTexturePacksList.Items.Add($"Texture Pack {Path.GetFileName(packPath)} - {(users == 0 ? "not used" : users == 1 ? "1 area data" : $"{users} area data")}");
        }

        if (NsbtxTexturePacksList.Items.Count > 0)
        {
            NsbtxTexturePacksList.SelectedIndex = 0;
        }
        else
        {
            NsbtxStatusText.Text = "No NSBTX texture packs were found in the unpacked project files.";
        }

        ConfigureAreaDataEditor();
    }

    private void NsbtxTexturePackType_Checked(object? sender, RoutedEventArgs e)
    {
        if (loadingNsbtx || currentProject is null)
        {
            return;
        }

        ConfigureNsbtxEditor();
    }

    private async void NsbtxTexturePacksList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingNsbtx || switchingDocument || currentProject is null || NsbtxTexturePacksList.SelectedIndex < 0 || NsbtxTexturePacksList.SelectedIndex >= nsbtxPackPaths.Count)
        {
            return;
        }

        int packIndex = NsbtxTexturePacksList.SelectedIndex;
        int open = currentNsbtxPackPath is null ? -1 : nsbtxPackPaths.IndexOf(currentNsbtxPackPath);
        if (!await ShouldLoadDocumentAsync(packIndex == open, "Texture Editor", () => NsbtxTexturePacksList.SelectedIndex = open,
            change => change.Area == "Texture pack palettes")) return;
        RunSwitchingDocument(() => NsbtxTexturePacksList.SelectedIndex = packIndex);

        loadingNsbtx = true;
        try
        {
            string path = nsbtxPackPaths[NsbtxTexturePacksList.SelectedIndex];
            currentNsbtxPackPath = path;
            NsbtxPackUsage.Text = DescribePackUsage(int.TryParse(Path.GetFileName(path), out int packId) ? packId : -1);
            string discarded = string.Join(", ", nsbtxDirtyPalettes.Where(index => currentNsbtxPack is not null && index < currentNsbtxPack.Palettes.Count).Select(index => currentNsbtxPack!.Palettes[index].Name));
            currentNsbtxPack = NsbtxTexturePack.Load(path);
            nsbtxChosenPalettes.Clear();
            nsbtxDirtyPalettes.Clear();
            nsbtxEditedColor = -1;
            nsbtxSelectedTexture = -1;
            nsbtxPaletteSlots.Clear();
            try
            {
                foreach (NsbtxPaletteLayout.PaletteSlot slot in NsbtxPaletteLayout.Read(File.ReadAllBytes(path))) nsbtxPaletteSlots.TryAdd(slot.Name, slot);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DSPRE] Palettes of {path} could not be located for editing: {ex.Message}");
            }

            if (discarded.Length > 0) Console.Error.WriteLine($"[DSPRE] Unsaved palette changes discarded: {discarded}");
            if (currentNsbtxPack is not null)
            {
                TrimNsbtxPalettesToFile(currentNsbtxPack);
                ResolveNsbtxModelPalettes(currentNsbtxPack, int.TryParse(Path.GetFileName(path), out int resolvedPack) ? resolvedPack : -1);
            }
            if (currentNsbtxPack is null)
            {
                NsbtxTextureGrid.Children.Clear();
                NsbtxPaletteGrid.Children.Clear();
                NsbtxStatusText.Text = $"Could not read NSBTX file: {Path.GetFileName(path)}";
                return;
            }

            NsbtxStatusText.Text = $"Loaded {Path.GetFileName(path)} ({currentNsbtxPack.Textures.Count} textures, {currentNsbtxPack.Palettes.Count} palettes).";
        }
        finally
        {
            loadingNsbtx = false;
        }

        RefreshNsbtxTextureGrid();
        if (currentNsbtxPack is { Textures.Count: > 0 }) SelectNsbtxTexture(0);
    }

    // The palette each texture is currently drawn with. An NSBTX does not store which palette goes
    // with which texture (the models that use the pack do), so this starts from the name match and
    // only lasts while the pack is open.
    private readonly Dictionary<int, int> nsbtxChosenPalettes = [];
    private int nsbtxSelectedTexture = -1;
    // Palette editing works on the loaded pack's own colour arrays, so every texture drawn with an
    // edited palette shows the change at once; nothing reaches the file until Save.
    private readonly Dictionary<string, NsbtxPaletteLayout.PaletteSlot> nsbtxPaletteSlots = [];
    private readonly HashSet<int> nsbtxDirtyPalettes = [];
    // What the models say: which palette each texture of the open pack is drawn with. Read from the
    // map and building models once per editor visit; narrowed to the open pack on selection.
    private Dictionary<int, List<NsbmdMaterialPairs.Pair>>? nsbtxMapModelPairs;
    private List<NsbmdMaterialPairs.Pair>? nsbtxBuildingModelPairs;
    private readonly Dictionary<string, List<string>> nsbtxModelPalettes = new(StringComparer.Ordinal);
    private int nsbtxEditedColor = -1;
    private bool loadingNsbtxColor;

    private static readonly IBrush NsbtxTileBorderBrush = new SolidColorBrush(Color.Parse("#33414D"));
    private static readonly IBrush NsbtxTileSelectedBrush = new SolidColorBrush(Color.Parse("#65D8C7"));

    private int GetNsbtxPaletteFor(int textureIndex)
    {
        if (currentNsbtxPack is null) return 0;
        if (nsbtxChosenPalettes.TryGetValue(textureIndex, out int chosen)) return chosen;
        return Math.Max(0, NsbtxOwnPalette(textureIndex));
    }

    /// <summary>How many colours a texture format indexes, so palettes too short for it are left out
    /// of the palette panel. 0 means any palette can be tried (the 4x4 compressed format picks its
    /// colours from anywhere in the palette); -1 is direct colour, which uses no palette at all.</summary>
    private static int GetNsbtxPaletteColorsNeeded(int format) => format switch
    {
        1 => 32,
        2 => 4,
        3 => 16,
        4 => 256,
        6 => 8,
        7 => -1,
        _ => 0
    };

    private Border CreateNsbtxTile(Bitmap? image, string caption, bool selected, Action onClick)
    {
        var content = new StackPanel { Spacing = 4 };
        if (image is not null)
        {
            var picture = new Image { Source = image, Stretch = Stretch.None, HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center };
            RenderOptions.SetBitmapInterpolationMode(picture, BitmapInterpolationMode.None);
            content.Children.Add(picture);
        }

        content.Children.Add(new TextBlock
        {
            Text = caption,
            FontSize = 11,
            HorizontalAlignment = global::Avalonia.Layout.HorizontalAlignment.Center,
            Foreground = image is null ? Brushes.IndianRed : null
        });

        var tile = new Border
        {
            Child = content,
            Background = Brushes.Transparent,
            BorderBrush = selected ? NsbtxTileSelectedBrush : NsbtxTileBorderBrush,
            BorderThickness = new Thickness(2),
            CornerRadius = new CornerRadius(3),
            Padding = new Thickness(6),
            Margin = new Thickness(0, 0, 8, 8),
            Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand)
        };
        ToolTip.SetTip(tile, caption);
        tile.PointerPressed += (_, _) => onClick();
        return tile;
    }

    private Bitmap? TryRenderNsbtx(int textureIndex, int paletteIndex)
    {
        try
        {
            return currentNsbtxPack?.Render(textureIndex, paletteIndex, NsbtxScaleSlider.Value);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Texture {textureIndex} could not be drawn with palette {paletteIndex}: {ex.Message}");
            return null;
        }
    }

    private void RefreshNsbtxTextureGrid()
    {
        NsbtxTextureGrid.Children.Clear();
        if (currentNsbtxPack is null) return;
        for (int i = 0; i < currentNsbtxPack.Textures.Count; i++)
        {
            int textureIndex = i;
            NsbtxTextureEntry texture = currentNsbtxPack.Textures[i];
            NsbtxTextureGrid.Children.Add(CreateNsbtxTile(
                TryRenderNsbtx(i, GetNsbtxPaletteFor(i)), texture.Name, i == nsbtxSelectedTexture, () => SelectNsbtxTexture(textureIndex)));
        }
    }

    private void SelectNsbtxTexture(int textureIndex)
    {
        if (currentNsbtxPack is null || textureIndex < 0 || textureIndex >= currentNsbtxPack.Textures.Count) return;
        nsbtxSelectedTexture = textureIndex;
        for (int i = 0; i < NsbtxTextureGrid.Children.Count; i++)
        {
            if (NsbtxTextureGrid.Children[i] is Border tile) tile.BorderBrush = i == textureIndex ? NsbtxTileSelectedBrush : NsbtxTileBorderBrush;
        }

        RefreshNsbtxPalettePanel();
    }

    /// <summary>Draws the selected texture once per palette it can use; clicking one makes it the
    /// palette that texture is shown with in the grid.</summary>
    private void RefreshNsbtxPalettePanel()
    {
        NsbtxPaletteGrid.Children.Clear();
        if (currentNsbtxPack is null || nsbtxSelectedTexture < 0 || nsbtxSelectedTexture >= currentNsbtxPack.Textures.Count)
        {
            NsbtxPaletteHint.Text = "Select a texture to see it in each palette it can use.";
            return;
        }

        int textureIndex = nsbtxSelectedTexture;
        NsbtxTextureEntry texture = currentNsbtxPack.Textures[textureIndex];
        int needed = GetNsbtxPaletteColorsNeeded(texture.Format);
        if (needed < 0)
        {
            NsbtxPaletteHint.Text = $"{texture.Name} ({texture.Width}x{texture.Height}) stores its colours directly and uses no palette.";
            return;
        }

        int used = NsbtxUsedColors(texture);
        List<int> usable = Enumerable.Range(0, currentNsbtxPack.Palettes.Count)
            .Where(index => NsbtxPaletteColorCount(index) >= used)
            .ToList();
        // A pack whose palettes are all shorter than the format nominally needs still has to offer something.
        if (usable.Count == 0) usable = Enumerable.Range(0, currentNsbtxPack.Palettes.Count).ToList();

        int current = GetNsbtxPaletteFor(textureIndex);
        foreach (int paletteIndex in usable)
        {
            int index = paletteIndex;
            NsbtxPaletteGrid.Children.Add(CreateNsbtxTile(
                TryRenderNsbtx(textureIndex, paletteIndex), currentNsbtxPack.Palettes[paletteIndex].Name, paletteIndex == current, () => ChooseNsbtxPalette(textureIndex, index)));
        }

        string currentName = current < currentNsbtxPack.Palettes.Count ? currentNsbtxPack.Palettes[current].Name : "none";
        NsbtxPaletteHint.Text = usable.Count == 0
            ? $"{texture.Name} ({texture.Width}x{texture.Height}): this pack has no palettes."
            : $"{texture.Name} ({texture.Width}x{texture.Height}) uses {used} colours, shown with {currentName}. {usable.Count} of {currentNsbtxPack.Palettes.Count} palettes have enough; click one to preview it.";
        RefreshNsbtxPaletteEditor();
    }

    /// <summary>The palette the game draws a texture with: the one named after it. Which palette is
    /// previewed above does not change that, so edits always go to this one.</summary>
    private int NsbtxOwnPalette(int textureIndex)
    {
        if (currentNsbtxPack is null || textureIndex < 0 || textureIndex >= currentNsbtxPack.Textures.Count) return -1;
        string name = currentNsbtxPack.Textures[textureIndex].Name;
        if (nsbtxModelPalettes.TryGetValue(name, out List<string>? palettes))
        {
            int index = currentNsbtxPack.Palettes.FindIndex(palette => palette.Name == palettes[0]);
            if (index >= 0) return index;
        }

        return FindMatchingNsbtxPalette(name);
    }

    /// <summary>The palette edits go to: the texture's own, or - when neither a model nor a name ties
    /// one to it - the palette being previewed.</summary>
    private int NsbtxEditTarget(int textureIndex)
    {
        int own = NsbtxOwnPalette(textureIndex);
        return own >= 0 ? own : GetNsbtxPaletteFor(textureIndex);
    }

    /// <summary>The viewer's NSBTX reader guesses each palette's length from the next palette's start
    /// in list order, which is not storage order, so palettes could run on into their neighbours'
    /// colours (or garbage, for the last). Cut each to its real size in the file.</summary>
    private void TrimNsbtxPalettesToFile(NsbtxTexturePack pack)
    {
        for (int i = 0; i < pack.Palettes.Count; i++)
        {
            NsbtxPaletteEntry palette = pack.Palettes[i];
            if (!nsbtxPaletteSlots.TryGetValue(palette.Name, out NsbtxPaletteLayout.PaletteSlot? slot) || palette.Colors.Length <= slot.Length / 2) continue;
            pack.Palettes[i] = new NsbtxPaletteEntry { Index = palette.Index, Name = palette.Name, Length = slot.Length, Colors = palette.Colors[..(slot.Length / 2)] };
        }
    }

    /// <summary>Collects, for each texture of the pack, the palettes the models that use this pack draw
    /// it with: for a map pack, the terrain models of the maps whose area data picks it (every map when
    /// none does); for a building pack, the building models.</summary>
    private void ResolveNsbtxModelPalettes(NsbtxTexturePack pack, int packId)
    {
        nsbtxModelPalettes.Clear();
        if (currentProject is null) return;
        try
        {
            IEnumerable<NsbmdMaterialPairs.Pair> pairs;
            if (CurrentTexturePackKind == TexturePackKind.Map)
            {
                nsbtxMapModelPairs ??= NsbmdMaterialPairs.ReadMapPairs(currentProject);
                EnsureMapEditorDataLoaded();
                List<int> maps = mapAreaData.Where(entry => entry.Value.MapTileset == packId).Select(entry => entry.Key).ToList();
                pairs = (maps.Count > 0 ? maps : nsbtxMapModelPairs.Keys.ToList())
                    .SelectMany(mapId => nsbtxMapModelPairs.TryGetValue(mapId, out List<NsbmdMaterialPairs.Pair>? list) ? list : []);
            }
            else
            {
                pairs = nsbtxBuildingModelPairs ??= NsbmdMaterialPairs.ReadBuildingPairs(currentProject);
            }

            var inPack = pack.Palettes.Select(palette => palette.Name).ToHashSet(StringComparer.Ordinal);
            foreach (IGrouping<string, NsbmdMaterialPairs.Pair> texture in pairs.Where(pair => inPack.Contains(pair.Palette)).GroupBy(pair => pair.Texture))
            {
                // Most-used first, so a texture some maps draw with another palette still opens on the usual one.
                nsbtxModelPalettes[texture.Key] = texture.GroupBy(pair => pair.Palette).OrderByDescending(group => group.Count()).Select(group => group.Key).ToList();
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Model palettes could not be read for texture pack {packId}: {ex.Message}");
        }
    }

    /// <summary>How many colours of its palette a texture really indexes (its highest index + 1), so a
    /// palette offered for it has a colour for every texel. 4x4-compressed textures address the palette
    /// per 4x4 block, at an offset stored with each block.</summary>
    private static int NsbtxUsedColors(NsbtxTextureEntry texture)
    {
        byte[] data = texture.Source;
        int max = -1;
        switch (texture.Format)
        {
            case 1: foreach (byte b in data) max = Math.Max(max, b & 0x1F); break;
            case 2: foreach (byte b in data) for (int shift = 0; shift < 8; shift += 2) max = Math.Max(max, (b >> shift) & 3); break;
            case 3: foreach (byte b in data) max = Math.Max(max, Math.Max(b & 0xF, b >> 4)); break;
            case 4: foreach (byte b in data) max = Math.Max(max, b); break;
            case 6: foreach (byte b in data) max = Math.Max(max, b & 7); break;
            case 5:
                for (int i = 0; i + 1 < texture.SpData.Length; i += 2)
                {
                    int block = texture.SpData[i] | texture.SpData[i + 1] << 8;
                    int mode = block >> 14;
                    // The offset is in 4-byte units (two colours); modes 0 and 2 read four colours, 1 and 3 two.
                    max = Math.Max(max, (block & 0x3FFF) * 2 + (mode is 0 or 2 ? 4 : 2) - 1);
                }
                break;
        }

        return max + 1;
    }

    /// <summary>Colours in a palette: from its real size in the file where known (the viewer's own
    /// reader cannot tell where the last palette ends).</summary>
    private int NsbtxPaletteColorCount(int paletteIndex)
    {
        NsbtxPaletteEntry palette = currentNsbtxPack!.Palettes[paletteIndex];
        return nsbtxPaletteSlots.TryGetValue(palette.Name, out NsbtxPaletteLayout.PaletteSlot? slot)
            ? Math.Min(slot.Length / 2, palette.Colors.Length)
            : palette.Colors.Length;
    }

    private void RefreshNsbtxPaletteEditor()
    {
        int textureIndex = nsbtxSelectedTexture;
        int own = NsbtxEditTarget(textureIndex);
        if (currentNsbtxPack is null || textureIndex < 0 || own < 0 || own >= currentNsbtxPack.Palettes.Count ||
            GetNsbtxPaletteColorsNeeded(currentNsbtxPack.Textures[textureIndex].Format) < 0)
        {
            NsbtxPaletteEditor.IsVisible = false;
            return;
        }

        NsbtxPaletteEditor.IsVisible = true;
        NsbtxTextureEntry texture = currentNsbtxPack.Textures[textureIndex];
        NsbtxPaletteEntry ownPalette = currentNsbtxPack.Palettes[own];
        bool editable = nsbtxPaletteSlots.ContainsKey(ownPalette.Name);
        NsbtxOwnPaletteLabel.Text = $"Palette {ownPalette.Name}{(nsbtxDirtyPalettes.Contains(own) ? " - not saved" : string.Empty)}";

        List<string> sharers = Enumerable.Range(0, currentNsbtxPack.Textures.Count)
            .Where(index => index != textureIndex && NsbtxOwnPalette(index) == own)
            .Select(index => currentNsbtxPack.Textures[index].Name)
            .ToList();
        string source;
        if (nsbtxModelPalettes.TryGetValue(texture.Name, out List<string>? modelPalettes) && modelPalettes[0] == ownPalette.Name)
        {
            string models = CurrentTexturePackKind == TexturePackKind.Map ? "The maps using this pack" : "The building models";
            source = $"{models} draw {texture.Name} with this palette, whichever one is previewed above" +
                (modelPalettes.Count > 1 ? $" (some use {string.Join(", ", modelPalettes.Skip(1))} instead). " : ". ");
        }
        else if (NsbtxOwnPalette(textureIndex) == own)
        {
            source = $"No model in this project uses {texture.Name}; this palette is assumed from its name. ";
        }
        else
        {
            source = $"No model in this project uses {texture.Name} and no palette is named after it, so edits go to the palette previewed above. ";
        }

        int usedColors = NsbtxUsedColors(texture);
        if (NsbtxPaletteColorCount(own) < usedColors)
        {
            source += $"{texture.Name} uses {usedColors} colours but {ownPalette.Name} only has {NsbtxPaletteColorCount(own)}; the rest are read from whatever palette follows it, so only the first {NsbtxPaletteColorCount(own)} can be changed here. ";
        }

        NsbtxOwnPaletteNote.Text = source +
            (sharers.Count == 0 ? "No other texture in this pack uses it." : $"Changing it also changes {string.Join(", ", sharers)}.") +
            (editable ? " Click a colour to edit it." : " Its place in the file could not be found, so it cannot be saved.");

        NsbtxPaletteSwatches.Children.Clear();
        int colorCount = NsbtxPaletteColorCount(own);
        if (nsbtxEditedColor >= colorCount) nsbtxEditedColor = -1;
        for (int i = 0; i < colorCount; i++)
        {
            int colorIndex = i;
            uint argb = ownPalette.Colors[i];
            var swatch = new Border
            {
                Width = 16,
                Height = 16,
                Margin = new Thickness(0, 0, 2, 2),
                Background = new SolidColorBrush(Color.FromUInt32(argb)),
                BorderBrush = i == nsbtxEditedColor ? NsbtxTileSelectedBrush : NsbtxTileBorderBrush,
                BorderThickness = new Thickness(i == nsbtxEditedColor ? 2 : 1),
                Cursor = new global::Avalonia.Input.Cursor(global::Avalonia.Input.StandardCursorType.Hand)
            };
            ToolTip.SetTip(swatch, $"Colour {i}");
            swatch.PointerPressed += (_, _) =>
            {
                nsbtxEditedColor = colorIndex;
                RefreshNsbtxPaletteEditor();
            };
            NsbtxPaletteSwatches.Children.Add(swatch);
        }

        NsbtxColorEditor.IsVisible = editable && nsbtxEditedColor >= 0;
        if (NsbtxColorEditor.IsVisible)
        {
            uint argb = ownPalette.Colors[nsbtxEditedColor];
            loadingNsbtxColor = true;
            NsbtxColorRed.Value = ((argb >> 16) & 0xFF) >> 3;
            NsbtxColorGreen.Value = ((argb >> 8) & 0xFF) >> 3;
            NsbtxColorBlue.Value = (argb & 0xFF) >> 3;
            loadingNsbtxColor = false;
            NsbtxColorPreview.Background = new SolidColorBrush(Color.FromUInt32(argb));
        }

        int shown = GetNsbtxPaletteFor(textureIndex);
        NsbtxCopyPaletteButton.IsVisible = shown != own && shown < currentNsbtxPack.Palettes.Count;
        NsbtxCopyPaletteButton.IsEnabled = editable;
        NsbtxCopyPaletteButton.Content = $"Copy {(shown < currentNsbtxPack.Palettes.Count ? currentNsbtxPack.Palettes[shown].Name : "?")}'s colours into {ownPalette.Name}";
        NsbtxSavePaletteButton.IsEnabled = nsbtxDirtyPalettes.Count > 0;
        NsbtxRevertPaletteButton.IsEnabled = nsbtxDirtyPalettes.Count > 0;
    }

    /// <summary>Gives the texture the look of the previewed palette by copying its colours into the
    /// palette the texture really uses.</summary>
    private void NsbtxCopyPalette_Click(object? sender, RoutedEventArgs e)
    {
        int textureIndex = nsbtxSelectedTexture;
        int own = NsbtxEditTarget(textureIndex);
        int shown = GetNsbtxPaletteFor(textureIndex);
        if (currentNsbtxPack is null || own < 0 || shown == own || shown >= currentNsbtxPack.Palettes.Count) return;

        uint[] target = currentNsbtxPack.Palettes[own].Colors;
        uint[] source = currentNsbtxPack.Palettes[shown].Colors;
        int count = Math.Min(NsbtxPaletteColorCount(own), NsbtxPaletteColorCount(shown));
        Array.Copy(source, target, count);
        nsbtxDirtyPalettes.Add(own);
        nsbtxChosenPalettes[textureIndex] = own;
        RefreshNsbtxTextureGrid();
        RefreshNsbtxPalettePanel();
        NsbtxStatusText.Text = $"Copied {count} colours of {currentNsbtxPack.Palettes[shown].Name} into {currentNsbtxPack.Palettes[own].Name}. Save palette to keep it.";
    }

    private void NsbtxColorChannel_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (loadingNsbtxColor || currentNsbtxPack is null || nsbtxEditedColor < 0) return;
        int own = NsbtxEditTarget(nsbtxSelectedTexture);
        if (own < 0 || own >= currentNsbtxPack.Palettes.Count || nsbtxEditedColor >= currentNsbtxPack.Palettes[own].Colors.Length) return;

        uint Channel(NumericUpDown input) => (uint)Math.Clamp((int)(input.Value ?? 0), 0, 31) << 3;
        currentNsbtxPack.Palettes[own].Colors[nsbtxEditedColor] = 0xFF000000 | Channel(NsbtxColorRed) << 16 | Channel(NsbtxColorGreen) << 8 | Channel(NsbtxColorBlue);
        nsbtxDirtyPalettes.Add(own);
        nsbtxChosenPalettes[nsbtxSelectedTexture] = own;
        RefreshNsbtxTextureGrid();
        RefreshNsbtxPalettePanel();
    }

    private void NsbtxSavePalette_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentNsbtxPack is null || currentNsbtxPackPath is null || nsbtxDirtyPalettes.Count == 0) return;
        int packId = int.Parse(Path.GetFileName(currentNsbtxPackPath));
        var saved = new List<string>();
        try
        {
            foreach (int paletteIndex in nsbtxDirtyPalettes.Order())
            {
                NsbtxPaletteEntry palette = currentNsbtxPack.Palettes[paletteIndex];
                // Stored as BGR555: five bits a channel, red lowest.
                List<ushort> colors = palette.Colors.Take(NsbtxPaletteColorCount(paletteIndex))
                    .Select(argb => (ushort)((((argb >> 16) & 0xFF) >> 3) | ((((argb >> 8) & 0xFF) >> 3) << 5) | (((argb & 0xFF) >> 3) << 10)))
                    .ToList();
                TexturePackService.SetPalette(currentProject, CurrentTexturePackKind, packId, palette.Name, colors);
                saved.Add(palette.Name);
            }

            nsbtxDirtyPalettes.Clear();
            RefreshNsbtxPaletteEditor();
            NsbtxStatusText.Text = $"Saved palette {string.Join(", ", saved)} to texture pack {packId:D4}.";
        }
        catch (Exception ex)
        {
            LogUiError("Palette could not be saved", ex);
            NsbtxStatusText.Text = $"Palette could not be saved: {ex.Message}";
        }
    }

    private void NsbtxRevertPalette_Click(object? sender, RoutedEventArgs e)
    {
        if (currentNsbtxPack is null || currentNsbtxPackPath is null) return;
        int textureIndex = nsbtxSelectedTexture;
        NsbtxTexturePack? reloaded = NsbtxTexturePack.Load(currentNsbtxPackPath);
        if (reloaded is null) return;
        TrimNsbtxPalettesToFile(reloaded);

        currentNsbtxPack = reloaded;
        nsbtxDirtyPalettes.Clear();
        RefreshNsbtxTextureGrid();
        SelectNsbtxTexture(textureIndex);
        NsbtxStatusText.Text = "Palette changes discarded.";
    }

    private void ChooseNsbtxPalette(int textureIndex, int paletteIndex)
    {
        if (currentNsbtxPack is null || textureIndex >= currentNsbtxPack.Textures.Count) return;
        nsbtxChosenPalettes[textureIndex] = paletteIndex;
        if (textureIndex < NsbtxTextureGrid.Children.Count)
        {
            NsbtxTextureGrid.Children[textureIndex] = CreateNsbtxTile(
                TryRenderNsbtx(textureIndex, paletteIndex), currentNsbtxPack.Textures[textureIndex].Name, textureIndex == nsbtxSelectedTexture, () => SelectNsbtxTexture(textureIndex));
        }

        RefreshNsbtxPalettePanel();
    }

    private void NsbtxScaleSlider_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (NsbtxScaleLabel is null || NsbtxTextureGrid is null) return;
        NsbtxScaleLabel.Text = $"x{e.NewValue:F0}";
        RefreshNsbtxTextureGrid();
        RefreshNsbtxPalettePanel();
    }

    private int FindMatchingNsbtxPalette(string textureName)
    {
        if (currentNsbtxPack is null)
        {
            return -1;
        }

        string name = textureName;
        while (name.Length > 0)
        {
            int paletteIndex = currentNsbtxPack.Palettes.FindIndex(palette =>
                string.Equals(palette.Name, name + "_pl", StringComparison.Ordinal) ||
                string.Equals(palette.Name, name, StringComparison.Ordinal));
            if (paletteIndex >= 0)
            {
                return paletteIndex;
            }

            name = name[..^1];
        }

        return currentNsbtxPack.Palettes.FindIndex(palette => palette.Name.StartsWith(textureName, StringComparison.Ordinal));
    }

    private string GetNsbtxTilesetDirectory()
    {
        if (currentProject is null)
        {
            return string.Empty;
        }

        return NsbtxMapTilesetRadioButton.IsChecked == true
            ? currentProject.MapTexturesDirectory
            : currentProject.BuildingTexturesDirectory;
    }

    private bool IsHeartGoldSoulSilver()
    {
        return currentProject is not null
            && currentProject.TryGetGameMetadata(out RomGameMetadata? metadata)
            && metadata is not null
            && metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;
    }

    private void ConfigureAreaDataEditor()
    {
        if (currentProject is null)
        {
            return;
        }

        loadingAreaData = true;
        try
        {
            bool hgss = IsHeartGoldSoulSilver();
            AreaDataDynamicTexturesLabel.IsVisible = hgss;
            AreaDataDynamicTexturesInput.IsVisible = hgss;
            AreaDataAreaTypePanel.IsVisible = hgss;

            AreaDataLightTypeInput.Items.Clear();
            foreach (string lightType in hgss
                ? ["Model's light", "Day/Night Light", "Unknown Light"]
                : new[] { "Day/Night Light", "Model's light", "Unknown Light" })
            {
                AreaDataLightTypeInput.Items.Add(lightType);
            }

            FillTexturePackSelector(AreaDataMapTilesetInput, currentProject.MapTexturesDirectory, "Map texture pack");
            FillTexturePackSelector(AreaDataBuildingTilesetInput, currentProject.BuildingTexturesDirectory, "Building texture pack");

            if (areaDataRecords.Count == 0) LoadAreaDataUsage();
            areaDataIds.Clear();
            AreaDataList.Items.Clear();
            foreach (int id in AreaDataService.GetIds(currentProject))
            {
                areaDataIds.Add(id);
                AreaDataList.Items.Add(DescribeAreaDataListEntry(id));
            }
        }
        finally
        {
            loadingAreaData = false;
        }

        if (AreaDataList.Items.Count > 0)
        {
            AreaDataList.SelectedIndex = 0;
        }
    }

    private async void AreaDataList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingAreaData || switchingDocument || currentProject is null || AreaDataList.SelectedIndex < 0 || AreaDataList.SelectedIndex >= areaDataIds.Count)
        {
            return;
        }

        int areaIndex = AreaDataList.SelectedIndex;
        int open = currentAreaData is null ? -1 : areaDataIds.IndexOf(currentAreaData.Id);
        if (!await ShouldLoadDocumentAsync(areaIndex == open, "Texture Editor", () => AreaDataList.SelectedIndex = open,
            change => change.Area == "Area data")) return;
        RunSwitchingDocument(() => AreaDataList.SelectedIndex = areaIndex);

        loadingAreaData = true;
        try
        {
            bool hgss = IsHeartGoldSoulSilver();
            currentAreaData = AreaDataService.Load(currentProject, areaDataIds[AreaDataList.SelectedIndex], hgss);
            AreaDataTitle.Text = $"Area Data {currentAreaData.Id:D3}";
            SelectTexturePack(AreaDataMapTilesetInput, currentAreaData.MapTileset, "Map texture pack");
            SelectTexturePack(AreaDataBuildingTilesetInput, currentAreaData.BuildingsTileset, "Building texture pack");
            AreaDataLightTypeInput.SelectedIndex = currentAreaData.LightType < AreaDataLightTypeInput.Items.Count
                ? currentAreaData.LightType
                : -1;
            AreaDataDynamicTexturesInput.Text = currentAreaData.DynamicTextureType.ToString();
            AreaDataIndoorRadioButton.IsChecked = currentAreaData.AreaType == AreaDataRecord.TypeIndoor;
            AreaDataOutdoorRadioButton.IsChecked = currentAreaData.AreaType != AreaDataRecord.TypeIndoor;
            ShowAreaDataUsage(currentAreaData.Id);
        }
        catch (Exception ex)
        {
            LogUiError("Area data could not be read", ex);
            NsbtxStatusText.Text = ex.Message;
        }
        finally
        {
            loadingAreaData = false;
        }

        _ = ShowAreaDataPreviewAsync();
    }

    /// <summary>Reads every area data file and which headers name each, for the usage lists and labels.</summary>
    private void LoadAreaDataUsage()
    {
        areaDataRecords.Clear();
        headersByAreaData.Clear();
        if (currentProject is null) return;

        bool hgss = IsHeartGoldSoulSilver();
        foreach (int id in AreaDataService.GetIds(currentProject))
        {
            try
            {
                areaDataRecords[id] = AreaDataService.Load(currentProject, id, hgss);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DSPRE] Area data {id} could not be read: {ex.Message}");
            }
        }

        if (headerStore is null) return;
        foreach (HeaderListEntry entry in allHeaders)
        {
            try
            {
                int areaDataId = headerStore.Load(entry.Id).AreaDataId;
                if (!headersByAreaData.TryGetValue(areaDataId, out List<HeaderListEntry>? users)) headersByAreaData[areaDataId] = users = [];
                users.Add(entry);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DSPRE] Header {entry.Id} could not be read for its area data: {ex.Message}");
            }
        }
    }

    private List<int> AreaDataUsingPack(int packId)
    {
        bool building = CurrentTexturePackKind == TexturePackKind.Building;
        return areaDataRecords.Values
            .Where(record => (building ? record.BuildingsTileset : record.MapTileset) == packId)
            .Select(record => record.Id)
            .OrderBy(id => id)
            .ToList();
    }

    private string DescribePackUsage(int packId)
    {
        List<int> users = AreaDataUsingPack(packId);
        if (users.Count == 0) return "No area data file uses this pack, so no location is drawn with it.";
        int headers = users.Sum(id => headersByAreaData.TryGetValue(id, out List<HeaderListEntry>? list) ? list.Count : 0);
        string files = string.Join(", ", users.Take(12).Select(id => id.ToString("D3"))) + (users.Count > 12 ? ", ..." : string.Empty);
        return $"Used by area data {files} - {headers} header(s) in all. Open one in the Area Data tab to see a map drawn with this pack.";
    }

    private string DescribeAreaDataListEntry(int id)
    {
        int headers = headersByAreaData.TryGetValue(id, out List<HeaderListEntry>? users) ? users.Count : 0;
        string usage = headers == 0 ? "unused" : headers == 1 ? "1 header" : $"{headers} headers";
        return areaDataRecords.TryGetValue(id, out AreaDataRecord? record)
            ? $"{id:D3} - map {record.MapTileset:D2}, buildings {record.BuildingsTileset:D2} ({usage})"
            : $"{id:D3} ({usage})";
    }

    private static void FillTexturePackSelector(ComboBox selector, string directory, string label)
    {
        selector.Items.Clear();
        int count = Directory.Exists(directory) ? Directory.GetFiles(directory).Length : 0;
        for (int i = 0; i < count; i++) selector.Items.Add($"{label} {i:D2}");
    }

    /// <summary>Selects a pack by number, adding an entry for one the project does not have rather than
    /// showing nothing - the area data can name a pack that is missing.</summary>
    private static void SelectTexturePack(ComboBox selector, int packId, string label)
    {
        while (selector.Items.Count > 0 && selector.Items[^1] is string last && last.EndsWith("(missing)", StringComparison.Ordinal))
        {
            selector.Items.RemoveAt(selector.Items.Count - 1);
        }

        if (packId >= selector.Items.Count) selector.Items.Add($"{label} {packId:D2} (missing)");
        selector.SelectedIndex = Math.Min(packId, selector.Items.Count - 1);
    }

    private static ushort SelectedPackId(ComboBox selector, ushort fallback)
    {
        if (selector.SelectedItem is not string label) return fallback;
        string number = label.Replace(" (missing)", string.Empty, StringComparison.Ordinal).Split(' ')[^1];
        return ushort.TryParse(number, out ushort id) ? id : fallback;
    }

    /// <summary>Lists the headers that name this area data, and collects their maps as preview choices.</summary>
    private void ShowAreaDataUsage(int areaDataId)
    {
        AreaDataHeadersList.Items.Clear();
        areaDataPreviewMapIds.Clear();
        AreaDataPreviewMapSelector.Items.Clear();
        List<HeaderListEntry> users = headersByAreaData.TryGetValue(areaDataId, out List<HeaderListEntry>? list) ? list : [];
        AreaDataUsageTitle.Text = users.Count switch
        {
            0 => "Used by: no header",
            1 => "Used by 1 header",
            _ => $"Used by {users.Count} headers"
        };
        foreach (HeaderListEntry header in users) AreaDataHeadersList.Items.Add(header.ToString());

        if (headerStore is null) return;
        EnsureMapEditorDataLoaded();
        // A header's maps are read from its matrix, so only the first few headers are searched: that is
        // plenty of examples and keeps switching between files quick.
        foreach (HeaderListEntry header in users.Take(8))
        {
            foreach (MapPlacement placement in GetHeaderMapPlacements(headerStore.Load(header.Id)))
            {
                if (areaDataPreviewMapIds.Contains(placement.MapId) || areaDataPreviewMapIds.Count >= 40) continue;
                areaDataPreviewMapIds.Add(placement.MapId);
                MapViewEntry? map = mapEntries.FirstOrDefault(entry => entry.Id == placement.MapId);
                AreaDataPreviewMapSelector.Items.Add($"Map {placement.MapId:D4}{(map is null ? string.Empty : $" - {map.Name}")} (header {header.Id:D3} {header.InternalName})");
            }
        }

        if (AreaDataPreviewMapSelector.Items.Count > 0) AreaDataPreviewMapSelector.SelectedIndex = 0;
    }

    private void AreaDataTileset_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingAreaData) return;
        _ = ShowAreaDataPreviewAsync();
    }

    private void AreaDataPreviewMapSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingAreaData) return;
        _ = ShowAreaDataPreviewAsync();
    }

    /// <summary>Draws the chosen example map with the packs currently picked (saved or not).</summary>
    private async Task ShowAreaDataPreviewAsync()
    {
        int request = ++areaDataPreviewRequest;
        if (currentAreaData is null) return;
        int index = AreaDataPreviewMapSelector.SelectedIndex;
        if (index < 0 || index >= areaDataPreviewMapIds.Count)
        {
            AreaDataPreviewViewer.IsVisible = false;
            AreaDataPreviewPlaceholder.Text = "No header uses this area data, so there is no map to show it on.";
            AreaDataPreviewPlaceholder.IsVisible = true;
            return;
        }

        int mapId = areaDataPreviewMapIds[index];
        int texturePack = SelectedPackId(AreaDataMapTilesetInput, currentAreaData.MapTileset);
        int buildingPack = SelectedPackId(AreaDataBuildingTilesetInput, currentAreaData.BuildingsTileset);
        AreaDataPreviewPlaceholder.Text = $"Drawing map {mapId:D4}...";
        AreaDataPreviewPlaceholder.IsVisible = true;
        string? scene = await EnsureMapPreviewSceneAsync(mapId, texturePack, buildingPack);
        if (request != areaDataPreviewRequest) return;
        if (scene is null)
        {
            AreaDataPreviewPlaceholder.Text = $"Map {mapId:D4} could not be converted.";
            return;
        }

        try
        {
            AreaDataPreviewViewer.IsVisible = true;
            ConfigureBuildingAnimations(AreaDataPreviewViewer, AreaDataFields(currentAreaData).AreaType == AreaDataRecord.TypeIndoor && IsHeartGoldSoulSilver());
            AreaDataPreviewViewer.LoadScene(scene);
            ApplyMapAnimation(AreaDataPreviewViewer, AreaDataFields(currentAreaData).DynamicTextureType);
            AreaDataPreviewViewer.ResetView();
            AreaDataPreviewPlaceholder.IsVisible = false;
            AreaDataPreviewStatus.Text = $"Map {mapId:D4} drawn with map texture pack {texturePack:D2} and building texture pack {buildingPack:D2}" +
                (texturePack != currentAreaData.MapTileset || buildingPack != currentAreaData.BuildingsTileset ? " - not saved yet." : ".");
        }
        catch (Exception ex)
        {
            LogUiError("Area data preview could not be drawn", ex);
            AreaDataPreviewPlaceholder.Text = ex.Message;
        }
    }

    /// <summary>The area data as the Texture Editor's fields would save it.</summary>
    private (ushort MapTileset, ushort BuildingsTileset, ushort DynamicTextureType, ushort LightType, byte AreaType) AreaDataFields(AreaDataRecord loaded) =>
        (SelectedPackId(AreaDataMapTilesetInput, loaded.MapTileset),
         SelectedPackId(AreaDataBuildingTilesetInput, loaded.BuildingsTileset),
         ParseUShort(AreaDataDynamicTexturesInput.Text, loaded.DynamicTextureType),
         AreaDataLightTypeInput.SelectedIndex >= 0 ? (ushort)AreaDataLightTypeInput.SelectedIndex : loaded.LightType,
         AreaDataIndoorRadioButton.IsChecked == true ? AreaDataRecord.TypeIndoor : AreaDataRecord.TypeOutdoor);

    private bool AreaDataUnsaved() =>
        currentAreaData is not null && !loadingAreaData &&
        AreaDataFields(currentAreaData) != (currentAreaData.MapTileset, currentAreaData.BuildingsTileset,
            currentAreaData.DynamicTextureType, currentAreaData.LightType, currentAreaData.AreaType);

    private void AreaDataSave_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentAreaData is null)
        {
            return;
        }

        (currentAreaData.MapTileset, currentAreaData.BuildingsTileset, currentAreaData.DynamicTextureType,
            currentAreaData.LightType, currentAreaData.AreaType) = AreaDataFields(currentAreaData);

        AreaDataService.Save(currentProject, currentAreaData, IsHeartGoldSoulSilver());
        // The Map Editor keeps the area data each map resolved to; keep its copies in step so it picks
        // up the new packs without reopening the project.
        foreach (AreaDataRecord record in mapAreaData.Values.Where(record => record.Id == currentAreaData.Id).Distinct())
        {
            record.MapTileset = currentAreaData.MapTileset;
            record.BuildingsTileset = currentAreaData.BuildingsTileset;
            record.LightType = currentAreaData.LightType;
            record.DynamicTextureType = currentAreaData.DynamicTextureType;
            record.AreaType = currentAreaData.AreaType;
        }

        areaDataRecords[currentAreaData.Id] = currentAreaData;
        int listIndex = areaDataIds.IndexOf(currentAreaData.Id);
        if (listIndex >= 0)
        {
            loadingAreaData = true;
            AreaDataList.Items[listIndex] = DescribeAreaDataListEntry(currentAreaData.Id);
            AreaDataList.SelectedIndex = listIndex;
            loadingAreaData = false;
        }

        NsbtxStatusText.Text = $"Saved area data {currentAreaData.Id:D3}.";
        _ = ShowAreaDataPreviewAsync();
        RedrawMapViewsUsingAreaData((ushort)currentAreaData.Id);
    }

    private void AreaDataAdd_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null)
        {
            return;
        }

        AreaDataService.Add(currentProject);
        ConfigureAreaDataEditor();
        AreaDataList.SelectedIndex = AreaDataList.Items.Count - 1;
        NsbtxStatusText.Text = "Added a new AreaData file.";
    }

    private void AreaDataRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null)
        {
            return;
        }

        if (!AreaDataService.RemoveLast(currentProject))
        {
            NsbtxStatusText.Text = "At least one AreaData file must be kept.";
            return;
        }

        ConfigureAreaDataEditor();
        NsbtxStatusText.Text = "Removed the final AreaData file.";
    }

    private async void AreaDataImport_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentAreaData is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("AreaData") { Patterns = ["*.bin", "*"] }]
        });
        if (files.Count != 1)
        {
            return;
        }

        AreaDataService.Import(currentProject, currentAreaData.Id, files[0].Path.LocalPath);
        int selectedIndex = AreaDataList.SelectedIndex;
        ConfigureAreaDataEditor();
        AreaDataList.SelectedIndex = selectedIndex;
        NsbtxStatusText.Text = "AreaData file imported successfully.";
    }

    private async void AreaDataExport_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentAreaData is null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"Area Data {currentAreaData.Id}.bin",
            FileTypeChoices = [new FilePickerFileType("AreaData") { Patterns = ["*.bin"] }]
        });
        if (file is null)
        {
            return;
        }

        File.Copy(AreaDataService.GetPath(currentProject, currentAreaData.Id), file.Path.LocalPath, true);
        NsbtxStatusText.Text = "AreaData file exported successfully.";
    }

    private static ushort ParseUShort(string? text, ushort fallback) =>
        ushort.TryParse(text, out ushort value) ? value : fallback;

    private async void NsbtxReplaceSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (currentNsbtxPackPath is null)
        {
            NsbtxStatusText.Text = "Select a texture pack before replacing it.";
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            SuggestedStartLocation = await ImportStartFolderAsync(),
            FileTypeFilter = [new FilePickerFileType("NSBTX") { Patterns = ["*.nsbtx", "*.btx", "*"] }]
        });
        if (files.Count != 1)
        {
            return;
        }

        TexturePackService.Import(currentProject!, CurrentTexturePackKind, int.Parse(Path.GetFileName(currentNsbtxPackPath)), files[0].Path.LocalPath);
        int selectedIndex = NsbtxTexturePacksList.SelectedIndex;
        ConfigureNsbtxEditor();
        NsbtxTexturePacksList.SelectedIndex = selectedIndex;
        NsbtxStatusText.Text = "NSBTX tileset imported successfully.";
    }

    private async void NsbtxExportSelected_Click(object? sender, RoutedEventArgs e)
    {
        if (currentNsbtxPackPath is null)
        {
            NsbtxStatusText.Text = "There is no selected NSBTX pack to export.";
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"Texture Pack {Path.GetFileName(currentNsbtxPackPath)}.nsbtx",
            FileTypeChoices = [new FilePickerFileType("NSBTX") { Patterns = ["*.nsbtx"] }]
        });
        if (file is null)
        {
            return;
        }

        File.Copy(currentNsbtxPackPath, file.Path.LocalPath, true);
        NsbtxStatusText.Text = "NSBTX tileset exported successfully.";
    }

    #region Building Model Editor

    private bool loadingBldModelEditor;

    /// <summary>The standalone Building Editor (legacy DS_Map/Editors/BuildingEditor.cs): browses the
    /// building model files themselves, as opposed to the map editors' Buildings panes, which place
    /// instances of them on a map.</summary>
    private void ConfigureBuildingModelEditor()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;

        // Same first-use extraction as ConfigureBuildingEditor, for projects opened before building
        // models were unpacked at project open.
        if (!Directory.Exists(currentProject.ExteriorBuildingModelsDirectory))
        {
            new RomExtractionService().PrepareTilesets(currentProject, metadata);
        }

        loadingBldModelEditor = true;
        try
        {
            bool hgss = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;
            BldModelInteriorCheckBox.IsVisible = hgss;
            if (!hgss) BldModelInteriorCheckBox.IsChecked = false;

            int previousTexture = BldModelTextureSelector.SelectedIndex;
            BldModelTextureSelector.Items.Clear();
            BldModelTextureSelector.Items.Add("Embedded textures");
            int packCount = TexturePackService.Count(currentProject, TexturePackKind.Building);
            for (int i = 0; i < packCount; i++) BldModelTextureSelector.Items.Add($"Building Texture Pack [{i:D2}]");
            BldModelTextureSelector.SelectedIndex = previousTexture >= 0 && previousTexture <= packCount ? previousTexture : 0;
        }
        finally
        {
            loadingBldModelEditor = false;
        }

        RefreshBldModelList(Math.Max(BldModelList.SelectedIndex, 0));
    }

    private void RefreshBldModelList(int selectIndex)
    {
        if (currentProject is null) return;
        loadingBldModelEditor = true;
        try
        {
            BldModelList.Items.Clear();
            foreach ((int id, string name) in MapBuildingService.GetBuildingModels(currentProject, BldModelInteriorCheckBox.IsChecked == true))
            {
                BldModelList.Items.Add($"[{id:D3}] {(string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name)}");
            }
        }
        finally
        {
            loadingBldModelEditor = false;
        }

        if (BldModelList.Items.Count == 0)
        {
            BldModelViewerPlaceholder.Text = "No building models were found in the unpacked project files.";
            BldModelViewerPlaceholder.IsVisible = true;
            BldModelStatus.Text = string.Empty;
            return;
        }

        int index = Math.Min(selectIndex, BldModelList.Items.Count - 1);
        if (BldModelList.SelectedIndex == index) _ = LoadBldModelPreviewAsync();
        else BldModelList.SelectedIndex = index;
    }

    private void BldModelInteriorCheckBox_Changed(object? sender, RoutedEventArgs e)
    {
        if (loadingBldModelEditor) return;
        RefreshBldModelList(0);
    }

    private void BldModelList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingBldModelEditor) return;
        _ = LoadBldModelPreviewAsync();
    }

    private void BldModelTextureSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingBldModelEditor) return;
        _ = LoadBldModelPreviewAsync();
    }

    private void BldModelResetView_Click(object? sender, RoutedEventArgs e) => BldModelViewer.ResetView();

    private async Task LoadBldModelPreviewAsync()
    {
        if (currentProject is null || BldModelList.SelectedIndex < 0) return;
        if (!currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;

        int modelId = BldModelList.SelectedIndex;
        bool interior = BldModelInteriorCheckBox.IsChecked == true;
        int texturePackId = BldModelTextureSelector.SelectedIndex - 1;
        string label = BldModelList.SelectedItem as string ?? modelId.ToString("D3");

        BeginBlockingLoad("Loading building model...", showImmediately: false);
        try
        {
            string previewDirectory = Path.Combine(
                BuildingModelService.GetPreviewDirectory(currentProject, interior, modelId),
                texturePackId < 0 ? "embedded" : $"tex{texturePackId:D2}");
            string scenePath = Path.Combine(previewDirectory, "scene.json");
            if (!File.Exists(scenePath))
            {
                BldModelStatus.Text = $"Converting {label}...";
                ToolResult result = await new RomExtractionService().ExportBuildingModelAsync(
                    currentProject, metadata, interior, modelId, previewDirectory,
                    Path.Combine(AppContext.BaseDirectory, "Tools"), texturePackId);
                if (!result.Succeeded)
                {
                    string details = string.Join("\n", new[] { result.StandardError, result.StandardOutput }.Where(text => !string.IsNullOrWhiteSpace(text)));
                    BldModelStatus.Text = string.IsNullOrWhiteSpace(details)
                        ? $"Model conversion failed with exit code {result.ExitCode}."
                        : $"Model conversion failed:\n{details}";
                    BldModelViewerPlaceholder.Text = "Building model could not be converted.";
                    BldModelViewerPlaceholder.IsVisible = true;
                    return;
                }
            }

            BldModelViewer.LoadScene(scenePath);
            BldModelViewer.ResetView();
            // The default distance frames the model edge to edge; step back so all of it is in view.
            BldModelViewer.Zoom(1.6f);
            BldModelViewerPlaceholder.IsVisible = false;
            string packLabel = texturePackId < 0 ? "embedded textures" : $"building texture pack {texturePackId:D2}";
            BldModelStatus.Text = texturePackId >= 0 && BldModelViewer.TextureCount == 0
                ? $"Loaded {label} ({BldModelViewer.TriangleCount:N0} triangles). Building texture pack {texturePackId:D2} has no textures matching this model's materials."
                : $"Loaded {label} ({BldModelViewer.TriangleCount:N0} triangles, {packLabel}).";
        }
        catch (Exception ex)
        {
            LogUiError("Building model could not be loaded", ex);
            BldModelStatus.Text = ex.Message;
            BldModelViewerPlaceholder.Text = "Building model could not be loaded.";
            BldModelViewerPlaceholder.IsVisible = true;
        }
        finally
        {
            EndBlockingLoad();
        }
    }

    private async void BldModelImport_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || BldModelList.SelectedIndex < 0)
        {
            BldModelStatus.Text = "Select a building model before replacing it.";
            return;
        }

        int modelId = BldModelList.SelectedIndex;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Import NSBMD building model",
            SuggestedStartLocation = await ImportStartFolderAsync(),
            FileTypeFilter = [new FilePickerFileType("NSBMD model") { Patterns = ["*.nsbmd", "*"] }]
        });
        if (files.Count != 1) return;

        try
        {
            BuildingModelService.Import(currentProject, BldModelInteriorCheckBox.IsChecked == true, modelId, files[0].Path.LocalPath);
        }
        catch (Exception ex)
        {
            LogUiError("Building model could not be imported", ex);
            BldModelStatus.Text = ex.Message;
            return;
        }

        RefreshBldModelList(modelId);
    }

    private async void BldModelExport_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || BldModelList.SelectedIndex < 0)
        {
            BldModelStatus.Text = "There is no selected building model to export.";
            return;
        }

        int modelId = BldModelList.SelectedIndex;
        bool interior = BldModelInteriorCheckBox.IsChecked == true;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"{BldModelList.SelectedItem}.nsbmd",
            FileTypeChoices = [new FilePickerFileType("NSBMD model") { Patterns = ["*.nsbmd"] }]
        });
        if (file is null) return;

        try
        {
            BuildingModelService.Export(currentProject, interior, modelId, file.Path.LocalPath);
            BldModelStatus.Text = "Building model exported successfully.";
        }
        catch (Exception ex)
        {
            LogUiError("Building model could not be exported", ex);
            BldModelStatus.Text = ex.Message;
        }
    }

    #endregion

    private TexturePackKind CurrentTexturePackKind =>
        NsbtxMapTilesetRadioButton.IsChecked == true ? TexturePackKind.Map : TexturePackKind.Building;

    private void NsbtxAdd_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null)
        {
            return;
        }

        try
        {
            TexturePackService.Add(currentProject, CurrentTexturePackKind);
        }
        catch (FileNotFoundException ex)
        {
            NsbtxStatusText.Text = ex.Message;
            return;
        }

        ConfigureNsbtxEditor();
        NsbtxTexturePacksList.SelectedIndex = nsbtxPackPaths.Count - 1;
        NsbtxStatusText.Text = "Added a new NSBTX slot.";
    }

    private void NsbtxRemove_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || !TexturePackService.RemoveLast(currentProject, CurrentTexturePackKind))
        {
            NsbtxStatusText.Text = "At least one tileset must be kept.";
            return;
        }

        ConfigureNsbtxEditor();
        NsbtxStatusText.Text = "Removed the final NSBTX slot.";
    }

    private void MapViewer_RenderError(string message)
    {
        MapViewerStatus.Text = message;
        MapViewerPlaceholder.Text = message;
        MapViewerPlaceholder.IsVisible = true;
    }

    private void MapRotateLeft_Click(object? sender, RoutedEventArgs e) => MapViewer.Rotate(-15f, 0f);
    private void MapRotateRight_Click(object? sender, RoutedEventArgs e) => MapViewer.Rotate(15f, 0f);
    private void MapTiltUp_Click(object? sender, RoutedEventArgs e) => MapViewer.Rotate(0f, -10f);
    private void MapTiltDown_Click(object? sender, RoutedEventArgs e) => MapViewer.Rotate(0f, 10f);
    private void MapPanLeft_Click(object? sender, RoutedEventArgs e) => MapViewer.Pan(-1f, 0f);
    private void MapPanRight_Click(object? sender, RoutedEventArgs e) => MapViewer.Pan(1f, 0f);
    private void MapPanUp_Click(object? sender, RoutedEventArgs e) => MapViewer.Pan(0f, 1f);
    private void MapPanDown_Click(object? sender, RoutedEventArgs e) => MapViewer.Pan(0f, -1f);
    private void MapZoomIn_Click(object? sender, RoutedEventArgs e) => MapViewer.Zoom(-1f);
    private void MapZoomOut_Click(object? sender, RoutedEventArgs e) => MapViewer.Zoom(1f);
    private void MapResetView_Click(object? sender, RoutedEventArgs e) => MapViewer.ResetView();

    private void OverworldMapRotateLeft_Click(object? sender, RoutedEventArgs e) => OverworldMapViewer.Rotate(-15f, 0f);
    private void OverworldMapRotateRight_Click(object? sender, RoutedEventArgs e) => OverworldMapViewer.Rotate(15f, 0f);
    private void OverworldMapTiltUp_Click(object? sender, RoutedEventArgs e) => OverworldMapViewer.Rotate(0f, -10f);
    private void OverworldMapTiltDown_Click(object? sender, RoutedEventArgs e) => OverworldMapViewer.Rotate(0f, 10f);
    private void OverworldMapPanLeft_Click(object? sender, RoutedEventArgs e) => OverworldMapViewer.Pan(-1f, 0f);
    private void OverworldMapPanRight_Click(object? sender, RoutedEventArgs e) => OverworldMapViewer.Pan(1f, 0f);
    private void OverworldMapPanUp_Click(object? sender, RoutedEventArgs e) => OverworldMapViewer.Pan(0f, 1f);
    private void OverworldMapPanDown_Click(object? sender, RoutedEventArgs e) => OverworldMapViewer.Pan(0f, -1f);
    private void OverworldMapZoomIn_Click(object? sender, RoutedEventArgs e) => OverworldMapViewer.Zoom(-1f);
    private void OverworldMapZoomOut_Click(object? sender, RoutedEventArgs e) => OverworldMapViewer.Zoom(1f);
    private void OverworldMapResetView_Click(object? sender, RoutedEventArgs e) => OverworldMapViewer.ResetView();

    private void IndoorMapRotateLeft_Click(object? sender, RoutedEventArgs e) => IndoorMapViewer.Rotate(-15f, 0f);
    private void IndoorMapRotateRight_Click(object? sender, RoutedEventArgs e) => IndoorMapViewer.Rotate(15f, 0f);
    private void IndoorMapTiltUp_Click(object? sender, RoutedEventArgs e) => IndoorMapViewer.Rotate(0f, -10f);
    private void IndoorMapTiltDown_Click(object? sender, RoutedEventArgs e) => IndoorMapViewer.Rotate(0f, 10f);
    private void IndoorMapPanLeft_Click(object? sender, RoutedEventArgs e) => IndoorMapViewer.Pan(-1f, 0f);
    private void IndoorMapPanRight_Click(object? sender, RoutedEventArgs e) => IndoorMapViewer.Pan(1f, 0f);
    private void IndoorMapPanUp_Click(object? sender, RoutedEventArgs e) => IndoorMapViewer.Pan(0f, 1f);
    private void IndoorMapPanDown_Click(object? sender, RoutedEventArgs e) => IndoorMapViewer.Pan(0f, -1f);
    private void IndoorMapZoomIn_Click(object? sender, RoutedEventArgs e) => IndoorMapViewer.Zoom(-1f);
    private void IndoorMapZoomOut_Click(object? sender, RoutedEventArgs e) => IndoorMapViewer.Zoom(1f);
    private void IndoorMapResetView_Click(object? sender, RoutedEventArgs e) => IndoorMapViewer.ResetView();

    private async void MapSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        BeginBlockingLoad("Loading map...", showImmediately: false);
        try
        {
            await SelectMapAsync();
        }
        finally
        {
            EndBlockingLoad();
        }
    }

    private async Task SelectMapAsync()
    {
        if (loadingMapEditor || currentProject is null || MapSelector.SelectedIndex < 0 || MapSelector.SelectedIndex >= mapEntries.Count) return;
        // The Buildings/Permissions sidebar starts collapsed for every newly-selected map (see
        // ToggleBuildingsPane_Click's comment) - without resetting it here, it would stay open (still
        // showing the previous map's buildings/permission grid momentarily) across a map switch.
        // Also resets the overhead-locked camera (see MapSidebarTabs_SelectionChanged) if the previous
        // map was left on the Permissions tab - otherwise the new map would silently load with the
        // camera still locked top-down and no visible sidebar explaining why.
        MapSidebarTabs.SelectedItem = BuildingsTabItem;
        // Buildings belong to the map, not to whichever texture pack ends up selected below, so this
        // must run unconditionally here rather than after the texture-preselect dance - that dance
        // short-circuits with an early return on a map's very first selection (its default packs never
        // match the combos' starting index), which was skipping this entirely and leaving buildings
        // "not loaded" until some later, unrelated selection change happened to land here instead.
        // The header decides which event file and script file the map's events use, so it has to
        // follow the map before the events and their script preview load.
        SyncHeaderToSingleMap();
        ReportLoadProgress("Reading buildings...", 0.05);
        await ConfigureBuildingEditor();
        ReportLoadProgress("Reading move permissions...", 0.15);
        await YieldToRenderAsync();
        ConfigurePermissionsEditor();
        ReportLoadProgress("Reading events...", 0.25);
        await YieldToRenderAsync();
        ConfigureEventsEditor();
        // The reset above left the sidebar on Buildings; put back whichever pane is actually open.
        ApplyMapPane();
        if (currentProject.TryGetGameMetadata(out RomGameMetadata? selectionMetadata) && selectionMetadata is not null)
        {
            // Selecting a map preselects its own tileset; the combo then drives loading.
            int defaultPack = GetDefaultTexturePack(mapEntries[MapSelector.SelectedIndex], selectionMetadata);
            int desiredIndex = defaultPack + 1 < MapTextureSelector.Items.Count ? defaultPack + 1 : 0;
            int defaultBuildingPack = GetDefaultBuildingTexturePack(mapEntries[MapSelector.SelectedIndex]);
            int desiredBuildingIndex = defaultBuildingPack + 1 < MapBuildingTextureSelector.Items.Count ? defaultBuildingPack + 1 : 0;
            // Both packs are set before anything loads: letting each combo's handler load the map
            // started one load with the new terrain pack but the previous map's building pack, whose
            // buildings then fell back to dmybox00 whenever that load happened to finish last.
            bool buildingPackChanged = MapBuildingTextureSelector.SelectedIndex != desiredBuildingIndex;
            settingMapTexturePacks = true;
            try
            {
                MapTextureSelector.SelectedIndex = desiredIndex;
                MapBuildingTextureSelector.SelectedIndex = desiredBuildingIndex;
            }
            finally
            {
                settingMapTexturePacks = false;
            }

            if (buildingPackChanged)
            {
                await RefreshBuildingModelList();
                SyncModelSelectorToSelectedBuilding();
            }
        }

        await LoadSelectedMapAsync();
    }

    private void LoadSelectedMap() => _ = LoadSelectedMapAsync();

    private async Task LoadSelectedMapAsync()
    {
        BeginBlockingLoad("Loading map...", showImmediately: false);
        try
        {
            await LoadSelectedMapAsyncCore();
        }
        finally
        {
            EndBlockingLoad();
        }
    }

    private async Task LoadSelectedMapAsyncCore()
    {
        if (currentProject is null || MapSelector.SelectedIndex < 0 || MapSelector.SelectedIndex >= mapEntries.Count) return;
        MapViewEntry map = mapEntries[MapSelector.SelectedIndex];
        int generation = ++mapLoadGeneration;
        if (!keepSceneForBuildingPreview) MapViewer.ClearScene(keepEventIcons: true);
        keepSceneForBuildingPreview = false;
        shownMapId = -1;
        try
        {
            _ = MapViewService.LoadBytes(map);
            if (currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata is not null)
            {
                int texturePackId = MapTextureSelector.SelectedIndex > 0 ? MapTextureSelector.SelectedIndex - 1 : -1;
                int buildingTexturePackId = MapBuildingTextureSelector.SelectedIndex > 0 ? MapBuildingTextureSelector.SelectedIndex - 1 : -1;
                // Each pack pair converts into its own folder: terrain.glb/building_*.glb reference their
                // textures by file name, so packs sharing a folder can resolve each other's images.
                bool interiorBuildings = UsesInteriorBuildings(map.Id, metadata);
                string modelDirectory = MapModelCache.GetModelDirectory(currentProject, map.Id, texturePackId, buildingTexturePackId, interiorBuildings);
                // Unsaved building edits are rendered from a copy of the map file written outside the
                // project, into their own folder so the saved map's cached model stays untouched.
                string? previewMapFile = null;
                string? reuseModelsFrom = null;
                if (BuildingsHaveUnsavedChanges())
                {
                    string previewRoot = Path.Combine(currentProject.RootPath, "expanded", "map_preview");
                    previewMapFile = Path.Combine(previewRoot, $"{map.Id:D4}.bin");
                    MapBuildingService.WritePreviewMapFile(currentProject, map.Id, currentBuildings, previewMapFile);
                    reuseModelsFrom = Path.Combine(modelDirectory);
                    modelDirectory = Path.Combine(previewRoot, "models", map.Id.ToString("D4"), $"tex{texturePackId:D2}{(interiorBuildings ? "_int" : "")}");
                    if (!File.Exists(Path.Combine(reuseModelsFrom, "scene.json"))) reuseModelsFrom = modelDirectory;
                }

                // Two loads of the same map would otherwise convert into the same folder at once, one
                // reading models the other is still rewriting.
                await MapModelExportGate.WaitAsync();
                try
                {
                    if (generation != mapLoadGeneration) return;
                    bool cached = previewMapFile is null &&
                        MapModelCache.IsFresh(currentProject, map.Id, texturePackId, buildingTexturePackId, Path.Combine(modelDirectory, "scene.json"));
                    if (!cached)
                    {
                        Directory.CreateDirectory(modelDirectory);
                        MapViewerStatus.Text = $"Converting {map.Id:D4} model...";
                        ReportLoadProgress($"Converting the model of map {map.Id:D4}...", 0.35);
                        ToolResult result = await new RomExtractionService().ExportMapModelAsync(
                            currentProject,
                            metadata,
                            map.Id,
                            modelDirectory,
                            Path.Combine(AppContext.BaseDirectory, "Tools"),
                            texturePackId,
                            buildingTexturePackId,
                            mapFilePath: previewMapFile,
                            interiorBuildings: interiorBuildings,
                            reuseModelsFrom: reuseModelsFrom);
                        if (!result.Succeeded)
                        {
                            string details = string.Join("\n", new[] { result.StandardError, result.StandardOutput }.Where(text => !string.IsNullOrWhiteSpace(text)));
                            MapViewerStatus.Text = string.IsNullOrWhiteSpace(details)
                                ? $"Model conversion failed with exit code {result.ExitCode}."
                                : $"Model conversion failed:\n{details}";
                            return;
                        }
                    }
                }
                finally
                {
                    MapModelExportGate.Release();
                }

                if (generation != mapLoadGeneration) return;
                ReportLoadProgress("Building the 3D scene...", 0.85);
                await YieldToRenderAsync();
                if (generation != mapLoadGeneration) return;
                ConfigureBuildingAnimations(MapViewer, InteriorBuildingsFor(map.Id));
                MapViewer.LoadScene(Path.Combine(modelDirectory, "scene.json"));
                shownMapId = map.Id;
                shownMapModelDirectories = reuseModelsFrom is null ? [modelDirectory] : [modelDirectory, reuseModelsFrom];
                ApplyMapAnimation(MapViewer, DynamicTypeOfMap(map.Id));
                if (focusBuildingAfterLoad is int focusIndex)
                {
                    focusBuildingAfterLoad = null;
                    MapViewer.FocusOnPart(focusIndex);
                }
                // LoadScene/LoadSceneCore always resets MapViewer's event markers to empty (it has no
                // way to know about them - events are runtime-only overlay, never baked into the
                // exported scene), so whatever ConfigureEventsEditor
                // populated earlier in this map switch needs to be re-applied now that the scene it was
                // drawn against actually exists; without this, events appeared briefly then vanished the
                // instant the model finished loading, on every map switch.
                RefreshEventMarkers();
                HighlightSelectedEvent();
                RefreshEventDirtyHighlight();
                MapViewerPlaceholder.IsVisible = false;
                string packLabel = texturePackId < 0 ? "untextured" : $"texture pack {texturePackId:D2}";
                string buildingLabel = MapViewer.PartCount == 1 ? "1 building" : $"{MapViewer.PartCount} buildings";
                MapViewerStatus.Text = texturePackId >= 0 && MapViewer.TextureCount == 0
                    ? $"Loaded {map.Id:D4} - {map.Name} ({MapViewer.TriangleCount:N0} triangles, {buildingLabel}). Texture pack {texturePackId:D2} has no textures matching this model's materials."
                    : $"Loaded {map.Id:D4} - {map.Name} ({MapViewer.TriangleCount:N0} triangles, {buildingLabel}, {packLabel})";
            }
        }
        catch (Exception ex)
        {
            LogUiError("Map could not be loaded", ex);
            MapViewerStatus.Text = ex.Message;
            MapViewerPlaceholder.Text = "Map could not be loaded.";
        }
    }

    #region Building Editor

    /// <summary>Loads the current map's placed Buildings and the model list for whichever
    /// interior/exterior radio is selected, then repopulates the ListBox/fields. Called whenever the
    /// selected map changes.</summary>
    private async Task ConfigureBuildingEditor()
    {
        if (currentProject is null || MapSelector.SelectedIndex < 0 || MapSelector.SelectedIndex >= mapEntries.Count) return;
        MapViewEntry map = mapEntries[MapSelector.SelectedIndex];

        // Projects opened before building-model editing was added won't have these directories on
        // disk yet (they're only populated by PrepareTilesets during project open); extract them here
        // on first use instead of requiring the user to re-open/re-extract the whole project.
        if (!Directory.Exists(currentProject.ExteriorBuildingModelsDirectory) &&
            currentProject.TryGetGameMetadata(out RomGameMetadata? extractMetadata) && extractMetadata is not null)
        {
            new RomExtractionService().PrepareTilesets(currentProject, extractMetadata);
        }

        loadingBuildingEditor = true;
        try
        {
            currentBuildings = GetSharedMapBuildings((ushort)map.Id);
            savedBuildings = sharedMapSavedBuildings[(ushort)map.Id];
            // Pick the building set the map itself uses; the user can still switch it.
            if (currentProject.TryGetGameMetadata(out RomGameMetadata? setMetadata) && setMetadata is not null && setMetadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver)
            {
                bool interior = UsesInteriorBuildings(map.Id, setMetadata);
                BuildingInteriorRadio.IsChecked = interior;
                BuildingExteriorRadio.IsChecked = !interior;
            }

            await RefreshBuildingModelList();
            // A newly-selected map always starts back at its first building, never carrying over
            // whichever index happened to be selected on the previous map.
            BuildingListBox.SelectedIndex = -1;
            RefreshBuildingListBoxItems();
            RefreshBuildingDirtyHighlight();
        }
        finally
        {
            loadingBuildingEditor = false;
        }

        // RefreshBuildingListBoxItems above selects a row (or leaves none selected, if the new map has
        // no buildings at all) while loadingBuildingEditor is still true, so BuildingListBox_
        // SelectionChanged's own guard skips populating the fields for it - without this explicit call,
        // switching to a map with different (or no) buildings left the previous map's building's
        // position/rotation/scale/model still showing, editable, and pointing at nothing real.
        SyncBuildingFieldsToSelectedBuilding();
    }

    /// <summary>Repopulates BuildingModelSelector. allBuildingModels always holds the full interior/
    /// exterior catalog (used to look up a building's model name even if that model is filtered out of
    /// the combo below); currentBuildingModels holds whatever subset is actually shown in the combo -
    /// every model when "show all" is checked, or only the ones ListValidBuildingModelsAsync confirms
    /// would render as themselves (not fall back to dmybox00) against the current building texture
    /// pack otherwise.</summary>
    private async Task RefreshBuildingModelList()
    {
        if (currentProject is null) return;
        bool interior = BuildingInteriorRadio.IsChecked == true;
        allBuildingModels = MapBuildingService.GetBuildingModels(currentProject, interior).ToList();
        currentBuildingModels = allBuildingModels;
        if (!currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;

        List<int> packIds = MapBuildingTextureSelector.SelectedIndex > 0 ? [MapBuildingTextureSelector.SelectedIndex - 1] : [];
        List<(int Id, string Name)> available = await GetAvailableBuildingModelsAsync(allBuildingModels, interior, packIds, metadata);
        if (BuildingShowAllModelsCheckBox.IsChecked != true)
        {
            currentBuildingModels = WithModelsInUse(allBuildingModels, available, currentBuildings);
        }

        BuildingModelSelector.Items.Clear();
        foreach ((int id, string name) in currentBuildingModels)
        {
            BuildingModelSelector.Items.Add($"[{id}] {(string.IsNullOrWhiteSpace(name) ? "(unnamed)" : name)}");
        }

        UpdateBuildingPalette(new BuildingPaletteContent(
            interior, packIds.Count > 0 ? packIds[0] : -1, available, DescribeBuildingPalette(packIds)));
    }

    #region Building model availability and palette

    private sealed record BuildingPaletteContent(
        bool Interior,
        int TexturePackId,
        IReadOnlyList<(int Id, string Name)> Models,
        string Description);

    private BuildingPaletteContent? buildingPaletteContent;
    private BuildingPaletteWindow? buildingPalette;

    /// <summary>The models that can actually appear in-game with the given building texture packs.
    /// The pack's own building config file is authoritative where it can be read (see
    /// BuildingConfigService); otherwise this falls back to the looser "has a texture the pack
    /// provides" test the bridge uses when deciding what renders as dmybox00. No pack at all (an
    /// untextured view) restricts nothing.</summary>
    private readonly Dictionary<(string Project, bool Interior, int PackId, DateTime PackWritten), IReadOnlyCollection<int>> validBuildingModelCache = [];

    private async Task<List<(int Id, string Name)>> GetAvailableBuildingModelsAsync(
        List<(int Id, string Name)> allModels, bool interior, IReadOnlyList<int> packIds, RomGameMetadata metadata)
    {
        if (currentProject is null || packIds.Count == 0) return allModels;

        var availableIds = new HashSet<int>();
        foreach (int packId in packIds)
        {
            if (!interior && BuildingConfigService.TryGetModelIds(currentProject, packId, out IReadOnlyList<int> configured))
            {
                availableIds.UnionWith(configured);
                continue;
            }

            try
            {
                // A bridge call per map switch otherwise; the answer only changes with the pack files.
                string packPath = Path.Combine(currentProject.BuildingTexturesDirectory, packId.ToString("D4"));
                var key = (currentProject.RootPath, interior, packId, File.Exists(packPath) ? File.GetLastWriteTimeUtc(packPath) : default);
                if (!validBuildingModelCache.TryGetValue(key, out IReadOnlyCollection<int>? valid))
                {
                    valid = (await new RomExtractionService().ListValidBuildingModelsAsync(
                        currentProject, metadata, interior, Path.Combine(AppContext.BaseDirectory, "Tools"), packId)).ToList();
                    validBuildingModelCache[key] = valid;
                }

                availableIds.UnionWith(valid);
            }
            catch (Exception ex)
            {
                LogUiError($"Could not determine which building models are valid for building texture pack {packId:D2}", ex);
                return allModels;
            }
        }

        return allModels.Where(model => availableIds.Contains(model.Id)).ToList();
    }

    /// <summary>The Model combo's contents: the available models plus any model a placed building
    /// already uses, marked, so switching building texture never blanks or silently rewrites a
    /// building that the new texture doesn't support.</summary>
    private static List<(int Id, string Name)> WithModelsInUse(
        List<(int Id, string Name)> allModels, List<(int Id, string Name)> available, IEnumerable<BuildingRecord> buildings)
    {
        var availableIds = available.Select(model => model.Id).ToHashSet();
        var usedIds = buildings.Select(building => (int)building.ModelId).ToHashSet();
        if (usedIds.IsSubsetOf(availableIds)) return available;

        return allModels
            .Where(model => availableIds.Contains(model.Id) || usedIds.Contains(model.Id))
            .Select(model => availableIds.Contains(model.Id)
                ? model
                : (model.Id, $"{(string.IsNullOrWhiteSpace(model.Name) ? "(unnamed)" : model.Name)} - not in this building texture"))
            .ToList();
    }

    private static string DescribeBuildingPalette(IReadOnlyList<int> packIds) => packIds.Count switch
    {
        0 => "No building texture selected - showing every building model.",
        1 => $"Building models available with building texture pack {packIds[0]:D2}.",
        _ => $"Building models available with building texture packs {string.Join(", ", packIds.Select(id => id.ToString("D2")))}."
    };

    private void UpdateBuildingPalette(BuildingPaletteContent content)
    {
        buildingPaletteContent = content;
        if (buildingPalette is not null) ShowBuildingPaletteContent(content);
    }

    private void OpenBuildingPalette()
    {
        if (buildingPaletteContent is not BuildingPaletteContent content) return;

        if (buildingPalette is null)
        {
            buildingPalette = new BuildingPaletteWindow();
            buildingPalette.Closed += (_, _) => buildingPalette = null;
            buildingPalette.Show(this);
        }
        else
        {
            buildingPalette.Activate();
        }

        ShowBuildingPaletteContent(content);
    }

    private void ShowBuildingPaletteContent(BuildingPaletteContent content)
    {
        if (buildingPalette is null || currentProject is null) return;
        if (!currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;

        buildingPalette.SetModels(
            currentProject, metadata, content.Interior, content.TexturePackId, content.Models, content.Description,
            modelId => AddBuildingFromPalette((uint)modelId));
    }

    private void AddBuildingFromPalette(uint modelId)
    {
        // The palette is its own window, so the loading overlay doesn't cover it.
        if (blockingLoadCount > 0) return;
        AddBuilding(modelId);
    }

    #endregion

    private string DescribeBuilding(int index, BuildingRecord building)
    {
        int modelIndex = allBuildingModels.FindIndex(model => model.Id == (int)building.ModelId);
        string? modelLabel = modelIndex >= 0 ? allBuildingModels[modelIndex].Name : null;
        string displayLabel = modelIndex < 0 ? "MISSING" : string.IsNullOrWhiteSpace(modelLabel) ? "(unnamed)" : modelLabel;
        return $"{index + 1:D2} - [{building.ModelId}] {displayLabel}";
    }

    /// <summary>Repopulates the ListBox from currentBuildings, then restores whichever index was
    /// selected before the refresh (or defaults to the first building if none was) - Items.Clear()
    /// drops the ListBox's selection entirely, and every caller of this method wants the same building
    /// still targeted afterward rather than landing on "nothing selected".</summary>
    private void RefreshBuildingListBoxItems()
    {
        int desiredIndex = BuildingListBox.SelectedIndex >= 0 ? BuildingListBox.SelectedIndex : 0;

        BuildingListBox.Items.Clear();
        for (int i = 0; i < currentBuildings.Count; i++)
        {
            BuildingListBox.Items.Add(DescribeBuilding(i, currentBuildings[i]));
        }

        BuildingListBox.SelectedIndex = currentBuildings.Count > 0 ? Math.Clamp(desiredIndex, 0, currentBuildings.Count - 1) : -1;
    }

    private BuildingRecord? SelectedBuilding =>
        BuildingListBox.SelectedIndex >= 0 && BuildingListBox.SelectedIndex < currentBuildings.Count
            ? currentBuildings[BuildingListBox.SelectedIndex]
            : null;

    private void BuildingListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Runs even while loadingBuildingEditor is set (e.g. from a programmatic Items.Clear()+
        // re-add+re-select inside RefreshBuildingListBoxItems) so the 3D highlight always tracks
        // whichever row ends up selected, including the cases below that skip the field-population
        // block entirely.
        HighlightSelectedBuilding();

        if (loadingBuildingEditor) return;
        SyncBuildingFieldsToSelectedBuilding();
    }

    /// <summary>Populates the Position/Rotation/Scale/Model fields from SelectedBuilding, and
    /// enables/disables them based on whether there's a building to edit at all - a map with no
    /// buildings (or none selected) leaves nothing for these fields to apply to, so they're disabled
    /// and cleared rather than left showing stale, non-functional values from whatever was selected
    /// before. Manages its own loadingBuildingEditor guard, so unlike the fields it sets, it's safe to
    /// call directly (not just from BuildingListBox's own SelectionChanged) - ConfigureBuildingEditor
    /// needs exactly that, since it selects a row while loadingBuildingEditor is already true for the
    /// whole map switch, which would otherwise make BuildingListBox_SelectionChanged skip this entirely.</summary>
    private void SyncBuildingFieldsToSelectedBuilding()
    {
        BuildingRecord? building = SelectedBuilding;
        bool hasBuilding = building is not null;

        BuildingModelSelector.IsEnabled = hasBuilding;
        BuildingPositionX.IsEnabled = hasBuilding;
        BuildingPositionY.IsEnabled = hasBuilding;
        BuildingPositionZ.IsEnabled = hasBuilding;
        BuildingRotationX.IsEnabled = hasBuilding;
        BuildingRotationY.IsEnabled = hasBuilding;
        BuildingRotationZ.IsEnabled = hasBuilding;
        BuildingScaleWidth.IsEnabled = hasBuilding;
        BuildingScaleHeight.IsEnabled = hasBuilding;
        BuildingScaleLength.IsEnabled = hasBuilding;

        bool wasLoading = loadingBuildingEditor;
        loadingBuildingEditor = true;
        try
        {
            if (building is null)
            {
                BuildingModelSelector.SelectedIndex = -1;
                BuildingPositionX.Value = null;
                BuildingPositionY.Value = null;
                BuildingPositionZ.Value = null;
                BuildingRotationX.Value = null;
                BuildingRotationY.Value = null;
                BuildingRotationZ.Value = null;
                BuildingScaleWidth.Value = null;
                BuildingScaleHeight.Value = null;
                BuildingScaleLength.Value = null;
                return;
            }

            // currentBuildingModels may be filtered (see RefreshBuildingModelList), so a building's
            // model ID no longer necessarily lines up with its position in the combo - look it up by
            // ID instead of assuming index == ID. -1 (nothing selected) if the model is filtered out
            // or otherwise missing, which is expected and not an error.
            BuildingModelSelector.SelectedIndex = currentBuildingModels.FindIndex(model => model.Id == (int)building.ModelId);

            BuildingPositionX.Value = (decimal)building.XPosition + (decimal)building.XFraction / 65535m;
            BuildingPositionY.Value = (decimal)building.YPosition + (decimal)building.YFraction / 65535m;
            BuildingPositionZ.Value = (decimal)building.ZPosition + (decimal)building.ZFraction / 65535m;

            BuildingRotationX.Value = (decimal)BuildingRecord.U16ToDegrees(building.XRotation);
            BuildingRotationY.Value = (decimal)BuildingRecord.U16ToDegrees(building.YRotation);
            BuildingRotationZ.Value = (decimal)BuildingRecord.U16ToDegrees(building.ZRotation);

            BuildingScaleWidth.Value = building.Width;
            BuildingScaleHeight.Value = building.Height;
            BuildingScaleLength.Value = building.Length;
        }
        finally
        {
            loadingBuildingEditor = wasLoading;
        }
    }

    private async void BuildingInteriorExterior_Checked(object? sender, RoutedEventArgs e)
    {
        if (loadingBuildingEditor) return;
        await RefreshBuildingModelList();
        RefreshBuildingListBoxItems();
        HighlightSelectedBuilding();
    }

    private async void BuildingShowAllModels_Changed(object? sender, RoutedEventArgs e)
    {
        if (loadingBuildingEditor) return;
        await RefreshBuildingModelList();
        SyncModelSelectorToSelectedBuilding();
    }

    private void AddBuilding_Click(object? sender, RoutedEventArgs e) => AddBuilding(0);

    private void OpenBuildingPalette_Click(object? sender, RoutedEventArgs e) => OpenBuildingPalette();

    private const short NewBuildingHeight = 5;
    private int? focusBuildingAfterLoad;

    private void AddBuilding(uint modelId)
    {
        if (MapSelector.SelectedIndex < 0) return;
        // Raised above the ground so a new building is not hidden inside the terrain until it is moved.
        currentBuildings.Add(new BuildingRecord { ModelId = modelId, YPosition = NewBuildingHeight });
        loadingBuildingEditor = true;
        try
        {
            RefreshBuildingListBoxItems();
        }
        finally
        {
            loadingBuildingEditor = false;
        }
        BuildingListBox.SelectedIndex = currentBuildings.Count - 1;
        // The list selected its first row while loading was set, so on a map with no buildings the selection never
        // changes again and the fields would stay disabled.
        SyncBuildingFieldsToSelectedBuilding();
        focusBuildingAfterLoad = currentBuildings.Count - 1;
        PreviewBuildingChanges();
    }

    private void DuplicateBuilding_Click(object? sender, RoutedEventArgs e)
    {
        BuildingRecord? building = SelectedBuilding;
        if (building is null) return;

        currentBuildings.Add(building.Clone());
        loadingBuildingEditor = true;
        try
        {
            RefreshBuildingListBoxItems();
        }
        finally
        {
            loadingBuildingEditor = false;
        }
        BuildingListBox.SelectedIndex = currentBuildings.Count - 1;
        PreviewBuildingChanges();
    }

    private void DeleteBuilding_Click(object? sender, RoutedEventArgs e)
    {
        int index = BuildingListBox.SelectedIndex;
        if (index < 0 || index >= currentBuildings.Count) return;

        currentBuildings.RemoveAt(index);
        loadingBuildingEditor = true;
        try
        {
            RefreshBuildingListBoxItems();
        }
        finally
        {
            loadingBuildingEditor = false;
        }
        BuildingListBox.SelectedIndex = currentBuildings.Count > 0 ? Math.Min(index, currentBuildings.Count - 1) : -1;
        PreviewBuildingChanges();
    }

    private void BuildingModelSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingBuildingEditor) return;
        BuildingRecord? building = SelectedBuilding;
        if (building is null || BuildingModelSelector.SelectedIndex < 0 || BuildingModelSelector.SelectedIndex >= currentBuildingModels.Count) return;

        // currentBuildingModels may be filtered (see RefreshBuildingModelList), so the combo's
        // position no longer necessarily matches the model's actual ID.
        building.ModelId = (uint)currentBuildingModels[BuildingModelSelector.SelectedIndex].Id;

        // Mutating BuildingListBox.Items[index] directly (the old approach) deselected the row: the
        // ListBox tracks its selection by item identity, and swapping in a new string instance at the
        // same index doesn't count as "the same item" to it. Going through the same refresh helper
        // every other mutation uses keeps the currently-selected building actually selected.
        loadingBuildingEditor = true;
        try
        {
            RefreshBuildingListBoxItems();
        }
        finally
        {
            loadingBuildingEditor = false;
        }

        // A different model needs its own NSBMD converted, which UpdatePartTransform's cached-scene
        // fast path cannot do, so the map is re-exported with the unsaved buildings.
        PreviewBuildingChanges();
    }

    private void BuildingPosition_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (loadingBuildingEditor) return;
        BuildingRecord? building = SelectedBuilding;
        if (building is null) return;

        SplitWholeAndFraction(BuildingPositionX.Value ?? 0, out short xWhole, out ushort xFraction);
        SplitWholeAndFraction(BuildingPositionY.Value ?? 0, out short yWhole, out ushort yFraction);
        SplitWholeAndFraction(BuildingPositionZ.Value ?? 0, out short zWhole, out ushort zFraction);
        (building.XPosition, building.XFraction) = (xWhole, xFraction);
        (building.YPosition, building.YFraction) = (yWhole, yFraction);
        (building.ZPosition, building.ZFraction) = (zWhole, zFraction);

        PreviewSelectedBuildingTransform();
    }

    /// <summary>Splits a decimal position into a signed whole part and a 0-65535 fraction, matching
    /// legacy's xBuildUpDown_ValueChanged (DS_Map/Editors/MapEditor.cs): a negative fractional part
    /// borrows one whole unit so the stored fraction always stays non-negative.</summary>
    private static void SplitWholeAndFraction(decimal value, out short whole, out ushort fraction)
    {
        decimal wholePart = Math.Truncate(value);
        decimal decPart = value - wholePart;
        if (decPart < 0)
        {
            decPart += 1;
            wholePart -= 1;
        }

        whole = (short)wholePart;
        fraction = (ushort)(decPart * 65535m);
    }

    private void BuildingRotation_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (loadingBuildingEditor) return;
        BuildingRecord? building = SelectedBuilding;
        if (building is null) return;

        building.XRotation = BuildingRecord.DegreesToU16((float)(BuildingRotationX.Value ?? 0));
        building.YRotation = BuildingRecord.DegreesToU16((float)(BuildingRotationY.Value ?? 0));
        building.ZRotation = BuildingRecord.DegreesToU16((float)(BuildingRotationZ.Value ?? 0));

        PreviewSelectedBuildingTransform();
    }

    private void BuildingScale_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (loadingBuildingEditor) return;
        BuildingRecord? building = SelectedBuilding;
        if (building is null) return;

        building.Width = (uint)Math.Max(1, BuildingScaleWidth.Value ?? 16);
        building.Height = (uint)Math.Max(1, BuildingScaleHeight.Value ?? 16);
        building.Length = (uint)Math.Max(1, BuildingScaleLength.Value ?? 16);

        PreviewSelectedBuildingTransform();
    }

    /// <summary>Re-places the selected building in the already-loaded 3D scene using MapGlView's
    /// cached decode (see MapGlView.UpdatePartTransform) - no disk IO or model re-conversion, so this
    /// is fast enough to call on every position/rotation/scale field change for a live preview. The
    /// part index matches the building's own index because DSPRE.ScriptTool's ExportMapScene builds
    /// the scene manifest by iterating the map's buildings in that same order.</summary>
    private void PreviewSelectedBuildingTransform()
    {
        int index = BuildingListBox.SelectedIndex;
        BuildingRecord? building = SelectedBuilding;
        if (building is null) return;

        float fullX = building.XPosition + building.XFraction / 65536f;
        float fullY = building.YPosition + building.YFraction / 65536f;
        float fullZ = building.ZPosition + building.ZFraction / 65536f;
        const float unitsPerGridCell = 16f;

        MapViewer.UpdatePartTransform(
            index,
            new OpenTK.Mathematics.Vector3(fullX * unitsPerGridCell, fullY * unitsPerGridCell, fullZ * unitsPerGridCell),
            new OpenTK.Mathematics.Vector3(
                BuildingRecord.U16ToDegrees(building.XRotation),
                BuildingRecord.U16ToDegrees(building.YRotation),
                BuildingRecord.U16ToDegrees(building.ZRotation)),
            new OpenTK.Mathematics.Vector3(building.Width / 16f, building.Height / 16f, building.Length / 16f));

        RefreshBuildingDirtyHighlight();
    }

    /// <summary>Selects the building at the given placed-part index (the same index used by
    /// MapGlView's SetSelectedPart/UpdatePartTransform) - the counterpart to clicking a row in
    /// BuildingListBox, but driven by clicking the building itself in the 3D view.</summary>
    private void SelectBuildingPart(int partIndex)
    {
        if (partIndex < 0 || partIndex >= currentBuildings.Count) return;
        BuildingListBox.SelectedIndex = partIndex;
    }

    /// <summary>Compares currentBuildings against the last-loaded-or-saved snapshot and tells
    /// MapViewer which placed parts differ, so they stay highlighted blue until saved - see
    /// MapGlView.SetDirtyParts.</summary>
    private void RefreshBuildingDirtyHighlight()
    {
        // Unsaved when nothing saved is equal to it, so a deleted building does not flag every one after it.
        var pool = new List<BuildingRecord>(savedBuildings);
        var matched = new bool[currentBuildings.Count];
        for (int i = 0; i < currentBuildings.Count; i++)
        {
            if (i < savedBuildings.Count && BuildingsEqual(currentBuildings[i], savedBuildings[i]) && pool.Remove(savedBuildings[i])) matched[i] = true;
        }

        var dirty = new List<int>();
        for (int i = 0; i < currentBuildings.Count; i++)
        {
            if (matched[i]) continue;
            int partner = pool.FindIndex(candidate => BuildingsEqual(currentBuildings[i], candidate));
            if (partner >= 0) pool.RemoveAt(partner);
            else dirty.Add(i);
        }

        MapViewer.SetDirtyParts(dirty);
    }

    private static bool BuildingsEqual(BuildingRecord a, BuildingRecord b) =>
        a.ModelId == b.ModelId &&
        a.XPosition == b.XPosition && a.XFraction == b.XFraction &&
        a.YPosition == b.YPosition && a.YFraction == b.YFraction &&
        a.ZPosition == b.ZPosition && a.ZFraction == b.ZFraction &&
        a.XRotation == b.XRotation && a.YRotation == b.YRotation && a.ZRotation == b.ZRotation &&
        a.Width == b.Width && a.Height == b.Height && a.Length == b.Length;

    /// <summary>Re-renders the map with the building list as edited, without saving it: adding,
    /// duplicating or deleting a building, or changing its model, changes which models must be
    /// converted, which only a fresh export can do (see LoadSelectedMapAsyncCore's preview file).</summary>
    private void PreviewBuildingChanges()
    {
        RefreshBuildingDirtyHighlight();
        MapViewerStatus.Text = "Showing unsaved building changes. Use Save Map to keep them.";
        if (TryReplaceBuildingsInScene()) return;
        keepSceneForBuildingPreview = true;
        LoadSelectedMap();
    }

    private bool keepSceneForBuildingPreview;
    private int shownMapId = -1;
    private string[] shownMapModelDirectories = [];

    /// <summary>Re-places the buildings in the loaded scene when every model they use was already converted
    /// for it, so adding, duplicating or deleting a building neither runs the converter nor re-reads the
    /// terrain. Models never converted for this map fall back to a full reload.</summary>
    private bool TryReplaceBuildingsInScene()
    {
        if (MapSelector.SelectedIndex < 0 || MapSelector.SelectedIndex >= mapEntries.Count ||
            mapEntries[MapSelector.SelectedIndex].Id != shownMapId) return false;

        var parts = new List<(string, OpenTK.Mathematics.Vector3, OpenTK.Mathematics.Vector3, OpenTK.Mathematics.Vector3)>();
        foreach (BuildingRecord building in currentBuildings)
        {
            string? model = shownMapModelDirectories
                .Select(directory => Path.Combine(directory, $"building_{building.ModelId:D4}"))
                .Where(Directory.Exists)
                .SelectMany(folder => Directory.GetFiles(folder, "*.glb"))
                .FirstOrDefault();
            if (model is null) return false;

            const float unitsPerGridCell = 16f;
            parts.Add((
                model,
                new OpenTK.Mathematics.Vector3(
                    (building.XPosition + building.XFraction / 65536f) * unitsPerGridCell,
                    (building.YPosition + building.YFraction / 65536f) * unitsPerGridCell,
                    (building.ZPosition + building.ZFraction / 65536f) * unitsPerGridCell),
                new OpenTK.Mathematics.Vector3(
                    BuildingRecord.U16ToDegrees(building.XRotation),
                    BuildingRecord.U16ToDegrees(building.YRotation),
                    BuildingRecord.U16ToDegrees(building.ZRotation)),
                new OpenTK.Mathematics.Vector3(building.Width / 16f, building.Height / 16f, building.Length / 16f)));
        }

        if (!MapViewer.ReplaceParts(parts)) return false;
        if (focusBuildingAfterLoad is int focusIndex)
        {
            focusBuildingAfterLoad = null;
            MapViewer.FocusOnPart(focusIndex);
        }

        return true;
    }

    private bool BuildingsHaveUnsavedChanges() =>
        currentBuildings.Count != savedBuildings.Count ||
        currentBuildings.Where((building, index) => !BuildingsEqual(building, savedBuildings[index])).Any();


    #endregion

    #region Events Editor

    // World-space conversion for event positions - matches the permission grid's own convention
    // (RebuildScene's grid-quad generation in MapGlView), not BuildingRecord's: an event's
    // MapPositionX/Y are unsigned 0-31 grid-cell indices (like a permission cell's col/row), not an
    // already-centered signed coordinate the way a building's XPosition/ZPosition are, so placing one
    // needs the same "-GridSize/2" centering step the permission grid quads use.
    private const int EventGridSize = 32;
    private const float EventUnitsPerGridCell = 16f;

    // User-adjustable via the Events pane's "Marker display" fields (EventMarkerSizeInput/
    // EventMarkerHeightOffsetInput) - not persisted, defaults matching the XAML Value= attributes.
    // HeightOffset exists because markers otherwise render flush with the ground at the event's own
    // (usually ~0) Z, where they get z-fought/buried by map/building geometry and are hard to make out
    // without zooming in - floating them above the map keeps them visible from a normal camera distance.
    private float eventMarkerSize = 18f;
    private float eventMarkerHeightOffset;
    private float eventMarkerOffsetX = 8f;
    private float eventMarkerOffsetZ = 4f;
    private float eventMarkerRotationDegrees = 75f;

    private static readonly ushort[] OverworldTypeValues = [OverworldEvent.TypeNormal, OverworldEvent.TypeTrainer, OverworldEvent.TypeItem];
    // Matches EventTypeToAddSelector's XAML item order (Overworld/Warp/Trigger/Spawnable), which is not
    // EventKind's own declaration order (Spawnable/Overworld/Warp/Trigger, kept that way to match
    // legacy's on-disk serialization order) - Overworld is first here since NPCs are the most commonly
    // added event, not for any format reason.
    private static readonly EventKind[] EventTypeToAddOrder = [EventKind.Overworld, EventKind.Warp, EventKind.Trigger, EventKind.Spawnable];

    private sealed record EventListEntry(EventKind Kind, int LocalIndex);

    // Events are shared state across editors for the same reason buildings are (see
    // sharedMapBuildings's comment) - keyed by event FILE id (a header's own EventFileId), not map id,
    // since that is what the ROM format actually indexes events by (see MapEventService.ResolveMapEventFileIds).
    private readonly Dictionary<int, MapEventFile> sharedMapEvents = [];
    private readonly Dictionary<int, MapEventFile> sharedMapSavedEvents = [];
    private MapEventFile? currentMapEventFile;
    private List<EventListEntry> eventFlatIndex = [];
    private bool loadingEventEditor;

    // Bundled 1:1 from DS_Map/Resources/Graphics/Event Icons (copied as loose Content next to the exe -
    // see DSPRE.Avalonia.csproj) - the same static icons legacy's own EventEditor.cs draws for
    // Warp/Trigger/Spawnable, plus every name RomInfo.ow3DSpriteDict/GetOverworldImage's own fallbacks
    // can resolve an Overworld event to (see ResolveOverworldIconKey/RomExtractionService.
    // ExportOverworldSpriteAsync). Registered under MapViewer's "static:{name}" icon-key namespace,
    // distinct from the per-sprite "ow:{entry}:{orientation}" keys real overworld sprites use.
    private static readonly string[] StaticEventIconNames =
        ["warp", "trigger", "sign", "overworld", "overworldUnreadable",
         "brown_sign", "red_sign", "gray_sign", "route_sign", "blue_sign", "dawn_platinum"];
    private bool staticEventIconsLoaded;

    // In-memory only (not persisted across sessions, beyond the on-disk PNG cache
    // RequestOverworldSprite itself writes) - which icon key a given overlayTableEntry+orientation
    // combination actually resolved to, once ExportOverworldSpriteAsync has answered for it. Every
    // Overworld marker falls back to the generic "static:overworld" bounding-box icon until its own
    // entry appears here.
    private readonly Dictionary<(int Entry, int Orientation), string> resolvedOverworldIconKeys = [];
    private readonly HashSet<(int Entry, int Orientation)> requestedOverworldSprites = [];
    private readonly List<(int Entry, int Orientation)> pendingOverworldSprites = [];
    private bool overworldSpriteWorkerRunning;

    private MapEventFile GetSharedMapEvents(int eventFileId)
    {
        if (!sharedMapEvents.TryGetValue(eventFileId, out MapEventFile? file))
        {
            file = currentProject is null ? new MapEventFile { Id = eventFileId } : MapEventService.Load(currentProject, eventFileId);
            sharedMapEvents[eventFileId] = file;
            sharedMapSavedEvents[eventFileId] = CloneEventFile(file);
        }

        return file;
    }

    private static MapEventFile CloneEventFile(MapEventFile file) => new()
    {
        Id = file.Id,
        Spawnables = file.Spawnables.Select(ev => ev.Clone()).ToList(),
        Overworlds = file.Overworlds.Select(ev => ev.Clone()).ToList(),
        Warps = file.Warps.Select(ev => ev.Clone()).ToList(),
        Triggers = file.Triggers.Select(ev => ev.Clone()).ToList()
    };

    private static MapEvent? TryResolveEvent(MapEventFile file, EventListEntry entry) => entry.Kind switch
    {
        EventKind.Overworld => entry.LocalIndex < file.Overworlds.Count ? file.Overworlds[entry.LocalIndex] : null,
        EventKind.Warp => entry.LocalIndex < file.Warps.Count ? file.Warps[entry.LocalIndex] : null,
        EventKind.Trigger => entry.LocalIndex < file.Triggers.Count ? file.Triggers[entry.LocalIndex] : null,
        EventKind.Spawnable => entry.LocalIndex < file.Spawnables.Count ? file.Spawnables[entry.LocalIndex] : null,
        _ => null
    };

    private static bool EventsEqual(MapEvent a, MapEvent b)
    {
        if (a.GetType() != b.GetType()) return false;
        if (a.MapPositionX != b.MapPositionX || a.MapPositionY != b.MapPositionY ||
            a.MatrixPositionX != b.MatrixPositionX || a.MatrixPositionY != b.MatrixPositionY)
        {
            return false;
        }

        return (a, b) switch
        {
            (SpawnableEvent x, SpawnableEvent y) => x.ScriptNumber == y.ScriptNumber && x.Type == y.Type && x.ZPosition == y.ZPosition &&
                x.Direction == y.Direction && x.Unknown2 == y.Unknown2 && x.Unknown4 == y.Unknown4 && x.Unknown5 == y.Unknown5,
            (OverworldEvent x, OverworldEvent y) => x.OverworldId == y.OverworldId && x.OverlayTableEntry == y.OverlayTableEntry &&
                x.Movement == y.Movement && x.Type == y.Type && x.Flag == y.Flag && x.ScriptNumber == y.ScriptNumber &&
                x.Orientation == y.Orientation && x.SightRange == y.SightRange && x.Unknown1 == y.Unknown1 && x.Unknown2 == y.Unknown2 &&
                x.XRange == y.XRange && x.YRange == y.YRange && x.ZPosition == y.ZPosition,
            (WarpEvent x, WarpEvent y) => x.DestinationHeaderId == y.DestinationHeaderId && x.DestinationWarpIndex == y.DestinationWarpIndex && x.Height == y.Height,
            (TriggerEvent x, TriggerEvent y) => x.ScriptNumber == y.ScriptNumber && x.WidthX == y.WidthX && x.HeightY == y.HeightY &&
                x.ZPosition == y.ZPosition && x.ExpectedVarValue == y.ExpectedVarValue && x.VariableWatched == y.VariableWatched,
            _ => false
        };
    }

    /// <summary>An event's height in grid-cell units (before the final *EventUnitsPerGridCell world-space
    /// step) - Overworld/Spawnable store this as 16.16 fixed point (matches legacy EventEditor.cs's own
    /// zPosition read/write shifts), Trigger stores it as already-plain grid-cell units, and Warp has no
    /// comparable field at all (its Height's real semantics are unconfirmed - treated as plain grid-cell
    /// units here too, the same as Trigger, for a reasonable marker position rather than none).</summary>
    private static float EventHeightGridUnits(MapEvent ev) => ev switch
    {
        OverworldEvent ow => ow.ZPosition / 65536f,
        SpawnableEvent sp => sp.ZPosition / 65536f,
        TriggerEvent tr => tr.ZPosition,
        WarpEvent wp => wp.Height,
        _ => 0f
    };

    private static OpenTK.Mathematics.Vector3 EventWorldPosition(MapEvent ev) => new(
        (ev.MapPositionX - EventGridSize / 2f) * EventUnitsPerGridCell,
        EventHeightGridUnits(ev) * EventUnitsPerGridCell,
        (ev.MapPositionY - EventGridSize / 2f) * EventUnitsPerGridCell);

    /// <summary>An event's marker. Overworlds stand on the terrain surface under their tile (the game
    /// places them on the ground itself; the stored height only picks the level where surfaces
    /// overlap), lifted so the sprite's lower edge rests on it, plus the user's extra height, and keep
    /// their in-game proportions - a 16px item ball is half the size of a 32px NPC. Warps, triggers and
    /// spawnables mark a tile rather than stand on it, so their icons lie flat on top of exactly that
    /// tile.</summary>
    private MapGlView.EventMarkerSpec EventMarker(MapGlView viewer, OpenTK.Mathematics.Vector3 tileCorner, string iconKey, OpenTK.Mathematics.Vector3 color, bool upright)
    {
        const float StandardSpriteWidth = 32f;
        float tileCenterX = tileCorner.X + EventUnitsPerGridCell / 2f;
        float tileCenterZ = tileCorner.Z + EventUnitsPerGridCell / 2f;
        float ground = viewer.TerrainHeightAt(tileCenterX, tileCenterZ, tileCorner.Y);
        int iconWidth = viewer.EventIconWidth(iconKey);
        if (!upright)
        {
            // Lifted off the surface so the icon neither z-fights with the ground nor sinks into a door
            // mat or similar building model lying on it.
            const float FlatLift = 3f;
            float tileHalf = EventUnitsPerGridCell / 2f;
            float height = iconWidth > 0 ? ground + FlatLift : ground + tileHalf;
            return new MapGlView.EventMarkerSpec(new OpenTK.Mathematics.Vector3(tileCenterX, height, tileCenterZ), color, tileHalf, iconKey, 0f);
        }

        float halfSize = iconKey.StartsWith("ow:", StringComparison.Ordinal) && iconWidth > 0
            ? eventMarkerSize * iconWidth / StandardSpriteWidth
            : eventMarkerSize;
        float lift = iconWidth > 0
            ? halfSize * MathF.Abs(MathF.Sin(eventMarkerRotationDegrees * MathF.PI / 180f))
            : halfSize;
        var position = new OpenTK.Mathematics.Vector3(tileCorner.X + eventMarkerOffsetX, ground + lift + eventMarkerHeightOffset, tileCorner.Z + eventMarkerOffsetZ);
        return new MapGlView.EventMarkerSpec(position, color, halfSize, iconKey, eventMarkerRotationDegrees);
    }

    private static OpenTK.Mathematics.Vector3 EventMarkerColor(EventKind kind) => kind switch
    {
        EventKind.Overworld => new OpenTK.Mathematics.Vector3(0.15f, 0.80f, 0.25f),
        EventKind.Warp => new OpenTK.Mathematics.Vector3(0.25f, 0.45f, 0.95f),
        EventKind.Trigger => new OpenTK.Mathematics.Vector3(0.95f, 0.60f, 0.10f),
        EventKind.Spawnable => new OpenTK.Mathematics.Vector3(0.70f, 0.30f, 0.90f),
        _ => new OpenTK.Mathematics.Vector3(0.8f, 0.8f, 0.8f)
    };

    /// <summary>Loads the current map's event file (see mapEventFileIds/GetSharedMapEvents) and
    /// repopulates the Events list/markers/fields - the events counterpart to ConfigureBuildingEditor.
    /// A map with no header referencing it (so no resolvable EventFileId) simply has no events to show,
    /// same as a map with no AreaData resolves to no default texture pack.</summary>
    /// <summary>Events come from the event file of the header the map view follows (see
    /// SyncHeaderToSingleMap), so the events, their scripts and the header pane always agree; a map no
    /// header claims falls back to the first header whose matrix places it.</summary>
    private int? ResolveMapEventFileId(MapViewEntry map)
    {
        EnsureLocationLookupsLoaded();
        if (headerStore is not null && mapHeaderIds.TryGetValue(map.Id, out ushort headerId))
        {
            return selectedHeader?.Id == headerId ? selectedHeader.EventFileId : headerStore.Load(headerId).EventFileId;
        }

        return mapEventFileIds.TryGetValue(map.Id, out int eventFileId) ? eventFileId : null;
    }

    /// <summary>One event file serves every map of its header (all of Jubilife City's squares, say);
    /// each event records which matrix square it stands on. Like legacy's isEventOnCurrentMatrix, only
    /// the events on the shown map's square are listed and drawn.</summary>
    private (int X, int Y)? currentEventMatrixCell;

    private (int X, int Y)? ResolveMapMatrixCell(MapViewEntry map)
    {
        if (currentProject is null || headerStore is null || !mapHeaderIds.TryGetValue(map.Id, out ushort headerId)) return null;
        try
        {
            MatrixDocument matrix = MatrixService.Load(currentProject, headerStore.Load(headerId).MatrixId);
            (int X, int Y)? anyHeader = null;
            for (int row = 0; row < matrix.Height; row++)
            {
                for (int column = 0; column < matrix.Width; column++)
                {
                    if (matrix.Maps[row, column] != map.Id) continue;
                    if (!matrix.HasHeadersSection || matrix.Headers[row, column] == headerId) return (column, row);
                    anyHeader ??= (column, row);
                }
            }

            return anyHeader;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] The matrix square of map {map.Id:D4} could not be found: {ex.Message}");
            return null;
        }
    }

    private bool IsOnCurrentEventCell(MapEvent ev) =>
        currentEventMatrixCell is not { } cell || (ev.MatrixPositionX == cell.X && ev.MatrixPositionY == cell.Y);

    private void PlaceOnCurrentEventCell(MapEvent ev)
    {
        if (currentEventMatrixCell is not { } cell) return;
        ev.MatrixPositionX = (ushort)cell.X;
        ev.MatrixPositionY = (ushort)cell.Y;
    }

    private void ConfigureEventsEditor()
    {
        if (currentProject is null || MapSelector.SelectedIndex < 0 || MapSelector.SelectedIndex >= mapEntries.Count)
        {
            currentMapEventFile = null;
            currentEventMatrixCell = null;
            RefreshEventListBoxItems();
            RefreshEventMarkers();
            return;
        }

        EnsureStaticEventIconsLoaded();
        MapViewEntry map = mapEntries[MapSelector.SelectedIndex];
        if (!Directory.Exists(currentProject.EventsDirectory) &&
            currentProject.TryGetGameMetadata(out RomGameMetadata? extractMetadata) && extractMetadata is not null)
        {
            new RomExtractionService().PrepareEvents(currentProject, extractMetadata);
        }

        loadingEventEditor = true;
        try
        {
            currentMapEventFile = ResolveMapEventFileId(map) is int eventFileId ? GetSharedMapEvents(eventFileId) : null;
            currentEventMatrixCell = ResolveMapMatrixCell(map);
            EventListBox.SelectedIndex = -1;
            RefreshEventListBoxItems();
            RefreshEventMarkers();
            RefreshEventDirtyHighlight();
        }
        finally
        {
            loadingEventEditor = false;
        }

        SyncEventFieldsToSelectedEvent();
        RefreshEventScriptPreview();
    }

    private static string DescribeEvent(EventKind kind, int localIndex, MapEvent ev)
    {
        string label = kind switch
        {
            EventKind.Overworld when ev is OverworldEvent ow => ow.IsAlias ? $"AliasOf {ow.OverworldId}" : $"ID {ow.OverworldId} (Entry {ow.OverlayTableEntry})",
            EventKind.Warp when ev is WarpEvent wp => $"To Header {wp.DestinationHeaderId:D3}, Hook {wp.DestinationWarpIndex:D2}",
            EventKind.Trigger when ev is TriggerEvent tr => tr.VariableWatched != 0
                ? $"Run script {tr.ScriptNumber} when Var {tr.VariableWatched} is {tr.ExpectedVarValue}"
                : $"Run script {tr.ScriptNumber}",
            EventKind.Spawnable when ev is SpawnableEvent sp => sp.Type switch
            {
                SpawnableEvent.TypeMisc => $"Misc [Scr {sp.ScriptNumber}]",
                SpawnableEvent.TypeBoard => $"Board [Scr {sp.ScriptNumber}]",
                SpawnableEvent.TypeHiddenItem => "Hidden Item",
                _ => $"[Scr {sp.ScriptNumber}]"
            },
            _ => "?"
        };
        return $"{kind} {localIndex + 1:D2} - {label}";
    }

    private void RefreshEventListBoxItems()
    {
        int desiredIndex = EventListBox.SelectedIndex >= 0 ? EventListBox.SelectedIndex : 0;
        eventFlatIndex = [];
        EventListBox.Items.Clear();

        if (currentMapEventFile is not null)
        {
            void AddGroup<T>(List<T> list, EventKind kind) where T : MapEvent
            {
                if (!EventKindShown(kind)) return;
                for (int i = 0; i < list.Count; i++)
                {
                    if (!IsOnCurrentEventCell(list[i])) continue;
                    eventFlatIndex.Add(new EventListEntry(kind, i));
                    EventListBox.Items.Add(DescribeEvent(kind, i, list[i]));
                }
            }

            AddGroup(currentMapEventFile.Overworlds, EventKind.Overworld);
            AddGroup(currentMapEventFile.Warps, EventKind.Warp);
            AddGroup(currentMapEventFile.Triggers, EventKind.Trigger);
            AddGroup(currentMapEventFile.Spawnables, EventKind.Spawnable);
        }

        EventListBox.SelectedIndex = eventFlatIndex.Count > 0 ? Math.Clamp(desiredIndex, 0, eventFlatIndex.Count - 1) : -1;
    }

    private MapEvent? SelectedMapEvent
    {
        get
        {
            int index = EventListBox.SelectedIndex;
            if (currentMapEventFile is null || index < 0 || index >= eventFlatIndex.Count) return null;
            return TryResolveEvent(currentMapEventFile, eventFlatIndex[index]);
        }
    }

    /// <summary>Rebuilds MapViewer's event markers from the current map's event file - called after
    /// every edit (position, per-type fields, add/duplicate/delete), since markers have no cheap
    /// per-instance transform update the way buildings' UpdatePartTransform does; event counts are
    /// small enough per map that a full rebuild each time is not worth the extra bookkeeping to avoid.</summary>
    private void RefreshEventMarkers()
    {
        if (currentMapEventFile is null)
        {
            MapViewer.SetEventMarkers([]);
            return;
        }

        var markers = new List<MapGlView.EventMarkerSpec>(eventFlatIndex.Count);
        foreach (EventListEntry entry in eventFlatIndex)
        {
            MapEvent? ev = TryResolveEvent(currentMapEventFile, entry);
            if (ev is null) continue;
            string iconKey = entry.Kind switch
            {
                EventKind.Warp => "static:warp",
                EventKind.Trigger => "static:trigger",
                EventKind.Spawnable => "static:sign",
                EventKind.Overworld when ev is OverworldEvent ow => ResolveOverworldIconKey(ow),
                _ => "static:overworld"
            };
            // A marker keeps its place in the list (indices are the events') but has no size until its sprite is there.
            MapGlView.EventMarkerSpec marker = EventMarker(MapViewer, EventWorldPosition(ev), iconKey == PendingIconKey ? "static:overworld" : iconKey, EventMarkerColor(entry.Kind), upright: entry.Kind == EventKind.Overworld);
            markers.Add(iconKey == PendingIconKey ? marker with { HalfSize = 0f } : marker);
        }

        MapViewer.SetEventMarkers(markers);
    }

    private bool syncingDisplayChecks;

    private void CompositeDisplay_Changed(object? sender, RoutedEventArgs e)
    {
        // The checkboxes fire while the window is still being built.
        if (OverworldMapViewer is null || IndoorMapViewer is null || OverworldShowBuildingsCheck is null || IndoorShowBuildingsCheck is null ||
            OverworldShowOtherEventsCheck is null || IndoorShowOtherEventsCheck is null) return;
        // The two scopes of the Matrix tab share one set of display options.
        if (!syncingDisplayChecks)
        {
            syncingDisplayChecks = true;
            try
            {
                foreach ((CheckBox a, CheckBox b) in new[] { (OverworldShowBuildingsCheck, IndoorShowBuildingsCheck), (OverworldShowOverworldsCheck, IndoorShowOverworldsCheck), (OverworldShowOtherEventsCheck, IndoorShowOtherEventsCheck) })
                {
                    if (ReferenceEquals(sender, a)) b.IsChecked = a.IsChecked;
                    else if (ReferenceEquals(sender, b)) a.IsChecked = b.IsChecked;
                }
            }
            finally
            {
                syncingDisplayChecks = false;
            }
        }

        OverworldMapViewer.SetPartsVisible(OverworldShowBuildingsCheck.IsChecked == true);
        IndoorMapViewer.SetPartsVisible(IndoorShowBuildingsCheck.IsChecked == true);
        RefreshCompositeEventMarkers();
    }

    private void RefreshCompositeEventMarkers()
    {
        RefreshCompositeEventMarkers(OverworldMapViewer, currentOverworldPlacements, headerView: true);
        RefreshCompositeEventMarkers(IndoorMapViewer, currentIndoorPlacements, headerView: false);
    }

    /// <summary>Draws the events of the maps in a multi-map view, each at its matrix square plus its
    /// tile. Header's Maps shows the selected header's events on its own squares; Whole Matrix shows,
    /// for every square, the events of that square's header. View only - events are edited in the
    /// Single Map view.</summary>
    private const int MaxDetailedMatrixSpan = 4;

    /// <summary>Placements spanning more than 4x4 cells (the overworld matrix) are drawn as bare terrain:
    /// buildings and events of hundreds of maps are unreadable and slow.</summary>
    private static bool IsLargePlacementSet(IReadOnlyList<MapPlacement> placements)
    {
        if (placements.Count == 0) return false;
        return placements.Max(p => p.Row) - placements.Min(p => p.Row) + 1 > MaxDetailedMatrixSpan ||
               placements.Max(p => p.Column) - placements.Min(p => p.Column) + 1 > MaxDetailedMatrixSpan;
    }

    private void RefreshCompositeEventMarkers(MapGlView viewer, IReadOnlyList<MapPlacement> placements, bool headerView)
    {
        if (currentProject is null || placements.Count == 0 || IsLargePlacementSet(placements))
        {
            viewer.SetEventMarkers([]);
            return;
        }

        bool showOverworlds = (headerView ? OverworldShowOverworldsCheck : IndoorShowOverworldsCheck).IsChecked == true;
        bool showOthers = (headerView ? OverworldShowOtherEventsCheck : IndoorShowOtherEventsCheck).IsChecked == true;
        var markers = new List<MapGlView.EventMarkerSpec>();
        if (showOverworlds || showOthers)
        {
            EnsureStaticEventIconsLoaded();
            foreach ((int eventFileId, HashSet<(int Column, int Row)> cells) in CompositeEventSources(placements, headerView))
            {
                MapEventFile events;
                try
                {
                    events = GetSharedMapEvents(eventFileId);
                }
                catch (Exception ex)
                {
                    Console.Error.WriteLine($"[DSPRE] Event file {eventFileId} could not be read for the map view: {ex.Message}");
                    continue;
                }

                IEnumerable<(EventKind Kind, MapEvent Event)> all =
                    (showOverworlds ? events.Overworlds.Select(ev => (EventKind.Overworld, (MapEvent)ev)) : [])
                    .Concat(showOthers ? events.Warps.Select(ev => (EventKind.Warp, (MapEvent)ev)) : [])
                    .Concat(showOthers ? events.Triggers.Select(ev => (EventKind.Trigger, (MapEvent)ev)) : [])
                    .Concat(showOthers ? events.Spawnables.Select(ev => (EventKind.Spawnable, (MapEvent)ev)) : []);
                foreach ((EventKind kind, MapEvent ev) in all)
                {
                    if (!cells.Contains((ev.MatrixPositionX, ev.MatrixPositionY))) continue;
                    string iconKey = kind switch
                    {
                        EventKind.Warp => "static:warp",
                        EventKind.Trigger => "static:trigger",
                        EventKind.Spawnable => "static:sign",
                        EventKind.Overworld when ev is OverworldEvent ow => ResolveOverworldIconKey(ow),
                        _ => "static:overworld"
                    };
                    if (iconKey == PendingIconKey) continue;
                    RegisterEventIconOn(viewer, iconKey);
                    OpenTK.Mathematics.Vector3 tileCorner = EventWorldPosition(ev) +
                        new OpenTK.Mathematics.Vector3(ev.MatrixPositionX * MatrixCellWorldSize, 0f, ev.MatrixPositionY * MatrixCellWorldSize);
                    markers.Add(EventMarker(viewer, tileCorner, iconKey, EventMarkerColor(kind), upright: kind == EventKind.Overworld));
                }
            }
        }

        viewer.SetEventMarkers(markers);
    }

    /// <summary>Which event files a multi-map view draws from, and on which of its squares.</summary>
    private List<(int EventFileId, HashSet<(int Column, int Row)> Cells)> CompositeEventSources(IReadOnlyList<MapPlacement> placements, bool headerView)
    {
        var sources = new List<(int, HashSet<(int, int)>)>();
        if (currentProject is null || headerStore is null || selectedHeader is null) return sources;
        if (headerView)
        {
            sources.Add((selectedHeader.EventFileId, placements.Select(placement => (placement.Column, placement.Row)).ToHashSet()));
            return sources;
        }

        if (shownWholeMatrixId is not int matrixId) return sources;
        MatrixDocument matrix;
        try
        {
            matrix = IsEditingMatrix && currentMatrix is not null && currentMatrix.Id == matrixId ? currentMatrix : MatrixService.Load(currentProject, matrixId);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Matrix {matrixId} could not be read for its events: {ex.Message}");
            return sources;
        }

        // A matrix with no header section belongs wholly to the header that names it.
        foreach (IGrouping<ushort, MapPlacement> group in placements.GroupBy(placement =>
                     matrix.HasHeadersSection && placement.Row < matrix.Height && placement.Column < matrix.Width ? matrix.Headers[placement.Row, placement.Column] : selectedHeader.Id))
        {
            int eventFileId;
            try
            {
                eventFileId = headerStore.Load(group.Key).EventFileId;
            }
            catch (Exception)
            {
                continue;
            }

            HashSet<(int, int)> cells = group.Select(placement => (placement.Column, placement.Row)).ToHashSet();
            int existing = sources.FindIndex(source => source.Item1 == eventFileId);
            if (existing >= 0) sources[existing].Item2.UnionWith(cells);
            else sources.Add((eventFileId, cells));
        }

        return sources;
    }

    /// <summary>Each 3D view keeps its own icon textures, so an icon the Single Map view already has is
    /// loaded again for another view from the same files: the bundled icons and the exported sprites.</summary>
    private void RegisterEventIconOn(MapGlView viewer, string iconKey)
    {
        if (viewer.HasEventIcon(iconKey)) return;
        string? path = null;
        if (iconKey.StartsWith("static:", StringComparison.Ordinal))
        {
            path = Path.Combine(AppContext.BaseDirectory, "Resources", "Event Icons", $"{iconKey["static:".Length..]}.png");
        }
        else if (iconKey.Split(':') is ["ow", string entry, string orientation] && OverworldSpriteDirectory is string directory &&
                 int.TryParse(entry, out int entryId) && int.TryParse(orientation, out int orientationId))
        {
            path = Path.Combine(directory, $"{entryId:D4}_{orientationId}.png");
        }

        try
        {
            if (path is not null && File.Exists(path)) viewer.RegisterEventIcon(iconKey, File.ReadAllBytes(path));
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Event icon {iconKey} could not be loaded: {ex.Message}");
        }
    }

    /// <summary>Loads DS_Map's own bundled Warp/Trigger/Spawnable icons plus every named fallback
    /// GetOverworldImage's port (RomExtractionService.ExportOverworldSpriteAsync) can resolve an
    /// Overworld event to, once, the first time the Events editor is used this session. Missing files
    /// (e.g. a build that hasn't picked up the csproj's Content include yet) are skipped silently -
    /// RebuildScene's own missing-icon fallback (a solid kind-colored cube) still applies for those.</summary>
    private void EnsureStaticEventIconsLoaded()
    {
        if (staticEventIconsLoaded) return;
        staticEventIconsLoaded = true;
        foreach (string name in StaticEventIconNames)
        {
            string path = Path.Combine(AppContext.BaseDirectory, "Resources", "Event Icons", $"{name}.png");
            if (File.Exists(path))
            {
                MapViewer.RegisterEventIcon($"static:{name}", File.ReadAllBytes(path));
            }
        }
    }

    /// <summary>Which icon key an Overworld event's marker should use right now - "static:overworld" (a
    /// generic bounding box, the same placeholder legacy shows for an unresolved entry) until
    /// RequestOverworldSprite's background export answers for this exact overlayTableEntry+orientation
    /// pair, then either "ow:{entry}:{orientation}" (a real in-game sprite) or "static:{name}" (one of
    /// GetOverworldImage's own named fallbacks - a 3D-model entry with no flat sprite, or an unreadable
    /// one) once resolvedOverworldIconKeys has an answer.</summary>
    private const string PendingIconKey = "";

    /// <summary>Exports the sprite of every overworld in every event file in one background run as soon as a
    /// project opens, so the markers find them on disk instead of waiting for a bridge run per map.</summary>
    private void PrefetchOverworldSprites(RomProject project)
    {
        if (!Directory.Exists(project.EventsDirectory)) return;
        string? cacheDirectory = OverworldSpriteDirectory;
        _ = Task.Run(() =>
        {
            var wanted = new HashSet<(int Entry, int Orientation)>();
            foreach (string path in Directory.EnumerateFiles(project.EventsDirectory))
            {
                if (!int.TryParse(Path.GetFileName(path), out int eventFileId)) continue;
                try
                {
                    foreach (OverworldEvent ow in MapEventService.Load(project, eventFileId).Overworlds) wanted.Add((ow.OverlayTableEntry, ow.Orientation));
                }
                catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentException)
                {
                    Console.Error.WriteLine($"[DSPRE] Event file {eventFileId} could not be read for sprite prefetch: {ex.Message}");
                }
            }

            Dispatcher.UIThread.Post(() =>
            {
                if (!ReferenceEquals(project, currentProject) || cacheDirectory is null) return;
                foreach ((int Entry, int Orientation) key in wanted)
                {
                    if (resolvedOverworldIconKeys.ContainsKey(key) || RomExtractionService.TryGetCachedOverworldSprite(cacheDirectory, key.Entry, key.Orientation) is not null) continue;
                    RequestOverworldSprite(key);
                }
            }, DispatcherPriority.Background);
        });
    }

    private string ResolveOverworldIconKey(OverworldEvent ow)
    {
        (int Entry, int Orientation) key = (ow.OverlayTableEntry, ow.Orientation);
        if (resolvedOverworldIconKeys.TryGetValue(key, out string? resolved)) return resolved;
        if (OverworldSpriteDirectory is string directory &&
            RomExtractionService.TryGetCachedOverworldSprite(directory, key.Entry, key.Orientation) is { } cached)
        {
            return resolvedOverworldIconKeys[key] = RegisterOverworldSprite(key, cached);
        }

        RequestOverworldSprite(key);
        return PendingIconKey;
    }

    /// <summary>Lists and caches the editors fill once and keep, keyed by map, matrix or event file
    /// number: opening another project must not leave the previous one's in place (its map list, its
    /// matrices, its buildings and events, which a save would otherwise write into this project).</summary>
    private void ResetProjectCaches()
    {
        loadingMapEditor = true;
        try
        {
            mapEntries.Clear();
            MapSelector.Items.Clear();
        }
        finally
        {
            loadingMapEditor = false;
        }

        allMapMatrices.Clear();
        sharedMapBuildings.Clear();
        sharedMapSavedBuildings.Clear();
        sharedMapEvents.Clear();
        sharedMapSavedEvents.Clear();
        currentMapEventFile = null;
        trainerNamesCache.Clear();
        nsbtxChosenPalettes.Clear();
        nsbtxPaletteSlots.Clear();
        nsbtxDirtyPalettes.Clear();
        nsbtxModelPalettes.Clear();
    }

    /// <summary>Overworld sprites are cached by overlay table entry, which means a different sprite in
    /// every game and hack, so nothing cached for the previous project may be reused.</summary>
    private void ResetOverworldSpriteCaches()
    {
        resolvedOverworldIconKeys.Clear();
        ClearScriptIconBitmaps();
        requestedOverworldSprites.Clear();
        pendingOverworldSprites.Clear();
        MapViewer.RemoveEventIcons("ow:");
        if (OverworldSpriteDirectory is string directory)
        {
            try
            {
                RomExtractionService.DiscardStaleOverworldSpriteCache(directory);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                StatusLog.Warning("Overworld sprites", $"The old sprite cache in {directory} could not be cleared: {ex.Message}");
            }
        }
    }

    private string? OverworldSpriteDirectory =>
        currentProject is null ? null : Path.Combine(currentProject.RootPath, "expanded", "overworld_sprites");

    /// <summary>Queues an Overworld sprite the disk cache doesn't have yet. Everything requested while
    /// one refresh builds its markers goes to the bridge in a single run and is shown together, since
    /// starting the bridge costs far more than exporting a sprite.</summary>
    private void RequestOverworldSprite((int Entry, int Orientation) key)
    {
        if (currentProject is null || !requestedOverworldSprites.Add(key)) return;
        pendingOverworldSprites.Add(key);
        if (!overworldSpriteWorkerRunning)
        {
            overworldSpriteWorkerRunning = true;
            Dispatcher.UIThread.Post(() => _ = ProcessOverworldSpriteRequestsAsync(), DispatcherPriority.Background);
        }
    }

    private async Task ProcessOverworldSpriteRequestsAsync()
    {
        try
        {
            while (pendingOverworldSprites.Count > 0 && currentProject is not null &&
                   currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata is not null &&
                   OverworldSpriteDirectory is string directory)
            {
                List<(int Entry, int Orientation)> batch = [.. pendingOverworldSprites];
                pendingOverworldSprites.Clear();
                RomProject project = currentProject;
                Dictionary<(int Entry, int Orientation), OverworldSpriteResult> results;
                try
                {
                    results = await new RomExtractionService().ExportOverworldSpritesAsync(
                        project, metadata, batch, directory, Path.Combine(AppContext.BaseDirectory, "Tools"));
                }
                catch (Exception ex)
                {
                    LogUiError("Overworld sprites could not be exported", ex);
                    results = [];
                }

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    // Another project was opened while the bridge ran: these sprites are not its.
                    if (!ReferenceEquals(project, currentProject)) return;
                    foreach ((int Entry, int Orientation) key in batch)
                    {
                        resolvedOverworldIconKeys[key] = results.TryGetValue(key, out OverworldSpriteResult? result)
                            ? RegisterOverworldSprite(key, result)
                            : "static:overworld";
                    }

                    RefreshEventMarkers();
                    RefreshCompositeEventMarkers();
                    QueueScriptIconRefresh();
                });
            }
        }
        finally
        {
            overworldSpriteWorkerRunning = false;
        }
    }

    /// <summary>Uploads an exported sprite to the map view and returns the icon key its markers use.</summary>
    private string RegisterOverworldSprite((int Entry, int Orientation) key, OverworldSpriteResult result)
    {
        if (result.PngPath is not null)
        {
            string iconKey = $"ow:{key.Entry}:{key.Orientation}";
            try
            {
                if (MapViewer.HasEventIcon(iconKey) || MapViewer.RegisterEventIcon(iconKey, File.ReadAllBytes(result.PngPath))) return iconKey;
            }
            catch (Exception ex)
            {
                LogUiError("Overworld sprite could not be read", ex);
            }
        }

        string fallbackKey = $"static:{result.FallbackIconName ?? "overworld"}";
        return MapViewer.HasEventIcon(fallbackKey) ? fallbackKey : "static:overworld";
    }

    private void RefreshEventDirtyHighlight()
    {
        if (currentMapEventFile is null)
        {
            MapViewer.SetDirtyEvents([]);
            return;
        }

        MapEventFile saved = sharedMapSavedEvents.TryGetValue(currentMapEventFile.Id, out MapEventFile? snapshot) ? snapshot : new MapEventFile();
        // An event is unsaved when nothing saved is equal to it. Comparing slot by slot instead would
        // flag every event after a deleted (or inserted) one, as they all moved up a place.
        var pool = new List<MapEvent>(saved.Overworlds.Cast<MapEvent>().Concat(saved.Warps).Concat(saved.Triggers).Concat(saved.Spawnables));
        var current = new MapEvent?[eventFlatIndex.Count];
        var matched = new bool[eventFlatIndex.Count];
        for (int i = 0; i < eventFlatIndex.Count; i++)
        {
            current[i] = TryResolveEvent(currentMapEventFile, eventFlatIndex[i]);
            // The same slot first, so an edited event is not paired with an identical neighbour.
            MapEvent? sameSlot = TryResolveEvent(saved, eventFlatIndex[i]);
            if (current[i] is { } now && sameSlot is not null && EventsEqual(now, sameSlot) && pool.Remove(sameSlot)) matched[i] = true;
        }

        var dirty = new List<int>();
        for (int i = 0; i < eventFlatIndex.Count; i++)
        {
            if (current[i] is not { } now || matched[i]) continue;
            int partner = pool.FindIndex(candidate => EventsEqual(now, candidate));
            if (partner >= 0) pool.RemoveAt(partner);
            else dirty.Add(i);
        }

        MapViewer.SetDirtyEvents(dirty);
    }

    private void EventListBox_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        HighlightSelectedEvent();
        if (loadingEventEditor) return;
        SyncEventFieldsToSelectedEvent();
        RefreshEventScriptPreview();
    }

    /// <summary>Selects the event at the given flat marker index (see MapGlView.EventMarkerClicked) -
    /// the counterpart to clicking a row in EventListBox, but driven by clicking the marker itself in
    /// the 3D view.</summary>
    private void SelectEventMarker(int flatIndex)
    {
        if (flatIndex < 0 || flatIndex >= eventFlatIndex.Count) return;
        EventListBox.SelectedIndex = flatIndex;
    }

    private void SyncEventFieldsToSelectedEvent()
    {
        MapEvent? ev = SelectedMapEvent;
        bool hasEvent = ev is not null;
        bool isWarp = ev is WarpEvent;

        EventPositionX.IsEnabled = hasEvent;
        EventPositionY.IsEnabled = hasEvent;
        EventPositionZ.IsEnabled = hasEvent && !isWarp;
        EventPositionZ.IsVisible = !isWarp;
        EventPositionZLabel.IsVisible = !isWarp;

        EventOverworldFields.IsVisible = ev is OverworldEvent;
        EventWarpFields.IsVisible = ev is WarpEvent;
        EventTriggerFields.IsVisible = ev is TriggerEvent;
        EventSpawnableFields.IsVisible = ev is SpawnableEvent;

        bool wasLoading = loadingEventEditor;
        loadingEventEditor = true;
        try
        {
            if (ev is null)
            {
                EventPositionX.Value = null;
                EventPositionY.Value = null;
                EventPositionZ.Value = null;
                return;
            }

            EventPositionX.Value = ev.MapPositionX;
            EventPositionY.Value = ev.MapPositionY;

            switch (ev)
            {
                case OverworldEvent ow:
                    EventPositionZ.Value = (decimal)(ow.ZPosition / 65536f);
                    EventOverworldOverlayTableEntry.Value = ow.OverlayTableEntry;
                    EventOverworldId.Value = ow.OverworldId;
                    EventOverworldMovement.Value = ow.Movement;
                    EventOverworldTypeSelector.SelectedIndex = Array.IndexOf(OverworldTypeValues, ow.Type);
                    EventOverworldFlag.Value = ow.Flag;
                    EventOverworldScriptNumber.Value = ow.ScriptNumber;
                    EventOverworldOrientation.Value = ow.Orientation;
                    EventOverworldSightRange.Value = ow.SightRange;
                    break;
                case WarpEvent wp:
                    EventWarpHeaderId.Value = wp.DestinationHeaderId;
                    EventWarpAnchor.Value = wp.DestinationWarpIndex;
                    EventWarpHeight.Value = wp.Height;
                    break;
                case TriggerEvent tr:
                    EventPositionZ.Value = tr.ZPosition;
                    EventTriggerScriptNumber.Value = tr.ScriptNumber;
                    EventTriggerWidth.Value = tr.WidthX;
                    EventTriggerHeight.Value = tr.HeightY;
                    EventTriggerVariableWatched.Value = tr.VariableWatched;
                    EventTriggerExpectedValue.Value = tr.ExpectedVarValue;
                    break;
                case SpawnableEvent sp:
                    EventPositionZ.Value = (decimal)(sp.ZPosition / 65536f);
                    EventSpawnableTypeSelector.SelectedIndex = sp.Type < 3 ? sp.Type : -1;
                    EventSpawnableScriptNumber.Value = sp.ScriptNumber;
                    EventSpawnableDirection.Value = sp.Direction;
                    break;
            }
        }
        finally
        {
            loadingEventEditor = wasLoading;
        }
    }

    private void EventMarkerDisplay_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        eventMarkerSize = (float)(EventMarkerSizeInput.Value ?? 18m);
        eventMarkerHeightOffset = (float)(EventMarkerHeightOffsetInput.Value ?? 0m);
        eventMarkerOffsetX = (float)(EventMarkerOffsetXInput.Value ?? 8m);
        eventMarkerOffsetZ = (float)(EventMarkerOffsetZInput.Value ?? 4m);
        eventMarkerRotationDegrees = (float)(EventMarkerRotationInput.Value ?? 75m);
        RefreshEventMarkers();
    }

    private void EventPosition_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (loadingEventEditor) return;
        MapEvent? ev = SelectedMapEvent;
        if (ev is null) return;

        ev.MapPositionX = (short)Math.Clamp(EventPositionX.Value ?? 0, 0, EventGridSize - 1);
        ev.MapPositionY = (short)Math.Clamp(EventPositionY.Value ?? 0, 0, EventGridSize - 1);
        float zGridUnits = (float)(EventPositionZ.Value ?? 0);
        switch (ev)
        {
            case OverworldEvent ow: ow.ZPosition = (int)(zGridUnits * 65536f); break;
            case SpawnableEvent sp: sp.ZPosition = (int)(zGridUnits * 65536f); break;
            case TriggerEvent tr: tr.ZPosition = (ushort)Math.Max(0, zGridUnits); break;
            case WarpEvent wp: wp.Height = (uint)Math.Max(0, zGridUnits); break;
        }

        RefreshEventMarkers();
        RefreshEventDirtyHighlight();
    }

    private void EventOverworldField_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (loadingEventEditor || SelectedMapEvent is not OverworldEvent ev) return;
        ev.OverlayTableEntry = (ushort)(EventOverworldOverlayTableEntry.Value ?? 0);
        ev.OverworldId = (ushort)(EventOverworldId.Value ?? 0);
        ev.Movement = (ushort)(EventOverworldMovement.Value ?? 0);
        ev.Flag = (ushort)(EventOverworldFlag.Value ?? 0);
        ev.ScriptNumber = (ushort)(EventOverworldScriptNumber.Value ?? 0);
        ev.Orientation = (ushort)(EventOverworldOrientation.Value ?? 0);
        ev.SightRange = (ushort)(EventOverworldSightRange.Value ?? 0);
        // OverlayTableEntry/Orientation are what ResolveOverworldIconKey keys the marker's sprite
        // on (see RefreshEventMarkers) - without this, the 3D marker kept showing whatever sprite it
        // resolved to when the event was first drawn, even after picking a different overlay entry.
        RefreshEventMarkers();
        RefreshEventDirtyHighlight();
        RefreshEventScriptPreview();
    }

    private void EventOverworldTypeSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingEventEditor || SelectedMapEvent is not OverworldEvent ev || EventOverworldTypeSelector.SelectedIndex < 0) return;
        ev.Type = OverworldTypeValues[EventOverworldTypeSelector.SelectedIndex];
        RefreshEventDirtyHighlight();
    }

    private void EventWarpField_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (loadingEventEditor || SelectedMapEvent is not WarpEvent ev) return;
        ev.DestinationHeaderId = (ushort)(EventWarpHeaderId.Value ?? 0);
        ev.DestinationWarpIndex = (ushort)(EventWarpAnchor.Value ?? 0);
        ev.Height = (uint)Math.Max(0, EventWarpHeight.Value ?? 0);
        RefreshEventDirtyHighlight();
    }

    private void EventTriggerField_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (loadingEventEditor || SelectedMapEvent is not TriggerEvent ev) return;
        ev.ScriptNumber = (ushort)(EventTriggerScriptNumber.Value ?? 0);
        ev.WidthX = (ushort)Math.Max(1, EventTriggerWidth.Value ?? 1);
        ev.HeightY = (ushort)Math.Max(1, EventTriggerHeight.Value ?? 1);
        ev.VariableWatched = (ushort)(EventTriggerVariableWatched.Value ?? 0);
        ev.ExpectedVarValue = (ushort)(EventTriggerExpectedValue.Value ?? 0);
        RefreshEventDirtyHighlight();
        RefreshEventScriptPreview();
    }

    private void EventSpawnableField_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (loadingEventEditor || SelectedMapEvent is not SpawnableEvent ev) return;
        ev.ScriptNumber = (ushort)(EventSpawnableScriptNumber.Value ?? 0);
        ev.Direction = (ushort)(EventSpawnableDirection.Value ?? 0);
        RefreshEventDirtyHighlight();
        RefreshEventScriptPreview();
    }

    private void EventSpawnableTypeSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingEventEditor || SelectedMapEvent is not SpawnableEvent ev || EventSpawnableTypeSelector.SelectedIndex < 0) return;
        ev.Type = (ushort)EventSpawnableTypeSelector.SelectedIndex;
        RefreshEventDirtyHighlight();
    }

    private void AddEvent_Click(object? sender, RoutedEventArgs e)
    {
        if (currentMapEventFile is null || EventTypeToAddSelector.SelectedIndex < 0) return;
        EventKind kind = EventTypeToAddOrder[EventTypeToAddSelector.SelectedIndex];
        ShowEventKind(kind);
        switch (kind)
        {
            case EventKind.Overworld: currentMapEventFile.Overworlds.Add(new OverworldEvent()); break;
            case EventKind.Warp: currentMapEventFile.Warps.Add(new WarpEvent()); break;
            case EventKind.Trigger: currentMapEventFile.Triggers.Add(new TriggerEvent()); break;
            case EventKind.Spawnable: currentMapEventFile.Spawnables.Add(new SpawnableEvent()); break;
        }

        MapEvent added = kind switch
        {
            EventKind.Overworld => currentMapEventFile.Overworlds[^1],
            EventKind.Warp => currentMapEventFile.Warps[^1],
            EventKind.Trigger => currentMapEventFile.Triggers[^1],
            _ => currentMapEventFile.Spawnables[^1]
        };
        PlaceOnCurrentEventCell(added);

        RefreshEventListBoxItems();
        EventListBox.SelectedIndex = eventFlatIndex.FindLastIndex(entry => entry.Kind == kind);
        RefreshEventMarkers();
        RefreshEventDirtyHighlight();
    }

    private void DuplicateEvent_Click(object? sender, RoutedEventArgs e)
    {
        if (currentMapEventFile is null) return;
        int index = EventListBox.SelectedIndex;
        if (index < 0 || index >= eventFlatIndex.Count) return;
        EventListEntry entry = eventFlatIndex[index];

        switch (entry.Kind)
        {
            case EventKind.Overworld: currentMapEventFile.Overworlds.Add(currentMapEventFile.Overworlds[entry.LocalIndex].Clone()); break;
            case EventKind.Warp: currentMapEventFile.Warps.Add(currentMapEventFile.Warps[entry.LocalIndex].Clone()); break;
            case EventKind.Trigger: currentMapEventFile.Triggers.Add(currentMapEventFile.Triggers[entry.LocalIndex].Clone()); break;
            case EventKind.Spawnable: currentMapEventFile.Spawnables.Add(currentMapEventFile.Spawnables[entry.LocalIndex].Clone()); break;
        }

        RefreshEventListBoxItems();
        EventListBox.SelectedIndex = eventFlatIndex.FindLastIndex(e2 => e2.Kind == entry.Kind);
        RefreshEventMarkers();
        RefreshEventDirtyHighlight();
    }

    private void DeleteEvent_Click(object? sender, RoutedEventArgs e)
    {
        if (currentMapEventFile is null) return;
        int index = EventListBox.SelectedIndex;
        if (index < 0 || index >= eventFlatIndex.Count) return;
        EventListEntry entry = eventFlatIndex[index];

        switch (entry.Kind)
        {
            case EventKind.Overworld: currentMapEventFile.Overworlds.RemoveAt(entry.LocalIndex); break;
            case EventKind.Warp: currentMapEventFile.Warps.RemoveAt(entry.LocalIndex); break;
            case EventKind.Trigger: currentMapEventFile.Triggers.RemoveAt(entry.LocalIndex); break;
            case EventKind.Spawnable: currentMapEventFile.Spawnables.RemoveAt(entry.LocalIndex); break;
        }

        RefreshEventListBoxItems();
        EventListBox.SelectedIndex = eventFlatIndex.Count > 0 ? Math.Min(index, eventFlatIndex.Count - 1) : -1;
        RefreshEventMarkers();
        RefreshEventDirtyHighlight();
    }

    /// <summary>Extracts one "Script N:" ... "End" block from a plaintext scripts-section, matching the
    /// exact label format DSPRE.ScriptTool's own plaintext export uses - null if that script number
    /// isn't present (a common/cross-file script reference is normal, not an error, same as legacy's own
    /// "likely using a common script" warning for this exact situation).</summary>
    private static string? ExtractScriptBlock(string scriptsSectionText, int scriptNumber)
    {
        string[] lines = scriptsSectionText.Replace("\r\n", "\n").Split('\n');
        string marker = $"Script {scriptNumber}:";
        int startLine = -1;
        for (int i = 0; i < lines.Length; i++)
        {
            if (lines[i].Trim() == marker)
            {
                startLine = i;
                break;
            }
        }

        if (startLine < 0) return null;

        var block = new List<string> { lines[startLine] };
        for (int i = startLine + 1; i < lines.Length; i++)
        {
            block.Add(lines[i]);
            if (lines[i].Trim() == "End") break;
        }

        return string.Join('\n', block);
    }

    /// <summary>An event's own script number, or null for events with no script of their own (Warps;
    /// an Overworld that's an alias of another one).</summary>
    private static ushort? SelectedEventScriptNumber(MapEvent? ev) => ev switch
    {
        OverworldEvent ow when !ow.IsAlias => ow.ScriptNumber,
        SpawnableEvent sp => sp.ScriptNumber,
        TriggerEvent tr => tr.ScriptNumber,
        _ => null
    };

    /// <summary>Refreshes the isolated "Linked Script" preview for whichever event is selected - resolved
    /// against the currently selected header's own ScriptFileId, matching exactly how legacy's Event
    /// Editor resolves an event's scriptNumber (EventEditor.cs: "new ScriptFile(header.scriptFileID)").</summary>
    private void RefreshEventScriptPreview()
    {
        MapEvent? ev = SelectedMapEvent;
        ushort? scriptNumber = SelectedEventScriptNumber(ev);
        string? scriptName = scriptNumber is ushort number && selectedHeader is not null ? SubscriptName(selectedHeader.ScriptFileId, number) : null;
        EventScriptExpander.Header = scriptName is null ? "Linked Script" : $"Linked Script - {scriptName}";
        string numberLabel = scriptName is null ? "Script number" : $"Script number - {scriptName}";
        foreach (TextBlock label in new[] { EventOverworldScriptLabel, EventTriggerScriptLabel, EventSpawnableScriptLabel })
        {
            label.Text = numberLabel;
            ToolTip.SetTip(label, numberLabel);
        }

        if (ev is null)
        {
            EventScriptPreview.Text = "";
            EventScriptStatus.Text = "";
            return;
        }

        if (scriptNumber is null)
        {
            EventScriptPreview.Text = "";
            EventScriptStatus.Text = ev is WarpEvent ? "Warps have no linked script." : "This overworld is an alias with no script of its own.";
            return;
        }

        if (currentProject is null || selectedHeader is null)
        {
            EventScriptPreview.Text = "";
            EventScriptStatus.Text = "Select a header above to resolve which script file this event's script number belongs to.";
            return;
        }

        string path = Path.Combine(currentProject.ExpandedScriptsDirectory, $"{selectedHeader.ScriptFileId:D4}.script");
        if (!File.Exists(path))
        {
            EventScriptPreview.Text = "";
            EventScriptStatus.Text = $"Script file {ScriptFileLabel(selectedHeader.ScriptFileId, "")} is not exported.";
            return;
        }

        (string scriptsSection, _, _) = SplitScriptSections(File.ReadAllText(path));
        string? block = ExtractScriptBlock(scriptsSection, scriptNumber.Value);
        if (block is null)
        {
            EventScriptPreview.Text = "";
            EventScriptStatus.Text = $"Script {scriptNumber} was not found in {ScriptFileLabel(selectedHeader.ScriptFileId, "")}.script - it likely uses a common script or one from another file.";
            return;
        }

        EventScriptPreview.Text = block;
        EventScriptStatus.Text = $"Script {scriptNumber}{(scriptName is null ? "" : $" ({scriptName})")} from {ScriptFileLabel(selectedHeader.ScriptFileId, "")}.script.";
    }

    private void OpenEventScriptEditor_Click(object? sender, RoutedEventArgs e)
    {
        if (SelectedEventScriptNumber(SelectedMapEvent) is null || selectedHeader is null) return;
        SelectEditor("Script Editor");
        SelectScriptFileId(selectedHeader.ScriptFileId);
    }

    #endregion

    #region Permissions Editor

    private List<byte> permissionValueOrder = [];

    private void ShowBuildingsCheckBox_Changed(object? sender, RoutedEventArgs e) => MapViewer.SetPartsVisible(ShowBuildingsCheckBox.IsChecked == true);

    private void ShowTileGridCheckBox_Changed(object? sender, RoutedEventArgs e) => MapViewer.SetTileGridVisible(ShowTileGridCheckBox.IsChecked == true);

    private void ShowEventsCheckBox_Changed(object? sender, RoutedEventArgs e) => MapViewer.SetEventsVisible(ShowEventsCheckBox.IsChecked == true);

    private CheckBox EventKindFilter(EventKind kind) => kind switch
    {
        EventKind.Overworld => ShowOverworldEventsCheckBox,
        EventKind.Warp => ShowWarpEventsCheckBox,
        EventKind.Trigger => ShowTriggerEventsCheckBox,
        _ => ShowSpawnableEventsCheckBox
    };

    private bool EventKindShown(EventKind kind) => EventKindFilter(kind).IsChecked != false;

    /// <summary>Un-hides a kind of event about to be added, so the new event can be seen and selected.</summary>
    private void ShowEventKind(EventKind kind)
    {
        if (EventKindShown(kind)) return;
        loadingEventEditor = true;
        try
        {
            EventKindFilter(kind).IsChecked = true;
        }
        finally
        {
            loadingEventEditor = false;
        }
    }

    private void EventKindFilter_Changed(object? sender, RoutedEventArgs e)
    {
        // The checkboxes fire while the window is still being built, before the list exists.
        if (EventListBox is null || loadingEventEditor) return;
        EventListEntry? selected = EventListBox.SelectedIndex >= 0 && EventListBox.SelectedIndex < eventFlatIndex.Count
            ? eventFlatIndex[EventListBox.SelectedIndex]
            : null;
        loadingEventEditor = true;
        try
        {
            RefreshEventListBoxItems();
            int reselected = selected is null ? -1 : eventFlatIndex.IndexOf(selected);
            if (reselected >= 0) EventListBox.SelectedIndex = reselected;
            RefreshEventMarkers();
            RefreshEventDirtyHighlight();
        }
        finally
        {
            loadingEventEditor = false;
        }

        SyncEventFieldsToSelectedEvent();
        RefreshEventScriptPreview();
        HighlightSelectedEvent();
    }

    private void MapSidebarTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // The TabControl selects its default item as part of its own XAML initialization, which fires
        // this handler before InitializeComponent has finished assigning every named field (including,
        // sometimes, MapSidebarTabs itself) - sender is the one reference guaranteed to be live then.
        if (sender is not TabControl tabControl || MapViewer is null) return;
        // Selection changes of the lists inside the tabs (picking a building, an event) bubble up to
        // here too; only a change of tab may lock or unlock the overhead camera.
        if (!ReferenceEquals(e.Source, tabControl)) return;
        bool permissionsActive = ReferenceEquals(tabControl.SelectedItem, PermissionsTabItem);
        MapViewer.SetOverheadLocked(permissionsActive);
        PermissionLabels.IsVisible = permissionsActive;
        if (permissionsActive)
        {
            RefreshPermissionGridColors();
        }
        else
        {
            MapViewer.SetPermissionGrid(null);
            PermissionLabels.SetValues(null);
        }
    }

    /// <summary>Loads the current map's 32x32 move-permission grid (types + collisions). Cheap (a
    /// small fixed-size section of the per-map file also read by MapBuildingService), so unlike
    /// buildings' model catalog this always runs eagerly rather than needing a "first use" extraction
    /// step.</summary>
    private void ConfigurePermissionsEditor()
    {
        if (currentProject is null || MapSelector.SelectedIndex < 0 || MapSelector.SelectedIndex >= mapEntries.Count) return;
        MapViewEntry map = mapEntries[MapSelector.SelectedIndex];

        loadingPermissionsEditor = true;
        try
        {
            KeepUnsavedPermissions();
            if (permissionsMapId != map.Id) ClearPermissionHistory();
            (currentPermissionTypes, currentPermissionCollisions) = pendingPermissions.Remove(map.Id, out var pending)
                ? pending
                : MapPermissionsService.Load(currentProject, map.Id);
            permissionsMapId = map.Id;
            RefreshPermissionValueSelector();
        }
        finally
        {
            loadingPermissionsEditor = false;
        }

        if (ReferenceEquals(MapSidebarTabs.SelectedItem, PermissionsTabItem))
        {
            RefreshPermissionGridColors();
        }
    }

    /// <summary>Repopulates PermissionValueSelector with the friendly names for whichever mode
    /// (Collision/Type) is active, in the same "[XX] Name" format legacy's collisionPainterComboBox/
    /// typePainterComboBox use.</summary>
    private void RefreshPermissionValueSelector()
    {
        IReadOnlyDictionary<byte, string> names = permissionsShowType ? MapPermissionPalette.TypeNames : MapPermissionPalette.CollisionNames;
        permissionValueOrder = names.Keys.OrderBy(key => key).ToList();
        loadingPermissionsEditor = true;
        try
        {
            PermissionValueSelector.Items.Clear();
            foreach (byte key in permissionValueOrder) PermissionValueSelector.Items.Add(names[key]);
        }
        finally
        {
            loadingPermissionsEditor = false;
        }

        SetPermissionPaintValue(permissionsPaintValue);
    }

    /// <summary>Sets the current paint value and syncs both the dropdown and hex box to match, without
    /// re-triggering each other's change handlers.</summary>
    private void SetPermissionPaintValue(byte value)
    {
        permissionsPaintValue = value;
        loadingPermissionsEditor = true;
        try
        {
            PermissionValueHexInput.Text = value.ToString("X2");
            PermissionValueSelector.SelectedIndex = permissionValueOrder.IndexOf(value);
        }
        finally
        {
            loadingPermissionsEditor = false;
        }
    }

    private void PermissionMode_Checked(object? sender, RoutedEventArgs e)
    {
        if (loadingPermissionsEditor) return;
        // Read which radio raised this specific event rather than inferring the mode from the other
        // radio's IsChecked - RadioButton mutual exclusion doesn't guarantee the outgoing radio's
        // IsChecked has already flipped to false by the time the incoming one's Checked handler runs,
        // so switching from Type back to Collision could still see PermissionTypeModeRadio.IsChecked
        // as true and never actually switch the grid back to collision data.
        permissionsShowType = ReferenceEquals(sender, PermissionTypeModeRadio);
        RefreshPermissionValueSelector();
        RefreshPermissionGridColors();
    }

    private void PermissionValueSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingPermissionsEditor) return;
        int index = PermissionValueSelector.SelectedIndex;
        if (index < 0 || index >= permissionValueOrder.Count) return;
        SetPermissionPaintValue(permissionValueOrder[index]);
    }

    private void PermissionValueHexInput_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (loadingPermissionsEditor) return;
        if (byte.TryParse(PermissionValueHexInput.Text, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out byte value))
        {
            SetPermissionPaintValue(value);
        }
    }

    private void PermissionTransparency_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e)
    {
        if (loadingPermissionsEditor) return;
        permissionsTransparency = (byte)Math.Clamp(PermissionTransparencySlider.Value, 0, 255);
        RefreshPermissionGridColors();
    }

    private void PermissionClear_Click(object? sender, RoutedEventArgs e)
    {
        byte[,] grid = permissionsShowType ? currentPermissionTypes : currentPermissionCollisions;
        BeginPermissionEdit();
        if (grid.Cast<byte>().Any(value => value != 0)) CommitPermissionEdit();
        for (int row = 0; row < MapPermissionsService.GridSize; row++)
        {
            for (int col = 0; col < MapPermissionsService.GridSize; col++)
            {
                grid[row, col] = 0;
            }
        }

        RefreshPermissionGridColors();
    }

    /// <summary>Rebuilds the 32x32 color array (from the in-memory grid, current mode, and
    /// transparency) and hands it to MapViewer, which renders it as real 3D geometry in the same
    /// coordinate space as the terrain - see MapGlView.SetPermissionGrid.</summary>
    private void RefreshPermissionGridColors()
    {
        var colors = new MapGlView.GridCellColor[MapPermissionsService.GridSize, MapPermissionsService.GridSize];
        for (int row = 0; row < MapPermissionsService.GridSize; row++)
        {
            for (int col = 0; col < MapPermissionsService.GridSize; col++)
            {
                byte value = permissionsShowType ? currentPermissionTypes[row, col] : currentPermissionCollisions[row, col];
                (byte a, byte r, byte g, byte b) = permissionsShowType
                    ? MapPermissionPalette.GetTypeColor(value, permissionsTransparency)
                    : MapPermissionPalette.GetCollisionColor(value, permissionsTransparency);
                colors[row, col] = new MapGlView.GridCellColor(a, r, g, b);
            }
        }

        MapViewer.SetPermissionGrid(colors);
        PermissionLabels.SetValues(permissionsShowType ? currentPermissionTypes : currentPermissionCollisions);
    }

    // Left click/drag - paints the cell under the pointer with the current paint value, matching
    // legacy's movPictureBox_Click/_MouseMove left-button behavior.
    private void PaintPermissionCell(int col, int row)
    {
        if ((permissionsShowType ? currentPermissionTypes[row, col] : currentPermissionCollisions[row, col]) != permissionsPaintValue) CommitPermissionEdit();
        if (permissionsShowType) currentPermissionTypes[row, col] = permissionsPaintValue;
        else currentPermissionCollisions[row, col] = permissionsPaintValue;
        RefreshPermissionGridColors();
    }

    // Middle click - flood-fills the clicked cell's contiguous region of matching value with the
    // current paint value, matching legacy's FloodFillUtil/FloodFillCell.
    private void FloodFillPermissionCell(int col, int row)
    {
        byte[,] grid = permissionsShowType ? currentPermissionTypes : currentPermissionCollisions;
        byte target = grid[row, col];
        if (target == permissionsPaintValue) return;
        CommitPermissionEdit();

        int size = MapPermissionsService.GridSize;
        var stack = new Stack<(int Col, int Row)>();
        stack.Push((col, row));
        while (stack.Count > 0)
        {
            (int c, int r) = stack.Pop();
            if (c < 0 || c >= size || r < 0 || r >= size || grid[r, c] != target) continue;
            grid[r, c] = permissionsPaintValue;
            stack.Push((c + 1, r));
            stack.Push((c - 1, r));
            stack.Push((c, r + 1));
            stack.Push((c, r - 1));
        }

        RefreshPermissionGridColors();
    }

    // Right click - eyedropper: reads the clicked cell's value back out as the new paint value,
    // matching legacy's right-click handling in movPictureBox_Click.
    private void PickPermissionCell(int col, int row)
    {
        byte value = permissionsShowType ? currentPermissionTypes[row, col] : currentPermissionCollisions[row, col];
        SetPermissionPaintValue(value);
    }

    #endregion

    private async void WildEncounterSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingWildEncounter || switchingDocument) return;
        await OpenWildEncounterAsync(WildEncounterSelector.SelectedIndex);
    }

    /// <summary>Opens the encounter file at <paramref name="index"/> of WildEncounterSelector, once the open
    /// one's unsaved changes are saved or discarded. False when the user chose to stay.</summary>
    private async Task<bool> OpenWildEncounterAsync(int index)
    {
        int open = currentWildEncounter is null ? -1 : wildEncounterIds.IndexOf(currentWildEncounter.Id);
        if (!await ShouldLoadDocumentAsync(index == open, "Wild Pokemon Encounter Editor", () => WildEncounterSelector.SelectedIndex = open)) return index == open;
        RunSwitchingDocument(() => WildEncounterSelector.SelectedIndex = index);
        LoadSelectedWildEncounter();
        return true;
    }

    /// <summary>Loads whichever encounter file WildEncounterSelector currently points at. Split out
    /// from WildEncounterSelector_SelectionChanged so GoToWildEncounter_Click can call it directly
    /// right after setting the selector, instead of relying on the SelectionChanged event to fire on
    /// its own timing - setting SelectedIndex right after a full Items rebuild (ConfigureWildEncounterEditor)
    /// was unreliable about actually raising the event, which was the "opens the editor but shows the
    /// wrong/default encounter file" bug.</summary>
    private void LoadSelectedWildEncounter()
    {
        if (currentProject is null || WildEncounterSelector.SelectedIndex < 0 ||
            WildEncounterSelector.SelectedIndex >= wildEncounterIds.Count)
        {
            return;
        }

        loadingWildEncounter = true;
        try
        {
            currentWildEncounter = WildEncounterService.Load(currentProject, wildEncounterIds[WildEncounterSelector.SelectedIndex]);
            wildEncounterEdits.Loaded(currentWildEncounter, $"Encounter file {currentWildEncounter.Id:D4}");
            GrassGroundEncounterRows.Clear();
            WaterEncounterRows.Clear();
            foreach (WildEncounterRow row in currentWildEncounter.Rows)
            {
                var view = new WildEncounterRowView(row);
                (WaterEncounterCategories.Contains(row.Category) ? WaterEncounterRows : GrassGroundEncounterRows).Add(view);
            }
            UpdateSpeciesIcons(GrassGroundEncounterRows);
            UpdateSpeciesIcons(WaterEncounterRows);

            WildWalkingRate.Text = currentWildEncounter.Rates.GetValueOrDefault("Walking", 0).ToString();
            WildSurfRate.Text = currentWildEncounter.Rates.GetValueOrDefault("Surf", 0).ToString();
            WildOldRodRate.Text = currentWildEncounter.Rates.GetValueOrDefault("Old Rod", 0).ToString();
            WildGoodRodRate.Text = currentWildEncounter.Rates.GetValueOrDefault("Good Rod", 0).ToString();
            WildSuperRodRate.Text = currentWildEncounter.Rates.GetValueOrDefault("Super Rod", 0).ToString();
            bool hasRockSmash = currentWildEncounter.Rates.ContainsKey("Rock Smash");
            WildRockSmashRateLabel.IsVisible = hasRockSmash;
            WildRockSmashRate.Text = currentWildEncounter.Rates.GetValueOrDefault("Rock Smash", 0).ToString();

            WildEncounterStatus.Text = $"Loaded {currentWildEncounter.Id:D4}: {currentWildEncounter.Rows.Count} slots. Edit species IDs and levels, then save." +
                (hasRockSmash ? " Grass levels are set on the Morning slots and apply to Day and Night too." : "");
        }
        catch (Exception ex)
        {
            LogUiError("Wild encounter file could not be loaded", ex);
            WildEncounterStatus.Text = ex.Message;
        }
        finally
        {
            loadingWildEncounter = false;
        }
    }

    private void WildEncounterField_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (loadingWildEncounter || sender is not TextBox textBox || textBox.DataContext is not WildEncounterRowView row)
        {
            return;
        }

        ReportEdit(wildEncounterEdits, () => currentWildEncounter, WildEncounterStatus);
    }

    private void WildPokemon_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingWildEncounter) return;

        ReportEdit(wildEncounterEdits, () => currentWildEncounter, WildEncounterStatus);
        if (sender is ComboBox comboBox && comboBox.DataContext is SpeciesIconRowBase row)
        {
            UpdateSpeciesIcon(row);
        }
    }

    private void SpeciesIconRow_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (sender is ComboBox comboBox && comboBox.DataContext is SpeciesIconRowBase row)
        {
            UpdateSpeciesIcon(row);
        }
    }

    private void UpdateSpeciesIcon(SpeciesIconRowBase row)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            return;
        }

        byte[]? rgba = SpriteService.GetSpeciesIconRgba(currentProject, metadata, row.Species, 32, 24);
        row.Icon = CreateBitmapFromRgba(rgba, 32, 24);
    }

    private void UpdateSpeciesIcons(IEnumerable<SpeciesIconRowBase> rows)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            return;
        }

        foreach (SpeciesIconRowBase row in rows)
        {
            row.Icon = CreateBitmapFromRgba(SpriteService.GetSpeciesIconRgba(currentProject, metadata, row.Species, 32, 24), 32, 24);
        }
    }

    private void WildRate_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (loadingWildEncounter || currentWildEncounter is null || sender is not TextBox textBox || !int.TryParse(textBox.Text, out int rate))
        {
            return;
        }

        string category = textBox.Name switch
        {
            "WildWalkingRate" => "Walking",
            "WildSurfRate" => "Surf",
            "WildOldRodRate" => "Old Rod",
            "WildGoodRodRate" => "Good Rod",
            "WildSuperRodRate" => "Super Rod",
            "WildRockSmashRate" => "Rock Smash",
            _ => string.Empty
        };
        if (category.Length > 0 && currentWildEncounter.Rates.ContainsKey(category)) currentWildEncounter.Rates[category] = Math.Clamp(rate, 0, 255);
        ReportEdit(wildEncounterEdits, () => currentWildEncounter, WildEncounterStatus);
    }

    private void SaveWildEncounter_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentWildEncounter is null)
        {
            WildEncounterStatus.Text = "No wild encounter file is selected.";
            return;
        }

        try
        {
            WildEncounterService.Save(currentProject, currentWildEncounter);
            wildEncounterEdits.Saved(currentWildEncounter);
            WildEncounterStatus.Text = $"Saved encounter file {currentWildEncounter.Id:D4}. It will be packed when Save ROM is used.";
        }
        catch (Exception ex)
        {
            LogUiError("Wild encounter file could not be saved", ex);
            WildEncounterStatus.Text = ex.Message;
        }
    }

    private async void TextArchiveSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingText || switchingDocument || currentProject is null || TextArchiveSelector.SelectedIndex < 0 ||
            TextArchiveSelector.SelectedIndex >= textArchiveIds.Count)
        {
            return;
        }

        await OpenTextArchiveAsync(TextArchiveSelector.SelectedIndex);
    }

    /// <summary>Opens the archive at <paramref name="index"/> of TextArchiveSelector, once the open one's
    /// unsaved changes are saved or discarded. False when the user chose to stay.</summary>
    private async Task<bool> OpenTextArchiveAsync(int index)
    {
        int open = currentTextArchive is null ? -1 : textArchiveIds.IndexOf(currentTextArchive.Id);
        if (!await ShouldLoadDocumentAsync(index == open, "Text Editor", () => TextArchiveSelector.SelectedIndex = open, discard: DiscardTextArchiveEdits)) return index == open;
        RunSwitchingDocument(() => TextArchiveSelector.SelectedIndex = index);
        LoadSelectedTextArchive();
        return true;
    }

    /// <summary>Puts the open archive's messages back as saved. The archive object is shared with the
    /// Script Editor (see openTextArchives), so it is reverted in place rather than replaced.</summary>
    private void DiscardTextArchiveEdits()
    {
        if (currentProject is null || currentTextArchive is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        List<string> saved = TextArchiveService.Load(currentProject, currentTextArchive.Id, metadata).Messages.ToList();
        currentTextArchive.Messages.Clear();
        currentTextArchive.Messages.AddRange(saved);
        textDirty = false;
    }

    private void LoadSelectedTextArchive()
    {
        if (currentProject is null || TextArchiveSelector.SelectedIndex < 0 ||
            TextArchiveSelector.SelectedIndex >= textArchiveIds.Count ||
            !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            return;
        }

        loadingText = true;
        try
        {
            currentTextArchive = GetOpenTextArchive(textArchiveIds[TextArchiveSelector.SelectedIndex], metadata);
            // The open copy is shared (see openTextArchives) and may already hold edits made elsewhere, so
            // what counts as saved is what is on disk.
            textArchiveEdits.Loaded(TextArchiveService.Load(currentProject, currentTextArchive.Id, metadata), $"Text archive {currentTextArchive.Id:D4}");
            TextMessages.Clear();
            for (int i = 0; i < currentTextArchive.Messages.Count; i++)
            {
                TextMessages.Add(new TextMessageRow(i, currentTextArchive.Messages[i]));
            }

            ShowTextArchiveName(currentTextArchive.Id);
            textDirty = false;
            TextEditorStatus.Text = $"Loaded {currentTextArchive.Id:D4}.json ({TextMessages.Count} messages).";
        }
        catch (Exception ex)
        {
            LogUiError("Text archive could not be loaded", ex);
            TextEditorStatus.Text = ex.Message;
        }
        finally
        {
            loadingText = false;
        }
    }

    private void TextMessage_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (loadingText || currentTextArchive is null || sender is not TextBox textBox || textBox.DataContext is not TextMessageRow row)
        {
            return;
        }

        // TextChanged also arrives (after the fact) for text a load put in the box; only real edits count.
        string text = textBox.Text ?? string.Empty;
        if (row.Index < currentTextArchive.Messages.Count && currentTextArchive.Messages[row.Index] == text) return;
        row.Text = text;
        currentTextArchive.Messages[row.Index] = row.Text;
        textDirty = true;
        ReportEdit(textArchiveEdits, () => currentTextArchive, TextEditorStatus);
        UpdateTextPreview();
    }

    private void TextMessageSelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        TextEditorStatus.Text = currentTextArchive is null
            ? "No text archive loaded."
            : $"Archive {currentTextArchive.Id:D4}: {TextMessages.Count} messages.";
        UpdateTextPreview();
    }

    private void SaveTextArchive_Click(object? sender, RoutedEventArgs e)
    {
        SaveCurrentTextArchive();
    }

    private bool SaveCurrentTextArchive()
    {
        if (currentProject is null || currentTextArchive is null)
        {
            TextEditorStatus.Text = "No text archive is selected.";
            return false;
        }

        try
        {
            string scriptNote = SaveTextArchiveKeepingScripts(currentTextArchive);
            textArchiveEdits.Saved(currentTextArchive);
            textDirty = false;
            SaveTextArchiveName();
            TextEditorStatus.Text = $"Saved {currentTextArchive.Id:D4}.json. It will be encoded when Save ROM is used.{scriptNote}";
            return true;
        }
        catch (Exception ex)
        {
            LogUiError("Text archive could not be saved", ex);
            TextEditorStatus.Text = ex.Message;
            return false;
        }
    }

    private void AddTextMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (currentTextArchive is null) return;
        TextMessages.Add(new TextMessageRow(TextMessages.Count, string.Empty));
        currentTextArchive.Messages.Add(string.Empty);
        textDirty = true;
    }

    private void RemoveTextMessage_Click(object? sender, RoutedEventArgs e)
    {
        if (currentTextArchive is null || TextMessages.Count == 0) return;
        TextMessages.RemoveAt(TextMessages.Count - 1);
        currentTextArchive.Messages.RemoveAt(currentTextArchive.Messages.Count - 1);
        textDirty = true;
    }

    private void MoveTextUp_Click(object? sender, RoutedEventArgs e) => MoveSelectedTextMessage(-1);
    private void MoveTextDown_Click(object? sender, RoutedEventArgs e) => MoveSelectedTextMessage(1);

    private void MoveSelectedTextMessage(int direction)
    {
        int index = TextMessagesList.SelectedIndex;
        int target = index + direction;
        if (currentTextArchive is null || index < 0 || target < 0 || target >= TextMessages.Count) return;
        (TextMessages[index], TextMessages[target]) = (TextMessages[target], TextMessages[index]);
        (currentTextArchive.Messages[index], currentTextArchive.Messages[target]) =
            (currentTextArchive.Messages[target], currentTextArchive.Messages[index]);
        TextMessagesList.SelectedIndex = target;
        textDirty = true;
    }

    private void AddTextArchive_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null) return;
        int id = TextArchiveService.Add(currentProject);
        ConfigureTextEditor();
        TextArchiveSelector.SelectedIndex = textArchiveIds.IndexOf(id);
    }

    private void RemoveTextArchive_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || TextArchiveSelector.SelectedIndex < 0 || textArchiveIds.Count == 0) return;
        TextArchiveService.RemoveLast(currentProject);
        ConfigureTextEditor();
    }

    private TextSearchResultsWindow? textSearchResultsWindow;

    private void SearchText_Click(object? sender, RoutedEventArgs e)
    {
        string query = TextSearchInput.Text ?? string.Empty;
        if (query.Length == 0 || currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;

        StringComparison comparison = TextSearchCaseSensitive.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        IEnumerable<int> ids = TextSearchAllArchives.IsChecked == true ? textArchiveIds : currentTextArchive is null ? [] : [currentTextArchive.Id];
        var results = new List<TextSearchResultsWindow.Result>();
        try
        {
            foreach (int id in ids)
            {
                // The open archive is searched as it stands in the editor, unsaved edits included.
                IReadOnlyList<string> messages = currentTextArchive is not null && currentTextArchive.Id == id
                    ? currentTextArchive.Messages
                    : GetOpenTextArchive(id, metadata).Messages;
                for (int i = 0; i < messages.Count; i++)
                {
                    if (messages[i].Contains(query, comparison)) results.Add(new TextSearchResultsWindow.Result(id, i, messages[i]));
                }
            }
        }
        catch (Exception ex)
        {
            LogUiError("Text search failed", ex);
            TextSearchStatus.Text = ex.Message;
            return;
        }

        TextSearchStatus.Text = $"{results.Count} matching messages.";
        textSearchResultsWindow?.Close();
        if (results.Count == 0) return;

        textSearchResultsWindow = new TextSearchResultsWindow(query, results, GoToTextSearchResult);
        textSearchResultsWindow.Closed += (sender, _) =>
        {
            if (ReferenceEquals(sender, textSearchResultsWindow)) textSearchResultsWindow = null;
        };
        textSearchResultsWindow.Show(this);
    }

    private async void GoToTextSearchResult(TextSearchResultsWindow.Result result)
    {
        if (blockingLoadCount > 0) return;
        SelectEditor("Text Editor");
        int archiveIndex = textArchiveIds.IndexOf(result.ArchiveId);
        if (archiveIndex < 0)
        {
            TextEditorStatus.Text = $"Text archive {result.ArchiveId:D4} no longer exists.";
            return;
        }

        if (!await OpenTextArchiveAsync(archiveIndex)) return;
        if (currentTextArchive?.Id != result.ArchiveId || result.MessageIndex >= TextMessages.Count) return;

        TextMessagesList.SelectedIndex = result.MessageIndex;
        TextMessagesList.ScrollIntoView(result.MessageIndex);
        UpdateTextPreview();
    }

    #region Project search

    private const int MaxResultsPerCategory = 400;
    private ProjectSearchWindow? projectSearchWindow;

    private void SearchProject_Click(object? sender, RoutedEventArgs e) => OpenProjectSearch();

    private void OpenProjectSearch()
    {
        if (currentProject is null)
        {
            StatusText.Text = "Open a ROM or project before searching it.";
            return;
        }

        if (projectSearchWindow is not null)
        {
            projectSearchWindow.Activate();
            return;
        }

        projectSearchWindow = new ProjectSearchWindow(SearchProjectAsync);
        projectSearchWindow.Closed += (_, _) => projectSearchWindow = null;
        projectSearchWindow.Show(this);
    }

    /// <summary>Everything in the project that mentions <paramref name="query"/>. A header whose
    /// internal name or location name matches also brings in what it links to, so searching a place
    /// name finds its maps, matrix, scripts, texts and encounters even where they do not contain it.</summary>
    private async Task<IReadOnlyList<ProjectSearchResult>> SearchProjectAsync(string query)
    {
        var results = new List<ProjectSearchResult>();
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return results;
        bool Matches(string? text) => text?.Contains(query, StringComparison.OrdinalIgnoreCase) == true;
        void Add(string category, string label, Action open)
        {
            if (results.Count(result => result.Category == category) < MaxResultsPerCategory) results.Add(new ProjectSearchResult(category, label, open));
        }

        EnsureMapEditorDataLoaded();
        EnsureMatrixSelectorLoaded();
        EnsureLocationLookupsLoaded();

        // Headers, and everything linked to a header found by name.
        foreach (HeaderListEntry header in allHeaders)
        {
            if (!Matches(header.InternalName) && !Matches(header.LocationName) && header.Id.ToString() != query) continue;
            ushort id = header.Id;
            Add("Header", header.ToString(), () => OpenHeaderFromSearch(id));
            if (headerStore is null) continue;

            MapHeaderRecord record = headerStore.Load(id);
            string owner = $"{header.InternalName} ({header.LocationName})";
            foreach (MapViewEntry map in mapEntries.Where(map => mapHeaderIds.TryGetValue(map.Id, out ushort mapHeader) && mapHeader == id))
            {
                int mapId = map.Id;
                Add("Linked map", $"{map} - map of {owner}", () => OpenMapFromSearch(mapId));
            }

            Add("Linked matrix", $"Matrix {record.MatrixId:D4} - matrix of {owner}", () => OpenMatrixFromSearch(record.MatrixId));
            Add("Linked script", $"Script file {ScriptFileLabel(record.ScriptFileId, "")} - script of {owner}", () => OpenScriptFromSearch(record.ScriptFileId, null));
            Add("Linked level script", $"Level script {record.LevelScriptId:D4} - level script of {owner}", () => OpenLevelScriptFromSearch(record.LevelScriptId));
            Add("Linked text", $"Text archive {record.TextArchiveId:D4} - texts of {owner}", () => OpenTextArchiveFromSearch(record.TextArchiveId, 0));
            if (WildEncounterService.GetFileIds(currentProject).Contains(record.WildPokemonId))
            {
                Add("Linked encounters", $"Encounter file {record.WildPokemonId:D4} - wild Pokemon of {owner}", () => OpenWildEncounterFromSearch(record.WildPokemonId));
            }
        }

        foreach (MapViewEntry map in mapEntries.Where(map => Matches(map.ToString())))
        {
            int mapId = map.Id;
            Add("Map", map.ToString(), () => OpenMapFromSearch(mapId));
        }

        foreach (MatrixListEntry matrix in allMapMatrices.Where(matrix => Matches(matrix.Label)))
        {
            int matrixId = matrix.Id;
            Add("Matrix", matrix.Label, () => OpenMatrixFromSearch(matrixId));
        }

        // Files are read off the UI thread; archives open in the editors are searched as they are now.
        var openArchives = openTextArchives.ToDictionary(pair => pair.Key, pair => pair.Value.Messages.ToList());
        RomProject project = currentProject;
        var namedArchives = new Dictionary<int, string>
        {
            [TrainerService.GetTrainerNamesArchiveId(metadata)] = "Trainer",
            [TrainerService.GetPokemonNamesArchiveId(metadata)] = "Pokemon",
            [TrainerService.GetAttackNamesArchiveId(metadata)] = "Move",
            [TrainerService.GetItemNamesArchiveId(metadata)] = "Item"
        };
        var found = await Task.Run(() =>
        {
            var named = new List<(string Category, int Id, string Name)>();
            var messages = new List<(int Archive, int Index, string Text)>();
            foreach (int archiveId in TextArchiveService.GetArchiveIds(project))
            {
                List<string> archiveMessages;
                try
                {
                    archiveMessages = openArchives.TryGetValue(archiveId, out List<string>? open) ? open : TextArchiveService.Load(project, archiveId, metadata).Messages.ToList();
                }
                catch (Exception)
                {
                    continue;
                }

                for (int i = 0; i < archiveMessages.Count; i++)
                {
                    if (!archiveMessages[i].Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
                    if (namedArchives.TryGetValue(archiveId, out string? category)) named.Add((category, i, archiveMessages[i]));
                    else messages.Add((archiveId, i, archiveMessages[i]));
                }
            }

            var scripts = new List<(int File, int Line, string Text)>();
            if (Directory.Exists(project.ExpandedScriptsDirectory))
            {
                foreach (string path in Directory.GetFiles(project.ExpandedScriptsDirectory, "*.script").OrderBy(path => path, StringComparer.Ordinal))
                {
                    if (!int.TryParse(Path.GetFileNameWithoutExtension(path), out int fileId)) continue;
                    string[] lines = File.ReadAllLines(path);
                    for (int i = 0; i < lines.Length; i++)
                    {
                        if (lines[i].Contains(query, StringComparison.OrdinalIgnoreCase)) scripts.Add((fileId, i + 1, lines[i].Trim()));
                    }
                }
            }

            return (named, messages, scripts);
        });

        foreach ((string category, int id, string name) in found.named)
        {
            Action open = category switch
            {
                "Trainer" => () => OpenTrainerFromSearch(id),
                "Pokemon" => () => OpenListEditorFromSearch("Pokemon Editor", PokeEditorSpeciesSelector, id),
                "Move" => () => OpenListEditorFromSearch("Move Editor", MoveSelector, id),
                _ => () => OpenListEditorFromSearch("Item Editor", ItemSelector, id)
            };
            Add(category, $"{id:D3} - {name}", open);
        }

        foreach ((int file, int line, string text) in found.scripts)
        {
            Add("Script", $"Script file {file:D4}, line {line}: {text}", () => OpenScriptFromSearch(file, query));
        }

        foreach ((int archive, int index, string text) in found.messages)
        {
            Add("Text", $"Text archive {archive:D4}, message {index}: {text}", () => OpenTextArchiveFromSearch(archive, index));
        }

        return results;
    }

    private void OpenHeaderFromSearch(ushort headerId)
    {
        SelectEditor("Map Editor");
        ClearHeaderFilters();
        for (int i = 0; i < HeaderSelector.Items.Count; i++)
        {
            if (HeaderSelector.Items[i] is HeaderListEntry entry && entry.Id == headerId)
            {
                HeaderSelector.SelectedIndex = i;
                return;
            }
        }
    }

    private void OpenMapFromSearch(int mapId)
    {
        SelectEditor("Map Editor");
        EnsureMapEditorDataLoaded();
        int index = mapEntries.FindIndex(map => map.Id == mapId);
        if (index >= 0) OpenMapInSingleView(index, currentMapPane is MapPane.Buildings or MapPane.Permissions or MapPane.Events ? currentMapPane : MapPane.Header);
    }

    private void OpenMatrixFromSearch(int matrixId)
    {
        SelectEditor("Map Editor");
        EnsureMatrixSelectorLoaded();
        SetMatrixScope(headerScope: false);
        MatrixListEntry? entry = allMapMatrices.FirstOrDefault(item => item.Id == matrixId);
        if (entry is not null) MapMatrixSelector.SelectedItem = entry;
    }

    private void OpenScriptFromSearch(int scriptFileId, string? query)
    {
        SelectEditor("Script Editor");
        if (scriptFileIds.Count == 0) ConfigureScriptEditor();
        SelectScriptFileId(scriptFileId);
        if (query is null) return;
        // Search looks only at the visible tab, so show the first one holding the text.
        foreach ((TabItem tab, TextBox box) in new[] { (ScriptsTab, ScriptsTextBox), (FunctionsTab, FunctionsTextBox), (ActionsTab, ActionsTextBox) })
        {
            if (box.Text?.Contains(query, StringComparison.OrdinalIgnoreCase) != true) continue;
            ScriptSectionTabs.SelectedItem = tab;
            break;
        }

        ScriptSearchInput.Text = query;
        SearchScript_Click(null, new RoutedEventArgs());
    }

    private void OpenLevelScriptFromSearch(int levelScriptId)
    {
        SelectEditor("Level Script Editor");
        if (levelScriptIds.Count == 0) ConfigureLevelScriptEditor();
        SelectLevelScriptId(levelScriptId);
    }

    private void OpenTextArchiveFromSearch(int archiveId, int messageIndex) =>
        GoToTextSearchResult(new TextSearchResultsWindow.Result(archiveId, messageIndex, string.Empty));

    private async void OpenWildEncounterFromSearch(int encounterId)
    {
        SelectEditor("Wild Pokemon Encounter Editor");
        int index = wildEncounterIds.IndexOf(encounterId);
        if (index < 0) return;
        await OpenWildEncounterAsync(index);
    }

    private void OpenTrainerFromSearch(int trainerId)
    {
        SelectEditor("Trainer Editor");
        if (!string.IsNullOrEmpty(TrainerFilterInput.Text)) TrainerFilterInput.Text = string.Empty;
        if (filteredTrainerIds.Contains(trainerId)) _ = OpenTrainerAsync(trainerId);
    }

    /// <summary>For the editors whose selector lists every entry in ID order.</summary>
    private void OpenListEditorFromSearch(string editor, ComboBox selector, int id)
    {
        SelectEditor(editor);
        if (id < selector.ItemCount) selector.SelectedIndex = id;
    }

    #endregion

    #region Shared text archives

    // The Text Editor and the Script Editor (whose Message lines show and edit the text inline) work
    // on the same in-memory copy of an archive, so an edit in either is what the other shows, and
    // saving from either saves both editors' changes.
    private readonly Dictionary<int, TextArchiveData> openTextArchives = [];
    private int? scriptTextArchiveOverride;

    private TextArchiveData GetOpenTextArchive(int archiveId, RomGameMetadata metadata)
    {
        if (!openTextArchives.TryGetValue(archiveId, out TextArchiveData? archive))
        {
            archive = TextArchiveService.Load(currentProject!, archiveId, metadata);
            openTextArchives[archiveId] = archive;
        }

        return archive;
    }

    /// <summary>Redraws the Text Editor's messages from the shared archive, picking up edits made through
    /// the Script Editor since the list was built.</summary>
    private void RefreshTextMessageRows()
    {
        if (currentTextArchive is null) return;
        bool unchanged = TextMessages.Count == currentTextArchive.Messages.Count &&
            TextMessages.Select(row => row.Text).SequenceEqual(currentTextArchive.Messages);
        if (unchanged) return;

        int selected = TextMessagesList.SelectedIndex;
        loadingText = true;
        try
        {
            TextMessages.Clear();
            for (int i = 0; i < currentTextArchive.Messages.Count; i++) TextMessages.Add(new TextMessageRow(i, currentTextArchive.Messages[i]));
            TextMessagesList.SelectedIndex = Math.Min(selected, TextMessages.Count - 1);
        }
        finally
        {
            loadingText = false;
        }
    }

    #endregion

    #region Text preview

    // Measured from oxes/example_vs_seeker.png, a screenshot of the in-game message box at 854x149
    // (a DS pixel is 854/256 of these): where the first line's top sits, the distance between the two
    // lines, the font size that reproduces the glyph widths, and how far right text may run before
    // it reaches the box's right border.
    private const double TextPreviewLeft = 48;
    private const double TextPreviewTop = 14;
    private const double TextPreviewLineSpacing = 54;
    private const double TextPreviewFontSize = 57;
    private const double TextPreviewMaxLineWidth = 736;
    private const double TextPreviewShadowOffset = 854.0 / 256.0;
    private const int TextPreviewLinesPerScreen = 2;

    private static readonly FontFamily TextPreviewFont = new("avares://DSPRE.Avalonia/Assets/TextPreview#Pokémon DP Pro");
    private static readonly IBrush TextPreviewTextBrush = new SolidColorBrush(Color.FromRgb(82, 82, 90));
    private static readonly IBrush TextPreviewShadowBrush = new SolidColorBrush(Color.FromRgb(165, 165, 173));

    private List<string[]> textPreviewScreens = [];
    private int textPreviewScreen;
    private string textPreviewSource = string.Empty;

    /// <summary>Splits message text into what the two-line message box shows at each step. In the
    /// editor's text these are written out as a backslash and a letter: \n starts the next line, which
    /// scrolls the box up a line once both are full, while \r and \f end what the box shows and start
    /// a new, empty message box.</summary>
    private static List<string[]> BuildTextPreviewScreens(string text)
    {
        var pages = new List<List<string>> { new() { string.Empty } };
        for (int i = 0; i < text.Length; i++)
        {
            char c = text[i];
            char escape = c == '\\' && i + 1 < text.Length ? text[i + 1] : '\0';
            if (escape is 'n' or 'r' or 'f') i++;
            else if (c == '\r') continue;

            if (escape is 'f' or 'r') pages.Add([string.Empty]);
            else if (escape == 'n' || c == '\n') pages[^1].Add(string.Empty);
            else pages[^1][^1] += c;
        }

        var screens = new List<string[]>();
        foreach (List<string> lines in pages)
        {
            while (lines.Count > 1 && lines[^1].Length == 0) lines.RemoveAt(lines.Count - 1);
            if (lines.Count == 1 && lines[0].Length == 0 && pages.Count > 1) continue;

            int last = Math.Max(0, lines.Count - TextPreviewLinesPerScreen);
            for (int start = 0; start <= last; start++)
            {
                screens.Add(lines.Skip(start).Take(TextPreviewLinesPerScreen).ToArray());
            }
        }

        return screens;
    }

    /// <summary>Previews the highlighted part of the message being edited, or the whole message when
    /// nothing is highlighted.</summary>
    private void UpdateTextPreview()
    {
        string source = string.Empty;
        if (TopLevel.GetTopLevel(this)?.FocusManager?.GetFocusedElement() is TextBox { DataContext: TextMessageRow } focused)
        {
            source = string.IsNullOrEmpty(focused.SelectedText) ? focused.Text ?? string.Empty : focused.SelectedText;
        }
        else if (TextMessagesList.SelectedItem is TextMessageRow row)
        {
            source = row.Text;
        }

        if (source == textPreviewSource) return;
        textPreviewSource = source;
        textPreviewScreens = BuildTextPreviewScreens(source);
        textPreviewScreen = 0;
        RenderTextPreview();
    }

    private void RenderTextPreview()
    {
        // The box image is the canvas's first child; everything after it is the previous screen's text.
        while (TextPreviewCanvas.Children.Count > 1) TextPreviewCanvas.Children.RemoveAt(TextPreviewCanvas.Children.Count - 1);

        bool hasScreens = textPreviewScreens.Count > 0 && textPreviewSource.Length > 0;
        textPreviewScreen = Math.Clamp(textPreviewScreen, 0, Math.Max(0, textPreviewScreens.Count - 1));
        TextPreviewPreviousButton.IsEnabled = hasScreens && textPreviewScreen > 0;
        TextPreviewNextButton.IsEnabled = hasScreens && textPreviewScreen < textPreviewScreens.Count - 1;
        TextPreviewScreenLabel.Text = hasScreens && textPreviewScreens.Count > 1 ? $"Screen {textPreviewScreen + 1} of {textPreviewScreens.Count}" : string.Empty;
        if (!hasScreens)
        {
            TextPreviewStatus.Text = "Click a message, or highlight part of one, to preview it.";
            return;
        }

        var typeface = new Typeface(TextPreviewFont);
        var tooWide = new List<int>();
        string[] lines = textPreviewScreens[textPreviewScreen];
        for (int line = 0; line < lines.Length; line++)
        {
            double top = TextPreviewTop + line * TextPreviewLineSpacing;
            // The game draws a one-pixel shadow to the right of, below and diagonally from each glyph.
            foreach ((double dx, double dy, IBrush brush) in new[]
            {
                (TextPreviewShadowOffset, 0.0, TextPreviewShadowBrush),
                (0.0, TextPreviewShadowOffset, TextPreviewShadowBrush),
                (TextPreviewShadowOffset, TextPreviewShadowOffset, TextPreviewShadowBrush),
                (0.0, 0.0, TextPreviewTextBrush)
            })
            {
                var block = new TextBlock { Text = lines[line], FontFamily = TextPreviewFont, FontSize = TextPreviewFontSize, Foreground = brush };
                Canvas.SetLeft(block, TextPreviewLeft + dx);
                Canvas.SetTop(block, top + dy);
                TextPreviewCanvas.Children.Add(block);
            }

            var measured = new FormattedText(
                lines[line], CultureInfo.InvariantCulture, FlowDirection.LeftToRight, typeface, TextPreviewFontSize, TextPreviewTextBrush);
            if (measured.Width > TextPreviewMaxLineWidth) tooWide.Add(line + 1);
        }

        TextPreviewStatus.Text = tooWide.Count > 0
            ? $"Line {string.Join(" and ", tooWide)} of this screen runs past the edge of the message box. Text variables such as {{STRVAR_1 ...}} are shown as written, not as the name the game puts there."
            : "Shown as the message box draws it. Text variables such as {STRVAR_1 ...} are shown as written.";
        TextPreviewStatus.Foreground = tooWide.Count > 0 ? Brushes.IndianRed : TextPreviewStatusDefaultBrush;
    }

    private static readonly IBrush TextPreviewStatusDefaultBrush = new SolidColorBrush(Color.Parse("#AAB7C4"));

    private void TextPreviewPrevious_Click(object? sender, RoutedEventArgs e)
    {
        textPreviewScreen--;
        RenderTextPreview();
    }

    private void TextPreviewNext_Click(object? sender, RoutedEventArgs e)
    {
        textPreviewScreen++;
        RenderTextPreview();
    }

    #endregion

    private async void ReplaceText_Click(object? sender, RoutedEventArgs e)
    {
        string query = TextSearchInput.Text ?? string.Empty;
        string replacement = TextReplaceInput.Text ?? string.Empty;
        if (query.Length == 0 || currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        IEnumerable<int> ids = TextSearchAllArchives.IsChecked == true ? textArchiveIds : currentTextArchive is null ? [] : [currentTextArchive.Id];

        StringComparison countComparison = TextSearchCaseSensitive.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
        int pending = 0, pendingMessages = 0, pendingArchives = 0;
        foreach (int id in ids)
        {
            int inArchive = 0;
            foreach (string message in GetOpenTextArchive(id, metadata).Messages)
            {
                ReplaceAll(message, query, replacement, countComparison, out int count);
                inArchive += count;
                if (count > 0) pendingMessages++;
            }

            pending += inArchive;
            if (inArchive > 0) pendingArchives++;
        }

        if (pending == 0)
        {
            TextSearchStatus.Text = $"\"{query}\" was not found; nothing to replace.";
            return;
        }

        string? choice = await new ChoiceDialog(
            "Replace all",
            $"Replace {pending} occurrence{(pending == 1 ? "" : "s")} of \"{query}\" with \"{replacement}\" in {pendingMessages} message{(pendingMessages == 1 ? "" : "s")} " +
            $"across {pendingArchives} text archive{(pendingArchives == 1 ? "" : "s")}? The archives are saved straight away.",
            ("replace", "Replace All"), ("cancel", "Cancel")).ShowDialog<string?>(this);
        if (choice != "replace")
        {
            TextSearchStatus.Text = "Replace all was cancelled.";
            return;
        }

        int replacements = 0;
        foreach (int id in ids)
        {
            TextArchiveData archive = GetOpenTextArchive(id, metadata);
            StringComparison comparison = TextSearchCaseSensitive.IsChecked == true ? StringComparison.Ordinal : StringComparison.OrdinalIgnoreCase;
            int inArchive = 0;
            for (int i = 0; i < archive.Messages.Count; i++)
            {
                string original = archive.Messages[i];
                archive.Messages[i] = ReplaceAll(original, query, replacement, comparison, out int count);
                inArchive += count;
            }

            replacements += inArchive;
            if (inArchive > 0) SaveTextArchiveKeepingScripts(archive);
        }
        if (currentTextArchive is not null) LoadSelectedTextArchive();
        TextSearchStatus.Text = $"Replaced {replacements} occurrences.";
    }

    private static string ReplaceAll(string value, string search, string replacement, StringComparison comparison, out int count)
    {
        count = 0;
        int position = 0;
        var result = new System.Text.StringBuilder();
        while (position < value.Length)
        {
            int match = value.IndexOf(search, position, comparison);
            if (match < 0) { result.Append(value[position..]); break; }
            result.Append(value[position..match]).Append(replacement);
            position = match + search.Length;
            count++;
        }
        return count == 0 ? value : result.ToString();
    }

    private async void ImportTextArchive_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || TextArchiveSelector.SelectedIndex < 0) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions { AllowMultiple = false, FileTypeFilter = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }] });
        if (files.Count != 1) return;
        TextArchiveService.Import(currentProject, textArchiveIds[TextArchiveSelector.SelectedIndex], files[0].Path.LocalPath);
        LoadSelectedTextArchive();
    }

    private async void ExportTextArchive_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentTextArchive is null) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions { SuggestedFileName = $"{currentTextArchive.Id:D4}.json", FileTypeChoices = [new FilePickerFileType("JSON") { Patterns = ["*.json"] }] });
        if (file is not null) File.Copy(TextArchiveService.GetJsonPath(currentProject, currentTextArchive.Id), file.Path.LocalPath, true);
    }

    private void ConfigureScriptEditor()
    {
        if (currentProject is null)
        {
            return;
        }

        RefreshScriptFileNames();
        scriptFileIds.Clear();
        loadedScriptFileIndex = -1;
        ScriptFileSelector.Items.Clear();
        if (!Directory.Exists(currentProject.ExpandedScriptsDirectory))
        {
            ScriptSearchStatus.Text = "No exported plaintext scripts found.";
            return;
        }

        scriptFileIds.Add(GlobalLuaEntryId);
        ScriptFileSelector.Items.Add("global.lua");
        foreach (string path in Directory.GetFiles(currentProject.ExpandedScriptsDirectory, "*.script").OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (int.TryParse(Path.GetFileNameWithoutExtension(path), out int id))
            {
                scriptFileIds.Add(id);
                ScriptFileSelector.Items.Add(ScriptFileLabel(id));
            }
        }

        if (ScriptFileSelector.Items.Count > 1)
        {
            ScriptFileSelector.SelectedIndex = 1;
        }
        else
        {
            ScriptSearchStatus.Text = "No exported plaintext scripts found.";
        }
    }

    private void SelectScriptFileId(int scriptId)
    {
        for (int i = 0; i < scriptFileIds.Count; i++)
        {
            if (scriptFileIds[i] == scriptId)
            {
                ScriptFileSelector.SelectedIndex = i;
                return;
            }
        }

        ScriptSearchStatus.Text = $"Script file {scriptId:D4} was not exported.";
    }

    private void ConfigureSpecialEncounterEditor()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            SpecialEncounterStatus.Text = "Game metadata is unavailable.";
            return;
        }

        EnsurePokemonNamesLoaded(metadata);

        try
        {
            // Re-extracting would overwrite edits saved since the last Save ROM.
            if (!Directory.Exists(currentProject.ExtendedEncountersDirectory) && !Directory.Exists(currentProject.SafariZoneDirectory) && !Directory.Exists(currentProject.HeadbuttDirectory))
            {
                new RomExtractionService().PrepareSpecialEncounters(currentProject, metadata);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Special encounter archives could not be unpacked: {ex.Message}");
        }

        bool dppt = SpecialEncounterService.SupportsHoneyTree(metadata);
        bool hgss = SpecialEncounterService.SupportsHgssFeatures(metadata);
        HoneyTreeTab.IsVisible = dppt;
        GreatMarshTab.IsVisible = dppt;
        BugContestTab.IsVisible = hgss;
        HeadbuttTab.IsVisible = hgss;
        SafariZoneTab.IsVisible = hgss;
        // A hidden tab that is still selected keeps showing its content (the Honey Tree tab, first in the
        // list, on HGSS), so the first tab this game has is selected instead.
        if (SpecialEncounterTabs.SelectedItem is not TabItem { IsVisible: true })
        {
            SpecialEncounterTabs.SelectedItem = SpecialEncounterTabs.Items.OfType<TabItem>().FirstOrDefault(tab => tab.IsVisible);
        }

        loadingSpecialEncounters = true;
        try
        {
            if (dppt)
            {
                honeyTreeGroups = SpecialEncounterService.LoadHoneyTree(currentProject, metadata);
                honeyTreeEdits.Loaded(honeyTreeGroups, "Honey Tree encounters");
                HoneyTreeGroupSelector.Items.Clear();
                foreach (HoneyTreeGroup group in honeyTreeGroups) HoneyTreeGroupSelector.Items.Add(group.Name);

                greatMarshGroups = SpecialEncounterService.LoadGreatMarsh(currentProject);
                greatMarshEdits.Loaded(greatMarshGroups, "Great Marsh encounters");
                GreatMarshGroupSelector.Items.Clear();
                foreach (GreatMarshGroup group in greatMarshGroups) GreatMarshGroupSelector.Items.Add(group.Name);
            }

            if (hgss)
            {
                bugContestSets = SpecialEncounterService.LoadBugContest(currentProject);
                bugContestEdits.Loaded(bugContestSets, "Bug Contest encounters");
                BugContestSetSelector.Items.Clear();
                foreach (BugContestSet set in bugContestSets) BugContestSetSelector.Items.Add(set.Name);

                headbuttIds.Clear();
                HeadbuttFileSelector.Items.Clear();
                foreach (int id in SpecialEncounterService.GetHeadbuttIds(currentProject))
                {
                    headbuttIds.Add(id);
                    HeadbuttFileSelector.Items.Add($"Headbutt File {id:D4}");
                }

                safariZoneIds.Clear();
                SafariZoneFileSelector.Items.Clear();
                foreach (int id in SpecialEncounterService.GetSafariZoneIds(currentProject))
                {
                    safariZoneIds.Add(id);
                    string name = SafariZoneEncounterFile.Names.TryGetValue(id, out string? zone) ? zone : "Zone";
                    SafariZoneFileSelector.Items.Add($"{id:D4} - {name}");
                }

                SafariZoneGroupSelector.Items.Clear();
                foreach (string groupName in new[] { "Grass", "Surf", "Old Rod", "Good Rod", "Super Rod" })
                {
                    SafariZoneGroupSelector.Items.Add(groupName);
                }
            }
        }
        finally
        {
            loadingSpecialEncounters = false;
        }

        if (dppt)
        {
            if (HoneyTreeGroupSelector.Items.Count > 0) HoneyTreeGroupSelector.SelectedIndex = 0;
            if (GreatMarshGroupSelector.Items.Count > 0) GreatMarshGroupSelector.SelectedIndex = 0;
            SpecialEncounterStatus.Text = "Loaded Honey Tree and Great Marsh encounters.";
        }

        if (hgss)
        {
            if (BugContestSetSelector.Items.Count > 0) BugContestSetSelector.SelectedIndex = 0;
            if (HeadbuttFileSelector.Items.Count > 0) HeadbuttFileSelector.SelectedIndex = 0;
            if (SafariZoneGroupSelector.Items.Count > 0) SafariZoneGroupSelector.SelectedIndex = 0;
            if (SafariZoneFileSelector.Items.Count > 0) SafariZoneFileSelector.SelectedIndex = 0;
            SpecialEncounterStatus.Text = "Loaded Headbutt, Safari Zone and Bug Contest encounters.";
        }
    }

    private void EnsurePokemonNamesLoaded(RomGameMetadata metadata)
    {
        if (WildPokemonNames.Count > 0 || currentProject is null)
        {
            return;
        }

        int namesArchiveId = metadata.HeaderFormat switch
        {
            MapHeaderFormat.DiamondPearl => 362,
            MapHeaderFormat.Platinum => 412,
            MapHeaderFormat.HeartGoldSoulSilver => 237,
            _ => 412
        };

        try
        {
            TextArchiveData namesArchive = TextArchiveService.Load(currentProject, namesArchiveId, metadata);
            foreach (string name in namesArchive.Messages) WildPokemonNames.Add(name);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Pokemon names could not be loaded: {ex.Message}");
        }
    }

    private void ConfigureTrainerEditor()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            return;
        }

        // Ball seals only ever apply on Platinum/HGSS, and gender/ability-slot flags on HGSS or
        // Platinum with the AI backport, per DS_Map's TrainerEditor - see DSPRE.Core/TrainerService.cs.
        showTrainerBallSealsAndForms = metadata.HeaderFormat is MapHeaderFormat.Platinum or MapHeaderFormat.HeartGoldSoulSilver;
        showTrainerGenderAndAbility = TrainerPersonality.FlagsApply(currentProject, metadata);

        LoadTrainerNameLists(metadata);
        LoadTrainerNamesCache(metadata);

        trainerIds.Clear();
        trainerIds.AddRange(TrainerService.GetIds(currentProject));
        RefreshTrainerSelectorList();

        if (TrainerSelector.Items.Count > 0)
        {
            TrainerSelector.SelectedIndex = 0;
        }
        else
        {
            TrainerEditorStatus.Text = "No trainer data found in this project.";
        }
    }

    private void LoadTrainerNameLists(RomGameMetadata metadata)
    {
        if (currentProject is null)
        {
            return;
        }

        LoadNameListOnce(TrainerClassNames, TrainerService.GetTrainerClassNamesArchiveId(metadata), metadata, "trainer class");
        LoadNameListOnce(TrainerPokemonNames, TrainerService.GetPokemonNamesArchiveId(metadata), metadata, "Pokemon");
        LoadNameListOnce(TrainerItemNames, TrainerService.GetItemNamesArchiveId(metadata), metadata, "item");
        LoadNameListOnce(TrainerMoveNames, TrainerService.GetAttackNamesArchiveId(metadata), metadata, "move");
    }

    private void LoadNameListOnce(ObservableCollection<string> target, int archiveId, RomGameMetadata metadata, string label)
    {
        if (target.Count > 0 || currentProject is null)
        {
            return;
        }

        try
        {
            TextArchiveData archive = TextArchiveService.Load(currentProject, archiveId, metadata);
            foreach (string name in archive.Messages) target.Add(name);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Trainer editor {label} names could not be loaded: {ex.Message}");
        }
    }

    private void LoadTrainerNamesCache(RomGameMetadata metadata)
    {
        if (currentProject is null)
        {
            return;
        }

        trainerNamesCache.Clear();
        try
        {
            TextArchiveData namesArchive = TextArchiveService.Load(currentProject, TrainerService.GetTrainerNamesArchiveId(metadata), metadata);
            for (int i = 0; i < namesArchive.Messages.Count; i++)
            {
                trainerNamesCache[i] = TrainerService.UnwrapTrainerName(namesArchive.Messages[i]);
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Trainer names could not be loaded: {ex.Message}");
        }
    }

    private void RefreshTrainerSelectorList()
    {
        string filter = TrainerFilterInput.Text ?? "";
        int? previouslySelectedId = TrainerSelector.SelectedItem is TrainerSelectorEntry current ? current.Id : null;

        filteredTrainerIds.Clear();
        TrainerSelector.Items.Clear();
        foreach (int id in trainerIds)
        {
            string name = trainerNamesCache.TryGetValue(id, out string? cachedName) ? cachedName : $"Trainer {id}";
            if (!string.IsNullOrWhiteSpace(filter) &&
                name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                !id.ToString().Contains(filter, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            filteredTrainerIds.Add(id);
            TrainerSelector.Items.Add(new TrainerSelectorEntry(id, name));
        }

        if (previouslySelectedId is int selectedId)
        {
            int index = filteredTrainerIds.IndexOf(selectedId);
            if (index >= 0) TrainerSelector.SelectedIndex = index;
        }
    }

    private void TrainerFilterInput_TextChanged(object? sender, TextChangedEventArgs e) => RefreshTrainerSelectorList();

    private async void TrainerSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingTrainer || switchingDocument || currentProject is null || TrainerSelector.SelectedItem is not TrainerSelectorEntry entry)
        {
            return;
        }

        await OpenTrainerAsync(entry.Id);
    }

    /// <summary>Opens trainer <paramref name="id"/>, once the open trainer's unsaved changes are saved or
    /// discarded. False when the user chose to stay.</summary>
    private async Task<bool> OpenTrainerAsync(int id)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return false;
        int open = currentTrainer?.Id ?? -1;
        if (!await ShouldLoadDocumentAsync(id == open, "Trainer Editor", () => TrainerSelector.SelectedIndex = filteredTrainerIds.IndexOf(open))) return id == open;
        RunSwitchingDocument(() => TrainerSelector.SelectedIndex = filteredTrainerIds.IndexOf(id));
        LoadTrainer(id, metadata);
        return true;
    }

    private void LoadTrainer(int id, RomGameMetadata metadata)
    {
        if (currentProject is null)
        {
            return;
        }

        loadingTrainer = true;
        try
        {
            string name = trainerNamesCache.TryGetValue(id, out string? cachedName) ? cachedName : "";
            currentTrainer = TrainerService.Load(currentProject, id, metadata, name);
            trainerEdits.Loaded(currentTrainer, $"Trainer {id:D3}");

            TrainerNameInput.Text = currentTrainer.Name;
            TrainerClassSelector.SelectedIndex = currentTrainer.TrainerClass < TrainerClassNames.Count ? currentTrainer.TrainerClass : -1;
            TrainerDoubleBattleCheck.IsChecked = currentTrainer.DoubleBattle;
            TrainerChooseMovesCheck.IsChecked = currentTrainer.ChooseMoves;
            TrainerChooseItemsCheck.IsChecked = currentTrainer.ChooseItems;
            TrainerPartyCountInput.Text = currentTrainer.PartyCount.ToString();

            TrainerItem1Selector.SelectedIndex = currentTrainer.TrainerItems[0];
            TrainerItem2Selector.SelectedIndex = currentTrainer.TrainerItems[1];
            TrainerItem3Selector.SelectedIndex = currentTrainer.TrainerItems[2];
            TrainerItem4Selector.SelectedIndex = currentTrainer.TrainerItems[3];

            CheckBox[] aiChecks = GetTrainerAiCheckboxes();
            for (int i = 0; i < aiChecks.Length; i++)
            {
                aiChecks[i].IsChecked = currentTrainer.Ai[i];
            }

            TrainerPartyRowItems.Clear();
            for (int i = 0; i < TrainerRecord.PartySize; i++)
            {
                PartyMemberRow row = PartyRowFor(i, currentTrainer.Party[i]);
                TrainerPartyRowItems.Add(row);
                UpdatePartyRowIcon(row, metadata);
            }

            LoadTrainerClassSprite(currentTrainer.TrainerClass, metadata);
            ShowTrainerUses(id, metadata);
            TrainerEditorStatus.Text = $"Loaded trainer {id:D3} - {currentTrainer.Name}.";
        }
        finally
        {
            loadingTrainer = false;
        }
    }

    private void UpdatePartyRowIcon(PartyMemberRow row, RomGameMetadata metadata)
    {
        if (currentProject is null || row.Species < 0)
        {
            row.Icon = null;
            return;
        }

        byte[]? rgba = SpriteService.GetSpeciesIconRgba(currentProject, metadata, row.Species, 32, 24);
        row.Icon = CreateBitmapFromRgba(rgba, 32, 24);
    }

    internal static WriteableBitmap? CreateBitmapFromRgba(byte[]? rgba, int width, int height)
    {
        if (rgba is null || width <= 0 || height <= 0 || rgba.Length < width * height * 4)
        {
            return null;
        }

        var bitmap = new WriteableBitmap(new PixelSize(width, height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        using ILockedFramebuffer framebuffer = bitmap.Lock();
        Marshal.Copy(rgba, 0, framebuffer.Address, rgba.Length);
        return bitmap;
    }

    private void LoadTrainerClassSprite(int trainerClassId, RomGameMetadata metadata)
    {
        StopTrainerClassSpriteAnimation();
        trainerClassSpriteTiles = null;
        trainerClassSpritePalettes = null;
        trainerClassSpriteCells = null;
        trainerClassSpriteFrameCount = 0;
        trainerClassSpriteFrame = 0;

        if (currentProject is not null && SpriteService.TryLoadTrainerClassSprite(currentProject, metadata, trainerClassId, out NdsTileImage? tiles, out RgbColor[][]? palettes, out NdsCellBank? cells, out int frameCount)
            && tiles is not null && palettes is not null)
        {
            trainerClassSpriteTiles = tiles;
            trainerClassSpritePalettes = palettes;
            trainerClassSpriteCells = cells;
            trainerClassSpriteFrameCount = frameCount;
        }

        RenderTrainerClassSpriteFrame();
        if (TrainerAnimateClassSpriteCheck.IsChecked == true)
        {
            StartTrainerClassSpriteAnimation();
        }
    }

    private void RenderTrainerClassSpriteFrame()
    {
        if (trainerClassSpriteTiles is null || trainerClassSpritePalettes is null || trainerClassSpriteCells is null || trainerClassSpriteFrameCount == 0)
        {
            TrainerClassSpriteImage.Source = null;
            return;
        }

        byte[]? rgba = SpriteService.GetTrainerClassSpriteRgba(trainerClassSpriteTiles, trainerClassSpritePalettes, trainerClassSpriteCells, trainerClassSpriteFrame, 128, 96);
        TrainerClassSpriteImage.Source = CreateBitmapFromRgba(rgba, 128, 96);
    }

    private void StartTrainerClassSpriteAnimation()
    {
        StopTrainerClassSpriteAnimation();
        if (trainerClassSpriteFrameCount <= 1)
        {
            return;
        }

        trainerClassSpriteTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
        trainerClassSpriteTimer.Tick += (_, _) =>
        {
            trainerClassSpriteFrame = (trainerClassSpriteFrame + 1) % trainerClassSpriteFrameCount;
            RenderTrainerClassSpriteFrame();
        };
        trainerClassSpriteTimer.Start();
    }

    private void StopTrainerClassSpriteAnimation()
    {
        trainerClassSpriteTimer?.Stop();
        trainerClassSpriteTimer = null;
    }

    private void TrainerAnimateClassSpriteCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (loadingTrainer) return;
        if (TrainerAnimateClassSpriteCheck.IsChecked == true)
        {
            StartTrainerClassSpriteAnimation();
        }
        else
        {
            StopTrainerClassSpriteAnimation();
            trainerClassSpriteFrame = 0;
            RenderTrainerClassSpriteFrame();
        }
    }

    private CheckBox[] GetTrainerAiCheckboxes() =>
    [
        TrainerAi1Check, TrainerAi2Check, TrainerAi3Check, TrainerAi4Check, TrainerAi5Check, TrainerAi6Check,
        TrainerAi7Check, TrainerAi8Check, TrainerAi9Check, TrainerAi10Check, TrainerAi11Check
    ];

    private void MarkTrainerModified()
    {
        if (!loadingTrainer && currentTrainer is not null)
        {
            ReportEdit(trainerEdits, () =>
            {
                SyncTrainerParty();
                return currentTrainer;
            }, TrainerEditorStatus);
        }
    }

    private static PartyMemberRow PartyRowFor(int index, PartyPokemonRecord poke) => new()
    {
        Index = index,
        Species = poke.Species ?? -1,
        Level = poke.Level,
        Difficulty = poke.Difficulty,
        BallSeals = poke.BallSeals,
        Form = poke.Form,
        GenderIndex = (poke.GenderAndAbility & PartyPokemonRecord.GenderAbilityFlags.ForceFemale) != 0 ? 2
            : (poke.GenderAndAbility & PartyPokemonRecord.GenderAbilityFlags.ForceMale) != 0 ? 1 : 0,
        AbilityIndex = (poke.GenderAndAbility & PartyPokemonRecord.GenderAbilityFlags.AbilitySlot2) != 0 ? 2
            : (poke.GenderAndAbility & PartyPokemonRecord.GenderAbilityFlags.AbilitySlot1) != 0 ? 1 : 0,
        HeldItem = poke.HeldItem ?? -1,
        Move1 = poke.Moves is { Length: > 0 } moves0 ? moves0[0] : -1,
        Move2 = poke.Moves is { Length: > 1 } moves1 ? moves1[1] : -1,
        Move3 = poke.Moves is { Length: > 2 } moves2 ? moves2[2] : -1,
        Move4 = poke.Moves is { Length: > 3 } moves3 ? moves3[3] : -1
    };

    /// <summary>Copies the party grid (bound to TrainerPartyRowItems, not to currentTrainer) into the
    /// trainer as it is edited, so the edit counts as unsaved. Only what differs from how the grid shows
    /// the record is written, so showing a party (one without moves, say) never changes it.</summary>
    private void SyncTrainerParty()
    {
        if (currentTrainer is null) return;
        const PartyPokemonRecord.GenderAbilityFlags genderAbility = PartyPokemonRecord.GenderAbilityFlags.ForceMale
            | PartyPokemonRecord.GenderAbilityFlags.ForceFemale | PartyPokemonRecord.GenderAbilityFlags.AbilitySlot1
            | PartyPokemonRecord.GenderAbilityFlags.AbilitySlot2;
        for (int i = 0; i < TrainerPartyRowItems.Count && i < TrainerRecord.PartySize; i++)
        {
            PartyMemberRow row = TrainerPartyRowItems[i];
            PartyPokemonRecord poke = currentTrainer.Party[i];
            PartyMemberRow shown = PartyRowFor(i, poke);
            if (row.Species != shown.Species) poke.Species = row.Species >= 0 ? row.Species : null;
            if (row.Level != shown.Level) poke.Level = row.Level;
            if (row.Difficulty != shown.Difficulty) poke.Difficulty = (byte)Math.Clamp(row.Difficulty, 0, 255);
            if (row.BallSeals != shown.BallSeals) poke.BallSeals = (ushort)Math.Clamp(row.BallSeals, 0, ushort.MaxValue);
            if (row.Form != shown.Form) poke.Form = row.Form;
            if (row.GenderIndex != shown.GenderIndex || row.AbilityIndex != shown.AbilityIndex)
            {
                PartyPokemonRecord.GenderAbilityFlags flags = poke.GenderAndAbility & ~genderAbility;
                if (row.GenderIndex == 1) flags |= PartyPokemonRecord.GenderAbilityFlags.ForceMale;
                else if (row.GenderIndex == 2) flags |= PartyPokemonRecord.GenderAbilityFlags.ForceFemale;
                if (row.AbilityIndex == 1) flags |= PartyPokemonRecord.GenderAbilityFlags.AbilitySlot1;
                else if (row.AbilityIndex == 2) flags |= PartyPokemonRecord.GenderAbilityFlags.AbilitySlot2;
                poke.GenderAndAbility = flags;
            }
            if (row.HeldItem != shown.HeldItem) poke.HeldItem = row.HeldItem >= 0 ? row.HeldItem : null;
            if (row.Move1 != shown.Move1 || row.Move2 != shown.Move2 || row.Move3 != shown.Move3 || row.Move4 != shown.Move4)
            {
                poke.Moves = [Math.Max(0, row.Move1), Math.Max(0, row.Move2), Math.Max(0, row.Move3), Math.Max(0, row.Move4)];
            }
        }
    }

    private void TrainerNameInput_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (loadingTrainer || currentTrainer is null) return;
        currentTrainer.Name = TrainerNameInput.Text ?? "";
        MarkTrainerModified();
    }

    private void TrainerClassSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingTrainer || currentTrainer is null || TrainerClassSelector.SelectedIndex < 0) return;
        currentTrainer.TrainerClass = (byte)TrainerClassSelector.SelectedIndex;
        if (currentProject is not null && currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata is not null)
        {
            LoadTrainerClassSprite(currentTrainer.TrainerClass, metadata);
        }
        MarkTrainerModified();
    }

    private void TrainerFlag_Changed(object? sender, RoutedEventArgs e)
    {
        if (loadingTrainer || currentTrainer is null) return;
        currentTrainer.DoubleBattle = TrainerDoubleBattleCheck.IsChecked == true;
        MarkTrainerModified();
    }

    private void TrainerChooseFlag_Changed(object? sender, RoutedEventArgs e)
    {
        if (loadingTrainer || currentTrainer is null) return;
        currentTrainer.ChooseMoves = TrainerChooseMovesCheck.IsChecked == true;
        currentTrainer.ChooseItems = TrainerChooseItemsCheck.IsChecked == true;
        MarkTrainerModified();
    }

    private void TrainerPartyCountInput_TextChanged(object? sender, TextChangedEventArgs e)
    {
        if (loadingTrainer || currentTrainer is null) return;
        // TextChanged arrives after LoadTrainer has finished, so the loaded value itself comes through
        // here too; clamping that (a party count of 0 to 1) used to count as an edit on opening the editor.
        if (!byte.TryParse(TrainerPartyCountInput.Text, out byte count) || count == currentTrainer.PartyCount) return;
        currentTrainer.PartyCount = Math.Clamp(count, (byte)1, (byte)TrainerRecord.PartySize);
        MarkTrainerModified();
    }

    private void TrainerItem_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingTrainer || currentTrainer is null) return;
        currentTrainer.TrainerItems[0] = Math.Max(0, TrainerItem1Selector.SelectedIndex);
        currentTrainer.TrainerItems[1] = Math.Max(0, TrainerItem2Selector.SelectedIndex);
        currentTrainer.TrainerItems[2] = Math.Max(0, TrainerItem3Selector.SelectedIndex);
        currentTrainer.TrainerItems[3] = Math.Max(0, TrainerItem4Selector.SelectedIndex);
        MarkTrainerModified();
    }

    private void TrainerAiFlag_Click(object? sender, RoutedEventArgs e)
    {
        if (loadingTrainer || currentTrainer is null) return;
        CheckBox[] aiChecks = GetTrainerAiCheckboxes();
        for (int i = 0; i < aiChecks.Length; i++)
        {
            currentTrainer.Ai[i] = aiChecks[i].IsChecked == true;
        }
        MarkTrainerModified();
    }

    private void TrainerPartyField_Changed(object? sender, SelectionChangedEventArgs e) => MarkTrainerModified();

    private void TrainerPartySpecies_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        MarkTrainerModified();
        if (sender is ComboBox { DataContext: PartyMemberRow row } && currentProject is not null
            && currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata is not null)
        {
            UpdatePartyRowIcon(row, metadata);
        }
    }

    private void TrainerPartyTextField_Changed(object? sender, TextChangedEventArgs e) => MarkTrainerModified();

    private void SaveTrainer_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentTrainer is null) return;
        if (!currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;

        SyncTrainerParty();
        TrainerService.Save(currentProject, currentTrainer, metadata);
        trainerEdits.Saved(currentTrainer);

        // Trainer names live in a text archive (wrapped as "{TRAINER_NAME:...}"), separate from the
        // trdata/trpoke NARCs the rest of the record is stored in.
        try
        {
            TextArchiveData namesArchive = TextArchiveService.Load(currentProject, TrainerService.GetTrainerNamesArchiveId(metadata), metadata);
            while (namesArchive.Messages.Count <= currentTrainer.Id)
            {
                namesArchive.Messages.Add(TrainerService.WrapTrainerName(""));
            }
            namesArchive.Messages[currentTrainer.Id] = TrainerService.WrapTrainerName(currentTrainer.Name);
            SaveTextArchiveKeepingScripts(namesArchive);
            trainerNamesCache[currentTrainer.Id] = currentTrainer.Name;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Trainer name could not be saved: {ex.Message}");
        }

        RefreshTrainerSelectorList();
        TrainerEditorStatus.Text = $"Saved trainer {currentTrainer.Id:D3} - {currentTrainer.Name}.";
    }

    #region Pokemon Editor

    private void ConfigurePokemonEditor()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            return;
        }

        try
        {
            // Re-extracting would overwrite edits saved since the last Save ROM.
            if (!Directory.Exists(currentProject.PersonalDataDirectory)) new RomExtractionService().PreparePokemonData(currentProject, metadata);

            string[] pokemonNames = TextArchiveService.Load(currentProject, TrainerService.GetPokemonNamesArchiveId(metadata), metadata).Messages.ToArray();
            pokeBaseSpeciesCount = pokemonNames.Length;
            pokeTypeNames = TextArchiveService.Load(currentProject, PersonalDataService.GetTypeNamesArchiveId(metadata), metadata).Messages.ToArray();
            pokeAbilityNames = TextArchiveService.Load(currentProject, TrainerService.GetAbilityNamesArchiveId(metadata), metadata).Messages.ToArray();
            pokeItemNames = TextArchiveService.Load(currentProject, TrainerService.GetItemNamesArchiveId(metadata), metadata).Messages.ToArray();
            pokeMoveNames = TextArchiveService.Load(currentProject, TrainerService.GetAttackNamesArchiveId(metadata), metadata).Messages.ToArray();

            int fileCount = PersonalDataService.GetFileCount(currentProject);
            string[] fileNames = PersonalDataService.BuildFileNames(pokemonNames, fileCount);

            PokeEditorSpeciesNames.Clear();
            foreach (string name in fileNames) PokeEditorSpeciesNames.Add(name);

            int[] machineMoves = PersonalDataService.ReadMachineMoves(currentProject, metadata);
            pokeMachineLabels = new string[PersonalDataService.MachineCount];
            for (int i = 0; i < PersonalDataService.MachineCount; i++)
            {
                string label = PersonalDataService.MachineLabelFromIndex(i);
                string moveName = i < machineMoves.Length && machineMoves[i] < pokeMoveNames.Length ? pokeMoveNames[machineMoves[i]] : $"UNK_{i}";
                pokeMachineLabels[i] = $"{label} - {moveName}";
            }

            SetItems(PokeType1Selector, pokeTypeNames);
            SetItems(PokeType2Selector, pokeTypeNames);
            SetItems(PokeAbility1Selector, pokeAbilityNames);
            SetItems(PokeAbility2Selector, pokeAbilityNames);
            SetItems(PokeItem1Selector, pokeItemNames);
            SetItems(PokeItem2Selector, pokeItemNames);
            SetItems(PokeGrowthCurveSelector, Enum.GetNames<PokemonGrowthCurve>());
            SetItems(PokeDexColorSelector, Enum.GetNames<PokemonDexColor>());
            SetItems(PokeEggGroup1Selector, Enum.GetNames<PokemonEggGroup>());
            SetItems(PokeEggGroup2Selector, Enum.GetNames<PokemonEggGroup>());
            SetItems(PokeHatchResultSelector, fileNames);
            SetItems(PokeLearnsetMoveSelector, pokeMoveNames);
            SetItems(PokeEditorSpeciesSelector, fileNames);
            ConfigurePokedexTab(metadata);

            EvolutionMethodNames.Clear();
            foreach (string name in Enum.GetNames<EvolutionMethod>()) EvolutionMethodNames.Add(name);

            if (fileNames.Length > 1)
            {
                LoadPokeEditorSpecies(pokeEditorSpeciesId >= 0 && pokeEditorSpeciesId < fileNames.Length ? pokeEditorSpeciesId : 1, metadata);
            }

            PokeEditorStatus.Text = $"{fileCount} personal data entries loaded.";
        }
        catch (Exception ex)
        {
            LogUiError("Pokemon data could not be loaded", ex);
            PokeEditorStatus.Text = ex.Message;
        }
    }

    private static void SetItems(ItemsControl control, IEnumerable<string> items)
    {
        control.Items.Clear();
        foreach (string item in items) control.Items.Add(item);
    }

    private async void PokeEditorSpeciesSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingPokeEditor || switchingDocument || currentProject is null || PokeEditorSpeciesSelector.SelectedIndex < 0) return;
        await OpenPokemonSpeciesAsync(PokeEditorSpeciesSelector.SelectedIndex);
    }

    /// <summary>Opens species <paramref name="id"/>, once the open species' unsaved changes (personal data,
    /// learnset, evolutions, Pokédex text, sprites) are saved or discarded. False when the user chose to stay.</summary>
    private async Task<bool> OpenPokemonSpeciesAsync(int id)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return false;
        int open = pokeEditorSpeciesId;
        if (!await ShouldLoadDocumentAsync(id == open, "Pokemon Editor", () => PokeEditorSpeciesSelector.SelectedIndex = open)) return id == open;
        LoadPokeEditorSpecies(id, metadata);
        ShowEggMovesForSelectedSpecies();
        return true;
    }

    private void LoadPokeEditorSpecies(int id, RomGameMetadata metadata)
    {
        if (currentProject is null) return;

        loadingPokeEditor = true;
        try
        {
            pokeEditorSpeciesId = id;
            if (PokeEditorSpeciesSelector.SelectedIndex != id) PokeEditorSpeciesSelector.SelectedIndex = id;

            int iconSpeciesId = PersonalDataService.ResolveIconSpeciesId(id, pokeBaseSpeciesCount);
            PokeEditorSpeciesIcon.Source = CreateBitmapFromRgba(SpriteService.GetSpeciesIconRgba(currentProject, metadata, iconSpeciesId, 32, 24), 32, 24);

            LoadPersonalDataTab(id);

            try
            {
                currentLearnset = LearnsetService.Load(currentProject, id);
                learnsetEdits.Loaded(currentLearnset, $"Learnset {id:D3}");
            }
            catch (Exception ex)
            {
                currentLearnset = null;
                LogUiError("Learnset could not be loaded", ex);
            }
            RefreshLearnsetListBox();

            LoadEvolutionsTab(id);
            LoadPokeSpriteState(id, metadata);
            LoadPokedexText(id, metadata);
            ShowFootprintForSpecies(id);
            ShowShadowForSpecies(id);
            ShowAreasForSpecies(id);

            string name = id < PokeEditorSpeciesNames.Count ? PokeEditorSpeciesNames[id] : $"Entry {id}";
            PokeEditorStatus.Text = $"Loaded {id:D3} - {name}.";
        }
        finally
        {
            loadingPokeEditor = false;
        }
    }

    private void LoadPersonalDataTab(int id)
    {
        if (currentProject is null) return;

        try
        {
            currentPersonalData = PersonalDataService.Load(currentProject, id);
            personalDataEdits.Loaded(currentPersonalData, $"Personal data {id:D3}");
        }
        catch (Exception ex)
        {
            currentPersonalData = null;
            LogUiError("Personal data could not be loaded", ex);
            return;
        }

        PersonalDataRecord d = currentPersonalData;
        PokeBaseHpInput.Value = d.BaseHP;
        PokeBaseAtkInput.Value = d.BaseAtk;
        PokeBaseDefInput.Value = d.BaseDef;
        PokeBaseSpAtkInput.Value = d.BaseSpAtk;
        PokeBaseSpDefInput.Value = d.BaseSpDef;
        PokeBaseSpeedInput.Value = d.BaseSpeed;
        PokeEvHpInput.Value = d.EvHP;
        PokeEvAtkInput.Value = d.EvAtk;
        PokeEvDefInput.Value = d.EvDef;
        PokeEvSpAtkInput.Value = d.EvSpAtk;
        PokeEvSpDefInput.Value = d.EvSpDef;
        PokeEvSpeedInput.Value = d.EvSpeed;
        PokeType1Selector.SelectedIndex = (int)d.Type1;
        PokeType2Selector.SelectedIndex = (int)d.Type2;
        PokeAbility1Selector.SelectedIndex = Math.Min(d.FirstAbility, PokeAbility1Selector.Items.Count - 1);
        PokeAbility2Selector.SelectedIndex = Math.Min(d.SecondAbility, PokeAbility2Selector.Items.Count - 1);
        PokeItem1Selector.SelectedIndex = Math.Min(d.Item1, PokeItem1Selector.Items.Count - 1);
        PokeItem2Selector.SelectedIndex = Math.Min(d.Item2, PokeItem2Selector.Items.Count - 1);
        PokeEggGroup1Selector.SelectedIndex = d.EggGroup1;
        PokeEggGroup2Selector.SelectedIndex = d.EggGroup2;
        PokeGrowthCurveSelector.SelectedIndex = (int)d.GrowthCurve;
        PokeDexColorSelector.SelectedIndex = (int)d.Color;
        PokeFlipCheck.IsChecked = d.Flip;
        PokeCatchRateInput.Value = d.CatchRate;
        PokeGivenExpInput.Value = d.GivenExp;
        PokeEggStepsInput.Value = d.EggSteps;
        PokeBaseFriendshipInput.Value = d.BaseFriendship;
        PokeEscapeRateInput.Value = d.EscapeRate;
        PokeGenderRatioInput.Value = d.GenderRatio;
        PokeGenderLabel.Text = PersonalDataService.GetGenderText(d.GenderRatio);
        int hatchResult = PersonalDataService.GetHatchResult(currentProject, id);
        PokeHatchResultSelector.SelectedIndex = Math.Clamp(hatchResult, 0, Math.Max(0, PokeHatchResultSelector.Items.Count - 1));
        pokeSavedHatchResult = PokeHatchResultSelector.SelectedIndex;

        RebuildMachineLists();
    }

    private void PersonalDataField_Changed(object? sender, RoutedEventArgs e) => ApplyPersonalDataFields();
    private void PersonalDataField_Changed(object? sender, SelectionChangedEventArgs e) => ApplyPersonalDataFields();
    private void PersonalDataField_Changed(object? sender, NumericUpDownValueChangedEventArgs e) => ApplyPersonalDataFields();

    private void ApplyPersonalDataFields()
    {
        if (loadingPokeEditor || currentPersonalData is null) return;

        PersonalDataRecord d = currentPersonalData;
        d.BaseHP = (byte)(PokeBaseHpInput.Value ?? 0);
        d.BaseAtk = (byte)(PokeBaseAtkInput.Value ?? 0);
        d.BaseDef = (byte)(PokeBaseDefInput.Value ?? 0);
        d.BaseSpAtk = (byte)(PokeBaseSpAtkInput.Value ?? 0);
        d.BaseSpDef = (byte)(PokeBaseSpDefInput.Value ?? 0);
        d.BaseSpeed = (byte)(PokeBaseSpeedInput.Value ?? 0);
        d.EvHP = (byte)(PokeEvHpInput.Value ?? 0);
        d.EvAtk = (byte)(PokeEvAtkInput.Value ?? 0);
        d.EvDef = (byte)(PokeEvDefInput.Value ?? 0);
        d.EvSpAtk = (byte)(PokeEvSpAtkInput.Value ?? 0);
        d.EvSpDef = (byte)(PokeEvSpDefInput.Value ?? 0);
        d.EvSpeed = (byte)(PokeEvSpeedInput.Value ?? 0);
        d.Type1 = (PokemonType)Math.Max(0, PokeType1Selector.SelectedIndex);
        d.Type2 = (PokemonType)Math.Max(0, PokeType2Selector.SelectedIndex);
        d.FirstAbility = (byte)Math.Max(0, PokeAbility1Selector.SelectedIndex);
        d.SecondAbility = (byte)Math.Max(0, PokeAbility2Selector.SelectedIndex);
        d.Item1 = (ushort)Math.Max(0, PokeItem1Selector.SelectedIndex);
        d.Item2 = (ushort)Math.Max(0, PokeItem2Selector.SelectedIndex);
        d.EggGroup1 = (byte)Math.Max(0, PokeEggGroup1Selector.SelectedIndex);
        d.EggGroup2 = (byte)Math.Max(0, PokeEggGroup2Selector.SelectedIndex);
        d.GrowthCurve = (PokemonGrowthCurve)Math.Max(0, PokeGrowthCurveSelector.SelectedIndex);
        d.Color = (PokemonDexColor)Math.Max(0, PokeDexColorSelector.SelectedIndex);
        d.Flip = PokeFlipCheck.IsChecked == true;
        d.CatchRate = (byte)(PokeCatchRateInput.Value ?? 0);
        d.GivenExp = (byte)(PokeGivenExpInput.Value ?? 0);
        d.EggSteps = (byte)(PokeEggStepsInput.Value ?? 0);
        d.BaseFriendship = (byte)(PokeBaseFriendshipInput.Value ?? 0);
        d.EscapeRate = (byte)(PokeEscapeRateInput.Value ?? 0);
        d.GenderRatio = (byte)(PokeGenderRatioInput.Value ?? 0);
        PokeGenderLabel.Text = PersonalDataService.GetGenderText(d.GenderRatio);

        ReportEdit(personalDataEdits, () => currentPersonalData, PokeEditorStatus);
    }

    private void RebuildMachineLists()
    {
        if (currentPersonalData is null) return;

        PokeAddableMachinesList.Items.Clear();
        PokeAddedMachinesList.Items.Clear();
        for (int i = 0; i < PersonalDataService.MachineCount; i++)
        {
            string label = i < pokeMachineLabels.Length ? pokeMachineLabels[i] : $"Machine {i}";
            if (currentPersonalData.Machines.Contains((byte)i))
            {
                PokeAddedMachinesList.Items.Add(label);
            }
            else
            {
                PokeAddableMachinesList.Items.Add(label);
            }
        }
    }

    private void AddMachine_Click(object? sender, RoutedEventArgs e)
    {
        if (currentPersonalData is null || PokeAddableMachinesList.SelectedItem is not string label) return;
        int index = Array.IndexOf(pokeMachineLabels, label);
        if (index < 0) return;
        currentPersonalData.Machines.Add((byte)index);
        RebuildMachineLists();
        ReportEdit(personalDataEdits, () => currentPersonalData, PokeEditorStatus);
    }

    private void RemoveMachine_Click(object? sender, RoutedEventArgs e)
    {
        if (currentPersonalData is null || PokeAddedMachinesList.SelectedItem is not string label) return;
        int index = Array.IndexOf(pokeMachineLabels, label);
        if (index < 0) return;
        currentPersonalData.Machines.Remove((byte)index);
        RebuildMachineLists();
        ReportEdit(personalDataEdits, () => currentPersonalData, PokeEditorStatus);
    }

    private void AddAllMachines_Click(object? sender, RoutedEventArgs e)
    {
        if (currentPersonalData is null) return;
        for (byte i = 0; i < PersonalDataService.MachineCount; i++) currentPersonalData.Machines.Add(i);
        RebuildMachineLists();
        ReportEdit(personalDataEdits, () => currentPersonalData, PokeEditorStatus);
    }

    private void RemoveAllMachines_Click(object? sender, RoutedEventArgs e)
    {
        if (currentPersonalData is null) return;
        currentPersonalData.Machines.Clear();
        RebuildMachineLists();
        ReportEdit(personalDataEdits, () => currentPersonalData, PokeEditorStatus);
    }

    private void SavePersonalData_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentPersonalData is null) return;
        try
        {
            PersonalDataService.Save(currentProject, pokeEditorSpeciesId, currentPersonalData);
            personalDataEdits.Saved(currentPersonalData);
            PersonalDataService.SetHatchResult(currentProject, pokeEditorSpeciesId, Math.Max(0, PokeHatchResultSelector.SelectedIndex));
            pokeSavedHatchResult = PokeHatchResultSelector.SelectedIndex;
            PokeEditorStatus.Text = $"Saved personal data for {pokeEditorSpeciesId:D3}.";
        }
        catch (Exception ex)
        {
            LogUiError("Personal data could not be saved", ex);
            PokeEditorStatus.Text = ex.Message;
        }
    }

    private async void ExportPersonalDataCsv_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = "PersonalData.csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
        });
        if (file is null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream);
            writer.WriteLine(PersonalDataCsvService.Header);

            int count = PersonalDataService.GetFileCount(currentProject);
            for (int i = 0; i < count; i++)
            {
                PersonalDataRecord d = PersonalDataService.Load(currentProject, i);
                string name = i < PokeEditorSpeciesNames.Count ? PokeEditorSpeciesNames[i] : $"Pokemon_{i}";
                writer.WriteLine(PersonalDataCsvService.ExportRow(i, name, d, pokeTypeNames, pokeAbilityNames, pokeItemNames));
            }

            PokeEditorStatus.Text = $"Exported {count} personal data entries to {file.Name}.";
        }
        catch (Exception ex)
        {
            LogUiError("Personal data could not be exported", ex);
            PokeEditorStatus.Text = ex.Message;
        }
    }

    private async void ImportPersonalDataCsv_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null) return;
        // The open species is reloaded from the imported data afterwards.
        if (!await ConfirmLeavingDocumentAsync("Pokemon Editor")) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
        });
        if (files.Count != 1) return;

        try
        {
            string[] lines = File.ReadAllLines(files[0].Path.LocalPath);
            int maxId = PersonalDataService.GetFileCount(currentProject) - 1;
            PersonalDataCsvImportResult result = PersonalDataCsvService.Parse(lines, pokeTypeNames, pokeAbilityNames, pokeItemNames, maxId);
            List<PersonalDataCsvChange> changes = PersonalDataCsvService.ComputeChanges(currentProject, result.ValidEntries, pokeTypeNames, pokeAbilityNames);

            var preview = new PersonalDataImportPreviewWindow(result, changes);
            await preview.ShowDialog(this);
            if (!preview.Confirmed)
            {
                PokeEditorStatus.Text = "Personal data import cancelled.";
                return;
            }

            foreach (PersonalDataCsvEntry entry in result.ValidEntries)
            {
                PersonalDataCsvService.Apply(currentProject, entry);
            }

            if (pokeEditorSpeciesId >= 0 && currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata is not null)
            {
                LoadPokeEditorSpecies(pokeEditorSpeciesId, metadata);
            }

            PokeEditorStatus.Text = result.Errors.Count == 0
                ? $"Imported {result.ValidEntries.Count} personal data entries."
                : $"Imported {result.ValidEntries.Count} entries, {result.Errors.Count} row(s) skipped.";
        }
        catch (Exception ex)
        {
            LogUiError("Personal data could not be imported", ex);
            PokeEditorStatus.Text = ex.Message;
        }
    }

    private void RefreshLearnsetListBox()
    {
        PokeLearnsetList.Items.Clear();
        if (currentLearnset is null)
        {
            PokeLearnsetEntryCount.Text = string.Empty;
            return;
        }

        foreach (LearnsetEntry entry in currentLearnset.Entries)
        {
            string moveName = entry.Move < pokeMoveNames.Length ? pokeMoveNames[entry.Move] : $"Move {entry.Move}";
            PokeLearnsetList.Items.Add($"Lv. {entry.Level}: {moveName}");
        }

        PokeLearnsetEntryCount.Text = $"Entry Count: {currentLearnset.Entries.Count}" +
            (currentLearnset.Entries.Count > LearnsetService.VanillaLimit ? " !" : string.Empty);
    }

    private void PokeLearnsetList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingPokeEditor || currentLearnset is null) return;
        int index = PokeLearnsetList.SelectedIndex;
        if (index < 0 || index >= currentLearnset.Entries.Count) return;

        loadingPokeEditor = true;
        try
        {
            LearnsetEntry entry = currentLearnset.Entries[index];
            PokeLearnsetMoveSelector.SelectedIndex = Math.Min(entry.Move, PokeLearnsetMoveSelector.Items.Count - 1);
            PokeLearnsetLevelInput.Value = entry.Level;
            PokeLearnsetEditStatus.Text = string.Empty;
        }
        finally
        {
            loadingPokeEditor = false;
        }
    }

    private void PokeLearnsetEditField_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (!loadingPokeEditor) PokeLearnsetEditStatus.Text = string.Empty;
    }

    private void PokeLearnsetEditField_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (!loadingPokeEditor) PokeLearnsetEditStatus.Text = string.Empty;
    }

    private bool TryGetLearnsetEditValues(out byte level, out ushort move)
    {
        level = (byte)Math.Clamp((int)(PokeLearnsetLevelInput.Value ?? 0), 0, 255);
        move = (ushort)Math.Max(0, PokeLearnsetMoveSelector.SelectedIndex);
        return level is > 0 and <= 100 && PokeLearnsetMoveSelector.SelectedIndex > 0;
    }

    private void AddLearnsetEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (currentLearnset is null) return;
        if (!TryGetLearnsetEditValues(out byte level, out ushort move))
        {
            PokeLearnsetEditStatus.Text = "Invalid Move or Level!";
            return;
        }

        if (!LearnsetService.InsertSorted(currentLearnset.Entries, new LearnsetEntry(level, move)))
        {
            PokeLearnsetEditStatus.Text = "Entry already exists!";
            return;
        }

        PokeLearnsetEditStatus.Text = string.Empty;
        RefreshLearnsetListBox();
        ReportEdit(learnsetEdits, () => currentLearnset, PokeEditorStatus);
    }

    private void EditLearnsetEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (currentLearnset is null) return;
        int selected = PokeLearnsetList.SelectedIndex;
        if (selected < 0 || selected >= currentLearnset.Entries.Count) return;
        if (!TryGetLearnsetEditValues(out byte level, out ushort move))
        {
            PokeLearnsetEditStatus.Text = "Invalid Move or Level!";
            return;
        }

        LearnsetEntry original = currentLearnset.Entries[selected];
        currentLearnset.Entries.RemoveAt(selected);
        if (!LearnsetService.InsertSorted(currentLearnset.Entries, new LearnsetEntry(level, move)))
        {
            LearnsetService.InsertSorted(currentLearnset.Entries, original);
            PokeLearnsetEditStatus.Text = "Entry already exists!";
            RefreshLearnsetListBox();
            return;
        }

        PokeLearnsetEditStatus.Text = string.Empty;
        RefreshLearnsetListBox();
        ReportEdit(learnsetEdits, () => currentLearnset, PokeEditorStatus);
    }

    private void DeleteLearnsetEntry_Click(object? sender, RoutedEventArgs e)
    {
        if (currentLearnset is null) return;
        int selected = PokeLearnsetList.SelectedIndex;
        if (selected < 0 || selected >= currentLearnset.Entries.Count) return;

        currentLearnset.Entries.RemoveAt(selected);
        RefreshLearnsetListBox();
        ReportEdit(learnsetEdits, () => currentLearnset, PokeEditorStatus);
    }

    private void MoveLearnsetEntryUp_Click(object? sender, RoutedEventArgs e) => SwapLearnsetEntries(-1);
    private void MoveLearnsetEntryDown_Click(object? sender, RoutedEventArgs e) => SwapLearnsetEntries(1);

    /// <summary>Reordering is only allowed between adjacent entries that share the same level, matching
    /// the legacy editor's tie-breaking-only reorder buttons.</summary>
    private void SwapLearnsetEntries(int direction)
    {
        if (currentLearnset is null) return;
        int index = PokeLearnsetList.SelectedIndex;
        int other = index + direction;
        if (index < 0 || other < 0 || other >= currentLearnset.Entries.Count) return;
        if (currentLearnset.Entries[index].Level != currentLearnset.Entries[other].Level) return;

        (currentLearnset.Entries[index], currentLearnset.Entries[other]) = (currentLearnset.Entries[other], currentLearnset.Entries[index]);
        RefreshLearnsetListBox();
        PokeLearnsetList.SelectedIndex = other;
        ReportEdit(learnsetEdits, () => currentLearnset, PokeEditorStatus);
    }

    private void SaveLearnset_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentLearnset is null) return;
        try
        {
            LearnsetService.Save(currentProject, currentLearnset);
            learnsetEdits.Saved(currentLearnset);
            PokeEditorStatus.Text = $"Saved learnset for {pokeEditorSpeciesId:D3}.";
        }
        catch (Exception ex)
        {
            LogUiError("Learnset could not be saved", ex);
            PokeEditorStatus.Text = ex.Message;
        }
    }

    private void LoadEvolutionsTab(int id)
    {
        if (currentProject is null) return;

        try
        {
            currentEvolutionFile = EvolutionService.Load(currentProject, id);
            evolutionEdits.Loaded(currentEvolutionFile, $"Evolutions {id:D3}");
        }
        catch (Exception ex)
        {
            currentEvolutionFile = null;
            LogUiError("Evolution data could not be loaded", ex);
        }

        EvolutionSlotRows.Clear();
        if (currentEvolutionFile is null) return;

        string[] speciesNames = PokeEditorSpeciesNames.ToArray();
        foreach (EvolutionSlot slot in currentEvolutionFile.Slots)
        {
            EvolutionSlotRows.Add(new EvolutionSlotRow(slot, pokeItemNames, pokeMoveNames, speciesNames));
        }
    }

    private void EvolutionSlotMethod_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingPokeEditor || sender is not ComboBox { DataContext: EvolutionSlotRow row }) return;

        // ResetGUIRow (None) zeroes Param; a valid method whose meaning is "Ignored" (Friendship/Trade/
        // the HGSS Loc_* evolutions) also forces Param back to 0, matching the legacy editor exactly -
        // only the "meaningful" parameter types (level/item/move/pokemon/beauty) keep whatever was there.
        if (!row.ParamEnabled)
        {
            row.Param = 0;
        }
        else if (row.Param > row.ParamMaximum)
        {
            row.Param = row.ParamMaximum;
        }

        row.RaiseAllChanged();
        ReportEdit(evolutionEdits, () => currentEvolutionFile, PokeEditorStatus);
    }

    private void EvolutionSlotParam_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (loadingPokeEditor || sender is not NumericUpDown { DataContext: EvolutionSlotRow row }) return;
        row.RaiseAllChanged();
        ReportEdit(evolutionEdits, () => currentEvolutionFile, PokeEditorStatus);
    }

    private void SaveEvolutions_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentEvolutionFile is null) return;
        try
        {
            EvolutionService.Save(currentProject, currentEvolutionFile);
            evolutionEdits.Saved(currentEvolutionFile);
            PokeEditorStatus.Text = $"Saved evolution data for {pokeEditorSpeciesId:D3}.";
        }
        catch (Exception ex)
        {
            LogUiError("Evolution data could not be saved", ex);
            PokeEditorStatus.Text = ex.Message;
        }
    }

    private void PokeSpriteShinyCheck_Changed(object? sender, RoutedEventArgs e) => RedrawPokeSprites();

    /// <summary>Battle sprites (front/back x male/female) only exist at this simple species*6 offset
    /// scheme for base species - alt forms (Deoxys/Wormadam/etc.) live in a separate NARC keyed by a
    /// hand-authored per-species index table that this port doesn't include yet (see PokeSpriteEditor
    /// scoping notes), so the preview is intentionally left blank for those entries rather than guessing.
    /// Decodes once into pokeSpriteIndices/pokeSprite{Normal,Shiny}Palette so Import/Export/Save can work
    /// against in-memory state without re-reading files on every shiny-checkbox toggle.</summary>
    private void LoadPokeSpriteState(int id, RomGameMetadata metadata)
    {
        pokeSpriteIsDiamondPearl = metadata.HeaderFormat == MapHeaderFormat.DiamondPearl;
        for (int i = 0; i < pokeSpriteIndices.Length; i++) pokeSpriteIndices[i] = null;
        pokeSpritesUnsaved = false;
        Array.Clear(pokeSpriteSources);
        pokeSpriteNormalPalette = null;
        pokeSpriteShinyPalette = null;

        if (currentProject is not null && id < pokeBaseSpeciesCount)
        {
            int baseOffset = id * 6;
            for (int i = 0; i < 4; i++)
            {
                string path = Path.Combine(currentProject.PokemonBattleSpritesDirectory, (baseOffset + i).ToString("D4"));
                if (!File.Exists(path)) continue;
                byte[] data = File.ReadAllBytes(path);
                pokeSpriteIndices[i] = pokeSpriteIsDiamondPearl
                    ? BattleSpriteService.DecodeSpriteIndicesDiamondPearl(data)
                    : BattleSpriteService.DecodeSpriteIndices(data);
            }

            string normalPalettePath = Path.Combine(currentProject.PokemonBattleSpritesDirectory, (baseOffset + 4).ToString("D4"));
            if (File.Exists(normalPalettePath)) pokeSpriteNormalPalette = BattleSpriteService.DecodePalette(File.ReadAllBytes(normalPalettePath));

            string shinyPalettePath = Path.Combine(currentProject.PokemonBattleSpritesDirectory, (baseOffset + 5).ToString("D4"));
            if (File.Exists(shinyPalettePath)) pokeSpriteShinyPalette = BattleSpriteService.DecodePalette(File.ReadAllBytes(shinyPalettePath));
        }

        RedrawPokeSprites();
    }

    private void RedrawPokeSprites()
    {
        bool shiny = PokeSpriteShinyCheck.IsChecked == true;
        RgbColor[]? palette = shiny ? pokeSpriteShinyPalette : pokeSpriteNormalPalette;
        PokeSpriteFemaleBack.Source = ComposePokeSpriteBitmap(pokeSpriteIndices[(int)BattleSpriteService.BattleSpriteSlot.FemaleBack], palette);
        PokeSpriteMaleBack.Source = ComposePokeSpriteBitmap(pokeSpriteIndices[(int)BattleSpriteService.BattleSpriteSlot.MaleBack], palette);
        PokeSpriteFemaleFront.Source = ComposePokeSpriteBitmap(pokeSpriteIndices[(int)BattleSpriteService.BattleSpriteSlot.FemaleFront], palette);
        PokeSpriteMaleFront.Source = ComposePokeSpriteBitmap(pokeSpriteIndices[(int)BattleSpriteService.BattleSpriteSlot.MaleFront], palette);
        PokeSpriteStatus.Text = pokeEditorSpeciesId >= 0 && pokeEditorSpeciesId >= pokeBaseSpeciesCount
            ? "Battle sprites for alternate forms aren't supported by this editor yet."
            : string.Empty;
    }

    private static WriteableBitmap? ComposePokeSpriteBitmap(byte[]? indices, RgbColor[]? palette)
    {
        if (indices is null || palette is null) return null;
        return CreateBitmapFromRgba(BattleSpriteService.ComposeRgba(indices, palette), BattleSpriteService.SpriteWidth, BattleSpriteService.SpriteHeight);
    }

    private static BattleSpriteService.BattleSpriteSlot ParseSpriteSlotTag(string tag) => tag switch
    {
        "FemaleBack" => BattleSpriteService.BattleSpriteSlot.FemaleBack,
        "MaleBack" => BattleSpriteService.BattleSpriteSlot.MaleBack,
        "FemaleFront" => BattleSpriteService.BattleSpriteSlot.FemaleFront,
        _ => BattleSpriteService.BattleSpriteSlot.MaleFront,
    };

    /// <summary>Decodes an arbitrary user-supplied image file to RGBA bytes using Avalonia's own
    /// Skia-backed Bitmap loader (PNG/BMP/GIF/JPEG) - no extra image-codec dependency needed. CopyPixels
    /// returns pixels in the bitmap's own reported Format, which is commonly Bgra8888 on this backend, so
    /// the B/R channels are swapped back into RGBA order when that's what was reported.</summary>
    private async void ImportPokeSprite_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || pokeEditorSpeciesId < 0 || pokeEditorSpeciesId >= pokeBaseSpeciesCount) return;
        if ((sender as Button)?.Tag is not string tag) return;
        BattleSpriteService.BattleSpriteSlot slot = ParseSpriteSlotTag(tag);

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Images") { Patterns = ["*.png", "*.bmp", "*.gif"] }]
        });
        if (files.Count != 1) return;

        try
        {
            bool shiny = PokeSpriteShinyCheck.IsChecked == true;
            (byte[] indices, RgbColor[] palette) = BattleSpriteService.ImportSpriteImage(
                files[0].Path.LocalPath,
                shiny ? pokeSpriteShinyPalette : pokeSpriteNormalPalette);
            if (shiny) pokeSpriteShinyPalette = palette; else pokeSpriteNormalPalette = palette;

            pokeSpriteIndices[(int)slot] = indices;
            pokeSpriteSources[(int)slot] = (files[0].Path.LocalPath, shiny);
            pokeSpritesUnsaved = true;
            RedrawPokeSprites();
            PokeSpriteStatus.Text = $"Imported {tag} sprite. Click Save Sprite Changes to keep it.";
        }
        catch (Exception ex)
        {
            LogUiError("Sprite could not be imported", ex);
            PokeSpriteStatus.Text = ex.Message;
        }
    }

    private async void ExportPokeSprite_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.Tag is not string tag) return;
        BattleSpriteService.BattleSpriteSlot slot = ParseSpriteSlotTag(tag);
        bool shiny = PokeSpriteShinyCheck.IsChecked == true;
        byte[]? indices = pokeSpriteIndices[(int)slot];
        RgbColor[]? palette = shiny ? pokeSpriteShinyPalette : pokeSpriteNormalPalette;
        if (indices is null || palette is null)
        {
            PokeSpriteStatus.Text = "No sprite loaded to export.";
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"{pokeEditorSpeciesId:D3}_{tag}{(shiny ? "_shiny" : string.Empty)}.png",
            FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"] }]
        });
        if (file is null) return;

        WriteableBitmap? bitmap = CreateBitmapFromRgba(BattleSpriteService.ComposeRgba(indices, palette), BattleSpriteService.SpriteWidth, BattleSpriteService.SpriteHeight);
        if (bitmap is null) return;

        bitmap.Save(file.Path.LocalPath);
        PokeSpriteStatus.Text = $"Exported {tag} sprite to {file.Name}.";
    }

    private void SavePokeSprites_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || pokeEditorSpeciesId < 0 || pokeEditorSpeciesId >= pokeBaseSpeciesCount) return;
        try
        {
            // Palettes first: a journaled sprite import replays by matching the image against the
            // palette already on disk.
            if (pokeSpriteNormalPalette is not null) BattleSpriteService.SaveBattlePalette(currentProject, pokeEditorSpeciesId, shiny: false, pokeSpriteNormalPalette);
            if (pokeSpriteShinyPalette is not null) BattleSpriteService.SaveBattlePalette(currentProject, pokeEditorSpeciesId, shiny: true, pokeSpriteShinyPalette);

            for (int i = 0; i < pokeSpriteIndices.Length; i++)
            {
                byte[]? indices = pokeSpriteIndices[i];
                if (indices is not null)
                {
                    (string Path, bool Shiny)? source = pokeSpriteSources[i];
                    BattleSpriteService.SaveBattleSprite(currentProject, pokeEditorSpeciesId, (BattleSpriteService.BattleSpriteSlot)i, indices, pokeSpriteIsDiamondPearl,
                        source?.Path, source?.Shiny ?? false);
                }
            }

            pokeSpritesUnsaved = false;
            PokeSpriteStatus.Text = $"Saved sprite changes for {pokeEditorSpeciesId:D3}.";
        }
        catch (Exception ex)
        {
            LogUiError("Sprite changes could not be saved", ex);
            PokeSpriteStatus.Text = ex.Message;
        }
    }

    #endregion

    #region Move Editor

    private void ConfigureMoveEditor()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            return;
        }

        try
        {
            // Re-extracting would overwrite edits saved since the last Save ROM.
            if (!Directory.Exists(currentProject.MoveDataDirectory)) new RomExtractionService().PrepareMoveData(currentProject, metadata);

            moveEditorTypeNames = TextArchiveService.Load(currentProject, PersonalDataService.GetTypeNamesArchiveId(metadata), metadata).Messages.ToArray();
            moveNamesArchive = GetOpenTextArchive(TrainerService.GetAttackNamesArchiveId(metadata), metadata);
            moveDescriptionsArchive = GetOpenTextArchive(MoveDataService.GetMoveDescriptionsArchiveId(metadata), metadata);
            moveEditorMoveNames = moveNamesArchive.Messages.ToArray();
            moveEditorDescriptions = moveDescriptionsArchive.Messages.ToArray();
            savedMoveNames = TextArchiveService.Load(currentProject, moveNamesArchive.Id, metadata).Messages.ToList();
            savedMoveDescriptions = TextArchiveService.Load(currentProject, moveDescriptionsArchive.Id, metadata).Messages.ToList();

            SetItems(MoveTypeSelector, moveEditorTypeNames);
            SetItems(MoveSplitSelector, Enum.GetNames<MoveSplit>());
            SetItems(MoveContestConditionSelector, Enum.GetNames<ContestCondition>());
            SetItems(MoveRangeSelector, MoveDataService.AttackRangeDescriptions.Select(r => $"{r.Name}: {r.Description}"));

            int fileCount = MoveDataService.GetFileCount(currentProject);
            SetItems(MoveBattleEffectSelector, Enumerable.Range(0, fileCount == 0 ? MoveEffectDescriptions.Descriptions.Length : fileCount)
                .Select(i => i < MoveEffectDescriptions.Descriptions.Length ? $"{i:D3} - {MoveEffectDescriptions.Descriptions[i]}" : $"{i:D3} - Undocumented"));

            SetItems(MoveSelector, moveEditorMoveNames);

            if (moveEditorMoveNames.Length > 1)
            {
                LoadMove(currentMoveId >= 0 && currentMoveId < moveEditorMoveNames.Length ? currentMoveId : 1);
            }

            MoveEditorStatus.Text = $"{fileCount} move data entries loaded.";
        }
        catch (Exception ex)
        {
            LogUiError("Move data could not be loaded", ex);
            MoveEditorStatus.Text = ex.Message;
        }
    }

    private async void MoveSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingMoveEditor || switchingDocument || currentProject is null || MoveSelector.SelectedIndex < 0) return;
        int id = MoveSelector.SelectedIndex;
        int open = currentMoveId;
        if (!await ShouldLoadDocumentAsync(id == open, "Move Editor", () => MoveSelector.SelectedIndex = open)) return;
        LoadMove(id);
    }

    private void LoadMove(int id)
    {
        if (currentProject is null) return;

        loadingMoveEditor = true;
        try
        {
            currentMoveId = id;
            if (MoveSelector.SelectedIndex != id) MoveSelector.SelectedIndex = id;

            try
            {
                currentMoveData = MoveDataService.Load(currentProject, id);
                moveEdits.Loaded(currentMoveData, $"Move {id:D3}");
            }
            catch (Exception ex)
            {
                currentMoveData = null;
                LogUiError("Move data could not be loaded", ex);
                return;
            }

            MoveRecord m = currentMoveData;
            MoveTypeSelector.SelectedIndex = (int)m.MoveType;
            MoveSplitSelector.SelectedIndex = (int)m.Split;
            MovePowerInput.Value = m.Damage;
            MoveAccuracyInput.Value = m.Accuracy;
            MovePpInput.Value = m.Pp;

            int rangeIndex = 0;
            for (int i = 0; i < MoveDataService.AttackRangeDescriptions.Length; i++)
            {
                if (MoveDataService.AttackRangeDescriptions[i].Value == m.Target) { rangeIndex = i; break; }
            }
            MoveRangeSelector.SelectedIndex = rangeIndex;

            MovePriorityInput.Value = m.Priority;
            UpdateMovePriorityIndicator(m.Priority);
            MoveSideEffectInput.Value = m.SideEffectProbability;

            MoveContestConditionSelector.SelectedIndex = (int)m.ContestConditionType;
            MoveContestAppealInput.Value = m.ContestAppeal;

            MoveBattleEffectSelector.SelectedIndex = Math.Min(m.BattleEffect, Math.Max(0, MoveBattleEffectSelector.Items.Count - 1));

            foreach (CheckBox checkBox in MoveFlagsPanel.Children.OfType<CheckBox>())
            {
                int bit = MoveFlagBitFromLabel(checkBox.Content as string);
                checkBox.IsChecked = bit >= 0 && (m.FlagField & (1 << bit)) != 0;
            }

            MoveNameInput.Text = id < moveEditorMoveNames.Length ? moveEditorMoveNames[id] : string.Empty;
            MoveDescriptionText.Text = DescriptionForEditing(id < moveEditorDescriptions.Length ? moveEditorDescriptions[id] : string.Empty);

            string name = id < moveEditorMoveNames.Length ? moveEditorMoveNames[id] : $"Move {id}";
            MoveEditorStatus.Text = $"Loaded {id:D3} - {name}.";
            RenderMovePreview();
        }
        finally
        {
            loadingMoveEditor = false;
        }
    }

    private static int MoveFlagBitFromLabel(string? label) => label switch
    {
        "Contact" => 0,
        "Protect" => 1,
        "MagicCoat" => 2,
        "Snatch" => 3,
        "MirrorMove" => 4,
        "KingsRock" => 5,
        "KeepHpBar" => 6,
        "DelShadow" => 7,
        _ => -1
    };

    private void UpdateMovePriorityIndicator(int priority)
    {
        MovePriorityIndicator.Text = priority switch
        {
            0 => string.Empty,
            > 0 => $"Moves first (priority +{priority})",
            _ => $"Moves last (priority {priority})"
        };
    }

    private void MoveField_Changed(object? sender, RoutedEventArgs e) => ApplyMoveFields();
    private void MoveField_Changed(object? sender, SelectionChangedEventArgs e) => ApplyMoveFields();
    private void MoveField_Changed(object? sender, NumericUpDownValueChangedEventArgs e) => ApplyMoveFields();

    private void MovePriority_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        UpdateMovePriorityIndicator((int)(MovePriorityInput.Value ?? 0));
        ApplyMoveFields();
    }

    private void ApplyMoveFields()
    {
        if (loadingMoveEditor || currentMoveData is null) return;

        MoveRecord m = currentMoveData;
        m.MoveType = (PokemonType)Math.Max(0, MoveTypeSelector.SelectedIndex);
        m.Split = (MoveSplit)Math.Max(0, MoveSplitSelector.SelectedIndex);
        m.Damage = (byte)(MovePowerInput.Value ?? 0);
        m.Accuracy = (byte)(MoveAccuracyInput.Value ?? 0);
        m.Pp = (byte)(MovePpInput.Value ?? 0);

        int rangeIndex = MoveRangeSelector.SelectedIndex;
        if (rangeIndex >= 0 && rangeIndex < MoveDataService.AttackRangeDescriptions.Length)
        {
            m.Target = MoveDataService.AttackRangeDescriptions[rangeIndex].Value;
        }

        m.Priority = (sbyte)Math.Clamp((int)(MovePriorityInput.Value ?? 0), sbyte.MinValue, sbyte.MaxValue);
        m.SideEffectProbability = (byte)(MoveSideEffectInput.Value ?? 0);
        m.ContestConditionType = (ContestCondition)Math.Max(0, MoveContestConditionSelector.SelectedIndex);
        m.ContestAppeal = (byte)(MoveContestAppealInput.Value ?? 0);
        m.BattleEffect = (ushort)Math.Max(0, MoveBattleEffectSelector.SelectedIndex);

        RenderMovePreview();
        ReportEdit(moveEdits, () => currentMoveData, MoveEditorStatus);
    }

    private void MoveFlagsCheckBox_CheckedChanged(object? sender, RoutedEventArgs e)
    {
        if (loadingMoveEditor || currentMoveData is null) return;

        byte flags = 0;
        foreach (CheckBox checkBox in MoveFlagsPanel.Children.OfType<CheckBox>())
        {
            int bit = MoveFlagBitFromLabel(checkBox.Content as string);
            if (bit >= 0 && checkBox.IsChecked == true) flags |= (byte)(1 << bit);
        }

        currentMoveData.FlagField = flags;
        ReportEdit(moveEdits, () => currentMoveData, MoveEditorStatus);
    }

    /// <summary>The archive writes a description's line breaks as the text code \n; the editor shows them
    /// as real lines.</summary>
    private static string DescriptionForEditing(string stored) => stored.Replace("\\n", "\n");

    private static string DescriptionForStoring(string edited) => edited.Replace("\r\n", "\n").Replace("\n", "\\n");

    private void MoveText_Changed(object? sender, TextChangedEventArgs e)
    {
        if (loadingMoveEditor || currentMoveId < 0 || moveNamesArchive is null || moveDescriptionsArchive is null) return;
        int id = currentMoveId;
        // TextChanged also arrives (after the fact) for text a load put in the box; only real edits count.
        if (ReferenceEquals(sender, MoveNameInput) && id < moveEditorMoveNames.Length && id < moveNamesArchive.Messages.Count)
        {
            string name = MoveNameInput.Text ?? string.Empty;
            if (name == moveEditorMoveNames[id]) return;
            moveEditorMoveNames[id] = moveNamesArchive.Messages[id] = name;
        }
        else if (ReferenceEquals(sender, MoveDescriptionText) && id < moveEditorDescriptions.Length && id < moveDescriptionsArchive.Messages.Count)
        {
            string description = DescriptionForStoring(MoveDescriptionText.Text ?? string.Empty);
            if (description == moveEditorDescriptions[id]) return;
            moveEditorDescriptions[id] = moveDescriptionsArchive.Messages[id] = description;
        }
        else return;

        RenderMovePreview();
        MoveEditorStatus.Text = MoveTextsUnsaved()
            ? $"Move {id:D3}'s name or description changed; Save Move saves it."
            : $"Move {id:D3}'s name and description match the saved ones again.";
    }

    private bool MoveTextsUnsaved() =>
        moveNamesArchive is not null && !moveNamesArchive.Messages.SequenceEqual(savedMoveNames)
        || moveDescriptionsArchive is not null && !moveDescriptionsArchive.Messages.SequenceEqual(savedMoveDescriptions);

    /// <summary>Saves the move names and descriptions archives when edited (names renaming the scripts'
    /// MOVE_ constants to match), and refreshes the move list's names. Returns a note for the status line.</summary>
    private string SaveMoveTexts()
    {
        if (currentProject is null || moveNamesArchive is null || moveDescriptionsArchive is null) return "";
        string note = "";
        if (!moveNamesArchive.Messages.SequenceEqual(savedMoveNames))
        {
            note += " Saved the move names." + SaveTextArchiveKeepingScripts(moveNamesArchive);
            savedMoveNames = [.. moveNamesArchive.Messages];
            bool wasLoading = loadingMoveEditor;
            loadingMoveEditor = true;
            try
            {
                int selected = MoveSelector.SelectedIndex;
                SetItems(MoveSelector, moveEditorMoveNames);
                MoveSelector.SelectedIndex = selected;
            }
            finally
            {
                loadingMoveEditor = wasLoading;
            }
        }
        if (!moveDescriptionsArchive.Messages.SequenceEqual(savedMoveDescriptions))
        {
            TextArchiveService.Save(currentProject, moveDescriptionsArchive);
            savedMoveDescriptions = [.. moveDescriptionsArchive.Messages];
            note += " Saved the move descriptions.";
        }
        return note;
    }

    private void SaveMove_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentMoveData is null) return;
        try
        {
            MoveDataService.Save(currentProject, currentMoveId, currentMoveData);
            moveEdits.Saved(currentMoveData);
            string textNote = SaveMoveTexts();
            MoveEditorStatus.Text = $"Saved move data for {currentMoveId:D3}.{textNote}";
        }
        catch (Exception ex)
        {
            LogUiError("Move data could not be saved", ex);
            MoveEditorStatus.Text = ex.Message;
        }
    }

    private async void ExportMoveDataCsv_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null) return;

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = "MoveData.csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
        });
        if (file is null) return;

        try
        {
            await using var stream = await file.OpenWriteAsync();
            using var writer = new StreamWriter(stream);
            writer.WriteLine(MoveDataCsvService.Header);

            int count = MoveDataService.GetFileCount(currentProject);
            for (int i = 0; i < count; i++)
            {
                MoveRecord move = MoveDataService.Load(currentProject, i);
                string name = i < moveEditorMoveNames.Length ? moveEditorMoveNames[i] : $"Move_{i}";
                writer.WriteLine(MoveDataCsvService.ExportRow(i, name, move, moveEditorTypeNames));
            }

            MoveEditorStatus.Text = $"Exported {count} move data entries to {file.Name}.";
        }
        catch (Exception ex)
        {
            LogUiError("Move data could not be exported", ex);
            MoveEditorStatus.Text = ex.Message;
        }
    }

    private async void ImportMoveDataCsv_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null) return;
        // The open move is reloaded from the imported data afterwards.
        if (!await ConfirmLeavingDocumentAsync("Move Editor")) return;

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
        });
        if (files.Count != 1) return;

        try
        {
            string[] lines = File.ReadAllLines(files[0].Path.LocalPath);
            int maxId = MoveDataService.GetFileCount(currentProject) - 1;
            MoveDataCsvImportResult result = MoveDataCsvService.Parse(lines, moveEditorTypeNames, maxId);
            List<MoveDataCsvChange> changes = MoveDataCsvService.ComputeChanges(currentProject, result.ValidEntries, moveEditorTypeNames);

            var preview = new MoveDataImportPreviewWindow(result, changes);
            await preview.ShowDialog(this);
            if (!preview.Confirmed)
            {
                MoveEditorStatus.Text = "Move data import cancelled.";
                return;
            }

            foreach (MoveDataCsvEntry entry in result.ValidEntries)
            {
                MoveDataCsvService.Apply(currentProject, entry);
            }

            if (currentMoveId >= 0) LoadMove(currentMoveId);

            MoveEditorStatus.Text = result.Errors.Count == 0
                ? $"Imported {result.ValidEntries.Count} move data entries."
                : $"Imported {result.ValidEntries.Count} entries, {result.Errors.Count} row(s) skipped.";
        }
        catch (Exception ex)
        {
            LogUiError("Move data could not be imported", ex);
            MoveEditorStatus.Text = ex.Message;
        }
    }

    #endregion

    #region Item Editor

    private void ConfigureItemEditor()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            return;
        }

        try
        {
            // Re-extracting would overwrite edits saved since the last Save ROM.
            if (!Directory.Exists(currentProject.ItemDataDirectory)) new RomExtractionService().PrepareItemData(currentProject, metadata);

            itemNamesArchive = GetOpenTextArchive(TrainerService.GetItemNamesArchiveId(metadata), metadata);
            itemEditorNames = itemNamesArchive.Messages.ToArray();
            savedItemNames = TextArchiveService.Load(currentProject, itemNamesArchive.Id, metadata).Messages.ToList();
            itemDescriptionsArchive = GetOpenTextArchive(ItemDataService.GetItemDescriptionsArchiveId(metadata), metadata);
            itemEditorDescriptions = itemDescriptionsArchive.Messages.ToArray();
            savedItemDescriptions = TextArchiveService.Load(currentProject, itemDescriptionsArchive.Id, metadata).Messages.ToList();

            SetItems(FieldPocketSelector, Enum.GetNames<FieldPocket>());
            // Legacy removes "GiratinaBoost" from this list on DP (that hold effect doesn't exist there),
            // but does so by index-removal from the combo, which desyncs every later SelectedIndex from
            // its enum value - a real bug, not intentional behavior worth reproducing. Keeping the full
            // list on every version avoids silently mis-assigning hold effects when editing DP items.
            SetItems(HoldEffectSelector, Enum.GetNames<HoldEffect>());
            SetItems(NaturalGiftTypeSelector, Enum.GetNames<NaturalGiftType>());
            SetItems(FieldFunctionSelector, Enum.GetNames<FieldUseFunc>());
            SetItems(BattleFunctionSelector, Enum.GetNames<BattleUseFunc>());

            (List<int> iconIds, List<int> paletteIds) = ItemDataService.ListItemIconFiles(currentProject);
            SetItems(ItemIconSelector, iconIds.Select(i => i.ToString("D4")));
            SetItems(ItemPaletteSelector, paletteIds.Select(i => i.ToString("D4")));

            SetItems(ItemSelector, itemEditorNames);

            if (itemEditorNames.Length > 1)
            {
                LoadItem(currentItemId >= 0 && currentItemId < itemEditorNames.Length ? currentItemId : 1, metadata);
            }

            ItemEditorStatus.Text = $"{ItemDataService.GetFileCount(currentProject)} item data entries loaded.";
        }
        catch (Exception ex)
        {
            LogUiError("Item data could not be loaded", ex);
            ItemEditorStatus.Text = ex.Message;
        }
    }

    private async void ItemSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingItemEditor || switchingDocument || currentProject is null || ItemSelector.SelectedIndex < 0) return;
        if (!currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        int id = ItemSelector.SelectedIndex;
        int open = currentItemId;
        if (!await ShouldLoadDocumentAsync(id == open, "Item Editor", () => ItemSelector.SelectedIndex = open)) return;
        LoadItem(id, metadata);
    }

    private void LoadItem(int id, RomGameMetadata metadata)
    {
        if (currentProject is null) return;

        loadingItemEditor = true;
        try
        {
            currentItemId = id;
            if (ItemSelector.SelectedIndex != id) ItemSelector.SelectedIndex = id;
            ItemNameInput.Text = id < itemEditorNames.Length ? itemEditorNames[id] : string.Empty;
            ItemDescriptionText.Text = DescriptionForEditing(id < itemEditorDescriptions.Length ? itemEditorDescriptions[id] : string.Empty);

            currentItemTableEntry = ItemDataService.ReadTableEntry(currentProject, metadata, id);
            itemTableEntryEdits.Loaded(currentItemTableEntry, $"Item {id:D3} table entry");
            ItemDataIdInput.Value = currentItemTableEntry.ItemDataId;
            SelectComboString(ItemIconSelector, currentItemTableEntry.ItemIconId.ToString("D4"));
            SelectComboString(ItemPaletteSelector, currentItemTableEntry.ItemPaletteId.ToString("D4"));
            RenderItemIcon();

            try
            {
                currentItemData = ItemDataService.Load(currentProject, currentItemTableEntry.ItemDataId);
                itemDataEdits.Loaded(currentItemData, $"Item data {currentItemTableEntry.ItemDataId:D3}");
            }
            catch (Exception ex)
            {
                currentItemData = null;
                LogUiError("Item data could not be loaded", ex);
                return;
            }

            PopulateItemDataFields();

            string name = id < itemEditorNames.Length ? itemEditorNames[id] : $"Item {id}";
            ItemEditorStatus.Text = $"Loaded {id:D3} - {name}.";
        }
        finally
        {
            loadingItemEditor = false;
        }
    }

    private static void SelectComboString(ComboBox box, string value)
    {
        for (int i = 0; i < box.Items.Count; i++)
        {
            if (box.Items[i] as string == value)
            {
                box.SelectedIndex = i;
                return;
            }
        }

        box.SelectedIndex = -1;
    }

    private void RenderItemIcon()
    {
        if (currentProject is null) return;
        byte[]? rgba = SpriteService.GetItemIconRgba(currentProject, currentItemTableEntry.ItemIconId, currentItemTableEntry.ItemPaletteId);
        ItemIconImage.Source = CreateBitmapFromRgba(rgba, 32, 32);
        RenderItemPreview();
    }

    private async void ExportItemIcon_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || ItemSelector.SelectedIndex < 0) return;
        byte[]? rgba = SpriteService.GetItemIconRgba(currentProject, currentItemTableEntry.ItemIconId, currentItemTableEntry.ItemPaletteId);
        if (rgba is null)
        {
            ItemEditorStatus.Text = "This item's icon could not be read.";
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export item icon",
            SuggestedFileName = $"item_{ItemSelector.SelectedIndex:D4}.png",
            FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"] }]
        });
        if (file is null) return;

        try
        {
            CreateBitmapFromRgba(rgba, 32, 32)?.Save(file.Path.LocalPath);
            ItemEditorStatus.Text = "Item icon exported.";
        }
        catch (Exception ex)
        {
            LogUiError("Item icon could not be exported", ex);
            ItemEditorStatus.Text = ex.Message;
        }
    }

    private void PopulateItemDataFields()
    {
        if (currentItemData is null) return;
        ItemRecord d = currentItemData;

        FieldPocketSelector.SelectedIndex = (int)d.FieldPocketValue;
        PokeBallsBattlePocketCheck.IsChecked = (d.BattlePocketValue & BattlePocket.PokeBalls) != 0;
        BattleItemsBattlePocketCheck.IsChecked = (d.BattlePocketValue & BattlePocket.BattleItems) != 0;
        HpRestoreBattlePocketCheck.IsChecked = (d.BattlePocketValue & BattlePocket.HpRestore) != 0;
        StatusHealersBattlePocketCheck.IsChecked = (d.BattlePocketValue & BattlePocket.StatusHealers) != 0;
        PpRestoreBattlePocketCheck.IsChecked = (d.BattlePocketValue & BattlePocket.PpRestore) != 0;
        PreventTossCheck.IsChecked = d.PreventToss;
        CanSelectCheck.IsChecked = d.Selectable;
        PartyUseCheck.IsChecked = d.PartyUse == 1;
        PartyUseParamsPanel.IsEnabled = d.PartyUse == 1;

        PriceInput.Value = d.Price;
        HoldEffectSelector.SelectedIndex = (int)d.HoldEffectValue;
        HoldEffectParamInput.Value = d.HoldEffectParam;

        // NaturalGiftType has gaps in its numeric values (9, 18-30 unused), so it's matched by name, not
        // SelectedIndex, matching legacy's own Enum.GetName/SelectedItem approach exactly.
        SelectComboString(NaturalGiftTypeSelector, d.NaturalGiftTypeValue.ToString());
        NaturalGiftPowerInput.Value = d.NaturalGiftPower;
        PluckEffectInput.Value = d.PluckEffect;
        FlingEffectInput.Value = d.FlingEffect;
        FlingPowerInput.Value = d.FlingPower;

        SelectEnumOrUnknown(FieldFunctionSelector, (int)d.FieldUseFuncValue, Enum.IsDefined(d.FieldUseFuncValue));
        SelectEnumOrUnknown(BattleFunctionSelector, (int)d.BattleUseFuncValue, Enum.IsDefined(d.BattleUseFuncValue));

        PopulatePartyUseParamFields();
    }

    private static void SelectEnumOrUnknown(ComboBox box, int value, bool isDefined)
    {
        if (isDefined)
        {
            box.SelectedIndex = value;
            return;
        }

        string unknown = $"Unknown ({value})";
        if (!box.Items.Cast<object?>().Any(item => item as string == unknown))
        {
            box.Items.Add(unknown);
        }

        SelectComboString(box, unknown);
    }

    private void PopulatePartyUseParamFields()
    {
        if (currentItemData is null) return;
        ItemPartyUseParam p = currentItemData.PartyUseParam;

        SlpHealCheck.IsChecked = p.SlpHeal;
        PsnHealCheck.IsChecked = p.PsnHeal;
        BrnHealCheck.IsChecked = p.BrnHeal;
        FrzHealCheck.IsChecked = p.FrzHeal;
        PrzHealCheck.IsChecked = p.PrzHeal;
        CfsHealCheck.IsChecked = p.CfsHeal;
        InfHealCheck.IsChecked = p.InfHeal;
        GuardSpecCheck.IsChecked = p.GuardSpec;

        ReviveCheck.IsChecked = p.Revive;
        ReviveAllCheck.IsChecked = p.ReviveAll;
        LevelUpCheck.IsChecked = p.LevelUp;
        EvolveCheck.IsChecked = p.Evolve;

        AtkStagesInput.Value = p.AtkStages;
        DefStagesInput.Value = p.DefStages;
        SpAtkStagesInput.Value = p.SpAtkStages;
        SpDefStagesInput.Value = p.SpDefStages;
        SpeedStagesInput.Value = p.SpeedStages;
        AccuracyStagesInput.Value = p.AccuracyStages;
        CritRateStagesInput.Value = p.CritRateStages;

        HpRestoreCheck.IsChecked = p.HPRestore;
        HpRestoreParamInput.Value = p.HPRestoreParam;
        PpRestoreCheck.IsChecked = p.PPRestore;
        PpRestoreParamInput.Value = p.PPRestoreParam;
        PpUpsCheck.IsChecked = p.PPUps;
        PpMaxCheck.IsChecked = p.PPMax;
        PpRestoreAllCheck.IsChecked = p.PPRestoreAll;

        EvHpCheck.IsChecked = p.EVHp;
        EvAtkCheck.IsChecked = p.EVAtk;
        EvDefCheck.IsChecked = p.EVDef;
        EvSpeedCheck.IsChecked = p.EVSpeed;
        EvSpAtkCheck.IsChecked = p.EVSpAtk;
        EvSpDefCheck.IsChecked = p.EVSpDef;

        EvHpValueInput.Value = p.EVHpValue;
        EvAtkValueInput.Value = p.EVAtkValue;
        EvDefValueInput.Value = p.EVDefValue;
        EvSpeedValueInput.Value = p.EVSpeedValue;
        EvSpAtkValueInput.Value = p.EVSpAtkValue;
        EvSpDefValueInput.Value = p.EVSpDefValue;

        FriendshipLowCheck.IsChecked = p.FriendshipLow;
        FriendshipMidCheck.IsChecked = p.FriendshipMid;
        FriendshipHighCheck.IsChecked = p.FriendshipHigh;

        FriendshipLowValueInput.Value = p.FriendshipLowValue;
        FriendshipMidValueInput.Value = p.FriendshipMidValue;
        FriendshipHighValueInput.Value = p.FriendshipHighValue;
    }

    private async void ItemDataId_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (loadingItemEditor || switchingDocument || currentProject is null) return;

        ushort newDataId = (ushort)Math.Max(0, (int)(ItemDataIdInput.Value ?? 0));
        ushort openDataId = currentItemTableEntry.ItemDataId;
        if (newDataId == openDataId) return;
        // Pointing the item at other data shows that data instead, so edits to the data shown now are saved
        // or discarded first.
        if (!await ConfirmLeavingDocumentAsync("Item Editor", change => change.Area == "Item data", () => ItemDataIdInput.Value = openDataId)) return;
        RunSwitchingDocument(() => ItemDataIdInput.Value = newDataId);
        currentItemTableEntry = currentItemTableEntry with { ItemDataId = newDataId };
        try
        {
            currentItemData = ItemDataService.Load(currentProject, newDataId);
            itemDataEdits.Loaded(currentItemData, $"Item data {newDataId:D3}");
            PopulateItemDataFields();
        }
        catch (Exception ex)
        {
            LogUiError("Item data could not be loaded", ex);
        }

        ReportEdit(itemTableEntryEdits, () => currentItemTableEntry, ItemEditorStatus);
    }

    private void ItemTableEntry_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingItemEditor) return;

        if (ItemIconSelector.SelectedItem is string iconStr && ushort.TryParse(iconStr, out ushort iconId))
        {
            currentItemTableEntry = currentItemTableEntry with { ItemIconId = iconId };
        }

        if (ItemPaletteSelector.SelectedItem is string paletteStr && ushort.TryParse(paletteStr, out ushort paletteId))
        {
            currentItemTableEntry = currentItemTableEntry with { ItemPaletteId = paletteId };
        }

        RenderItemIcon();
        ReportEdit(itemTableEntryEdits, () => currentItemTableEntry, ItemEditorStatus);
    }

    private void ItemField_Changed(object? sender, RoutedEventArgs e) => ApplyItemFields();
    private void ItemField_Changed(object? sender, SelectionChangedEventArgs e) => ApplyItemFields();
    private void ItemField_Changed(object? sender, NumericUpDownValueChangedEventArgs e) => ApplyItemFields();

    private void ApplyItemFields()
    {
        if (loadingItemEditor || currentItemData is null) return;

        ItemRecord d = currentItemData;
        d.FieldPocketValue = (FieldPocket)Math.Max(0, FieldPocketSelector.SelectedIndex);
        d.PreventToss = PreventTossCheck.IsChecked == true;
        d.Selectable = CanSelectCheck.IsChecked == true;

        d.Price = (ushort)(PriceInput.Value ?? 0);
        d.HoldEffectValue = (HoldEffect)Math.Max(0, HoldEffectSelector.SelectedIndex);
        d.HoldEffectParam = (byte)(HoldEffectParamInput.Value ?? 0);

        if (NaturalGiftTypeSelector.SelectedItem is string giftTypeName && Enum.TryParse(giftTypeName, out NaturalGiftType giftType))
        {
            d.NaturalGiftTypeValue = giftType;
        }

        d.NaturalGiftPower = (byte)(NaturalGiftPowerInput.Value ?? 0);
        d.PluckEffect = (byte)(PluckEffectInput.Value ?? 0);
        d.FlingEffect = (byte)(FlingEffectInput.Value ?? 0);
        d.FlingPower = (byte)(FlingPowerInput.Value ?? 0);

        d.FieldUseFuncValue = ParseFuncSelection<FieldUseFunc>(FieldFunctionSelector);
        d.BattleUseFuncValue = ParseFuncSelection<BattleUseFunc>(BattleFunctionSelector);

        ReportEdit(itemDataEdits, () => currentItemData, ItemEditorStatus);
    }

    private static T ParseFuncSelection<T>(ComboBox box) where T : struct, Enum
    {
        if (box.SelectedItem is string text)
        {
            if (text.StartsWith("Unknown (") && text.EndsWith(')'))
            {
                string numeric = text[9..^1];
                if (byte.TryParse(numeric, out byte value)) return (T)(object)value;
            }
            else if (Enum.TryParse(text, out T parsed))
            {
                return parsed;
            }
        }

        return default;
    }

    private void BattlePocket_Changed(object? sender, RoutedEventArgs e)
    {
        if (loadingItemEditor || currentItemData is null) return;

        BattlePocket pocket = BattlePocket.None;
        if (PokeBallsBattlePocketCheck.IsChecked == true) pocket |= BattlePocket.PokeBalls;
        if (BattleItemsBattlePocketCheck.IsChecked == true) pocket |= BattlePocket.BattleItems;
        if (HpRestoreBattlePocketCheck.IsChecked == true) pocket |= BattlePocket.HpRestore;
        if (StatusHealersBattlePocketCheck.IsChecked == true) pocket |= BattlePocket.StatusHealers;
        if (PpRestoreBattlePocketCheck.IsChecked == true) pocket |= BattlePocket.PpRestore;

        currentItemData.BattlePocketValue = pocket;
        ReportEdit(itemDataEdits, () => currentItemData, ItemEditorStatus);
    }

    private void PartyUseCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (loadingItemEditor || currentItemData is null) return;

        currentItemData.PartyUse = (byte)(PartyUseCheck.IsChecked == true ? 1 : 0);
        PartyUseParamsPanel.IsEnabled = PartyUseCheck.IsChecked == true;
        ReportEdit(itemDataEdits, () => currentItemData, ItemEditorStatus);
    }

    private void PartyUseParam_Changed(object? sender, RoutedEventArgs e) => ApplyPartyUseParams();
    private void PartyUseParam_NumericChanged(object? sender, NumericUpDownValueChangedEventArgs e) => ApplyPartyUseParams();

    private void ApplyPartyUseParams()
    {
        if (loadingItemEditor || currentItemData is null) return;

        ItemPartyUseParam p = currentItemData.PartyUseParam;

        p.SlpHeal = SlpHealCheck.IsChecked == true;
        p.PsnHeal = PsnHealCheck.IsChecked == true;
        p.BrnHeal = BrnHealCheck.IsChecked == true;
        p.FrzHeal = FrzHealCheck.IsChecked == true;
        p.PrzHeal = PrzHealCheck.IsChecked == true;
        p.CfsHeal = CfsHealCheck.IsChecked == true;
        p.InfHeal = InfHealCheck.IsChecked == true;
        p.GuardSpec = GuardSpecCheck.IsChecked == true;

        p.Revive = ReviveCheck.IsChecked == true;
        p.ReviveAll = ReviveAllCheck.IsChecked == true;
        p.LevelUp = LevelUpCheck.IsChecked == true;
        p.Evolve = EvolveCheck.IsChecked == true;

        p.AtkStages = (sbyte)(AtkStagesInput.Value ?? 0);
        p.DefStages = (sbyte)(DefStagesInput.Value ?? 0);
        p.SpAtkStages = (sbyte)(SpAtkStagesInput.Value ?? 0);
        p.SpDefStages = (sbyte)(SpDefStagesInput.Value ?? 0);
        p.SpeedStages = (sbyte)(SpeedStagesInput.Value ?? 0);
        p.AccuracyStages = (sbyte)(AccuracyStagesInput.Value ?? 0);
        p.CritRateStages = (sbyte)(CritRateStagesInput.Value ?? 0);

        p.HPRestore = HpRestoreCheck.IsChecked == true;
        p.HPRestoreParam = (byte)(HpRestoreParamInput.Value ?? 0);
        p.PPRestore = PpRestoreCheck.IsChecked == true;
        p.PPRestoreParam = (byte)(PpRestoreParamInput.Value ?? 0);
        p.PPUps = PpUpsCheck.IsChecked == true;
        p.PPMax = PpMaxCheck.IsChecked == true;
        p.PPRestoreAll = PpRestoreAllCheck.IsChecked == true;

        p.EVHp = EvHpCheck.IsChecked == true;
        p.EVAtk = EvAtkCheck.IsChecked == true;
        p.EVDef = EvDefCheck.IsChecked == true;
        p.EVSpeed = EvSpeedCheck.IsChecked == true;
        p.EVSpAtk = EvSpAtkCheck.IsChecked == true;
        p.EVSpDef = EvSpDefCheck.IsChecked == true;

        p.EVHpValue = (sbyte)(EvHpValueInput.Value ?? 0);
        p.EVAtkValue = (sbyte)(EvAtkValueInput.Value ?? 0);
        p.EVDefValue = (sbyte)(EvDefValueInput.Value ?? 0);
        p.EVSpeedValue = (sbyte)(EvSpeedValueInput.Value ?? 0);
        p.EVSpAtkValue = (sbyte)(EvSpAtkValueInput.Value ?? 0);
        p.EVSpDefValue = (sbyte)(EvSpDefValueInput.Value ?? 0);

        p.FriendshipLow = FriendshipLowCheck.IsChecked == true;
        p.FriendshipMid = FriendshipMidCheck.IsChecked == true;
        p.FriendshipHigh = FriendshipHighCheck.IsChecked == true;

        p.FriendshipLowValue = (sbyte)(FriendshipLowValueInput.Value ?? 0);
        p.FriendshipMidValue = (sbyte)(FriendshipMidValueInput.Value ?? 0);
        p.FriendshipHighValue = (sbyte)(FriendshipHighValueInput.Value ?? 0);

        ReportEdit(itemDataEdits, () => currentItemData, ItemEditorStatus);
    }

    private void ItemText_Changed(object? sender, TextChangedEventArgs e)
    {
        if (loadingItemEditor || itemNamesArchive is null || itemDescriptionsArchive is null) return;
        int id = currentItemId;
        if (id < 0) return;
        // TextChanged also arrives (after the fact) for text a load put in the box; only real edits count.
        if (ReferenceEquals(sender, ItemNameInput) && id < itemEditorNames.Length && id < itemNamesArchive.Messages.Count)
        {
            string name = ItemNameInput.Text ?? string.Empty;
            if (name == itemEditorNames[id]) return;
            itemEditorNames[id] = itemNamesArchive.Messages[id] = name;
        }
        else if (ReferenceEquals(sender, ItemDescriptionText) && id < itemEditorDescriptions.Length && id < itemDescriptionsArchive.Messages.Count)
        {
            string description = DescriptionForStoring(ItemDescriptionText.Text ?? string.Empty);
            if (description == itemEditorDescriptions[id]) return;
            itemEditorDescriptions[id] = itemDescriptionsArchive.Messages[id] = description;
        }
        else return;

        RenderItemPreview();
        ItemEditorStatus.Text = ItemTextsUnsaved()
            ? $"Item {id:D3}'s name or description changed; Save Item saves it."
            : $"Item {id:D3}'s name and description match the saved ones again.";
    }

    private bool ItemTextsUnsaved() =>
        itemNamesArchive is not null && !itemNamesArchive.Messages.SequenceEqual(savedItemNames)
        || itemDescriptionsArchive is not null && !itemDescriptionsArchive.Messages.SequenceEqual(savedItemDescriptions);

    /// <summary>Saves the item names and descriptions archives when edited (names renaming the scripts'
    /// ITEM_ constants to match), and refreshes the item list's names. Returns a note for the status line.</summary>
    private string SaveItemTexts()
    {
        if (currentProject is null || itemNamesArchive is null || itemDescriptionsArchive is null) return "";
        string note = "";
        if (!itemNamesArchive.Messages.SequenceEqual(savedItemNames))
        {
            note += " Saved the item names." + SaveTextArchiveKeepingScripts(itemNamesArchive);
            savedItemNames = [.. itemNamesArchive.Messages];
            bool wasLoading = loadingItemEditor;
            loadingItemEditor = true;
            try
            {
                int selected = ItemSelector.SelectedIndex;
                SetItems(ItemSelector, itemEditorNames);
                ItemSelector.SelectedIndex = selected;
            }
            finally
            {
                loadingItemEditor = wasLoading;
            }
        }
        if (!itemDescriptionsArchive.Messages.SequenceEqual(savedItemDescriptions))
        {
            TextArchiveService.Save(currentProject, itemDescriptionsArchive);
            savedItemDescriptions = [.. itemDescriptionsArchive.Messages];
            note += " Saved the item descriptions.";
        }
        return note;
    }

    private void SaveItem_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentItemData is null) return;
        if (!currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;

        try
        {
            ItemDataService.Save(currentProject, currentItemTableEntry.ItemDataId, currentItemData);
            ItemDataService.WriteTableEntry(currentProject, metadata, currentItemId, currentItemTableEntry);
            itemDataEdits.Saved(currentItemData);
            itemTableEntryEdits.Saved(currentItemTableEntry);
            ItemEditorStatus.Text = $"Saved item {currentItemId:D3}.{SaveItemTexts()}";
        }
        catch (Exception ex)
        {
            LogUiError("Item could not be saved", ex);
            ItemEditorStatus.Text = ex.Message;
        }
    }

    #endregion

    #region Trade Editor

    private void ConfigureTradeEditor()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            return;
        }

        try
        {
            // Re-extracting would overwrite edits saved since the last Save ROM.
            if (!Directory.Exists(currentProject.TradeDataDirectory)) new RomExtractionService().PrepareTradeData(currentProject, metadata);

            int tradeCount = TradeDataService.GetFileCount(currentProject);
            if (tradeCount == 0)
            {
                TradeEditorStatus.Text = "No trades could be found! The NARC may have failed to unpack.";
                return;
            }

            tradeEditorIsHgss = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;
            TradeUnknownInput.IsEnabled = tradeEditorIsHgss;

            string[] pokemonNames = TextArchiveService.Load(currentProject, TrainerService.GetPokemonNamesArchiveId(metadata), metadata).Messages.ToArray();
            string[] abilityNames = TextArchiveService.Load(currentProject, TrainerService.GetAbilityNamesArchiveId(metadata), metadata).Messages.ToArray();
            string[] itemNames = TextArchiveService.Load(currentProject, TrainerService.GetItemNamesArchiveId(metadata), metadata).Messages.ToArray();

            SetItems(TradeSpeciesSelector, pokemonNames);
            SetItems(TradeRequestedSpeciesSelector, pokemonNames);
            SetItems(TradeAbilitySelector, abilityNames);
            SetItems(TradeHeldItemSelector, itemNames);
            SetItems(TradeOtGenderSelector, ["Male", "Female"]);
            SetItems(TradeLanguageSelector, Enum.GetNames<TradeOriginLanguage>());

            TradeIdInput.Maximum = tradeCount - 1;

            currentTradeArchive = TextArchiveService.Load(currentProject, TradeDataService.GetTextBankArchiveId(metadata), metadata);

            LoadTrade(currentTradeId >= 0 && currentTradeId < tradeCount ? currentTradeId : 0);

            TradeEditorStatus.Text = $"{tradeCount} trade entries loaded.";
        }
        catch (Exception ex)
        {
            LogUiError("Trade data could not be loaded", ex);
            TradeEditorStatus.Text = ex.Message;
        }
    }

    private async void TradeId_Changed(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (loadingTradeEditor || switchingDocument || currentProject is null) return;

        int newId = (int)(TradeIdInput.Value ?? 0);
        int tradeCount = TradeDataService.GetFileCount(currentProject);
        if (newId < 0 || newId >= tradeCount) return;

        int open = currentTradeId;
        if (!await ShouldLoadDocumentAsync(newId == open, "Trade Editor", () => TradeIdInput.Value = open)) return;
        LoadTrade(newId);
    }

    private void LoadTrade(int id)
    {
        if (currentProject is null || currentTradeArchive is null) return;

        loadingTradeEditor = true;
        try
        {
            currentTradeId = id;
            if ((int)(TradeIdInput.Value ?? -1) != id) TradeIdInput.Value = id;

            currentTradeData = TradeDataService.Load(currentProject, id, tradeEditorIsHgss);
            ShowTradeUses(id);
            tradeEdits.Loaded(currentTradeData, $"Trade {id:D2}");
            TradeRecord t = currentTradeData;

            TradeSpeciesSelector.SelectedIndex = Math.Min(t.Species, TradeSpeciesSelector.Items.Count - 1);
            TradeRequestedSpeciesSelector.SelectedIndex = Math.Min(t.RequestedSpecies, TradeRequestedSpeciesSelector.Items.Count - 1);
            TradeAbilitySelector.SelectedIndex = Math.Min(Math.Max(0, t.Ability), TradeAbilitySelector.Items.Count - 1);
            TradeHeldItemSelector.SelectedIndex = Math.Min(t.HeldItem, TradeHeldItemSelector.Items.Count - 1);

            TradeHpIvInput.Value = t.HpIv;
            TradeAtkIvInput.Value = t.AtkIv;
            TradeDefIvInput.Value = t.DefIv;
            TradeSpeedIvInput.Value = t.SpeedIv;
            TradeSpAtkIvInput.Value = t.SpAtkIv;
            TradeSpDefIvInput.Value = t.SpDefIv;

            TradeCoolInput.Value = t.Cool;
            TradeBeautyInput.Value = t.Beauty;
            TradeCuteInput.Value = t.Cute;
            TradeSmartInput.Value = t.Smart;
            TradeToughInput.Value = t.Tough;

            // The file's 32-bit fields are unsigned values held in ints.
            TradeOtIdInput.Value = (uint)t.OtId & 0xFFFF;
            TradeOtSecretIdInput.Value = (uint)t.OtId >> 16;
            TradePidInput.Value = (uint)t.Pid;
            TradeSheenInput.Value = t.Sheen;
            TradeUnknownInput.Value = (uint)t.Unknown;
            TradeOtGenderSelector.SelectedIndex = Math.Clamp(t.OtGender, 0, 1);
            TradeLanguageSelector.SelectedIndex = Math.Clamp(t.Language, 0, TradeLanguageSelector.Items.Count - 1);

            int tradeCount = TradeDataService.GetFileCount(currentProject);
            string nickname = id < currentTradeArchive.Messages.Count ? currentTradeArchive.Messages[id] : string.Empty;
            string otName = id + tradeCount < currentTradeArchive.Messages.Count ? currentTradeArchive.Messages[id + tradeCount] : string.Empty;
            tradeLoadedNickname = nickname.Length > 10 ? nickname[..10] : nickname;
            tradeLoadedOtName = otName.Length > 7 ? otName[..7] : otName;
            TradeNicknameInput.Text = tradeLoadedNickname;
            TradeOtNameInput.Text = tradeLoadedOtName;

            UpdateTradeIcons();
            TradeEditorStatus.Text = $"Loaded trade {id}.";
        }
        finally
        {
            loadingTradeEditor = false;
        }
    }

    private void TradeField_Changed(object? sender, RoutedEventArgs e) => ApplyTradeFields();
    private void TradeField_Changed(object? sender, SelectionChangedEventArgs e) => ApplyTradeFields();

    // Its own name: XAML binds handlers by name alone, so an overload of TradeField_Changed could be the
    // one wired to the species boxes and skip redrawing their icons.
    private void TradeSpecies_Changed(object? sender, SelectionChangedEventArgs e)
    {
        UpdateTradeIcons();
        ApplyTradeFields();
    }

    private void UpdateTradeIcons()
    {
        TradeSpeciesIcon.Source = TradeSpeciesSelector.SelectedIndex > 0 ? LoadTablePokemonIcon(TradeSpeciesSelector.SelectedIndex) : null;
        TradeRequestedSpeciesIcon.Source = TradeRequestedSpeciesSelector.SelectedIndex > 0 ? LoadTablePokemonIcon(TradeRequestedSpeciesSelector.SelectedIndex) : null;
    }
    private void TradeField_Changed(object? sender, NumericUpDownValueChangedEventArgs e) => ApplyTradeFields();

    private void ApplyTradeFields()
    {
        if (loadingTradeEditor || currentTradeData is null) return;

        TradeRecord t = currentTradeData;
        t.Species = Math.Max(0, TradeSpeciesSelector.SelectedIndex);
        t.RequestedSpecies = Math.Max(0, TradeRequestedSpeciesSelector.SelectedIndex);
        t.Ability = Math.Max(0, TradeAbilitySelector.SelectedIndex);
        t.HeldItem = Math.Max(0, TradeHeldItemSelector.SelectedIndex);

        t.HpIv = (int)(TradeHpIvInput.Value ?? 0);
        t.AtkIv = (int)(TradeAtkIvInput.Value ?? 0);
        t.DefIv = (int)(TradeDefIvInput.Value ?? 0);
        t.SpeedIv = (int)(TradeSpeedIvInput.Value ?? 0);
        t.SpAtkIv = (int)(TradeSpAtkIvInput.Value ?? 0);
        t.SpDefIv = (int)(TradeSpDefIvInput.Value ?? 0);

        t.Cool = (int)(TradeCoolInput.Value ?? 0);
        t.Beauty = (int)(TradeBeautyInput.Value ?? 0);
        t.Cute = (int)(TradeCuteInput.Value ?? 0);
        t.Smart = (int)(TradeSmartInput.Value ?? 0);
        t.Tough = (int)(TradeToughInput.Value ?? 0);

        t.OtId = (int)((uint)(TradeOtIdInput.Value ?? 0) | ((uint)(TradeOtSecretIdInput.Value ?? 0) << 16));
        t.Pid = (int)(uint)(TradePidInput.Value ?? 0);
        t.Sheen = (int)(TradeSheenInput.Value ?? 0);
        t.Unknown = tradeEditorIsHgss ? (int)(uint)(TradeUnknownInput.Value ?? 0) : 0;
        t.OtGender = Math.Max(0, TradeOtGenderSelector.SelectedIndex);
        t.Language = Math.Max(0, TradeLanguageSelector.SelectedIndex);

        ReportEdit(tradeEdits, () => currentTradeData, TradeEditorStatus);
    }

    // The names as loaded: TextChanged arrives after LoadTrade has finished, so the loaded text itself
    // comes through the handler too and must not be reported as an edit.
    private string tradeLoadedNickname = string.Empty;
    private string tradeLoadedOtName = string.Empty;

    private void TradeText_Changed(object? sender, TextChangedEventArgs e)
    {
        if (loadingTradeEditor) return;
        bool nickname = ReferenceEquals(sender, TradeNicknameInput);
        string field = nickname ? "nickname" : "original trainer name";
        string value = (sender as TextBox)?.Text ?? string.Empty;
        bool othersChanged = nickname ? (TradeOtNameInput.Text ?? string.Empty) != tradeLoadedOtName : (TradeNicknameInput.Text ?? string.Empty) != tradeLoadedNickname;
        if (value == (nickname ? tradeLoadedNickname : tradeLoadedOtName))
        {
            if (TradeEditorStatus.Text?.StartsWith($"Trade {currentTradeId}: {field} changed", StringComparison.Ordinal) == true && !othersChanged)
            {
                TradeEditorStatus.Text = $"Trade {currentTradeId}: {field} matches the saved text again.";
            }

            return;
        }

        TradeEditorStatus.Text = $"Trade {currentTradeId}: {field} changed to \"{value}\". Save Text Data to keep it.";
    }

    private void SaveTradeData_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentTradeData is null) return;
        try
        {
            TradeDataService.Save(currentProject, currentTradeId, currentTradeData, tradeEditorIsHgss);
            tradeEdits.Saved(currentTradeData);
            TradeEditorStatus.Text = $"Saved trade data for {currentTradeId}.";
        }
        catch (Exception ex)
        {
            LogUiError("Trade data could not be saved", ex);
            TradeEditorStatus.Text = ex.Message;
        }
    }

    private bool TradeTextUnsaved() =>
        currentTradeArchive is not null &&
        ((TradeNicknameInput.Text ?? string.Empty) != tradeLoadedNickname || (TradeOtNameInput.Text ?? string.Empty) != tradeLoadedOtName);

    private void SaveTradeText_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentTradeArchive is null) return;

        int tradeCount = TradeDataService.GetFileCount(currentProject);
        if (currentTradeId < 0 || currentTradeId + tradeCount >= currentTradeArchive.Messages.Count)
        {
            TradeEditorStatus.Text = "Can't save to text bank. Index is out of range.";
            return;
        }

        try
        {
            currentTradeArchive.Messages[currentTradeId] = TradeNicknameInput.Text ?? string.Empty;
            currentTradeArchive.Messages[currentTradeId + tradeCount] = TradeOtNameInput.Text ?? string.Empty;
            TextArchiveService.Save(currentProject, currentTradeArchive);
            tradeLoadedNickname = TradeNicknameInput.Text ?? string.Empty;
            tradeLoadedOtName = TradeOtNameInput.Text ?? string.Empty;
            TradeEditorStatus.Text = $"Saved trade text data for {currentTradeId}.";
        }
        catch (Exception ex)
        {
            LogUiError("Trade text data could not be saved", ex);
            TradeEditorStatus.Text = ex.Message;
        }
    }

    private void SaveTradeAll_Click(object? sender, RoutedEventArgs e)
    {
        SaveTradeData_Click(sender, e);
        SaveTradeText_Click(sender, e);
    }

    #endregion

    #region Fly Warp Editor

    #region Overworld Graphic Editor

    private List<OverworldSpriteEntry> owGraphicEntries = [];
    private OverworldSpriteSheet? owGraphicSheet;
    private RomProject? owGraphicEditorProject;
    private bool loadingOwGraphicEditor;

    /// <summary>Ported from the legacy DS_Map/Editors/BtxEditor ("Overworld Editor"), without its
    /// add/delete of custom entries, which only exists for ROMs patched with hzla's table expansion.</summary>
    private void ConfigureOverworldGraphicEditor()
    {
        if (currentProject is null || ReferenceEquals(owGraphicEditorProject, currentProject)) return;
        if (!currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        owGraphicEditorProject = currentProject;

        OwGraphicRenderStatePanel.IsVisible = OverworldSpriteService.SupportsRenderState(metadata);
        loadingOwGraphicEditor = true;
        try
        {
            if (!Directory.Exists(currentProject.OverworldSpritesDirectory))
            {
                new RomExtractionService().PrepareOverworldSprites(currentProject, metadata);
            }

            owGraphicEntries = OverworldSpriteService.LoadTable(currentProject, metadata);
            OwGraphicList.Items.Clear();
            foreach (OverworldSpriteEntry entry in owGraphicEntries) OwGraphicList.Items.Add(CreateOwGraphicRow(entry));

            OwGraphicStatus.Text = $"{owGraphicEntries.Count} overworld entries loaded.";
            UpdateOwExpansionStatus();
        }
        catch (Exception ex)
        {
            LogUiError("Overworld table could not be loaded", ex);
            owGraphicEntries = [];
            OwGraphicList.Items.Clear();
            OwGraphicStatus.Text = ex.Message;
        }
        finally
        {
            loadingOwGraphicEditor = false;
        }

        if (OwGraphicList.Items.Count > 0) OwGraphicList.SelectedIndex = 0;
        else ShowSelectedOwGraphic();
    }

    public sealed record OwGraphicRow(string Label, IImage? Thumbnail);

    /// <summary>A sheet is square frames stacked vertically; the list shows the third frame from the
    /// bottom (or the nearest one a shorter sheet has) as the entry's thumbnail.</summary>
    private OwGraphicRow CreateOwGraphicRow(OverworldSpriteEntry entry)
    {
        IImage? thumbnail = null;
        try
        {
            if (currentProject is not null && OverworldSpriteService.LoadSheet(currentProject, entry.SpriteId, 0) is OverworldSpriteSheet sheet)
            {
                int frameSize = sheet.Width;
                int frameCount = sheet.Height / frameSize;
                if (frameCount > 0)
                {
                    int frame = Math.Max(0, frameCount - 3);
                    byte[] rgba = new byte[frameSize * frameSize * 4];
                    Array.Copy(sheet.Rgba, frame * frameSize * frameSize * 4, rgba, 0, rgba.Length);
                    thumbnail = CreateBitmapFromRgba(rgba, frameSize, frameSize);
                }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Thumbnail of overworld sprite {entry.SpriteId:D4} could not be made: {ex.Message}");
        }

        return new OwGraphicRow($"OW Entry {entry.EntryId} - sprite {entry.SpriteId:D4}", thumbnail);
    }

    private OverworldSpriteEntry? SelectedOwGraphicEntry =>
        OwGraphicList.SelectedIndex >= 0 && OwGraphicList.SelectedIndex < owGraphicEntries.Count ? owGraphicEntries[OwGraphicList.SelectedIndex] : null;

    private void OwGraphicList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingOwGraphicEditor) return;
        // The shiny choice belongs to one sheet; most sheets have no second palette at all.
        loadingOwGraphicEditor = true;
        OwGraphicShinyCheck.IsChecked = false;
        loadingOwGraphicEditor = false;
        ShowSelectedOwGraphic();
        UpdateOwExpansionStatus();
    }

    private void ShowSelectedOwGraphic()
    {
        OverworldSpriteEntry? entry = SelectedOwGraphicEntry;
        owGraphicSheet = null;
        string? message = null;
        if (currentProject is not null && entry is not null)
        {
            try
            {
                int paletteIndex = OwGraphicShinyCheck.IsChecked == true ? 1 : 0;
                owGraphicSheet = OverworldSpriteService.LoadSheet(currentProject, entry.SpriteId, paletteIndex);
                message = owGraphicSheet is null
                    ? File.Exists(OverworldSpriteService.GetSpritePath(currentProject, entry.SpriteId))
                        ? $"OW Entry {entry.EntryId}: sprite file {entry.SpriteId:D4} is not a supported 16-colour sprite sheet (it may be a 3D model)."
                        : $"OW Entry {entry.EntryId}: sprite file {entry.SpriteId:D4} does not exist."
                    : $"OW Entry {entry.EntryId}: sprite {entry.SpriteId:D4}, {owGraphicSheet.Width}x{owGraphicSheet.Height}, {owGraphicSheet.ColorCount} colours, {owGraphicSheet.PaletteCount} palette(s).";
            }
            catch (Exception ex)
            {
                LogUiError("Overworld sprite could not be read", ex);
                message = ex.Message;
            }
        }

        loadingOwGraphicEditor = true;
        try
        {
            OwGraphicShinyCheck.IsEnabled = owGraphicSheet?.HasShinyPalette == true;
            OwGraphicExportButton.IsEnabled = OwGraphicImportButton.IsEnabled = owGraphicSheet is not null;
            OwGraphicImage.Source = owGraphicSheet is null ? null : CreateBitmapFromRgba(owGraphicSheet.Rgba, owGraphicSheet.Width, owGraphicSheet.Height);
            ApplyOwGraphicZoom();
            ShowOwGraphicRenderState(entry);
        }
        finally
        {
            loadingOwGraphicEditor = false;
        }

        if (message is not null) OwGraphicStatus.Text = message;
    }

    private void ApplyOwGraphicZoom()
    {
        if (OwGraphicImage is null || OwGraphicZoomSlider is null) return;
        double zoom = Math.Max(1, OwGraphicZoomSlider.Value);
        OwGraphicImage.Width = (owGraphicSheet?.Width ?? 0) * zoom;
        OwGraphicImage.Height = (owGraphicSheet?.Height ?? 0) * zoom;
        OwGraphicGrid?.SetZoom(zoom);
    }

    private void OwGraphicGridCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (OwGraphicGrid is not null) OwGraphicGrid.IsVisible = OwGraphicGridCheck.IsChecked == true;
    }

    private void OwGraphicZoomSlider_ValueChanged(object? sender, global::Avalonia.Controls.Primitives.RangeBaseValueChangedEventArgs e) => ApplyOwGraphicZoom();

    private void OwGraphicShinyCheck_Changed(object? sender, RoutedEventArgs e)
    {
        if (loadingOwGraphicEditor) return;
        ShowSelectedOwGraphic();
    }

    private void ShowOwGraphicRenderState(OverworldSpriteEntry? entry)
    {
        OverworldRenderState? state = null;
        if (entry is not null && currentProject is not null && currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) && metadata is not null)
        {
            try
            {
                state = OverworldSpriteService.LoadRenderState(currentProject, metadata, entry.EntryId);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DSPRE] Render state of overworld entry {entry.EntryId} could not be read: {ex.Message}");
            }
        }

        foreach ((ComboBox combo, int? value) in new (ComboBox, int?)[]
        {
            (OwGraphicDrawType, state?.DrawType), (OwGraphicShadowType, state?.ShadowType),
            (OwGraphicFootmarkType, state?.FootmarkType), (OwGraphicReflectType, state?.ReflectType)
        })
        {
            combo.IsEnabled = state is not null;
            combo.SelectedIndex = value is int index && index < combo.ItemCount ? index : -1;
        }
    }

    /// <summary>Written straight away on change, as in legacy. A field showing no selection holds a
    /// value this editor has no name for, so nothing is written until all four are set.</summary>
    private void OwGraphicRenderState_Changed(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingOwGraphicEditor || currentProject is null || SelectedOwGraphicEntry is not OverworldSpriteEntry entry) return;
        if (!currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        if (OwGraphicDrawType.SelectedIndex < 0 || OwGraphicShadowType.SelectedIndex < 0 || OwGraphicFootmarkType.SelectedIndex < 0 || OwGraphicReflectType.SelectedIndex < 0) return;

        try
        {
            OverworldSpriteService.SaveRenderState(currentProject, metadata, entry.EntryId, new OverworldRenderState
            {
                DrawType = OwGraphicDrawType.SelectedIndex,
                ShadowType = OwGraphicShadowType.SelectedIndex,
                FootmarkType = OwGraphicFootmarkType.SelectedIndex,
                ReflectType = OwGraphicReflectType.SelectedIndex
            });
            OwGraphicStatus.Text = $"Render state of OW Entry {entry.EntryId} saved.";
        }
        catch (Exception ex)
        {
            LogUiError("Overworld render state could not be saved", ex);
            OwGraphicStatus.Text = ex.Message;
        }
    }

    private async void OwGraphicExport_Click(object? sender, RoutedEventArgs e)
    {
        if (owGraphicSheet is null || SelectedOwGraphicEntry is not OverworldSpriteEntry entry) return;
        OverworldSpriteSheet sheet = owGraphicSheet;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export overworld sprite sheet",
            SuggestedFileName = $"ow_{entry.EntryId:D4}.png",
            FileTypeChoices = [new FilePickerFileType("PNG") { Patterns = ["*.png"] }]
        });
        if (file is null) return;

        try
        {
            CreateBitmapFromRgba(sheet.Rgba, sheet.Width, sheet.Height)?.Save(file.Path.LocalPath);
            OwGraphicStatus.Text = "Sprite sheet exported.";
        }
        catch (Exception ex)
        {
            LogUiError("Overworld sprite could not be exported", ex);
            OwGraphicStatus.Text = ex.Message;
        }
    }

    private async void OwGraphicImport_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || owGraphicSheet is null || SelectedOwGraphicEntry is not OverworldSpriteEntry entry) return;
        int paletteIndex = OwGraphicShinyCheck.IsChecked == true ? 1 : 0;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Import overworld sprite sheet",
            SuggestedStartLocation = await ImportStartFolderAsync(),
            FileTypeFilter = [new FilePickerFileType("PNG") { Patterns = ["*.png"] }]
        });
        if (files.Count != 1) return;

        try
        {
            OverworldSpriteService.ImportImage(currentProject, entry.SpriteId, paletteIndex, files[0].Path.LocalPath);

            // Every entry drawing this sprite file gets a fresh thumbnail; replacing a row drops the
            // list's selection, so it is put back.
            int selected = OwGraphicList.SelectedIndex;
            loadingOwGraphicEditor = true;
            try
            {
                for (int i = 0; i < owGraphicEntries.Count; i++)
                {
                    if (owGraphicEntries[i].SpriteId == entry.SpriteId) OwGraphicList.Items[i] = CreateOwGraphicRow(owGraphicEntries[i]);
                }

                OwGraphicList.SelectedIndex = selected;
            }
            finally
            {
                loadingOwGraphicEditor = false;
            }

            ShowSelectedOwGraphic();
            OwGraphicStatus.Text = $"OW Entry {entry.EntryId} imported and saved to sprite file {entry.SpriteId:D4}.";
        }
        catch (Exception ex)
        {
            LogUiError("Overworld sprite could not be imported", ex);
            OwGraphicStatus.Text = ex.Message;
        }
    }

    #endregion

    #region TM Editor

    private List<MachineEntry> tmMachines = [];
    private List<string> tmMoveNames = [];
    private readonly List<PokemonType> tmPaletteTypes = [];
    private RomProject? tmEditorProject;
    private bool loadingTmEditor;

    /// <summary>Ported from the legacy DS_Map/Editors/TMEditor.cs. Loaded once per project so unsaved
    /// edits survive switching editors; "Reload From ROM" discards them.</summary>
    private void ConfigureTmEditor()
    {
        if (currentProject is null || ReferenceEquals(tmEditorProject, currentProject)) return;
        LoadTmEditor();
    }

    private void LoadTmEditor()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        tmEditorProject = currentProject;

        loadingTmEditor = true;
        try
        {
            tmMoveNames = LoadTableEditorNames(TrainerService.GetAttackNamesArchiveId(metadata), metadata);
            List<string> typeNames = LoadTableEditorNames(PersonalDataService.GetTypeNamesArchiveId(metadata), metadata);

            TmMoveSelector.Items.Clear();
            for (int i = 0; i < tmMoveNames.Count; i++) TmMoveSelector.Items.Add($"[{i:D3}] {tmMoveNames[i]}");

            tmPaletteTypes.Clear();
            TmPaletteTypeSelector.Items.Clear();
            foreach (PokemonType type in Enum.GetValues<PokemonType>())
            {
                // The "???" type has no icon palette of its own.
                if (type == PokemonType.Unknown) continue;
                tmPaletteTypes.Add(type);
                TmPaletteTypeSelector.Items.Add((int)type < typeNames.Count ? typeNames[(int)type] : type.ToString());
            }

            tmMachines = MachineMoveService.Load(currentProject, metadata);
            tmEdits.Loaded(tmMachines, "TM/HM table");
            RefreshTmMachineList(0);
            TmEditorStatus.Text = $"{tmMachines.Count} TMs/HMs loaded.";
        }
        catch (Exception ex)
        {
            LogUiError("TM/HM data could not be loaded", ex);
            tmMachines = [];
            TmMachineList.Items.Clear();
            TmEditorStatus.Text = ex.Message;
        }
        finally
        {
            loadingTmEditor = false;
        }

        ShowSelectedTmMachine();
    }

    private string DescribeTmMove(int moveId) => moveId < tmMoveNames.Count ? tmMoveNames[moveId] : $"UNK_{moveId}";

    private string DescribeTmMachine(int index) =>
        $"{PersonalDataService.MachineLabelFromIndex(index)} - {DescribeTmMove(tmMachines[index].MoveId)}";

    private void RefreshTmMachineList(int selectIndex)
    {
        bool wasLoading = loadingTmEditor;
        loadingTmEditor = true;
        try
        {
            TmMachineList.Items.Clear();
            for (int i = 0; i < tmMachines.Count; i++) TmMachineList.Items.Add(DescribeTmMachine(i));
            TmMachineList.SelectedIndex = tmMachines.Count > 0 ? Math.Clamp(selectIndex, 0, tmMachines.Count - 1) : -1;
        }
        finally
        {
            loadingTmEditor = wasLoading;
        }
    }

    private MachineEntry? SelectedTmMachine =>
        TmMachineList.SelectedIndex >= 0 && TmMachineList.SelectedIndex < tmMachines.Count ? tmMachines[TmMachineList.SelectedIndex] : null;

    private void ShowSelectedTmMachine()
    {
        MachineEntry? machine = SelectedTmMachine;
        bool wasLoading = loadingTmEditor;
        loadingTmEditor = true;
        try
        {
            TmMoveSelector.SelectedIndex = machine is not null && machine.MoveId < TmMoveSelector.ItemCount ? machine.MoveId : -1;
            TmPaletteIdInput.Value = machine?.PaletteId ?? 0;
            TmPaletteTypeSelector.SelectedIndex = machine is not null && MachineMoveService.TypeForPalette(machine.PaletteId) is PokemonType type
                ? tmPaletteTypes.IndexOf(type)
                : -1;
            RenderTmIcon(machine);
        }
        finally
        {
            loadingTmEditor = wasLoading;
        }
    }

    /// <summary>The machine's bag icon: its item's icon (TMs/HMs are items 328 onwards, as in
    /// MachineMoveService), drawn with the palette set here.</summary>
    private void RenderTmIcon(MachineEntry? machine)
    {
        TmIconImage.Source = null;
        if (machine is null || currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        try
        {
            int itemId = MachineMoveService.FirstMachineItemId + tmMachines.IndexOf(machine);
            ItemTableEntry entry = ItemDataService.ReadTableEntry(currentProject, metadata, itemId);
            TmIconImage.Source = CreateBitmapFromRgba(SpriteService.GetItemIconRgba(currentProject, entry.ItemIconId, machine.PaletteId), 32, 32);
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or ArgumentOutOfRangeException)
        {
            TmEditorStatus.Text = $"The icon could not be drawn: {ex.Message}";
        }
    }

    private void TmMachineList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingTmEditor) return;
        ShowSelectedTmMachine();
    }

    private void TmMoveSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingTmEditor || SelectedTmMachine is not MachineEntry machine || TmMoveSelector.SelectedIndex < 0) return;
        machine.MoveId = (ushort)TmMoveSelector.SelectedIndex;
        RefreshTmMachineList(TmMachineList.SelectedIndex);
        ReportEdit(tmEdits, () => tmMachines, TmEditorStatus);
    }

    private void TmPaletteTypeSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingTmEditor || SelectedTmMachine is not MachineEntry machine || TmPaletteTypeSelector.SelectedIndex < 0) return;
        machine.PaletteId = MachineMoveService.PaletteForType(tmPaletteTypes[TmPaletteTypeSelector.SelectedIndex]);
        ShowSelectedTmMachine();
        ReportEdit(tmEdits, () => tmMachines, TmEditorStatus);
    }

    private void TmPaletteIdInput_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (loadingTmEditor || SelectedTmMachine is not MachineEntry machine) return;
        machine.PaletteId = (ushort)(TmPaletteIdInput.Value ?? 0);
        ShowSelectedTmMachine();
        ReportEdit(tmEdits, () => tmMachines, TmEditorStatus);
    }

    private ushort TmPaletteForMove(int moveId)
    {
        try
        {
            return MachineMoveService.PaletteForType(MoveDataService.Load(currentProject!, moveId).MoveType);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Move {moveId}'s type could not be read for its TM palette: {ex.Message}");
            return MachineMoveService.PaletteForType(PokemonType.Normal);
        }
    }

    private void TmAutoPalette_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || SelectedTmMachine is not MachineEntry machine) return;
        machine.PaletteId = TmPaletteForMove(machine.MoveId);
        ShowSelectedTmMachine();
        ReportEdit(tmEdits, () => tmMachines, TmEditorStatus);
    }

    private void TmAutoPaletteAll_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || tmMachines.Count == 0) return;
        foreach (MachineEntry machine in tmMachines) machine.PaletteId = TmPaletteForMove(machine.MoveId);
        ShowSelectedTmMachine();
        if (tmEdits.DescribeLatestChange(tmMachines) is string change) TmEditorStatus.Text = $"Every palette was set from its move's type. {change}";
        else TmEditorStatus.Text = "Every palette already matched its move's type; nothing changed.";
    }

    private void ReloadTmEditor_Click(object? sender, RoutedEventArgs e) => LoadTmEditor();

    private void SaveTmEditor_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || tmMachines.Count == 0) return;
        if (!currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        try
        {
            MachineMoveService.Save(currentProject, metadata, tmMachines);
            tmEdits.Saved(tmMachines);
            TmEditorStatus.Text = "TMs/HMs saved.";
        }
        catch (Exception ex)
        {
            LogUiError("TM/HM data could not be saved", ex);
            TmEditorStatus.Text = ex.Message;
        }
    }

    private async void ExportTmEditor_Click(object? sender, RoutedEventArgs e)
    {
        if (tmMachines.Count == 0) return;
        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Export Machine Data",
            SuggestedFileName = "machines.csv",
            FileTypeChoices = [new FilePickerFileType("CSV") { Patterns = ["*.csv"] }]
        });
        if (file is null) return;

        try
        {
            var lines = new List<string> { "Machine,Move ID,Move Name,Palette ID" };
            for (int i = 0; i < tmMachines.Count; i++)
            {
                lines.Add($"{PersonalDataService.MachineLabelFromIndex(i)},{tmMachines[i].MoveId},{DescribeTmMove(tmMachines[i].MoveId)},{tmMachines[i].PaletteId}");
            }

            File.WriteAllLines(file.Path.LocalPath, lines);
            TmEditorStatus.Text = "Machine data exported.";
        }
        catch (Exception ex)
        {
            LogUiError("Machine data could not be exported", ex);
            TmEditorStatus.Text = ex.Message;
        }
    }

    private async void ImportTmEditor_Click(object? sender, RoutedEventArgs e)
    {
        if (tmMachines.Count == 0) return;
        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Import Machine Data",
            FileTypeFilter = [new FilePickerFileType("CSV") { Patterns = ["*.csv", "*"] }]
        });
        if (files.Count != 1) return;

        try
        {
            // Parsed into a copy first so a bad row leaves the current data untouched.
            List<MachineEntry> imported = tmMachines.Select(machine => new MachineEntry { MoveId = machine.MoveId, PaletteId = machine.PaletteId }).ToList();
            int applied = 0;
            foreach (string line in File.ReadAllLines(files[0].Path.LocalPath).Skip(1))
            {
                string[] parts = line.Split(',');
                if (parts.Length < 4) continue;

                string label = parts[0].Trim();
                int index = label.StartsWith("TM", StringComparison.OrdinalIgnoreCase) ? int.Parse(label[2..]) - 1
                    : label.StartsWith("HM", StringComparison.OrdinalIgnoreCase) ? int.Parse(label[2..]) + PersonalDataService.TmsCount - 1
                    : -1;
                if (index < 0 || index >= imported.Count) continue;

                imported[index].MoveId = ushort.Parse(parts[1].Trim());
                imported[index].PaletteId = ushort.Parse(parts[^1].Trim());
                applied++;
            }

            tmMachines = imported;
            RefreshTmMachineList(TmMachineList.SelectedIndex);
            ShowSelectedTmMachine();
            TmEditorStatus.Text = tmEdits.DescribeLatestChange(tmMachines) is string change
                ? $"Imported {applied} machines. {change}"
                : $"Imported {applied} machines; they already matched the table, so nothing changed.";
        }
        catch (Exception ex)
        {
            LogUiError("Machine data could not be imported", ex);
            TmEditorStatus.Text = $"Import failed: {ex.Message}";
        }
    }

    #endregion

    #region Egg Move Editor

    private List<EggMoveEntry> eggMoveEntries = [];
    private List<string> eggSpeciesNames = [];
    private List<string> eggMoveNames = [];
    private EggMoveLimits eggMoveLimits = new(0, 16);
    private RomProject? eggMoveEditorProject;
    private bool loadingEggMoveEditor;

    /// <summary>The Pokemon Editor's Egg Moves tab, ported from the legacy DS_Map/Editors/EggMoveEditor.cs
    /// (without its search, bulk replace and CSV tools). It always shows the Pokemon picked at the top of
    /// the Pokemon Editor. Loaded once per project so unsaved edits survive switching editors.</summary>
    private void ConfigureEggMoveEditor()
    {
        if (currentProject is null || ReferenceEquals(eggMoveEditorProject, currentProject)) return;
        if (!currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        eggMoveEditorProject = currentProject;

        loadingEggMoveEditor = true;
        try
        {
            eggSpeciesNames = LoadTableEditorNames(TrainerService.GetPokemonNamesArchiveId(metadata), metadata);
            eggMoveNames = LoadTableEditorNames(TrainerService.GetAttackNamesArchiveId(metadata), metadata);
            eggMoveLimits = EggMoveService.GetLimits(metadata);

            EggMoveSelector.Items.Clear();
            for (int i = 0; i < eggMoveNames.Count; i++) EggMoveSelector.Items.Add($"[{i:D3}] {eggMoveNames[i]}");

            eggMoveEntries = EggMoveService.Load(currentProject, metadata);
            eggMoveEdits.Loaded(eggMoveEntries, "Egg move list");
            EggMoveEditorStatus.Text = "Egg moves loaded.";
        }
        catch (Exception ex)
        {
            LogUiError("Egg move data could not be loaded", ex);
            eggMoveEntries = [];
            EggMoveEditorStatus.Text = ex.Message;
        }
        finally
        {
            loadingEggMoveEditor = false;
        }
    }

    private string DescribeEggSpecies(int speciesId) =>
        $"[{speciesId:D3}] {(speciesId >= 0 && speciesId < eggSpeciesNames.Count ? eggSpeciesNames[speciesId] : "UNKNOWN")}";

    private string DescribeEggMove(int moveId) =>
        $"[{moveId:D3}] {(moveId < eggMoveNames.Count ? eggMoveNames[moveId] : "UNKNOWN")}";

    private int EggMoveSpecies => PokeEditorSpeciesSelector.SelectedIndex;

    private EggMoveEntry? SelectedEggEntry => eggMoveEntries.FirstOrDefault(entry => entry.SpeciesId == EggMoveSpecies);

    /// <summary>Shows the egg moves of the Pokemon picked at the top of the Pokemon Editor.</summary>
    private void ShowEggMovesForSelectedSpecies(int selectMoveIndex = 0)
    {
        EggMoveEntry? entry = SelectedEggEntry;
        EggMoveSpeciesTitle.Text = EggMoveSpecies < 0
            ? "No Pokemon selected"
            : $"Egg moves of {DescribeEggSpecies(EggMoveSpecies)}{(entry is null ? " (none yet)" : "")}";

        loadingEggMoveEditor = true;
        try
        {
            EggMoveList.Items.Clear();
            if (entry is not null)
            {
                foreach (ushort move in entry.MoveIds) EggMoveList.Items.Add(DescribeEggMove(move));
                EggMoveList.SelectedIndex = entry.MoveIds.Count > 0 ? Math.Clamp(selectMoveIndex, 0, entry.MoveIds.Count - 1) : -1;
            }
        }
        finally
        {
            loadingEggMoveEditor = false;
        }

        SyncEggMoveSelector();
        UpdateEggMoveLabels();
    }

    private void SyncEggMoveSelector()
    {
        if (SelectedEggEntry is EggMoveEntry entry && EggMoveList.SelectedIndex >= 0 && EggMoveList.SelectedIndex < entry.MoveIds.Count
            && entry.MoveIds[EggMoveList.SelectedIndex] < EggMoveSelector.ItemCount)
        {
            EggMoveSelector.SelectedIndex = entry.MoveIds[EggMoveList.SelectedIndex];
        }
    }

    private void UpdateEggMoveLabels()
    {
        IBrush normal = Foreground ?? Brushes.White;
        EggMoveEntryCount.Text = $"Pokemon with egg moves: {eggMoveEntries.Count}";

        int moveCount = SelectedEggEntry?.MoveIds.Count ?? 0;
        EggMoveMoveCount.Text = $"Moves: {moveCount} / {eggMoveLimits.MaxMovesPerSpecies}";
        EggMoveMoveCount.Foreground = moveCount > eggMoveLimits.MaxMovesPerSpecies ? Brushes.IndianRed : normal;

        int size = EggMoveService.GetSizeInBytes(eggMoveEntries);
        EggMoveListSize.Text = $"List size: {size} / {eggMoveLimits.MaxTableBytes} bytes";
        EggMoveListSize.Foreground = size > eggMoveLimits.MaxTableBytes ? Brushes.IndianRed : normal;
    }

    private void EggMoveList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingEggMoveEditor) return;
        SyncEggMoveSelector();
    }

    private void EggMoveAdd_Click(object? sender, RoutedEventArgs e)
    {
        if (EggMoveSpecies < 0 || EggMoveSelector.SelectedIndex < 0) return;
        // The Pokemon's entry in the list is created by its first egg move.
        EggMoveEntry entry = SelectedEggEntry ?? AddEggMoveEntry();
        entry.MoveIds.Add((ushort)EggMoveSelector.SelectedIndex);
        ShowEggMovesForSelectedSpecies(entry.MoveIds.Count - 1);
        ReportEdit(eggMoveEdits, () => eggMoveEntries, EggMoveEditorStatus);
    }

    private EggMoveEntry AddEggMoveEntry()
    {
        var entry = new EggMoveEntry { SpeciesId = EggMoveSpecies };
        eggMoveEntries.Add(entry);
        return entry;
    }

    private void EggMoveReplace_Click(object? sender, RoutedEventArgs e)
    {
        int index = EggMoveList.SelectedIndex;
        if (SelectedEggEntry is not EggMoveEntry entry || index < 0 || index >= entry.MoveIds.Count || EggMoveSelector.SelectedIndex < 0) return;
        entry.MoveIds[index] = (ushort)EggMoveSelector.SelectedIndex;
        ShowEggMovesForSelectedSpecies(index);
        ReportEdit(eggMoveEdits, () => eggMoveEntries, EggMoveEditorStatus);
    }

    private void EggMoveDelete_Click(object? sender, RoutedEventArgs e)
    {
        int index = EggMoveList.SelectedIndex;
        if (SelectedEggEntry is not EggMoveEntry entry || index < 0 || index >= entry.MoveIds.Count) return;
        entry.MoveIds.RemoveAt(index);
        // A Pokemon left with no egg moves is dropped from the list rather than kept as an empty entry.
        if (entry.MoveIds.Count == 0) eggMoveEntries.Remove(entry);
        ShowEggMovesForSelectedSpecies(index);
        ReportEdit(eggMoveEdits, () => eggMoveEntries, EggMoveEditorStatus);
    }

    private void SaveEggMoves_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        try
        {
            EggMoveService.Save(currentProject, metadata, eggMoveEntries);
            eggMoveEdits.Saved(eggMoveEntries);
            List<string> overfull = eggMoveEntries
                .Where(entry => entry.MoveIds.Count > eggMoveLimits.MaxMovesPerSpecies)
                .Select(entry => DescribeEggSpecies(entry.SpeciesId))
                .ToList();
            EggMoveEditorStatus.Text = overfull.Count == 0
                ? "Egg moves saved."
                : $"Egg moves saved. These have more than {eggMoveLimits.MaxMovesPerSpecies} egg moves, which may cause issues in-game: {string.Join(", ", overfull)}";
        }
        catch (Exception ex)
        {
            LogUiError("Egg move data could not be saved", ex);
            EggMoveEditorStatus.Text = ex.Message;
        }
    }

    #endregion

    #region Item Table Editor

    private PickupTable? pickupTable;
    private RomProject? itemTableEditorProject;
    private bool loadingItemTableEditor;

    /// <summary>Ported from the legacy DS_Map/Editors/ItemTableEditor.cs, which is just a host for the
    /// Pickup Table and Hidden Items editors. Loaded once per project so unsaved edits survive
    /// switching editors.</summary>
    private void ConfigureItemTableEditor()
    {
        if (currentProject is null || ReferenceEquals(itemTableEditorProject, currentProject)) return;
        if (!currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        itemTableEditorProject = currentProject;

        FillNames(ItemTableItemNames, LoadTableEditorNames(TrainerService.GetItemNamesArchiveId(metadata), metadata).Select((name, i) => $"[{i:D3}] {name}"));
        LoadPickupTable();
        LoadHiddenItems();
    }

    private void LoadPickupTable()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        loadingItemTableEditor = true;
        try
        {
            pickupTable = PickupTableService.Load(currentProject, metadata);
            pickupEdits.Loaded(pickupTable, "Pickup table");
            PickupDivisorInput.Value = pickupTable.ActivationDivisor;
            PickupWeightRows.ItemsSource = pickupTable.WeightThresholds
                .Select((_, i) => new PickupWeightRow($"Slot {i + 1}", pickupTable.WeightThresholds, i, UpdatePickupOdds)).ToList();
            PickupCommonRows.ItemsSource = pickupTable.CommonItems
                .Select((_, i) => new PickupItemRow(DescribePickupEntry(i, PickupTable.CommonSlotsPerBracket), pickupTable.CommonItems, i)).ToList();
            PickupRareRows.ItemsSource = pickupTable.RareItems
                .Select((_, i) => new PickupItemRow(DescribePickupEntry(i, PickupTable.RareSlotsPerBracket), pickupTable.RareItems, i)).ToList();
            PickupSaveButton.IsEnabled = true;
            PickupStatus.Text = "Pickup table loaded.";
        }
        catch (Exception ex)
        {
            LogUiError("Pickup table could not be loaded", ex);
            pickupTable = null;
            PickupWeightRows.ItemsSource = null;
            PickupCommonRows.ItemsSource = null;
            PickupRareRows.ItemsSource = null;
            PickupSaveButton.IsEnabled = false;
            PickupStatus.Text = ex.Message;
        }
        finally
        {
            loadingItemTableEditor = false;
        }

        UpdatePickupOdds();
    }

    /// <summary>Which level brackets read list entry index, and as which slot: bracket b reads entries
    /// b..b+slotsPerBracket-1 as its slots 1..slotsPerBracket.</summary>
    private static string DescribePickupEntry(int index, int slotsPerBracket)
    {
        int firstBracket = Math.Max(0, index - (slotsPerBracket - 1));
        int lastBracket = Math.Min(PickupTable.BracketCount - 1, index);
        int lowSlot = index - lastBracket + 1;
        int highSlot = index - firstBracket + 1;
        string slots = lowSlot == highSlot ? $"slot {lowSlot}" : $"slots {lowSlot}-{highSlot}";
        return $"Entry {index + 1:D2} - Lv {firstBracket * 10 + 1}-{(lastBracket + 1) * 10} ({slots})";
    }

    private void UpdatePickupOdds()
    {
        if (pickupTable is null)
        {
            PickupOddsText.Text = string.Empty;
            return;
        }

        double activation = 100.0 / Math.Max(1, (int)pickupTable.ActivationDivisor);
        var lines = new List<string> { $"Activation        {activation,6:F2}%" };
        int previous = 0;
        for (int i = 0; i < pickupTable.WeightThresholds.Count; i++)
        {
            int threshold = pickupTable.WeightThresholds[i];
            int range = threshold - previous;
            lines.Add(range < 0
                ? $"Slot {i + 1}            invalid (threshold below the previous slot's)"
                : $"Slot {i + 1}            {activation * range / 100.0,6:F2}%   rolls {previous}-{threshold - 1}");
            previous = threshold;
        }

        if (previous < 98) lines.Add($"No item           {activation * (98 - previous) / 100.0,6:F2}%   rolls {previous}-97");
        lines.Add($"Rare (2 slots)    {activation * 2 / 100.0,6:F2}%   rolls 98-99");
        PickupOddsText.Text = string.Join("\n", lines);
    }

    private void PickupDivisorInput_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e)
    {
        if (loadingItemTableEditor || pickupTable is null) return;
        pickupTable.ActivationDivisor = (byte)Math.Clamp((int)(PickupDivisorInput.Value ?? 1), 1, 255);
        UpdatePickupOdds();
    }

    private void ReloadPickupTable_Click(object? sender, RoutedEventArgs e) => LoadPickupTable();

    private void SavePickupTable_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || pickupTable is null) return;
        if (!currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;

        for (int i = 1; i < pickupTable.WeightThresholds.Count; i++)
        {
            if (pickupTable.WeightThresholds[i] < pickupTable.WeightThresholds[i - 1])
            {
                PickupStatus.Text = $"Slot {i + 1}'s threshold must not be below slot {i}'s.";
                return;
            }
        }

        try
        {
            PickupTableService.Save(currentProject, metadata, pickupTable);
            pickupEdits.Saved(pickupTable);
            PickupStatus.Text = "Pickup table saved.";
        }
        catch (Exception ex)
        {
            LogUiError("Pickup table could not be saved", ex);
            PickupStatus.Text = ex.Message;
        }
    }

    #endregion

    #region Table Editor

    private List<BattleEffectCombo> tableCombos = [];
    private List<BattleEffectLink> tableVsTrainers = [];
    private List<BattleEffectLink> tableVsPokemon = [];
    private List<ConditionalMusicEntry> tableConditionalMusic = [];
    private RomProject? tableEditorProject;

    /// <summary>Ported from the legacy DS_Map/Editors/TableEditor.cs. Tables are read once per project
    /// so unsaved edits survive switching editors; each table saves on its own.</summary>
    private void ConfigureTableEditor()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        if (ReferenceEquals(tableEditorProject, currentProject)) return;
        tableEditorProject = currentProject;

        TableEditorNotice.Text = metadata.HeaderFormat switch
        {
            MapHeaderFormat.HeartGoldSoulSilver => "Entry counts are fixed by the game: rows can be changed but not added or removed.",
            MapHeaderFormat.Platinum => "Platinum only has the battle effect combos table; the other tables exist in HeartGold/SoulSilver only.",
            _ => "Diamond/Pearl has none of these tables."
        };

        FillNames(TableEditorHeaderNames, Enumerable.Range(0, currentProject.GetHeaderCount())
            .Select(i => $"{i:D3} - {currentProject.GetInternalName((ushort)i)} - {FlyWarpLocationOfHeader(i)}"));
        FillNames(TableEditorTrainerClassNames, LoadTableEditorNames(TrainerService.GetTrainerClassNamesArchiveId(metadata), metadata).Select((name, i) => $"[{i:D3}] {name}"));
        FillNames(TableEditorPokemonNames, LoadTableEditorNames(TrainerService.GetPokemonNamesArchiveId(metadata), metadata).Select((name, i) => $"[{i:D3}] {name}"));

        // Combos first: the two link tables' Combo dropdowns list them.
        tableCombos = LoadTable(metadata, BattleTableKind.EffectCombos, TableCombosStatus, TableCombosSaveButton,
            () => BattleTableService.LoadEffectCombos(currentProject, metadata));
        tableComboEdits.Loaded(tableCombos, "Battle effect combos");
        RefreshTableComboNames();
        TableCombosRows.ItemsSource = tableCombos.Select((combo, i) => new TableComboRow(i, combo)).ToList();

        tableVsTrainers = LoadTable(metadata, BattleTableKind.VsTrainer, TableVsTrainerStatus, TableVsTrainerSaveButton,
            () => BattleTableService.LoadLinks(currentProject, metadata, BattleTableKind.VsTrainer));
        tableVsTrainerEdits.Loaded(tableVsTrainers, "VS. trainer class effects");
        TableVsTrainerRows.ItemsSource = tableVsTrainers.Select((link, i) => new TableLinkRow(i, link, LoadTableTrainerClassPicture)).ToList();

        tableVsPokemon = LoadTable(metadata, BattleTableKind.VsPokemon, TableVsPokemonStatus, TableVsPokemonSaveButton,
            () => BattleTableService.LoadLinks(currentProject, metadata, BattleTableKind.VsPokemon));
        tableVsPokemonEdits.Loaded(tableVsPokemon, "VS. wild Pokemon effects");
        TableVsPokemonRows.ItemsSource = tableVsPokemon.Select((link, i) => new TableLinkRow(i, link, LoadTablePokemonIcon)).ToList();

        tableConditionalMusic = LoadTable(metadata, BattleTableKind.ConditionalMusic, TableConditionalMusicStatus, TableConditionalMusicSaveButton,
            () => BattleTableService.LoadConditionalMusic(currentProject, metadata));
        tableConditionalMusicEdits.Loaded(tableConditionalMusic, "Conditional music table");
        TableConditionalMusicRows.ItemsSource = tableConditionalMusic.Select((entry, i) => new TableConditionalMusicRow(i, entry)).ToList();
    }

    private IImage? LoadTableTrainerClassPicture(int trainerClassId)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return null;
        try
        {
            if (!SpriteService.TryLoadTrainerClassSprite(currentProject, metadata, trainerClassId, out NdsTileImage? tiles, out RgbColor[][]? palettes, out NdsCellBank? cells, out _)
                || tiles is null || palettes is null || cells is null)
            {
                return null;
            }

            return CreateBitmapFromRgba(SpriteService.GetTrainerClassSpriteRgba(tiles, palettes, cells, 0, 128, 96), 128, 96);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Trainer class {trainerClassId} picture could not be loaded: {ex.Message}");
            return null;
        }
    }

    private IImage? LoadTablePokemonIcon(int species)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return null;
        try
        {
            return CreateBitmapFromRgba(SpriteService.GetSpeciesIconRgba(currentProject, metadata, species, 32, 24), 32, 24);
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Pokemon {species} icon could not be loaded: {ex.Message}");
            return null;
        }
    }

    private static void FillNames(ObservableCollection<string> target, IEnumerable<string> names)
    {
        target.Clear();
        foreach (string name in names) target.Add(name);
    }

    private List<string> LoadTableEditorNames(int archiveId, RomGameMetadata metadata)
    {
        try
        {
            return TextArchiveService.Load(currentProject!, archiveId, metadata).Messages.ToList();
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[DSPRE] Text archive {archiveId} could not be loaded for the table editor: {ex.Message}");
            return [];
        }
    }

    private List<T> LoadTable<T>(RomGameMetadata metadata, BattleTableKind kind, TextBlock status, Button saveButton, Func<List<T>> load)
    {
        saveButton.IsEnabled = false;
        if (!BattleTableService.IsSupported(metadata, kind))
        {
            status.Text = "Not available in this game.";
            return [];
        }

        try
        {
            List<T> entries = load();
            status.Text = $"{entries.Count} entries loaded.";
            saveButton.IsEnabled = entries.Count > 0;
            return entries;
        }
        catch (Exception ex)
        {
            LogUiError($"Table {kind} could not be loaded", ex);
            status.Text = ex.Message;
            return [];
        }
    }

    private void RefreshTableComboNames() =>
        FillNames(TableEditorComboNames, tableCombos.Select((combo, i) => $"Combo {i:D2} - Effect #{combo.VsGraphic}, Music #{combo.BattleMusic}"));

    private void SaveTable(TextBlock status, string description, EditTracker tracker, object document, Action<RomProject, RomGameMetadata> save)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        try
        {
            save(currentProject, metadata);
            tracker.Saved(document);
            status.Text = $"{description} saved.";
        }
        catch (Exception ex)
        {
            LogUiError($"{description} could not be saved", ex);
            status.Text = ex.Message;
        }
    }

    private void SaveTableCombos_Click(object? sender, RoutedEventArgs e) =>
        SaveTable(TableCombosStatus, "Battle effect combos", tableComboEdits, tableCombos, (project, metadata) => BattleTableService.SaveEffectCombos(project, metadata, tableCombos));

    private void SaveTableVsTrainer_Click(object? sender, RoutedEventArgs e) =>
        SaveTable(TableVsTrainerStatus, "VS. trainer class effects", tableVsTrainerEdits, tableVsTrainers, (project, metadata) => BattleTableService.SaveLinks(project, metadata, BattleTableKind.VsTrainer, tableVsTrainers));

    private void SaveTableVsPokemon_Click(object? sender, RoutedEventArgs e) =>
        SaveTable(TableVsPokemonStatus, "VS. wild Pokemon effects", tableVsPokemonEdits, tableVsPokemon, (project, metadata) => BattleTableService.SaveLinks(project, metadata, BattleTableKind.VsPokemon, tableVsPokemon));

    private void SaveTableConditionalMusic_Click(object? sender, RoutedEventArgs e) =>
        SaveTable(TableConditionalMusicStatus, "Conditional music table", tableConditionalMusicEdits, tableConditionalMusic, (project, metadata) => BattleTableService.SaveConditionalMusic(project, metadata, tableConditionalMusic));

    #endregion

    #region Overlay Editor

    private sealed record OverlayRow(string Id, string Compressed, string Marked, string RamAddress, string UncompressedSize, string FileSize, IBrush Background);

    private static readonly IBrush OverlayRowBrush = new SolidColorBrush(Color.Parse("#151C23"));
    private static readonly IBrush OverlayMismatchBrush = new SolidColorBrush(Color.Parse("#5A2327"));

    /// <summary>Ported from the legacy DS_Map/Editors/OverlayEditor.cs as a read-only table. For
    /// ds-rom projects legacy is read-only too (compression is ds-rom's job at build time); its
    /// ndstool-only compression toggles are not ported.</summary>
    private void ConfigureOverlayEditor()
    {
        if (currentProject is null) return;
        bool dsRom = currentProject.Format == RomProjectFormat.DsRom;
        OverlayCompressedHeader.Text = dsRom ? "Compressed On Disk" : "Compressed";
        OverlayMarkedHeader.Text = dsRom ? "Will Compress On Build" : "Marked Compressed";
        OverlayEditorNotice.Text = dsRom
            ? "Read-only: ds-rom handles overlay compression automatically. Overlays are stored uncompressed on disk for editing; \"Will Compress On Build\" shows which ones are compressed when the ROM is saved. Those settings live in arm9_overlays/overlays.yaml."
            : "Read-only: this is an ndstool project. A row is highlighted when an overlay's actual compression state does not match its \"Marked Compressed\" flag in the overlay table, which can cause errors on hardware.";

        try
        {
            IReadOnlyList<OverlayInfo> overlays = OverlayService.GetOverlays(currentProject);
            OverlayRows.ItemsSource = overlays.Select(overlay => new OverlayRow(
                overlay.Id.ToString("D3"),
                overlay.FileSize is null ? "missing" : overlay.CompressedOnDisk ? "Yes" : "No",
                overlay.MarkedCompressed ? "Yes" : "No",
                $"0x{overlay.RamAddress:X8}",
                $"0x{overlay.UncompressedSize:X}",
                overlay.FileSize is long size ? $"0x{size:X}" : "-",
                !dsRom && overlay.FileSize is not null && overlay.CompressedOnDisk != overlay.MarkedCompressed ? OverlayMismatchBrush : OverlayRowBrush)).ToList();

            int mismatches = dsRom ? 0 : overlays.Count(overlay => overlay.FileSize is not null && overlay.CompressedOnDisk != overlay.MarkedCompressed);
            OverlayEditorStatus.Text = mismatches > 0
                ? $"{overlays.Count} overlays, {mismatches} with a compression mismatch."
                : $"{overlays.Count} overlays.";
        }
        catch (Exception ex)
        {
            LogUiError("Overlay table could not be loaded", ex);
            OverlayRows.ItemsSource = null;
            OverlayEditorStatus.Text = ex.Message;
        }
    }

    private void RefreshOverlayEditor_Click(object? sender, RoutedEventArgs e) => ConfigureOverlayEditor();

    #endregion

    #region Map previews

    /// <summary>Converts one map (with its own default texture packs) for a preview view, reusing the
    /// Map Editor's converted-model cache. Null if it cannot be converted.</summary>
    private async Task<string?> EnsureMapPreviewSceneAsync(int mapId)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return null;
        EnsureMapEditorDataLoaded();
        MapViewEntry? entry = mapEntries.FirstOrDefault(map => map.Id == mapId);
        if (entry is null) return null;
        return await EnsureMapPreviewSceneAsync(mapId, GetDefaultTexturePack(entry, metadata), GetDefaultBuildingTexturePack(entry));
    }

    private async Task<string?> EnsureMapPreviewSceneAsync(int mapId, int texturePack, int buildingPack)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return null;
        EnsureMapEditorDataLoaded();
        bool interiorBuildings = UsesInteriorBuildings(mapId, metadata);
        string modelDirectory = MapModelCache.GetModelDirectory(currentProject, mapId, texturePack, buildingPack, interiorBuildings);
        string scenePath = Path.Combine(modelDirectory, "scene.json");
        if (MapModelCache.IsFresh(currentProject, mapId, texturePack, buildingPack, scenePath)) return scenePath;

        Directory.CreateDirectory(modelDirectory);
        ToolResult result = await new RomExtractionService().ExportMapModelAsync(
            currentProject, metadata, mapId, modelDirectory, Path.Combine(AppContext.BaseDirectory, "Tools"), texturePack, buildingPack, interiorBuildings: interiorBuildings);
        return result.Succeeded && File.Exists(scenePath) ? scenePath : null;
    }

    #endregion

    #region Camera preview

    private int cameraPreviewIndex = -1;
    private int cameraPreviewMapId = -1;
    private int cameraPreviewRequest;

    private void AttachCameraPreview()
    {
        CameraRows.AddHandler(GotFocusEvent, (_, e) => PreviewCameraRowOf(e.Source), RoutingStrategies.Bubble, handledEventsToo: true);
        CameraRows.AddHandler(LostFocusEvent, (_, e) => PreviewCameraRowOf(e.Source), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>The camera being edited is the one whose row has focus; the preview follows it and is
    /// redrawn when one of its fields is committed.</summary>
    private void PreviewCameraRowOf(object? source)
    {
        if ((source as StyledElement)?.DataContext is not CameraRow row) return;
        cameraPreviewIndex = row.Index;
        ApplyCameraPreview();
    }

    private void ConfigureCameraPreviewMaps()
    {
        EnsureMapEditorDataLoaded();
        if (CameraPreviewMapSelector.ItemCount == mapEntries.Count && mapEntries.Count > 0) return;
        CameraPreviewMapSelector.Items.Clear();
        foreach (MapViewEntry map in mapEntries) CameraPreviewMapSelector.Items.Add(map.ToString());
        int preferred = MapSelector.SelectedIndex >= 0 ? MapSelector.SelectedIndex : 0;
        if (CameraPreviewMapSelector.ItemCount > 0) CameraPreviewMapSelector.SelectedIndex = Math.Min(preferred, CameraPreviewMapSelector.ItemCount - 1);
    }

    private async void CameraPreviewMapSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        int index = CameraPreviewMapSelector.SelectedIndex;
        if (index < 0 || index >= mapEntries.Count) return;
        int mapId = mapEntries[index].Id;
        int request = ++cameraPreviewRequest;
        CameraPreviewViewer.ClearScene(keepEventIcons: true);
        CameraPreviewPlaceholder.Text = "Converting the preview map...";
        CameraPreviewPlaceholder.IsVisible = true;
        string? scene = await EnsureMapPreviewSceneAsync(mapId);
        if (request != cameraPreviewRequest) return;
        if (scene is null)
        {
            CameraPreviewPlaceholder.Text = $"Map {mapId:D4} could not be converted.";
            return;
        }

        try
        {
            ConfigureBuildingAnimations(CameraPreviewViewer, InteriorBuildingsFor(mapId));
            CameraPreviewViewer.LoadScene(scene);
            ApplyMapAnimation(CameraPreviewViewer, DynamicTypeOfMap(mapId));
            cameraPreviewMapId = mapId;
            CameraPreviewPlaceholder.IsVisible = false;
            ApplyCameraPreview();
        }
        catch (Exception ex)
        {
            LogUiError("Camera preview map could not be loaded", ex);
            CameraPreviewPlaceholder.Text = ex.Message;
        }
    }

    private void CameraPreviewReset_Click(object? sender, RoutedEventArgs e) => ApplyCameraPreview();

    /// <summary>Game cameras store their angles in 1/65536ths of a turn, the vertical one negative for
    /// looking down; the distance is in world units (16 per tile) and the field of view is half the
    /// vertical angle - read that way, the stock cameras frame about the 16 x 12 tiles the DS shows.</summary>
    private void ApplyCameraPreview()
    {
        if (currentCameraTable is null || cameraPreviewIndex < 0 || cameraPreviewIndex >= currentCameraTable.Cameras.Count || cameraPreviewMapId < 0) return;
        GameCameraRecord camera = currentCameraTable.Cameras[cameraPreviewIndex];
        float pitchDown = -camera.VerticalRotation * 360f / 65536f;
        float yaw = camera.HorizontalRotation * 360f / 65536f;
        float distance = camera.Distance / 4096f;
        float fieldOfView = 2f * camera.Fov * 360f / 65536f;
        CameraPreviewViewer.SetGameCamera(pitchDown, yaw, distance, fieldOfView, camera.IsOrthographic);
        CameraPreviewPlaceholder.IsVisible = false;
        string name = DescribeCameraAngle(cameraPreviewIndex);
        CameraPreviewDescription.Text =
            $"Camera {cameraPreviewIndex}{(name.Length > 0 ? $" ({name})" : "")}: looking down {pitchDown:F1}°, turned {yaw:F1}°, " +
            $"{distance:F0} units ({distance / 16f:F1} tiles) away, {fieldOfView:F1}° field of view{(camera.IsOrthographic ? ", orthographic" : "")}.";
    }

    #endregion

    #region Tile previews

    /// <summary>What a preview view last loaded, so a change of tile alone only moves the marker.</summary>
    private sealed class TilePreviewState
    {
        public int MapId = -1;
        public int Request;
    }

    private readonly TilePreviewState spawnPreviewState = new();
    private readonly TilePreviewState flyWarpPreviewState = new();

    /// <summary>Shows the map in a header's matrix square with the player's overworld sprite (overworld
    /// table entry 0) standing on the given tile, facing the given way (0 up, 1 down, 2 left, 3 right).
    /// Returns the map shown, or null with the reason in the placeholder.</summary>
    private async Task<int?> ShowTilePreviewAsync(
        MapGlView viewer, TextBlock placeholder, TilePreviewState state,
        int headerId, int matrixX, int matrixY, int localX, int localY, int orientation)
    {
        if (currentProject is null || headerStore is null) return null;
        int request = ++state.Request;

        int mapId;
        try
        {
            MapHeaderRecord header = headerStore.Load((ushort)headerId);
            MatrixDocument matrix = MatrixService.Load(currentProject, header.MatrixId);
            mapId = matrixY >= 0 && matrixX >= 0 && matrixY < matrix.Height && matrixX < matrix.Width ? matrix.Maps[matrixY, matrixX] : MatrixDocument.EmptyMap;
        }
        catch (Exception ex)
        {
            placeholder.Text = ex.Message;
            placeholder.IsVisible = true;
            return null;
        }

        if (mapId == MatrixDocument.EmptyMap)
        {
            state.MapId = -1;
            placeholder.Text = $"Matrix square ({matrixX}, {matrixY}) of header {headerId} has no map.";
            placeholder.IsVisible = true;
            return null;
        }

        if (mapId != state.MapId)
        {
            viewer.ClearScene(keepEventIcons: true);
            placeholder.Text = "Converting the map...";
            placeholder.IsVisible = true;
            string? scene = await EnsureMapPreviewSceneAsync(mapId);
            if (request != state.Request) return null;
            if (scene is null)
            {
                placeholder.Text = $"Map {mapId:D4} could not be converted.";
                return null;
            }

            try
            {
                ConfigureBuildingAnimations(viewer, InteriorBuildingsFor(mapId));
                viewer.LoadScene(scene);
                ApplyMapAnimation(viewer, DynamicTypeOfMap(mapId));
                viewer.ResetView();
                state.MapId = mapId;
            }
            catch (Exception ex)
            {
                LogUiError("Preview map could not be loaded", ex);
                placeholder.Text = ex.Message;
                return null;
            }
        }

        string iconKey = await EnsureOverworldIconAsync(viewer, 0, orientation);
        if (request != state.Request) return null;

        // Same tile-to-world placement as the Map Editor's event markers.
        var tileCorner = new OpenTK.Mathematics.Vector3(
            (localX - EventGridSize / 2f) * EventUnitsPerGridCell, 0f, (localY - EventGridSize / 2f) * EventUnitsPerGridCell);
        viewer.SetEventMarkers([EventMarker(viewer, tileCorner, iconKey, new OpenTK.Mathematics.Vector3(0.95f, 0.2f, 0.2f), upright: true)]);
        placeholder.IsVisible = false;
        return mapId;
    }

    /// <summary>Registers an overworld sprite on a view (each view keeps its own icon textures), using
    /// the same exported-PNG cache as the Map Editor's event markers. Returns the icon key; an icon
    /// that could not be made leaves the marker drawn as a plain coloured block.</summary>
    private async Task<string> EnsureOverworldIconAsync(MapGlView viewer, int overlayTableEntry, int orientation)
    {
        string key = $"ow:{overlayTableEntry}:{orientation}";
        if (viewer.HasEventIcon(key) || currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return key;

        string cachePath = Path.Combine(currentProject.RootPath, "expanded", "overworld_sprites", $"{overlayTableEntry:D4}_{orientation}.png");
        try
        {
            if (!File.Exists(cachePath))
            {
                Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);
                await new RomExtractionService().ExportOverworldSpriteAsync(
                    currentProject, metadata, overlayTableEntry, orientation, cachePath, Path.Combine(AppContext.BaseDirectory, "Tools"));
            }

            if (File.Exists(cachePath)) viewer.RegisterEventIcon(key, await File.ReadAllBytesAsync(cachePath));
        }
        catch (Exception ex)
        {
            LogUiError("The player's overworld sprite could not be exported", ex);
        }

        return key;
    }

    /// <summary>Shows the map the spawn point is on (the header's matrix square at the chosen matrix X/Y)
    /// with the player standing on the chosen tile, facing the chosen way.</summary>
    private async void RefreshSpawnPreview()
    {
        if (SpawnHeaderSelector.SelectedIndex < 0) return;
        int localX = (int)(SpawnLocalX.Value ?? 0);
        int localY = (int)(SpawnLocalY.Value ?? 0);
        int direction = Math.Max(0, SpawnDirectionSelector.SelectedIndex);
        int? mapId = await ShowTilePreviewAsync(
            SpawnPreviewViewer, SpawnPreviewPlaceholder, spawnPreviewState, SpawnHeaderSelector.SelectedIndex,
            (int)(SpawnMatrixX.Value ?? 0), (int)(SpawnMatrixY.Value ?? 0), localX, localY, direction);
        SpawnPreviewDescription.Text = mapId is int shown
            ? $"The player starts on map {shown:D4} at tile ({localX}, {localY}), facing {(SpawnDirectionSelector.SelectedItem as ComboBoxItem)?.Content ?? "?"}."
            : string.Empty;
    }

    private int flyWarpPreviewSlot = -1;

    private void AttachFlyWarpPreview()
    {
        FlyWarpRows.AddHandler(GotFocusEvent, (_, e) => PreviewFlyWarpRowOf(e.Source), RoutingStrategies.Bubble, handledEventsToo: true);
        FlyWarpRows.AddHandler(LostFocusEvent, (_, e) => PreviewFlyWarpRowOf(e.Source), RoutingStrategies.Bubble, handledEventsToo: true);
    }

    /// <summary>The slot being edited is the one whose row has focus.</summary>
    private void PreviewFlyWarpRowOf(object? source)
    {
        if ((source as StyledElement)?.DataContext is not FlyWarpRow row) return;
        flyWarpPreviewSlot = row.Index;
        RefreshFlyWarpPreview();
    }

    /// <summary>A fly destination is a header plus a tile position across that header's whole matrix
    /// (32 tiles per matrix square); the player lands facing down.</summary>
    private async void RefreshFlyWarpPreview()
    {
        if (flyWarpPreviewSlot < 0 || flyWarpPreviewSlot >= flyWarpEntries.Count) return;
        FlyWarpEntry entry = flyWarpEntries[flyWarpPreviewSlot];
        const int tilesPerMap = 32;
        int? mapId = await ShowTilePreviewAsync(
            FlyWarpPreviewViewer, FlyWarpPreviewPlaceholder, flyWarpPreviewState, entry.HeaderIdFly,
            entry.GlobalX / tilesPerMap, entry.GlobalY / tilesPerMap, entry.GlobalX % tilesPerMap, entry.GlobalY % tilesPerMap, 1);
        FlyWarpPreviewDescription.Text = mapId is int shown
            ? $"Slot {flyWarpPreviewSlot}: flying to {FlyWarpLocationOfHeader(entry.HeaderIdFly)} lands on map {shown:D4} at tile ({entry.GlobalX % tilesPerMap}, {entry.GlobalY % tilesPerMap})."
            : $"Slot {flyWarpPreviewSlot}: {FlyWarpLocationOfHeader(entry.HeaderIdFly)}.";
    }

    #endregion

    #region Spawn Editor

    private bool loadingSpawnEditor;
    private bool spawnEditorDirty;

    /// <summary>Ported from the legacy DS_Map/Editors/SpawnEditor.cs. Unsaved edits survive switching
    /// to another editor and back; "Reload From ROM" is what discards them.</summary>
    private void ConfigureSpawnEditor()
    {
        if (currentProject is null) return;
        if (spawnEditorDirty && SpawnHeaderSelector.Items.Count == currentProject.GetHeaderCount()) return;
        LoadSpawnSettings();
    }

    private void LoadSpawnSettings()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;

        loadingSpawnEditor = true;
        try
        {
            SpawnHeaderSelector.Items.Clear();
            int headerCount = currentProject.GetHeaderCount();
            for (int i = 0; i < headerCount; i++)
            {
                SpawnHeaderSelector.Items.Add($"{i:D3} - {currentProject.GetInternalName((ushort)i)}");
            }

            SpawnSettings settings = SpawnSettingsService.Load(currentProject, metadata);
            SpawnHeaderSelector.SelectedIndex = settings.HeaderId < headerCount ? settings.HeaderId : -1;
            UpdateSpawnHeaderDetails();

            // Same clamping as legacy: a matrix cell past the header's own matrix is pulled back to
            // its last row/column rather than rejected.
            SpawnMatrixX.Value = Math.Min(settings.GlobalX / SpawnSettings.MapTiles, SpawnMatrixX.Maximum);
            SpawnMatrixY.Value = Math.Min(settings.GlobalY / SpawnSettings.MapTiles, SpawnMatrixY.Maximum);
            SpawnLocalX.Value = settings.GlobalX % SpawnSettings.MapTiles;
            SpawnLocalY.Value = settings.GlobalY % SpawnSettings.MapTiles;
            SpawnDirectionSelector.SelectedIndex = settings.Direction < SpawnDirectionSelector.ItemCount ? settings.Direction : -1;
            SpawnInitialMoney.Value = settings.InitialMoney;

            spawnEditorDirty = false;
            spawnEdits.Loaded(ReadSpawnSettingsFromFields(), "Spawn settings");
            RefreshSpawnPreview();
            SpawnEditorStatus.Text = SpawnHeaderSelector.SelectedIndex < 0
                ? $"Spawn settings loaded, but the ROM's spawn header ({settings.HeaderId}) does not exist. Pick a header and save."
                : "Spawn settings loaded.";
        }
        catch (Exception ex)
        {
            LogUiError("Spawn settings could not be loaded", ex);
            SpawnEditorStatus.Text = ex.Message;
        }
        finally
        {
            loadingSpawnEditor = false;
        }
    }

    /// <summary>The matrix cell inputs are bounded by the selected header's own matrix, exactly as
    /// legacy's spawnHeaderComboBox_IndexChanged does.</summary>
    private void UpdateSpawnHeaderDetails()
    {
        SpawnLocationLabel.Text = string.Empty;
        if (currentProject is null || headerStore is null || SpawnHeaderSelector.SelectedIndex < 0) return;
        if (!currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;

        try
        {
            MapHeaderRecord header = headerStore.Load((ushort)SpawnHeaderSelector.SelectedIndex);
            MatrixDocument matrix = MatrixService.Load(currentProject, header.MatrixId);
            SpawnMatrixX.Maximum = Math.Max(matrix.Width - 1, 0);
            SpawnMatrixY.Maximum = Math.Max(matrix.Height - 1, 0);

            string location = currentProject.GetLocationNames(metadata).FirstOrDefault(option => option.Id == header.LocationNameId)?.Label ?? "Unknown location";
            SpawnLocationLabel.Text = $"{location} - matrix {header.MatrixId:D4} ({matrix.Width}x{matrix.Height})";
        }
        catch (Exception ex)
        {
            LogUiError("Spawn header details could not be read", ex);
            SpawnLocationLabel.Text = ex.Message;
        }
    }

    private void MarkSpawnEditorDirty()
    {
        if (loadingSpawnEditor) return;
        RefreshSpawnPreview();
        spawnEditorDirty = true;
        ReportEdit(spawnEdits, () => ReadSpawnSettingsFromFields(), SpawnEditorStatus);
    }

    /// <summary>The spawn settings as the fields currently show them, or null while a header or a
    /// direction is not chosen.</summary>
    private SpawnSettings? ReadSpawnSettingsFromFields()
    {
        if (SpawnHeaderSelector.SelectedIndex < 0 || SpawnDirectionSelector.SelectedIndex < 0) return null;
        return new SpawnSettings
        {
            HeaderId = (ushort)SpawnHeaderSelector.SelectedIndex,
            GlobalX = (ushort)((int)(SpawnMatrixX.Value ?? 0) * SpawnSettings.MapTiles + (int)(SpawnLocalX.Value ?? 0)),
            GlobalY = (ushort)((int)(SpawnMatrixY.Value ?? 0) * SpawnSettings.MapTiles + (int)(SpawnLocalY.Value ?? 0)),
            Direction = (ushort)SpawnDirectionSelector.SelectedIndex,
            InitialMoney = (uint)(SpawnInitialMoney.Value ?? 0)
        };
    }

    private void SpawnHeaderSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingSpawnEditor) return;
        UpdateSpawnHeaderDetails();
        MarkSpawnEditorDirty();
    }

    private void SpawnDirectionSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e) => MarkSpawnEditorDirty();

    private void SpawnField_ValueChanged(object? sender, NumericUpDownValueChangedEventArgs e) => MarkSpawnEditorDirty();

    private void ReloadSpawnSettings_Click(object? sender, RoutedEventArgs e) => LoadSpawnSettings();

    private void SaveSpawnSettings_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;
        if (SpawnHeaderSelector.SelectedIndex < 0 || SpawnDirectionSelector.SelectedIndex < 0)
        {
            SpawnEditorStatus.Text = "Pick a spawn header and a player direction before saving.";
            return;
        }

        try
        {
            SpawnSettings settings = ReadSpawnSettingsFromFields()!;
            SpawnSettingsService.Save(currentProject, metadata, settings);
            spawnEdits.Saved(settings);
            spawnEditorDirty = false;

            SpawnSettingsLayout layout = SpawnSettingsService.GetLayout(metadata);
            SpawnEditorStatus.Text = $"Spawn settings saved (ARM9 offset 0x{layout.Arm9Offset:X}, overlay {layout.MoneyOverlayNumber} offset 0x{layout.MoneyOverlayOffset:X}).";
        }
        catch (Exception ex)
        {
            LogUiError("Spawn settings could not be saved", ex);
            SpawnEditorStatus.Text = ex.Message;
        }
    }

    #endregion

    /// <summary>The in-game location name of a header, for labelling fly warp slots and header choices.</summary>
    private string FlyWarpLocationOfHeader(int headerId)
    {
        HeaderListEntry? header = allHeaders.FirstOrDefault(entry => entry.Id == headerId);
        return header?.LocationName ?? "Unknown location";
    }

    private void ConfigureFlyWarpEditor()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            return;
        }

        try
        {
            FlyWarpHeaderNames.Clear();
            int headerCount = currentProject.GetHeaderCount();
            for (int i = 0; i < headerCount; i++)
            {
                FlyWarpHeaderNames.Add($"{i:D4} - {currentProject.GetInternalName((ushort)i)} - {FlyWarpLocationOfHeader(i)}");
            }

            flyWarpEntries = FlyWarpService.Load(currentProject, metadata);
            flyWarpEdits.Loaded(flyWarpEntries, "Fly warp table");
            bool isHgss = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;

            FlyWarpRowItems.Clear();
            for (int i = 0; i < flyWarpEntries.Count; i++)
            {
                FlyWarpRowItems.Add(new FlyWarpRow(i, flyWarpEntries[i], isHgss, FlyWarpLocationOfHeader));
            }

            FlyWarpEditorStatus.Text = $"{flyWarpEntries.Count} fly warp slots loaded.";
        }
        catch (Exception ex)
        {
            LogUiError("Fly warp table could not be loaded", ex);
            FlyWarpEditorStatus.Text = ex.Message;
        }
    }

    private void FlyWarpField_Changed(object? sender, RoutedEventArgs e) =>
        ReportEdit(flyWarpEdits, () => flyWarpEntries, FlyWarpEditorStatus);

    private void FlyWarpField_Changed(object? sender, SelectionChangedEventArgs e) =>
        ReportEdit(flyWarpEdits, () => flyWarpEntries, FlyWarpEditorStatus);

    private void FlyWarpField_NumericChanged(object? sender, NumericUpDownValueChangedEventArgs e) =>
        ReportEdit(flyWarpEdits, () => flyWarpEntries, FlyWarpEditorStatus);

    private void SaveFlyWarpTable_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null) return;
        if (!currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return;

        try
        {
            // FlyWarpRowItems wrap flyWarpEntries by reference, so every field edit already lives in
            // this same list - no need to reassemble it from the row wrappers here.
            FlyWarpService.Save(currentProject, metadata, flyWarpEntries);
            flyWarpEdits.Saved(flyWarpEntries);
            FlyWarpEditorStatus.Text = "Fly warp table saved.";
        }
        catch (Exception ex)
        {
            LogUiError("Fly warp table could not be saved", ex);
            FlyWarpEditorStatus.Text = ex.Message;
        }
    }

    #endregion

    private void HoneyTreeGroupSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingSpecialEncounters || honeyTreeGroups is null || HoneyTreeGroupSelector.SelectedIndex < 0) return;

        HoneyTreeGroup group = honeyTreeGroups[HoneyTreeGroupSelector.SelectedIndex];
        HoneyTreeDescription.Text = group.Description;
        HoneyTreeSlots.Clear();
        for (int i = 0; i < group.Encounters.Count; i++)
        {
            HoneyTreeEncounter encounter = group.Encounters[i];
            int rate = i < SpecialEncounterService.HoneyTreeSlotRates.Length ? SpecialEncounterService.HoneyTreeSlotRates[i] : 0;
            HoneyTreeSlots.Add(new SpeciesSlotRow($"Slot {i} ({rate}%)", () => encounter.Species, value => encounter.Species = value));
        }
        UpdateSpeciesIcons(HoneyTreeSlots);
    }

    private void GreatMarshGroupSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingSpecialEncounters || greatMarshGroups is null || GreatMarshGroupSelector.SelectedIndex < 0) return;

        GreatMarshGroup group = greatMarshGroups[GreatMarshGroupSelector.SelectedIndex];
        GreatMarshDescription.Text = group.Description;
        GreatMarshSlots.Clear();
        for (int i = 0; i < group.Encounters.Count; i++)
        {
            GreatMarshEncounter encounter = group.Encounters[i];
            GreatMarshSlots.Add(new SpeciesSlotRow($"Slot {i}", () => encounter.Species, value => encounter.Species = value));
        }
        UpdateSpeciesIcons(GreatMarshSlots);
    }

    private void BugContestSetSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingSpecialEncounters || bugContestSets is null || BugContestSetSelector.SelectedIndex < 0) return;

        BugContestSet set = bugContestSets[BugContestSetSelector.SelectedIndex];
        BugContestDescription.Text = set.Description;
        BugContestSlots.Clear();
        for (int i = 0; i < set.Encounters.Count; i++)
        {
            BugContestSlots.Add(new BugContestSlotRow($"{i}", set.Encounters[i]));
        }
        UpdateSpeciesIcons(BugContestSlots);
    }

    private async void HeadbuttFileSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingSpecialEncounters || switchingDocument || currentProject is null ||
            HeadbuttFileSelector.SelectedIndex < 0 || HeadbuttFileSelector.SelectedIndex >= headbuttIds.Count)
        {
            return;
        }

        int index = HeadbuttFileSelector.SelectedIndex;
        int open = currentHeadbuttFile is null ? -1 : headbuttIds.IndexOf(currentHeadbuttFile.Id);
        if (!await ShouldLoadDocumentAsync(index == open, "Special Encounters", () => HeadbuttFileSelector.SelectedIndex = open,
            change => change.Area == "Headbutt encounters")) return;
        RunSwitchingDocument(() => HeadbuttFileSelector.SelectedIndex = index);

        try
        {
            currentHeadbuttFile = SpecialEncounterService.LoadHeadbutt(currentProject, headbuttIds[HeadbuttFileSelector.SelectedIndex]);
            headbuttEdits.Loaded(currentHeadbuttFile, $"Headbutt file {currentHeadbuttFile.Id:D4}");
            HeadbuttSlots.Clear();
            for (int i = 0; i < currentHeadbuttFile.NormalEncounters.Count; i++)
            {
                HeadbuttSlots.Add(new HeadbuttSlotRow($"Normal slot {i}", currentHeadbuttFile.NormalEncounters[i]));
            }

            for (int i = 0; i < currentHeadbuttFile.SpecialEncounters.Count; i++)
            {
                HeadbuttSlots.Add(new HeadbuttSlotRow($"Special slot {i}", currentHeadbuttFile.SpecialEncounters[i]));
            }
            UpdateSpeciesIcons(HeadbuttSlots);

            HeadbuttDescription.Text =
                $"{currentHeadbuttFile.NormalTreeGroups.Count} normal tree groups, {currentHeadbuttFile.SpecialTreeGroups.Count} special tree groups.";
        }
        catch (Exception ex)
        {
            LogUiError("Headbutt encounters could not be loaded", ex);
            SpecialEncounterStatus.Text = ex.Message;
        }
    }

    private async void SafariZoneFileSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingSpecialEncounters || switchingDocument || currentProject is null ||
            SafariZoneFileSelector.SelectedIndex < 0 || SafariZoneFileSelector.SelectedIndex >= safariZoneIds.Count)
        {
            return;
        }

        int index = SafariZoneFileSelector.SelectedIndex;
        int open = currentSafariZoneFile is null ? -1 : safariZoneIds.IndexOf(currentSafariZoneFile.Id);
        if (!await ShouldLoadDocumentAsync(index == open, "Special Encounters", () => SafariZoneFileSelector.SelectedIndex = open,
            change => change.Area == "Safari Zone encounters")) return;
        RunSwitchingDocument(() => SafariZoneFileSelector.SelectedIndex = index);

        try
        {
            currentSafariZoneFile = SpecialEncounterService.LoadSafariZone(currentProject, safariZoneIds[SafariZoneFileSelector.SelectedIndex]);
            safariZoneEdits.Loaded(currentSafariZoneFile, $"Safari Zone {currentSafariZoneFile.Id:D4}");
            RefreshSafariZoneSlots();
        }
        catch (Exception ex)
        {
            LogUiError("Safari Zone encounters could not be loaded", ex);
            SpecialEncounterStatus.Text = ex.Message;
        }
    }

    private void SafariZoneGroupSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingSpecialEncounters) return;
        RefreshSafariZoneSlots();
    }

    private void RefreshSafariZoneSlots()
    {
        SafariZoneSlots.Clear();
        if (currentSafariZoneFile is null || SafariZoneGroupSelector.SelectedIndex < 0)
        {
            return;
        }

        (string name, SafariZoneEncounterGroup group) = currentSafariZoneFile.AllGroups().ElementAt(SafariZoneGroupSelector.SelectedIndex);
        AddSafariSlots("Morning", group.MorningEncounters);
        AddSafariSlots("Day", group.DayEncounters);
        AddSafariSlots("Night", group.NightEncounters);
        AddSafariSlots("Morning (object)", group.MorningEncountersObject);
        AddSafariSlots("Day (object)", group.DayEncountersObject);
        AddSafariSlots("Night (object)", group.NightEncountersObject);
        UpdateSpeciesIcons(SafariZoneSlots);

        SafariZoneDescription.Text = $"{name}: {group.ObjectSlots} object slots.";
    }

    private void AddSafariSlots(string prefix, List<SafariZoneEncounter> encounters)
    {
        for (int i = 0; i < encounters.Count; i++)
        {
            SafariZoneSlots.Add(new SafariSlotRow($"{prefix} slot {i}", encounters[i]));
        }
    }

    private void SaveHoneyTree_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || honeyTreeGroups is null ||
            !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            return;
        }

        try
        {
            SpecialEncounterService.SaveHoneyTree(currentProject, metadata, honeyTreeGroups);
            honeyTreeEdits.Saved(honeyTreeGroups);
            SpecialEncounterStatus.Text = "Honey Tree encounters saved successfully.";
        }
        catch (Exception ex)
        {
            LogUiError("Honey Tree encounters could not be saved", ex);
            SpecialEncounterStatus.Text = ex.Message;
        }
    }

    private void SaveGreatMarsh_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || greatMarshGroups is null) return;

        try
        {
            SpecialEncounterService.SaveGreatMarsh(currentProject, greatMarshGroups);
            greatMarshEdits.Saved(greatMarshGroups);
            SpecialEncounterStatus.Text = "Great Marsh encounters saved successfully.";
        }
        catch (Exception ex)
        {
            LogUiError("Great Marsh encounters could not be saved", ex);
            SpecialEncounterStatus.Text = ex.Message;
        }
    }

    private void SaveBugContest_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || bugContestSets is null) return;

        try
        {
            SpecialEncounterService.SaveBugContest(currentProject, bugContestSets);
            bugContestEdits.Saved(bugContestSets);
            SpecialEncounterStatus.Text = "Bug Contest encounters saved successfully.";
        }
        catch (Exception ex)
        {
            LogUiError("Bug Contest encounters could not be saved", ex);
            SpecialEncounterStatus.Text = ex.Message;
        }
    }

    private void SaveHeadbutt_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentHeadbuttFile is null) return;

        try
        {
            SpecialEncounterService.SaveHeadbutt(currentProject, currentHeadbuttFile);
            headbuttEdits.Saved(currentHeadbuttFile);
            SpecialEncounterStatus.Text = $"Headbutt File {currentHeadbuttFile.Id:D4} saved successfully.";
        }
        catch (Exception ex)
        {
            LogUiError("Headbutt encounters could not be saved", ex);
            SpecialEncounterStatus.Text = ex.Message;
        }
    }

    private void SaveSafariZone_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentSafariZoneFile is null) return;

        try
        {
            SpecialEncounterService.SaveSafariZone(currentProject, currentSafariZoneFile);
            safariZoneEdits.Saved(currentSafariZoneFile);
            SpecialEncounterStatus.Text = $"Safari Zone {currentSafariZoneFile.Id:D4} saved successfully.";
        }
        catch (Exception ex)
        {
            LogUiError("Safari Zone encounters could not be saved", ex);
            SpecialEncounterStatus.Text = ex.Message;
        }
    }

    /// <summary>The same names the Header Editor's Camera dropdown uses (HeaderDisplayCatalog); cameras
    /// it has no name for are left blank.</summary>
    private string DescribeCameraAngle(int index)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null) return string.Empty;
        return HeaderDisplayCatalog.GetCameraAngles(metadata.HeaderFormat).FirstOrDefault(option => option.Id == index)?.Label ?? string.Empty;
    }

    private void ConfigureCameraEditor()
    {
        if (currentProject is null)
        {
            return;
        }

        CameraRowItems.Clear();
        currentCameraTable = null;

        if (!currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            CameraStatus.Text = "Game metadata is unavailable; the camera table cannot be located.";
            return;
        }

        bool hasOffsets = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;
        CameraXDispHeader.IsVisible = hasOffsets;
        CameraYDispHeader.IsVisible = hasOffsets;
        CameraZDispHeader.IsVisible = hasOffsets;

        if (hasOffsets && currentProject.Format != RomProjectFormat.DsRom)
        {
            CameraStatus.Text = "Convert this project to ds-rom format before using the Camera Editor for this ROM.";
            return;
        }

        try
        {
            currentCameraTable = CameraService.Load(currentProject, metadata);
            cameraEdits.Loaded(currentCameraTable, "Camera table");
            for (int i = 0; i < currentCameraTable.Cameras.Count; i++)
            {
                CameraRowItems.Add(new CameraRow(i, currentCameraTable.Cameras[i], currentCameraTable.Layout.HasOffsets, DescribeCameraAngle(i)));
            }

            CameraStatus.Text = currentCameraTable.Warning is null
                ? $"Loaded {currentCameraTable.Cameras.Count} cameras from overlay {currentCameraTable.Layout.OverlayNumber} at 0x{currentCameraTable.OverlayOffset:X}."
                : currentCameraTable.Warning;
        }
        catch (Exception ex)
        {
            LogUiError("Camera table could not be loaded", ex);
            CameraStatus.Text = ex.Message;
        }
    }

    /// <summary>Mirrors the original grid's cell validation: invalid input reverts to the stored value.</summary>
    private void CameraCell_LostFocus(object? sender, RoutedEventArgs e)
    {
        if (sender is TextBox { Tag: string field, DataContext: CameraRow row } box)
        {
            string stored = row.GetStoredText(field);
            if (box.Text != stored)
            {
                box.Text = stored;
            }
        }
    }

    private async void ExportCamera_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not CameraRow row)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"{Path.GetFileName(currentProject?.RootPath)} - Camera {row.Index}.bin",
            FileTypeChoices = [new FilePickerFileType("Camera File") { Patterns = ["*.bin"] }]
        });
        if (file is null)
        {
            return;
        }

        File.WriteAllBytes(file.Path.LocalPath, row.Camera.ToByteArray());
        CameraStatus.Text = "Camera correctly saved.";
    }

    private async void ImportCamera_Click(object? sender, RoutedEventArgs e)
    {
        if ((sender as Button)?.DataContext is not CameraRow row || currentCameraTable is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Camera File") { Patterns = ["*.bin"] }]
        });
        if (files.Count != 1)
        {
            return;
        }

        try
        {
            GameCameraRecord imported = GameCameraRecord.FromByteArray(
                File.ReadAllBytes(files[0].Path.LocalPath),
                currentCameraTable.Layout.HasOffsets);
            currentCameraTable.Cameras[row.Index] = imported;
            CameraRowItems[row.Index] = new CameraRow(row.Index, imported, currentCameraTable.Layout.HasOffsets, DescribeCameraAngle(row.Index));
            CameraStatus.Text = "Camera correctly imported. Save the table to write it to the ROM files.";
        }
        catch (Exception ex)
        {
            LogUiError("Camera could not be imported", ex);
            CameraStatus.Text = ex.Message;
        }
    }

    private async void ExportCameraTable_Click(object? sender, RoutedEventArgs e)
    {
        if (currentCameraTable is null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"{Path.GetFileName(currentProject?.RootPath)} - CameraTable.bin",
            FileTypeChoices = [new FilePickerFileType("Camera Table File") { Patterns = ["*.bin"] }]
        });
        if (file is null)
        {
            return;
        }

        CameraService.ExportTable(file.Path.LocalPath, currentCameraTable);
        CameraStatus.Text = "Camera table correctly saved.";
    }

    private async void ImportCameraTable_Click(object? sender, RoutedEventArgs e)
    {
        if (currentCameraTable is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Camera Table File") { Patterns = ["*.bin"] }]
        });
        if (files.Count != 1)
        {
            return;
        }

        try
        {
            List<GameCameraRecord> imported = CameraService.ImportTable(files[0].Path.LocalPath, currentCameraTable.Layout);
            for (int i = 0; i < imported.Count && i < currentCameraTable.Cameras.Count; i++)
            {
                currentCameraTable.Cameras[i] = imported[i];
                CameraRowItems[i] = new CameraRow(i, imported[i], currentCameraTable.Layout.HasOffsets, DescribeCameraAngle(i));
            }

            CameraStatus.Text = "Camera Table imported correctly. Save the table to write it to the ROM files.";
        }
        catch (Exception ex)
        {
            LogUiError("Camera table could not be imported", ex);
            CameraStatus.Text = ex.Message;
        }
    }

    private void SaveCameraTable_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentCameraTable is null)
        {
            return;
        }

        try
        {
            CameraService.Save(currentProject, currentCameraTable);
            cameraEdits.Saved(currentCameraTable);
            CameraStatus.Text = "Camera table correctly saved.";
        }
        catch (Exception ex)
        {
            LogUiError("Camera table could not be saved", ex);
            CameraStatus.Text = ex.Message;
        }
    }

    #region Script Command Database

    private ScriptCommandDatabase? scriptCommandDatabase;
    private List<ScriptCommandInfo> allScriptCommands = [];
    private List<ScriptCommandInfo> filteredScriptCommands = [];
    private List<ScriptSubEntryInfo> allScriptMovements = [];
    private List<ScriptSubEntryInfo> filteredScriptMovements = [];

    /// <summary>Loads the script command reference database for the current project's game version -
    /// see ScriptCommandDatabaseService. The database itself isn't checked into this repo (same as
    /// Tools/ - CI populates a "databases" directory alongside the built app from
    /// https://github.com/DS-Pokemon-Rom-Editor/scrcmd-database), so a missing file here is expected
    /// on a from-source checkout without that step run, not necessarily a bug.</summary>
    private void ConfigureScriptCommandDatabase()
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            return;
        }

        string databasesDirectory = Path.Combine(AppContext.BaseDirectory, "databases");
        // The ROM's own database is the one its scripts are parsed with, once the bridge has made it.
        string projectDatabase = ScriptDatabaseStore.ProjectDatabasePath(currentProject);
        bool usingProjectDatabase = File.Exists(projectDatabase);
        string? path = usingProjectDatabase ? projectDatabase : ScriptCommandDatabaseService.ResolvePath(databasesDirectory, metadata.HeaderFormat);
        if (path is null)
        {
            scriptCommandDatabase = null;
            allScriptCommands = [];
            allScriptMovements = [];
            ScriptCommandDatabaseStatus.Text =
                $"Database not found - place {ScriptCommandDatabaseService.GetFileName(metadata.HeaderFormat)} under {databasesDirectory} " +
                "(see github.com/DS-Pokemon-Rom-Editor/scrcmd-database).";
            RefreshScriptCommandResults();
            RefreshScriptMovementResults();
            return;
        }

        try
        {
            scriptCommandDatabase = ScriptCommandDatabaseService.Load(path);
            scriptCommandsByName = null;
            scriptMovementsByName = null;
            allScriptCommands = scriptCommandDatabase.Commands.Values.OrderBy(command => command.CommandId).ToList();
            allScriptMovements = scriptCommandDatabase.Movements.Values.OrderBy(movement => movement.Id).ToList();
            ScriptCommandDatabaseStatus.Text = usingProjectDatabase
                ? $"Loaded {allScriptCommands.Count} commands and {allScriptMovements.Count} movements from this ROM's script database (replace it under Settings)."
                : $"Loaded {allScriptCommands.Count} commands and {allScriptMovements.Count} movements from {Path.GetFileName(path)}.";
            RefreshScriptCommandResults();
            RefreshScriptMovementResults();
        }
        catch (Exception ex)
        {
            LogUiError("Script command database could not be loaded", ex);
            ScriptCommandDatabaseStatus.Text = ex.Message;
        }
    }

    /// <summary>Fetches the current game version's script command database straight from
    /// DS-Pokemon-Rom-Editor/scrcmd-database on GitHub (see ScriptCommandDatabaseService.DownloadAsync)
    /// and overwrites the local copy under "databases", so users don't have to track that repo
    /// manually to pick up new/corrected command entries. Reloads the panel from the freshly-written
    /// file afterward via ConfigureScriptCommandDatabase.</summary>
    private async void ScriptCommandDatabaseDownload_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            return;
        }

        ScriptCommandDatabaseDownloadButton.IsEnabled = false;
        ScriptCommandDatabaseStatus.Text = $"Downloading {ScriptCommandDatabaseService.GetFileName(metadata.HeaderFormat)}...";
        try
        {
            string databasesDirectory = Path.Combine(AppContext.BaseDirectory, "databases");
            string downloaded = await ScriptCommandDatabaseService.DownloadAsync(databasesDirectory, metadata.HeaderFormat);
            ScriptDatabaseStore.CopyToBridge(downloaded);
            ConfigureScriptCommandDatabase();
            if (File.Exists(ScriptDatabaseStore.ProjectDatabasePath(currentProject)))
            {
                ScriptCommandDatabaseStatus.Text += " The latest built-in database was downloaded, but this ROM keeps its own copy; Settings > Reset to Built-In switches it over.";
            }
        }
        catch (Exception ex)
        {
            LogUiError("Script command database could not be downloaded", ex);
            ScriptCommandDatabaseStatus.Text = ex.Message;
        }
        finally
        {
            ScriptCommandDatabaseDownloadButton.IsEnabled = true;
        }
    }

    private void ScriptCommandSearch_TextChanged(object? sender, TextChangedEventArgs e)
    {
        RefreshScriptCommandResults();
        RefreshScriptMovementResults();
    }

    private void RefreshScriptCommandResults()
    {
        string query = ScriptCommandSearchInput.Text?.Trim() ?? "";
        filteredScriptCommands = string.IsNullOrEmpty(query)
            ? allScriptCommands
            : allScriptCommands.Where(command => MatchesScriptCommandQuery(command, query)).ToList();

        ScriptCommandResultsList.Items.Clear();
        foreach (ScriptCommandInfo command in filteredScriptCommands)
        {
            ScriptCommandResultsList.Items.Add($"[0x{command.CommandId:X4}] {command.Name}");
        }

        ShowScriptCommandDetail(filteredScriptCommands.Count > 0 ? filteredScriptCommands[0] : null);
        if (filteredScriptCommands.Count > 0) ScriptCommandResultsList.SelectedIndex = 0;
    }

    private static bool MatchesScriptCommandQuery(ScriptCommandInfo command, string query) =>
        command.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        command.DecompName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        command.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
        command.CommandId.ToString("X4").Contains(query, StringComparison.OrdinalIgnoreCase);

    private void ScriptCommandResultsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        int index = ScriptCommandResultsList.SelectedIndex;
        ShowScriptCommandDetail(index >= 0 && index < filteredScriptCommands.Count ? filteredScriptCommands[index] : null);
    }

    #region Script command tooltips

    private Dictionary<string, ScriptCommandInfo>? scriptCommandsByName;
    private Dictionary<string, ScriptSubEntryInfo>? scriptMovementsByName;
    private readonly Dictionary<TextBox, string> hoveredScriptWords = [];

    /// <summary>Hovering a command (or, in the Actions section, a movement) in the Script Editor shows
    /// what the command database says about it, the same text the Script Commands page shows.</summary>
    private void AttachScriptCommandTooltips()
    {
        foreach (TextBox box in new[] { ScriptsTextBox, FunctionsTextBox, ActionsTextBox })
        {
            ToolTip.SetPlacement(box, PlacementMode.Pointer);
            ToolTip.SetShowDelay(box, 250);
            box.PointerMoved += ScriptTextBox_PointerMoved;
            box.PointerExited += (_, _) => HideScriptCommandTooltip(box);
        }
    }

    private void ScriptTextBox_PointerMoved(object? sender, PointerEventArgs e)
    {
        if (sender is not TextBox box) return;
        string word = GetScriptWordAt(box, e);
        if (hoveredScriptWords.TryGetValue(box, out string? previous) && previous == word) return;
        hoveredScriptWords[box] = word;

        Control? tip = word.Length == 0 ? null : BuildScriptTooltip(word);
        if (tip is null)
        {
            HideScriptCommandTooltip(box);
            hoveredScriptWords[box] = word;
            return;
        }

        ToolTip.SetTip(box, tip);
        ToolTip.SetIsOpen(box, true);
    }

    private void HideScriptCommandTooltip(TextBox box)
    {
        hoveredScriptWords.Remove(box);
        ToolTip.SetIsOpen(box, false);
        ToolTip.SetTip(box, null);
    }

    /// <summary>The identifier under the pointer, found by hit-testing the text box's own laid-out
    /// text; empty when the pointer is not over text.</summary>
    private static string GetScriptWordAt(TextBox box, PointerEventArgs e)
    {
        TextPresenter? presenter = box.GetVisualDescendants().OfType<TextPresenter>().FirstOrDefault();
        string text = box.Text ?? string.Empty;
        if (presenter is null || text.Length == 0) return string.Empty;

        TextHitTestResult hit = presenter.TextLayout.HitTestPoint(e.GetPosition(presenter));
        if (!hit.IsInside) return string.Empty;

        int index = Math.Clamp(hit.TextPosition, 0, text.Length - 1);
        static bool IsWordChar(char c) => char.IsLetterOrDigit(c) || c == '_';
        if (!IsWordChar(text[index])) return string.Empty;

        int start = index;
        while (start > 0 && IsWordChar(text[start - 1])) start--;
        int end = index;
        while (end < text.Length - 1 && IsWordChar(text[end + 1])) end++;
        return text[start..(end + 1)];
    }

    private Control? BuildScriptTooltip(string word)
    {
        if (scriptCommandDatabase is null) ConfigureScriptCommandDatabase();
        if (scriptCommandDatabase is null) return null;

        if (scriptCommandsByName is null || scriptMovementsByName is null)
        {
            scriptCommandsByName = new Dictionary<string, ScriptCommandInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (ScriptCommandInfo entry in scriptCommandDatabase.Commands.Values)
            {
                if (entry.Name.Length > 0) scriptCommandsByName.TryAdd(entry.Name, entry);
                if (entry.DecompName.Length > 0) scriptCommandsByName.TryAdd(entry.DecompName, entry);
            }

            scriptMovementsByName = new Dictionary<string, ScriptSubEntryInfo>(StringComparer.OrdinalIgnoreCase);
            foreach (ScriptSubEntryInfo entry in scriptCommandDatabase.Movements.Values)
            {
                if (entry.Name.Length > 0) scriptMovementsByName.TryAdd(entry.Name, entry);
                if (entry.DecompName.Length > 0) scriptMovementsByName.TryAdd(entry.DecompName, entry);
            }
        }

        var panel = new StackPanel { Spacing = 4, MaxWidth = 460 };
        if (scriptCommandsByName.TryGetValue(word, out ScriptCommandInfo? command))
        {
            (string title, string description, string parameters, string notes) = DescribeScriptCommand(command);
            panel.Children.Add(new TextBlock { Text = title, FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (description.Length > 0) panel.Children.Add(new TextBlock { Text = description, TextWrapping = TextWrapping.Wrap });
            panel.Children.Add(new TextBlock { Text = $"Parameters:\n{parameters}", FontFamily = new FontFamily("monospace"), TextWrapping = TextWrapping.Wrap });
            if (notes.Length > 0) panel.Children.Add(new TextBlock { Text = notes, TextWrapping = TextWrapping.Wrap });
            return panel;
        }

        if (scriptMovementsByName.TryGetValue(word, out ScriptSubEntryInfo? movement))
        {
            panel.Children.Add(new TextBlock { Text = $"Movement [0x{movement.Id:X4}] {movement.Name}", FontWeight = FontWeight.SemiBold, TextWrapping = TextWrapping.Wrap });
            if (movement.Description.Length > 0) panel.Children.Add(new TextBlock { Text = movement.Description, TextWrapping = TextWrapping.Wrap });
            return panel;
        }

        return null;
    }

    private static (string Title, string Description, string Parameters, string Notes) DescribeScriptCommand(ScriptCommandInfo command)
    {
        string decompSuffix = string.IsNullOrEmpty(command.DecompName) || command.DecompName == command.Name
            ? ""
            : $" ({command.DecompName})";
        string parameters = "(none)";
        if (command.ParameterSizes.Count > 0)
        {
            var lines = new List<string>();
            for (int i = 0; i < command.ParameterSizes.Count; i++)
            {
                string type = i < command.ParameterTypes.Count && command.ParameterTypes[i] is { } parameterType ? parameterType.ToString() : "?";
                string name = i < command.ParameterNames.Count ? command.ParameterNames[i] : "";
                lines.Add($"{i + 1}. {name} - {type}, {command.ParameterSizes[i]} bytes");
            }

            parameters = string.Join("\n", lines);
        }

        return ($"[0x{command.CommandId:X4}] {command.Name}{decompSuffix}", command.Description, parameters,
            string.IsNullOrEmpty(command.Notes) ? "" : $"Note: {command.Notes}");
    }

    #endregion

    private void ShowScriptCommandDetail(ScriptCommandInfo? command)
    {
        if (command is null)
        {
            ScriptCommandDetailTitle.Text = "";
            ScriptCommandDetailDescription.Text = "";
            ScriptCommandDetailParameters.Text = "";
            ScriptCommandDetailNotes.Text = "";
            return;
        }

        (string title, string description, string parameters, string notes) = DescribeScriptCommand(command);
        ScriptCommandDetailTitle.Text = title;
        ScriptCommandDetailDescription.Text = description;
        ScriptCommandDetailParameters.Text = parameters;
        ScriptCommandDetailNotes.Text = notes;
    }

    private void RefreshScriptMovementResults()
    {
        string query = ScriptCommandSearchInput.Text?.Trim() ?? "";
        filteredScriptMovements = string.IsNullOrEmpty(query)
            ? allScriptMovements
            : allScriptMovements.Where(movement =>
                movement.Name.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                movement.DecompName.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                movement.Description.Contains(query, StringComparison.OrdinalIgnoreCase) ||
                movement.Id.ToString("X4").Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();

        ScriptMovementResultsList.Items.Clear();
        foreach (ScriptSubEntryInfo movement in filteredScriptMovements)
        {
            ScriptMovementResultsList.Items.Add($"[0x{movement.Id:X4}] {movement.Name}");
        }

        ShowScriptMovementDetail(filteredScriptMovements.Count > 0 ? filteredScriptMovements[0] : null);
        if (filteredScriptMovements.Count > 0) ScriptMovementResultsList.SelectedIndex = 0;
    }

    private void ScriptMovementResultsList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        int index = ScriptMovementResultsList.SelectedIndex;
        ShowScriptMovementDetail(index >= 0 && index < filteredScriptMovements.Count ? filteredScriptMovements[index] : null);
    }

    private void ShowScriptMovementDetail(ScriptSubEntryInfo? movement)
    {
        if (movement is null)
        {
            ScriptMovementDetailTitle.Text = "";
            ScriptMovementDetailDescription.Text = "";
            return;
        }

        string decompSuffix = string.IsNullOrEmpty(movement.DecompName) || movement.DecompName == movement.Name
            ? ""
            : $" ({movement.DecompName})";
        ScriptMovementDetailTitle.Text = $"[0x{movement.Id:X4}] {movement.Name}{decompSuffix}";
        ScriptMovementDetailDescription.Text = movement.Description;
    }

    #endregion

    private void ConfigureLevelScriptEditor()
    {
        if (currentProject is null)
        {
            return;
        }

        loadingLevelScript = true;
        try
        {
            RefreshLevelScriptNames();
            levelScriptIds.Clear();
            LevelScriptSelector.Items.Clear();
            foreach (int id in LevelScriptService.GetIds(currentProject))
            {
                levelScriptIds.Add(id);
                LevelScriptSelector.Items.Add(LevelScriptLabel(id));
            }
        }
        finally
        {
            loadingLevelScript = false;
        }

        if (LevelScriptSelector.Items.Count > 0)
        {
            LevelScriptSelector.SelectedIndex = 0;
        }
        else
        {
            LevelScriptStatus.Text = "No level scripts were found in the unpacked scripts.";
        }
    }

    private void SelectLevelScriptId(int levelScriptId)
    {
        for (int i = 0; i < levelScriptIds.Count; i++)
        {
            if (levelScriptIds[i] == levelScriptId)
            {
                LevelScriptSelector.SelectedIndex = i;
                return;
            }
        }

        LevelScriptStatus.Text = $"Script file {levelScriptId:D4} is not a level script.";
    }

    private async void LevelScriptSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingLevelScript || switchingDocument || currentProject is null || LevelScriptSelector.SelectedIndex < 0 || LevelScriptSelector.SelectedIndex >= levelScriptIds.Count)
        {
            return;
        }

        int index = LevelScriptSelector.SelectedIndex;
        int open = currentLevelScript is null ? -1 : levelScriptIds.IndexOf(currentLevelScript.Id);
        if (!await ShouldLoadDocumentAsync(index == open, "Level Script Editor", () => LevelScriptSelector.SelectedIndex = open)) return;
        RunSwitchingDocument(() => LevelScriptSelector.SelectedIndex = index);
        int id = levelScriptIds[index];
        try
        {
            currentLevelScript = LevelScriptService.Load(currentProject, id);
            levelScriptEdits.Loaded(currentLevelScript, $"Level script {id:D4}");
            LevelScriptTriggersList.SelectedIndex = -1;
            RefreshLevelScriptTriggers();
            if (LevelScriptTriggersList.Items.Count > 0)
            {
                LevelScriptTriggersList.SelectedIndex = 0;
            }

            LoadHeadersUsingLevelScript((ushort)id);
            ShowLevelScriptName(id);
            LevelScriptStatus.Text = $"Loaded Level Script {id:D4} ({currentLevelScript.Triggers.Count} triggers).";
        }
        catch (Exception ex)
        {
            currentLevelScript = null;
            LevelScriptTriggersList.Items.Clear();
            LogUiError("Level script could not be loaded", ex);
            LevelScriptStatus.Text = ex.Message;
        }
    }

    private void RefreshLevelScriptTriggers()
    {
        int selectedIndex = LevelScriptTriggersList.SelectedIndex;
        loadingLevelScript = true;
        try
        {
            LevelScriptTriggersList.Items.Clear();
            if (currentLevelScript is null)
            {
                return;
            }

            foreach (LevelScriptTrigger trigger in currentLevelScript.Triggers)
            {
                LevelScriptTriggersList.Items.Add(trigger.Describe(levelScriptDisplayInHex));
            }
        }
        finally
        {
            loadingLevelScript = false;
        }

        if (selectedIndex >= 0 && selectedIndex < LevelScriptTriggersList.Items.Count)
        {
            LevelScriptTriggersList.SelectedIndex = selectedIndex;
        }
    }

    private void LevelScriptTriggersList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        LoadSelectedLevelScriptTrigger();
    }

    private void LoadSelectedLevelScriptTrigger()
    {
        if (loadingLevelScript || currentLevelScript is null ||
            LevelScriptTriggersList.SelectedIndex < 0 || LevelScriptTriggersList.SelectedIndex >= currentLevelScript.Triggers.Count)
        {
            return;
        }

        LevelScriptTrigger trigger = currentLevelScript.Triggers[LevelScriptTriggersList.SelectedIndex];
        loadingLevelScript = true;
        try
        {
            LevelScriptVariableValueRadio.IsChecked = trigger.TriggerType == LevelScriptTriggerType.VariableValue;
            LevelScriptMapChangeRadio.IsChecked = trigger.TriggerType == LevelScriptTriggerType.MapChange;
            LevelScriptScreenResetRadio.IsChecked = trigger.TriggerType == LevelScriptTriggerType.ScreenReset;
            LevelScriptLoadGameRadio.IsChecked = trigger.TriggerType == LevelScriptTriggerType.LoadGame;
            LevelScriptTriggerScriptInput.Text = FormatLevelScriptNumber(trigger.ScriptTriggered);
            LevelScriptVariableInput.Text = FormatLevelScriptNumber(trigger.VariableToWatch);
            LevelScriptValueInput.Text = FormatLevelScriptNumber(trigger.ExpectedValue);
        }
        finally
        {
            loadingLevelScript = false;
        }

        UpdateLevelScriptFieldVisibility();
    }

    private void LevelScriptTriggerType_Checked(object? sender, RoutedEventArgs e)
    {
        if (LevelScriptVariableLabel is null)
        {
            return;
        }

        UpdateLevelScriptFieldVisibility();
    }

    private void UpdateLevelScriptFieldVisibility()
    {
        bool isVariableValue = LevelScriptVariableValueRadio.IsChecked == true;
        LevelScriptVariableLabel.IsVisible = isVariableValue;
        LevelScriptVariableInput.IsVisible = isVariableValue;
        LevelScriptValueLabel.IsVisible = isVariableValue;
        LevelScriptValueInput.IsVisible = isVariableValue;
    }

    private void LevelScriptNumberFormat_Checked(object? sender, RoutedEventArgs e)
    {
        if (LevelScriptTriggersList is null)
        {
            return;
        }

        levelScriptDisplayInHex = LevelScriptHexRadio.IsChecked == true;
        RefreshLevelScriptTriggers();
        LoadSelectedLevelScriptTrigger();
    }

    private string FormatLevelScriptNumber(int value) => levelScriptDisplayInHex ? "0x" + value.ToString("X") : value.ToString();

    private static bool TryParseLevelScriptNumber(string? text, out int value)
    {
        text = text?.Trim() ?? string.Empty;
        if (text.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
        {
            return int.TryParse(text[2..], System.Globalization.NumberStyles.HexNumber, null, out value);
        }

        return int.TryParse(text, out value);
    }

    private bool TryBuildLevelScriptTrigger(out LevelScriptTrigger trigger)
    {
        trigger = LevelScriptTrigger.MapScreenLoad(LevelScriptTriggerType.MapChange, 0);
        if (!TryParseLevelScriptNumber(LevelScriptTriggerScriptInput.Text, out int scriptId) || scriptId < 0)
        {
            LevelScriptStatus.Text = "The script ID is not a valid number.";
            return false;
        }

        if (LevelScriptVariableValueRadio.IsChecked == true)
        {
            if (!TryParseLevelScriptNumber(LevelScriptVariableInput.Text, out int variable) || variable <= 0)
            {
                LevelScriptStatus.Text = "The variable to watch must be a number greater than zero.";
                return false;
            }

            if (!TryParseLevelScriptNumber(LevelScriptValueInput.Text, out int expected) || expected < 0)
            {
                LevelScriptStatus.Text = "The expected value is not a valid number.";
                return false;
            }

            trigger = LevelScriptTrigger.VariableValue(scriptId, variable, expected);
            return true;
        }

        LevelScriptTriggerType type = LevelScriptMapChangeRadio.IsChecked == true ? LevelScriptTriggerType.MapChange
            : LevelScriptScreenResetRadio.IsChecked == true ? LevelScriptTriggerType.ScreenReset
            : LevelScriptTriggerType.LoadGame;
        trigger = LevelScriptTrigger.MapScreenLoad(type, scriptId);
        return true;
    }

    private void AddLevelScriptTrigger_Click(object? sender, RoutedEventArgs e)
    {
        if (currentLevelScript is null)
        {
            LevelScriptStatus.Text = "Select a level script first.";
            return;
        }

        if (!TryBuildLevelScriptTrigger(out LevelScriptTrigger trigger))
        {
            return;
        }

        currentLevelScript.Triggers.Add(trigger);
        RefreshLevelScriptTriggers();
        LevelScriptTriggersList.SelectedIndex = LevelScriptTriggersList.Items.Count - 1;
        LevelScriptStatus.Text = "Trigger added. Save the level script to write it to the ROM files.";
    }

    private void UpdateLevelScriptTrigger_Click(object? sender, RoutedEventArgs e)
    {
        if (currentLevelScript is null || LevelScriptTriggersList.SelectedIndex < 0 || LevelScriptTriggersList.SelectedIndex >= currentLevelScript.Triggers.Count)
        {
            LevelScriptStatus.Text = "Select a trigger to update.";
            return;
        }

        if (!TryBuildLevelScriptTrigger(out LevelScriptTrigger trigger))
        {
            return;
        }

        currentLevelScript.Triggers[LevelScriptTriggersList.SelectedIndex] = trigger;
        RefreshLevelScriptTriggers();
        LevelScriptStatus.Text = "Trigger updated. Save the level script to write it to the ROM files.";
    }

    private void RemoveLevelScriptTrigger_Click(object? sender, RoutedEventArgs e)
    {
        if (currentLevelScript is null || LevelScriptTriggersList.SelectedIndex < 0 || LevelScriptTriggersList.SelectedIndex >= currentLevelScript.Triggers.Count)
        {
            LevelScriptStatus.Text = "Select a trigger to remove.";
            return;
        }

        currentLevelScript.Triggers.RemoveAt(LevelScriptTriggersList.SelectedIndex);
        RefreshLevelScriptTriggers();
        LevelScriptStatus.Text = "Trigger removed. Save the level script to write it to the ROM files.";
    }

    private void GoToTriggerScript_Click(object? sender, RoutedEventArgs e)
    {
        int scriptId;
        if (currentLevelScript is not null && LevelScriptTriggersList.SelectedIndex >= 0 && LevelScriptTriggersList.SelectedIndex < currentLevelScript.Triggers.Count)
        {
            scriptId = currentLevelScript.Triggers[LevelScriptTriggersList.SelectedIndex].ScriptTriggered;
        }
        else if (!TryParseLevelScriptNumber(LevelScriptTriggerScriptInput.Text, out scriptId))
        {
            LevelScriptStatus.Text = "Select a trigger, or enter a valid script ID.";
            return;
        }

        if (scriptId < 0)
        {
            LevelScriptStatus.Text = "The triggered script ID is not valid.";
            return;
        }

        SelectEditor("Script Editor");
        if (scriptFileIds.Count == 0) ConfigureScriptEditor();
        SelectScriptFileId(scriptId);
    }

    private void SaveLevelScript_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentLevelScript is null)
        {
            return;
        }
        try
        {
            LevelScriptService.Save(currentProject, currentLevelScript, LevelScriptPaddingCheckBox.IsChecked == true);
            levelScriptEdits.Saved(currentLevelScript);
            SaveLevelScriptName();
            LevelScriptStatus.Text = $"Saved Level Script {currentLevelScript.Id:D4}.";
        }
        catch (Exception ex)
        {
            LogUiError("Level script could not be saved", ex);
            LevelScriptStatus.Text = ex.Message;
        }
    }

    private async void ImportLevelScript_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || currentLevelScript is null)
        {
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            FileTypeFilter = [new FilePickerFileType("Level Script") { Patterns = ["*.lscr", "*.bin", "*"] }]
        });
        if (files.Count != 1)
        {
            return;
        }

        try
        {
            LevelScriptFile imported = LevelScriptService.LoadPath(files[0].Path.LocalPath, currentLevelScript.Id);
            currentLevelScript.Triggers.Clear();
            currentLevelScript.Triggers.AddRange(imported.Triggers);
            RefreshLevelScriptTriggers();
            LevelScriptStatus.Text = "Level script imported. Save it to write it to the ROM files.";
        }
        catch (Exception ex)
        {
            LogUiError("Level script could not be imported", ex);
            LevelScriptStatus.Text = ex.Message;
        }
    }

    private async void ExportLevelScript_Click(object? sender, RoutedEventArgs e)
    {
        if (currentLevelScript is null)
        {
            return;
        }

        var file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            SuggestedFileName = $"Level Script {currentLevelScript.Id:D4}.lscr",
            FileTypeChoices = [new FilePickerFileType("Level Script") { Patterns = ["*.lscr"] }]
        });
        if (file is null)
        {
            return;
        }

        File.WriteAllBytes(file.Path.LocalPath, LevelScriptService.ToByteArray(currentLevelScript, LevelScriptPaddingCheckBox.IsChecked == true));
        LevelScriptStatus.Text = "Level script exported successfully.";
    }

    private void LoadHeadersUsingLevelScript(ushort levelScriptId)
    {
        HeadersUsingLevelScriptList.Items.Clear();
        if (headerStore is null || currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            return;
        }

        IReadOnlyList<HeaderOption> locations = currentProject.GetLocationNames(metadata);
        for (ushort id = 0; id < currentProject.GetHeaderCount(); id++)
        {
            MapHeaderRecord header = headerStore.Load(id);
            if (header.LevelScriptId != levelScriptId)
            {
                continue;
            }

            string internalName = currentProject.GetInternalName(id);
            string locationName = locations.FirstOrDefault(option => option.Id == header.LocationNameId)?.Label ?? "Unknown location";
            HeadersUsingLevelScriptList.Items.Add(new HeaderLink(id, internalName, locationName, header.TextArchiveId));
        }

        if (HeadersUsingLevelScriptList.Items.Count > 0)
        {
            HeadersUsingLevelScriptList.SelectedIndex = 0;
        }
    }

    private void GoToHeaderFromLevelScript_Click(object? sender, RoutedEventArgs e)
    {
        if (HeadersUsingLevelScriptList.SelectedItem is not HeaderLink header)
        {
            StatusText.Text = "No header is selected.";
            return;
        }

        SelectEditor("Map Editor");
        ClearHeaderFilters();
        for (int i = 0; i < HeaderSelector.Items.Count; i++)
        {
            if (HeaderSelector.Items[i] is HeaderListEntry entry && entry.Id == header.Id)
            {
                HeaderSelector.SelectedIndex = i;
                return;
            }
        }
    }

    private async void ScriptFileSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingScript || switchingDocument) return;
        int index = ScriptFileSelector.SelectedIndex;
        int open = loadedScriptFileIndex;
        if (!await ShouldLoadDocumentAsync(index == open, "Script Editor", () => ScriptFileSelector.SelectedIndex = open, discard: UndoLiveScriptMessageEdits)) return;
        RunSwitchingDocument(() => ScriptFileSelector.SelectedIndex = index);
        LoadSelectedScriptFile();
    }

    private void LoadSelectedScriptFile()
    {
        if (currentProject is null || ScriptFileSelector.SelectedIndex < 0 || ScriptFileSelector.SelectedIndex >= scriptFileIds.Count)
        {
            return;
        }

        int id = scriptFileIds[ScriptFileSelector.SelectedIndex];
        if (id == GlobalLuaEntryId)
        {
            LoadGlobalLuaEntry();
            return;
        }
        string path = Path.Combine(currentProject.ExpandedScriptsDirectory, $"{id:D4}.script");
        if (!File.Exists(path))
        {
            return;
        }

        loadingScript = true;
        try
        {
            string content = File.ReadAllText(path);
            currentScriptTextArchive = LoadScriptTextArchive((ushort)id);
            string expanded = ExpandScriptMessages(content);
            (ScriptsTextBox.Text, FunctionsTextBox.Text, ActionsTextBox.Text) = SplitScriptSections(expanded);
            LoadLuaForScript(id, content);
            loadedScriptFileIndex = ScriptFileSelector.SelectedIndex;
            scriptEdits.Loaded(CurrentScriptDocument(), $"Script {id:D4}");
            RefreshScriptMessageList();
            UpdateScriptSectionCounts();
            LoadHeadersUsingScript((ushort)id);
            AttachScriptIconHooks();
            QueueScriptIconRefresh();
            ScriptSearchStatus.Text = currentScriptTextArchive is null
                ? $"Loaded {id:D4}.script. No linked text archive was found."
                : $"Loaded {id:D4}.script with Text Archive {currentScriptTextArchive.Id:D4}.";
        }
        catch (Exception ex)
        {
            LogUiError("Script could not be loaded", ex);
            ScriptSearchStatus.Text = ex.Message;
        }
        finally
        {
            loadingScript = false;
        }
    }

    // The ScriptFileSelector entry whose file the boxes hold. The selector itself can be ahead of it while
    // moving to another file is being confirmed.
    private int loadedScriptFileIndex = -1;

    /// <summary>The script editor's text as it stands, for change tracking; null when no script is open.</summary>
    private ScriptDocument? CurrentScriptDocument() =>
        loadedScriptFileIndex >= 0 && loadedScriptFileIndex < scriptFileIds.Count
            ? new ScriptDocument { Id = scriptFileIds[loadedScriptFileIndex], Scripts = ScriptsTextBox.Text ?? "", Functions = FunctionsTextBox.Text ?? "", Actions = ActionsTextBox.Text ?? "", Lua = scriptUsesLua ? LuaTextBox.Text ?? "" : "", GlobalLua = GlobalLuaTextBox.Text ?? "" }
            : null;

    public sealed class ScriptDocument
    {
        public int Id { get; set; }
        public string Scripts { get; set; } = "";
        public string Functions { get; set; } = "";
        public string Actions { get; set; } = "";
        public string Lua { get; set; } = "";
        public string GlobalLua { get; set; } = "";
    }

    private void SaveScript_Click(object? sender, RoutedEventArgs e) => SaveCurrentScript();

    private void ExportScript_Click(object? sender, RoutedEventArgs e) => SaveCurrentScript();

    private void SaveCurrentScript()
    {
        if (currentProject is null || ScriptFileSelector.SelectedIndex < 0 || ScriptFileSelector.SelectedIndex >= scriptFileIds.Count)
        {
            ScriptSearchStatus.Text = "No script file is selected.";
            return;
        }

        try
        {
            int id = scriptFileIds[ScriptFileSelector.SelectedIndex];
            if (id == GlobalLuaEntryId)
            {
                ScriptSearchStatus.Text = SaveGlobalLuaIfChanged(id).Trim() is { Length: > 0 } note ? note : "global.lua has no changes to save.";
                scriptEdits.Saved(CurrentScriptDocument());
                return;
            }
            if (scriptUsesLua)
            {
                SaveLuaScript(id);
                return;
            }
            string globalNote = SaveGlobalLuaIfChanged(id);
            (string scripts, string functions, string actions, string messageNote) = CollapseScriptMessagesForSave();
            string content = "//===== SCRIPTS =====//\n" + scripts.TrimEnd() +
                             "\n\n//===== FUNCTIONS =====//\n" + functions.TrimEnd() +
                             "\n\n//===== ACTIONS =====//\n" + actions.TrimEnd() + "\n";
            ScriptFileService.Save(currentProject, id, content);
            ShowSavedScriptMessages(scripts, functions, actions);
            scriptEdits.Saved(CurrentScriptDocument());
            UpdateScriptSectionCounts();
            ScriptSearchStatus.Text = currentScriptTextArchive is null
                ? $"Saved {id:D4}.script. Binary rebuild is not wired yet."
                : $"Saved {id:D4}.script and Text Archive {currentScriptTextArchive.Id:D4}.{messageNote}";
            ScriptSearchStatus.Text += globalNote;
        }
        catch (Exception ex)
        {
            LogUiError("Script could not be saved", ex);
            ScriptSearchStatus.Text = ex.Message;
        }
    }

    private void SearchScript_Click(object? sender, RoutedEventArgs e)
    {
        string query = ScriptSearchInput.Text?.Trim() ?? string.Empty;
        TextBox activeTextBox = GetActiveScriptTextBox();
        if (query.Length == 0)
        {
            activeTextBox.SelectionStart = 0;
            activeTextBox.SelectionEnd = 0;
            ScriptSearchStatus.Text = "Enter a search term.";
            return;
        }

        string text = activeTextBox.Text ?? string.Empty;
        int matches = CountMatches(text, query);
        SelectMatch(activeTextBox, query, text.IndexOf(query, StringComparison.OrdinalIgnoreCase), matches);
    }

    private void PreviousScriptMatch_Click(object? sender, RoutedEventArgs e)
    {
        MoveScriptMatch(forward: false);
    }

    private void NextScriptMatch_Click(object? sender, RoutedEventArgs e)
    {
        MoveScriptMatch(forward: true);
    }

    private void MoveScriptMatch(bool forward)
    {
        string query = ScriptSearchInput.Text?.Trim() ?? string.Empty;
        TextBox activeTextBox = GetActiveScriptTextBox();
        string text = activeTextBox.Text ?? string.Empty;
        if (query.Length == 0)
        {
            ScriptSearchStatus.Text = "Enter a search term.";
            return;
        }

        int matches = CountMatches(text, query);
        if (matches == 0)
        {
            SelectMatch(activeTextBox, query, -1, 0);
            return;
        }

        int match = forward
            ? text.IndexOf(query, Math.Min(activeTextBox.SelectionEnd, text.Length), StringComparison.OrdinalIgnoreCase)
            : FindPreviousMatch(text, query, Math.Max(activeTextBox.SelectionStart - 1, 0));

        if (match < 0)
        {
            match = forward
                ? text.IndexOf(query, StringComparison.OrdinalIgnoreCase)
                : FindPreviousMatch(text, query, text.Length);
        }

        SelectMatch(activeTextBox, query, match, matches);
    }

    private static int FindPreviousMatch(string text, string query, int start)
    {
        int match = -1;
        int offset = 0;
        while (offset <= start && offset <= text.Length - query.Length)
        {
            int candidate = text.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase);
            if (candidate < 0 || candidate > start)
            {
                break;
            }

            match = candidate;
            offset = candidate + Math.Max(query.Length, 1);
        }

        return match;
    }

    private void SelectMatch(TextBox textBox, string query, int match, int totalMatches)
    {
        if (match >= 0)
        {
            textBox.SelectionStart = match;
            textBox.SelectionEnd = match + query.Length;
            int ordinal = GetMatchOrdinal(textBox.Text ?? string.Empty, query, match);
            ScriptSearchStatus.Text = $"Match at {ordinal} of {totalMatches}.";
        }
        else
        {
            textBox.SelectionStart = 0;
            textBox.SelectionEnd = 0;
            ScriptSearchStatus.Text = "No matches in the current text view.";
        }
    }

    // Converts a character offset into its 1-based position among all matches.
    private static int GetMatchOrdinal(string text, string query, int matchOffset)
    {
        int ordinal = 0;
        int offset = 0;
        while (offset <= matchOffset && offset <= text.Length - query.Length)
        {
            int candidate = text.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase);
            if (candidate < 0)
            {
                break;
            }

            ordinal++;
            if (candidate == matchOffset)
            {
                break;
            }

            offset = candidate + Math.Max(query.Length, 1);
        }

        return ordinal;
    }

    private void ScriptSearchInput_TextChanged(object? sender, TextChangedEventArgs e)
    {
        SearchScript_Click(sender, e);
    }

    private TextBox GetActiveScriptTextBox() => ScriptSectionTabs.SelectedIndex switch
    {
        1 => FunctionsTextBox,
        2 => ActionsTextBox,
        3 => GlobalLuaToggle.IsChecked == true ? GlobalLuaTextBox : LuaTextBox,
        _ => ScriptsTextBox
    };

    private static int CountMatches(string text, string query)
    {
        int count = 0;
        int offset = 0;
        while (offset <= text.Length - query.Length)
        {
            int match = text.IndexOf(query, offset, StringComparison.OrdinalIgnoreCase);
            if (match < 0)
            {
                break;
            }

            count++;
            offset = match + Math.Max(query.Length, 1);
        }

        return count;
    }

    private void UpdateScriptSectionCounts()
    {
        ScriptsTab.Header = $"Scripts ({CountContainers(ScriptsTextBox.Text, "Script")})";
        FunctionsTab.Header = $"Functions ({CountContainers(FunctionsTextBox.Text, "Function")})";
        ActionsTab.Header = $"Actions ({CountContainers(ActionsTextBox.Text, "Action")})";
        UpdateLineNumbers(ScriptsTextBox, ScriptsLineNumbers);
        UpdateLineNumbers(FunctionsTextBox, FunctionsLineNumbers);
        UpdateLineNumbers(ActionsTextBox, ActionsLineNumbers);
        UpdateLineNumbers(LuaTextBox, LuaLineNumbers);
    }

    private static void UpdateLineNumbers(TextBox editor, TextBlock gutter)
    {
        int lineCount = Math.Max(1, (editor.Text ?? string.Empty).Count(c => c == '\n') + 1);
        gutter.Text = string.Join('\n', Enumerable.Range(1, lineCount));
    }

    private void ScriptSectionTextChanged(object? sender, TextChangedEventArgs e)
    {
        UpdateScriptSectionCounts();
        QueueScriptIconRefresh();
        if (!loadingScript && !updatingScriptText)
        {
            ApplyLiveScriptMessageEdits();
        }
    }

    private TextArchiveData? LoadScriptTextArchive(ushort scriptId, bool useOverride = true)
    {
        if (currentProject is null || headerStore is null ||
            !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            return null;
        }

        // Opened from the Map Editor, the script's messages come from that header's own text archive,
        // the same one the Text pane shows, rather than whichever header happens to use the script first.
        if (useOverride && scriptTextArchiveOverride is int archiveId)
        {
            try
            {
                return GetOpenTextArchive(archiveId, metadata);
            }
            catch (FileNotFoundException)
            {
            }
        }

        for (ushort headerId = 0; headerId < currentProject.GetHeaderCount(); headerId++)
        {
            MapHeaderRecord header = headerStore.Load(headerId);
            if (header.ScriptFileId != scriptId)
            {
                continue;
            }

            try
            {
                return GetOpenTextArchive(header.TextArchiveId, metadata);
            }
            catch (FileNotFoundException)
            {
                // A script can be referenced by more than one header; try the next
                // header if this project's expanded text archive is unavailable.
            }
        }

        return null;
    }

    private static int CountContainers(string? text, string containerName)
    {
        if (string.IsNullOrEmpty(text))
        {
            return 0;
        }

        return text.Split('\n')
            .Count(line => line.TrimStart().StartsWith(containerName + " ", StringComparison.OrdinalIgnoreCase) &&
                           line.TrimEnd().EndsWith(":", StringComparison.Ordinal));
    }

    private static (string scripts, string functions, string actions) SplitScriptSections(string content)
    {
        const string functionsMarker = "//===== FUNCTIONS =====//";
        const string actionsMarker = "//===== ACTIONS =====//";
        int functionsIndex = content.IndexOf(functionsMarker, StringComparison.OrdinalIgnoreCase);
        int actionsIndex = content.IndexOf(actionsMarker, StringComparison.OrdinalIgnoreCase);
        if (functionsIndex < 0 || actionsIndex < 0 || actionsIndex < functionsIndex)
        {
            return (content, string.Empty, string.Empty);
        }

        return (
            content[..functionsIndex].Replace("//===== SCRIPTS =====//", string.Empty, StringComparison.OrdinalIgnoreCase).Trim(),
            content[(functionsIndex + functionsMarker.Length)..actionsIndex].Trim(),
            content[(actionsIndex + actionsMarker.Length)..].Trim());
    }

    private void HeaderSelector_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (refreshingHeaderLabels) return;
        LoadSelectedHeader();
    }

    #region Map View layout

    // The Map View is one map area plus one editor pane whose content follows the buttons under the
    // map. Buildings/Permissions/Events each have a separate implementation per map view (header's
    // maps, whole matrix, single map); the pane shows the one belonging to the active view. The Wild
    // Encounters and Trainers panes borrow the standalone editors' own panels while they are open.
    private enum MapPane
    {
        Header,
        Buildings,
        Permissions,
        Wild,
        Trainers,
        Events,
        Script,
        LevelScript,
        Text,
        Matrix
    }

    private MapPane currentMapPane = MapPane.Header;
    private double mapEditorPaneWidth = 460;
    private readonly List<int> mapPaneTrainerIds = [];
    private bool loadingMapPane;

    private bool IsMapViewOpen => HeaderMapEditorPanel.IsVisible;

    /// <summary>The Map View hides the editor list and the page title so the map gets the window;
    /// its own "Editors" button brings the list back.</summary>
    private void SetMapViewChrome(bool mapView)
    {
        if (mapView) MapViewEditorsToggle.IsChecked = false;
        ApplyEditorSidebarVisibility(!mapView);
        PageHeader.IsVisible = !mapView;
        ContentArea.Margin = new Thickness(mapView ? 8 : 28);
        ContentBorder.Margin = mapView ? new Thickness(0, 0, 0, 6) : new Thickness(0, 24, 0, 24);
        ContentBorder.Padding = new Thickness(mapView ? 8 : 24);
    }

    private void ApplyEditorSidebarVisibility(bool show)
    {
        EditorSidebar.IsVisible = show;
        MainContent.ColumnDefinitions[0].Width = new GridLength(show ? 260 : 0);
    }

    private void MapViewEditorsToggle_Click(object? sender, RoutedEventArgs e) =>
        ApplyEditorSidebarVisibility(MapViewEditorsToggle.IsChecked == true);

    private void MapPaneButton_Click(object? sender, RoutedEventArgs e)
    {
        if (sender is not global::Avalonia.Controls.Primitives.ToggleButton { Tag: string tag } || !Enum.TryParse(tag, out MapPane pane)) return;
        currentMapPane = pane;
        if (MapViewHidePaneToggle.IsChecked == true)
        {
            MapViewHidePaneToggle.IsChecked = false;
            ApplyMapViewSplit();
        }

        ApplyMapPane();
    }

    private void MapViewHideMapToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (MapViewHideMapToggle.IsChecked == true) MapViewHidePaneToggle.IsChecked = false;
        ApplyMapViewSplit();
    }

    private void MapViewHidePaneToggle_Click(object? sender, RoutedEventArgs e)
    {
        if (MapViewHidePaneToggle.IsChecked == true) MapViewHideMapToggle.IsChecked = false;
        ApplyMapViewSplit();
    }

    private void ApplyMapViewSplit()
    {
        bool hideMap = MapViewHideMapToggle.IsChecked == true;
        bool hidePane = MapViewHidePaneToggle.IsChecked == true;
        ColumnDefinitions columns = MapViewSplitGrid.ColumnDefinitions;
        // The splitter drags this width directly, so it is read back before being replaced.
        if (columns[2].Width.IsAbsolute && columns[2].Width.Value > 0) mapEditorPaneWidth = columns[2].Width.Value;

        MapViewMapArea.IsVisible = !hideMap;
        MapEditorPane.IsVisible = !hidePane;
        MapViewSplitter.IsVisible = !hideMap && !hidePane;
        columns[0].Width = hideMap ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
        columns[1].Width = new GridLength(hideMap || hidePane ? 0 : 6);
        columns[2].Width = hidePane ? new GridLength(0)
            : hideMap ? new GridLength(1, GridUnitType.Star)
            : new GridLength(mapEditorPaneWidth);
    }

    private void MapModeTabs_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        // Selection changes of the combo boxes and lists inside the tabs bubble up to here too.
        if (!ReferenceEquals(e.Source, MapModeTabs) || MapEditorPane is null) return;
        SyncMapViewToHeader();
        ApplyMapPane();
    }

    private bool syncingHeaderFromMap;

    // The header at the top is what links the three map views: picking a matrix or a map moves the
    // header, and every view shows that header's part of the world. Each view remembers the header it
    // last matched, and one that is behind catches up when it is shown, so a hidden view never
    // converts maps nobody is looking at.
    private ushort? headerMapsViewHeader;
    private ushort? wholeMatrixViewHeader;
    private ushort? singleMapViewHeader;
    private int? shownWholeMatrixId;

    // The Matrix tab shows either the selected header's own maps (the shared overworld matrix holds hundreds
    // of maps, each outdoor header owns a few) or the whole matrix, which is also what Edit Matrix works on.
    // The two have separate viewers; this chooses the one shown, and the tab's other code asks which is active.
    private bool matrixHeaderScope = true;
    private bool? lastScopeHeaderWasOutdoor;

    private bool IsHeaderMapsView => ReferenceEquals(MapModeTabs.SelectedItem, MatrixMapTab) && matrixHeaderScope;

    private bool IsWholeMatrixView => ReferenceEquals(MapModeTabs.SelectedItem, MatrixMapTab) && !matrixHeaderScope;

    /// <summary>Opens the Matrix tab on the header's own maps or on the whole matrix.</summary>
    private void SetMatrixScope(bool headerScope)
    {
        matrixHeaderScope = headerScope;
        HeaderScopeBody.IsVisible = headerScope;
        WholeScopeBody.IsVisible = !headerScope;
        MatrixScopeHeaderButton.IsChecked = headerScope;
        MatrixScopeWholeButton.IsChecked = !headerScope;
        if (!ReferenceEquals(MapModeTabs.SelectedItem, MatrixMapTab))
        {
            MapModeTabs.SelectedItem = MatrixMapTab;
            return;
        }

        SyncMapViewToHeader();
        ApplyMapPane();
    }

    private async void MatrixScope_Click(object? sender, RoutedEventArgs e)
    {
        bool headerScope = sender is Control { Tag: "Header" };
        if (headerScope == matrixHeaderScope)
        {
            // Clicking the shown scope again must not un-press its button.
            MatrixScopeHeaderButton.IsChecked = matrixHeaderScope;
            MatrixScopeWholeButton.IsChecked = !matrixHeaderScope;
            return;
        }

        if (headerScope && IsEditingMatrix)
        {
            if (!await ConfirmLeavingEditedMatrixAsync())
            {
                MatrixScopeHeaderButton.IsChecked = false;
                MatrixScopeWholeButton.IsChecked = true;
                return;
            }

            EditMatrixToggle.IsChecked = false;
            if (currentMapPane == MapPane.Matrix) currentMapPane = MapPane.Header;
        }

        SetMatrixScope(headerScope);
    }

    private void ResetMapViewHeaders()
    {
        headerMapsViewHeader = wholeMatrixViewHeader = singleMapViewHeader = null;
        shownWholeMatrixId = null;
    }

    /// <summary>Brings the visible map view up to the selected header: the Header's Maps view to its own
    /// maps, the Whole Matrix view to its matrix, and the Single Map view to one of its maps (unless the
    /// map shown already is one).</summary>
    private void SyncMapViewToHeader()
    {
        if (selectedHeader is null || currentProject is null || MapModeTabs is null) return;
        MapHeaderRecord header = selectedHeader;
        if (IsHeaderMapsView)
        {
            if (headerMapsViewHeader == header.Id) return;
            headerMapsViewHeader = header.Id;
            ShowHeaderMaps(header);
        }
        else if (IsWholeMatrixView)
        {
            if (wholeMatrixViewHeader == header.Id) return;
            wholeMatrixViewHeader = header.Id;
            EnsureMatrixSelectorLoaded();
            MatrixListEntry? matrixEntry = allMapMatrices.FirstOrDefault(entry => entry.Id == header.MatrixId);
            if (matrixEntry is null) return;
            if (!ReferenceEquals(MapMatrixSelector.SelectedItem, matrixEntry))
            {
                // Its SelectionChanged handler loads the matrix (and keeps this header, whose matrix it is).
                MapMatrixSelector.SelectedItem = matrixEntry;
            }
            else if (shownWholeMatrixId != header.MatrixId)
            {
                ApplyHeaderTexturePacks(header, wholeMatrix: true);
                ShowMatrixComposite(IsEditingMatrix && currentMatrix is not null ? GetMatrixPlacements(currentMatrix) : GetMatrixPlacements(header.MatrixId), header.MatrixId);
            }
        }
        else if (ReferenceEquals(MapModeTabs.SelectedItem, SingleMapTab))
        {
            if (singleMapViewHeader == header.Id) return;
            singleMapViewHeader = header.Id;
            EnsureMapEditorDataLoaded();
            List<MapPlacement> placements = GetHeaderMapPlacements(header);
            int shownMapId = MapSelector.SelectedIndex >= 0 && MapSelector.SelectedIndex < mapEntries.Count ? mapEntries[MapSelector.SelectedIndex].Id : -1;
            if (placements.Count == 0 || placements.Any(placement => placement.MapId == shownMapId)) return;
            int index = mapEntries.FindIndex(entry => entry.Id == placements[0].MapId);
            if (index >= 0) MapSelector.SelectedIndex = index;
        }
    }

    private void ShowHeaderMaps(MapHeaderRecord header)
    {
        ApplyHeaderTexturePacks(header, wholeMatrix: false);
        List<MapPlacement> placements = GetHeaderMapPlacements(header);
        if (placements.Count == 0)
        {
            currentOverworldPlacements = [];
            HeaderMapsOverlay.SetCells([]);
            // The previous header's maps must not stay on screen.
            OverworldMapViewer.ClearScene(keepEventIcons: true);
            OverworldMapViewer.SetEventMarkers([]);
            OverworldMapViewerPlaceholder.Text = $"Header {header.Id:D4} has no maps in matrix {header.MatrixId:D4}.";
            OverworldMapViewerPlaceholder.IsVisible = true;
            OverworldViewerStatus.Text = $"Header {header.Id:D4} has no maps assigned in matrix {header.MatrixId:D4}.";
        }
        else
        {
            _ = LoadCompositeMapsAsync(placements);
        }
    }

    /// <summary>In the Single Map view the map is what the user picked, so the header at the top (and
    /// with it the Header, Wild Encounters and Trainers panes) follows to the header that owns that
    /// map. A map no header references leaves the header as it is.</summary>
    /// <summary>In the Whole Matrix view the header at the top follows the matrix: the header that uses
    /// it (a header whose own matrix it is), else the header of its first placed square.</summary>
    private void SyncHeaderToMatrix(int matrixId)
    {
        if (headerStore is null) return;
        if (selectedHeader?.MatrixId == matrixId)
        {
            wholeMatrixViewHeader = selectedHeader.Id;
            return;
        }

        ushort? headerId = allHeaders.Select(entry => (ushort?)entry.Id).FirstOrDefault(id => headerStore.Load(id!.Value).MatrixId == matrixId);
        if (headerId is null && currentProject is not null)
        {
            try
            {
                MatrixDocument matrix = MatrixService.Load(currentProject, matrixId);
                if (matrix.HasHeadersSection && ResolveMatrixFirstHeader(matrix) is ushort cellHeader) headerId = cellHeader;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"[DSPRE] Matrix {matrixId:D4} could not be read to find its header: {ex.Message}");
            }
        }

        if (headerId is ushort id) SelectHeaderQuietly(id);
        wholeMatrixViewHeader = selectedHeader?.Id;
    }

    private static ushort? ResolveMatrixFirstHeader(MatrixDocument matrix)
    {
        for (int row = 0; row < matrix.Height; row++)
        {
            for (int col = 0; col < matrix.Width; col++)
            {
                if (matrix.Maps[row, col] != MatrixDocument.EmptyMap) return matrix.Headers[row, col];
            }
        }

        return null;
    }

    /// <summary>Selects a header without re-routing the map views to that header's own maps.</summary>
    private void SelectHeaderQuietly(ushort headerId)
    {
        if (selectedHeader?.Id == headerId) return;
        syncingHeaderFromMap = true;
        try
        {
            if (!string.IsNullOrEmpty(HeaderSearchInput.Text)) ClearHeaderFilters();
            for (int i = 0; i < HeaderSelector.Items.Count; i++)
            {
                if (HeaderSelector.Items[i] is HeaderListEntry entry && entry.Id == headerId)
                {
                    HeaderSelector.SelectedIndex = i;
                    break;
                }
            }
        }
        finally
        {
            syncingHeaderFromMap = false;
        }
    }

    private void SyncHeaderToSingleMap()
    {
        if (!ReferenceEquals(MapModeTabs.SelectedItem, SingleMapTab)) return;
        if (MapSelector.SelectedIndex < 0 || MapSelector.SelectedIndex >= mapEntries.Count) return;

        EnsureLocationLookupsLoaded();
        int mapId = mapEntries[MapSelector.SelectedIndex].Id;
        // A map several headers use stays with the current header when it is one of them, so the Single
        // Map view following the header never throws the header over to another one.
        bool currentHeaderHasMap = selectedHeader is not null && GetHeaderMapPlacements(selectedHeader).Any(placement => placement.MapId == mapId);
        if (!currentHeaderHasMap && mapHeaderIds.TryGetValue(mapId, out ushort headerId)) SelectHeaderQuietly(headerId);
        singleMapViewHeader = selectedHeader?.Id;
        RefreshMapOwnerHeader();
    }

    /// <summary>Opens one map in the Single Map view with the given pane, for the panes that only
    /// work per map (move permissions, events) when reached from a multi-map view.</summary>
    /// <summary>Saves everything the Map View edits: the header, and in the Single Map view the map's
    /// buildings, move permissions, the header's events and the texture packs its area data picks.
    /// Parts that did not change are left alone, so the edit journal only records real changes.</summary>
    private void SaveMap_Click(object? sender, RoutedEventArgs e)
    {
        if (blockingLoadCount > 0 || currentProject is null) return;
        var saved = new List<string>();
        try
        {
            if (selectedHeader is not null)
            {
                if (!SaveCurrentHeader(out bool headerChanged)) return;
                if (headerChanged) saved.Add($"header {selectedHeader.Id:D3}");
            }
            saved.AddRange(SavePendingHeaders().Select(id => $"header {id:D3}"));

            bool single = ReferenceEquals(MapModeTabs.SelectedItem, SingleMapTab);
            bool singleMap = single && MapSelector.SelectedIndex >= 0 && MapSelector.SelectedIndex < mapEntries.Count;
            bool reloadMap = false;
            if (IsEditingMatrix && currentMatrix is not null && matrixEdits.IsUnsaved(currentMatrix))
            {
                string moved = SaveMatrixWithEvents(currentMatrix);
                matrixEdits.Saved(currentMatrix);
                saved.Add($"matrix {currentMatrix.Id:D4}{moved}");
            }

            // Buildings and permissions are saved for every map edited, whichever view it was edited in.
            foreach (ushort mapId in MapsWithUnsavedBuildings())
            {
                List<BuildingRecord> buildings = sharedMapBuildings[mapId];
                MapBuildingService.Save(currentProject, mapId, buildings);
                sharedMapSavedBuildings[mapId] = buildings.Select(building => building.Snapshot()).ToList();
                if (ReferenceEquals(buildings, currentBuildings)) savedBuildings = sharedMapSavedBuildings[mapId];
                InvalidateCachedMapModels([mapId]);
                saved.Add($"buildings of map {mapId:D4}");
                if (singleMap && mapEntries[MapSelector.SelectedIndex].Id == mapId) reloadMap = true;
            }
            RefreshBuildingDirtyHighlight();

            foreach ((int mapId, byte[,] types, byte[,] collisions) in UnsavedPermissions())
            {
                MapPermissionsService.Save(currentProject, mapId, types, collisions);
                pendingPermissions.Remove(mapId);
                saved.Add($"move permissions of map {mapId:D4}");
            }

            if (singleMap)
            {
                MapViewEntry map = mapEntries[MapSelector.SelectedIndex];
                if (SaveMapTexturePacks(map) is string texturePacks) saved.Add(texturePacks);
                if (reloadMap) LoadSelectedMap();
            }

            // Events are saved whichever view is showing, including files edited before moving to another map.
            foreach (MapEventFile events in UnsavedEventFiles())
            {
                MapEventService.Save(currentProject, events);
                sharedMapSavedEvents[events.Id] = CloneEventFile(events);
                saved.Add($"events (event file {events.Id:D4})");
            }
            RefreshEventDirtyHighlight();

            string status = saved.Count == 0 ? "Nothing has changed since the last save." : $"Saved {string.Join(", ", saved)}.";
            SaveMapHint.Text = status;
            StatusText.Text = status;
        }
        catch (Exception ex)
        {
            LogUiError("The map could not be saved", ex);
            StatusText.Text = $"The map could not be saved: {ex.Message}";
        }
    }

    // The map whose grids currentPermissionTypes/currentPermissionCollisions hold, and the edited grids of
    // maps the Single Map view has moved away from, kept until they are saved.
    private int permissionsMapId = -1;
    private readonly Dictionary<int, (byte[,] Types, byte[,] Collisions)> pendingPermissions = [];

    private bool PermissionsUnsaved(int mapId, byte[,] types, byte[,] collisions)
    {
        if (currentProject is null) return false;
        (byte[,] savedTypes, byte[,] savedCollisions) = MapPermissionsService.Load(currentProject, mapId);
        return !GridsEqual(savedTypes, types) || !GridsEqual(savedCollisions, collisions);
    }

    /// <summary>Drops the edits to a map's permissions, after its file was replaced or removed.</summary>
    private void ForgetPermissions(int mapId)
    {
        pendingPermissions.Remove(mapId);
        if (permissionsMapId == mapId) permissionsMapId = -1;
        ClearPermissionHistory();
    }

    private void KeepUnsavedPermissions()
    {
        if (permissionsMapId >= 0 && PermissionsUnsaved(permissionsMapId, currentPermissionTypes, currentPermissionCollisions))
        {
            pendingPermissions[permissionsMapId] = (currentPermissionTypes, currentPermissionCollisions);
        }
    }

    /// <summary>Every map whose move permissions differ from the saved ones: the one shown and those left
    /// with edits.</summary>
    private List<(int MapId, byte[,] Types, byte[,] Collisions)> UnsavedPermissions()
    {
        var unsaved = pendingPermissions.Select(pair => (pair.Key, pair.Value.Types, pair.Value.Collisions)).ToList();
        if (permissionsMapId >= 0 && !pendingPermissions.ContainsKey(permissionsMapId)
            && PermissionsUnsaved(permissionsMapId, currentPermissionTypes, currentPermissionCollisions))
        {
            unsaved.Add((permissionsMapId, currentPermissionTypes, currentPermissionCollisions));
        }
        return unsaved.OrderBy(entry => entry.Key).ToList();
    }

    /// <summary>Every map whose buildings differ from the saved ones, in whichever view they were edited.</summary>
    private List<ushort> MapsWithUnsavedBuildings() =>
        sharedMapBuildings
            .Where(pair => sharedMapSavedBuildings.TryGetValue(pair.Key, out List<BuildingRecord>? saved)
                && (pair.Value.Count != saved.Count || pair.Value.Where((building, index) => !BuildingsEqual(building, saved[index])).Any()))
            .Select(pair => pair.Key)
            .Order()
            .ToList();

    private static bool GridsEqual(byte[,] a, byte[,] b) =>
        a.GetLength(0) == b.GetLength(0) && a.GetLength(1) == b.GetLength(1) && a.Cast<byte>().SequenceEqual(b.Cast<byte>());

    /// <summary>The texture pack selectors choose what the map is drawn with; saving writes that choice
    /// into the area data the map's header uses, which every map sharing that area data picks up.
    /// "Untextured" is a viewing option only and is never saved.</summary>
    private string? SaveMapTexturePacks(MapViewEntry map)
    {
        if (currentProject is null || !mapAreaData.TryGetValue(map.Id, out AreaDataRecord? areaData)) return null;
        int mapPack = MapTextureSelector.SelectedIndex - 1;
        int buildingPack = MapBuildingTextureSelector.SelectedIndex - 1;
        bool mapChanged = mapPack >= 0 && mapPack != areaData.MapTileset;
        bool buildingChanged = buildingPack >= 0 && buildingPack != areaData.BuildingsTileset;
        if (!mapChanged && !buildingChanged) return null;

        if (mapChanged) areaData.MapTileset = (ushort)mapPack;
        if (buildingChanged) areaData.BuildingsTileset = (ushort)buildingPack;
        AreaDataService.Save(currentProject, areaData, IsHeartGoldSoulSilver());
        int sharing = mapAreaData.Values.Count(record => ReferenceEquals(record, areaData));
        return $"texture packs (area data {areaData.Id:D3}, used by {sharing} map{(sharing == 1 ? "" : "s")})";
    }

    private static IEnumerable<(int Row, int Column, string Label)> PlacementCells(IEnumerable<MapPlacement> placements) =>
        placements.Select(placement => (placement.Row, placement.Column, placement.MapId.ToString("D4")));

    private void OpenMapAtPoint(MapGlView viewer, MatrixGridOverlay overlay, List<MapPlacement> placements, Point point)
    {
        if (IsEditingMatrix && ReferenceEquals(viewer, IndoorMapViewer)) return;
        if (!viewer.TryPickGround(point, out float worldX, out float worldZ) ||
            !overlay.TryGetCell(worldX, worldZ, out int row, out int column)) return;
        MapPlacement? placement = placements.FirstOrDefault(candidate => candidate.Row == row && candidate.Column == column);
        if (placement is null) return;
        EnsureMapEditorDataLoaded();
        int index = mapEntries.FindIndex(map => map.Id == placement.MapId);
        if (index < 0) return;
        overlay.Select(row, column);
        // Per-map panes carry over; the others (wild, script...) belong to the header and stay put too.
        OpenMapInSingleView(index, currentMapPane == MapPane.Matrix ? MapPane.Header : currentMapPane);
    }

    /// <summary>The single map's building and event highlights and click-picking only apply in their own
    /// pane, so a click while editing buildings never selects an event (and vice versa) and neither
    /// stays highlighted on the permission grid.</summary>
    private void ApplyMapSelectionModes()
    {
        MapViewer.BuildingPickingEnabled = currentMapPane == MapPane.Buildings;
        MapViewer.EventPickingEnabled = currentMapPane == MapPane.Events;
        HighlightSelectedBuilding();
        HighlightSelectedEvent();
    }

    private void HighlightSelectedBuilding() =>
        MapViewer.SetSelectedPart(currentMapPane == MapPane.Buildings ? BuildingListBox.SelectedIndex : -1);

    private void HighlightSelectedEvent() =>
        MapViewer.SetSelectedEvent(currentMapPane == MapPane.Events ? EventListBox.SelectedIndex : -1);

    private void OpenMapInSingleView(int mapIndex, MapPane pane)
    {
        // The map is being chosen here, so switching to the view must not first jump to the header's.
        singleMapViewHeader = selectedHeader?.Id;
        currentMapPane = pane;
        MapModeTabs.SelectedItem = SingleMapTab;
        if (MapSelector.SelectedIndex == mapIndex) ApplyMapPane();
        else MapSelector.SelectedIndex = mapIndex;
    }

    /// <summary>Shows the content of the current pane for the active map view.</summary>
    private void ApplyMapPane()
    {
        if (MapEditorPane is null || MapModeTabs is null) return;
        bool single = ReferenceEquals(MapModeTabs.SelectedItem, SingleMapTab);
        bool wholeMatrix = IsWholeMatrixView;
        // Matrix editing only exists in the Whole Matrix view; leaving that view ends it.
        if (!wholeMatrix && EditMatrixToggle.IsChecked == true)
        {
            EditMatrixToggle.IsChecked = false;
            if (currentMapPane == MapPane.Matrix) currentMapPane = MapPane.Header;
        }

        if (currentMapPane == MapPane.Matrix && !IsEditingMatrix) currentMapPane = MapPane.Header;
        MatrixOverlay.IsVisible = wholeMatrix;
        if (wholeMatrix && IsEditingMatrix && currentMatrix is not null) MatrixOverlay.SetMatrix(currentMatrix.Height, currentMatrix.Width, DescribeMatrixCell);
        else if (wholeMatrix) MatrixOverlay.SetCells(PlacementCells(currentIndoorPlacements));
        MapPane pane = currentMapPane;
        ApplyMapSelectionModes();
        // Buildings, permissions and events belong to single map files. In the multi-map views the
        // pane lists the maps shown instead, and picking one opens it in the Single Map view.
        bool perMapPane = pane is MapPane.Buildings or MapPane.Permissions or MapPane.Events;

        MapPaneHeader.IsVisible = pane == MapPane.Header;
        MapSidebarBorder.IsVisible = perMapPane && single;
        MapPaneMapPicker.IsVisible = perMapPane && !single;
        MapPaneWild.IsVisible = pane == MapPane.Wild;
        MapPaneTrainers.IsVisible = pane == MapPane.Trainers;
        MapPaneScript.IsVisible = pane == MapPane.Script;
        MapPaneLevelScript.IsVisible = pane == MapPane.LevelScript;
        MapPaneText.IsVisible = pane == MapPane.Text;
        MapPaneMatrix.IsVisible = pane == MapPane.Matrix;

        if (single)
        {
            // Selecting the Permissions tab is what locks the single map's camera overhead and draws
            // the permission grid (MapSidebarTabs_SelectionChanged), so any other pane must leave it.
            MapSidebarTabs.SelectedItem = pane switch
            {
                MapPane.Permissions => PermissionsTabItem,
                MapPane.Events => EventsTabItem,
                _ => BuildingsTabItem
            };
        }

        if (perMapPane && !single) RefreshMapPaneMapPicker(pane);
        if (pane == MapPane.Wild) ShowMapPaneWildEncounters();
        if (pane == MapPane.Trainers) ShowMapPaneTrainers();
        if (pane == MapPane.Script) ShowMapPaneScript();
        if (pane == MapPane.LevelScript) ShowMapPaneLevelScript();
        if (pane == MapPane.Text) ShowMapPaneText();

        if (pane == MapPane.Header) RefreshMapOwnerHeader();
        MapPaneHeaderButton.IsChecked = pane == MapPane.Header;
        MapPaneBuildingsButton.IsChecked = pane == MapPane.Buildings;
        MapPanePermissionsButton.IsChecked = pane == MapPane.Permissions;
        MapPaneWildButton.IsChecked = pane == MapPane.Wild;
        MapPaneTrainersButton.IsChecked = pane == MapPane.Trainers;
        MapPaneEventsButton.IsChecked = pane == MapPane.Events;
        MapPaneScriptButton.IsChecked = pane == MapPane.Script;
        MapPaneLevelScriptButton.IsChecked = pane == MapPane.LevelScript;
        MapPaneTextButton.IsChecked = pane == MapPane.Text;
        SaveMapHint.Text = single ? string.Empty
            : IsEditingMatrix ? "Saves the header and the matrix."
            : "Only the header can be edited here; pick a single map for the rest.";
    }

    private void RefreshMapPaneMapPicker(MapPane pane)
    {
        bool overworld = IsHeaderMapsView;
        string what = pane switch
        {
            MapPane.Buildings => "Buildings",
            MapPane.Permissions => "Movement permissions",
            _ => "Events"
        };
        MapPaneMapPickerText.Text = $"{what} are edited one map at a time. Pick one of the maps shown to open it in the Single Map view.";
        loadingMapPane = true;
        try
        {
            MapPaneMapPickerList.Items.Clear();
            foreach (MapPlacement placement in overworld ? currentOverworldPlacements : currentIndoorPlacements)
            {
                MapPaneMapPickerList.Items.Add(new MapReference(placement.MapId, placement.Name));
            }
        }
        finally
        {
            loadingMapPane = false;
        }
    }

    private void MapPaneMapPickerList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingMapPane || MapPaneMapPickerList.SelectedItem is not MapReference reference) return;
        EnsureMapEditorDataLoaded();
        int index = mapEntries.FindIndex(map => map.Id == reference.MapId);
        if (index >= 0) OpenMapInSingleView(index, currentMapPane);
    }

    /// <summary>In the Map Editor the script file is the header's own: it cannot be swapped, and the header jumps and the text archive's
    /// messages (written in the script instead) are left out. The Script Editor page keeps all of them.</summary>
    private void SetScriptPanelForMapView(bool inMapView)
    {
        ScriptFilePickerLabel.IsVisible = ScriptFileSelector.IsVisible = ScriptFileFindInput.IsVisible = ScriptFileFindCount.IsVisible = !inMapView;
        ScriptHeadersBar.IsVisible = !inMapView;
        ScriptMessagesPanel.IsVisible = ScriptMessagesSplitter.IsVisible = !inMapView;
        ScriptBodyGrid.ColumnDefinitions[1].Width = inMapView ? new GridLength(0) : GridLength.Auto;
        ScriptBodyGrid.ColumnDefinitions[2].Width = inMapView ? new GridLength(0) : new GridLength(360);
    }

    private void ShowMapPaneScript()
    {
        MoveToHost(ScriptEditorPanel, MapPaneScriptHost);
        SetScriptPanelForMapView(true);
        ScriptEditorPanel.IsVisible = false;
        if (selectedHeader is null)
        {
            MapPaneScriptStatus.Text = "No header is selected.";
            return;
        }

        if (scriptFileIds.Count == 0) ConfigureScriptEditor();
        ScriptEditorPanel.IsVisible = true;
        bool archiveChanged = scriptTextArchiveOverride != selectedHeader.TextArchiveId;
        scriptTextArchiveOverride = selectedHeader.TextArchiveId;
        int previousScript = ScriptFileSelector.SelectedIndex;
        SelectScriptFileId(selectedHeader.ScriptFileId);
        if (ScriptFileSelector.SelectedIndex == previousScript)
        {
            if (archiveChanged && !HasOpenDocumentChanges("Script Editor")) LoadSelectedScriptFile();
            else RefreshScriptMessages();
        }
        MapPaneScriptStatus.Text = $"Script file {ScriptFileLabel(selectedHeader.ScriptFileId, "")}, used by header {selectedHeader.Id:D3}.";
    }

    /// <summary>In the Map Editor the level script and text archive are the header's own: no picking another one, finding one by name,
    /// header jumps, or adding and removing archives.</summary>
    private void SetLevelScriptAndTextPanelsForMapView(bool inMapView)
    {
        LevelScriptPickerLabel.IsVisible = LevelScriptSelector.IsVisible = LevelScriptFindInput.IsVisible = LevelScriptFindCount.IsVisible = !inMapView;
        LevelScriptHeadersBar.IsVisible = !inMapView;
        TextArchivePickerLabel.IsVisible = TextArchiveSelector.IsVisible = AddTextArchiveButton.IsVisible = RemoveTextArchiveButton.IsVisible = TextSearchAllArchives.IsVisible = !inMapView;
    }

    private void ShowMapPaneLevelScript()
    {
        MoveToHost(LevelScriptEditorPanel, MapPaneLevelScriptHost);
        SetLevelScriptAndTextPanelsForMapView(true);
        LevelScriptEditorPanel.IsVisible = false;
        if (selectedHeader is null)
        {
            MapPaneLevelScriptStatus.Text = "No header is selected.";
            return;
        }

        if (levelScriptIds.Count == 0) ConfigureLevelScriptEditor();
        if (!levelScriptIds.Contains(selectedHeader.LevelScriptId))
        {
            MapPaneLevelScriptStatus.Text = $"Level script {LevelScriptLabel(selectedHeader.LevelScriptId, "")} (used by header {selectedHeader.Id:D3}) was not found.";
            return;
        }

        LevelScriptEditorPanel.IsVisible = true;
        SelectLevelScriptId(selectedHeader.LevelScriptId);
        MapPaneLevelScriptStatus.Text = $"Level script {LevelScriptLabel(selectedHeader.LevelScriptId, "")}, used by header {selectedHeader.Id:D3}.";
    }

    private void ShowMapPaneText()
    {
        MoveToHost(TextEditorPanel, MapPaneTextHost);
        SetLevelScriptAndTextPanelsForMapView(true);
        TextEditorPanel.IsVisible = false;
        if (selectedHeader is null)
        {
            MapPaneTextStatus.Text = "No header is selected.";
            return;
        }

        if (textArchiveIds.Count == 0) ConfigureTextEditor();
        int archiveIndex = textArchiveIds.IndexOf(selectedHeader.TextArchiveId);
        if (archiveIndex < 0)
        {
            MapPaneTextStatus.Text = $"Text archive {TextArchiveLabel(selectedHeader.TextArchiveId, "")} (used by header {selectedHeader.Id:D3}) was not decoded.";
            return;
        }

        TextEditorPanel.IsVisible = true;
        if (currentTextArchive?.Id == selectedHeader.TextArchiveId) RefreshTextMessageRows();
        else _ = ShowMapPaneTextArchiveAsync(archiveIndex, selectedHeader);
        MapPaneTextStatus.Text = $"Text archive {TextArchiveLabel(selectedHeader.TextArchiveId, "")}, used by header {selectedHeader.Id:D3}.";
    }

    private async Task ShowMapPaneTextArchiveAsync(int archiveIndex, MapHeaderRecord header)
    {
        if (!await OpenTextArchiveAsync(archiveIndex))
        {
            MapPaneTextStatus.Text = $"Still showing text archive {currentTextArchive?.Id:D4}, which has unsaved changes; header {header.Id:D3} uses {header.TextArchiveId:D4}.";
        }
    }

    private static void MoveToHost(Control control, Panel host)
    {
        if (ReferenceEquals(control.Parent, host)) return;
        (control.Parent as Panel)?.Children.Remove(control);
        host.Children.Add(control);
    }

    /// <summary>Puts the Wild Encounter and Trainer editors' panels back on their own pages. They are
    /// single controls, so the Map View's panes can only show them by taking them from there.</summary>
    private void RestoreSharedEditorPanels()
    {
        SetScriptPanelForMapView(false);
        SetLevelScriptAndTextPanelsForMapView(false);
        foreach (Control panel in new Control[] { WildEncounterEditorPanel, TrainerEditorPanel, ScriptEditorPanel, LevelScriptEditorPanel, TextEditorPanel })
        {
            if (ReferenceEquals(panel.Parent, EditorPanelsHost)) continue;
            MoveToHost(panel, EditorPanelsHost);
            panel.IsVisible = false;
        }
    }

    private void ShowMapPaneWildEncounters()
    {
        MoveToHost(WildEncounterEditorPanel, MapPaneWildHost);
        WildEncounterEditorPanel.IsVisible = false;
        if (selectedHeader is null)
        {
            MapPaneWildStatus.Text = "No header is selected.";
            return;
        }

        if (wildEncounterIds.Count == 0) ConfigureWildEncounterEditor();
        int selectorIndex = wildEncounterIds.IndexOf(selectedHeader.WildPokemonId);
        if (selectorIndex < 0)
        {
            MapPaneWildStatus.Text = $"Header {selectedHeader.Id:D3} has no wild encounter file (its wild Pokemon ID is {selectedHeader.WildPokemonId}). Set one in the Header pane to edit encounters here.";
            return;
        }

        WildEncounterEditorPanel.IsVisible = true;
        MapPaneWildStatus.Text = $"Wild encounter file {selectedHeader.WildPokemonId:D4}, used by header {selectedHeader.Id:D3}.";
        if (WildEncounterSelector.SelectedIndex != selectorIndex) _ = OpenWildEncounterAsync(selectorIndex);
    }

    /// <summary>Lists the trainers whose overworld events sit in the header's event file, read from the
    /// same in-memory copy the Events pane edits, so a trainer just placed there shows up here too.</summary>
    private void ShowMapPaneTrainers()
    {
        MoveToHost(TrainerEditorPanel, MapPaneTrainerHost);
        TrainerEditorPanel.IsVisible = false;
        int? previousId = MapPaneTrainerList.SelectedIndex >= 0 && MapPaneTrainerList.SelectedIndex < mapPaneTrainerIds.Count
            ? mapPaneTrainerIds[MapPaneTrainerList.SelectedIndex]
            : null;

        loadingMapPane = true;
        try
        {
            mapPaneTrainerIds.Clear();
            MapPaneTrainerList.Items.Clear();
            if (selectedHeader is null || currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
            {
                MapPaneTrainerStatus.Text = "No header is selected.";
                return;
            }

            if (trainerIds.Count == 0) ConfigureTrainerEditor();
            foreach (OverworldEvent overworld in GetSharedMapEvents(selectedHeader.EventFileId).Overworlds)
            {
                if (overworld.Type != OverworldEvent.TypeTrainer) continue;
                if (TrainerService.GetTrainerIdForOverworldScript(metadata, overworld.ScriptNumber) is not int trainerId) continue;
                if (!trainerIds.Contains(trainerId) || mapPaneTrainerIds.Contains(trainerId)) continue;

                mapPaneTrainerIds.Add(trainerId);
                MapPaneTrainerList.Items.Add($"{trainerId:D3} - {TrainerDisplayName(trainerId)}");
            }

            int placedCount = mapPaneTrainerIds.Count;
            // Trainers fought straight from the header's scripts (gym leaders, rivals, villains) have no
            // trainer overworld of their own.
            foreach (ScriptTrainerBattle battle in ScriptFileService.FindTrainerBattles(currentProject, selectedHeader.ScriptFileId))
            {
                if (!trainerIds.Contains(battle.TrainerId) || mapPaneTrainerIds.Contains(battle.TrainerId)) continue;

                mapPaneTrainerIds.Add(battle.TrainerId);
                MapPaneTrainerList.Items.Add($"{battle.TrainerId:D3} - {TrainerDisplayName(battle.TrainerId)} ({battle.Command} in {battle.Section})");
            }

            int scriptedCount = mapPaneTrainerIds.Count - placedCount;
            MapPaneTrainerStatus.Text = mapPaneTrainerIds.Count == 0
                ? $"No trainers are placed in event file {selectedHeader.EventFileId:D4} or fought in script file {ScriptFileLabel(selectedHeader.ScriptFileId, "")}, the ones header {selectedHeader.Id:D3} uses."
                : $"{placedCount} trainer(s) placed in event file {selectedHeader.EventFileId:D4}, {scriptedCount} more fought in script file {ScriptFileLabel(selectedHeader.ScriptFileId, "")}.";
        }
        catch (Exception ex)
        {
            LogUiError("Trainers on the map could not be listed", ex);
            MapPaneTrainerStatus.Text = ex.Message;
        }
        finally
        {
            loadingMapPane = false;
        }

        if (mapPaneTrainerIds.Count == 0) return;
        TrainerEditorPanel.IsVisible = true;
        int index = previousId is int id ? mapPaneTrainerIds.IndexOf(id) : -1;
        MapPaneTrainerList.SelectedIndex = Math.Max(index, 0);
        ShowSelectedMapPaneTrainer();
    }

    private string TrainerDisplayName(int trainerId) =>
        trainerNamesCache.TryGetValue(trainerId, out string? cachedName) ? cachedName : $"Trainer {trainerId}";

    private void MapPaneTrainerList_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (loadingMapPane) return;
        ShowSelectedMapPaneTrainer();
    }

    private void ShowSelectedMapPaneTrainer()
    {
        int listIndex = MapPaneTrainerList.SelectedIndex;
        if (listIndex < 0 || listIndex >= mapPaneTrainerIds.Count) return;

        // The trainer editor's own filter would otherwise hide the trainer being asked for.
        if (!string.IsNullOrEmpty(TrainerFilterInput.Text)) TrainerFilterInput.Text = string.Empty;
        if (filteredTrainerIds.Contains(mapPaneTrainerIds[listIndex])) _ = OpenTrainerAsync(mapPaneTrainerIds[listIndex]);
    }

    #endregion

    private void AreaIconInput_SelectionChanged(object? sender, SelectionChangedEventArgs e)
    {
        if (currentProject?.TryGetGameMetadata(out RomGameMetadata? metadata) == true && metadata is not null &&
            AreaIconInput.SelectedIndex >= 0)
        {
            UpdateAreaIconImage((ushort)AreaIconInput.SelectedIndex, metadata.HeaderFormat);
        }
    }

    private void UpdateAreaIconImage(ushort iconId, MapHeaderFormat format)
    {
        string? fileName = format switch
        {
            MapHeaderFormat.HeartGoldSoulSilver => iconId switch
            {
                1 => "hgsswall.png",
                2 => "hgsswood.png",
                3 => "hgsstown.png",
                4 => "hgsscave.png",
                5 => "hgssforest.png",
                6 => "hgsswater.png",
                7 => "hgssfield.png",
                8 => "hgsslake.png",
                9 => "hgssgray.png",
                _ => null
            },
            MapHeaderFormat.Platinum => iconId <= 9 ? $"areaicon{iconId:D2}.png" : null,
            _ => "dp.png"
        };

        string path = fileName is null
            ? string.Empty
            : Path.Combine(AppContext.BaseDirectory, "Resources", "Area Icons", fileName);
        (AreaIconImage.Source as IDisposable)?.Dispose();
        AreaIconImage.Source = File.Exists(path) ? new Bitmap(path) : null;
    }

    private void LoadSelectedHeader()
    {
        if (headerStore is null || HeaderSelector.SelectedIndex < 0)
        {
            return;
        }

        try
        {
            if (HeaderSelector.SelectedItem is not HeaderListEntry selectedEntry)
            {
                return;
            }

            KeepUnsavedHeader();
            selectedHeader = headerStore.Load(selectedEntry.Id);
            // A header left with unsaved edits shows them again; selectedHeader stays the saved one, which
            // HeaderHasUnsavedChanges compares the fields against.
            MapHeaderRecord shown = selectedHeader;
            string? shownName = null;
            if (pendingHeaders.Remove(selectedHeader.Id, out (MapHeaderRecord Record, string Name) pending)) (shown, shownName) = pending;
            RomGameMetadata? metadata = null;
            // When the header is only following the matrix or map picked in a map view, that view stays
            // as it is; the other views catch up when they are shown (SyncMapViewToHeader).
            if (currentProject?.TryGetGameMetadata(out metadata) == true && metadata is not null && !syncingHeaderFromMap)
            {
                EnsureMapEditorDataLoaded();
                EnsureMatrixSelectorLoaded();
                // Matrix 0 is the shared overworld matrix: every outdoor location's header owns a
                // handful of cells scattered across it, so an outdoor header opens on just its own maps
                // (see GetHeaderMapPlacements), while indoor/standalone locations, each with their own
                // matrix, open on the whole matrix.
                bool outdoor = selectedHeader.MatrixId == OverworldMatrixId;
                bool kindChanged = lastScopeHeaderWasOutdoor != outdoor;
                lastScopeHeaderWasOutdoor = outdoor;
                // The scope only follows the header when the kind of place changes, so a choice made for
                // one outdoor header is kept for the next one.
                if (kindChanged && !IsEditingMatrix) SetMatrixScope(headerScope: outdoor);
                else if (!ReferenceEquals(MapModeTabs.SelectedItem, MatrixMapTab)) MapModeTabs.SelectedItem = MatrixMapTab;
                SyncMapViewToHeader();
            }
            string headerDisplayName = shownName ?? currentProject?.GetInternalName(shown.Id) ?? string.Empty;
            HeaderSummaryName.Text = $"{selectedHeader.Id:D4} - {headerDisplayName}";
            HeaderNameInput.Text = headerDisplayName;
            FillHeaderIdCombos();
            SelectIdChoice(AreaDataInput, shown.AreaDataId);
            SelectIdChoice(MatrixInput, shown.MatrixId);
            SelectIdChoice(ScriptInput, shown.ScriptFileId);
            SelectIdChoice(LevelScriptInput, shown.LevelScriptId);
            SelectIdChoice(TextArchiveInput, shown.TextArchiveId);
            SelectOption(MusicDayInput, shown.MusicDayId);
            SelectOption(MusicNightInput, shown.MusicNightId);
            SelectWildEncounters(shown.WildPokemonId);
            SelectIdChoice(EventInput, shown.EventFileId);
            SelectOption(LocationInput, shown.LocationNameId);
            SelectOption(LocationSpecifierInput, shown.LocationSpecifier);
            SelectOption(AreaIconInput, shown.AreaIcon);
            if (metadata is not null)
            {
                UpdateAreaIconImage(shown.AreaIcon, metadata.HeaderFormat);
            }
            SelectOption(WeatherInput, shown.WeatherId);
            SelectOption(CameraInput, shown.CameraAngleId);
            BattleBackgroundInput.Text = shown.BattleBackground.ToString();
            FlagsInput.Text = shown.Flags.ToString();
            FollowModeInput.Text = shown.FollowMode.ToString();
            LocationTypeInput.Text = shown.LocationType.ToString();
            WorldMapXInput.Text = shown.WorldMapX.ToString();
            WorldMapYInput.Text = shown.WorldMapY.ToString();
            Unknown0Input.Text = shown.Unknown0.ToString();
            Unknown1Input.Text = shown.Unknown1.ToString();
            KantoFlagInput.IsChecked = shown.KantoFlag;
            LoadHeadersUsingScript(shown.ScriptFileId);
        }
        catch (Exception ex)
        {
            LogUiError("Header could not be loaded", ex);
            StatusText.Text = $"Header could not be loaded: {ex.Message}";
        }

        if (IsMapViewOpen) ApplyMapPane();
    }

    private void SelectEditor(string title)
    {
        for (int i = 0; i < EditorList.Items.Count; i++)
        {
            if (EditorList.Items[i] is ListBoxItem item && item.Content is string content && content == title)
            {
                EditorList.SelectedIndex = i;
                return;
            }
        }
    }

    private async void GoToWildEncounter_Click(object? sender, RoutedEventArgs e)
    {
        if (NoWildEncountersCheck.IsChecked == true || WildPokemonInput.SelectedItem is not IdChoice { Id: var encounterId })
        {
            StatusText.Text = "The wild Pokemon encounter file ID is not valid.";
            return;
        }

        SelectEditor("Wild Pokemon Encounter Editor");
        if (wildEncounterIds.Count == 0) ConfigureWildEncounterEditor();
        int selectorIndex = wildEncounterIds.IndexOf(encounterId);
        if (selectorIndex >= 0)
        {
            await OpenWildEncounterAsync(selectorIndex);
        }
        else
        {
            WildEncounterStatus.Text = $"Encounter file {encounterId:D4} was not unpacked.";
        }
    }

    private void LoadHeadersUsingScript(ushort scriptId)
    {
        HeadersUsingScriptList.Items.Clear();
        if (headerStore is null || currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            return;
        }

        IReadOnlyList<HeaderOption> locations = currentProject.GetLocationNames(metadata);
        for (ushort id = 0; id < currentProject.GetHeaderCount(); id++)
        {
            MapHeaderRecord header = headerStore.Load(id);
            if (header.ScriptFileId != scriptId)
            {
                continue;
            }

            string internalName = currentProject.GetInternalName(id);
            string locationName = locations.FirstOrDefault(option => option.Id == header.LocationNameId)?.Label ?? "Unknown location";
            HeadersUsingScriptList.Items.Add(new HeaderLink(id, internalName, locationName, header.TextArchiveId));
        }

        if (HeadersUsingScriptList.Items.Count > 0)
        {
            HeadersUsingScriptList.SelectedIndex = 0;
        }
    }

    private void GoToHeader_Click(object? sender, RoutedEventArgs e)
    {
        if (HeadersUsingScriptList.SelectedItem is not HeaderLink header)
        {
            StatusText.Text = "No header is selected.";
            return;
        }

        SelectEditor("Map Editor");
        ClearHeaderFilters();
        for (int i = 0; i < HeaderSelector.Items.Count; i++)
        {
            if (HeaderSelector.Items[i] is HeaderListEntry entry && entry.Id == header.Id)
            {
                HeaderSelector.SelectedIndex = i;
                break;
            }
        }
    }

    private MapHeaderRecord ReadHeaderFromFields()
    {
        MapHeaderRecord current = selectedHeader!;
        return current with
        {
            AreaIcon = ParseOptionByte(AreaIconInput, current.AreaIcon),
            AreaDataId = (byte)ComboId(AreaDataInput, "area data"),
            MatrixId = (ushort)ComboId(MatrixInput, "matrix"),
            ScriptFileId = (ushort)ComboId(ScriptInput, "script file"),
            LevelScriptId = (ushort)ComboId(LevelScriptInput, "level script"),
            TextArchiveId = (ushort)ComboId(TextArchiveInput, "text archive"),
            MusicDayId = ParseOption(MusicDayInput, current.MusicDayId),
            MusicNightId = ParseOption(MusicNightInput, current.MusicNightId),
            WildPokemonId = NoWildEncountersCheck.IsChecked == true ? NoWildEncounters : (ushort)ComboId(WildPokemonInput, "wild encounter file"),
            EventFileId = (ushort)ComboId(EventInput, "event file"),
            LocationNameId = ParseOption(LocationInput, current.LocationNameId),
            LocationSpecifier = ParseOptionByte(LocationSpecifierInput, current.LocationSpecifier),
            WeatherId = ParseOptionByte(WeatherInput, current.WeatherId),
            CameraAngleId = ParseOptionByte(CameraInput, current.CameraAngleId),
            BattleBackground = ParseByte(BattleBackgroundInput),
            Flags = ParseByte(FlagsInput)
            , FollowMode = ParseByte(FollowModeInput)
            , LocationType = ParseByte(LocationTypeInput)
            , WorldMapX = ParseByte(WorldMapXInput)
            , WorldMapY = ParseByte(WorldMapYInput)
            , Unknown0 = ParseByte(Unknown0Input)
            , Unknown1 = ParseByte(Unknown1Input)
            , KantoFlag = KantoFlagInput.IsChecked == true
        };
    }

    private bool HeaderHasUnsavedChanges()
    {
        if (headerStore is null || selectedHeader is null) return false;
        try
        {
            MapHeaderRecord updated = ReadHeaderFromFields();
            string name = HeaderNameInput.Text ?? string.Empty;
            return updated != selectedHeader || name != (currentProject?.GetInternalName(updated.Id) ?? name);
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private bool SaveCurrentHeader() => SaveCurrentHeader(out _);

    // Headers the Map Editor moved away from with unsaved edits (the header follows whatever the map views
    // show, so it changes too often to ask each time), by id: the record and internal name as edited.
    private readonly Dictionary<int, (MapHeaderRecord Record, string Name)> pendingHeaders = [];

    private void KeepUnsavedHeader()
    {
        if (selectedHeader is null || !HeaderHasUnsavedChanges()) return;
        pendingHeaders[selectedHeader.Id] = (ReadHeaderFromFields(), HeaderNameInput.Text ?? string.Empty);
    }

    /// <summary>Saves the headers left with unsaved edits. Returns their ids.</summary>
    private List<int> SavePendingHeaders()
    {
        var saved = new List<int>();
        if (headerStore is null || currentProject is null) return saved;
        foreach ((int id, (MapHeaderRecord record, string name)) in pendingHeaders.OrderBy(pair => pair.Key).ToList())
        {
            headerStore.Save(record);
            currentProject.SaveInternalName((ushort)id, name);
            pendingHeaders.Remove(id);
            saved.Add(id);
        }

        if (saved.Count > 0) RefreshHeaderNames();
        return saved;
    }

    private bool SaveCurrentHeader(out bool changed)
    {
        changed = false;
        if (headerStore is null || selectedHeader is null)
        {
            StatusText.Text = "No header is loaded.";
            return false;
        }

        try
        {
            MapHeaderRecord updated = ReadHeaderFromFields();

            string name = HeaderNameInput.Text ?? string.Empty;
            string previousName = currentProject?.GetInternalName(updated.Id) ?? name;
            changed = updated != selectedHeader || name != previousName;
            if (!changed) return true;

            ushort previousAreaData = selectedHeader.AreaDataId, previousLocation = selectedHeader.LocationNameId;
            headerStore.Save(updated);
            headerIdCombosStale = true;
            currentProject?.SaveInternalName(updated.Id, name);
            selectedHeader = updated;
            StatusText.Text = $"Header {updated.Id:D4} saved.";
            if (updated.AreaDataId != previousAreaData)
            {
                RefreshMapHeaderLookups();
                RedrawMapViewsUsingAreaData(updated.AreaDataId);
            }

            if (name != previousName || updated.LocationNameId != previousLocation) RefreshHeaderNames();
            return true;
        }
        catch (FormatException ex)
        {
            LogUiError("Header validation failed", ex);
            StatusText.Text = ex.Message;
            return false;
        }
        catch (Exception ex)
        {
            LogUiError("Header could not be saved", ex);
            StatusText.Text = $"Header could not be saved: {ex.Message}";
            return false;
        }
    }

    private async void SaveRom_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || headerStore is null)
        {
            StatusText.Text = "Open a ROM project before saving a ROM.";
            return;
        }

        await BuildRomAsync(sourceRomPath ?? InferSourceRomPath(currentProject.RootPath), overwritesSource: true);
    }

    /// <summary>Legacy's Save ROM: builds the ROM to a file the user picks, starting in the ROM export
    /// folder from Settings. The source ROM, and with it the edit journal's base, stays the same.</summary>
    private async void SaveRomAs_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || headerStore is null)
        {
            StatusText.Text = "Open a ROM project before saving a ROM.";
            return;
        }

        string suggested = Path.GetFileName(sourceRomPath ?? InferSourceRomPath(currentProject.RootPath));
        IStorageFile? file = await StorageProvider.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = "Save ROM As",
            SuggestedFileName = suggested,
            SuggestedStartLocation = await StartFolderAsync(appSettings.RomExportFolder),
            DefaultExtension = "nds",
            FileTypeChoices = [new FilePickerFileType("Nintendo DS ROM") { Patterns = ["*.nds"] }]
        });
        if (file is null) return;
        await BuildRomAsync(file.Path.LocalPath, overwritesSource: false);
    }

    private async Task BuildRomAsync(string outputRomPath, bool overwritesSource)
    {
        if (currentProject is null) return;
        // The ROM is built from the project's files, so edits not saved into them yet are left out.
        List<UnsavedChange> changes = CollectUnsavedChanges();
        if (changes.Count > 0)
        {
            string? choice = await new ChoiceDialog(
                "Unsaved changes",
                $"{DescribeUnsavedAreas(changes)} {(changes.Count == 1 ? "has" : "have")} unsaved changes, which the ROM leaves out unless they are saved first.",
                ("save", "Save All and Build"), ("skip", "Build Without Them"), ("cancel", "Cancel")).ShowDialog<string?>(this);
            if (choice == "save")
            {
                List<UnsavedChange> remaining = SaveChanges(changes);
                if (remaining.Count > 0)
                {
                    StatusText.Text = $"The ROM was not built: {DescribeUnsavedAreas(remaining)} could not be saved.";
                    return;
                }
            }
            else if (choice != "skip")
            {
                return;
            }
        }

        string toolsDirectory = Path.Combine(AppContext.BaseDirectory, "Tools");

        StatusText.Text = overwritesSource ? $"Building ROM for overwrite: {outputRomPath}" : $"Building ROM: {outputRomPath}";
        if (!currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            StatusText.Text = "Game metadata could not be resolved; ROM was not built.";
            return;
        }

        try
        {
            ToolResult result = await RomProjectPipeline.BuildRomAsync(
                currentProject,
                metadata,
                outputRomPath,
                toolsDirectory,
                status => StatusText.Text = status);
            if (!result.Succeeded)
            {
                Console.Error.WriteLine($"[DSPRE] ROM build failed with exit code {result.ExitCode}.");
                Console.Error.WriteLine(result.StandardOutput);
                Console.Error.WriteLine(result.StandardError);
                StatusText.Text = string.IsNullOrWhiteSpace(result.StandardError)
                    ? $"ROM build failed with exit code {result.ExitCode}; ROM was not written."
                    : $"ROM build failed: {result.StandardError.Trim()}";
                return;
            }

            StatusText.Text = overwritesSource ? $"ROM overwritten: {outputRomPath}" : $"ROM saved: {outputRomPath}";
        }
        catch (Exception ex)
        {
            LogUiError("ROM build could not start", ex);
            StatusText.Text = $"ROM build could not start: {ex.Message}";
        }
    }

    /// <summary>Applies another project's edit journal to the open project (e.g. to reproduce a set of
    /// edits on a fresh extraction). The replayed edits are journaled into this project's own journal.</summary>
    private async void ReplayJournal_Click(object? sender, RoutedEventArgs e)
    {
        if (currentProject is null || !currentProject.TryGetGameMetadata(out RomGameMetadata? metadata) || metadata is null)
        {
            StatusText.Text = "Open a ROM project before replaying a journal.";
            return;
        }

        var files = await StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            AllowMultiple = false,
            Title = "Replay edit journal",
            FileTypeFilter = [new FilePickerFileType("DSPRE edit journal") { Patterns = ["*.dspre"] }]
        });
        if (files.Count != 1) return;

        string journalPath = Path.GetFullPath(files[0].Path.LocalPath);
        if (EditJournal.Active is not null && journalPath == Path.GetFullPath(EditJournal.Active.FilePath))
        {
            StatusText.Text = "That is this project's own journal; its edits are already applied.";
            return;
        }

        // Every editor is opened afresh on the replayed project.
        if (!await ConfirmLeavingProjectAsync("replaying the journal")) return;
        try
        {
            RomProjectPipeline.PrepareEditorData(currentProject, metadata);
            int applied = EditJournalReplayer.Replay(currentProject, metadata, journalPath);
            sharedMapBuildings.Clear();
            sharedMapSavedBuildings.Clear();
            ShowProjectStatus(currentProject);
            StatusText.Text = $"Replayed {applied} journal call(s) from {Path.GetFileName(journalPath)}.";
        }
        catch (Exception ex)
        {
            LogUiError("Journal replay failed", ex);
            StatusText.Text = $"Journal replay failed: {ex.Message}";
        }
    }

    private static string InferSourceRomPath(string projectPath)
    {
        string projectName = Path.GetFileName(projectPath);
        const string suffix = "_DSPRE_contents";
        if (projectName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            projectName = projectName[..^suffix.Length];
        }

        return Path.Combine(
            Directory.GetParent(projectPath)?.FullName ?? Environment.CurrentDirectory,
            $"{projectName}.nds");
    }

    #region Status messages

    private readonly EditTracker matrixEdits = new();
    private readonly EditTracker wildEncounterEdits = new();
    private readonly EditTracker textArchiveEdits = new();
    private readonly EditTracker trainerEdits = new();
    private readonly EditTracker personalDataEdits = new();
    private readonly EditTracker learnsetEdits = new();
    private readonly EditTracker evolutionEdits = new();
    private readonly EditTracker moveEdits = new();
    private readonly EditTracker itemTableEntryEdits = new();
    private readonly EditTracker itemDataEdits = new();
    private readonly EditTracker tradeEdits = new();
    private readonly EditTracker tmEdits = new();
    private readonly EditTracker eggMoveEdits = new();
    private readonly EditTracker hiddenItemEdits = new();
    private readonly EditTracker spawnEdits = new();
    private readonly EditTracker flyWarpEdits = new();
    private readonly EditTracker pickupEdits = new();
    private readonly EditTracker tableComboEdits = new();
    private readonly EditTracker tableVsTrainerEdits = new();
    private readonly EditTracker tableVsPokemonEdits = new();
    private readonly EditTracker tableConditionalMusicEdits = new();
    private readonly EditTracker honeyTreeEdits = new();
    private readonly EditTracker greatMarshEdits = new();
    private readonly EditTracker bugContestEdits = new();
    private readonly EditTracker headbuttEdits = new();
    private readonly EditTracker safariZoneEdits = new();
    private readonly EditTracker cameraEdits = new();
    private readonly EditTracker levelScriptEdits = new();
    private readonly EditTracker scriptEdits = new();

    /// <summary>Reports the latest change to an editor's document on its status line. Controls fire
    /// their change events while a list is still being drawn, so an event with no actual change
    /// behind it reports nothing. The comparison waits until the current input has been handled,
    /// because a control raises its change event before its two-way binding has written the new
    /// value into the document.</summary>
    private static void ReportEdit(EditTracker tracker, Func<object?> document, TextBlock status, string? suffix = null) =>
        Dispatcher.UIThread.Post(() =>
        {
            if (tracker.DescribeLatestChange(document()) is string change) status.Text = suffix is null ? change : $"{change} {suffix}";
        }, DispatcherPriority.Background);

    // Status lines that change on nearly every click (the text preview's hint, the Header pane's
    // linked-file summaries, the Map View pane headings, the overview's load checklist) would bury
    // everything else, so they are left out of the log and the bottom bar.
    private static readonly HashSet<string> QuietStatusBlocks =
    [
        "TextPreviewStatus", "MapPaneWildStatus", "MapPaneTrainerStatus", "MapPaneScriptStatus", "MapPaneLevelScriptStatus", "MapPaneTextStatus"
    ];

    private static readonly IBrush StatusInfoBrush = new SolidColorBrush(Color.Parse("#65D8C7"));
    private static readonly IBrush StatusWarningBrush = new SolidColorBrush(Color.Parse("#E8B75F"));
    private static readonly IBrush StatusErrorBrush = new SolidColorBrush(Color.Parse("#E26D6D"));

    private readonly Dictionary<string, string> lastStatusByBlock = [];
    private bool mirroringStatus;
    private StatusLogWindow? statusLogWindow;

    /// <summary>Every editor reports into its own status line. Rather than route each of those
    /// assignments through a helper, the lines are found by name (anything ending in "Status" or
    /// "StatusText") and watched, so a message set anywhere is logged and repeated in the bottom bar.</summary>
    private void WatchStatusTextBlocks()
    {
        foreach (System.Reflection.FieldInfo field in typeof(MainWindow).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Public))
        {
            if (field.FieldType != typeof(TextBlock)) continue;
            if (!field.Name.EndsWith("Status", StringComparison.Ordinal) && !field.Name.EndsWith("StatusText", StringComparison.Ordinal)) continue;
            if (field.Name.StartsWith("Overview", StringComparison.Ordinal) || QuietStatusBlocks.Contains(field.Name)) continue;
            if (field.GetValue(this) is not TextBlock block) continue;

            string blockName = field.Name;
            block.PropertyChanged += (_, e) =>
            {
                if (e.Property == TextBlock.TextProperty) OnStatusTextChanged(blockName, block.Text);
            };
        }
    }

    private void OnStatusTextChanged(string blockName, string? text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        bool bottomBar = blockName == nameof(StatusText);
        if (bottomBar && mirroringStatus) return;
        if (lastStatusByBlock.TryGetValue(blockName, out string? previous) && previous == text) return;
        lastStatusByBlock[blockName] = text;

        StatusLevel level = ClassifyStatus(text);
        string source = bottomBar ? "DSPRE" : PageTitle.Text ?? "DSPRE";
        StatusLog.Write(level, source, text);

        if (!bottomBar)
        {
            mirroringStatus = true;
            try
            {
                StatusText.Text = $"{source}: {text}";
            }
            finally
            {
                mirroringStatus = false;
            }
        }

        StatusText.Foreground = level switch
        {
            StatusLevel.Error => StatusErrorBrush,
            StatusLevel.Warning => StatusWarningBrush,
            _ => StatusInfoBrush
        };
    }

    /// <summary>Status lines carry no level of their own, so problems are recognised by wording.</summary>
    private static StatusLevel ClassifyStatus(string text)
    {
        string lower = text.ToLowerInvariant();
        if (lower.Contains("exception")) return StatusLevel.Error;
        string[] problems =
        [
            "failed", "could not", "couldn't", "cannot", "can't", "error", "not found", "invalid", "does not exist",
            "no longer exists", "missing", "too many", "not the same size", "not supported", "not a ", "refused", "must "
        ];
        return problems.Any(lower.Contains) ? StatusLevel.Warning : StatusLevel.Info;
    }

    private void OpenStatusLog_Click(object? sender, RoutedEventArgs e)
    {
        if (statusLogWindow is not null)
        {
            statusLogWindow.Activate();
            return;
        }

        statusLogWindow = new StatusLogWindow();
        statusLogWindow.Closed += (_, _) => statusLogWindow = null;
        statusLogWindow.Show(this);
    }

    #endregion

    private static void LogUiError(string context, Exception exception)
    {
        StatusLogConsole.Original.WriteLine($"[DSPRE] {context}: {exception}");
        StatusLog.Error(context, exception.ToString());
    }

    private static void SetOptions(ComboBox comboBox, IReadOnlyList<HeaderOption> options)
    {
        comboBox.Items.Clear();
        foreach (HeaderOption option in options)
        {
            comboBox.Items.Add(option);
        }
    }

    private static void SelectOption(ComboBox comboBox, ushort id)
    {
        for (int i = 0; i < comboBox.Items.Count; i++)
        {
            if (comboBox.Items[i] is HeaderOption option && option.Id == id)
            {
                comboBox.SelectedIndex = i;
                return;
            }
        }

        comboBox.SelectedItem = new HeaderOption(id, "Unknown ID");
    }

    private static ushort ParseOption(ComboBox comboBox, ushort fallback)
    {
        if (comboBox.SelectedItem is HeaderOption option)
        {
            return option.Id;
        }

        return fallback;
    }

    private static byte ParseOptionByte(ComboBox comboBox, byte fallback)
    {
        ushort value = ParseOption(comboBox, fallback);
        if (value <= byte.MaxValue)
        {
            return (byte)value;
        }

        throw new FormatException($"The selected value {value} is outside the byte range.");
    }

    private static IReadOnlyList<HeaderOption> GetAreaIcons(MapHeaderFormat format)
    {
        return format switch
        {
            MapHeaderFormat.HeartGoldSoulSilver => [new(0, "Not displayed"), new(1, "Wall"), new(2, "Wood"), new(3, "Town"), new(4, "Cave"), new(5, "Forest"), new(6, "Water"), new(7, "Field"), new(8, "Lake"), new(9, "Gray")],
            MapHeaderFormat.Platinum => [new(0, "City"), new(1, "City"), new(2, "Town 1"), new(3, "Town 2"), new(4, "Cave"), new(5, "Forest"), new(6, "Water"), new(7, "Field"), new(8, "Island"), new(9, "Wood")],
            _ => [new(0, "Not applicable")]
        };
    }

    private static ushort ParseUShort(TextBox input)
    {
        if (ushort.TryParse(input.Text, out ushort value))
        {
            return value;
        }

        throw new FormatException($"'{input.Text}' is not a valid unsigned 16-bit value.");
    }

    private static byte ParseByte(TextBox input)
    {
        if (byte.TryParse(input.Text, out byte value))
        {
            return value;
        }

        throw new FormatException($"'{input.Text}' is not a valid unsigned 8-bit value.");
    }
}
