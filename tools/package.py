"""Builds the release zip: unpack it into the Deadlock folder, on top of Deadworks.

    python tools/package.py <version> ["<Deadlock dir>"]

Needs a Release build first (dotnet build AbilityDraft -c Release) and the signature file
(python tools/find_sigs.py). Output: dist/AbilityDraft-<version>.zip
"""
import os, sys, zipfile

version = sys.argv[1]
game = sys.argv[2] if len(sys.argv) > 2 else r"C:\Program Files (x86)\Steam\steamapps\common\Deadlock"
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
plugins = os.path.join(game, "game", "bin", "win64", "managed", "plugins")
build = None
steam_inf = os.path.join(game, "game", "citadel", "steam.inf")
if os.path.exists(steam_inf):
    for line in open(steam_inf, encoding="utf-8", errors="replace"):
        if line.startswith("ServerVersion="):
            build = line.split("=", 1)[1].strip()

START_BAT = r"""@echo off
rem Ability Draft server. Players join from the Deadlock console:  connect <your ip>:27067
rem Forward UDP/TCP 27067 on the router for players outside your network.
cd /d "%~dp0"
if not defined DOTNET_ROOT if exist "%LOCALAPPDATA%\Microsoft\dotnet\dotnet.exe" set "DOTNET_ROOT=%LOCALAPPDATA%\Microsoft\dotnet"
if not defined DOTNET_ROOT if not exist "%ProgramFiles%\dotnet\dotnet.exe" echo WARNING: .NET 10 was not found - the plugin will not load. Install it from https://dotnet.microsoft.com/download/dotnet/10.0
deadworks.exe -dedicated -console -condebug -insecure -allow_no_lobby_connect ^
  +hostport 27067 +sv_hibernate_when_empty 0 ^
  +net_limit_sv_message_process_time_ms_drop_burst 60000 +net_limit_sv_message_process_time_ms_drop_rate 60000 ^
  +map dl_midtown
pause
"""

INSTALL = f"""Ability Draft for Deadlock {version}
=====================================

A server-side Deadworks plugin: players draft four abilities from other heroes on the Street Brawl
draft screen, then play Standard or Street Brawl. Players need to install nothing; an optional client
addon (clientside/pak01_dir.vpk in the repository) adds mouse picks and a working TAB upgrade view.

Built and tested against Deadlock build {build or "?"} with Deadworks v0.5.4.

INSTALL (the server owner only)
1. Deadlock installed from Steam (or a separate copy through SteamCMD, app 1422450).
2. .NET 10 runtime or SDK:  https://dotnet.microsoft.com/download/dotnet/10.0
3. The latest Deadworks release, unpacked into the Deadlock folder:
   https://github.com/Deadworks-net/deadworks/releases
4. Unpack THIS zip into the same Deadlock folder (it adds files under game\\bin\\win64).
5. Run game\\bin\\win64\\start-abilitydraft.bat

PLAY
- Join your own server from the Deadlock console:  connect localhost:27067
- Friends need no port forwarding: on start the server prints "STEAM CONNECT: connect [A:1:...]" in its window
  (the lobby leader also sees it in chat, and /id shows it again). Friends paste that whole line into their
  console. The id changes every time the server starts. Joining by address works too:
  connect <server ip>:27067  (forward UDP/TCP 27067 on the router).
- Pick heroes; a hero somebody already has cannot be picked. The lobby leader (first player in) types /draft
  in chat - or /chaos, which gives everyone a random hero and four random abilities with no picks.
- Vote for the rules by typing 1 (Standard) or 2 (Street Brawl) in chat.
- The match starts and the draft screen opens with ability cards. Take a card by typing 1, 2 or 3 in chat
  (left, top, right). Four picks, the last one is the ultimate; three rerolls per pick with the Reroll button.
  With the client addon, type /click before the draft and click the cards instead.
  No two players get the same ability.
- Street Brawl: after the fourth ability the same screen goes on to the usual items; the kit starts unlocked.
  Standard: the map reloads once everyone is done, and every player gets their team, hero and kit back.
- Upgrades (ALT + ability key) and items that attach to one ability work as in a normal match.
- When the match ends, the lobby reopens after 20 seconds and everyone but the lobby leader is kicked.
  /newdraft (lobby leader) returns everyone to the lobby at any time.

SETTINGS
game\\bin\\win64\\managed\\plugins\\AbilityDraft.config.json is created on the first start and read at every map
start. The file explains itself; in short:
  "Blacklist": ["Hotel Guest", "ability_frank_revive"]   abilities that are never offered (internal, English or
                                                         Russian name from AbilityDraft.abilities.txt)
  "Language": "en"                 "en" or "ru" - the language of the plugin's messages
  "MaxPlayersStandard": 12         how many players the lobby takes
  "MaxPlayersStreetBrawl": 8       with more players than this, a vote for Street Brawl plays Standard
  "UniqueHeroes": true             no two players on the same hero
  "SecondsAfterMatch": 20          delay before the lobby reopens after a match; 0 switches it off

CLIENT ADDON (optional, for each player who wants it)
clientside/pak01_dir.vpk in the repository, with install notes next to it. It must match the game build and is
rebuilt after game patches that touch items or abilities.

AFTER A GAME PATCH
- Deadworks stops starting until its next release: update Deadworks.
- If the server log says "signature not found", the bundled AbilityDraft.signatures.json no longer matches the
  game. Regenerate it with tools/find_sigs.py from the repository (see PATCHING.md there). Until then the
  plugin falls back to a text menu instead of the draft screen.

Source, journal and patch notes: https://github.com/dronyara/DeadlockADStreet
Made with AI assistance (Claude Code + universal-modder). Use on your own servers only.
"""

files = {
    "game/bin/win64/managed/plugins/AbilityDraft.dll": os.path.join(plugins, "AbilityDraft.dll"),
    "game/bin/win64/managed/plugins/AbilityDraft.signatures.json": os.path.join(plugins, "AbilityDraft.signatures.json"),
}
for arc, src in files.items():
    if not os.path.exists(src):
        raise SystemExit(f"missing {src}")

os.makedirs(os.path.join(root, "dist"), exist_ok=True)
out = os.path.join(root, "dist", f"AbilityDraft-{version}.zip")
with zipfile.ZipFile(out, "w", zipfile.ZIP_DEFLATED) as z:
    for arc, src in files.items():
        z.write(src, arc)
    z.writestr("game/bin/win64/start-abilitydraft.bat", START_BAT.replace("\n", "\r\n"))
    z.writestr("AbilityDraft-README.txt", INSTALL.replace("\n", "\r\n"))
print(out, os.path.getsize(out), "bytes; game build", build)
for i in zipfile.ZipFile(out).infolist():
    print("  ", i.filename, i.file_size)
