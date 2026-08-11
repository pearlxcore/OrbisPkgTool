# GP4 Sony Differential

Semantic comparison of OrbisPkgTool `gp4gen` output vs Sony `gengp4_app.exe`
(Age of Wonders: Planetfall, CUSA13236, 4,259 files).

## Hierarchy (canonical, fixed)

```xml
<psproject fmt="gp4" version="1000">
  <volume> ... </volume>     <!-- sibling -->
  <files> ... </files>       <!-- sibling -->
  <rootdir> ... </rootdir>   <!-- sibling -->
</psproject>
```

Fixed: `<files>` was previously nested INSIDE `<volume>`.

## psproject attributes

| attr | Sony | OrbisPkgTool |
|---|---|---|
| fmt | gp4 | gp4 |
| version | 1000 | 1000 |
| XML decl | 1.1 | 1.0 (accepted by both builders) |

## volume children

| element | Sony | OrbisPkgTool |
|---|---|---|
| volume_type | pkg_ps4_app | pkg_ps4_app |
| volume_id | PS4VOLUME | PS4VOLUME |
| volume_ts | present | present |
| package | attributes | attributes (matched) |
| chunk_info | 1 chunk, 1 scenario | matched |

## package attributes

| attr | Sony | OrbisPkgTool |
|---|---|---|
| content_id | EP4139-CUSA13236_00-AOWPLANETFALL000 | from param.sfo |
| passcode | 32 zeros | 32 zeros |
| storage_type | digital50 | digital50 (was digital25 — fixed) |
| app_type | full | full |
| version / title / title_id | absent | absent (matches) |

## file elements

| attr | Sony | OrbisPkgTool |
|---|---|---|
| targ_path | present | present |
| orig_path | present | present |
| pfs_compression | enable on CONTENT, disable on sce_sys/sce_module/eboot | enable/disable matched (was lost — fixed) |

File count: identical (all 4,259 paths).

## rootdir

Sony emits a nested `<dir targ_name=...>` tree. OrbisPkgTool previously emitted
`<rootdir />` empty — now builds the full tree from target paths.

## Roundtrip preservation

Parse(Sony GP4) → Serialize → Parse must preserve: volume type, content ID,
passcode, storage_type, app_type, all targ_path, all orig_path, all
pfs_compression, directory hierarchy, chunk/scenario config.

## Status

GP4 generation bugs fixed; builder-from-Sony-GP4 isolation test in progress.
