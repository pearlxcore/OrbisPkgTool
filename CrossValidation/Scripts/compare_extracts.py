#!/usr/bin/env python3
"""Byte-level comparison of two extracted PKG dumps (original vs rebuilt).

Compares every file by relative path: presence, size, and SHA-256.
Exits non-zero if ANY file differs or is missing.

Usage: python compare_extracts.py <dump-original> <dump-rebuilt>
"""
import hashlib
import os
import sys

HASH = True  # set False for a fast size-only pass


def snapshot(root: str) -> dict:
    out = {}
    for dirpath, dirnames, filenames in os.walk(root):
        # keep deterministic order
        dirnames.sort()
        for fn in sorted(filenames):
            full = os.path.join(dirpath, fn)
            rel = os.path.relpath(full, root)
            st = os.stat(full)
            if HASH:
                h = hashlib.sha256()
                with open(full, "rb") as f:
                    for chunk in iter(lambda: f.read(1 << 20), b""):
                        h.update(chunk)
                out[rel] = (st.st_size, h.hexdigest())
            else:
                out[rel] = st.st_size
    return out


def main() -> None:
    if len(sys.argv) != 3:
        print(__doc__)
        sys.exit(1)
    a_root, b_root = sys.argv[1], sys.argv[2]
    print(f"scanning {a_root} ...")
    a = snapshot(a_root)
    print(f"scanning {b_root} ...")
    b = snapshot(b_root)

    missing_in_b = sorted(set(a) - set(b))
    missing_in_a = sorted(set(b) - set(a))
    diff = sorted(p for p in set(a) & set(b) if a[p] != b[p])

    print(f"\n{len(a)} files in original, {len(b)} files in rebuilt")
    print(f"missing from rebuilt : {len(missing_in_b)}")
    print(f"missing from original: {len(missing_in_a)}")
    print(f"size/hash differ     : {len(diff)}")

    for p in missing_in_b[:20]:
        print(f"  - missing in rebuilt: {p}")
    for p in missing_in_a[:20]:
        print(f"  - extra in rebuilt : {p}")
    for p in diff[:20]:
        a_v, b_v = a[p], b[p]
        print(f"  - differs: {p}  original={a_v} rebuilt={b_v}")

    ok = not (missing_in_b or missing_in_a or diff)
    print(f"\nRESULT: {'IDENTICAL' if ok else 'DIFFERENCES FOUND'}")
    sys.exit(0 if ok else 1)


if __name__ == "__main__":
    main()
