# OrbisPkgTool — Native C# Replacement for orbis-pub-cmd.exe

Pure managed C# (.NET 10, cross-platform) port of the two `orbis-pub-cmd.exe`
commands used by PS4 PKG tools: **`img_file_list`** and **`img_extract`**.

No P/Invoke, no native interop, no dependency on any existing PKG library —
everything (PKG parsing, RSA/AES/XTS crypto, PFS filesystem, PFSC decompression)
is implemented from scratch using `System.Security.Cryptography` and
`System.IO.Compression`.

## Projects

```
OrbisPkgTool/            class library (net10.0) — the replacement
  PkgReader.cs           public API: ListFiles / ExtractFile / ExtractEntryBytes / ExtractAll
  PkgFileEntry.cs        listing entry model
  Pkg/PkgHeader.cs       PKG header (big-endian)
  Pkg/PkgEntry.cs        entry table + well-known entry IDs/names
  Pkg/PkgBuilder.cs      PKG builder (in-memory + streaming) with per-file
                         compression policy replay (pfs_compression)
  Pkg/BuildOptions.cs    build options (PfscMode.Compressed is the default)
  Crypto/PkgCrypto.cs    passcode → derived keys, AES-128-CBC entry decryption, RSA, EKPFS
  Crypto/PkgKeySet.cs    embedded RSA keys (leaked dk3 private key + fake keyset)
  Gp4/Gp4Project.cs      GP4 parse/serialize/generate (pfs_compression-aware)
  Pfs/PfsReader.cs       PFS filesystem: inodes, dirents, XTS, indirection, contiguous runs
  Pfs/PfsWriter.cs       inner/outer PFS writer + per-file block allocation manifest
  Pfs/PFSCStream.cs      PFSC (zlib-compressed inner image) decompression
  Pfs/PFSCWriter.cs      PFSC writer: per-file raw/compressed policy, both paths
  Pfs/PfscDeflate.cs     zlib 4 KiB-window deflate (zlib1.dll, SharpZipLib fallback)
  Pfs/PfscProfiler.cs    original-package compression-policy inspector → GP4 profile
OrbisPkgTool.Cli/        console harness — drop-in orbis-pub-cmd command syntax
```

## Usage

```
OrbisPkgTool.Cli img_file_list [--passcode <32ch>] [--oformat [short|long]+[original_size|packed_size]] <pkg>
OrbisPkgTool.Cli img_extract   [--passcode <32ch>] <pkg>[:<entry_path>] <out_dir>
OrbisPkgTool.Cli pkginfo       <pkg>   (title/id/type — DLC vs Theme vs Avatar vs Wallpaper)
OrbisPkgTool.Cli verify        <pkg>   (SHA256 digest-table integrity check)
OrbisPkgTool.Cli bench         <pkg>   (listing performance)
OrbisPkgTool.Cli selftest      (validates the embedded RSA key constants)
OrbisPkgTool.Cli inspect <pkg> (diagnostics: header, EKPFS, PFS structure)
OrbisPkgTool.Cli xtstest <pkg> (XTS parameter diagnostics)
OrbisPkgTool.Cli sweep <dir> [--out report.tsv] [--list]
                                (test every *.pkg in a folder tree; writes a TSV report)
```

Default passcode: `00000000000000000000000000000000` (same as the native tool's
default). A wrong passcode is rejected with `Passcode mismatch.` — identical
behavior to orbis-pub-cmd.

**Official/retail PKGs**: when the passcode does not match, the library
automatically RSA-recovers `dk3` from ENTRY_KEYS[3] with the leaked key-3
private key (the same path scene tools use) and decrypts key-index-3 Sc0
entries (npbind.dat, license.dat, …) without the passcode.

**Add-on type detection** (`pkginfo`): distinguishes DLC from Theme/Avatar/
Wallpaper using the param.sfo CATEGORY, the content-id prefix (IP9100 = theme)
and the title.

```csharp
using var pkg = new PkgReader(@"C:\Games\game.pkg");          // passcode optional
foreach (var f in pkg.ListFiles())
    Console.WriteLine($"{(f.IsDirectory ? 'D' : 'F')} {f.Size} {f.Path}");
pkg.ExtractFile("Sc0/changeinfo/changeinfo.xml", outDir);
pkg.ExtractAll(outDir);                                       // full PKG extraction
```

## Validation (vs the real orbis-pub-cmd.exe)

Tested on a 7.3 GB fake game PKG (Digimon World Next Order, CUSA05392) and an
official patch PKG (Disgaea 1 Complete):

| Check | Result |
|---|---|
| `img_file_list` paths (757 entries) | **100 % identical** (zero diff) |
| `img_file_list` file sizes (488 files) | **100 % identical** (zero diff) |
| Sc0 entry extraction (param.sfo) | byte-identical SHA256 |
| Image0 single-block file (.pos) | byte-identical SHA256 |
| Image0 multi-block file (eboot.bin, 19 MB) | byte-identical SHA256 |
| Image0 doubly-indirect file (archive.psarc, 237 MB) | byte-identical SHA256 |
| Full PKG extraction (488 files, ~12 GB) | completed with no errors |
| Passcode validation | `Passcode mismatch.` on official PKG, same as orbis |
| Unicode paths (full-width colon in filename) | **works** — orbis fails ("Could not open or read image file") |

## Reverse-engineering notes

See `REVERSE_ENGINEERING_NOTES.md` for the binary analysis (OpenSSL 1.0.2g
statically linked, command/option strings, entry formats) and the validated
format details — including the critical PS4 PFS AES-XTS **little-endian-first
GF(2^128) tweak advance** that differs from the OpenSSL/mbedtls convention.

## Compression policy replay (repack parity)

`repack` now replays the ORIGINAL package's per-file compression decisions:
during extraction it profiles the original's PFSC block table + inner-PFS
inode allocation (`PfscProfiler`), stamps the resulting enable/disable policy
into the generated GP4 (`pfs_compression` attributes — the Sony-native
mechanism, so the GP4 stays cross-buildable by LibOrbisPkg/orbis-pub-cmd),
and the builder stores every block of a "disable" file RAW while compressing
everything else. See `docs/PFSC_COMPRESSION_POLICY.md` for the design,
verification results and the original's zero-fill ownership analysis.

```
OrbisPkgTool.Cli repack game.pkg                 # policy replay is automatic
OrbisPkgTool.Cli pfscprofile game.pkg --ref original.pkg   # policy diff
```

PFSC compressed blocks use a 4 KiB deflate window (zlib1.dll
`deflateInit2`, windowBits=-12) — formally valid for the declared
`0x48 0x89` zlib header — with a SharpZipLib fallback when zlib is absent.
