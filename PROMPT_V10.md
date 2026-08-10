# Complete Verified Findings — Pure C# PS4 PKG Builder Accepted by orbis-pub-cmd

Goal: replace orbis-pub-cmd's img_create with pure C#. Every finding below was
verified empirically against orbis-pub-cmd 3.87 output (the real Digimon FPKG
and controlled fixtures). Final acceptance = `orbis img_file_list` shows
Image0 files.

## 1. PFSC container (compressed mode — the long-missing piece)
- Compressed sectors are **complete RFC1950 zlib streams**:
  `0x48 0x89` (CMF/FLG, CINFO=4 → 4KiB window) + raw deflate + **big-endian
  Adler32 of the decompressed block** (last 4 bytes). Verified: all 6 tested
  orbis sectors matched computed Adler32.
- Our writer previously emitted header+deflate WITHOUT the Adler32 trailer —
  orbis rejected it. Store-only mode also works (LibOrbisPkg does the same).
- `dataOffset` must be 0x10000 (block-aligned) — a packed 1544-byte offset
  made orbis reject the whole PKG.
- Raw-stored blocks (incompressible) = exactly 65536 bytes.
- orbis itself only compresses the first ~9 metadata blocks; stores file data
  raw. Our all-compressed output is accepted too.
- Level-6 SharpZipLib raw deflate matches orbis's Huffman output byte-for-byte.

## 2. PKG header assembly
- Header buffer must span 0x1100 (0x1000 header + 256-byte RSA signature at
  0x1000) — an 0x1000-only buffer overflows on the signature BlockCopy.
- `pfs_image_offset` must be 0x80000-aligned (0xFFFF alignment rejected).
- Entry encryption: AES-CBC ciphertext must be stored at the 16-aligned size
  (npbind.dat 532 → 544). Truncating to originalSize corrupts unaligned
  entries.
- Header digests/signature: sc_entries hashes (0x100/0x120/0x140), body
  digest (0x160, SHA256 of 0x2000..pfsOffset), pfs digests (0x440/0x460).

## 3. Inner PFS (D32, mode 0x8, unseeded)
- **Inode table spans multiple blocks**: D32 inodes 0xA8, packed with a skip
  rule (never straddle a block boundary). 739 inodes → 2 blocks. Real orbis:
  inode 390 at block 2; superroot=block 3, FPT=4, empty=5, uroot=6;
  header `dinode_block_count`=2, hdr dinode size=131072, blocks=2.
- **Files use contiguous-run pointers, NOT indirect blocks**: multi-block
  file inode = `db[0]=first, db[1..11]=0xFFFFFFFF (-1), ib=0`. The -1 means
  "contiguous from previous". Single-block files: db[1..11]=0.
- Inode numbering: 0=superroot, 1=flat_path_table, 2=uroot, then dirs, files.
- FPT: 8 bytes/entry (u32 hash + u32 inode|flags<<28; dirs flag=0x2, files=0);
  sorted by hash; covers dirs AND files.
- nlink: uroot = 3 + subdirs; dirs = 2 + subdirs; files = 1.
- uroot dirents populated (. .. + entries) — empty uroot also mounts.

## 4. Outer PFS (S32, mode 0xD, XTS + signed)
- uroot dirents MUST be populated (".", "..", "pfs_image.dat") — empty uroot
  makes orbis's VFS walk fail.
- Block signatures: HMAC-SHA256(signKey, block) where
  signKey = HMAC(EKPFS, LE32(2) || seed); slots at inode+0x64 (direct),
  inode+0x214 (indirect, S32 layout); header sig at 0x380 over 0..0x5A0;
  bottom-up signing order (data → child indirect → ib1 → ib0 → inode).
- Doubly-indirect: ib1 (block 6) holds pointers to child indirect blocks
  (7..); WriteIndirectS32 block = dataStart + firstDataIndex + i (not
  dataStart+dataPos — overlapping direct blocks).
- XTS: sectors 16+ encrypted (blocks 0/4 plaintext); tweak = sector index LE.

## 5. Entry table
- **Duplicate entry IDs reject the whole PKG.** Extracted sce_sys contains
  license.dat/license.info/psreserved.dat; assembler added them as fixed
  placeholders AND via name→ID map → 2× 0x400/0x401/0x409 → "Format of the
  package file is not valid". Fix: replace placeholder data with real file
  content, never duplicate an ID.
- Real orbis 3.87 entry IDs (differ from psdevwiki): pronunciation.xml=0x1004,
  pronunciation.sig=0x1005, pic1.png=0x1006 (NOT 0x1241), shareparam.json=
  0x100B, shareoverlayimage.png=0x100C, icon0.dds=0x1280, pic0.dds=0x12A0,
  pic1.dds=0x12C0.
- keystone is NOT an Sc0 entry in real FPKGs — it lives in the inner PFS.

## 6. Streaming >2GB (real games)
- byte[] is dead for 11.9 GB inner PFS: temp-file pipeline
  inner.pfs → inner.pfsc → outer.pfs → final.pkg, all 64-bit offsets;
  int overflow bugs fixed in PFSC block sizing.
- AssembleToFile: header+entry table built small in memory, body streamed.

## 7. Reader-side (for parity with our writer)
- Our reader already handles: multi-block inode tables, -1 contiguous runs,
  FPT flags, signed/unsigned inodes, XTS — used as the regression oracle.

## Current status
| Test | orbis img_file_list |
|------|---------------------|
| 3-file fixture, compressed PFSC | ✅ |
| 1.1 GB streaming (doubly-indirect) | ✅ |
| 500-file fixture (2 inode blocks) | ✅ |
| fixture with license/psreserved (dupe-fix) | ✅ |
| Digimon 11.9 GB rebuild (v8) | building |

Next: v8 orbis validation + extraction content hash comparison vs original.
