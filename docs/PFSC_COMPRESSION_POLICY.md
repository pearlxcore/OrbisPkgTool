# PFSC Compression Policy Replay — Design & Findings

Status: implemented (per-file raw/compressed replay, GP4-attribute transport,
4 KiB zlib window). This document records the design, the verification results,
and the zero-fill ownership analysis of the original Yooka-Laylee FPKG.

## Problem

Rebuilt PKGs were smaller than the original because our builder compressed
every PFSC block while the original deliberately stored ~29% of content raw
(per-file `pfs_compression="disable"` in its GP4). `BuildOptions.PfscMode`
also defaulted to `Store`, so a repack without `--pfsc-mode compressed`
silently produced a fully-uncompressed multi-GB package.

## Design (as implemented)

1. **`PfscProfiler`** (`Pfs/PfscProfiler.cs`) — reads the original's PFSC
   block table + inner-PFS inode walk and classifies every file:
   - any compressed block → `enable`
   - all allocated blocks raw → `disable`
   - zero data blocks → `none`

   This is the *effective* policy (an enabled-but-incompressible file and a
   disabled file are indistinguishable from the final PFSC — replaying
   "disable" for both is correct because it produces identical storage
   behavior for unchanged content).

2. **Policy transport = GP4 `pfs_compression` attribute** (the Sony-native
   mechanism). `gp4gen --pfsc-profile <json>` stamps the replayed policy into
   the generated GP4; `repack` auto-captures the profile during extraction
   and saves it as `pfsc_profile.json` in the work dir (never under Image0 —
   `FromFolder` packages everything there). Hand-authored GP4s with real
   policies work automatically, and our GP4s become cross-buildable by
   LibOrbisPkg/orther GP4 consumers.

3. **`PfsWriter` allocation manifest** — `BuildInnerPfsToStream` now returns
   `PfsBuildResult` (per-file `startBlock`/`blockCount`). Block ranges are
   NEVER derived from cumulative file lengths: the layout contains header,
   inode table, dirents, FPT and collision-resolver blocks before any file
   data.

4. **`PFSCWriter.RawBlockSet`** — a block bitmap; blocks of "disable" files
   are stored raw without even attempting compression (matches orbis
   behavior). Both `Build` (memory) and `BuildToStream` (streaming) honor it
   identically — byte-identical output for the same policy (regression-tested).

5. **Default flipped** to `Compressed` (BuildOptions, CLI `build`, CLI
   `repack`).

6. **4 KiB deflate window** (`Pfs/PfscDeflate.cs`) — zlib1.dll
   `deflateInit2(windowBits=-12)` produces raw deflate formally valid for the
   declared `0x48 0x89` header (CINFO=4 → 4096-byte window). Previously
   SharpZipLib's 32 KiB window could emit back-references beyond the declared
   window — accepted by zlib-family decoders (inflate only enforces its
   CONFIGURED window) but formally invalid. SharpZipLib remains the fallback
   when zlib1.dll is absent.

   Note: the 4 KiB window compresses slightly worse, so marginally more
   blocks hit the raw-fallback threshold (~240 extra raw blocks on the Yooka
   rebuild, +15 MB stored) — a deliberate correctness-over-size trade.

7. **CLI `pfscprofile <pkg> [--out profile.json] [--ref ref.pkg]`** — block
   statistics + per-file policy + optional diff against a reference PKG.

## Verification (2026-08-20, yooka_base_rebuilt.pkg 2.6 GB)

- Full repack (extract → profile → gp4gen → build) completed in 2.7 min.
- `pfscprofile --ref` on the result: **1046/1046 files policy-identical,
  0 mismatched, 0 missing**.
- 8-stage `validate`: PASS.
- Full extract comparison: 1065/1065 files present, 0 size mismatches,
  SHA-256 identical on the 15 largest files.
- 23/23 regression tests green (18 existing + 5 new policy tests).

## Zero-fill ownership analysis (yooka_orig.pfsc — the ORIGINAL's PFSC)

The original's PFSC contains 32,796 all-zero blocks = **2.05 GB decompressed**
(stored cost: ~2.7 MB compressed + 3 MB raw). Ownership is now PROVEN:

- They form **one contiguous run: blocks 43..32766** (32,724 blocks = 2.05 GB)
  — starting immediately after the PFS metadata blocks and ending exactly at
  the 32K-block boundary, all stored compressed.
- The block table has a **60.7 MB physical gap** (zeros) between entry 32767
  (offset 3,508,374) and entry 32768 (offset 0x4000000 = 64 MB), plus a small
  75 KB tail gap. The original builder physically zero-pads the container at
  the 32K-block boundary.

**Conclusion:** the 2.05 GB is a pre-allocated zeroed extent in the original
builder's inner-PFS layout (allocation slack, most likely PlayGo/large-file
pre-allocation), NOT content. It is invisible to readers (the block table
preserves logical positions) and **must not be reproduced** — our compact
layout is equivalent. This fully explains the original's 5.5 GB decompressed
size vs our 3.35 GB (5.5 GB − 2.05 GB ≈ 3.35 GB + structural blocks).

## Remaining size-gap breakdown (Yooka base, post-fix)

| Source | Approx. |
|---|---|
| Zero-fill extent (deliberately not reproduced) | ~2.05 GB decompressed / ~5.7 MB stored |
| 60.7 MB container gap (deliberately not reproduced) | 60.7 MB |
| Deflate window difference (4 KiB vs Sony's) | ~+15 MB stored |
| Residual engine differences | ~1% |

Size remains a **diagnostic**, not a gate: the goal is cross-tool
compatibility (orbis-pub-cmd, LibOrbisPkg, shadPS4, jailbroken PS4), and
every consumer reads via the block table, which is layout-independent.
