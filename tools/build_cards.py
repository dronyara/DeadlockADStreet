"""Sources for the optional "clickable cards" client addon, made from YOUR OWN Deadlock files.

    python tools/build_cards.py <out dir> ["<Deadlock dir>"] [--csdk "<Reduced_CSDK_12 dir>"]

A click on a draft card makes the client build item data for it, which crashes for an ability. So every ability
in the pool gets a twin: a real item named ad_<ability> with the ability's icon and name. The plugin deals twins
to players who switched clicking on (/vpk), and answers the client's "buyitem ad_<ability>" with the ability.

Needs data/abilities.vdata (tools/dump-vdata.ps1). Writes, under <out dir>:
  content/scripts/abilities.vdata                      - the game's ability data plus the twins
  game/resource/localization/citadel_gc_mod_names/*    - item names with the twins added (plain text, packed as is)
With --csdk it also compiles the data with the CSDK resource compiler and packs <out dir>/abilitydraft_cards_dir.vpk.
Nothing here goes into the repository: the output holds Valve's data and stays on the machine that owns the game.

Install on a client: copy the VPK to game/citadel/addons/pak01_dir.vpk (or the next free pakNN_dir.vpk) and, in
game/citadel/gameinfo.gi, make the end of SearchPaths read
    Game citadel/addons / Mod citadel / Write citadel / Game citadel / Mod core / Write core / Game core
(one per line; without the Mod and Write lines the game looks for its configs in the addon folder and will not
start). Rebuild it after every game
patch that touches items or abilities: the VPK carries a full copy of the ability data and goes stale.
"""
import io, os, re, shutil, subprocess, sys

from vpkfile import build_resource, build_vpk, read_dir, read_file, resource_blocks

csdk = None
if "--csdk" in sys.argv:
    csdk = sys.argv.pop(sys.argv.index("--csdk") + 1)
args = [a for a in sys.argv[1:] if not a.startswith("--")]
out = args[0]
game = args[1] if len(args) > 1 else r"C:\Program Files (x86)\Steam\steamapps\common\Deadlock"
root = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
PREFIX = "ad_"
BASE = "armor_upgrade_t1"       # a stock item template; the twin inherits everything an item needs from it

rows = re.findall(r'new\("([^"]*)", "([^"]*)", "([^"]*)", (\d), "([^"]*)", "((?:[^"\\]|\\.)*)", "((?:[^"\\]|\\.)*)", (true|false), "([^"]*)"\)',
                  open(os.path.join(root, "AbilityDraft", "AbilityPool.g.cs"), encoding="utf-8").read())
if not rows:
    raise SystemExit("AbilityPool.g.cs has no abilities - run tools/dump-vdata.ps1 first")

src = open(os.path.join(root, "data", "abilities.vdata"), encoding="utf-8-sig").read().replace("\r\n", "\n")
if f"\n\t{BASE} = \n" not in src:
    raise SystemExit(f"{BASE} is gone from abilities.vdata - pick another item template (see PATCHING.md)")
# The decompiled file already holds everything its includes brought in; compiling them again would duplicate it.
src = re.sub(r"\n\t_include = \n\t\[\n(?:\t\t[^\n]*\n)*\t\]", "", src, count=1)
end = src.rstrip().rfind("}")
# Found by trying variants in game: an enabled twin is clickable; one inheriting a disabled Street Brawl legendary
# was not, and a plainly disabled one was never clicked, so twins stay enabled. The slot type decides the
# colours: Ability gives the plain dark card a bare ability gets and a tooltip with a background; Armor is green,
# Tech purple, and Invalid is dark too but leaves the tooltip without a background.
ability_blocks = {m.group(1): m.group(0) for m in re.finditer(r"\n\t([A-Za-z_0-9]+) = \n\t\{\n.*?\n\t\}", src, re.S)}


def tooltip(ability):
    """The ability's hover text, said the way an item says it: its numbers, and its description sections."""
    block = ability_blocks.get(ability, "")
    props = re.search(r"\n\t\tm_mapAbilityProperties = \n\t\t\{\n.*?\n\t\t\}", block, re.S)
    details = re.search(r"\n\t\tm_AbilityTooltipDetails = \n\t\t\{\n(.*?)\n\t\t\}", block, re.S)
    if not props or not details:
        return []
    attributes = []
    for section in re.findall(r"\n\t{4}\{\n(.*?)\n\t{4}\}", "\n" + details.group(1), re.S):
        loc = re.search(r'\n\t{5}m_strLocString = "([^"]*)"', "\n" + section)
        basic = re.search(r"\n\t{5}m_vecBasicProperties = \n\t{5}\[\n(.*?)\n\t{5}\]", "\n" + section, re.S)
        important = re.findall(r'm_strImportantProperty = "([^"]*)"', section)
        attributes.append(
            "{\n" + (f'm_strLocString = "{loc.group(1)}"\n' if loc else "")
            + "m_vecImportantAbilityProperties = \n[\n" + "".join(f'{{\nm_strImportantProperty = "{p}"\n}},\n' for p in important) + "]\n"
            + "m_vecAbilityProperties = \n[\n" + "".join(f'"{p}",\n' for p in (re.findall(r'"([^"]+)"', basic.group(1)) if basic else [])) + "]\n},\n")
    # The values the description's {s:...} placeholders are filled from live in the property map.
    return [props.group(0).strip("\n").strip("\t"),
            'm_vecTooltipSectionInfo = \n[\n{\nm_eAbilitySectionType = "EArea_Active"\nm_vecSectionAttributes = \n[\n' + "".join(attributes) + "]\n},\n]"]


twins = []
described = 0
for hero, hero_en, hero_ru, slot, name, en, ru, ult, icon in rows:
    fields = ['_class = "citadel_item"', "_multibase = ", "[", f'\t"{BASE}",', "]", "m_bDisabled = false",
              'm_eItemSlotType = "EItemSlotType_Ability"',
              f'm_strAbilityImage = panorama:"{icon}"', f'm_strShopIconLarge = panorama:"{icon}"', *tooltip(name)]
    described += len(fields) > 9
    twins.append(f"\t{PREFIX}{name} = \n\t{{\n" + "".join(f"\t\t{f}\n" for f in fields) + "\t}\n")
print(f"{described} of {len(twins)} twins carry the ability's tooltip")
vdata = os.path.join(out, "content", "scripts", "abilities.vdata")
os.makedirs(os.path.dirname(vdata), exist_ok=True)
with io.open(vdata, "w", encoding="utf-8", newline="\n") as f:
    f.write(src[:end] + "".join(twins) + "}\n")
print(f"{vdata}: {len(twins)} twins")

loc_dir = os.path.join(game, "game", "citadel", "resource", "localization", "citadel_gc_mod_names")
packed = {}     # path inside the VPK -> bytes
for lang, col in (("english", 5), ("russian", 6)):
    path = os.path.join(loc_dir, f"citadel_gc_mod_names_{lang}.txt")
    if not os.path.exists(path):
        print(f"skip {lang}: {path} not found")
        continue
    raw = open(path, "rb").read()
    enc = "utf-16" if raw[:2] in (b"\xff\xfe", b"\xfe\xff") else "utf-8-sig" if raw[:3] == b"\xef\xbb\xbf" else "utf-8"
    text = raw.decode(enc)
    cut = text.rfind("}", 0, text.rstrip().rfind("}"))      # the brace closing the token list
    nl = "\r\n" if "\r\n" in text else "\n"
    # AbilityPool.g.cs holds C# string literals, and the localization format escapes the same two characters.
    lines = "".join(f'\t\t"{PREFIX}{r[4]}"\t"{r[col]}"{nl}' for r in rows)
    dst = os.path.join(out, "game", "resource", "localization", "citadel_gc_mod_names", os.path.basename(path))
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    packed["resource/localization/citadel_gc_mod_names/" + os.path.basename(path)] = (text[:cut] + lines + text[cut:]).encode(enc)
    open(dst, "wb").write(packed["resource/localization/citadel_gc_mod_names/" + os.path.basename(path)])
    print(f"{dst}: {len(rows)} names ({enc})")

if not csdk:
    raise SystemExit("sources written; pass --csdk <Reduced_CSDK_12 dir> to compile and pack them")

# The compiler works on an addon inside its own tree: content/citadel_addons/<name> -> game/citadel_addons/<name>.
ADDON = "abilitydraft"
content = os.path.join(csdk, "content", "citadel_addons", ADDON, "scripts")
compiled = os.path.join(csdk, "game", "citadel_addons", ADDON, "scripts", "abilities.vdata_c")
os.makedirs(content, exist_ok=True)
os.makedirs(os.path.dirname(compiled), exist_ok=True)
shutil.copyfile(vdata, os.path.join(content, "abilities.vdata"))
if os.path.exists(compiled):
    os.remove(compiled)
bin_dir = os.path.join(csdk, "game", "bin", "win64")
# The CSDK's binaries are older than the game, hence the schema switch its own launcher uses as well.
run = subprocess.run([os.path.join(bin_dir, "resourcecompiler.exe"), "-danger_mode_ignore_schema_mismatches",
                      "-game", os.path.join(csdk, "game", "citadel"), "-i", os.path.join(content, "abilities.vdata")],
                     cwd=bin_dir, capture_output=True, text=True, errors="replace")
if not os.path.exists(compiled):
    raise SystemExit("resource compiler failed:\n" + "\n".join(l for l in run.stdout.splitlines() if l.strip())[-3000:])

# The reduced CSDK has no game files to resolve references against, so its output lacks the list of resources the
# data refers to (particles, models, sounds). The twins add no new ones: the game's own list is carried over.
versions, blocks = resource_blocks(open(compiled, "rb").read())
pak = os.path.join(game, "game", "citadel", "pak01_dir.vpk")
stock = dict(resource_blocks(read_file(pak, read_dir(pak)["scripts/abilities.vdata_c"]))[1])
if "RERL" not in dict(blocks) and "RERL" in stock:
    blocks.insert(0, ("RERL", stock["RERL"]))
packed["scripts/abilities.vdata_c"] = build_resource(versions, blocks)

# The click in the TAB upgrade view: the addon's own layouts, script and style (addon/panorama)...
pano_content = os.path.join(csdk, "content", "citadel_addons", ADDON, "panorama")
pano_game = os.path.join(csdk, "game", "citadel_addons", ADDON, "panorama")
for d in (pano_content, pano_game):
    shutil.rmtree(d, ignore_errors=True)
shutil.copytree(os.path.join(root, "addon", "panorama"), pano_content)
run = subprocess.run([os.path.join(bin_dir, "resourcecompiler.exe"), "-danger_mode_ignore_schema_mismatches",
                      "-game", os.path.join(csdk, "game", "citadel"), "-r", "-i", os.path.join(pano_content, "layout", "*.xml")],
                     cwd=bin_dir, capture_output=True, text=True, errors="replace")
ui = {}
for folder, _, names in os.walk(pano_game):
    for n in names:
        full = os.path.join(folder, n)
        ui["panorama/" + os.path.relpath(full, pano_game).replace("\\", "/")] = open(full, "rb").read()
sources = sum(len(names) for _, _, names in os.walk(pano_content))
if len(ui) != sources:
    raise SystemExit(f"resource compiler built {len(ui)} of {sources} interface files:\n"
                     + "\n".join(l for l in run.stdout.splitlines() if l.strip())[-3000:])
packed.update(ui)

# ...and the Deadworks UI bridge that lets a server load them: Deadworks' own client bootstrap, taken unchanged
# from its service and checked against the checksum published next to it (scripts under GPL-3.0, see the
# client-bootstrap folder of github.com/Deadworks-net/deadworks). Its Play-menu page and server browser are
# left out: this addon does not change the menu.
BRIDGE_FILES = ("panorama/layout/hud_hideout.vxml_c", "panorama/scripts/dw_bootstrap.vjs_c", "panorama/scripts/dw_addon.vjs_c")
bridge_path = os.path.join(root, "data", "deadworks-bootstrap.vpk")
if not os.path.exists(bridge_path):
    import bz2, hashlib, json, urllib.request
    manifest = json.load(urllib.request.urlopen("https://api.deadworks.net/api/bootstrap", timeout=30))
    bridge = bz2.decompress(urllib.request.urlopen(manifest["download_url"], timeout=120).read())
    if hashlib.sha256(bridge).hexdigest() != manifest["sha256"]:
        raise SystemExit("the downloaded Deadworks bootstrap does not match its published checksum")
    open(bridge_path, "wb").write(bridge)
    print(f"Deadworks bootstrap v{manifest['version']} downloaded to {bridge_path}")
bridge_dir = read_dir(bridge_path)
missing = [f for f in BRIDGE_FILES if f not in bridge_dir]
if missing:
    raise SystemExit(f"the Deadworks bootstrap no longer has {missing} - see PATCHING.md")
for f in BRIDGE_FILES:
    packed[f] = read_file(bridge_path, bridge_dir[f])

vpk = os.path.join(out, "abilitydraft_cards_dir.vpk")
open(vpk, "wb").write(build_vpk(packed))
print(f"{vpk}: {os.path.getsize(vpk)} bytes, {len(packed)} files")
for name in sorted(packed):
    print(f"  {len(packed[name]):>8} {name}")
