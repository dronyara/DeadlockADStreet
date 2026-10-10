# What to fix after a Deadlock patch

**English** · [Русский](PATCHING.ru.md)

The plugin depends on three things inside the game. After a big patch, check them in this order. The server log
(`game/citadel/console.log`, lines tagged `[AD]`) and `%TEMP%\abilitydraft.log` say which one broke.

## 1. Deadworks does not start
Symptom: the server window closes at once and `console.log` is not created. `deadworks.exe` prints
`Failed to find signatures … Deadlock build NNNN is not supported`.

Fix: download the latest release from https://github.com/Deadworks-net/deadworks/releases and unpack it into the
Deadlock folder over the old one. If you build the plugin from source, rebuild it: `dotnet build AbilityDraft -c Release`.

## 2. "signature not found" — the functions behind the stock draft screen
The plugin calls two functions in `server.dll`: Reroll (deal three new cards) and Advance (move to the next pick, or
end the draft). It finds them by byte signatures stored in
`game/bin/win64/managed/plugins/AbilityDraft.signatures.json`.

Fix:
```
pip install capstone        # once
python tools\find_sigs.py   # finds the functions and rewrites AbilityDraft.signatures.json
```
then restart the server. The log should say `native draft functions resolved: advance=… reroll=…`.
Until that works, the plugin still runs: it drafts with the text menu instead of the stock screen.

If the script prints `no code reference…` or `no call found…`, Valve has rewritten the command handler.
Finding the functions by hand (IDA or Ghidra):
1. Open `game/citadel/bin/win64/server.dll` and find the string `itemdraftreroll`.
2. Follow the reference to it: a long chain comparing client command names (`buyitem`, `sellitem`, `itemdraftreroll`, …).
3. The comparison with `itemdraftreroll` has a `je` to a branch. The last `call` in that branch before its `jmp`,
   with the hero in `rcx`, is Reroll. The same goes for `itemdraftskip` → Skip.
4. Advance is the last `call` inside Skip. Skip itself is switched off in release builds by a stub check, but the
   function it calls at its end works, and it closes the client's screen properly after the last pick.
5. Take the first 12–22 bytes of each function, replace relative addresses with `?`, and put them in the json.

On build 6745 (2 October 2026): skip = `server.dll+0x7f10a0`, reroll = `server.dll+0x7f12b0`,
advance = `server.dll+0x7f0670`.

## 3. Cards show garbage, or the server crashes during the draft — the draft structure layout
The plugin writes ability ids straight into the hero's networked draft state. Offsets on build 6745:

| What | Where |
|---|---|
| `CCitadelPlayerPawn.m_ItemDraftRoundState` | read from the game's schema automatically |
| card count / pointer to the card array | state `+8` / `+16` |
| draft id (`-1` when no draft is active) | state `+112` |
| rounds left / rounds total (the "pick N of M" header) | read from the schema automatically |
| size of one card (`ItemDraftOption_t`) | `248` bytes |
| item id inside a card | card `+96` |
| upgrade bits (bit 1 is the "enhanced" badge) | card `+100` |
| "already drafted" / "rare" | card `+240` / `+241` |

To check: with the server in Street Brawl, write `draftdump <slot> upgrade_healbane` into `%TEMP%\abilitydraft.cmd`.
It prints the field offsets from the schema and the raw bytes. The first card must hold the token of the item the
game has just dealt (the names are in `console.log`, lines `hero_x rolled … upgrade_…`). A token is MurmurHash2 of
the lower-case name with seed `0x31415926`.

If the card size or the token offset has moved, change the constants `OptionSize`, `OptionItemId`,
`OptionUpgradeBits`, `OptionRare` and `StateId` at the top of `AbilityDraft/NativeDraft.cs`.

## 4. Street Brawl or the switch to Standard behaves differently
The plugin relies on these server convars and commands:
- `citadel_gamemode_streetbrawl_enabled` — the mode follows it live;
- `citadel_street_brawl_reset` — starts a Street Brawl match on the running map (it does so whatever the convar says);
- `citadel_active_lane` — Street Brawl sets it to one lane and never resets it; the plugin puts it back to `0`.
- `CStreetBrawlController` inside the game rules (`CCitadelGameRules.m_tStreetBrawl`, found through the schema):
  `m_eStreetBrawlState` (`2` is the buy phase on build 6774) and `m_flNextStateTime`. While somebody is still
  drafting, the plugin pushes that time back every tick, which is what stops the timer. If the draft is cut off by
  the round again, check the state number with `brawl` in `%TEMP%bilitydraft.cmd` and fix `BrawlBuyPhase` in
  `AbilityDraft/Brawl.cs`.
- The spawn walls are the map's brushes `amber_spawn_block_brush` and `sapphire_spawn_block_brush`; the plugin
  switches them with the `Enable` / `Disable` inputs. `walls` in the bridge file lists them (`effects=0x20` is "off").
  If the lobby has no walls after a map update, the brushes were renamed: `named func_brush` lists all names.
- The mode convars are replicated to clients and stay with them after they leave, so the plugin sets them back as
  soon as a match ends.

Standard after the draft is a map reload, because Street Brawl removes the side lanes' Walkers and Barracks for good.

## 5. New heroes
The ability pool is generated from the game's files and compiled into the plugin, so a new hero does not appear on
its own. Heroes marked disabled or in development are left out, except those named near the top of
`tools/gen_pool.py`: `PLAYABLE_ANYWAY` for heroes the game still marks "in development" although they can be picked
(Baba on build 6766), and `UNRELEASED` for heroes whose abilities are wanted in the draft while the hero stays
unpickable.

1. Decompile `scripts/heroes.vdata_c` and `scripts/abilities.vdata_c` from `game/citadel/pak01_dir.vpk` with
   [Source 2 Viewer](https://valveresourceformat.github.io/) and save them as `data/heroes.vdata` and
   `data/abilities.vdata`.
2. `python tools\gen_pool.py data "<Deadlock folder>" AbilityDraft\AbilityPool.g.cs`
3. `dotnet build AbilityDraft -c Release`

## 6. Publishing a new release
```
dotnet build AbilityDraft -c Release
python tools\find_sigs.py
python tools\package.py v0.1.1
```
The zip lands in `dist/`. Its signature file matches the game build it was made on.

## 7. Upgrades or ability-targeted items stop working — the hero's ability table
The plugin writes the drafted kit into the server's table of the hero's own abilities
(`CitadelHeroData_t.m_mapBoundAbilities`), so the engine handles upgrades and ability-targeted items by itself.
The field is found through the schema; the layout inside it is hard-coded at the top of `AbilityDraft/HeroBinding.cs`
(build 6759): the map's node array at `+0x10`, the node count at `+0x1c`; a node is `0x28` bytes with the slot in
the low 16 bits of `+0x10`, a pointer to the ability's name at `+0x18` and the name's token at `+0x20`.

A node is only written when it reads back as "a name and that name's token". When the layout has moved the server
log says `hero table: no trusted entry for …`, nothing is written, and the plugin falls back to answering the
upgrade and item commands itself (`Training.cs`, `Imbue.cs`; the item part then needs the client addon).

To look at the memory, write `herodata Inferno 160` into `%TEMP%\abilitydraft.cmd` and follow the pointer at `+0x10`
with `peek <address> 640`: ability names show up next to their slots.

## 8. The client addon
`clientside/pak01_dir.vpk` carries a copy of the game's ability data and goes stale with every patch that touches
items or abilities. Rebuild it (`tools/build_cards.py`, see the README), copy the result over
`clientside/pak01_dir.vpk` and update the build number in `clientside/README.md`.

- The resource compiler comes from CSDK 12 and is older than the game; it is run with
  `-danger_mode_ignore_schema_mismatches`, as the CSDK's own launcher does.
- The Deadworks UI bridge is downloaded from Deadworks and checked against its published checksum. When Deadworks
  changes its bootstrap, delete `data/deadworks-bootstrap.vpk` and build again.
- `addon/panorama/scripts/abilitydraft_hud.js` relies on names from the game's own UI: the panels `hud_signature`,
  `CitadelAbilityIcon`, `CitadelHudAbilityUpgradePips`, `AbilityUnlock1-3`, `OptionsContainer`, and the style classes
  `isUnlocked`, `canAffordUpgrade`, `hasAbilityUpgrade`, `selected`, `dismiss`. If the TAB view or the pick animation
  stops working after a patch, decompile `panorama/layout/citadel_hud_ability_upgrade_pips.vxml_c`,
  `panorama/styles/citadel_hud_ability_upgrade_pips.vcss_c` and `panorama/styles/citadel_item_draft_panel.vcss_c`
  and compare.
