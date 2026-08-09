I'm building a PS4 fake PKG tool in C#. My PFS writer (BuildInnerPfs/BuildOuterPfs) produces PFS files that orbis-pub-cmd rejects. I've narrowed the bug to the inner PFS format. Here's what I understand about the correct build process and where my tool differs.

## Current Problem (Brief)

**Symptom:** PKGs built by our C# tool are rejected by orbis-pub-cmd. orbis shows `Image0` directory but lists ZERO files under it. orbis verify says "Sc0 directory not found" / "Directory parse error." Cross-injection test: orbis outer PFS + our PKG header = orbis lists Image0 files ✅. Our outer PFS + our header = orbis rejects ❌. **Bug is in our PFS writer, specifically BuildInnerPfs.**

**What we've ruled out (across 8+ hours of debugging):**
- PKG header assembly — confirmed correct
- Outer PFS header/structure — byte-level match with orbis
- GP4 format/parsing — handles both attribute and child-element formats
- param.sfo — 28 keys, correct format, matches Digimon reference
- File path handling — Image0/ prefix stripped, Sc0 files separated
- Dirent format — null terminators, alignment
- ndinodeblock — fixed (was 2, now 1)
- Block signing/signatures — computed correctly

**Remaining difference:** Our inner PFS has 7 inodes vs orbis's 9. Different nlink counts. Different FPT entry count. Same PFS header, same mode, same superroot dirents.

## The Working Pipeline (orbis-pub-cmd method)

### 1. Folder Structure
The PS4 game dump has TWO folders at root:
```
Dump/
├── Image0/          ← Main game files
│   ├── eboot.bin
│   ├── Media/       ← Game assets
│   ├── sce_module/  ← PRX modules
│   └── ...
└── Sc0/             ← Package metadata
    ├── param.sfo
    ├── icon0.png
    ├── pic0.png
    ├── trophy/trophy00.trp
    ├── license.dat / license.info
    └── playgo-chunk.dat / playgo-chunk.sha / playgo-manifest.xml
```

### 2. Restructure (before building)
```
Move:  Sc0/* → Image0/sce_sys/
Delete: Sc0/ folder
Delete: playgo-chunk.dat, playgo-chunk.sha, playgo-manifest.xml from Image0/sce_sys/
Delete: param.sfo.original (backup)
```
Result:
```
Dump/Image0/
├── eboot.bin
├── Media/
├── sce_module/
└── sce_sys/         ← was Sc0/
    ├── param.sfo
    ├── icon0.png
    ├── trophy/
    └── ... (NO playgo files!)
```

### 3. GP4 Generation
```
gengp4_app.exe Dump/Image0/ → project.gp4
```
The GP4 lists ALL files in Image0/ with paths relative to Image0/. Files in `sce_sys/` become Sc0 PKG entries. Everything else goes into the inner PFS.

### 4. GP4 Fix (gengp4 bug)
gengp4 sometimes writes `default_id="1"` and `<scenario id="1">` — must be changed to `"0"`:
```powershell
(gc project.gp4) -replace 'default_id="1"','default_id="0"' -replace '<scenario id="1"','<scenario id="0"' | sc project.gp4
```

### 5. Build
```
orbis-pub-cmd.exe img_create project.gp4 output.pkg
```
This builds the inner PFS, wraps in PFSC, builds outer PFS, assembles PKG header. orbis generates PlayGo files fresh during build.

## My Tool's Build Flow

My `PkgBuilder.Build()` does:
1. Parse GP4 → get file list
2. Separate: `sce_sys/*` → Sc0 entries, everything else → inner PFS files
3. `PfsWriter.BuildInnerPfs(files)` → inner PFS binary
4. `PFSCWriter.Build(inner)` → zlib-compressed PFSC
5. `PfsWriter.BuildOuterPfs(pfsc)` → outer PFS with XTS-encryption + signing
6. `Assemble()` → PKG header + entry table + digests + RSA signature

## What's Confirmed Working
- **PKG header assembly**: ✅ Proven — inject orbis PFS into our header, orbis lists all Image0 files
- **Outer PFS header**: ✅ Byte-identical to orbis (except ndblock/size)
- **Outer PFS inode table**: ✅ Same structure (S32, same flags, same mode)
- **Sc0/Image0 separation**: ✅ Files under `sce_sys/` go to PKG entries, rest to inner PFS
- **Our reader**: ✅ Reads all PKGs correctly (list, extract, verify)
- **Small PKGs** (≤12 data blocks, no indirect): Build correctly, our reader mounts them

## What's Broken
- orbis-pub-cmd shows `Image0` directory but NO files when mounting our inner PFS
- orbis verify says "Sc0 directory not found" / "Directory parse error"

## Inner PFS Differences I've Found
Comparing orbis inner PFS vs our inner PFS for same files:

| Field | orbis | Ours |
|-------|-------|------|
| ndinode | 9 | 7 |
| ndblock | 62 | 10 |
| ino[2] nlink (uroot) | 4 | 2 |
| ino[3] nlink | 3 | 1 |
| FPT size | 48 bytes | 32 bytes |
| ino[1] flags | 0x00020010 | 0x00020010 (same) |
| mode on all inodes | 0x416D / 0x816D | Same |

The PFS superroot dirents (block 2) and FPT (block 3) hex dumps are IDENTICAL between ours and orbis.

## Specific Questions
1. Does orbis validate `nlink` counts strictly? If ino[2] says nlink=2 but orbis expects nlink=4 (matching number of subdirectories), would that cause "Directory parse error"?
2. Does the inner PFS inode table need to include additional system inodes (like a separate inode for each directory level) that we're not creating?
3. Does the FPT need entries for directories, not just files? orbis has FPT size=48 (4 entries), we have 32 (maybe 2-3 entries).
4. Are there any other inner PFS fields that orbis validates strictly that I might be missing?

## Relevant Code
My `BuildInnerPfs` is in `PfsWriter.cs` (~300 lines). It creates D32-mode inner PFS with inodes for superroot, flat_path_table, uroot, and one inode per file/directory. Inode flags follow the real PKG convention (0x00020010 for system inodes, 0x00000010 for user directories).

The outer PFS wrapping is in `BuildOuterPfs` — confirmed correct by cross-injection test.
