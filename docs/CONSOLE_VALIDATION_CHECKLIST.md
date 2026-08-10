# Console Validation Checklist (jailbroken PS4)

This is the ONLY remaining gate before the builder can claim PS4 compatibility.
Until a real install/launch passes, the project is documented as
*PC/orbis-pub-cmd compatible* only.

## Before you start

- Use the existing known-good rebuild first:
  `%TEMP%\digi_full\digi_FINAL.pkg` (Digimon World: Next Order, 11.166 GB,
  built 2026-08-10, validated: orbis list ✓, orbis verify profile identical
  to the original ✓, our 8-stage validation ✓).
- Rebuild it fresh if needed:
  `run_digi_validation.bat` in the repo root (pure C#, ~11 min).
- Do NOT change builder code immediately if installation fails — capture the
  exact error first (screenshot / HEN log) and report it.

## Checklist

| # | Check | Pass | Fail + notes |
|---|-------|------|--------------|
| 1 | PKG recognized (appears in the installer / payloader) | ☐ | |
| 2 | Installation starts | ☐ | |
| 3 | Installation completes | ☐ | |
| 4 | Game appears on the home screen with correct title/icon | ☐ | |
| 5 | Game launches | ☐ | |
| 6 | sce_sys metadata loads (param.sfo fields shown correctly) | ☐ | |
| 7 | Trophy subsystem behavior (trophy00.trp present, trophies visible) | ☐ | |
| 8 | Save creation + loading | ☐ | |
| 9 | Large files readable (Digimon archive.psarc ~237 MB, 3,629 blocks) | ☐ | |
| 10 | No corruption/errors after extended gameplay | ☐ | |

## If a step fails

1. Note the exact step + error text.
2. Run `OrbisPkgTool.Cli validate <pkg>` and `verify <pkg>` — both must pass.
3. Compare against the ORIGINAL Digimon FPKG (install it as a control if the
   same step fails there, the issue is not our builder).
4. Report findings before changing any frozen-format code.
