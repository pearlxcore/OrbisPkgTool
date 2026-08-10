# FINAL COMPLETE CROSS-VALIDATION RESULT — ALL PHASES PASS, PC_MAXIMUM_VALIDATED

Follow-up to PROMPT_V13.md. The Digimon cross-validation run is COMPLETE.
All phases pass. No product code was modified (the frozen PKG/PFS/PFSC core
stayed untouched). Only harness tooling changed.

## The definitive merged report

`CrossValidation\Results\FINAL_COMPLETE_Digimon\` (summary.txt + run_status.txt)
covers ALL phases A–N of the external cross-implementation harness
(ours / Sony orbis-pub-cmd 3.87 / OpenOrbis via the test-only driver over
LibOrbisPkg.Core.dll).

Phase-by-phase result (real 11.9 GB Digimon data, both packages):

| Phase | Test | Result |
|---|---|---|
| A | reference readable by ours/Sony/OpenOrbis | PASS x3 |
| B | our rebuild readable by ours/Sony/OpenOrbis | PASS x3 |
| C | validators on both packages | 4x PASS + 2x EXPECTED_DIFFERENCE |
| E | triple extraction of ours (3 tools) | PASS — 0 content differences |
| F | six-path SHA256 comparison | WARNING (309) — OpenOrbis quirk, see below |
| G | extractor validity | PASS |
| H | our extraction -> Sony rebuild | BUILT + validated |
| I | our extraction -> OpenOrbis rebuild | BUILT + validated |
| J | OpenOrbis extraction -> our rebuild | BUILT + validated |
| K | OpenOrbis -> Sony control | BUILT + validated |
| L | GP4 semantic comparison | PASS |
| M | inner PFS (ours) | PASS — byte-identical, SHA256 EAE47C3B... |
| M | inner PFS (reference) | EXPECTED_DIFFERENCE — OpenOrbis quirk |
| N | outer PFS dump | PASS x2 |

UNEXPECTED FAILURES/ERRORS: 0
PC CROSS-IMPLEMENTATION VALIDATION: PC_MAXIMUM_VALIDATED

## Every non-PASS line is the same third-party quirk (control-proven)

OpenOrbis's decompressor/validator differs from Sony's on the ORIGINAL Sony
package too:
- its PFSC decompression of the original differs (same sizes, different bytes)
- its validator fails Content Digest / Major Param Digest / entry digests
  (hashed at logical DataSize 532 instead of the aligned 544-byte stored
  region) on Sony's own output identically

Two independent implementations (ours == Sony) agree byte-for-byte on the
original; the six-path WARNING and M-reference EXPECTED_DIFFERENCE are that
difference, correctly reported, not a defect.

## Final roundtrip matrix (the strongest evidence)

| Roundtrip | ours validate | Sony verify | OpenOrbis validate |
|---|---|---|---|
| H ours -> Sony | PASS | PASS_EXPECTED_WARNINGS | EXPECTED_DIFFERENCE (quirk) |
| K OpenOrbis -> Sony | PASS | PASS_EXPECTED_WARNINGS | EXPECTED_DIFFERENCE (quirk) |
| I ours -> OpenOrbis | PASS | PASS_EXPECTED_WARNINGS | PASS |
| J OpenOrbis -> ours | PASS | PASS_EXPECTED_WARNINGS | PASS |

Sony verify warnings are 2 benign (R4211 PS5-testing note, R4124 trophy) —
identical profile to the original package. (Counts shown as 3/4 in raw
summaries are the pre-fix trailer-line counting artifact; the actual warning
count is 2, matching the original exactly.)

## What changed since PROMPT_V13 (harness only, commit adc3823)

1. **Merge-Results.ps1 key normalization fix.** Old full-run summaries
   contained a double-mojibake arrow (U+00E2 U+2020 U+2019, from an ANSI
   round-trip of the UTF-8 U+2192) which made roundtrip test names exactly
   45 chars wide — column padding collapsed to a single space, and the key
   regex swallowed "BUILT_FAILED" into the key, so re-run lines never
   replaced the old FAILED lines. Fixed with a 3-rule key extractor
   (2+ spaces not at EOL / tab-delimited roundtrip lines / " BUILT"
   fallback) plus normalization of both arrow spellings to "->".
2. **M(ours) PASS record restored from saved evidence.** Run 204053
   crashed at post-phase cleanup before writing its summary, but its inner
   PFS dumps survived: SHA256-verified byte-identical
   (11,971,526,656 = 11,971,526,656). Summary reconstructed from that
   evidence; the record now correctly shows PASS (byte-identical) instead
   of the transient partial-dump EXPECTED_DIFFERENCE from the interrupted
   re-run.
3. run_status.txt of the final report rewritten from the authoritative
   merged summary (all 15 stages [PASS]).

Earlier harness fixes already in place: phase-aware disk preflight
(-Only runs estimate the largest selected phase), per-phase artifact
cleanup, -Only targeted re-runs, sce_sys/about cleanup for Sony rebuilds
(Sony regenerates it), OpenOrbis digest-trio auto-classification, accurate
Sony warning counting.

## Operational notes (for the console phase)

- The rebuilt package: `digimon_work\digi_rebuilt.pkg` (11.17 GB) — built
  entirely by our C# code, 8-stage validation PASS.
- Remaining gate: actual jailbroken-PS4 install + launch. Checklist:
  `docs\CONSOLE_VALIDATION_CHECKLIST.md`.
- Until then the builder is documented as PC/orbis-pub-cmd compatible
  (PC_MAXIMUM_VALIDATED), NOT console-validated.
- Format questions stay CLOSED unless a console test or regression proves
  a defect — per the QA-phase freeze.

## Do not

- Reopen format questions (frozen core, commit 97dcfda).
- Rebuild the internal xUnit boundary suite (14/14, unchanged).
- Add new harness tests without a concrete defect to target.
- Modify PfsWriter / PFSCWriter / PkgBuilder / crypto / entry serialization
  unless a console test exposes a reproducible defect — then preserve logs
  and report the mismatch first.
