# CrossValidation — External Cross-Implementation Validation Harness

The strongest PC-side compatibility gate for OrbisPkgTool before console
testing: the same packages are read, listed, validated, extracted, and rebuilt
by **three independent implementations**:

| Participant | Tool | Notes |
|---|---|---|
| A | OrbisPkgTool (this repo) | `OrbisPkgTool.Cli.exe` |
| B | Sony orbis-pub-cmd 3.87 | `orbis-pub-cmd.exe` (CyB1K patched build) |
| C | OpenOrbis / LibOrbisPkg (current fork) | `OpenOrbisDriver.exe` — a small test-only driver over the OpenOrbis `LibOrbisPkg.Core.dll` (local copy in `lib/`; `lib/*.dll` is gitignored — copy it from `PS4PKGTool\Library.Core\LibOrbisPkg.Core.dll` or build from the OpenOrbis repo) |

Nothing here modifies the production builder. The format core stays frozen;
if a test exposes a reproducible defect, preserve the logs and report the
exact mismatch — do not silently patch production code.

## Setup

1. Copy `config.example.json` → `config.json` and edit the tool paths, the
   reference package, your rebuilt package, and the label.
2. Build the driver once:
   ```powershell
   dotnet build .\CrossValidation\OpenOrbisDriver -c Debug
   ```
3. Build OrbisPkgTool (Debug) if the configured exe path is stale.

## Run

```powershell
# Quick — environment, capabilities, baseline, three readers, three
# validators, entry comparison, file-list comparison (no big extractions):
.\CrossValidation\Run-QuickValidation.ps1

# Full — everything plus triple extraction, six-path content hashes,
# extractor validity, roundtrips (ours→Sony, ours→OpenOrbis,
# OpenOrbis→ours, OpenOrbis→Sony control), GP4 semantics, inner/outer PFS:
.\CrossValidation\Run-FullValidation.ps1
```

Each run creates a unique timestamped directory under `Results/`:

```
Results/20260810_150000_Digimon/
├── 00_environment.txt
├── capabilities.txt
├── summary.txt
├── OrbisPkgTool/ Sony/ OpenOrbis/
├── Manifests/ Comparisons/ GP4/ InnerPfs/ OuterPfs/ RoundTrips/
```

Every external command is logged with the command, working directory,
timestamps, duration, exit code, and full output (stdout + stderr
interleaved; nothing discarded).

## Notes

- **Sony paths**: orbis-pub-cmd 3.87 fails on some Unicode paths (U+FF1A).
  Packages are copied to an ASCII-safe work path before Sony operations;
  source files are never altered.
- **Result states**: `PASS`, `PASS_EXPECTED_WARNINGS` (e.g. Sony R4211/R4124
  appear on the original too), `EXPECTED_DIFFERENCE` (e.g. physical layout),
  `WARNING`, `FAIL`, `NOT_SUPPORTED`, `SKIPPED`, `SKIPPED_DEPENDENCY_FAILED`,
  `ERROR`.
- **Sony warnings** (R4211 PS5-testing note, R4124 trophy) are expected on
  BOTH the reference and our rebuild — that parity is a PASS.
- **Large PFSC DataStart is dynamic** (0x10000 for small, 0x20000+ for large
  images). The pointer table must fit before DataStart — never assume 0x10000.
- **Entry DataSize is the LOGICAL size** (Digimon npbind.dat = 532 in the
  table, 544 stored) — do not treat that as an error.
- Full mode may need ~4× the package sizes in disk; `-KeepArtifacts` is not
  implemented — set `cleanup_large_artifacts: false` in config to keep
  extractions. Manifests and logs always remain.

## Confidence labels

- `SELF_VALIDATED` — our tools only.
- `SONY_PC_VALIDATED` — + Sony orbis-pub-cmd.
- `CROSS_IMPLEMENTATION_VALIDATED` — + OpenOrbis.
- `PC_MAXIMUM_VALIDATED` — all of the above agree on readability, validation,
  extraction contents, and at least one roundtrip.
- `CONSOLE_VALIDATED` — reserved for the actual jailbroken-PS4 install test
  (see `../docs/CONSOLE_VALIDATION_CHECKLIST.md`).
