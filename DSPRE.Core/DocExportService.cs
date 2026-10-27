using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DSPRE.Core;

/// <summary>Legacy "Export Docs" (DS_Map/DocTool.cs ExportDocs): human-readable CSV/text/JSON dumps of
/// a project's Pokémon, learnsets, evolutions, trainers, moves, TM/HM compatibility, egg moves, event
/// overworlds, map headers and wild encounters, in the same file names and formats, so sheets and
/// scripts built on legacy's output keep working. The script plaintext dump comes from the legacy
/// bridge (export-script-docs), since script parsing lives there.</summary>
public static class DocExportService
{
    private const int MapSize = 32;

    /// <summary>Writes every document except the scripts into <paramref name="outputDirectory"/>.
    /// Returns the files written.</summary>
    public static List<string> Export(RomProject project, RomGameMetadata metadata, string outputDirectory)
    {
        Directory.CreateDirectory(outputDirectory);
        var names = new Names(project, metadata);
        var written = new List<string>();
        void Write(string fileName, string content)
        {
            string path = Path.Combine(outputDirectory, fileName);
            File.WriteAllText(path, content, new UTF8Encoding(false));
            written.Add(path);
        }

        Write("PokemonPersonalData.csv", PersonalData(project, names));
        Write("LearnsetData.csv", Learnsets(project, names));
        Write("EvolutionData.csv", Evolutions(project, names));
        Write("TrainerData.txt", Trainers(project, metadata, names));
        Write("MoveData.csv", Moves(project, names));
        Write("TMHMData.csv", Machines(project, metadata, names));
        Write("EggMoveData.csv", EggMoves(project, metadata, names));
        Write("EventOverworlds.csv", EventOverworlds(project, metadata));
        Write("MapHeaders.csv", MapHeaders(project, metadata));
        Write("Encounters.json", Encounters(project, metadata, names));
        return written;
    }

    /// <summary>The scripts as plaintext, one NNNN.txt per script file with scripts, into
    /// &lt;outputDirectory&gt;/scripts through the legacy bridge. Returns how many were written and how
    /// many could not be read.</summary>
    public static async Task<(int Exported, int Failed)> ExportScriptsAsync(RomProject project, RomGameMetadata metadata, string toolsDirectory, string outputDirectory)
    {
        string scripts = Path.Combine(outputDirectory, "scripts");
        ToolResult result = await new RomExtractionService().RunToolboxAsync(project, metadata, toolsDirectory, ["export-script-docs", scripts]);
        if (!result.Succeeded) throw new InvalidOperationException($"The scripts could not be exported: {result.StandardError.Trim()}");
        string? summary = result.StandardOutput.Split('\n').LastOrDefault(line => line.StartsWith("EXPORTED|", StringComparison.Ordinal));
        string[] parts = summary?.Trim().Split('|') ?? [];
        return parts.Length == 3 && int.TryParse(parts[1], out int exported) && int.TryParse(parts[2], out int failed) ? (exported, failed) : (0, 0);
    }

    /// <summary>The text archive names every document uses. Species names gain the alternate-form
    /// entries for the extra personal files, as legacy's DocTool builds them.</summary>
    private sealed class Names
    {
        public Names(RomProject project, RomGameMetadata metadata)
        {
            string[] Archive(int id) => TextArchiveService.Load(project, id, metadata).Messages.ToArray();
            string[] basePokemon = Archive(TrainerService.GetPokemonNamesArchiveId(metadata));
            int extra = PersonalDataService.GetFileCount(project) - basePokemon.Length;
            Pokemon = basePokemon.Concat(Enumerable.Range(0, Math.Max(0, extra)).Select(i => i < PersonalDataService.AltForms.Count
                    ? $"{(PersonalDataService.AltForms[i].MonId < basePokemon.Length ? basePokemon[PersonalDataService.AltForms[i].MonId] : $"UNKNOWN_{PersonalDataService.AltForms[i].MonId}")} - {PersonalDataService.AltForms[i].Description}"
                    : $"Extra entry {basePokemon.Length + i}"))
                .ToArray();
            Items = Archive(TrainerService.GetItemNamesArchiveId(metadata));
            Abilities = Archive(TrainerService.GetAbilityNamesArchiveId(metadata));
            Moves = Archive(TrainerService.GetAttackNamesArchiveId(metadata));
            Trainers = Archive(TrainerService.GetTrainerNamesArchiveId(metadata)).Select(TrainerService.UnwrapTrainerName).ToArray();
            TrainerClasses = Archive(TrainerService.GetTrainerClassNamesArchiveId(metadata));
            Types = Archive(PersonalDataService.GetTypeNamesArchiveId(metadata));
        }

        public string[] Pokemon { get; }
        public string[] Items { get; }
        public string[] Abilities { get; }
        public string[] Moves { get; }
        public string[] Trainers { get; }
        public string[] TrainerClasses { get; }
        public string[] Types { get; }
    }

    private static string At(string[] names, int id, string fallbackPrefix) => id >= 0 && id < names.Length ? names[id] : $"{fallbackPrefix}{id}";

    private static string Type(Names names, PokemonType type) => At(names.Types, (int)type, "UnknownType_");

    private static string PersonalData(RomProject project, Names names)
    {
        var text = new StringBuilder();
        text.AppendLine("ID,Name,Type1,Type2,BaseHP,BaseAttack,BaseDefense,BaseSpecialAttack,BaseSpecialDefense,BaseSpeed,Ability1,Ability2,Item1,Item2");
        for (int i = 0; i < PersonalDataService.GetFileCount(project); i++)
        {
            PersonalDataRecord p = PersonalDataService.Load(project, i);
            // The space before the special attack column is legacy's.
            text.AppendLine($"{i},{At(names.Pokemon, i, "")},{Type(names, p.Type1)},{Type(names, p.Type2)}," +
                $"{p.BaseHP},{p.BaseAtk},{p.BaseDef}, {p.BaseSpAtk},{p.BaseSpDef},{p.BaseSpeed}," +
                $"{At(names.Abilities, p.FirstAbility, "")},{At(names.Abilities, p.SecondAbility, "")}," +
                $"{At(names.Items, p.Item1, "")},{At(names.Items, p.Item2, "")}");
        }

        return text.ToString();
    }

    private static int FileCount(string directory) => Directory.Exists(directory) ? Directory.GetFiles(directory).Length : 0;

    private static string Learnsets(RomProject project, Names names)
    {
        var text = new StringBuilder("ID,Name");
        for (int i = 0; i < 20; i++) text.Append($",LevelMove{i}");
        text.AppendLine();
        for (int i = 0; i < FileCount(project.LearnsetsDirectory); i++)
        {
            List<LearnsetEntry> entries = LearnsetService.Load(project, i).Entries;
            text.Append($"{i},{At(names.Pokemon, i, "")}");
            foreach (LearnsetEntry entry in entries.Take(20)) text.Append($",{entry.Level}|{At(names.Moves, entry.Move, "")}");
            text.Append(',', Math.Max(0, 20 - entries.Count));
            text.AppendLine();
        }

        return text.ToString();
    }

    private static string Evolutions(RomProject project, Names names)
    {
        var text = new StringBuilder();
        text.AppendLine("ID,Name,[Method|Param|Target]");
        for (int i = 0; i < FileCount(project.EvolutionsDirectory); i++)
        {
            text.Append($"{i},{At(names.Pokemon, i, "")}");
            foreach (EvolutionSlot slot in EvolutionService.Load(project, i).Slots)
            {
                if (slot.Target == 0) break;
                string param = EvolutionService.ParamMeanings.GetValueOrDefault(slot.Method) switch
                {
                    EvolutionParamMeaning.Ignored => "Ignored",
                    EvolutionParamMeaning.ItemName => At(names.Items, slot.Param, ""),
                    EvolutionParamMeaning.MoveName => At(names.Moves, slot.Param, ""),
                    EvolutionParamMeaning.PokemonName => At(names.Pokemon, slot.Param, ""),
                    _ => slot.Param.ToString()
                };
                text.Append($",[{slot.Method}|{param}|{At(names.Pokemon, slot.Target, "")}]");
            }

            text.AppendLine();
        }

        return text.ToString();
    }

    /// <summary>Each trainer's party in Showdown's format, with the nature and ability the game gives
    /// each Pokémon (see TrainerPersonality).</summary>
    private static string Trainers(RomProject project, RomGameMetadata metadata, Names names)
    {
        var text = new StringBuilder();
        bool[] classGenders = TrainerPersonality.ReadTrainerClassGenders(project, metadata);
        var personal = new Dictionary<int, PersonalDataRecord>();
        PersonalDataRecord Personal(int species) =>
            personal.TryGetValue(species, out PersonalDataRecord? record) ? record : personal[species] = PersonalDataService.Load(project, species);

        int trainerCount = FileCount(project.TrainerPropertiesDirectory);
        for (int i = 1; i < trainerCount; i++)
        {
            string name = At(names.Trainers, i, "");
            TrainerRecord trainer = TrainerService.Load(project, i, metadata, name);
            var personality = new TrainerPersonality(project, metadata, trainer.TrainerClass >= classGenders.Length || classGenders[trainer.TrainerClass]);

            text.Append($"[{i}] {At(names.TrainerClasses, trainer.TrainerClass, "")} {name}");
            string[] items = trainer.TrainerItems.Select(item => item != 0 ? At(names.Items, item, "") : "None").ToArray();
            if (items[0] != "None") text.Append($" @ ({string.Join(", ", items.Where(item => item != "None"))})");
            text.Append(":\n\n");

            foreach (PartyPokemonRecord mon in trainer.Party.Take(trainer.PartyCount))
            {
                int species = mon.Species ?? 0;
                byte flags = (byte)mon.GenderAndAbility;
                int genderOverride = flags & 0x0F;
                PersonalDataRecord monData = Personal(species);
                uint pid = personality.NextPid(i, trainer.TrainerClass, species, (byte)mon.Level, monData.GenderRatio, genderOverride, flags >> 4, mon.Difficulty);

                text.Append(At(names.Pokemon, species, ""));
                // As legacy: other override values have no gender letter but still get the brackets.
                if (genderOverride != 0) text.Append($" ({genderOverride switch { 1 => "M", 2 => "F", _ => "" }})");
                string heldItem = mon.HeldItem is int item ? At(names.Items, item, "") : "None";
                if (heldItem != "None") text.Append($" @ {heldItem}");
                text.Append($"\nAbility: {At(names.Abilities, pid % 2 == 0 ? monData.FirstAbility : monData.SecondAbility, "")}");
                text.Append($"\nLevel: {mon.Level}");
                text.Append($"\n{TrainerPersonality.Natures[TrainerPersonality.NatureOf(pid)].Split(':')[0]} Nature");
                text.Append($"\nIVs: {string.Join(" / ", Enumerable.Repeat((mon.Difficulty * 31 / 255).ToString(), 6))}");
                IEnumerable<int> moves = mon.Moves is int[] chosen ? chosen : MovesAtLevel(project, species, mon.Level);
                string[] moveNames = moves.Select(move => At(names.Moves, move, "")).Where(move => move != "None" && move != "-").ToArray();
                text.Append("\n- " + string.Join("\n- ", moveNames));
                text.Append("\n\n");
            }

            text.Append("\n\n\n");
        }

        return text.ToString();
    }

    /// <summary>The last four distinct moves learnt by the level (legacy LearnsetData.GetLearnsetAtLevel):
    /// what a trainer's Pokémon knows when the trainer does not choose its moves.</summary>
    private static List<int> MovesAtLevel(RomProject project, int species, int level)
    {
        var moves = new List<int>();
        foreach (LearnsetEntry entry in LearnsetService.Load(project, species).Entries)
        {
            if (entry.Level > level || entry.Move == 0 || moves.Contains(entry.Move)) continue;
            if (moves.Count >= 4) moves.RemoveAt(0);
            moves.Add(entry.Move);
        }

        return moves;
    }

    private static readonly string[] MoveFlagNames = ["CONTACT", "PROTECT", "MAGIC_COAT", "SNATCH", "MIRROR_MOVE", "KINGSROCK", "KEEP_HP_BAR", "DEL_SHADOW"];

    private static string Moves(RomProject project, Names names)
    {
        var text = new StringBuilder();
        text.AppendLine("Move ID,Move Name,Move Type,Move Split,Power,Accuracy,Priority,Side Effect Probability,PP,Range,Flags,Effect Description");
        int fileCount = MoveDataService.GetFileCount(project);
        for (int i = 0; i < names.Moves.Length && i < fileCount; i++)
        {
            MoveRecord move = MoveDataService.Load(project, i);
            string flags = string.Join("|", MoveFlagNames.Where((_, bit) => (move.FlagField & (1 << bit)) != 0));
            string effect = move.BattleEffect < MoveEffectDescriptions.Descriptions.Length ? MoveEffectDescriptions.Descriptions[move.BattleEffect] : $"UnknownEffect_{move.BattleEffect}";
            string split = move.Split switch { MoveSplit.Physical => "PHYSICAL", MoveSplit.Special => "SPECIAL", MoveSplit.Status => "STATUS", _ => ((byte)move.Split).ToString() };
            text.AppendLine($"{i},{names.Moves[i]},{Type(names, move.MoveType)},{split}," +
                $"{move.Damage},{move.Accuracy},{move.Priority},{move.SideEffectProbability},{move.Pp}," +
                $"{MoveDataService.GetAttackRangeName(move.Target)},[{flags}],{effect}");
        }

        return text.ToString();
    }

    private static string Machines(RomProject project, RomGameMetadata metadata, Names names)
    {
        var text = new StringBuilder("ID,Name");
        int[] machineMoves = PersonalDataService.ReadMachineMoves(project, metadata);
        for (int i = 0; i < PersonalDataService.MachineCount; i++)
        {
            string move = machineMoves[i] < names.Moves.Length ? names.Moves[machineMoves[i]] : $"UNK_{machineMoves[i]}";
            text.Append($",{PersonalDataService.MachineLabelFromIndex(i)} - {move}");
        }

        text.AppendLine();
        for (int i = 0; i < PersonalDataService.GetFileCount(project); i++)
        {
            SortedSet<byte> machines = PersonalDataService.Load(project, i).Machines;
            text.Append($"{i},{At(names.Pokemon, i, "")},[");
            text.Append(string.Join(",", Enumerable.Range(0, PersonalDataService.MachineCount).Select(b => machines.Contains((byte)b) ? "true" : "false")));
            text.AppendLine("]");
        }

        return text.ToString();
    }

    private static string EggMoves(RomProject project, RomGameMetadata metadata, Names names)
    {
        var text = new StringBuilder();
        text.AppendLine("SpeciesID,SpeciesName,MoveID,MoveName");
        foreach (EggMoveEntry entry in EggMoveService.Load(project, metadata))
        {
            string species = At(names.Pokemon, entry.SpeciesId, "SPECIES_");
            foreach (ushort move in entry.MoveIds) text.AppendLine($"{entry.SpeciesId},{species},{move},{At(names.Moves, move, "MOVE_")}");
        }

        return text.ToString();
    }

    /// <summary>Overlay table entries legacy draws as 3D objects (RomInfo.Set3DOverworldsDict): they have
    /// no overworld sprite, so the sprite column stays empty.</summary>
    private static bool Is3DOverworld(uint entry) => entry is >= 91 and <= 96 or >= 101 and <= 116;

    private static string EventOverworlds(RomProject project, RomGameMetadata metadata)
    {
        var text = new StringBuilder();
        text.AppendLine("EventFileID,OverworldIndex,OwID,OverlayTableEntry,OwSpriteID,Movement,Type,Flag,ScriptNumber,Orientation,SightRange,Unknown1,Unknown2,XRange,YRange," +
            "XMatrix,YMatrix,XMap,YMap,XCoord,YCoord,ZPosition,IsAlias");
        var sprites = new Dictionary<uint, uint>();
        foreach (OverworldSpriteEntry entry in OverworldSpriteService.LoadTable(project, metadata)) sprites.TryAdd(entry.EntryId, entry.SpriteId);

        if (!Directory.Exists(project.EventsDirectory)) return text.ToString();
        foreach (string path in Directory.GetFiles(project.EventsDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
        {
            if (!int.TryParse(Path.GetFileName(path), out int fileId)) continue;
            MapEventFile events = MapEventService.Parse(File.ReadAllBytes(path), fileId);
            for (int i = 0; i < events.Overworlds.Count; i++)
            {
                OverworldEvent ow = events.Overworlds[i];
                string sprite = !Is3DOverworld(ow.OverlayTableEntry) && sprites.TryGetValue(ow.OverlayTableEntry, out uint spriteId) ? spriteId.ToString() : "";
                text.AppendLine($"{fileId},{i},{ow.OverworldId},{ow.OverlayTableEntry},{sprite}," +
                    $"{ow.Movement},{ow.Type},{ow.Flag},{ow.ScriptNumber},{ow.Orientation},{ow.SightRange},{ow.Unknown1},{ow.Unknown2},{ow.XRange},{ow.YRange}," +
                    $"{ow.MatrixPositionX},{ow.MatrixPositionY},{ow.MapPositionX},{ow.MapPositionY}," +
                    $"{ow.MapPositionX + MapSize * ow.MatrixPositionX},{ow.MapPositionY + MapSize * ow.MatrixPositionY},{ow.ZPosition},{(ow.IsAlias ? 1 : 0)}");
            }
        }

        return text.ToString();
    }

    private static string MapHeaders(RomProject project, RomGameMetadata metadata)
    {
        var text = new StringBuilder();
        text.AppendLine("HeaderID,ScriptFileID,EventFileID,MapNameIndexInTextArchive,WildPokemonFileID,WeatherID");
        if (MapHeaderStoreFactory.Create(project, metadata) is not IMapHeaderStore store) return text.ToString();
        for (int id = 0; id < store.Count; id++)
        {
            MapHeaderRecord header = store.Load((ushort)id);
            text.AppendLine($"{id},{header.ScriptFileId},{header.EventFileId},{header.LocationNameId},{header.WildPokemonId},{header.WeatherId}");
        }

        return text.ToString();
    }

    private static string Encounters(RomProject project, RomGameMetadata metadata, Names names)
    {
        bool hgss = metadata.HeaderFormat == MapHeaderFormat.HeartGoldSoulSilver;
        var encounters = new List<object>();
        string directory = project.EncountersDirectory;
        if (Directory.Exists(directory))
        {
            foreach (string path in Directory.GetFiles(directory).Where(path => int.TryParse(Path.GetFileName(path), out _)).OrderBy(path => int.Parse(Path.GetFileName(path))))
            {
                int fileId = int.Parse(Path.GetFileName(path));
                byte[] data = File.ReadAllBytes(path);
                encounters.Add(hgss ? EncounterHgss(fileId, data, names.Pokemon) : EncounterDpPt(fileId, data, names.Pokemon));
            }
        }

        var root = new
        {
            generatedAt = DateTime.UtcNow,
            projectName = ScriptDatabaseStore.ProjectKey(project),
            romID = metadata.GameCode,
            gameFamily = metadata.HeaderFormat switch { MapHeaderFormat.DiamondPearl => "DP", MapHeaderFormat.Platinum => "Plat", _ => "HGSS" },
            gameVersion = metadata.GameCode[..3] switch { "ADA" => "Diamond", "APA" => "Pearl", "CPU" => "Platinum", "IPK" => "HeartGold", _ => "SoulSilver" },
            gameLanguage = metadata.Language.ToString(),
            encounters
        };
        return JsonSerializer.Serialize(root, new JsonSerializerOptions { WriteIndented = true, DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull });
    }

    private static string SpeciesName(int species, string[] pokemon) => species >= 0 && species < pokemon.Length ? pokemon[species] : $"UNKNOWN_{species}";

    private static object Species(int slot, int species, string[] pokemon) => new { slot, species, speciesName = SpeciesName(species, pokemon) };

    private static List<object> SpeciesList(IEnumerable<int> species, string[] pokemon) => species.Select((id, slot) => Species(slot, id, pokemon)).ToList();

    private static List<object> LevelledSpecies(ushort[] species, byte[] min, byte[] max, string[] pokemon) =>
        species.Select((id, slot) => (object)new { slot, species = (int)id, speciesName = SpeciesName(id, pokemon), minLv = min[slot], maxLv = max[slot] }).ToList();

    /// <summary>Reads like legacy EncounterFileDPPt: fields past the end of a short file read as 0.</summary>
    private sealed class Reader(byte[] data)
    {
        public uint U32(int offset) => offset + 4 <= data.Length ? BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset)) : 0;
        public ushort U16(int offset) => offset + 2 <= data.Length ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset)) : (ushort)0;
        public byte U8(int offset) => offset < data.Length ? data[offset] : (byte)0;
    }

    private static object EncounterDpPt(int fileId, byte[] data, string[] pokemon)
    {
        var r = new Reader(data);
        uint[] U32s(int start, int count) => Enumerable.Range(0, count).Select(i => r.U32(start + 4 * i)).ToArray();
        List<object> Water(int start)
        {
            var species = new ushort[5];
            var min = new byte[5];
            var max = new byte[5];
            for (int i = 0; i < 5; i++)
            {
                max[i] = r.U8(start + 8 * i);
                min[i] = r.U8(start + 8 * i + 1);
                species[i] = (ushort)r.U32(start + 8 * i + 4);
            }

            return LevelledSpecies(species, min, max, pokemon);
        }

        return new
        {
            fileId,
            rates = new { walking = (byte)r.U32(0), surf = (byte)r.U32(204), oldRod = (byte)r.U32(292), goodRod = (byte)r.U32(336), superRod = (byte)r.U32(380) },
            walking = Enumerable.Range(0, 12).Select(i => (object)new
            {
                slot = i,
                level = (byte)r.U32(4 + 8 * i),
                species = unchecked((int)r.U32(8 + 8 * i)),
                speciesName = SpeciesName(unchecked((int)r.U32(8 + 8 * i)), pokemon)
            }).ToList(),
            timeSpecific = new
            {
                day = SpeciesList(U32s(108, 2).Select(v => unchecked((int)v)), pokemon),
                night = SpeciesList(U32s(116, 2).Select(v => unchecked((int)v)), pokemon)
            },
            radar = SpeciesList(U32s(124, 4).Select(v => unchecked((int)v)), pokemon),
            dualSlot = new
            {
                ruby = SpeciesList(U32s(164, 2).Select(v => unchecked((int)v)), pokemon),
                sapphire = SpeciesList(U32s(172, 2).Select(v => unchecked((int)v)), pokemon),
                emerald = SpeciesList(U32s(180, 2).Select(v => unchecked((int)v)), pokemon),
                fireRed = SpeciesList(U32s(188, 2).Select(v => unchecked((int)v)), pokemon),
                leafGreen = SpeciesList(U32s(196, 2).Select(v => unchecked((int)v)), pokemon)
            },
            swarms = SpeciesList(U32s(100, 2).Select(v => (int)(ushort)v), pokemon),
            forms = new { regionalForms = U32s(140, 5), unknownTable = r.U32(160) },
            surf = Water(208),
            oldRod = Water(296),
            goodRod = Water(340),
            superRod = Water(384)
        };
    }

    private static object EncounterHgss(int fileId, byte[] data, string[] pokemon)
    {
        var r = new Reader(data);
        List<object> U16s(int start, int count) => SpeciesList(Enumerable.Range(0, count).Select(i => (int)r.U16(start + 2 * i)), pokemon);
        List<object> Slots(int start, int count)
        {
            var species = new ushort[count];
            var min = new byte[count];
            var max = new byte[count];
            for (int i = 0; i < count; i++)
            {
                min[i] = r.U8(start + 4 * i);
                max[i] = r.U8(start + 4 * i + 1);
                species[i] = r.U16(start + 4 * i + 2);
            }

            return LevelledSpecies(species, min, max, pokemon);
        }

        return new
        {
            fileId,
            rates = new { walking = r.U8(0), surf = r.U8(1), rockSmash = r.U8(2), oldRod = r.U8(3), goodRod = r.U8(4), superRod = r.U8(5) },
            walkingLevels = Enumerable.Range(0, 12).Select(i => r.U8(8 + i)).ToArray(),
            grass = new { morning = U16s(20, 12), day = U16s(44, 12), night = U16s(68, 12) },
            surf = Slots(100, 5),
            rockSmash = Slots(120, 2),
            oldRod = Slots(128, 5),
            goodRod = Slots(148, 5),
            superRod = Slots(168, 5),
            swarms = U16s(188, 4),
            pokegearMusic = new { hoenn = U16s(92, 2), sinnoh = U16s(96, 2) }
        };
    }
}
