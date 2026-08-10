# FINAL VALIDATION RESULT — Pure C# PKG Builder Accepted by orbis-pub-cmd

Follow-up to PROMPT_V10.md. The goal is achieved and committed
(`97dcfda`, pushed to github.com/pearlxcore/OrbisPkgTool).

## The acceptance test (real game, not a fixture)
A complete Digimon World: Next Order FPKG (11.9 GB inner PFS, 485 files)
was rebuilt from an extracted dump using ONLY our C# code (no
orbis-pub-cmd anywhere in the build):

```
Build (pure C#):             11.166 GB in ~11 min  ✅
orbis img_file_list:         FULL Image0 tree listed (all 485 files)  ✅
orbis img_verify:             2 warnings — IDENTICAL to the original ✅
our verify:                   Integrity OK (26 entries)               ✅
```

## Key result: verification profile matches the original exactly
The rebuilt PKG produces the SAME orbis img_verify warnings as the
original Digimon FPKG:
- `[Warn] needs to be tested on PlayStation(R)5 system (TRC R4211)`
- `[Warn] full app without trophy pack file (TRC R4124)` — the original
  gets this too despite having trophy00.trp (it's a checker quirk, not
  a package defect)
2 warnings on BOTH. Our param.sfo + entry set is behaviorally identical
to the original (26 entries, same IDs/sizes).

## Entry parity confirmed
Rebuilt Sc0 = original Sc0 (26 entries): license.dat/info, nptitle.dat,
npbind.dat, psreserved.dat, param.sfo, playgo-chunk.dat/sha/manifest,
pronunciation.xml/sig, pic1.png (0x1006!), shareparam.json,
shareoverlayimage.png, icon0.png, pic0.png, icon0.dds, pic0.dds,
pic1.dds, trophy/trophy00.trp. keystone lives in the inner PFS on both.

## What was the last fix (after V10's dupe-ID finding was applied)
Nothing further was needed: the duplicate-entry-ID fix (replace fixed
placeholders with real sce_sys content instead of adding a second entry)
was the final blocker. v8 rebuild → PASS.

## Operational notes for large games
- Peak disk need ≈ 3.2× inner-PFS size during build (inner + PFSC +
  outer temp files) + output; the temp dir is auto-cleaned (also on
  failure via finally; force-killing the process leaks it).
- orbis-pub-cmd 3.87 cannot open paths containing the full-width colon
  (U+FF1A) — use ASCII paths when testing PKGs directly.
- Build time ≈ 11 min for 11.4 GB output; all 64-bit/streamed, no
  byte[] over 2 GB anywhere.

## Remaining (not code)
1. Install-on-console test (jailbroken PS4) — the final proof.
2. Optionally switch default PFSC mode from store-raw to compressed
   (compressed is proven — PROMPT_V8/V10 — but store-raw stays the
   stable default until console testing).
3. 50-150 GB PKGs: same code paths (multi-block inode tables, streaming,
   doubly-indirect outer) — already proven at 11.9 GB.
