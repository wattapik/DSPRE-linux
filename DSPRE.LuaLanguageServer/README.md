# dspre-lua-ls: editing DSPRE Lua scripts in an IDE

A language server (LSP over stdio) for a DSPRE project's Lua scripts, `expanded/scripts/NNNN.lua`, `global.lua` and
`level.lua`. It runs DSPRE's own Lua compiler (`DSPRE.Core`), so what it reports is what **Save Script** and
`DSPRE.Avalonia check` would report, and it completes from the project's own script command database, not a fixed list.

| Feature | Comes from |
|---|---|
| Errors and warnings as you type | `LuaScriptCompiler` on the open buffer, with `global.lua` (also the unsaved buffer) in front |
| Completion | commands and movements (by database name and snake_case alias), `global.lua` helpers and `var`/`flag` names, locals, built-ins, keyword snippets; inside `var[` / `flag[` the names global.lua gave numbers, inserted as the number |
| Hover | command signature, description and parameters from the database; a helper or name's definition; `var[0x4000]` shows its name |
| Signature help | the current argument of a command or global.lua helper (a result argument shows as optional) |
| Go to definition | locals and `function`s in the file; global.lua names, in global.lua |
| Find references | locals within the file; `global.lua` names and helpers across every script file (a file that declares its own local of that name is skipped); a command under both its database name and its alias |
| Rename | locals, and `global.lua` helpers and variable/flag names in all files at once. Commands, movements and built-ins are refused, as are invalid names and names already in use |
| Semantic highlighting | commands, movements, helpers, locals, parameters, `var`/`flag` names and constants such as `ITEM_POTION` get their own token types |
| Outline | `script(n)` entries and functions |
| Formatting | re-indents by block (`function`/`if`/`for`/`while`/`repeat`/`do`, brackets) and trims whitespace. Tokens are never changed, so a formatted script compiles the same |

The project is found by walking up from the file to the folder DSPRE opens as a project (`header.yaml`/`config.yaml`) that has
`expanded/scripts`. The project must have been opened in DSPRE once (that is what creates the scripts folder). Lua files
elsewhere are ignored.

## Build

```bash
dotnet publish DSPRE.LuaLanguageServer -c Release -o out/dspre-lua-ls
# run: out/dspre-lua-ls/dspre-lua-ls          (or: dotnet out/dspre-lua-ls/dspre-lua-ls.dll)
```

Put the output folder on your PATH, or give the full path in the editor settings below. Add `-r linux-x64 --self-contained` (or
`win-x64`, `osx-arm64`) for a build that needs no .NET install.

### Script command databases

Commands come from the project's own database if DSPRE made one (`edited_databases/<rom name>/scrcmd_database.json` in DSPRE's
data folder), otherwise from the built-in `*_scrcmd_database.json` files. The server looks for those in this order: `--databases
<folder>`, the `DSPRE_DATABASES` environment variable, a `databases` folder next to the server, DSPRE's data folder
(`%AppData%\DSPRE\databases`, inside the wine prefix off Windows). Without any database the server still checks syntax and completes
locals, but not commands, and tells you once.

## JetBrains Rider

Rider has no Lua support or LSP client of its own, so add two plugins:

1. **EmmyLua** (or any plugin that gives `*.lua` a file type), so Rider treats the files as Lua.
2. **LSP4IJ** (Red Hat's language server client for JetBrains IDEs).

Then *Settings | Languages & Frameworks | Language Servers | + | New Language Server*:

- Name: `DSPRE Lua`
- Command: `/full/path/to/dspre-lua-ls --stdio` (add `--databases <folder>` if needed)
- Mappings | File name patterns: `*.lua` (language id `lua`)

Open the project's `expanded/scripts` folder (or the whole project folder) as a Rider folder/solution. If EmmyLua's own
inspections report every command as an undefined global, turn those inspections off or run `dspre-lua-ls stubs -p <project>` (below),
which declares them.

## Visual Studio Code

`vscode/` holds a small extension that starts the server for Lua files inside `expanded/scripts`.

```bash
cd DSPRE.LuaLanguageServer/vscode
npm install
npx vsce package          # makes dspre-lua-0.1.0.vsix; then: code --install-extension dspre-lua-0.1.0.vsix
```

Settings: `dspreLua.server.command` (default `["dspre-lua-ls"]`, e.g. `["dotnet", "/path/dspre-lua-ls.dll"]`) and
`dspreLua.server.databases`. VS Code's built-in Lua grammar highlights the files. Do not run the "Lua" extension (sumneko) on the same
files: it does not know the DSPRE commands. It is fine for other Lua projects, since this extension only attaches to `expanded/scripts`.

## Other editors

Any LSP client works: server command `dspre-lua-ls --stdio`, filetype `lua`, root = the folder holding `expanded/`. Neovim:

```lua
vim.lsp.config['dspre_lua'] = {
  cmd = { 'dspre-lua-ls', '--stdio' },
  filetypes = { 'lua' },
  root_markers = { 'header.yaml', 'header.bin' },
}
vim.lsp.enable('dspre_lua')
```

## Without the server: API stubs for LuaLS / EmmyLua

```bash
dspre-lua-ls stubs -p <project folder>
```

writes `expanded/scripts/.dspre/api.lua` (every command and alias with parameter names and types, movements, `var`/`flag`, the
`global.lua` helpers and names) and `expanded/scripts/.luarc.json` pointing at it, so lua-language-server and EmmyLua give completion
and hover. They cannot tell whether a script compiles; the DSPRE server can. Re-run it after changing global.lua or the database.

## Saving

An external editor writes the `.lua` only; the `.script` is compiled from it when DSPRE saves the script file or when you run
`DSPRE.Avalonia build -p <project>` (or `check -p <project>` to only list errors). Edits made this way reach the edit journal when
`build` compiles them.
