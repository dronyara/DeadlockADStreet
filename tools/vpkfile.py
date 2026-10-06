"""Just enough of Valve's VPK v2 and resource container formats for the addon builders: read one file out of
the game's archive, write a single-file VPK, and list or replace the blocks of a compiled (_c) resource."""
import hashlib, io, os, struct, zlib


def read_dir(path):
    """Directory of a pak01_dir.vpk: path inside the archive -> where its bytes are."""
    with open(path, "rb") as f:
        sig, ver, tree_size = struct.unpack("<III", f.read(12))
        if sig != 0x55AA1234:
            raise SystemExit(f"{path} is not a VPK")
        header = 12
        if ver == 2:
            f.read(16)
            header = 28
        tree = f.read(tree_size)
    pos = 0

    def cstr():
        nonlocal pos
        end = tree.index(b"\0", pos)
        s = tree[pos:end].decode("utf-8", "replace")
        pos = end + 1
        return s

    out = {}
    while ext := cstr():
        while d := cstr():
            while name := cstr():
                crc, preload, archive, offset, length, term = struct.unpack_from("<IHHIIH", tree, pos)
                pos += 18
                out[(d + "/" if d.strip() else "") + name + "." + ext] = (archive, offset, length, tree[pos:pos + preload], header + tree_size)
                pos += preload
    return out


def read_file(dir_path, entry):
    archive, offset, length, preload, data_start = entry
    if archive == 0x7FFF:
        path, offset = dir_path, offset + data_start
    else:
        path = dir_path.replace("_dir.vpk", "_%03d.vpk" % archive)
    with open(path, "rb") as f:
        f.seek(offset)
        return preload + f.read(length)


def build_vpk(entries):
    """A single-file VPK v2: header, directory tree, then the file data. entries: path inside -> bytes."""
    tree = {}       # ext -> dir -> [(name, data)]
    for path, data in entries.items():
        d, base = os.path.split(path)
        name, ext = os.path.splitext(base)
        tree.setdefault(ext[1:], {}).setdefault(d or " ", []).append((name, data))
    t, blob, offset = io.BytesIO(), io.BytesIO(), 0
    for ext, dirs in tree.items():
        t.write(ext.encode() + b"\0")
        for d, items in dirs.items():
            t.write(d.encode() + b"\0")
            for name, data in items:
                t.write(name.encode() + b"\0")
                # crc, preload bytes, archive index (0x7fff = this file), offset, length, terminator
                t.write(struct.pack("<IHHIIH", zlib.crc32(data) & 0xFFFFFFFF, 0, 0x7FFF, offset, len(data), 0xFFFF))
                blob.write(data)
                offset += len(data)
            t.write(b"\0")
        t.write(b"\0")
    t.write(b"\0")
    tree_bytes, data_bytes = t.getvalue(), blob.getvalue()
    header = struct.pack("<IIIIIII", 0x55AA1234, 2, len(tree_bytes), len(data_bytes), 0, 48, 0)
    body = header + tree_bytes + data_bytes
    checksums = hashlib.md5(tree_bytes).digest() + hashlib.md5(b"").digest()
    checksums += hashlib.md5(body + checksums).digest()
    return body + checksums


def resource_blocks(data):
    """Blocks of a compiled resource: (header version, resource version), [(type, bytes)]."""
    size, header_version, version, block_offset, count = struct.unpack_from("<IHHII", data, 0)
    blocks, p = [], 8 + block_offset
    for _ in range(count):
        offset, length = struct.unpack_from("<II", data, p + 4)
        blocks.append((data[p:p + 4].decode("ascii"), data[p + 4 + offset:p + 4 + offset + length]))
        p += 12
    return (header_version, version), blocks


def build_resource(versions, blocks):
    """The reverse of resource_blocks. Block data is 16-byte aligned, as the compiler lays it out."""
    table = 16 + 12 * len(blocks)
    pos = (table + 15) & ~15
    body, entries = b"", b""
    for i, (typ, data) in enumerate(blocks):
        body += b"\0" * (pos - (((table + 15) & ~15) + len(body)))
        field = 16 + 12 * i + 4
        entries += typ.encode("ascii") + struct.pack("<II", pos - field, len(data))
        body += data
        pos = (pos + len(data) + 15) & ~15
    head = struct.pack("<HHII", versions[0], versions[1], 8, len(blocks)) + entries
    head += b"\0" * (((table + 15) & ~15) - 4 - len(head))
    return struct.pack("<I", 4 + len(head) + len(body)) + head + body
