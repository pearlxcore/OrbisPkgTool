# Latest Findings — Duplicate Entry IDs Was the Final Rejection Cause

## The bug (just found + fixed)
PKGs built from extracted games were rejected by orbis-pub-cmd with
`[Error] Format of the package file is not valid.` — even after every PFS
fix. Root cause: **duplicate entry IDs in the PKG entry table**.

The extracted `sce_sys/` contains `license.dat`, `license.info`,
`psreserved.dat`. Our assembler added them TWICE:
1. As fixed placeholder entries (zero-filled, with correct flags/key),
2. Again via the Known-name→ID loop (with real file content).

Result: two entries with ID 0x400, two with 0x401, two with 0x409.
orbis rejects any table with duplicate IDs. All our passing test fixtures
had empty `sce_sys` (only param.sfo) — that's why they passed.

**Fix:** when a Known-named sce_sys file matches an already-added fixed
entry, REPLACE the placeholder's Data with the real file content instead
of adding a second entry. Applied to both the in-memory and streaming
assemble paths.

## Other findings this round (all verified against the real Digimon FPKG)
1. **Inner PFS inode table spans multiple blocks** — 739 inodes → 2 blocks.
   D32 inodes (0xA8) are packed with a skip rule (never straddle a block
   boundary); real orbis: inode 390 at block 2, superroot=block 3, FPT=4,
   empty=5, uroot=6, hdr dinode size=131072 blocks=2. Writer now matches.
2. **Inner PFS uses CONTIGUOUS-RUN pointers, NOT indirect blocks** — the
   original's archive.psarc (237 MB, 3629 blocks) inode is just
   `db[0]=751, db[1..11]=0xFFFFFFFF, ib[0..4]=0`. The -1 sentinel means
   "contiguous from previous". Our writer already emits db[1]=-1 ✓.
3. **Real entry IDs from orbis 3.87** (differ from psdevwiki):
   pronunciation.xml=0x1004, pronunciation.sig=0x1005, pic1.png=0x1006
   (NOT 0x1241!), shareparam.json=0x100B, shareoverlayimage.png=0x100C,
   icon0.dds=0x1280, pic0.dds=0x12A0, pic1.dds=0x12C0.

## Current acceptance status
| Test PKG | orbis img_file_list |
|----------|---------------------|
| 3-file fixture (compressed PFSC + Adler32) | ✅ |
| 1.1 GB (streaming, doubly-indirect outer) | ✅ |
| 500-file fixture (2 inode-table blocks) | ✅ |
| fixture with license/psreserved (post-dupe-fix) | ✅ |
| **Digimon 11.9 GB rebuild (v8, dupe-fix)** | **building now** |

Everything else (outer PFS indirect+sigs, XTS, PFSC store-raw AND
compressed, keystone in inner PFS, entry encryption incl. unaligned
npbind.dat 532→544) is proven accepted. The final validation is the
Digimon v8 orbis test + extraction content comparison vs the original.
