# PKG Builder — Format Findings

The PS4 PKG/PFS/PFSC format knowledge encoded in this codebase, classified by
how it was established. The compatibility core is **frozen** (commit 97dcfda):
do not change a `[Sony]` value without a regression or console test proving a
defect. All constants live centrally in `OrbisPkgTool/Pfs/PfsFormat.cs` with
the same classification.

---

## 1. Empirically verified against orbis-pub-cmd 3.87 / real FPKGs

Verified by byte-level comparison with the original Digimon World: Next Order
FPKG, MediEvil, Adventure Time, and controlled fixtures built by
orbis-pub-cmd 3.87 itself. Acceptance = `orbis img_file_list` shows the full
Image0 tree of our pure-C# rebuild (11.9 GB, 485 files).

| Fact | Value |
|---|---|
| PFS block size | 0x10000 |
| XTS sector size | 0x1000 |
| PFS magic / version | 20130315 / 1 |
| Inner PFS mode | 0x8 (unseeded, plaintext, D32) |
| Outer PFS mode | 0xD (seeded, XTS, signed, S32) |
| D32 inode size / S32 inode size | 0xA8 / 0x2C8 |
| Inode table starts at block 1; inode never straddles a block boundary | skip-rule packing; 739 inodes → 2 blocks (Digimon) |
| Header dinode at 0x50; direct ptrs at +0x64; S32 indirect at +0x214 | |
| FPT record | 8 bytes (u32 hash + u32 inode\|flags<<28; dir flag 0x2) |
| Multi-block file pointers (inner) | `db[0]=first, db[1..11]=0xFFFFFFFF` = contiguous run; **no indirect blocks** (Digimon archive.psarc: 3629 blocks) |
| nlink | uroot = 3 + subdirs; dirs = 2 + subdirs; files = 1 |
| PFSC dataOffset | block-aligned **past the end of the block table** (0x10000 for small PFSCs; large PFSCs >8063 blocks need 0x20000+ — table would otherwise overlap data) |
| PFSC compressed sector | **complete RFC1950 zlib stream**: `48 89` (CINFO=4) + raw deflate + **big-endian Adler32** of the decompressed block |
| PFSC deflate level | 6 (byte-identical Huffman to orbis via SharpZipLib raw Deflater) |
| Incompressible PFSC blocks | stored raw, exactly 0x10000 bytes |
| PKG entry table offset / body | 0x2A80 / 0x2000 |
| pfs_image_offset alignment | 0x80000 |
| **Entry table DataSize = LOGICAL size** | Digimon npbind.dat: table says 532, stored region is 544 (16-aligned ciphertext, real bytes to the end) |
| Encrypted entry flags | flags1 bit31 set + flags2 key index (npbind: key 3) |
| Digest regions | sc_entries1 0x100, sc_entries2 0x120, digest-table 0x140, body 0x160, pfs 0x440/0x460, header 0xFE0, RSA sig 0x1000 (256B) |
| Outer PFS signatures | HMAC-SHA256(signKey=HMAC(EKPFS, LE32(2)\|\|seed), block); slots at +0x64/+0x214 (S32), header sig at 0x380 over 0..0x5A0 (sig region zeroed); signed bottom-up before XTS |
| Entry IDs (orbis 3.87, differ from psdevwiki) | pronunciation.xml 0x1004, pronunciation.sig 0x1005, **pic1.png 0x1006 (NOT 0x1241)**, shareparam.json 0x100B, shareoverlayimage.png 0x100C, icon0.dds 0x1280, pic0.dds 0x12A0, pic1.dds 0x12C0 |
| keystone | NOT an Sc0 entry — lives in the inner PFS |
| Duplicate entry IDs | rejected: `Format of the package file is not valid` |
| orbis verify profile of our rebuild | identical to the original (same 2 benign warnings: TRC R4211, R4124) |

## 2. Confirmed by OpenOrbis / LibOrbisPkg (independent implementation)

| Fact | Value |
|---|---|
| PFSC unk4 = 0, unk8 = 6 | LibOrbisPkg PFSCReader requirements |
| FPT collision resolver | collided FPT entry = `0x80000000 \| resolverOffset`; `collision_resolver` file in superroot (inode 2), dirs/files shifted +1; resolver holds the colliding nodes' dirents (full paths) + 0x18 padding per collided hash; replaces the empty block |
| Empty block after the FPT | present when there is NO collision resolver |
| Store-only PFSC | LibOrbisPkg's own PFSCWriter stores all blocks raw |
| GP4 structure / gengp4 conventions | flat dump layout (Image0 root, sce_sys inside) |

## 3. Implementation choices (not required by the format)

- **Store-only PFSC is the default** (`--pfsc-mode store`). Compressed mode is
  proven against orbis-pub-cmd (`--pfsc-mode compressed`) but stays opt-in
  until a real PS4 install test passes.
- **Temp-file build pipeline** for >1 GB games: `inner.pfs → inner.pfsc →
  outer.pfs → final.pkg` (peak disk ≈ 3.2× inner size; pre-flight disk check;
  temp dir auto-cleaned, also on failure and Ctrl+C).
- **Keystone auto-generated** into the inner PFS when absent.
- **param.sfo generated** from GP4 metadata (28 keys, `CreateGameTemplate`).
- **File ordering**: alphabetical; dirs before files; FPT sorted by hash.
- Progress reporting, cancellation, and `--manifest build.json` are ours.
- Build validation (`--validate` / `validate` command): 8-stage structured
  check (header/entries, outer PFS, PFSC, inner PFS, digests, HMAC sigs,
  filesystem walk) with fail-fast builder invariants.

## 4. Not yet console-verified

Explicitly NOT proven (the tool is documented as *PC/orbis-pub-cmd compatible*,
not *fully PS4-compatible*):

- Actual installation and launch on a jailbroken PS4 (see
  `CONSOLE_VALIDATION_CHECKLIST.md`)
- 50–150 GB package stress test (code paths are shared with the proven
  11.9 GB build — multi-block inode tables, streaming, doubly-indirect outer)
- All PS4 PKG content types (themes, AC/DLC, remasters, patches beyond gp)
- Patch/DLC behavior
- Trophy subsystem behavior on hardware
