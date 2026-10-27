> **Fork notice.** This is a modified fork of [DSPRE](https://github.com/DS-Pokemon-Rom-Editor/DSPRE) by the DS Pokémon ROM Editor Reloaded contributors (originally by Nomura), licensed under the GNU Affero General Public License v3.0 (see `LICENSE`). It adds a cross-platform Avalonia app (`DSPRE.Avalonia`, `DSPRE.Core`), Lua scripting with a language server, and many new editors; see `CLAUDE.md` and the changelogs. It is not the official release. The `Tools/` executables are the ones shipped by upstream; ROMs and other tools are not included.
>
> **Bundled third-party work:**
> - The **Map Creator** (`DSPRE.Core/MapCreator*.cs`, `DSPRE.Avalonia/MapCreator*.cs`) reads and writes the file formats of, and is a port of parts of, [Pokémon DS Map Studio](https://github.com/Trifindo/Pokemon-DS-Map-Studio) by Trifindo. See that project for its license and credits.
> - `DSPRE.Avalonia/Assets/TextPreview/pokemon-dp-pro.otf`: "Pokémon DP Pro" by crystalwalrein, CC BY-SA 3.0 (`license.txt` beside it).

it's all work in progress vibe code slop that works on my linux machine and probably nowhere else
i made it for myself so if it doesn't work for you i'm sorry and i don't want to engage in pull requests on here 
either and nobody contact me about this project either i don't care.
mostly tested on platinum only and idk the region either.
matrices and maps and headers can be decoupled (detrupled?) which is bad but i dont want to fix rn 

features
=====
- can run on native linux (only reason it exists)
- better links between data probably
- tighter integration between scripts and text and pokedex and text and others
- "decompile" scripts further to lua for easier writing, and track vars and flags in a global.lua file 
- view all the maps better. and easier map and matrix editing
- can edit more stuff and graphics and palettes and some animations maybe
- lots of other stuff that i already forgot
- map creator: place tiles (from Pokemon DS Map Studio tilesets, or cut from the project's own maps) on a 32x32 grid and build the model into a new or existing map
- save edits to a text-based journal that can be rerun later and is committed to a git repo.
