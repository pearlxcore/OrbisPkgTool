# QA + Release Hardening Complete — 14/14 Regression Tests Green

Follow-up to PROMPT_V11.md. The format core stayed FROZEN; the QA phase
added a permanent regression suite, structured validation, and pipeline
hardening. Pushed: `97dcfda` (core) → `becac26`, `e51a6f8`, `e92eef9`.

## The regression suite immediately caught 3 REAL defects

1. **PFSC block-table overflow.** dataOffset was hardcoded 0x10000, but the
   table (8 bytes/entry) exceeds 0xFC00 once the PFSC has >8063 blocks
   (>~528 MB). The table tail was overwritten by data. The 11.9 GB digi
   build was silently affected (orbis tolerates it for store-raw because it
   ignores the table, but compressed mode would break).
   FIX: `dataOffset = align(tableOffset + (blockCount+1)*8, 0x10000)` —
   small PFSCs stay exactly 0x10000 (byte-identical to orbis), large ones
   shift to 0x20000+.

2. **Encrypted-entry table flags.** npbind.dat was encrypted with dk[3] but
   the entry table wrote flags1=0 / flags2=0 — readers treated it as
   plaintext and returned raw ciphertext. FIX: flags1 |= 0x80000000 and
   flags2 = KeyIndex<<12 for every Encrypted entry.

3. **Entry table DataSize is the LOGICAL size.** Verified against the
   original Digimon: npbind.dat table entry = **532**, but the stored region
   is 544 bytes of real ciphertext (16-aligned, non-zero tail). Our builder
   stored 544 in the table (close but not identical) and our reader read
   only 532 bytes — truncating the last AES block (corrupt tail bytes).
   FIX: builder table DataSize = logical size, storage = aligned region,
   digests cover the aligned region; reader reads the aligned region and
   truncates to DataSize.

## Regression suite (OrbisPkgTool.Tests, xUnit, 14 tests)

Boundaries encoded: tiny FS (inner/FPT/dirents/PFSC/outer/PKG + exact
roundtrip) · compressed PFSC (48 89 header + RFC1950 + Adler32 + roundtrip) ·
raw PFSC (sectors at 0x10000) · multi-inode-table (400 files → 2 blocks, no
boundary straddle) · direct/contiguous (1/2/11/12/13 blocks, db[1]=-1
sentinel) · outer indirect (12/13/1832/1833 blocks) · duplicate-entry
prevention · AES alignment (532/544 + exact roundtrip) · FPT collision
resolver (synthetic `/AB` vs `/B#` hash collision, orbis-accepted) ·
>1 GB streaming (sparse) · validator invariants.

## Pipeline hardening

- `BuildOptions`: PfscMode (Store default / Compressed), Validate,
  ManifestPath (build.json: sizes, counts, inode table info, SHA256),
  Progress (long-based, per stage), CancellationToken.
- Disk-space preflight: `required ≈ 3.2× inner + output` vs
  `DriveInfo.AvailableFreeSpace`, abort before building.
- CLI: `--pfsc-mode store|compressed`, `--manifest file.json`, `--validate`,
  stage progress lines, Ctrl+C → clean temp cleanup (exit 130).
- `validate` command + `--validate`: 8-stage structured validation
  (header/entries → outer PFS → PFSC → inner PFS → digests → outer HMAC
  signatures → filesystem walk), fail-fast builder invariants.

## Status

| Area | Result |
|------|--------|
| Regression suite | 14/14 PASS |
| orbis-pub-cmd acceptance | still PASS (fresh builds re-verified) |
| FPT collision | PASS (orbis lists collided names) |
| Docs | docs/PKG_BUILDER_FORMAT_FINDINGS.md (Sony/OpenOrbis/Choice/Not-verified classification), docs/CONSOLE_VALIDATION_CHECKLIST.md |
| Console test | PENDING — the only remaining gate |

The tool is **PC/orbis-pub-cmd compatible**; it is NOT yet claimed
PS4-compatible (needs the jailbroken-PS4 install test per the checklist).
Do not reopen format questions unless a console/regression test proves a
defect.
