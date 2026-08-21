# OrbisPkgTool

C#/.NET 10 tool for working with PS4 fake PKGs (FPKGs).

It can read, extract, validate, build, repack, and merge packages without needing `orbis-pub-cmd.exe` or LibOrbisPkg at runtime.

PKG, PFS, PFSC, crypto, and compression support are implemented in the project. `zlib1.dll` is optional and is used for PFSC compression when available. SharpZipLib is used as a fallback.

The project also includes:

- ATRAC9 `.at9` to WAV decoding
- PSN official update checking
- PKG header and entry inspection tools
- GP4 generation
- trophy TRP tools
- param.sfo tools
- a WinForms GUI

The compatibility code has been tested against real FPKGs and `orbis-pub-cmd 3.87`. The current test suite has 83 xUnit tests.

## Projects

```text
OrbisPkgTool/                        class library (net10.0-windows) + CLI
                                     entry point (Program.cs)
  PkgReader.cs                       ListFiles / ExtractFile /
                                     ExtractEntryBytes / ExtractAll /
                                     OpenRawPfscStream / InnerPfs
  PkgFileEntry.cs                    file listing model
  PkgInfo.cs                         package metadata
  Pkg/PkgHeader.cs                   PKG header
  Pkg/PkgEntry.cs                    entry table and known entry IDs/names
  Pkg/PkgBuilder.cs                  in-memory and streaming PKG builder
  Pkg/BuildOptions.cs                build options
  Pkg/PkgValidator.cs                structural validation
  Pkg/PkgHeaderDump.cs               46-row header dump for A/B comparison
  Crypto/PkgCrypto.cs                passcode and package crypto
  Crypto/PkgKeySet.cs                RSA/fake PKG key material
  Gp4/Gp4Project.cs                  GP4 parse, serialize, and generation
  Media/                             ATRAC9 decoder
  Psn/UpdateCheck.cs                 PSN update checking
  Pfs/PfsFormat.cs                   PFS format constants
  Pfs/PfsReader.cs                   PFS reader
  Pfs/PfsWriter.cs                   PFS writer
  Pfs/PFSCStream.cs                  PFSC decompression
  Pfs/PFSCWriter.cs                  PFSC writer
  Pfs/PfscDeflate.cs                 PFSC deflate support
  Pfs/PfscProfiler.cs                compression policy profiler
  Sfo/ParamSfo.cs                    param.sfo read/serialize
  Trp/Trp.cs                         trophy TRP read/create
  Util/MersenneTwister.cs            MT19937 used by package crypto

OrbisPkgTool.Gui/                    Windows Forms GUI
OrbisPkgTool.Tests/                  xUnit tests
```

## Commands

```text
list        List files in a PKG
extract     Extract files from a PKG
verify      Verify PKG hashes/signatures
info        Show PKG metadata
inspect     Dump the PFS tree
entries     Dump the raw PKG entry table
validate    Run structural validation
build       Build a fake PKG from GP4
orbis-build Build a fake PKG using orbis-pub-cmd
repack      Extract, restructure, generate GP4, and rebuild
merge       Merge a base PKG and update PKG
gp4gen      Generate GP4 from a folder
restructure Prepare a dump for building
sweep       Batch verify PKGs in a folder
bench       Benchmark listing speed
selftest    Validate RSA keys
sfo         param.sfo tools
trp         Trophy TRP tools
```

Use `-h` on commands that have extra options.

### Diagnostic commands

```text
pfscprofile    PFSC compression stats and per-file policy
shadps4diag    Step through shadPS4-style PKG reading
s4trace        shadPS4Plus allocation/bounds trace
s4extract      shadPS4Plus-style extraction
s4crypto       shadPS4 crypto chain checks
pkgfields      Dump PKG header/entry fields for comparison

signverify
pfsdump
pfsblock
innerfpt
iblock
fixdigests
resignpfs
xtstest
buildtest
emptypayload
pfscompare
dumppfsc
xtsdump
inflatecheck
deftest
blkcount
```

## Examples

### List files

```powershell
OrbisPkgTool.exe list game.pkg
```

### Extract a package

```powershell
OrbisPkgTool.exe extract --passcode 00000000000000000000000000000000 game.pkg out/
```

Unicode paths are handled by the application. If launching from another app, use `ProcessStartInfo` instead of going through `cmd.exe`.

### Extract a single file

```powershell
OrbisPkgTool.exe extract game.pkg:Sc0/param.sfo out/
OrbisPkgTool.exe extract game.pkg:Image0/eboot.bin out/
```

### Validate a package

```powershell
OrbisPkgTool.exe validate --passcode 00000000000000000000000000000000 game.pkg
```

For scene FPKGs with bad or leftover entry digests/signatures:

```powershell
OrbisPkgTool.exe validate --fake-tolerant game.pkg
```

`--fake-tolerant` skips the checks that commonly fail on repacked scene packages while still running the other validation stages.

### Generate a GP4

```powershell
OrbisPkgTool.exe gp4gen ./Image0 --title "My Game" --title-id CUSA00001 --out game.gp4
```

### Build a fake PKG

```powershell
OrbisPkgTool.exe build game.gp4 ./Image0 --out game.pkg --passcode 00000000000000000000000000000000 --validate
```

The builder uses the `pfs_compression` values from the GP4.

### Repack a package

```powershell
OrbisPkgTool.exe repack original.pkg --out rebuilt.pkg
```

This extracts the package, restructures the files, generates a GP4, and builds a new package.

The original per-file PFSC compression policy is also copied into the generated GP4.

### Merge a base and update

```powershell
OrbisPkgTool.exe merge "Game [CUSA00001] 00 - Base.pkg" "Game [CUSA00001] 01 - Update v01.09.pkg" --out merged.pkg --validate
```

`merge` extracts the base and update, overlays the update files on the base, and rebuilds them as one base-app PKG.

The base `CATEGORY=gd` is kept. `APP_VER` and `VERSION` are taken from the update. The output uses the default passcode.

### Check PFSC compression policy

```powershell
OrbisPkgTool.exe pfscprofile original.pkg --out pfsc_profile.json
OrbisPkgTool.exe pfscprofile rebuilt.pkg --ref original.pkg
```

The saved JSON can also be used with `gp4gen --pfsc-profile`.

## Passcode

Default passcode:

```text
00000000000000000000000000000000
```

If the passcode is wrong:

```text
Passcode mismatch.
```

For official/retail PKGs, the library can recover `dk3` from `ENTRY_KEYS[3]` using the included key material. This allows supported key-index-3 Sc0 entries such as `npbind.dat` and `license.dat` to be decrypted without the package passcode.

## C# API

```csharp
using var pkg = new PkgReader(@"C:\Games\game.pkg");

foreach (var f in pkg.ListFiles())
{
    Console.WriteLine($"{(f.IsDirectory ? 'D' : 'F')} {f.Size} {f.Path}");
}

pkg.ExtractFile("Sc0/changeinfo/changeinfo.xml", outDir);
pkg.ExtractAll(outDir);

byte[] sfo = pkg.ExtractEntryBytes(0x00001000);

PkgBuilder.Build(
    "game.gp4",
    "./Image0",
    "game.pkg",
    new BuildOptions
    {
        PfscMode = PfscMode.Compressed,
        Validate = true
    });
```

## Compression policy replay

When repacking, OrbisPkgTool reads the original package's per-file PFSC compression settings and writes them into the generated GP4 as `pfs_compression` values.

`PfscProfiler` gets this information from the PFSC block table and inner PFS allocation data.

Files with compression disabled are stored as raw PFSC blocks. Other files are compressed normally.

The in-memory and streaming build paths use the same policy.

PFSC compression uses a 4 KiB deflate window. If `zlib1.dll` is available it is used with `windowBits=-12`. Otherwise SharpZipLib is used.

## Validation

The tool has been compared with `orbis-pub-cmd 3.87` using several packages, including:

- Digimon World: Next Order (`CUSA05392`)
- an official Disgaea 1 Complete patch
- Yooka-Laylee and the Impossible Lair (`CUSA16139`)

Current results:

| Check | Result |
|---|---|
| `img_file_list` paths, Digimon, 757 entries | no differences |
| `img_file_list` file sizes, Digimon, 488 files | no differences |
| Sc0 `param.sfo` extraction | same SHA256 |
| Image0 single-block file | same SHA256 |
| Image0 multi-block `eboot.bin` | same SHA256 |
| Image0 doubly-indirect `archive.psarc` | same SHA256 |
| Full Digimon extraction, 488 files / about 12 GB | completed without errors |
| Wrong passcode handling | same `Passcode mismatch.` result |
| Unicode filename handling | works in OrbisPkgTool where the tested Sony tool failed |
| `img_verify` on rebuilt Yooka package | passes with the same expected R4211/R4124 warnings as the original |
| Rebuilt Yooka extraction | 1081/1081 files matched |
| PFSC policy comparison | 0 mismatches across 1046 files |

These are PC-side checks. Actual console testing is still separate.

## Requirements

- .NET 10 SDK for building
- .NET 10 runtime for running
- Windows
- optional `zlib1.dll` for PFSC compression
- xUnit for the test project

Run tests with:

```powershell
dotnet test OrbisPkgTool.Tests/OrbisPkgTool.Tests.csproj
```

The test project is not included in `OrbisPkgTool.slnx`, so run it directly.

`zlib1.dll` is searched for in:

- `%ORBISPKG_ZLIB%`
- next to the executable
- Git for Windows `mingw64` locations
- `PATH`

If it is not found, SharpZipLib is used.
