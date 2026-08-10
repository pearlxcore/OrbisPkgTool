# Adler32 Hypothesis — CONFIRMED + Compressed PFSC Now Accepted by orbis-pub-cmd

## The report you asked for

### 1. Orbis compressed sector
```
header:        48 89  (CMF=0x48 → CINFO=4, 4KiB window; FLG=0x89 — a VALID RFC1950 header)
sector length: 84–236 bytes (for 65536-byte blocks)
last 4 bytes:  big-endian Adler-32 of the decompressed block
```

### 2. Adler32 test (6 compressed sectors from an orbis-built PKG)
```
block  0: adler32=8AF30948 stored=8AF30948 -> MATCH
block  1: adler32=99C398FF stored=99C398FF -> MATCH
block  2: adler32=7EEC08A8 stored=7EEC08A8 -> MATCH
block  3: adler32=D182122E stored=D182122E -> MATCH
block  4: adler32=000F0001 stored=000F0001 -> MATCH   (65536 zero bytes → 0x000F0001, textbook)
block  5: adler32=6D560F5E stored=6D560F5E -> MATCH
```
**Every orbis compressed sector is a COMPLETE RFC1950 zlib stream.**

### 3. WindowBits test
- Orbis declares windowBits=12 (4KiB) via CINFO=4.
- Our writer uses SharpZipLib raw Deflater level 6 (32KiB internal window).
- **No distance failures occurred** — orbis accepted our streams despite the
  4KiB window declaration. Empirically: no special window capping needed.

### 4. Complete compressed sector comparison
- Not byte-identical to orbis (different Huffman table choices), but
  **structurally identical**: `48 89` + raw deflate + BE Adler32.
- Raw-stored blocks (incompressible) = exactly 65536 bytes, matching orbis.
- NOTE: orbis only compresses the first ~9 metadata blocks of a fixture and
  stores all file-data blocks raw; we compress everything. orbis accepts
  all-compressed too.

### 5. orbis-pub-cmd compressed test
```
img_file_list:  ✅ PASS — Image0/a.bin, Image0/b.bin, Image0/dir/c.bin all listed
img_verify:     content-level errors only (missing icon0.png, eboot.bin,
                keystone, sce_module — expected for a 3-file fixture; NO
                structural/parse errors)
```
**The missing Adler32 trailer was the sole remaining cause of compressed-PFSC rejection.**

## Code change applied (PFSCWriter.cs)
Compressed block now written as:
```
0x48 0x89 + raw-deflate(level 6) + adler32(decompressed block)  [BE, 4 bytes]
```
Raw threshold: stream size (comp + 6) >= 65536 → store raw. Both the
byte[] `Build()` and stream `BuildToStream()` paths updated; `BuildToStream`
data pass is now packed-sequential with a running offset.

## Current standing
| Mode | orbis accepts | default |
|------|---------------|---------|
| Store-all-raw      | ✅ | yes (stable per earlier instruction) |
| Compressed+Adler32 | ✅ | candidate to become default once real-game test passes |

## Next
- Complete Digimon 11.9GB rebuild (pure C#, store-raw path) is running;
  stage 4 assemble errored previously — stack trace instrumentation added,
  re-run in progress.
- After it succeeds: orbis img_file_list + img_verify on the real FPKG.
