# NARC coverage (Platinum)

Which of the 215 NARCs in a Platinum project the Avalonia app (`DSPRE.Avalonia` + `DSPRE.Core`) reads or writes, and which it does not.

**60 covered, 155 not covered.**

How this was made: each NARC's name was searched for in the Core and Avalonia source, and doubtful hits were checked by hand. 
A NARC reached through an indirect path could be missing from the covered list. What each uncovered NARC contains is inferred 
from the Platinum data layout; not every one was opened.

## Covered

- **Maps:** `land_data`, `map_matrix`, `area_data`, `area_build`, `build_model`, `areabm_texset`, `map_tex_set`, `zone_event`, `fldtanime`
- **Scripts and text:** `scr_seq`, `msg` and `pl_msg`, `font` and `pl_font`
- **Pokémon:**
  - Stats and moves: `pl_personal`, `evo`, `wotbl`, `pl_waza_tbl`, `pms` (hatch result)
  - Experience curves: `pl_growtbl` (Growth Rate Editor)
  - Sprites and icons: `pl_pokegra`, `pl_otherpoke`, `pl_poke_icon`
  - Idle animation: `pl_poke_data` and `height`
  - Pokédex: `zukan_data`, `zukan_data_gira`, `pl_pokezukan`, `shinzukan`
- **Trainers:** `trdata`, `trpoke`, front and back sprites (`trfgra`, `trbgra`), battle messages (`trmsg/trtbl`, `trtblofs`, Trainer Message Editor)
- **Items:** `pl_item_data`, `item_icon`
- **Encounters:** `pl_enc_data`, `encdata_ex`
- **Battle Frontier:** `pl_btdpm` and `pl_btdtr` (Tower Pokémon sets and trainers), `pl_bsdpm` (Stage Pokémon sets), in the Battle Frontier Editor
- **Footprints:** `pokefoot` (Footprint tab of the Pokémon Editor)
- **Battle shadows:** `poke_shadow`, `poke_shadow_ofx`, `poke_yofs` (Shadow tab of the Pokémon Editor)
- **Pokédex areas:** `zukan_enc_platinum`: every species' lists (Areas tab of the Pokémon Editor) and the map's places (Pokedex Map Editor)
- **Other:** `fld_trade`, `mmodel` (overworld sprites), `pl_batt_bg` (the 23 scenic backdrops)

## Not covered

### Battle logic (biggest gap)

- `battle/skill/waza_seq`, `be_seq`, `sub_seq`: move effect scripts
- `battle/tr_ai/tr_ai_seq`: trainer AI scripts (the Trainer Editor only sets the AI flags)
- `wazaeffect/*`, `particledata/*`: move animations and particles (`waza_particle` is 2.4 MB)

### Pokémon data

- `poketool/personal/growtbl`: the Diamond/Pearl experience curves (not read by Platinum, as far as is known)
- `pokeanm`, `pl_pokeanm`, `pokeanime/*`: battle animation data
- `height_o`, `dp_height`, `dp_height_o`: Diamond/Pearl and other-form height tables
- `zukan_enc_diamond`, `zukan_enc_pearl`: the Diamond/Pearl Pokédex area lists (the Platinum one is covered)
- `resource/eng/zukan`: Pokédex UI graphics
- `itemtool/itemdata/nuts_data`: berries

### Battle Frontier and Tower

- `btdpm`, `btdtr`: the Diamond/Pearl Tower data (the Platinum `pl_` ones are covered)
- `frontier/script/fr_script`: Frontier scripts
- `frontier_bg`, `frontier_obj`, `particledata/pl_frontier`: Frontier graphics and particles

### Contests

- `contest_data`, `contest_bg`, `contest_obj`

### Field

- `data/arealight`
- `data/trapmark`
- `data/field_cutin`: Fly and HM cut-ins
- `graphic/field_encounteffect`
- `data/mmodel/fldeff`
- `move_model_list` (`area_move_model/` and `mm_list/` copies)
- `tornworld/*`: Distortion World
- `msgdata/scenario/scr_msg`

### Underground and Wi-Fi (low value)

`ug_*`, `ugeffect_obj_graphic`, `underg_radar`, `ugroundeffect`, `wifi_*`, `wflby_map`, `wifip2pmatch`, `tradelist`

### Minigames

`bucket`, `balance_ball_gra`, `guru2`, `nutmixer`, `ppark`, `custom_ball/*`, `manene`, `scratch`, `poketch`, `poru*` (Pokétch), `trainer_case`

### UI graphics (none editable)

- Menus: `bag_gra`, `pl_bag_gra`, `box`, `menu_gra`, `config_gra`, `shop_gra`, `namein`, `mail_gra`, `winframe`, `pl_winframe`
- Summary and party screens: `pst_gra`, `pl_pst_gra`, `plist_gra`, `pl_plist_gra`
- Battle UI: `batt_obj`, `pl_batt_obj`, `b_bag_gra`, `b_plist_gra`, `pl_b_plist_gra`, `vs_demo_gra`
- Location pop-up: `arc/area_win_gra`

### Cutscenes and demos

`demo/*` (intro, title, egg hatch, evolution), `graphic/ending`, `opening`, `dendou_*` (Hall of Fame), `ship_demo*`, `demo_tengan_gra`, `demo_climax`, `tv`, `email_gra`

## Partly covered

- **`pl_batt_bg`:** only the scenic backdrops are importable. The 4-bit sheets (battle menus, platforms) can be browsed but not imported.
- **`pl_pokegra`:** the sprites, but not shadows or y-offsets.
- **`graphic/tmap_gra`:** the two 512x512 Town Map pictures (the terrain map, screen 25, and the route diagram, screen 26) are editable in the Pokedex Map Editor; which one the game's Pokédex area page shows is not confirmed. The other Town Map screens, the sprites and the palette colors are not editable.
- **`weather_sys`, `debug/cb_edit/d_test`:** used only as spare carriers for patches (synthetic overlay, dynamic headers). Their own contents are not editable.

## Next picks

- Move effect scripts and trainer AI scripts: the largest impact, and the largest effort.
- Berries (`nuts_data`) and Frontier scripts (`fr_script`).
