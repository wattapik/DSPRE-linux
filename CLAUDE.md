
# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## What this repo is

DSPRE (DS Pokémon ROM Editor Reloaded) is an editor for Nintendo DS Pokémon Generation IV ROMs
(Diamond/Pearl/Platinum, HeartGold/SoulSilver). It edits maps, scripts, events, Pokémon data,
trainers, encounters, and more, by unpacking ROM NARC archives to a working directory, editing the
extracted files, and repacking them.

The repo is mid-migration from a Windows-only WinForms app to a cross-platform Avalonia app, and
**both stacks are live at once** — see Architecture below before editing anything.

## Solution structure

| Project | Framework | Role |
|---|---|---|
| `DS_Map/DSPRE.csproj` | `net8.0-windows` (WinForms+WPF, `AllowUnsafeBlocks`) | **Legacy app.** Still the only place most ROM file formats, editors, and script/NARC logic actually live. |
| `Ekona/Ekona.csproj`, `Images/Images/Images.csproj` | `net8.0-windows` (WinForms) | Sprite/image format libraries (NCGR/NCER/NCLR etc.) used by `DS_Map`. |
| `DSPRE.Core/DSPRE.Core.csproj` | `net8.0`, platform-neutral | **New** shared library: `RomProject`, NARC pack/extract, ROM extraction/build orchestration, and newly-ported services (header, matrix, camera, area data, special encounters, etc.). Per `DSPRE.Core/README.txt`, this project **must never reference Avalonia, WinForms, WPF, Tao.OpenGl, or Windows-only APIs** — that's the whole point of it. |
| `DSPRE.Avalonia/DSPRE.Avalonia.csproj` | `net8.0` | **New** cross-platform UI (Linux/macOS/Windows), built on `DSPRE.Core`. One large code-behind file, `MainWindow.axaml.cs` (~4400 lines), backs `MainWindow.axaml`. Uses OpenTK for 3D map rendering (`MapGlView.cs`) instead of the legacy Tao.OpenGl path. |
| `DSPRE.LuaLanguageServer/DSPRE.LuaLanguageServer.csproj` | `net8.0`, exe `dspre-lua-ls` | LSP server for the Lua scripts (see Lua editor support below); references only `DSPRE.Core`. |
| `DSPRE.ScriptTool/DSPRE.ScriptTool.csproj` | `net8.0-windows`, `RuntimeIdentifier=win-x64` | Headless CLI **bridge**: references `DS_Map/DSPRE.csproj` directly and exposes legacy script rebuild/export and map-model export (via `apicula.exe`) as `rebuild`/`export`/`export-map` subcommands, so the new stack can call into old, not-yet-ported logic. |

### Why the bridge exists (read this before "porting" something)

`DSPRE.Avalonia` cannot link `DS_Map/DSPRE.csproj` directly (WinForms/Tao.OpenGl, Windows-only, and
`DSPRE.Core`'s no-Windows-deps rule would be violated transitively). Instead, whenever Avalonia needs
legacy script or map-model logic it hasn't been ported yet, `DSPRE.Core/RomExtractionService.cs`
shells out to a prebuilt `DSPRE.ScriptTool.exe` sitting under `Tools/ScriptTool/` next to the app:

- On Windows it's invoked directly.
- On Linux/macOS it's invoked via `wine` (`ResolveTool`/`RunScriptBridgeAsync` in
  `DSPRE.Core/RomExtractionService.cs`), since ScriptTool is only ever built as a Windows binary.

`DSPRE.Avalonia.csproj` has an MSBuild target (`BuildScriptTool`) that `dotnet publish`es
`DSPRE.ScriptTool` as a self-contained `win-x64` single-file exe before every build, so the bridge
exe is always up to date locally.

When adding a new cross-platform feature: put genuinely new/platform-neutral logic in `DSPRE.Core`.
Only fall back to routing through `DSPRE.ScriptTool` when the feature depends on legacy code in
`DS_Map` that hasn't been (and isn't worth being) ported yet.

### `RomInfo.gameFamily`/`RomInfo.gameVersion`

Almost everything format-related branches on game version — DP vs Platinum vs HGSS use different
file offsets, header formats, script command sets, and encounter table layouts (and DP/Pt vs HGSS
often use entirely different NARC paths, e.g. `fielddata/script/scr_seq.narc` vs `a/0/1/2`). Check
`RomInfo.gameFamily` (legacy) or `RomGameMetadata`/`MapHeaderFormat` (`DSPRE.Core`) before assuming a
format.

## Build commands

Requires the .NET 8+ SDK (works with dotnet 8, 9, or 10 SDKs — verified with 10.0.104 here).

```bash
# One-time (or whenever DSPRE.ScriptTool.csproj's deps change): restore its win-x64 RID assets.
# Building DSPRE.Avalonia without this fails with NETSDK1004 ("assets file not found") because its
# pre-build target publishes ScriptTool for win-x64 and needs --no-restore assets already present.
dotnet restore DSPRE.ScriptTool/DSPRE.ScriptTool.csproj -r win-x64

# Build the new cross-platform app (also builds DSPRE.Core, DS_Map, Ekona, Images, and
# self-contained-publishes DSPRE.ScriptTool as a side effect of its BuildScriptTool target)
dotnet build DSPRE.Avalonia/DSPRE.Avalonia.csproj

# Build just the platform-neutral library
dotnet build DSPRE.Core/DSPRE.Core.csproj

# Run the Avalonia app
dotnet run --project DSPRE.Avalonia/DSPRE.Avalonia.csproj
```

`dotnet build DS_Map.sln` (the whole solution, including the legacy `DS_Map` WinForms app) works on
Windows via `msbuild` — that's what CI (`.github/workflows/*.yaml`) uses on `windows-latest` runners,
after separately cloning the `scrcmd-database` and `ds-rom` repos and copying `dsrom.exe` into
`DS_Map/bin/Release/Tools/`. On Linux, building the full `.sln` in one `dotnet build` invocation can
race on `Ekona`'s `obj/`/`bin/` output between the parallel legacy-project and Avalonia-triggered
builds; building `DSPRE.Avalonia/DSPRE.Avalonia.csproj` directly (which pulls in everything it needs
via project references) avoids that.

`dotnet test DSPRE.Tests` runs the xunit suite (`DSPRE.Tests`, references `DSPRE.Core` only). `PlatinumFixture` extracts
`test/pokeplat.nds` once per run into a temp directory and gives each test its own copy of the prepared project (no ROM source
stamp, so nothing reaches a real edit journal). Tests drive the Core services the UI edits go through: load, change, save,
reload, and "unchanged save is byte-identical" checks. No UI-level (Avalonia) tests exist. `test/TRAGEDY.nds` is a sample ROM.

### Runtime dependencies not in source control

The app expects a `Tools/` directory (external `dsrom.exe`, `apicula.exe`, `blz.exe`, `charmap.xml`/
`charmap.json`, plus the published `ScriptTool/` bridge) and a script command `databases/` directory
alongside the executable — both are populated by CI from separate repos
(`DS-Pokemon-Rom-Editor/scrcmd-database`, `Prof9/ds-rom`) rather than committed here.

## Architecture (legacy `DS_Map`, still where most logic lives)

- **`RomFile`** (`DS_Map/ROMFiles/RomFile.cs`) — abstract base for every ROM data structure
  (`MapFile`, `MapHeader`, `EventFile`, `ScriptFile`, `EncounterFile`, `TrainerFile`, `TextArchive`,
  `GameMatrix`, `AreaData`, etc.). Each implements `ToByteArray()` for serialization back to ROM
  format; loading happens via constructors/`FromByteArray()` that parse binary NARC contents.
- **`Filesystem.cs`** — static helpers for ROM I/O and NARC pack/unpack path resolution.
  **`Narc.cs`** — Nitro Archive (NARC) format handling (BTAF/BTNF/GMIF sections).
- **`RomInfo.cs`** — static, globally-accessed ROM context: current game version/family, working
  directory, and `gameDirs` (per-game-version path table for every NARC section).
- **Script system** (`DS_Map/ROMFiles/ScriptFile.cs`, `DS_Map/Resources/ScriptDatabase.cs`) — the
  most elaborate subsystem. Scripts exist in dual form: binary (ROM/NARC) and human-readable
  plaintext `.script` files under `expanded/scripts/`. Plaintext is the preferred editing surface
  (editable in-app via ScintillaNET or externally in VS Code); binary is rebuilt from plaintext
  automatically whenever the plaintext file is newer, both on load and on ROM save. Command
  definitions come from JSON "script command databases" (built-in per game version, or user-supplied
  custom ones); an MD5 hash of the active database is stored in `expanded/scripts/.database_hash` so
  a database change triggers a full plaintext re-export instead of silently going stale.
- **3D graphics** (`DS_Map/LibNDSFormats/`) — NSBMD (models), NSBTX (textures), NSBCA/NSBTA/NSBTP
  (animations). Legacy rendering is Tao.OpenGl + HelixToolkit with column-major matrices (`MTX44`);
  the Avalonia side renders the same NSBMD data via OpenTK instead (`DSPRE.Avalonia/MapGlView.cs`).
- **Editors** (`DS_Map/Editors/`) — either `UserControl`s embedded in the legacy MDI main window or
  standalone `Form`s. Two dirty-tracking conventions coexist: `Form`s track a local `dirty` bool and
  gate closing with `CheckDiscardChanges()`; `UserControl` tab editors implement
  `IEditorWithUnsavedChanges` and self-register with `OpenEditorsRegistry` while dirty.
- **Handler-disable pattern**: event handlers that mutate other controls' values must guard with
  `if (Helpers.HandlersDisabled) return;` and wrap programmatic UI updates in
  `Helpers.DisableHandlers()`/`EnableHandlers()`, or setup code re-triggers handlers and produces
  false dirty state.

## ROM project formats

DSPRE supports two on-disk project layouts, auto-detected by marker file
(`RomProject.TryOpen` in `DSPRE.Core`, `RomInfo.IsDsRomProject` in legacy code):

- **ds-rom** (current default) — `header.yaml`/`config.yaml` present; filesystem root at `files/`,
  ARM9 at `arm9/arm9.bin`, overlays at `arm9_overlays/ov{ID:D3}.bin`. Built/extracted via `dsrom.exe`
  (from the [ds-rom](https://github.com/Prof9/ds-rom) project); overlay compression is automatic.
- **ndstool** (legacy) — `header.bin` present; root at `data/`, ARM9 at `arm9.bin`, overlays at
  `overlay/overlay_{ID:D4}.bin`. Supported for conversion only; converting to ds-rom backs up the
  original project as a `.ndstool_backup.zip` first.

Both `RomExtractionService` (`DSPRE.Core`) and the legacy save path pick tool arguments and paths
based on which format is active — check this before adding new file-path logic.

## Edit journal (`DSPRE.Core/EditJournal*.cs`)

Every edit made through the Avalonia app is appended to `<rom name>.edit-journal.dspre` next to the ROM the
project was extracted from (inside the project only when no ROM is known), a git-friendly,
replayable log (`SetMapHeader(header: 3, path: "MusicDayId", value: 1011)`,
`AddMapEvent(eventFile: 3, path: "Overworlds[0]", value: {...})`, `EditScriptFile(...)`,
`ImportTexturePack(..., source: "/abs/path")`...). `DSPRE.Avalonia --replay-journal <journal> --base-rom
<base.nds> --output <out.nds>` regenerates the edited ROM from the base ROM; the GUI's "Replay Journal"
button applies a journal to the open project.

- Document saves are journaled by diffing the document **as re-read from disk** before and after the
  write (`EditJournal.Track`), so each Core `Save` wraps its write in `Track` and the document kind is
  registered in `EditJournalDocuments` (load/save by key). File-level operations (create/delete/import
  a whole file) call `EditJournal.Record` and need a matching handler in `EditJournalReplayer`.
- **Any new way to modify the project must go through a Core service that journals it** — never write
  project files directly from `MainWindow.axaml.cs`, or the edit silently drops out of the journal.
- A `Load` that normalizes data (re-sorting, dropping fields) hides those details from the journal, so
  loads should reflect the bytes on disk (see `LearnsetService.Load`).

## Unsaved changes (`DSPRE.Avalonia/MainWindow.UnsavedChanges.cs`, `MainWindow.PendingChanges.cs`)

`CollectUnsavedChanges` is the one list of everything not saved yet; Save All, the sidebar's "!" markers,
the close/open-project/Save ROM prompts and "keep the editor as it was when reopened" all read it.

- A new editor must add its changes there, with its sidebar title as `Editor`, and `OpenDocument: true`
  for the document that opening another one replaces (one trainer, one script file) as opposed to a table
  the editor keeps whole. Track documents with an `EditTracker` whose baseline is what is on disk (a
  shared in-memory copy may already hold edits) and keep UI-bound rows synced into the document as they
  are edited, not only on save.
- An editor that switches documents goes through `ShouldLoadDocumentAsync`/`ConfirmLeavingDocumentAsync`
  (Save / Discard / Cancel), from its selector and from every go-to path; `TextChanged` arriving after a
  load must not count as an edit. The Map Editor instead keeps edited headers, buildings and permissions
  per id and saves them all with Save Map, since its header follows the map views.
- Inputs the user changed get a "!" adorner (`MainWindow.ControlMarks.cs`) without per-editor code: a new
  editor's panel only needs an entry in `FindInputEditor`, and inputs that navigate, filter or only change
  the view go in `UnmarkedInputs`.

## Map Creator (`DSPRE.Core/MapCreator*.cs`, `DSPRE.Avalonia/MainWindow.MapCreator.cs`, `MapCreatorGridView.cs`)

Builds a map's terrain from tiles on a 32 x 32 grid (9 layers, heights -15..15), as Pokemon DS Map Studio does.
Work files live in `expanded/map_creator/<name>/` as `name.pdsmap` + `name.pdsts` + PNG textures, in Studio's own
formats, so either program opens them; they are not journaled. Only **Build** touches the project, and it reuses the
staged OBJ import (`MapModelImportService`, `mapModelPendingImports`), so Save All journals the model like any other.

- `MapCreatorTileset` — port of Studio's `.pdsts` (big-endian tag stream) to expanded polygons (`MapTile`, `TilePolygon`);
  `Merge` adds another tileset's tiles and textures. `.pdsts.meta` (tile names/palette folders) is not read or written.
- `MapCreatorMap` — `.pdsmap` text grid. Column 0/row 0 is the south-west corner; tile -1 is empty.
- `MapCreatorComposer` — Studio's `ObjWriter.writeMapObj` without its stretching of tileable runs (a tile's "Stretch"/"Repeat" flags are read and written for Studio but ignored: stretched runs came out with the wrong texture scale, e.g. grass 4x too big and a row of trees smeared, while `MapCreatorPolygonMerger` fuses neighbours without changing the picture), then OBJ + MTL + textures. Studio space is z-up with 1 unit per cell; `ObjToImd.Rotate` turns it y-up.
- `MapCreatorTileCutter` — cuts a project map's apicula GLB into one-cell tiles (glTF x,y,z = Studio x,z,-y, cell = 16 units,
  map centred on the origin), clipping polygons at the cell borders.
- `MapCreatorPolygonMerger` — run at the end of `Compose`: fuses the pieces clipping made (same plane, same texture mapping,
  meeting edge to edge) back into the original polygons, so a re-built map has about the polygon count it was cut from and
  fits the game's model size limit. Without it a sliced map has several times the polygons and gets decimated into holes.
- Texture coordinates have a ceiling: `ObjToImd.FixTextureCoordinates` clamps a polygon's coordinates to +-2030 texels about their middle (the DS stores them in 16 bits), which squeezes anything beyond it into a *stretched* texture. So the polygon merger (`FitsRepeatLimit`, using `ObjToImd.NearestPowerOfTwo` of the texture size) never fuses past `(span + 1) * textureSize <= 4060` nor 8 repeats of the texture over one polygon (the DS interpolates coordinates coarsely over big polygons; Studio stops at 8). `Compose(compact: false)` skips the merging: the 3D preview and the grid view's layer image draw every cell as it is, and only Build merges. A map built before this keeps its stretched polygons until it is built again.
- Over the model size limit, `ObjToImd` simplifies with `MeshDecimator` (quadric edge collapse). It first removes only vertices inside a flat patch (not on a border or texture seam) and charges what a collapse does to the texture mapping of the triangles around it (`MappingError`), and border vertices carry border quadrics, so a map that is only a little too big loses next to nothing; an earlier version deleted whole isolated polygons for free and smeared textures (holes, a bridge showing rocks).
- `ObjToImd` writes triangle/quad **strips** (as Studio's `StripeCalculator` does), which is what keeps model sizes near the originals.
- Dragging the paint tool with a tile of several cells snaps to a lattice of the tile's size anchored at the stroke's first cell (`PaintCreatorTile`), so the copies sit side by side rather than overlapping.
- `MapCreatorThumbnails` renders a tile top-down on the CPU for the palette and grid.

## Changing a map's header (`DSPRE.Core/MapHeaderAssignmentService.cs`, `MainWindow.Headers.cs`)

The Header pane of the Map Editor has a "Header of map NNNN" dropdown (Single Map view) that picks which header the open map belongs to, as the Map Creator needs for a new map no header reaches (without one the Map Editor finds no area data and draws it untextured). `Assign` finds the matrix that places the map (the reaching header's, else the first holding it), gives the map's squares that header (a matrix with no header section gets one first, every other square keeping the header that owned the matrix) and points the header at the matrix, which changes the header's own matrix when it differed (the dialog says so). Saved at once through `MatrixService.Save` / the header store, so it journals like those.

The **Header Manager** (`MainWindow.HeaderManager.cs`, under Maps, an extra editor like the others) lists every header with its matrix, area data and texture pack, the maps it reaches and the maps no header reaches (`mapHeaderIds`), and can point a header at another matrix, give an unreached map to a header (`MapHeaderAssignmentService.Assign`) and add/remove headers (the Header pane's handlers). Assignments and add/remove are saved at once; the header's own values (every field of `MapHeaderRecord` plus the internal name; fields the Header pane has as lists - area data, matrix, script file, level script, text archive, event file, encounter file, music, location, specifier, icon, weather, camera - are lists with the same names, copied from the Header pane's own lists in `FillHeaderManagerCombos`; HGSS-only fields hide on the other games) are edited in the selected header's card and staged in `headerManagerPending` until Save Header or Save All (listed by `CollectUnsavedBattleDataChanges` as "Header Manager"). Location names are messages of one text archive (`LocationNameService`: DP 382, Platinum 433, HGSS 279/272); both the Header pane and the Header Manager have a "New Name..." button next to the location list that appends a message and picks it (journaled as a text archive edit). Renaming a header, changing its place, or editing the place-name text archive calls `RefreshHeaderNames` (`MainWindow.Headers.cs`), which rewrites every list showing those names (header selector, maps' names, matrix labels, the Header pane's lists, the Header Manager) without reselecting anything. The Map Editor's views no longer show the texture pack pickers (they are hidden, not removed: the views still read the pack from the selectors, which the area data sets) and the Events pane has no "Linked Script" box; packs are changed in the Texture Editor / Area Data. The whole-project part (unreached maps, add/remove) sits under a divider, apart from the selected header's card.

## Adding a map (`DSPRE.Core/NewMapService.cs`)

A new map (Map Creator "Build As New Map", the Map Model Editor's OBJ import as a new map, the Map Editor's "Add Map File") is made and linked in one go by `NewMapService.WireAsync`: its own area data (with the map's texture pack), a one-map matrix, a header (named after the map) and the header's own text archive, script file, level script, event file and a blank wild encounter file (`WildEncounterService.AddBlank`, journaled as `CreateEncounterFile`), all written into that header. It needs the Dynamic Header Allocation patch, since a header cannot be added otherwise; without it `RequireCanAddMaps` throws `RequirementMessage` before anything is changed and the UI (`EnsureCanAddMapsAsync`) tells the user to apply the patch first. The wiring itself has only been tried against a patched project by hand, not in the test suite (the fixture is unpatched).

## Deleting a map (`DSPRE.Core/MapDeletionService.cs`)

"Delete This Map..." (Map File menu) and "Delete Map..." (Map Model Editor) call `MapDeletionService.Delete`: every matrix square placing the map is emptied, and **no map is renumbered** (the game's code may name map numbers). The last map's file is removed; any other becomes a placeholder (the smallest model in the project, permissions zeroed, no buildings), journaled as `DeleteMap`. The header, area data and events are kept (Remove Unused Map Data clears those, and does renumber). The project is reloaded afterwards.

## Command line (`DSPRE.Avalonia/BuildCommand.cs`, `DSPRE.Core/LuaBuildService.cs`)

`DSPRE.Avalonia build -p <project_dir> [-o out.nds]` and `check -p <project_dir>` run without the UI (next to `--replay-journal`). `LuaBuildService.Compile` compiles every `expanded/scripts/NNNN.lua` as saving it in the Script Editor does (`CompileForSave`, message numbering, the script's text archive taken from the first header using it) and collects the errors per file with their line (`error: 0002.lua:2: ...`, global.lua errors under `global.lua`) rather than stopping at the first. `DSPRE.Avalonia decompile -p <project_dir> [--only 1,5-9] [--force] [--dry-run]` converts every `NNNN.script` to `NNNN.lua` (`LuaLiftService`, the same checked `LuaScriptService.Lift` as Convert to Lua; messages as text from `ScriptArchiveLookup`, shared with `LuaBuildService`): files that already have a `.lua` are skipped without `--force`, the first conversion creates `global.lua` from `DefaultGlobal`, a script that fails the equivalence check is listed and left, `.script` files are untouched, and `.lua` saves are journaled when the source ROM is known. `check` writes nothing; `build` writes the `.script` files and archives (journaled when the project's source ROM is known), stops with exit 1 on any error, and otherwise runs `RomProjectPipeline.BuildRomAsync` and overwrites the ROM the project was extracted from (`.dspre-source.json`) unless `-o` is given. The project must have been opened in DSPRE once (it needs the exported scripts).

## Matrix saves move events (`DSPRE.Core/MatrixEventShiftService.cs`)

An event stands where its matrix square (`MatrixPositionX/Y`) plus its place in the map (`MapPositionX/Y`) say, in the event file of the header that owns the square. Moving a map to another square of its matrix (expanding the matrix around it) left its events in the old square, so a warp into the map landed in black space. The Map Editor's matrix saves go through `MatrixEventShiftService.Save` (`SaveMatrixWithEvents`): maps that appear once in the old and the new matrix in different squares have the events of their old square moved to the new one (through `MapEventService.Save`, so they are journaled). A warp's destination is a *warp number* in the destination header's event file, not a position, so it needs no change; it does need to be a number that file has.

## Matrix texture packs (`DSPRE.Core/MatrixTexturePackService.cs`, `NsbtxMerger.cs`)

A matrix is drawn with one map texture pack, the one its header's area data names, whichever maps it places; a model finds its textures and palettes in it by name. A map from another area (the Mystery Zone border around a Map Creator map) looks for names its own pack has and this one lacks, and draws wrong or untextured. `MatrixTexturePackService.Complete` (the Matrix pane's "Complete Texture Pack") reads each placed map's texture/palette pairs (`NsbmdMaterialPairs`), and for the ones the matrix's pack lacks adds them, as raw bytes, from the pack the map normally resolves to (`ResolveMapAreaData`), writes the result to `expanded/texture_merges/` and imports it (journaled as `ImportTexturePack`). `NsbtxMerger` parses a BTX0's texture and palette dictionaries and rebuilds them: a dictionary is a header, a Patricia tree over the names as 128-bit little-endian numbers (root node bit 127 always left, node i+1 for name i; `BuildTree`/`Search`, checked against every dictionary of a real ROM) and the entries and names. Packs with 4x4-compressed textures are refused. A texture a model names that no pack has (`tshadow` on map 173 in Platinum) stays missing and is reported.

## Map data clean-up (`DSPRE.Core/MapDataCleanupService.cs`, ROM Toolbox buttons)

"Compress Map Data" merges identical map files, matrices, area data and map/building texture packs; "Remove Unused Map
Data" drops entries no header reaches (header -> matrix -> maps, header -> area data -> texture packs). What is left is
renumbered and all references rewritten. It runs under `EditJournal.Suppress()` and is journaled as one
`CleanUpMapData(merge, remove)` call that the replayer re-runs, so inner saves must not be journaled separately.
It cannot know about ids the game's code hard-codes. Map Creator `built.txt` files and the `expanded/map_models` caches are fixed up/cleared.

## Platinum map animations (`DSPRE.Core/FieldTextureAnimationService.cs`, `MapGlView.SetFrameAnimations`)

Water and flowers in Platinum are picture swaps, not scrolls. `data/fldtanime.narc` member 0 is a table of 52 entries (name,
then frame number/ticks pairs ended by 0xFF); member N+1 is the NSBTX with entry N's frame pictures. An entry animates whichever
map texture of its name the area's pack has; all entries run at once (an area data's third value does *not* select one: packs hold
several animated textures and the values don't follow them). Models name a material `<texture>_lm<number>`, so the viewer
matches materials by that base name and draws each animation's current frame in place of the texture. HeartGold/SoulSilver keep
the scrolling `TextureAnimation` path (`DynamicTextureService`).

## Pokémon cries (`DSPRE.Core/SdatArchive.cs`, `NitroSynth.cs`, `PokemonCryService.cs`, `DSPRE.Avalonia/AudioPlayer.cs`)

Gen IV cries are not sequences: each species has a bank (at index = species number; the `BANK_PVnnn` names are shuffled from species 387 on, so never look banks up by name) with one instrument whose sample is the recording (wave
archive `WAVE_ARC_PVnnn`); the game plays it as middle C. `SdatArchive` reads the sound archive's records and files, `NitroSynth`
decodes the SBNK/SWAR (PCM8/16, ADPCM) and `PokemonCryService.Render` returns mono 16-bit samples. Sequences are played by `NitroSequencer` (see Music Editor). Avalonia has no audio,
so `AudioPlayer` hands a temp WAV to paplay/pw-play/aplay/ffplay, afplay or PowerShell. The Pokédex tab has Play Cry / Save Cry as WAV / Replace Cry from WAV. Cry edits are staged, not written: `MainWindow.pendingCries` holds a `PokemonCryService.Pending` per species (shown through `Preview`, which works on the SDAT bytes in memory), listed by `CollectUnsavedCryChanges` and written by `PokemonCryService.Save` on Save All. A replacement is encoded to ADPCM at the old wave's rate
and must fit the old wave's room in its SWAR (nothing in the SDAT moves; longer recordings are cut with a short fade); it is journaled as `ImportPokemonCry`. `ReadSettings`/`ApplySettings` edit the bank instrument (base note, attack/decay/sustain/release, pan) and
the wave header (rate, loop) in place (`SetPokemonCrySettings`). The original cries are 8-bit, non-looping, ~10.5/13.4 kHz, with A/D/S/R all 127.

## Battle Background Editor (`DSPRE.Core/BattleBackgroundService.cs`, `Lz10.cs`, `MainWindow.BattleBackgrounds.cs`)

`battle/graphic/pl_batt_bg.narc` (Platinum; `batt_bg.narc` Diamond/Pearl) is LZ77-compressed NCGR/NSCR/NCLR members (Platinum: 83 tile
sheets, 92 screens, 167 palettes). Found by comparing renders (the game's table was not read): the 23 **8-bit 1024-tile sheets** (members 3-25)
are the scenic sky/ground backdrops, all drawn through the one 512x256 screen (member 2, tiles up to 1023), and the 69 consecutive
256-colour palettes (from 172) are three per sheet in order: day, evening, night (`BattleBackgroundArchive.Backdrops`). Which header
battle-background number uses which backdrop is **not known** (it is in the battle overlay). Importing: `BattleBackdropImporter.Build` takes up to three 512x256 PNGs (day, evening, night; the layout is the rendered picture: the screen is nearly
an identity layout of the sheet with flips), quantizes the three images *together* (the sheet's indices are shared by the three palettes; index 0 stays
see-through; median cut when over 255 colours), rewrites the sheet and palettes, and `Save` LZ77-compresses them (`Lz10.Compress`) and rebuilds the NARC
(`NarcFile.ReplaceMembers`, members change size). Staged in `pendingBackdrops`, written by Save All, journaled as `ImportBattleBackdrop`.
The rest of the archive (4-bit sheets and
screens: battle menus, platforms...) is only browsable by picking a tile sheet, screen and palette by hand.

## Lua scripts (`DSPRE.Core/Lua*.cs`, `ScriptTextDocument.cs`, `ScriptEquivalence.cs`)

A script file can be written as `expanded/scripts/NNNN.lua` (user reference: `LuaScripting.md`). The Lua is
compiled into the plaintext `.script`, which the ScriptTool bridge still builds the binary from, so the
compiler only ever targets the plaintext format and never the binary.

- `ScriptLanguage` — names from the active command database (ROM's own copy via `ScriptDatabaseStore`, else
  built-in): commands/movements by name and snake_case alias, plus the control-flow commands found by ID.
- `LuaParser` → `LuaScriptCompiler`: Lua runs at compile time; only commands and var/flag conditions become
  script code, built as a block graph and laid out into Scripts/Functions/Actions (identical ones merged).
- `LuaScriptLifter`: `.script` → Lua, inlining single-use Functions and recovering if/elseif/else joins.
- `ScriptEquivalence`: steps two `.script` files side by side (Jump/Call/Return are invisible) and must find
  no difference. `LuaScriptService.Lift` refuses a conversion that fails it, so changes to the lifter or
  compiler must keep every script in a real project round-tripping (lift → compile → equivalent).
- `expanded/scripts/global.lua` is run before every file (its line numbers are offset by
  `LuaException.GlobalLineBase` so errors name it). Its helpers that return a script body are templates
  (`LuaScriptCompiler.FindTemplates`, probed with placeholder arguments) which the lifter matches scripts against.
  Its globals holding `var[...]`/`flag[...]` name them (`FindNames`): scripts read and assign through the
  name, and the lifter writes names instead of numbers.
- A template helper (one that returns a body, like `npc_message`) works both as `script(1, npc_message("Hi"))` and as a statement inside a body, `script(1, function() npc_message("Hi") end)`: a statement call of a global helper that returns a no-argument function runs that function (`LuaCallStmt` in `LuaScriptCompiler`). Before, the statement form discarded the body and compiled to an empty script (`End`) with no warning.
- The Map Editor's Script pane shows the Script Editor's panel (`ShowMapPaneScript`) without the script file picker, the "headers using this script" bar and the text archive column (`SetScriptPanelForMapView`): the script is the header's own and messages are written in the script. The Level Script and Text panes likewise lose their pickers, finders, header jumps and the text archive add/remove/"All archives" controls (`SetLevelScriptAndTextPanelsForMapView`).
- Messages are written as text, `Message("text")`, which compiles to `Message "text"`; on save
  `ScriptMessageAllocator` numbers them and rebuilds the text archive (fully when `FindUse` finds the script
  owns it, otherwise keeping entries other scripts or variable-picked messages may use).
  `ScriptEquivalence` compares messages by text, given both archives.
- `.lua` saves are journaled as `EditLuaScript` line hunks (and `DeleteLuaScript`), global.lua as
  `EditGlobalLua`, alongside the `EditScriptFile` hunks of the compiled `.script`.

## Trainer messages, growth rates and Battle Frontier (`DSPRE.Core/TrainerMessageService.cs`, `GrowthTableService.cs`, `FrontierService.cs`, `DSPRE.Avalonia/MainWindow.BattleData.cs`)

Three editors whose pages are built in code and added to the sidebar by `RegisterExtraEditor` (so a new editor of this kind needs no `MainWindow.axaml` edits). They edit the NARC in `files/` directly through `NarcFile` (no unpacked copy), keep the whole table in memory with an `EditTracker`, and journal through `EditJournalDocuments` (`TrainerMessages`, `GrowthTables`, `FrontierSets`, `FrontierTrainers`).

- **Trainer messages** (legacy `TrainerMessageEditor`): `trtbl` is `(trainer u16, trigger u16)` entries with a trainer's entries together, `trtblofs` the byte offset of each trainer's first entry (0 for none), and entry `i` is message `i` of the trainer message text archive (`TextArchiveId`: 617 Platinum). The order of the original is not sorted, only grouped, so `Save` keeps it (it gathers a trainer's entries at the first one) and an unchanged save is byte-identical. The archive is rewritten under `EditJournal.Suppress()` because the table's own journal diff already carries the text.
- **Growth rates** (`pl_growtbl.narc`, Platinum only): 8 members of 101 `u32` (total experience at each level; 6 and 7 are unused copies of medium fast). `Standard` reproduces all of the game's curves exactly (a test checks every level).
- **Battle Frontier** (Platinum): `pl_btdpm`/`pl_bsdpm` members are 16-byte sets (species u16, 4 moves u16, EV flag byte with one bit per stat in HP/Atk/Def/Spe/SpA/SpD order, nature byte, item u16, a spare u16); member 0 is a zero placeholder so a set id is its member number. `pl_btdtr` members are `class u16, count u16, count x set id u16` padded to 4 bytes. Only edits in place: no sets or trainers are added or removed, since the game's counts are not known.

## Footprints (`DSPRE.Core/FootprintService.cs`, `DSPRE.Avalonia/MainWindow.Footprints.cs`, `FootprintCanvas.cs`)

`poketool/pokefoot/pokefoot.narc` (Platinum): member 0 palette, 1 and 2 the shared animation and cell bank, then member `species + 3` an LZ77-compressed 0x130-byte tile file (species 0 is a placeholder; 108 species have none, e.g. snakes and ghosts). The cell bank draws every footprint as a 16x8 piece from tiles 4,5 above a 16x8 piece from tiles 0,1; tiles 2,3,6,7 are unused fill and are kept as they are. A pixel is 0 or an ink: palette entry 4 or entry 1, both black; a footprint uses one throughout (1 for most of #252-386, 4 for the rest) and `Save` keeps the species' own, so rows are only `.` and `k`. Members are replaced only when their pixels changed, so an unchanged save leaves the NARC identical. Journaled as the `Footprints` document (`SetFootprints(path: "Footprints[41].Rows[9]", ...)`). It is the Pokémon Editor's Footprint tab (added in code after the Sprites tab, following the open species via `LoadPokeEditorSpecies`): it paints on a 16x16 `FootprintCanvas` and imports/exports 16x16 PNGs. The footprints of all species are loaded once and tracked as one table (Pokémon Editor, "Footprints"), written by the tab's Save button or Save All.

## Pokémon shadows and Pokédex areas (`DSPRE.Core/PokemonShadowService.cs`, `PokedexAreaService.cs`, `DSPRE.Avalonia/MainWindow.PokemonBattleTabs.cs`)

The Pokémon Editor's Shadow and Areas tabs, built like the Footprint tab (whole table in memory, one `EditTracker` each, Save button or Save All, journaled as `PokemonShadows` / `PokedexAreas`).

- **Shadows** (Platinum): `poketool/pokegra/poke_shadow.narc` (size 0 none to 3 large), `poke_shadow_ofx.narc` (signed shadow X offset) and `poke_yofs.narc` (signed sprite Y offset) are each one member with one byte per species 0-493. The size meanings come from the data (Diglett, Dugtrio and Hippowdon are 0, the starters are 1, Venusaur and Blastoise are 3); what one step of an offset moves has not been measured.
- **Pokédex areas** (Platinum, `application/zukanlist/zkn_data/zukan_enc_platinum.narc`, 4954 members): members 1 and 3 are tables of the map header each place stands for (21 special places such as mines, caves and lakes; 50 routes and cities; entry 0 unused), members 0 and 2 say where they are drawn. The lists are ten blocks of 495 members, member `4 + block * 495 + species` (national number, species 0 is a placeholder left untouched), each a run of 1-based area numbers ended by 0. Blocks 0-4 use the special places and 5-9 the routes, each as morning, day, night, Honey Tree, Poké Radar. This was worked out by comparing the lists with the games' own encounter tables (about 0.8-0.9 match for the time-of-day lists, 0.77 for Poké Radar, and the Honey Tree lists hold exactly the six Honey Tree species). `Load` reads every member in one pass (`NarcFile.ReadAllMembers`). The tab only changes the area map, not where a species really appears. The Diamond/Pearl files and the swarm and dual-slot encounters were not looked at.
- **The map's places** (`PokedexMapService`, same archive, members 0-3): member 0 is one u32 per special place (x, y, flag, 0; flag 1 on Mt. Coronet only), member 1 their map headers, member 2 36-byte records per route/city (x, y, width, height, then width x height cell flags 0/1, so a place can have holes and covers a box of at most 32 cells), member 3 their map headers; entry 0 of each is unused and kept. Cells and markers are 16 px squares on the Town Map picture, whose top-left is at (16, 8): found by laying every cell on the picture (100% of them land on drawn pixels at that offset, 95% at the next best). The picture is `graphic/tmap_gra.narc`: tiles member 20, screen 26 (a 512x512 background, stored as four 32x32 blocks one after the other), palette member 2; `RenderTownMap` draws it. The pictures can be edited in code (`TownMapPictureService`, the Pokedex Map Editor's Picture mode), but the UI for it is switched off for now (`PictureEditingEnabled` in `MainWindow.PokedexMap.cs`, because an edit did not show in game); the picture is still drawn behind the places. There are two 512 x 512 pictures in `tmap_gra.narc`, chosen with the editor's Picture selector (`TownMapView`): the terrain map (screen 25, palette bank 0, the grey and white Sinnoh landmass - the default, and what the Pokédex area page most likely shows) and the route diagram (screen 26, bank 1: roads and squares). The cells fit both, so geometry cannot say which one the game draws: the first version of the editor only had the route diagram and an edit to it did not show in the Pokédex or the Pokétch, so which picture the Pokédex really uses is not confirmed - test the terrain one in game, and the Pokétch's own town map is in `poketch.narc`, not here. Each picture is loaded as 512 rows of hex digits of its bank's colors (15 colors and see-through), journaled as `SetTownMapPicture(view: "Terrain", path: "Rows[n]", ...)` (no `view` for the route diagram). The tile sheet (member 20, 768 tiles) and the palette are shared with the archive's other screens, so on save a changed 8 x 8 block reuses a tile (flipped if need be) that already has its pixels or takes a tile no other screen uses (about 80 are free); unchanged blocks keep their tile, so an unchanged save is byte-identical, and a picture needing more tiles than that is refused without touching the file. The palette colors are not editable. The Picture mode has its own undo/redo (300 steps, snapshots of the immutable row strings) wired into the top bar's Undo/Redo (`StepPictureHistory`, ordered against the input history by time) and Ctrl+Z / Ctrl+Y / Ctrl+Shift+Z, and shows `TileBudget` (tiles needed against free, which depends on the picture since tiles only the changed blocks used become free) after every stroke, so an over-budget picture is seen before saving and can always be undone or reverted; the history is cleared when the picture is saved. What the map header columns do in game was not checked (the area numbers' names come from them). `AreaMapCanvas` draws the picture with the cells and markers laid over it. Places can be added at the end of either list (the tables then grow; `Counts` reads the sizes from the archive) and the last one removed again; the map's save refuses to shrink below a place a species list still names (`FindListedBeyond`), and a list naming a new place can only be saved after the map is (so Save All and the Areas tab's save do the map first). Whether the game reads places past the original 21 and 50 was not checked, so a hack with new places needs testing in game. The places are edited in their own page, the Pokedex Map Editor (`MainWindow.PokedexMap.cs`, under Maps: paint a route's cells with the left button, erase with the right, or move a special place's marker); the Pokémon Editor's Areas tab shares the same loaded map (`EnsureAreaMapLoaded`) and clicking a place there ticks it for the species.

## Conventions

- Avoid comments that restate what code does; only comment non-obvious "why" (a tricky invariant, a
  workaround for a specific bug). This applies throughout, old and new code alike.
- Nintendo DS data is little-endian; use `EndianBinaryReader` for sections that aren't.
- When adding a brand-new editor/data type to the legacy app, follow the existing pattern: a
  `RomFile` subclass with `ToByteArray()`, a `Filesystem.cs` path helper, and either a `Form` or
  `IEditorWithUnsavedChanges` `UserControl`, registered from `DS_Map/Main Window.cs`.

## Music Editor (`DSPRE.Core/SdatArchive.cs`, `NitroSequencer.cs`, `MusicService.cs`, `MainWindow.Music.cs`)

Lists every sequence in the project's sound archive (SDAT) as Music, Jingle or Sound effect, plays it, edits its notes, and exports it. There is no whole-sequence replacer yet.

- `SdatArchive` reads the symbol and info tables (sequences, banks, wave archives) and the files they point to.
- `NitroSequencer` is a software version of the DS sound engine that renders an SSEQ with the bank's instruments (PCM, pulse, noise) to stereo. Notes, programs, tempo, loops, jumps and variables follow the game; envelopes, vibrato/modulation and mixing are approximations. Rendering stops at the first backward jump (the loop point) plus `LoopRepeats` more passes, or at `MaxSeconds`.
- `Options.BankEntrySize` (the panel's "Instrument entry size") is the byte distance between instrument entries when an SBNK is read. The format uses 4; 8 reproduces an earlier bug that picked the wrong instruments and gave a harsh chip-like sound, which the user likes as a mode. Larger sizes just pick other instruments.
- `AudioPlayer.Volume` (the panel's Volume slider, shared with the Pokédex cries) scales the samples when a sound starts, because the external players have no common volume switch. WAV exports are not scaled.
- Playback fidelity has only been checked with spectrograms, not by ear.
- The Instruments list under the controls shows the selected sequence's bank (`NitroSequencer.Instruments`: layout single/drum set/key split, per key range the sample kind, wave rate/format/loop, base note, ADSR, pan). "Play Note" renders one note with `NitroSequencer.RenderNote`, a one-note SSEQ built in memory.
- The Notes section is a read-only piano roll (`PianoRollView`) of the selected sequence: `NitroSequencer.Analyze` runs the sequence as `Render` does and logs each note (track, instrument, key after transpose, velocity, start tick, length; 48 ticks a beat, bars assumed 4 beats), tempo changes and the loop point, with a per-track list and a text list of the track's notes. Tracks the first track starts begin one tick late, as in `Render`.
- Export is a WAV or the game's own `.sseq`, `.sbnk` and `.swar` files.
- Note editing (`SequenceEditor`, `MusicService.EditNotes`, Notes panel's "Edit note"): `NoteEvent.Offset` is the note command's offset in the SSEQ data (a note replayed by a loop/called section shares it, so one edit changes every play), `Transpose` is what the track added to the stored key, `Waits` is the track's note-wait flag. Key and velocity are rewritten in place; a length keeps its byte width when it fits (padded with leading `0x80` groups) and otherwise the data is spliced and every 3-byte target of open-track/jump/call (`0x93/94/95`, found by `FindOffsetOperands`, which refuses sequences with commands it cannot size, e.g. the `0xA0/0xA1` random prefixes) beyond the splice is rewritten. Deleting turns a note on a note-waiting track into a rest of the same length, otherwise removes it. On a note-waiting track a length change moves the following notes (the length is also the wait; Platinum's music tracks turn note-wait off, so there it does not); `SequenceEditor.Relocate` moves a note in time (`NoteOp.Shift`) or places a new one (`Create`, relative to an existing note) by splitting/shortening the rests around it so every other note keeps its time: on a note-waiting track the note needs rests where it goes, and the edit is refused when the track loops/jumps/calls (`Stops`) or a jump target lies inside the stretch crossed. An instrument change (`NoteOp.Program`) wraps the note in `0x81 program` ... `0x81 old program` (`WrapProgram`). The UI re-reads the edited sequence and refuses an edit that would disturb any other note; moving to another note in the list applies the unapplied field changes first. Journaled as `SetSequenceNote` / `AddSequenceNote` (offset, key, velocity, length, waits, shift, program, currentProgram). `SdatArchive.WithFileResized` lets a sequence grow by moving the files after it (rewriting the FAT, block and archive sizes). Edits are staged in `pendingSequences` (the SSEQ bytes plus the ordered `NoteOp`s; offsets of each op are into the file as the ops before it left it), previewed by Play and the roll, listed by `CollectUnsavedMusicChanges`, written by Save All / "Save Sequence", and journaled as `SetSequenceNote` / `DeleteSequenceNote`.

## Lua editor support (`DSPRE.LuaLanguageServer/`, `DSPRE.Core/LuaEditorSupport.cs`)

`dspre-lua-ls` is a hand-written LSP server (JSON-RPC over stdio, no LSP library) for editing `expanded/scripts/*.lua` in Rider, VS Code or any LSP client; `README.md` there has the setup for each, and `vscode/` is the VS Code extension.

- Diagnostics are `LuaScriptCompiler.Compile` on the open buffer with the `global.lua` buffer (or file) as its global, debounced 250 ms, so they match Save Script; the compiler stops at the first error, so only one shows at a time. `global.lua` is checked by compiling an empty script with it as the global. `level.lua` and other files are only parsed.
- The Core part is editor-neutral: `LuaGlobalSymbols.Find` (global.lua helpers and `X = var[..]`/`flag[..]` names by parsing, falling back to line patterns when it does not parse), `LuaCommandDocs` (parameter labels, signature, hover text; the database writes "nan" for a missing description) and `LuaStubGenerator` (`---@meta` API file + `.luarc.json` for LuaLS/EmmyLua, written by `dspre-lua-ls stubs -p <project>`; the server itself never writes into a project).
- The server has its own `LuaTokens` tokenizer (keeps comments and columns, never throws) because the compiler's lexer is internal, keeps lines only and throws. `LuaFormatter` changes leading/trailing whitespace and blank-line runs only, never tokens; indent is one level per *line* that opens blocks, so `script(1, function()` indents once.
- The project is found per file by walking up to a folder `RomProject.TryOpen` accepts that has `expanded/scripts`; the language comes from `LuaScriptService.LoadLanguage` (project database first, else built-in `--databases` / `DSPRE_DATABASES` / next to the server / the bridge data folder).
- `LuaRefactor` does references, rename and semantic tokens on tokens (no syntax tree): a name declared `local` anywhere in a file is that file's own; every other use of a `global.lua` name is that name, in all `expanded/scripts/*.lua` (open buffers over disk). Commands/movements/built-ins find references under every alias but refuse rename. Rename edits files by text edits the client applies, so unopened files are changed by the client, not by the server.
- Tested by `DSPRE.Tests/LuaEditorSupportTests.cs` (the test project references the server project). Not tested: the Rider/LSP4IJ and VS Code client setups, which were written from their documented settings and not run in those IDEs.

## Hidden items (`DSPRE.Core/HiddenItemService.cs`, `HiddenItemUsageService.cs`, `DSPRE.Avalonia/MainWindow.HiddenItems.cs`)

The Item Table Editor's Hidden Items tab. The game looks a hidden item up in an ARM9 table by script ID when a type-2 spawnable runs script 8000 + ID: entries are 8 bytes, u16 item, u8 amount, u8 extra, 2 unused, u16 script ID; the list ends at the first item 0.

- Nothing is hard-coded to a ROM: `Locate` finds the table from the code that reads it, which holds the table's address twice in a literal pool. **Platinum** (`LiteralPool`): the entry count (257) is the u32 right after the address; it is never changed, so removed entries are filled with item 0 and script ID 0xFFFF (the code keeps scanning every slot, and ID 0 would shadow a real entry). Ids are 0-228 then 256-283; US table 0xEA378. **HGSS** (`ThumbImmediate`): the count is the immediate of Thumb `cmp rN, #231` (the same value is at four places near the two address literals); the last two of those are the length (written on save, as legacy did) and the capacity (read only); US HeartGold table 0xFA558, sites 0x405E4/0x405E8. Both were checked against the real US ROMs (tests); other regions/SoulSilver rely on the search and are untested, Diamond/Pearl's table was not found and is refused.
- The Platinum "extra" byte (2 for 231 entries, 1 for 15, 0 for 9; HGSS always 0) is kept and editable; what it does is unknown.
- The tab reads every saved event file (`HiddenItemUsageService`) to show where each entry is, which are unused, which IDs are duplicated and which spawnables have no entry. Unsaved Map Editor edits are not seen until saved and Reload is pressed.

## Map Editor views (`MainWindow.axaml`, `MainWindow.axaml.cs` "Map View layout")

Two tabs: **Matrix** and **Single Map**. The Matrix tab has a scope switch, "This header's maps" (the maps the header owns in its matrix; the shared overworld matrix 0 has hundreds, so outdoor headers open on this) and "Whole matrix" (every placed map, with the matrix picker); Edit Matrix sits beside the switch and turns on the whole-matrix scope, since editing needs every square. The two scopes are still separate viewers/bodies (`HeaderScopeBody`/`WholeScopeBody`, `OverworldMapViewer`/`IndoorMapViewer`, `currentOverworldPlacements`/`currentIndoorPlacements`); code asks `IsHeaderMapsView` / `IsWholeMatrixView` (tab + `matrixHeaderScope`), never the tab alone. Picking a header sets the scope only when the kind of place changes (outdoor <-> not), so a choice is kept from one outdoor header to the next; the Buildings/Overworlds/Warps checkboxes of the two scopes are kept equal. A header with no maps clears the header-scope viewer and says so in a placeholder.

The Events pane has a "Go to script" button beside each event's script number (`MainWindow.EventScriptGoTo.cs`): it opens the selected header's script file in the Script Editor (waiting out the unsaved-changes question, abandoning if refused) and selects the line where that script starts - `script(N, ...` in the Lua tab when the file has a `.lua`, `Script N:` in the Scripts tab otherwise - scrolling the editor's outer ScrollViewer to it, since the text boxes size to their text. A number that is not in the file (common scripts) just opens the file and says so in the Script Editor's status; a hidden item spawnable's 8000+ number says it is a hidden item entry instead.

The Script Editor draws, in a 64 px column between the line numbers and the text of the Scripts and Lua tabs (`ScriptsIconCanvas`/`LuaIconCanvas`, `MainWindow.ScriptTriggerIcons.cs`), the icons of the events that run each script, beside its starting line (`Script N:` / `script(N, ...`): an overworld's sprite (`ResolveOverworldIconKey`, so the exported sprite cache), a trigger's icon, or a sign's (spawnable of type misc/board; hidden items are skipped), at most three with "+N" after, tooltip listing each event, header and position. Events come from `GetSharedMapEvents` of the headers in `HeadersUsingScriptList`, so unsaved Map Editor edits show. Lines sit at `Padding.Top + line * (Bounds.Height - padding) / lineCount` (NoWrap text, equal line heights); redraws are coalesced by `QueueScriptIconRefresh` and fired by text/size changes, file loads and a finished sprite export.

