# OrbisPkgTool — Native C# Replacement for orbis-pub-cmd.exe

Pure managed C# (.NET 10) reimplementation of Sony's `orbis-pub-cmd.exe`.
Reads, extracts, validates, builds, and repacks PS4 fake PKGs (FPKGs) with
no dependency on the Sony tool, LibOrbisPkg, or any external PKG library —
all PKG/PFS/PFSC parsing, AES-128-CBC / RSA / AES-XTS crypto, and zlib
deflate is implemented from scratch using `System.Security.Cryptography`.
The only native interop is an **optional** `zlib1.dll` load for the 4 KiB
deflate window used by PFSC compression (SharpZipLib fallback when absent).
Also includes an in-process ATRAC9 audio decoder (`.at9` → WAV), a PSN
official-update checker (TitlePatch XML/manifest parsing), and a legacy
46-row PKG header dump for A/B comparison against the Sony tool.

The compatibility core is **frozen** (commit 97dcfda): every format constant
verified against real FPKGs and orbis-pub-cmd 3.87 output. A regression
suite of 83 xUnit tests enforces it — see `OrbisPkgTool.Tests`.

## Projects

```
OrbisPkgTool/                        class library (net10.0-windows) + CLI
                                      entry point (Program.cs) — drop-in
                                      orbis-pub-cmd command syntax
  PkgReader.cs                       public API: ListFiles / ExtractFile /
                                      ExtractEntryBytes / ExtractAll /
                                      OpenRawPfscStream / InnerPfs
  PkgFileEntry.cs                    listing entry model
  PkgInfo.cs                         metadata (title/id/type/category)
  Pkg/PkgHeader.cs                   PKG header (big-endian)
  Pkg/PkgEntry.cs                    entry table + well-known entry IDs/names
  Pkg/PkgBuilder.cs                  PKG builder (in-memory + streaming)
                                      with per-file compression-policy replay
  Pkg/BuildOptions.cs                build options (PfscMode.Compressed default)
  Pkg/PkgValidator.cs                8-stage structural validation
                                      (--fake-tolerant for scene FPKGs)
  Pkg/PkgHeaderDump.cs               legacy 46-row header dump for A/B
                                      comparison against the Sony tool
  Crypto/PkgCrypto.cs                passcode → derived keys, AES-128-CBC
                                      entry decryption, RSA, EKPFS
  Crypto/PkgKeySet.cs                embedded RSA keys (leaked dk3 private
                                      key + fake keyset)
  Gp4/Gp4Project.cs                  GP4 parse/serialize/generate
                                      (pfs_compression-aware, --pfsc-profile)
  Media/                             in-process ATRAC9 decoder: .at9 → WAV
                                      (At9Decoder + LibAtrac9 port)
  Psn/UpdateCheck.cs                 PSN official update check: TitlePatch
                                      XML + manifest JSON parsing
  Pfs/PfsFormat.cs                   FROZEN format-constant registry
  Pfs/PfsReader.cs                   PFS filesystem: inodes, dirents, XTS,
                                      indirection, consecutive runs
  Pfs/PfsWriter.cs                   inner/outer PFS writer + per-file block
                                      allocation manifest (PfsBuildResult)
  Pfs/PFSCStream.cs                  PFSC (zlib-compressed inner image)
                                      decompression
  Pfs/PFSCWriter.cs                  PFSC writer: per-file raw/compressed
                                      policy, both memory + streaming paths
  Pfs/PfscDeflate.cs                 zlib1.dll 4 KiB-window deflate
                                      (windowBits=-12, SharpZipLib fallback)
  Pfs/PfscProfiler.cs                original-package compression-policy
                                      inspector → GP4 profile JSON
  Sfo/ParamSfo.cs                    param.sfo read/serialize + tables-shaped
                                      accessor + game/addon/theme templates
  Trp/Trp.cs                         trophy TRP pack read/create
  Util/MersenneTwister.cs            MT19937 for Sony's RSA2048EncryptKey
                                      padding scheme (PkgCrypto)
OrbisPkgTool.Gui/                    Windows Forms GUI (net10.0-windows)
OrbisPkgTool.Tests/                  83-test xUnit suite: compatibility
                                      regression, AT9 decoder, header dump,
                                      SFO tables, PSN update check
```

## Commands

```
list        : List files in a PKG              (list -h for details)
extract     : Extract files from a PKG         (extract -h for details)
verify      : Verify PKG hashes/signatures
info        : Show PKG metadata
inspect     : Full PFS tree dump
entries     : Dump the raw PKG entry table (ids, flags, sizes, offsets)
validate    : 8-stage structural validation    (--fake-tolerant for scene FPKGs)
build       : Build a fake PKG from GP4        (build -h for details)
orbis-build : Build a fake PKG using orbis-pub-cmd
repack      : Extract + restructure + gp4gen + build (one-shot)
              (auto-replays the original's per-file compression policy)
merge       : Integrate an update into its base → one self-contained
              base-app PKG at the update's version (keystone injected,
              saves work; output sealed with the default passcode)
gp4gen      : Generate GP4 from a folder       (gp4gen -h for details)
restructure : Restructure dump for build (--check dry-run)
sweep       : Batch verify PKGs in a folder
bench       : Benchmark listing speed
selftest    : Validate RSA keys
sfo         : param.sfo tools                  (sfo -h for details)
trp         : Trophy TRP tools                 (trp -h for details)

Diagnostic:
  pfscprofile    : PFSC compression stats + per-file policy (replay source)
  shadps4diag    : Mirror shadPS4 PKG reading logic step-by-step
  s4trace        : Exact shadPS4Plus allocation/bounds replica
  s4extract      : Exact shadPS4Plus Extract + ExtractFiles replica
  s4crypto       : Replicate shadPS4 crypto chain
  pkgfields      : Dump all PKG header/entry fields for A/B comparison
  signverify, pfsdump, pfsblock, innerfpt, iblock,
  fixdigests, resignpfs, xtstest, buildtest, emptypayload,
  pfscompare, dumppfsc, xtsdump, inflatecheck, deftest, blkcount
```

### Quick examples

```powershell
# List every file + directory in a PKG:
OrbisPkgTool.exe list game.pkg

# Extract the whole package (Unicode paths work natively — no cmd.exe needed
# when invoked through ProcessStartInfo, e.g. from another app or PowerShell):
OrbisPkgTool.exe extract --passcode 00000000000000000000000000000000 game.pkg out/

# Extract a single entry:
OrbisPkgTool.exe extract game.pkg:Sc0/param.sfo out/
OrbisPkgTool.exe extract game.pkg:Image0/eboot.bin out/

# 8-stage structural validation (digests, keys, PFSC, PFS, signatures):
OrbisPkgTool.exe validate --passcode 00000000000000000000000000000000 game.pkg

# Scene FPKGs sometimes ship with non-matching entry digests or signatures
# left over from the repacking tool. --fake-tolerant skips those stages and
# still verifies everything else, so you can validate a scene PKG without
# false negatives:
OrbisPkgTool.exe validate --fake-tolerant game.pkg

# Generate a GP4 from an extracted dump:
OrbisPkgTool.exe gp4gen ./Image0 --title "My Game" --title-id CUSA00001 --out game.gp4

# Build a fake PKG (compression policy honored from GP4 pfs_compression):
OrbisPkgTool.exe build game.gp4 ./Image0 --out game.pkg --passcode 00000000000000000000000000000000 --validate

# One-shot repack — extract → restructure → gp4gen → build, with automatic
# replay of the ORIGINAL's per-file compression policy (default --pfsc-mode
# compressed). This is the recommended path for repacking scene FPKGs.
OrbisPkgTool.exe repack original.pkg --out rebuilt.pkg

# Merge a base PKG with its update PKG — extracts both, overlays the
# update's files onto the base dump, and repacks as a single base-app PKG
# at the update's version. The output is sealed with the default passcode.
# CATEGORY=gd is kept from the base (so saves work); only APP_VER/VERSION
# move forward to the update's. Pass --validate to run the 8-stage check.
OrbisPkgTool.exe merge "Game [CUSA00001] 00 - Base.pkg" "Game [CUSA00001] 01 - Update v01.09.pkg" --out merged.pkg --validate

# Inspect the original's PFSC compression profile, optionally save the
# policy JSON for use with gp4gen --pfsc-profile, and diff against a
# reference PKG:
OrbisPkgTool.exe pfscprofile original.pkg --out pfsc_profile.json
OrbisPkgTool.exe pfscprofile rebuilt.pkg --ref original.pkg
```

Default passcode: `00000000000000000000000000000000` (same as the native
tool's default). A wrong passcode is rejected with `Passcode mismatch.` —
identical behavior to orbis-pub-cmd.

**Official/retail PKGs**: when the passcode does not match, the library
automatically RSA-recovers `dk3` from ENTRY_KEYS[3] with the leaked key-3
private key (the same path scene tools use) and decrypts key-index-3 Sc0
entries (npbind.dat, license.dat, …) without the passcode.

## C# API

```csharp
using var pkg = new PkgReader(@"C:\Games\game.pkg");          // passcode optional
foreach (var f in pkg.ListFiles())
    Console.WriteLine($"{(f.IsDirectory ? 'D' : 'F')} {f.Size} {f.Path}");
pkg.ExtractFile("Sc0/changeinfo/changeinfo.xml", outDir);
pkg.ExtractAll(outDir);                                       // full PKG extraction

// Extract a well-known entry by its numeric ID (e.g. param.sfo = 0x1000):
byte[] sfo = pkg.ExtractEntryBytes(0x00001000);

// Build a PKG from a GP4 + source folder, with per-file compression policy:
PkgBuilder.Build("game.gp4", "./Image0", "game.pkg",
    new BuildOptions { PfscMode = PfscMode.Compressed, Validate = true });
```

## Compression policy replay (repack parity)

`repack` replays the ORIGINAL package's per-file compression decisions:
during extraction it profiles the original's PFSC block table + inner-PFS
inode allocation (`PfscProfiler`), stamps the resulting enable/disable
policy into the generated GP4 (`pfs_compression` attributes — the
Sony-native mechanism, so the GP4 stays cross-buildable by LibOrbisPkg
and orbis-pub-cmd), and the builder stores every block of a "disable"
file RAW while compressing everything else. Both build paths (memory +
streaming) produce byte-identical output.

PFSC compressed blocks use a 4 KiB deflate window (zlib1.dll
`deflateInit2`, windowBits=-12) — formally valid for the declared
`0x48 0x89` zlib header (CINFO=4 → 4096-byte window) — with a SharpZipLib
fallback when zlib is absent.

## Validation (vs the real orbis-pub-cmd.exe)

Tested on a 7.3 GB fake game PKG (Digimon World: Next Order, CUSA05392),
an official patch PKG (Disgaea 1 Complete), and a 2.6 GB rebuild with
compression-policy replay (Yooka-Laylee and the Impossible Lair, CUSA16139):

| Check | Result |
|---|---|
| `img_file_list` paths (757 entries, Digimon) | **100% identical** (zero diff) |
| `img_file_list` file sizes (488 files, Digimon) | **100% identical** (zero diff) |
| Sc0 entry extraction (param.sfo) | byte-identical SHA256 |
| Image0 single-block file (.pos) | byte-identical SHA256 |
| Image0 multi-block file (eboot.bin, 19 MB) | byte-identical SHA256 |
| Image0 doubly-indirect file (archive.psarc, 237 MB) | byte-identical SHA256 |
| Full PKG extraction (488 files, ~12 GB) | completed with no errors |
| Passcode validation | `Passcode mismatch.` on official PKG, same as orbis |
| Unicode paths (full-width colon in filename) | **works** — orbis fails ("Could not open or read image file") |
| `img_verify` on a rebuilt PKG (Yooka, 2.6 GB) | **PASS** with expected-warning parity (R4211/R4124 appear on the original too) |
| `img_extract` of a rebuilt PKG (Yooka, 2.6 GB) | **byte-identical** to our extractor (1081/1081 files, 0 hash mismatches) |
| PFSC policy diff (rebuilt vs original) | **0 mismatches** across 1046 files |

## Reverse-engineering notes

See `REVERSE_ENGINEERING_NOTES.md` for the binary analysis (OpenSSL 1.0.2g
statically linked, command/option strings, entry formats) and the validated
format details — including the critical PS4 PFS AES-XTS
**little-endian-first GF(2^128) tweak advance** that differs from the
OpenSSL/mbedtls convention.

## Requirements

- .NET 10 SDK (build) / .NET 10 runtime (run)
- Windows — the CLI, core, and GUI all target `net10.0-windows`
- Optional: `zlib1.dll` for the 4 KiB deflate window (discovered at
  `%ORBISPKG_ZLIB%`, next to the exe, Git for Windows mingw64, or the PATH;
  SharpZipLib fallback when absent)
- Tests: `dotnet test OrbisPkgTool.Tests/OrbisPkgTool.Tests.csproj`
  (xUnit; the test project is not part of `OrbisPkgTool.slnx` and must be
  run directly)
