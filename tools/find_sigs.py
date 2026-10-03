"""Finds the two server.dll functions Ability Draft calls and writes their byte signatures.

Run after a Deadlock patch if the server log says "signature not found":
    pip install capstone
    python tools/find_sigs.py ["<Deadlock dir>"]

How it finds them (so it can be redone by hand in any disassembler):
  The pawn's client-command dispatcher compares the command name against a chain of strings.
    "itemdraftskip"   -> ... -> mov rcx, <pawn> ; call SKIP      (advance to the next draft pick)
    "itemdraftreroll" -> ... -> mov rcx, <pawn> ; call REROLL    (deal three new options)
  In both branches the wanted call is the LAST call before the branch jumps back to the common exit.
The signature is the function's first bytes with every relative address replaced by "?".
Output: AbilityDraft.signatures.json next to the plugin DLL (the plugin reads it on load).
"""
import bisect, json, os, re, struct, sys
from capstone import Cs, CS_ARCH_X86, CS_MODE_64
from capstone.x86 import X86_OP_IMM, X86_OP_MEM, X86_REG_RIP

game = sys.argv[1] if len(sys.argv) > 1 else r"C:\Program Files (x86)\Steam\steamapps\common\Deadlock"
d = open(os.path.join(game, "game", "citadel", "bin", "win64", "server.dll"), "rb").read()
pe = struct.unpack_from("<I", d, 0x3C)[0]
nsec, optsz = struct.unpack_from("<H", d, pe + 6)[0], struct.unpack_from("<H", d, pe + 20)[0]
secs = []
for i in range(nsec):
    o = pe + 24 + optsz + i * 40
    vs, va, rs, ro = struct.unpack_from("<IIII", d, o + 8)
    secs.append((d[o:o + 8].rstrip(b"\0").decode(), va, vs, ro, rs))
text = next(s for s in secs if s[0] == ".text")


def off2rva(off):
    return next(off - ro + va for _, va, vs, ro, rs in secs if ro <= off < ro + rs)


def rva2off(rva):
    return next(rva - va + ro for _, va, vs, ro, rs in secs if va <= rva < va + max(vs, rs))


md = Cs(CS_ARCH_X86, CS_MODE_64)
md.detail = True


def string_rva(s):
    return off2rva(d.index(s.encode() + b"\0"))


def lea_xref(target):
    t0, tsz = text[3], text[4]
    for m in re.finditer(rb"[\x48\x4c]\x8d[\x05\x0d\x15\x1d\x25\x2d\x35\x3d]", d[t0:t0 + tsz]):
        o = t0 + m.start()
        if off2rva(o) + 7 + struct.unpack_from("<i", d, o + 3)[0] == target:
            return off2rva(o)
    raise SystemExit(f"no code reference to string at {target:#x}")


def handler_call(command):
    """RVA of the function called by the dispatcher branch for this client command."""
    at = lea_xref(string_rva(command))
    ins = list(md.disasm(d[rva2off(at):rva2off(at) + 32], at))
    branch = next(int(i.op_str, 16) for i in ins if i.mnemonic == "je")          # lea ; cmp ; je <branch>
    last = None
    for i in md.disasm(d[rva2off(branch):rva2off(branch) + 96], branch):
        if i.mnemonic == "call":
            last = int(i.op_str, 16)
        if i.mnemonic == "jmp":
            break
    if last is None:
        raise SystemExit(f"no call found in the '{command}' branch")
    return last


def signature(rva, max_len=64):
    """Shortest unique prefix of the function, with relative addresses wildcarded."""
    pat = []
    code = d[text[3]:text[3] + text[4]]
    for i in md.disasm(d[rva2off(rva):rva2off(rva) + max_len + 16], rva):
        b = list(i.bytes)
        rel = any(o.type == X86_OP_MEM and o.mem.base == X86_REG_RIP for o in i.operands) or \
            (i.mnemonic in ("call", "jmp") or i.mnemonic.startswith("j")) and any(o.type == X86_OP_IMM for o in i.operands)
        if rel and len(b) >= 5:
            b[-4:] = [None] * 4
        pat += b
        if len(pat) >= 12:
            rx = re.compile(b"".join(b"." if x is None else re.escape(bytes([x])) for x in pat), re.S)
            if len(rx.findall(code)) == 1:
                return " ".join("?" if x is None else f"{x:02X}" for x in pat)
        if len(pat) >= max_len:
            break
    raise SystemExit(f"no unique signature within {max_len} bytes at {rva:#x}")


def last_call_in(rva, limit=0x400):
    """Target of the last direct call in the function starting at rva (it ends at the first int3 padding)."""
    last = None
    for i in md.disasm(d[rva2off(rva):rva2off(rva) + limit], rva):
        if i.mnemonic == "int3":
            break
        if i.mnemonic == "call" and i.operands[0].type == X86_OP_IMM:
            last = int(i.op_str, 16)
    if last is None:
        raise SystemExit(f"no call found in the function at {rva:#x}")
    return last


sigs = {}
rvas = {key: handler_call(command) for key, command in (("ItemDraftSkip", "itemdraftskip"), ("ItemDraftReroll", "itemdraftreroll"))}
# Skip is switched off in release builds, but it ends by calling the function that really moves a hero on to the
# next draft pick - or ends the draft, telling the client to close the screen - and that one has no such gate.
rvas["ItemDraftAdvance"] = last_call_in(rvas["ItemDraftSkip"])
for key, rva in rvas.items():
    sigs[key] = signature(rva)
    print(f"{key}: server.dll+{rva:#x}\n    {sigs[key]}")

out = os.path.join(game, "game", "bin", "win64", "managed", "plugins", "AbilityDraft.signatures.json")
with open(out, "w", encoding="utf-8") as f:
    json.dump(sigs, f, indent=2)
print("written:", out)
