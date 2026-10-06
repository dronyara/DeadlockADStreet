"""Builds the optional client addon for Ability Draft from YOUR OWN Deadlock files.

    python tools/build_vpk.py ["<Deadlock dir>"] [--out dist/abilitydraft_dir.vpk]

What it changes on the client: the draft screen's title ("Item Draft" -> "Ability Draft").
It takes the game's localization file for each language listed in TITLES, replaces that one line and packs the
result into a VPK. No game file is stored in this repository; the VPK is made on the machine that owns the game.

Install on a client (optional, players without it just see the stock title):
  1. copy the VPK to  game/citadel/addons/pak01_dir.vpk  (create the folder);
  2. in game/citadel/gameinfo.gi make the end of SearchPaths read (one per line; without the Mod and Write lines
     the game will not start):  Game citadel/addons / Mod citadel / Write citadel / Game citadel / Mod core /
     Write core / Game core
A game update restores gameinfo.gi, so step 2 has to be repeated after patches.
"""
import os, re, sys

from vpkfile import build_vpk

out = "dist/abilitydraft_dir.vpk"
if "--out" in sys.argv:
    out = sys.argv.pop(sys.argv.index("--out") + 1)
args = [a for a in sys.argv[1:] if not a.startswith("--")]
game = args[0] if args else r"C:\Program Files (x86)\Steam\steamapps\common\Deadlock"

# The stock screen first shows ability cards and then, in Street Brawl, goes on to items - so the title names both.
TITLE_KEY = "Citadel_StreetBrawl_Draft_Title"
TITLES = {
    "english": "Ability Draft",
    "russian": "Выбор способностей",
}

loc_dir = os.path.join(game, "game", "citadel", "resource", "localization", "citadel_main")
files = {}      # path inside the VPK -> bytes
for lang, title in TITLES.items():
    src = os.path.join(loc_dir, f"citadel_main_{lang}.txt")
    if not os.path.exists(src):
        print(f"skip {lang}: {src} not found")
        continue
    raw = open(src, "rb").read()
    if raw[:2] in (b"\xff\xfe", b"\xfe\xff"):
        enc, text = "utf-16", raw.decode("utf-16")
    else:
        enc, text = "utf-8-sig", raw.decode("utf-8-sig")
    line = re.compile(r'^(\s*"' + re.escape(TITLE_KEY) + r'"\s+)"((?:[^"\\]|\\.)*)"', re.M)
    m = line.search(text)
    if not m:
        raise SystemExit(f"{lang}: key {TITLE_KEY} not found - the game changed, see PATCHING.md")
    patched = line.sub(lambda g: g.group(1) + '"' + title + '"', text, count=1)
    files[f"resource/localization/citadel_main/citadel_main_{lang}.txt"] = patched.encode(enc)
    print(f"{lang}: {m.group(2)!r} -> {title!r}")

if not files:
    raise SystemExit("nothing to pack")

os.makedirs(os.path.dirname(out) or ".", exist_ok=True)
vpk = build_vpk(files)
open(out, "wb").write(vpk)
print(f"written {out}: {len(vpk)} bytes, {len(files)} files")
