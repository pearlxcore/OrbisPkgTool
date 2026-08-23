# OrbisPkgTool FPKG test checklist

Use only homebrew, your own test content, or packages you are authorized to test.
Always work with copies. Do not use the only copy of a package or an installed game.

## What this test proves

The goal is to verify the complete FPKG path, not merely that a command returns
success:

1. OrbisPkgTool can inspect and extract a base-app FPKG.
2. It can rebuild that extracted content into a valid FPKG.
3. The rebuilt FPKG installs on the tester's jailbroken PS4.
4. The installed game reaches its normal playable state.

## Required test material

- One known-working base-game FPKG, preferably a small title.
- A jailbroken PS4 where the original test FPKG is already known to install and run.
- Enough free PC storage for the package plus its extracted files and rebuilt output.
- Enough free PS4 storage for the test installation.

Record the original package's title ID, version, approximate size, and whether
its folder path contains non-English characters.

## Test A: inspect and extract

From the folder containing `OrbisPkgTool.exe`:

```powershell
.\OrbisPkgTool.exe info "Original.pkg"
.\OrbisPkgTool.exe validate --fake-tolerant "Original.pkg"
.\OrbisPkgTool.exe extract --verbose "Original.pkg" ".\extract-test"
```

Expected result: `extract-test` contains the package files and no extraction
error is reported. Keep the complete console output if anything fails.

## Test B: rebuild the same base FPKG

Use `repack` for the primary test. It performs extraction, restructuring, GP4
generation, and package creation with the source package's compression policy.

```powershell
.\OrbisPkgTool.exe repack "Original.pkg" --out ".\Rebuilt.pkg" --validate --work-dir ".\repack-work" --keep-work
```

Expected result: `Rebuilt.pkg` is created and validation completes without an
error. The rebuilt file does not need to match the original byte for byte.

For a separate create-PKG test from the extracted files:

```powershell
.\OrbisPkgTool.exe restructure ".\extract-test"
.\OrbisPkgTool.exe gp4gen ".\extract-test\Image0" --out ".\extract-test\project.gp4"
.\OrbisPkgTool.exe build ".\extract-test\project.gp4" ".\extract-test\Image0" --out ".\Created.pkg" --validate
```

If the separate create-PKG test fails, include the retained `extract-test`
folder structure and console output in the report, but do not upload game files.

## Test C: PS4 installation and launch

1. Transfer only `Rebuilt.pkg` to the jailbroken PS4.
2. Install it using the tester's normal package-install workflow.
3. Confirm its title, icon, and version are shown as expected in the PS4 library.
4. Launch it and test until reaching gameplay or the title's normal first
   playable screen.
5. Restart the game once and confirm it still launches.

Report separately whether installation failed, the game failed at launch, or
the game reached gameplay but has a content/runtime problem.

## Optional Test D: base plus update merge

Only run this when both packages are known-working test FPKGs for the same title.

```powershell
.\OrbisPkgTool.exe merge "Base.pkg" "Update.pkg" --out ".\Merged.pkg" --validate --work-dir ".\merge-work" --keep-work
```

Install `Merged.pkg` as a fresh test installation. Confirm the displayed game
version is the update version and the game reaches gameplay.

## Report template

```text
Build label:
Windows version:
Command used:
Package type: base / update / merge
Title ID and version:
Original package size:
Output package size:
Non-English characters in any path: yes / no
CLI result: pass / fail
PS4 install result: pass / fail
PS4 launch result: pass / fail
Playable-state result: pass / fail
Complete console output:
```

Do not send PKG files, copyrighted game content, or console identifiers.
