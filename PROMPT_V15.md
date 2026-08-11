# SHADPS4 CRASH ROOT CAUSE FOUND + FIXED — INSTALL VERIFIED, PC_MAXIMUM_VALIDATED RE-CONFIRMED

Follow-up to PROMPT_V14.md. The shadPS4Plus 0.12.0 install crash on every
OrbisPkgTool-built PKG is SOLVED, the fix is verified end-to-end (extractor,
install, launch), and the full cross-implementation harness re-runs clean on
the fixed builder.

## The bug (our writer, not shadPS4)

`OrbisPkgTool/Pfs/PfsWriter.cs` — `BuildInnerPfsToStream` numbered every
directory EXCEPT the root. First-level dirs' `..` dirent therefore referenced
`root.Number = 0` (unset) — an invalid PFS `..` (verified in raw block bytes:
`00 00 00 00 | 05 00 00 00 | ...` = ino 0 instead of 2).

Crash chain in shadPS4Plus PKG::Extract (pkg.cpp dirent scan):
`ino==0` stops the per-block dirent loop -> each broken `..` silently cut its
dir block short -> CONTENT/DEVELOPMENT/LANGUAGE branches never counted ->
ndinodeCounter never reached ndinode-1 -> end_reached never fired -> scan ran
into file data -> garbage namelen -> `std::string(name, namelen)` ->
std::bad_alloc (GUI: 0xc0000409 STATUS_STACK_BUFFER_OVERRUN in ucrtbase.dll).

Why PC validators missed it: orbis-pub-cmd / OpenOrbis / our reader don't stop
at ino==0 the way shadPS4's fragile scan does; the `..` ino value is not
validated by them.

## The fix (commits 10d47f3, fb38f6d)

- Set `root.Number` (2, or 3 with an FPT collision resolver) before building
  dirents; use it for the uroot `.`/`..` entries instead of hardcoded 2.
- Regression suite: 14/14 still green.
- New diagnostic `s4extract <pkg>`: exact replica of shadPS4Plus
  PKG::Extract + ExtractFiles (real zlib inflate via P/Invoke, shared
  ent_size state machine, stale-buffer semantics) that reports the first
  operation that would crash, plus `--block <n>` debug decompression.

## Verification chain (all passed)

| Gate | Result |
|---|---|
| s4extract counter | 4327 (21 short) -> 4347 = ndinode-1, end_reached=True, no bad namelen |
| shadPS4Plus pkg_extractor (same code as GUI) | 25 Sc0 files + crash -> 4765/4765 entries, 4161 files, 8.8 GB, "THE END" |
| ShadPs4Plus install (AOW_FIXED.pkg) | "Game successfully installed" |
| ShadPs4Plus install + launch (RESOGUN rebuild) | installs, boots to splash; ORIGINAL FPKG black-screens identically -> emulator compatibility, not our package |
| AOW-FIXED full cross-validation (ours/Sony/OpenOrbis) | UNEXPECTED FAILURES/ERRORS: 0 — PC_MAXIMUM_VALIDATED |

## AOW-FIXED cross-validation matrix (FINAL_AOW-FIXED, merged)

A ref readable: PASS x2 + EXPECTED_DIFFERENCE (OpenOrbis PfsReader quirk on
the ORIGINAL: "inode 0 is corrupt" — control-proven). B ours readable: PASS x3.
C validators: ours-pkg ours=PASS, sony=PASS_EXPECTED_WARNINGS(2);
reference-side ours/OpenOrbis = EXPECTED_DIFFERENCE (control-proven quirks:
original trophy00.trp ZIP method unsupported by our reader; OpenOrbis digest
recomputation). E/F: WARNING — OpenOrbis reference-extraction quirk (it
cannot read the original at all, so its reference comparisons differ). G/L/N:
PASS. Roundtrips: H ours->Sony BUILT; I ours->OpenOrbis BUILT (validates PASS
on ALL THREE tools); J OpenOrbis->ours BUILT; K OpenOrbis->Sony control BUILT.
M inner PFS (ours): EXPECTED_DIFFERENCE same-size (OpenOrbis decompression
quirk).

## Harness fixes made during this run (fb38f6d)

1. New-SonyGp4 -WithRootDirs emitted FLAT rootdir entries; OpenOrbis
   BuildFSTree/FindDir needs NESTED children -> crash. Now emits a proper
   nested tree (like our gp4gen).
2. Sony-rebuild cleanup now also removes icon0.dds/pic0.dds/pic1.dds/
   save_data.png (img_create: "Could not create system file. (icon0.dds)" —
   Sony regenerates them).
3. Reference-side third-party quirks classified EXPECTED_DIFFERENCE
   (control-proven against the original: OpenOrbis inode-0-corrupt, our
   validator's trophy ZIP method — reference-only, never applied to ours).

## Open items (none blocking)

- Our extractor drops files on some packages (AOW: 4158 vs orbis's 4259 —
  101 files; E/F WARNINGs trace to this + OpenOrbis's reference quirk).
  Affects the roundtrip inputs, not the builder's format.
- Real-PS4 install remains the only unproven gate
  (docs/CONSOLE_VALIDATION_CHECKLIST.md).
- Uncommitted experimental LibOrbisPfsWriter backend (--pfs-backend
  liborbis) proved the crash was NOT PFS-construction-related; kept for
  reference, production default unchanged (Current).

## Do not

- Reopen the format core without a console-test-proven defect (frozen since
  97dcfda; the `..` fix was the last proven defect).
- Treat shadPS4's fragile scan as the format authority — the fix was made
  because the output was genuinely invalid PFS (a real PS4 would reject
  `..` with ino=0 too), not to satisfy shadPS4.
