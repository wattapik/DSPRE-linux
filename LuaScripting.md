# Writing scripts in Lua

The Script Editor's **Lua** tab lets you write a script file as Lua instead of the plaintext script
format. **Convert to Lua** turns an existing script file into Lua and checks that it behaves exactly the
same before showing it. From then on `expanded/scripts/NNNN.lua` is the file you edit: **Save Script**
compiles it into `NNNN.script`, which is still what the ROM is built from. **Stop Using Lua** deletes the
`.lua` and goes back to editing the `.script` directly.

Lua runs while compiling, not in the game. Anything about game state (commands, variables, flags) becomes
script code. Everything else (locals, arithmetic, loops, helper functions) is worked out at compile time.

## Scripts and functions

```lua
script(1, function()       -- Script 1: the number events and triggers use
	LockAll()
	ReleaseAll()
end)                        -- reaching the end is End

script(2, 1)                -- Script 2 runs the same code as Script 1 (UseScript)

function goodbye()          -- a script Function, shared by several scripts
	Message(9)
	WaitButton()
	CloseMessage()
	ReleaseAll()
end                         -- reaching the end is Return

script(3, function()
	goodbye()               -- Call
	return goodbye()        -- Jump: goodbye takes over and does not come back
end)
```

A script can be given a name, between its number and its code:

```lua
script(1, "Rival intro", function()
	LockAll()
	ReleaseAll()
end)

script(2, "Rival intro again", 1)
```

The name changes nothing in the script; the Event Editor (the Linked Script heading and status) and the
Research script search show it beside the number. Converting a plaintext script gives no names.

Scripts must be numbered 1, 2, 3... without gaps. A global `function name()` is a script Function and
takes no parameters. In a script, `return` ends the script (End). In a Function, `return` returns to the
caller (Return). `End()` ends the script from anywhere.

## Commands

Every command in the script command database can be called by its database name or by a snake_case
alias of it: `LockAll()` or `lock_all()`, `CompareVarValue(...)` or `compare_var_value(...)`. Undocumented
commands keep their database names (`CMD_47(1)`). The argument count is checked against the database.

Arguments can be:

| Lua | Script |
|---|---|
| `30`, `0x800C` | numbers |
| `Overworld[3]` (or `ow[3]`), `Player`, `Camera`, `Following` | overworlds |
| `Up`, `Down`, `Left`, `Right` | directions |
| `Move[15]` | overworld movement types |
| `SEQ_SE_CONFIRM`, `SPECIES_PIKACHU`, `ITEM_POTION`, `MOVE_TACKLE`, `TRAINER_..._12` | named constants, as in the script |
| `EQUAL`, `DIFFERENT`, `LESS`, `GREATER`, `LESS_EQUAL`, `GREATER_EQUAL` | comparison operators |
| `sym("ANYTHING")` | any other token, passed through unchanged |

### Messages

Messages are written as their text. There are no message numbers to manage:

```lua
Message("Hello!\rHow are you?")
GenderMessage("Hi, sir!", "Hi, ma'am!")
SetIconBoard("Route 201", 2, 0, RESULT)
Message({"Hello?", "Is anyone there?", "Oh, hi!"})     -- several messages in turn
```

The text reads exactly as the archive stores it, escape codes such as `\r`, `\n` and
`{STRVAR_1, 3, 0, 0}` included. Converting a script file writes every message this way.

**Saving rebuilds the script's text archive.** Each distinct text gets a number, and the same text used
twice shares one entry. When only this script file uses the archive, the archive becomes exactly the
script's texts, in the order they first appear, and texts nothing shows any more are dropped. Otherwise
renumbering would break the others, so existing messages stay where they are, new texts reuse entries
nothing uses any more or go on the end, and the status line says why. This applies when the archive is
shared with another script file, when this script file is used with more than one archive, or when a
message is picked by a variable (`MessageFlex(0x8004)`).

A list of messages shows each in turn, with a `WaitButton` between them, so the player presses a button
to move on. After the last one the script carries on as usual: npc_message's own `WaitButton` and
`CloseMessage` follow it.

A number still works where you need one: `Message(4)` shows message 4 as it is, and `Message(4, "text")`
also sets its text. Menu options (`AddMultiOption`, `CreateMultiTouchBox`) come from another archive, so
they always take numbers.

## Movements

```lua
Movement(Player, { FaceNorth, WalkSouth8(2), EmoteExclamation })   -- WalkSouth8(2): twice
local walk_in = { walk_north8(3), face_south }                        -- aliases work too
Movement(Overworld[2], walk_in)
WaitMovement()
```

A movement without a count runs once. `step(0x41, 2)` is a movement by number.

## Variables, flags and conditions

```lua
local X, Y = var[0x8004], var[0x8005]   -- names for variables
GetPlayerPosition(X, Y)

var[0x4000] = 5            -- SetVar
var[0x4000] = var[0x4001]  -- SetVarFromVariable
X = X + 1                  -- IncrementVar (and X - 1 is DecrementVar)
flag[372] = true           -- SetFlag   (false: ClearFlag)

if flag[144] then ... end                              -- CheckFlag
if not flag[234] and var[0x4095] >= 1 then ... end     -- CompareVarValue, combined with and/or/not
elseif X == var[0x4002] then ...                       -- CompareVars

while var[0x4000] < 3 do ... end
repeat ... until flag[10]
```

A condition must compare a variable (`var[x] == 1`, `var[x] ~= var[y]`) or test a flag (`flag[x]`,
`not flag[x]`). For any other command that sets the condition, call it and then test what it left with
`condition(EQUAL)`:

```lua
CheckTrainerFlag(5)
if condition(EQUAL) then ... end
```

## Commands that give a result

Many commands store a result in a variable given as their last argument (`CheckItem`, `YesNoBox`,
`CheckPlayerGender`, `CheckPartyPokemonForm`...). Leave that argument out and use the command as a value:

```lua
RESULT = CheckPartyPokemonForm(0x4000)       -- CheckPartyPokemonForm 0x4000 0x800C
STARTER = CheckPlayerGender()                 -- the result goes straight into STARTER's variable

if CheckPartyPokemonForm(0x4000) == 1 then    -- result not kept
	...
end
if CheckItem(ITEM_POTION, 1) then ... end     -- a bare result means "not 0"
if not CheckItem(ITEM_POTION, 1) and YesNoBox() == 0 then ... end
while YesNoBox() == 1 do ... end              -- asks again on every pass
```

Where the result is not assigned to anything, it goes in VAR_RESULT, `0x800C`: the same variable the games
use for this, and the one the default global.lua calls `RESULT`. In a condition, each command runs right
before its result is tested, so `and`/`or` skip the commands they skip, as Lua would. Converting writes
these forms whenever the original script used them that way. Which commands count is read from the command
database's descriptions ("Stores 1 in Variable if...").

## Compile-time Lua

`local` values, arithmetic, numeric `for` loops (unrolled), `ipairs`/`pairs` loops over tables, `math`, and
`local function` helpers are worked out while compiling. A helper is copied into each place it is called,
so it can take parameters:

```lua
local function talk(n)
	Message(n)
	wait_button()
	close_message()
	release_all()
	End()
end

script(5, function()
	if flag[144] then talk(10) end
	talk(8)
end)
```

An `if` whose condition is known while compiling (such as `if i == 0 then` inside a helper) just picks its
branch and produces no script code. Identical Functions and Actions are merged in the compiled script.

## global.lua

`expanded/scripts/global.lua` holds helpers every Lua script file in the project can use. Edit it with the
**global.lua** button on the Lua tab. Saving it recompiles every Lua script file, because their `.script`
files depend on it. In global.lua, every `function name(...)` is a helper that is copied into each place it
is called. A Lua script file's own global functions are script Functions instead.

### Naming variables and flags

Give a variable or flag a name in global.lua, and every Lua script file can use the name in its place:

```lua
-- global.lua
STARTER = var[0x4000]
MET_RIVAL = flag[372]
RESULT = var[0x800C]     -- in the default global.lua: where YesNoBox, WaitTime... leave their result
```

```lua
-- any script file
if not MET_RIVAL and STARTER >= 1 then
	MET_RIVAL = true           -- SetFlag 372
	STARTER = STARTER + 1      -- IncrementVar 0x4000 1
end
YesNoBox(RESULT)
if RESULT == 0 then ... end
```

Converting a script file writes the names instead of the numbers: in conditions, assignments, and command
arguments that take a variable or a flag. For a file converted before a name existed, **Use Names** on the
Lua tab rewrites its `var[0x4000]` and `flag[372]` as their names.

### Naming script files

A table called `SCRIPT_NAMES` in global.lua gives script files readable names:

```lua
SCRIPT_NAMES = {
	[12] = "Rival intro",
	[40] = "Gym 1 leader",
}
```

The name appears beside the number in the Script Editor's file list and in the script file messages of the
Event Editor preview and Research. Saving global.lua updates them.

### Templates

A helper that returns a script body is a **template**:

```lua
function npc_message(text)
	return function()
		PlayFanfare(SEQ_SE_CONFIRM)
		LockAll()
		FacePlayer()
		Message(text)
		WaitButton()
		CloseMessage()
		ReleaseAll()
	end
end
```

```lua
script(18, npc_message("Hello?"))
script(19, npc_message({"Nice weather today.", "Don't you think?"}))
```

**Convert to Lua** recognises templates. Any script that a template in global.lua would produce is written
as a call to it, like the lines above. The first conversion in a project creates global.lua with
`npc_message` (someone who faces the player and says one message), `object_message` (the same without
facing the player) and `sign(text, kind)` (a DPPt sign board). Between them these cover about half of all scripts in
DPPt and HGSS. Add your own templates the same way, before converting the files that should use them.

### Gender messages

The default global.lua also has helpers for text that depends on the player's gender:

```lua
gender_message("Hi, sir!", "Hi, ma'am!")                  -- inside any script
script(5, npc_gender_message("Good day, sir.", "Good day, miss."))
```

On HGSS `gender_message` is the `GenderMessage` command. DP and Platinum have no such command, so there
it checks the player's gender into `var[0x800C]` and shows one `Message` or the other. The helper uses
`has_command("GenderMessage")`, which is true when the ROM's command database has that command. You can
use the same test in your own helpers. A project whose global.lua was created before these helpers
existed, or whose helpers still take message numbers, can copy them from here.

```lua
function gender_message(male_text, female_text)
	if has_command("GenderMessage") then
		GenderMessage(male_text, female_text)
	else
		CheckPlayerGender(var[0x800C])
		if var[0x800C] == 0 then
			Message(male_text)
		else
			Message(female_text)
		end
	end
end
```

## Dropping down to raw script

Lines the Lua cannot express go in an `asm` block, copied into the script as written. Inside it, `{name}`
refers to a Lua value: a script Function, a movement list, or a variable.

```lua
asm [[
	SomeOddCommand 0x800C 3
	Movement Player {walk_in}
	Jump {goodbye}
]]
```

`cmd("Name", args...)` calls a command whose database name is not a valid Lua name.

## level.lua

`expanded/scripts/level.lua` names level scripts, in a table called `LEVEL_SCRIPT_NAMES`:

```lua
LEVEL_SCRIPT_NAMES = {
	[3] = "Twinleaf Town",
}
```

The names show beside the number in the Level Script Editor, the Map Editor's Level Script pane and the
header's level script field. The Level Script Editor has a name box that writes this file for you.

## Editing in an IDE

The `.lua` files can be edited outside DSPRE, in Rider or VS Code, with error checking, completion of commands and of
global.lua's names, hover, signature help, go to definition and formatting: see `DSPRE.LuaLanguageServer/README.md`.
Convert every script file at once with `DSPRE.Avalonia decompile -p <project>` (`--only 1,5-9`, `--force` to replace existing `.lua`, `--dry-run` to only report). Each conversion is checked to behave the same, and the `.script` files are left as they are.
Compile and build from the command line with `DSPRE.Avalonia check -p <project>` / `build -p <project>`.
