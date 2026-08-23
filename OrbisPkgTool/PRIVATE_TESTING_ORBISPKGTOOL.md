# OrbisPkgTool private engine test build

Build label: `orbis-test.1`

This private build tests the standalone OrbisPkgTool command-line engine. Do not redistribute this archive, logs, command output, or package files.

Extract the whole ZIP to a writable folder. Open a terminal in that folder and run:

```text
OrbisPkgTool.exe --help
```

Run the included smoke test first. It tests the executable, command help,
self-test, and the full SFO create/read/edit/check path without requiring a PKG:

```powershell
.\Run-CliSmokeTest.ps1
```

Use `OrbisPkgTool.exe <command> --help` for the accepted arguments of a command.

## Main FPKG test

Read and follow `CLI_FPKG_TEST_CHECKLIST.md` included in this archive. It is
the primary test: extract a known-working base FPKG, repack it, install the
result on a jailbroken PS4, and confirm the game reaches a playable state.

The smoke test confirms only that the CLI executable works. It does not replace
the real FPKG and PS4 installation test.

## Report a problem

Send the completed report template from the checklist. Do not send PKG files.
