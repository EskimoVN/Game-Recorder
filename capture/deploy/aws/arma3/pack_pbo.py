#!/usr/bin/env python3
"""Packs a mission folder into a PBO: python3 -I pack_pbo.py <folder> <out.pbo>.

PBO layout: a "Vers" product entry carrying the prefix, one header entry per file (name with backslashes, packing method 0,
original size, reserved, timestamp, data size as uint32 LE), an empty entry, the file data in header order, then a zero byte
and the SHA-1 of everything before it. Standard library only, so it runs on the server.
"""
import hashlib
import struct
import sys
from pathlib import Path


def entry(name: bytes, method: int, original: int, reserved: int, timestamp: int, size: int) -> bytes:
    return name + b"\0" + struct.pack("<5I", method, original, reserved, timestamp, size)


def pack(folder: Path, out: Path) -> None:
    files = sorted(p for p in folder.rglob("*") if p.is_file())
    header = entry(b"", 0x56657273, 0, 0, 0, 0) + b"prefix\0" + folder.name.encode() + b"\0\0"
    data = b""
    for p in files:
        content = p.read_bytes()
        name = "\\".join(p.relative_to(folder).parts).encode()
        header += entry(name, 0, len(content), 0, int(p.stat().st_mtime), len(content))
        data += content
    header += entry(b"", 0, 0, 0, 0, 0)
    body = header + data
    out.write_bytes(body + b"\0" + hashlib.sha1(body).digest())


if __name__ == "__main__":
    if len(sys.argv) != 3:
        sys.exit(__doc__)
    pack(Path(sys.argv[1]), Path(sys.argv[2]))
