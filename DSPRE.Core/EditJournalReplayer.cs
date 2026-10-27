using System.Text.Json.Nodes;

namespace DSPRE.Core;

/// <summary>Re-applies an edit journal to a freshly extracted and prepared project. Document ops
/// (Set/Add/Remove + document name) are applied to the document as loaded from disk and saved through
/// the same Core service the editor uses; consecutive ops on the same document are batched into one
/// load/save, mirroring how they were recorded. Other calls dispatch to <see cref="Handlers"/>.</summary>
public static class EditJournalReplayer
{
    private static readonly Dictionary<string, JournalDocument> DocumentsByName =
        EditJournalDocuments.All.ToDictionary(document => document.Name);

    private static readonly Dictionary<string, Action<JournalContext, JournalCall>> Handlers = new()
    {
        ["BaseRom"] = (c, call) =>
        {
            string gameCode = call.Require("gameCode").GetValue<string>();
            if (!string.Equals(gameCode, c.Metadata.GameCode, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException($"Journal was recorded against {gameCode}, but this project is {c.Metadata.GameCode}.");
            }
        },
        ["CleanUpMapData"] = (c, call) => MapDataCleanupService.Apply(c.Project, c.Metadata, call.Require("merge").GetValue<bool>(), call.Require("remove").GetValue<bool>()),
        ["ImportPokemonCry"] = (c, call) => PokemonCryService.Import(c.Project, c.Metadata, Int(call, "species"), Source(call), new PokemonCryService.ImportOptions(
            call.Get("format") is JsonNode format ? format.GetValue<int>() : 2,
            call.Get("sampleRate") is JsonNode rate ? rate.GetValue<int>() : 0,
            call.Get("gainDb") is JsonNode gain ? gain.GetValue<double>() : 0,
            call.Get("normalize") is JsonNode normalize && normalize.GetValue<bool>())),
        ["SetPokemonCrySettings"] = (c, call) => PokemonCryService.ApplySettings(c.Project, c.Metadata, Int(call, "species"), new PokemonCryService.Settings(
            Int(call, "note"), Int(call, "attack"), Int(call, "decay"), Int(call, "sustain"), Int(call, "release"), Int(call, "pan"), Int(call, "sampleRate"),
            call.Require("loops").GetValue<bool>(), Int(call, "loopStart"), 0, 0, 0, 0)),
        ["SetSequenceNote"] = (c, call) => MusicService.EditNotes(c.Project, c.Metadata, Int(call, "sequence"), [SequenceNoteOp(call, create: false)]),
        ["AddSequenceNote"] = (c, call) => MusicService.EditNotes(c.Project, c.Metadata, Int(call, "sequence"), [SequenceNoteOp(call, create: true)]),
        ["DeleteSequenceNote"] = (c, call) => MusicService.EditNotes(c.Project, c.Metadata, Int(call, "sequence"),
            [new SequenceEditor.NoteOp(Int(call, "offset"), 0, 0, 1, Delete: true, Waits: call.Get("waits") is not JsonNode waits || waits.GetValue<bool>())]),
        ["ImportBattleBackdrop"] = (c, call) => BattleBackdropImporter.Save(c.Project, c.Metadata, Int(call, "backdrop"),
            [call.Get("day")?.GetValue<string>(), call.Get("evening")?.GetValue<string>(), call.Get("night")?.GetValue<string>()]),
        ["CreateMatrix"] = (c, call) => ExpectId(call, "matrix", MatrixService.Add(c.Project)),
        ["DeleteLastMatrix"] = (c, call) => RequireTrue(call, MatrixService.RemoveLast(c.Project)),
        ["CreateTextArchive"] = (c, call) => ExpectId(call, "archive", TextArchiveService.Add(c.Project)),
        ["DeleteLastTextArchive"] = (c, call) => RequireTrue(call, TextArchiveService.RemoveLast(c.Project)),
        ["ImportTextArchive"] = (c, call) => TextArchiveService.Import(c.Project, Int(call, "archive"), Source(call)),
        ["CreateAreaData"] = (c, call) => ExpectId(call, "areaData", AreaDataService.Add(c.Project)),
        ["DeleteLastAreaData"] = (c, call) => RequireTrue(call, AreaDataService.RemoveLast(c.Project)),
        ["ImportAreaData"] = (c, call) => AreaDataService.Import(c.Project, Int(call, "areaData"), Source(call)),
        ["CreateTexturePack"] = (c, call) => ExpectId(call, "pack", TexturePackService.Add(c.Project, Kind(call))),
        ["DeleteLastTexturePack"] = (c, call) => RequireTrue(call, TexturePackService.RemoveLast(c.Project, Kind(call))),
        ["ImportTexturePack"] = (c, call) => TexturePackService.Import(c.Project, Kind(call), Int(call, "pack"), Source(call)),
        ["ImportMapSection"] = (c, call) => MapFileService.ImportSection(c.Project, c.Metadata, Int(call, "map"), Enum.Parse<MapFileSection>(call.Require("section").GetValue<string>()), Source(call)),
        ["BlankMapSoundPlates"] = (c, call) => MapFileService.BlankSoundPlatesOf(c.Project, c.Metadata, Int(call, "map")),
        ["ImportMapFile"] = (c, call) => MapFileService.ImportWholeFile(c.Project, c.Metadata, Int(call, "map"), Source(call)),
        ["CreateMapFile"] = (c, call) => ExpectId(call, "map", MapFileService.Add(c.Project, c.Metadata)),
        ["DeleteMap"] = (c, call) => MapDeletionService.Delete(c.Project, c.Metadata, Int(call, "map")),
        ["DeleteLastMapFile"] = (c, call) => RequireTrue(call, MapFileService.RemoveLast(c.Project)),
        ["AddOverworldEntry"] = (c, call) => OverworldTableExpansion.Add(c.Project, c.Metadata, (uint)Int(call, "entry"), (uint)Int(call, "sprite"), (uint)Int(call, "cloneFrom"),
            call.Get("copySpriteFrom") is JsonNode copy ? (uint)copy.GetValue<int>() : null),
        ["DeleteOverworldEntry"] = (c, call) => OverworldTableExpansion.Delete(c.Project, c.Metadata, (uint)Int(call, "entry")),
        ["CreateHeader"] = (c, call) => ExpectId(call, "header", HeaderFileService.Add(c.Project, c.Metadata)),
        ["DeleteLastHeader"] = (c, call) => RequireTrue(call, HeaderFileService.RemoveLast(c.Project, c.Metadata)),
        ["CreateScriptFile"] = (c, call) => ExpectId(call, "script", Task.Run(() => HeaderFileService.AddScriptFileAsync(c.Project, c.Metadata, Path.Combine(AppContext.BaseDirectory, "Tools"))).GetAwaiter().GetResult()),
        ["CreateLevelScript"] = (c, call) => ExpectId(call, "levelScript", HeaderFileService.AddLevelScript(c.Project)),
        ["CreateEncounterFile"] = (c, call) => ExpectId(call, "encounterFile", WildEncounterService.AddBlank(c.Project)),
        ["CreateEventFile"] = (c, call) => ExpectId(call, "eventFile", HeaderFileService.AddEventFile(c.Project)),
        // Toolbox patches run on the legacy bridge next to the app, the same way they were applied.
        ["ApplyRomPatch"] = (c, call) =>
        {
            uint? offset = call.Get("offset") is JsonNode node ? node.GetValue<uint>() : null;
            string patch = call.Require("patch").GetValue<string>();
            RomToolboxResult result = Task.Run(() => RomToolboxService.ApplyAsync(c.Project, c.Metadata, Path.Combine(AppContext.BaseDirectory, "Tools"), patch, offset)).GetAwaiter().GetResult();
            if (!result.Succeeded) throw new InvalidOperationException($"ROM patch {patch} could not be applied: {result.Message}");
        },
        ["SetTexturePackPalette"] = (c, call) => TexturePackService.SetPalette(
            c.Project,
            Kind(call),
            Int(call, "pack"),
            call.Require("palette").GetValue<string>(),
            call.Require("colors").AsArray().Select(color => (ushort)(color?.GetValue<int>() ?? 0)).ToList()),
        ["ImportBuildingModel"] = (c, call) => BuildingModelService.Import(
            c.Project,
            call.Require("interior").GetValue<bool>(),
            Int(call, "model"),
            Source(call)),
        ["EditScriptFile"] = (c, call) => ScriptFileService.ApplyEdit(
            c.Project,
            Int(call, "script"),
            Int(call, "line"),
            Int(call, "remove"),
            call.Require("insert").AsArray().Select(line => line?.GetValue<string>() ?? string.Empty).ToList()),
        ["EditLuaScript"] = (c, call) => LuaScriptService.ApplyEdit(
            c.Project,
            Int(call, "script"),
            Int(call, "line"),
            Int(call, "remove"),
            call.Require("insert").AsArray().Select(line => line?.GetValue<string>() ?? string.Empty).ToList()),
        ["EditGlobalLua"] = (c, call) => LuaScriptService.ApplyGlobalEdit(
            c.Project,
            Int(call, "line"),
            Int(call, "remove"),
            call.Require("insert").AsArray().Select(line => line?.GetValue<string>() ?? string.Empty).ToList()),
        ["EditLevelLua"] = (c, call) => LevelScriptNames.ApplyEdit(
            c.Project,
            Int(call, "line"),
            Int(call, "remove"),
            call.Require("insert").AsArray().Select(line => line?.GetValue<string>() ?? string.Empty).ToList()),
        ["EditTextLua"] = (c, call) => TextArchiveNames.ApplyEdit(
            c.Project,
            Int(call, "line"),
            Int(call, "remove"),
            call.Require("insert").AsArray().Select(line => line?.GetValue<string>() ?? string.Empty).ToList()),
        ["DeleteLuaScript"] = (c, call) => LuaScriptService.Delete(c.Project, Int(call, "script")),
        ["ImportOverworldSpriteImage"] = (c, call) =>
        {
            if (!Directory.Exists(c.Project.OverworldSpritesDirectory)) new RomExtractionService().PrepareOverworldSprites(c.Project, c.Metadata);
            OverworldSpriteService.ImportImage(c.Project, (uint)Int(call, "sprite"), Int(call, "palette"), Source(call));
        },
        ["ImportPokemonIcon"] = (c, call) =>
        {
            if (!Directory.Exists(c.Project.MonIconsDirectory)) new RomExtractionService().PrepareSprites(c.Project, c.Metadata);
            PokemonIconService.Import(c.Project, c.Metadata, Int(call, "icon"), Source(call));
        },
        ["ImportTrainerGraphic"] = (c, call) =>
        {
            TrainerGraphicSide side = call.Get("side")?.GetValue<string>() == "Back" ? TrainerGraphicSide.Back : TrainerGraphicSide.Front;
            if (!Directory.Exists(c.Project.TrainerGraphicsDirectoryFor(side))) new RomExtractionService().PrepareSprites(c.Project, c.Metadata);
            TrainerGraphicService.Import(c.Project, c.Metadata, Int(call, "trainerClass"), Source(call), side);
        },
        ["ImportItemIcon"] = (c, call) =>
        {
            if (!Directory.Exists(c.Project.ItemIconsDirectory)) new RomExtractionService().PrepareItemData(c.Project, c.Metadata);
            ItemIconService.Import(c.Project, c.Metadata, Int(call, "item"), Source(call), Bool(call, "newFiles"));
        },
        ["ImportBattleSprite"] = (c, call) =>
        {
            int species = Int(call, "species");
            bool shiny = call.Require("shiny").GetValue<bool>();
            var slot = Enum.Parse<BattleSpriteService.BattleSpriteSlot>(call.Require("slot").GetValue<string>());
            string source = Source(call);
            (byte[] indices, _) = BattleSpriteService.ImportSpriteImage(source, BattleSpriteService.LoadBattlePalette(c.Project, species, shiny));
            BattleSpriteService.SaveBattleSprite(c.Project, species, slot, indices,
                c.Metadata.HeaderFormat == MapHeaderFormat.DiamondPearl, source, shiny);
        },
    };

    static EditJournalReplayer()
    {
        // Handlers are dispatched first, so one named like a document op would silently swallow it.
        string? clash = Handlers.Keys.FirstOrDefault(name =>
            Enum.GetNames<JournalOpVerb>().Any(verb => name.StartsWith(verb, StringComparison.Ordinal) && DocumentsByName.ContainsKey(name[verb.Length..])));
        if (clash is not null) throw new InvalidOperationException($"Journal handler '{clash}' collides with a document op name.");
    }

    public static int Replay(RomProject project, RomGameMetadata metadata, string journalPath, Action<string>? log = null)
    {
        var context = new JournalContext(project, metadata);
        List<JournalCall> calls = EditJournalFormat.Parse(journalPath).ToList();

        int applied = 0;
        int i = 0;
        while (i < calls.Count)
        {
            JournalCall call = calls[i];
            if (Handlers.TryGetValue(call.Function, out var handler))
            {
                Run(call, () => handler(context, call));
                applied++;
                i++;
                continue;
            }

            (JournalDocument document, JsonObject keys, _) = ResolveDocumentCall(call);
            int batchEnd = i + 1;
            while (batchEnd < calls.Count && IsSameDocument(calls[batchEnd], document, keys)) batchEnd++;

            List<JournalCall> batch = calls.GetRange(i, batchEnd - i);
            Run(call, () =>
            {
                object? root = document.Load(context, keys);
                foreach (JournalCall opCall in batch)
                {
                    (_, _, JournalOp op) = ResolveDocumentCall(opCall);
                    try
                    {
                        root = EditJournalModel.Apply(root, document.RootType, op);
                    }
                    catch (Exception ex) when (ex is not ReplayException)
                    {
                        throw new ReplayException(opCall, ex);
                    }
                }
                document.Save(context, keys, root ?? throw new InvalidDataException($"{document.Name} became null."));
            });
            log?.Invoke($"Line {call.LineNumber}: {document.Name} {EditJournalFormat.FormatArgs(keys)} ({batch.Count} change(s))");
            applied += batch.Count;
            i = batchEnd;
        }

        return applied;
    }

    public sealed class ReplayException(JournalCall call, Exception inner)
        : Exception($"Journal line {call.LineNumber} ({call.Function}) failed: {inner.Message}", inner);

    private static void Run(JournalCall call, Action action)
    {
        try
        {
            action();
        }
        catch (Exception ex) when (ex is not ReplayException)
        {
            throw new ReplayException(call, ex);
        }
    }

    private static (JournalDocument Document, JsonObject Keys, JournalOp Op) ResolveDocumentCall(JournalCall call)
    {
        foreach (JournalOpVerb verb in Enum.GetValues<JournalOpVerb>())
        {
            string prefix = verb.ToString();
            if (!call.Function.StartsWith(prefix, StringComparison.Ordinal)) continue;
            if (!DocumentsByName.TryGetValue(call.Function[prefix.Length..], out JournalDocument? document)) continue;

            var keys = new JsonObject();
            foreach ((string name, JsonNode? value) in call.Args)
            {
                if (name is not "path" and not "value") keys[name] = value?.DeepClone();
            }
            string path = call.Get("path")?.GetValue<string>() ?? string.Empty;
            return (document, keys, new JournalOp(verb, path, call.Get("value")?.DeepClone()));
        }

        throw new FormatException($"Line {call.LineNumber}: unknown journal function '{call.Function}'.");
    }

    private static bool IsSameDocument(JournalCall call, JournalDocument document, JsonObject keys)
    {
        if (Handlers.ContainsKey(call.Function)) return false;
        (JournalDocument otherDocument, JsonObject otherKeys, _) = ResolveDocumentCall(call);
        return otherDocument == document && EditJournalFormat.FormatValue(otherKeys) == EditJournalFormat.FormatValue(keys);
    }

    private static SequenceEditor.NoteOp SequenceNoteOp(JournalCall call, bool create)
    {
        int program = call.Get("program") is JsonNode value ? value.GetValue<int>() : -1;
        return new SequenceEditor.NoteOp(Int(call, "offset"), Int(call, "key"), Int(call, "velocity"), Int(call, "length"),
            Waits: call.Get("waits") is not JsonNode waits || waits.GetValue<bool>(),
            Shift: call.Get("shift") is JsonNode shift ? shift.GetValue<int>() : 0,
            Program: program >= 0 ? program : null,
            CurrentProgram: call.Get("currentProgram") is JsonNode current ? current.GetValue<int>() : 0,
            Create: create);
    }

    private static int Int(JournalCall call, string name) => call.Require(name).GetValue<int>();

    private static bool Bool(JournalCall call, string name) => call.Require(name).GetValue<bool>();

    private static string Source(JournalCall call)
    {
        string source = call.Require("source").GetValue<string>();
        return File.Exists(source) ? source : throw new FileNotFoundException($"Source file for {call.Function} no longer exists.", source);
    }

    private static TexturePackKind Kind(JournalCall call) => Enum.Parse<TexturePackKind>(call.Require("kind").GetValue<string>());

    private static void ExpectId(JournalCall call, string name, int actual)
    {
        int expected = Int(call, name);
        if (expected != actual) throw new InvalidDataException($"expected new {name} {expected}, but it was created as {actual}.");
    }

    private static void RequireTrue(JournalCall call, bool succeeded)
    {
        if (!succeeded) throw new InvalidDataException("the last entry could not be removed (at least one must remain).");
    }
}
