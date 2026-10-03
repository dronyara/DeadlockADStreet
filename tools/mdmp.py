"""Minimal minidump reader: exception record, registers, modules, and a stack scan for return addresses."""
import struct, sys

path = sys.argv[1]
d = open(path, 'rb').read()
sig, ver, nstreams, dir_rva = struct.unpack_from('<IIII', d, 0)
assert sig == 0x504D444D
streams = {}
for i in range(nstreams):
    t, size, rva = struct.unpack_from('<III', d, dir_rva + i * 12)
    streams[t] = (size, rva)

# modules (stream 4)
mods = []
size, rva = streams[4]
n = struct.unpack_from('<I', d, rva)[0]
for i in range(n):
    o = rva + 4 + i * 108
    base, msize = struct.unpack_from('<QI', d, o)
    name_rva = struct.unpack_from('<I', d, o + 20)[0]
    ln = struct.unpack_from('<I', d, name_rva)[0]
    name = d[name_rva + 4:name_rva + 4 + ln].decode('utf-16le')
    mods.append((base, msize, name))

def where(addr):
    for base, msize, name in mods:
        if base <= addr < base + msize:
            return f"{name.split(chr(92))[-1]}+0x{addr - base:x}"
    return None

# memory ranges: MemoryListStream (5) and Memory64ListStream (9)
ranges = []
if 5 in streams:
    size, rva = streams[5]
    n = struct.unpack_from('<I', d, rva)[0]
    for i in range(n):
        start, dsize, drva = struct.unpack_from('<QII', d, rva + 4 + i * 16)
        ranges.append((start, dsize, drva))
if 9 in streams:
    size, rva = streams[9]
    n, base_rva = struct.unpack_from('<QQ', d, rva)
    off = base_rva
    for i in range(n):
        start, dsize = struct.unpack_from('<QQ', d, rva + 16 + i * 16)
        ranges.append((start, dsize, off)); off += dsize

def read(addr, n):
    for start, dsize, drva in ranges:
        if start <= addr and addr + n <= start + dsize:
            return d[drva + addr - start: drva + addr - start + n]
    return None

# exception (stream 6)
size, rva = streams[6]
tid, _ = struct.unpack_from('<II', d, rva)
code, flags, rec, addr, nparams = struct.unpack_from('<IIQQI', d, rva + 8)
params = struct.unpack_from('<15Q', d, rva + 8 + 32)
ctx_size, ctx_rva = struct.unpack_from('<II', d, rva + 8 + 152)
print(f"exception 0x{code:08x} at 0x{addr:x} = {where(addr)}  thread {tid}")
if code == 0xC0000005:
    print(f"  access violation: {'write' if params[0] == 1 else 'read' if params[0] == 0 else 'execute'} of address 0x{params[1]:x}")

# x64 CONTEXT: integer registers start at offset 0x78 (Rax,Rcx,Rdx,Rbx,Rsp,Rbp,Rsi,Rdi,R8..R15), Rip at 0xF8
regs = struct.unpack_from('<16Q', d, ctx_rva + 0x78)
names = ['rax', 'rcx', 'rdx', 'rbx', 'rsp', 'rbp', 'rsi', 'rdi', 'r8', 'r9', 'r10', 'r11', 'r12', 'r13', 'r14', 'r15']
rip = struct.unpack_from('<Q', d, ctx_rva + 0xF8)[0]
print("  rip", hex(rip), where(rip))
print("  " + "  ".join(f"{n}={v:x}" for n, v in zip(names, regs)))
rsp = regs[4]
stack = read(rsp, 0x1800) or read(rsp, 0x800) or read(rsp, 0x200)
print(f"  stack bytes available: {len(stack) if stack else 0}")
print("return-address candidates on the stack (module+offset):")
seen = 0
if stack:
    for i in range(0, len(stack) - 7, 8):
        v = struct.unpack_from('<Q', stack, i)[0]
        w = where(v)
        if w and any(k in w.lower() for k in ('client.dll', 'panorama', 'engine2', 'server.dll', 'panoramauiclient', 'v8')):
            print(f"  rsp+0x{i:04x}: {w}")
            seen += 1
            if seen >= 40: break
