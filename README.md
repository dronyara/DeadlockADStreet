# Ability Draft for Deadlock

**English** · [Русский](README.ru.md)

A server-side [Deadworks](https://github.com/Deadworks-net/deadworks) plugin. Every player drafts four abilities
from other heroes on the Street Brawl draft screen, then the lobby plays Standard or Street Brawl with those kits.

The game client is not modified. Everything runs on a server you host; players join with `connect` and install nothing.

![The stock draft screen showing ability cards](showcase/native-draft-1-of-4.png)

## How a game goes
1. The host starts the server. Players open the Deadlock console and type `connect <ip>:27067`, then pick heroes
   (the usual way, or `/hero haze` and `/team amber|sapphire` in chat).
2. The host (the first player who joined) types **`/draft`** in chat (or `dw_draft` in the console).
3. Rules vote: type **1** for Standard or **2** for Street Brawl in chat. 25 seconds; a tie goes to the host's vote,
   otherwise to Standard.
4. The match starts and the **stock Street Brawl draft screen opens with ability cards**. Four picks, the last one
   is the ultimate, with three rerolls per pick on the stock Reroll button.
   **Take a card by typing its number in chat: Enter → `1`, `2` or `3` → Enter (left, top, right). The message is not
   shown to anyone. Do not click an ability card with the mouse — the game crashes.**
5. Street Brawl: after the fourth ability the same screen goes on to the usual items, and the match continues as
   Street Brawl.
   Standard: once everyone has four abilities (or after 50 seconds — the rest is filled in at random) the map reloads
   into a normal match, and the plugin puts every player back on their team and hero with their drafted kit.
6. `/newdraft` (host) returns everyone to the lobby.

If the stock screen is unavailable — after a game patch the plugin may fail to find the game functions it needs, see
[PATCHING.md](PATCHING.md) — the draft falls back to a text menu in front of the crosshair before the match starts:
`1`-`3` picks a card, `R` + `1`-`3` replaces it. `/lang en` switches ability names to English (Russian is the default).

## Install (host only)
Windows only: Deadworks has no Linux build because Valve ships no Linux server for Deadlock.

1. Deadlock from Steam (or a separate copy through SteamCMD, app `1422450`).
2. [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0).
3. The latest [Deadworks release](https://github.com/Deadworks-net/deadworks/releases), unpacked into the Deadlock folder.
4. The zip from this repository's [Releases](../../releases), unpacked into the same Deadlock folder.
5. Run `game\bin\win64\start-abilitydraft.bat`.

The host joins with console (`` ` ``) → `connect localhost:27067`.

**Friends outside your network do not need port forwarding.** On start the plugin switches on joining through Steam
and prints the command for it in the server log (`STEAM CONNECT: connect [A:1:…]`); the host also gets it in chat on
joining, and `/id` shows it again. Friends paste that whole line, brackets included, into their console. The id
changes every time the server starts. Joining by address works too: forward UDP/TCP 27067 on the router and use
`connect <ip>:27067`. Start the server with `-ad_nosteamconnect` to keep Steam joining off.

Running the server and the game on one machine takes a lot of memory. It works on 8 GB, but a map load takes about a
minute, and so does the reload into Standard.

### Building from source
```
dotnet build AbilityDraft -c Release
pip install capstone
python tools\find_sigs.py
```
The build copies the plugin into `game/bin/win64/managed/plugins`; `find_sigs.py` writes the signature file next to
it. If Steam is not in `C:\Program Files (x86)\Steam`, add `-p:DeadlockDir="<Deadlock folder>"` to the build and pass
the folder to the script. Start the server with `tools\host.ps1` (or `tools\host.ps1 -Lan`).

## Settings
`game\bin\win64\managed\plugins\AbilityDraft.config.json` is created on the first start and read again at every
map start:
```json
{ "Blacklist": ["Hotel Guest", "ability_frank_revive"] }
```
`Blacklist` lists abilities that are never offered. Use the internal name or the English or Russian name; all of
them are in `AbilityDraft.abilities.txt` next to the config. Names the plugin does not recognise are reported in the
server log.

No two players get the same ability. A card another player takes while it is on your screen is replaced for free.

## After a game patch
Deadworks stops starting until its next release, and the plugin's own signatures may need regenerating.
[PATCHING.md](PATCHING.md) lists what to check, in order.

## Known limits
- **Clicking an ability card crashes the client.** The stock screen treats every card as an item; an ability has
  no item data, and the client reads past it. Picks go through chat instead.
- **The header still says "item draft".** That text is in the client's localization and cannot be changed by a server.
- **The TAB upgrade menu shows the hero's original abilities**, because it takes its layout from the hero's data on
  the client. In Street Brawl the drafted kit therefore starts fully unlocked.
- **Standard gives 50 seconds for all four picks**, because the draft borrows Street Brawl's first buy phase.
- Tested with one player and bots. A full lobby and a complete match have not been tested, and only a handful of the
  152 abilities have been cast on a foreign hero.

## What is inside
- `AbilityDraft/` — the plugin (C#): `DraftPlugin.cs` (phases, vote, the text-menu draft), `NativeDraft.cs` (the
  draft on the stock screen, the switch to Standard), `Native.cs` (game functions found by signature), `Match.cs`,
  `Lobby.cs`, `Ui.cs`, `Debug.cs` (log and a file-driven test bridge), `AbilityPool.g.cs` (ability names, generated).
- `tools/` — server start scripts, the ability pool generator, the signature finder, release packaging, a crash dump reader.
- `showcase/` — a clip and screenshots.

There are no game files in this repository. The mod was built with AI assistance (Claude Code + universal-modder).
Use it on your own servers only; it does nothing to Valve's official servers.
