#!/usr/bin/env python3
"""Analyze the compression profile of a PS4 PKG's PFSC image (pfs_image.dat).

Prints per-64KiB-block statistics: how many blocks are stored RAW
(compressed size == block size) vs COMPRESSED, plus byte totals.

Usage: python pfsc_profile.py <path-to-pfs_image.dat>
"""
import struct
import sys

BLOCK_SIZE = 0x10000


def main(path: str) -> None:
    with open(path, "rb") as f:
        head = f.read(0x30)
        magic, unk1, unk2, block_size, align, unk3, table_off, data_off, rounded = struct.unpack(
            "<IIIIIIQQQ", head)
        assert magic == 0x43534650, f"bad PFSC magic {magic:#x}"
        nblocks = (rounded + block_size - 1) // block_size
        f.seek(table_off)
        table = struct.unpack(f"<{nblocks + 1}Q", f.read((nblocks + 1) * 8))

    raw = comp = 0
    raw_bytes = comp_bytes = 0
    sizes = {}
    for i in range(nblocks):
        sz = table[i + 1] - table[i]
        sizes[sz] = sizes.get(sz, 0) + 1
        if sz == block_size:
            raw += 1
            raw_bytes += sz
        else:
            comp += 1
            comp_bytes += sz

    print(f"PFSC file        : {path}")
    print(f"block size       : 0x{block_size:x} ({block_size})")
    print(f"blocks           : {nblocks}")
    print(f"rounded size     : {rounded} bytes")
    print(f"table offset     : 0x{table_off:x}   data offset: 0x{data_off:x}")
    print(f"RAW blocks       : {raw} ({raw / nblocks * 100:.1f}%)  -> {raw_bytes} bytes")
    print(f"COMPRESSED blocks: {comp} ({comp / nblocks * 100:.1f}%) -> {comp_bytes} bytes")
    print(f"raw bytes share  : {raw_bytes / (raw_bytes + comp_bytes) * 100:.1f}% of stored data")
    if comp:
        print(f"deflate ratio    : {comp_bytes / (comp * block_size) * 100:.1f}% of uncompressed (best-effort)")
    print("distinct block sizes (top 6):")
    for sz, cnt in sorted(sizes.items(), key=lambda kv: -kv[1])[:6]:
        print(f"  {sz:>8} bytes x {cnt}")


if __name__ == "__main__":
    if len(sys.argv) != 2:
        print(__doc__)
        sys.exit(1)
    main(sys.argv[1])
