namespace OrbisPkgTool.Gui;

/// <summary>
/// Complete registry of every OrbisPkgTool CLI command, with the exact
/// option surface of each. Built from the CLI's Program.cs dispatch table.
///
/// Split into two lists for the two-tab GUI workflow:
///   - <see cref="PackageOps"/>: 21 PKG-centric operations (pkg + passcode are
///     supplied by the Package bar, NOT listed as fields — the GUI injects
///     them at run time).
///   - <see cref="ToolOps"/>: 20 self-contained non-PKG operations (build,
///     repack, SFO/TRP, standalone tools).
/// </summary>
public static class CommandRegistry
{
    public static IReadOnlyList<CommandDef> PackageOps { get; } = BuildPackageOps();
    public static IReadOnlyList<CommandDef> ToolOps { get; } = BuildToolOps();

    // Aggregate kept for any legacy code that still iterates everything.
    public static IReadOnlyList<CommandDef> All =>
        [.. PackageOps, .. ToolOps];

    static CommandField Out() => new()
    {
        Id = "out", Label = "Output path", Kind = FieldKind.SaveFile, Filter = "All files (*.*)|*.*",
        Hint = "Leave blank to use default",
    };

    // ==================================================================
    // Package ops: 21 — pkg/passcode injected by PackageBar at run time,
    // so the per-op Fields never list them.
    // ==================================================================
    static List<CommandDef> BuildPackageOps()
    {
        var cmds = new List<CommandDef>();

        // ---------------------------------------------------------- list
        cmds.Add(new CommandDef
        {
            Name = "list", Group = "Inspect", Title = "List files in a PKG",
            Description = "List all files and folders inside a PKG.",
            CliWord = "list",
            Fields = [new CommandField
            {
                Id = "oformat", Label = "Output format", Kind = FieldKind.Combo,
                Choices = ["short", "long+original_size", "packed_size"],
                Default = "long+original_size",
                ChoiceRemarks = ["name only", "full detail (default)", "bytes in PKG"],
            }],
            BuildArgs = f =>
            {
                var a = new List<string>();
                if (f["passcode"] is { Length: > 0 } p) { a.Add("--passcode"); a.Add(p); }
                if (f["oformat"] is { Length: > 0 } o) { a.Add("--oformat"); a.Add(o); }
                a.Add(f["pkg"]);
                return a.ToArray();
            },
        });

        // ---------------------------------------------------------- info
        cmds.Add(new CommandDef
        {
            Name = "info", Group = "Inspect", Title = "Show PKG metadata",
            Description = "Show PKG metadata (title, IDs, category, version, passcode status).",
            CliWord = "info",
            Fields = [],
            BuildArgs = f => [f["pkg"]],
        });

        // -------------------------------------------------------- verify
        cmds.Add(new CommandDef
        {
            Name = "verify", Group = "Inspect", Title = "Verify hashes and signatures",
            Description = "Quick check of PKG header hashes and signatures (fast, CPU only).",
            CliWord = "verify",
            Fields = [],
            BuildArgs = f => [f["pkg"]],
        });

        // ------------------------------------------------------ validate
        cmds.Add(new CommandDef
        {
            Name = "validate", Group = "Inspect", Title = "Deep 8-stage validation",
            Description = "Deep 8-stage structural check: header, entries, PFS, PFSC, digests, signatures, filesystem walk.",
            CliWord = "validate",
            Fields = [new CommandField
            {
                Id = "fakeTolerant", Label = "Fake-PKG tolerant", Kind = FieldKind.Check,
                Hint = "Zeroed digests warn instead of fail",
            }],
            BuildArgs = f =>
            {
                var a = new List<string>();
                if (f["passcode"] is { Length: > 0 } p) { a.Add("--passcode"); a.Add(p); }
                if (f["fakeTolerant"] == "1") a.Add("--fake-tolerant");
                a.Add(f["pkg"]);
                return a.ToArray();
            },
        });

        // ---------------------------------------------------------- bench
        cmds.Add(new CommandDef
        {
            Name = "bench", Group = "Inspect", Title = "Benchmark listing speed",
            Description = "Measure how fast the PKG file listing is.",
            CliWord = "bench",
            Fields = [],
            BuildArgs = f => [f["pkg"]],
        });

        // ------------------------------------------------------ entries
        cmds.Add(new CommandDef
        {
            Name = "entries", Group = "Inspect", Title = "Dump PKG entry table",
            Description = "Dump the raw PKG entry table (id, name, size, offset).",
            CliWord = "entries",
            Fields = [],
            BuildArgs = f => [f["pkg"]],
        });

        // ------------------------------------------------------- inspect
        cmds.Add(new CommandDef
        {
            Name = "inspect", Group = "Inspect", Title = "Full PFS tree dump",
            Description = "Dump the full PFS tree (outer + inner) — useful when debugging.",
            CliWord = "inspect",
            Fields = [],
            BuildArgs = f =>
            {
                var a = new List<string>();
                if (f["passcode"] is { Length: > 0 } p) { a.Add("--passcode"); a.Add(p); }
                a.Add(f["pkg"]);
                return a.ToArray();
            },
        });

        // ------------------------------------------------------- extract
        cmds.Add(new CommandDef
        {
            Name = "extract", Group = "Extract", Title = "Extract files from a PKG",
            Description = "Extract files from a PKG to a folder (or a single file with pkg:entry).",
            CliWord = "extract",
            Fields = [
                new CommandField
                {
                    Id = "entry", Label = "Entry path", Kind = FieldKind.Text,
                    Hint = "e.g. sce_sys/param.sfo — blank extracts everything",
                },
                new CommandField
                {
                    Id = "outdir", Label = "Output directory", Kind = FieldKind.Folder, Position = 1,
                },
                new CommandField
                {
                    Id = "verbose", Label = "Verbose", Kind = FieldKind.Check,
                    Hint = "Show per-file progress",
                },
            ],
            BuildArgs = f =>
            {
                var a = new List<string>();
                if (f["passcode"] is { Length: > 0 } p) { a.Add("--passcode"); a.Add(p); }
                if (f["verbose"] == "1") a.Add("--verbose");
                a.Add(f["entry"] is { Length: > 0 } e ? $"{f["pkg"]}:{e}" : f["pkg"]);
                a.Add(f["outdir"]);
                return a.ToArray();
            },
        });

        // ----------------------------------------------------- dumpinner
        cmds.Add(new CommandDef
        {
            Name = "dumpinner", Group = "Extract", Title = "Extract raw inner PFS",
            Description = "Extract the raw decompressed inner PFS to a .pfs file (streams, >2 GB safe).",
            CliWord = "dumpinner",
            Fields = [
                new CommandField { Id = "out", Label = "Output (.pfs)", Kind = FieldKind.SaveFile,
                    Filter = "PFS images (*.pfs)|*.pfs|All files (*.*)|*.*", Position = 1,
                    Hint = "Raw decompressed inner PFS" }],
            BuildArgs = f => [f["pkg"], f["out"]],
        });

        // ------------------------------------------------------ dumppfsc
        cmds.Add(new CommandDef
        {
            Name = "dumppfsc", Group = "Extract", Title = "Extract raw PFSC container",
            Description = "Extract the raw PFSC-compressed pfs_image.dat to a .pfsc file.",
            CliWord = "dumppfsc",
            Fields = [
                new CommandField { Id = "out", Label = "Output (.pfsc)", Kind = FieldKind.SaveFile,
                    Filter = "PFSC images (*.pfsc)|*.pfsc|All files (*.*)|*.*", Position = 1,
                    Hint = "Raw PFSC-compressed pfs_image.dat" }],
            BuildArgs = f => [f["pkg"], f["out"]],
        });

        // ------------------------------------------------------- xtsdump
        cmds.Add(new CommandDef
        {
            Name = "xtsdump", Group = "Extract", Title = "Dump XTS-decrypted data",
            Description = "Decrypt and dump a region of the PKG image (XTS sectors).",
            CliWord = "xtsdump",
            Fields = [
                new CommandField { Id = "out", Label = "Output", Kind = FieldKind.SaveFile, Position = 1,
                    Hint = "XTS-decrypted region dump" }],
            BuildArgs = f => [f["pkg"], f["out"]],
        });

        // ------------------------------------------------------ pfsdump
        cmds.Add(new CommandDef
        {
            Name = "pfsdump", Group = "Diagnose", Title = "Dump outer PFS structure",
            Description = "Dump the outer PFS structure (headers, inodes, dirents).",
            CliWord = "pfsdump",
            Fields = [new CommandField
            {
                Id = "saveinner", Label = "Save inner PFS", Kind = FieldKind.SaveFile,
                Filter = "PFS images (*.pfs)|*.pfs|All files (*.*)|*.*",
                Hint = "Also extract the inner PFS to this .pfs file",
            }],
            BuildArgs = f =>
            {
                if (f["saveinner"] is { Length: > 0 } o)
                    return ["--save-inner", f["pkg"], o];
                return [f["pkg"]];
            },
        });

        // ----------------------------------------------------- signverify
        cmds.Add(new CommandDef
        {
            Name = "signverify", Group = "Diagnose", Title = "Verify outer PFS signatures",
            Description = "Verify the outer PFS HMAC signature slots.",
            CliWord = "signverify",
            Fields = [],
            BuildArgs = f => [f["pkg"]],
        });

        // ------------------------------------------------------ innerfpt
        cmds.Add(new CommandDef
        {
            Name = "innerfpt", Group = "Diagnose", Title = "Dump inner PFS flat path table",
            Description = "Dump the inner PFS flat path table.",
            CliWord = "innerfpt",
            Fields = [],
            BuildArgs = f => [f["pkg"]],
        });

        // ------------------------------------------------------ pfsblock
        cmds.Add(new CommandDef
        {
            Name = "pfsblock", Group = "Diagnose", Title = "Dump a PFS block",
            Description = "Dump a specific outer PFS block by index (hex).",
            CliWord = "pfsblock",
            Fields = [new CommandField
            {
                Id = "block", Label = "Block number", Kind = FieldKind.Text, Position = 1,
                Hint = "Hex index, e.g. 0x1A",
            }],
            BuildArgs = f => [f["pkg"], f["block"]],
        });

        // ---------------------------------------------------------- iblock
        cmds.Add(new CommandDef
        {
            Name = "iblock", Group = "Diagnose", Title = "Dump an inner PFS block",
            Description = "Dump a specific inner PFS block by index (hex).",
            CliWord = "iblock",
            Fields = [new CommandField
            {
                Id = "block", Label = "Block number", Kind = FieldKind.Text, Position = 1,
                Hint = "Hex index, e.g. 0x1A",
            }],
            BuildArgs = f => [f["pkg"], f["block"]],
        });

        // ------------------------------------------------------- xtstest
        cmds.Add(new CommandDef
        {
            Name = "xtstest", Group = "Diagnose", Title = "XTS encryption test",
            Description = "XTS sector encrypt/decrypt round-trip test.",
            CliWord = "xtstest",
            Fields = [],
            BuildArgs = f => [f["pkg"]],
        });

        // -------------------------------------------------------- blkcount
        cmds.Add(new CommandDef
        {
            Name = "blkcount", Group = "Diagnose", Title = "Count pfs_image.dat blocks",
            Description = "Enumerate and count outer-PFS pfs_image.dat blocks.",
            CliWord = "blkcount",
            Fields = [],
            BuildArgs = f => [f["pkg"]],
        });

        // ------------------------------------------------------ resignpfs
        cmds.Add(new CommandDef
        {
            Name = "resignpfs", Group = "Diagnose", Title = "Resign outer PFS",
            Description = "Recompute and rewrite the outer PFS HMAC signatures in place.",
            CliWord = "resignpfs",
            Fields = [new CommandField
            {
                Id = "maxblocks", Label = "Max blocks", Kind = FieldKind.Text,
                Hint = "Blank = all blocks",
            }],
            BuildArgs = f =>
            {
                var a = new List<string> { f["pkg"] };
                if (f["maxblocks"] is { Length: > 0 } m) a.Add(m);
                return a.ToArray();
            },
        });

        // ----------------------------------------------------- fixdigests
        cmds.Add(new CommandDef
        {
            Name = "fixdigests", Group = "Diagnose", Title = "Recompute PKG digests",
            Description = "Recompute all header digests of an existing PKG in place.",
            CliWord = "fixdigests",
            Fields = [],
            BuildArgs = f => [f["pkg"]],
        });

        return cmds;
    }

    // ==================================================================
    // Tool ops: 20 — self-contained (fields include every CLI input).
    // ==================================================================
    static List<CommandDef> BuildToolOps()
    {
        var cmds = new List<CommandDef>();

        // ----------------------------------------------------------- build
        cmds.Add(new CommandDef
        {
            Name = "build", Group = "Build", Title = "Build a PKG from GP4 (pure C#)",
            Description = "Build a PKG from a GP4 project + source folder using our own C# builder (no orbis-pub-cmd).",
            CliWord = "build",
            Fields = [
                new CommandField { Id = "gp4", Label = "Project (.gp4)", Kind = FieldKind.File,
                    Filter = "GP4 projects (*.gp4)|*.gp4|All files (*.*)|*.*", Position = 0 },
                new CommandField { Id = "folder", Label = "Source folder (Image0)", Kind = FieldKind.Folder, Position = 1,
                    Hint = "Game files the GP4 references" },
                Out(),
                new CommandField { Id = "passcode", Label = "Passcode (32 hex)", Kind = FieldKind.Text,
                    Default = CommandDef.DefaultPasscode,
                    Hint = "Default: the fake-PKG passcode" },
                new CommandField { Id = "pfsc", Label = "PFSC mode", Kind = FieldKind.Combo,
                    Choices = ["store", "compressed"], Default = "compressed",
                    ChoiceRemarks = ["no compression", "zlib PFSC (default)"] },
                new CommandField { Id = "workers", Label = "Workers", Kind = FieldKind.Text, Default = "1",
                    Hint = "Threads for PFSC compression (0 = all cores, default 1)" },
                new CommandField { Id = "manifest", Label = "Build manifest", Kind = FieldKind.Check,
                    Hint = "Write a .build.json manifest next to the PKG" },
                new CommandField { Id = "validate", Label = "Validate after build", Kind = FieldKind.Check,
                    Hint = "Run the 8-stage check on the result" },
            ],
            BuildArgs = f =>
            {
                var a = new List<string>();
                if (f["passcode"] is { Length: > 0 } p) { a.Add("--passcode"); a.Add(p); }
                if (f["out"] is { Length: > 0 } o) { a.Add("--out"); a.Add(o); }
                if (f["pfsc"] is { Length: > 0 } m) { a.Add("--pfsc-mode"); a.Add(m); }
                if (f["workers"] is { Length: > 0 } w) { a.Add("--workers"); a.Add(w); }
                if (f["manifest"] == "1") { a.Add("--manifest"); a.Add(f["out"] + ".build.json"); }
                if (f["validate"] == "1") a.Add("--validate");
                a.Add(f["gp4"]);
                a.Add(f["folder"]);
                return a.ToArray();
            },
        });

        // ----------------------------------------------------- orbis-build
        cmds.Add(new CommandDef
        {
            Name = "orbis-build", Group = "Build", Title = "Build via orbis-pub-cmd",
            Description = "Build a PKG by delegating to Sony's orbis-pub-cmd img_create (reference path).",
            CliWord = "orbis-build",
            Fields = [
                new CommandField { Id = "gp4", Label = "Project (.gp4)", Kind = FieldKind.File,
                    Filter = "GP4 projects (*.gp4)|*.gp4|All files (*.*)|*.*", Position = 0 },
                new CommandField { Id = "folder", Label = "Source folder (Image0)", Kind = FieldKind.Folder, Position = 1,
                    Hint = "Game files the GP4 references" },
                Out(),
                new CommandField { Id = "passcode", Label = "Passcode (32 hex)", Kind = FieldKind.Text,
                    Default = CommandDef.DefaultPasscode,
                    Hint = "Default: the fake-PKG passcode" },
            ],
            BuildArgs = f =>
            {
                var a = new List<string>();
                if (f["passcode"] is { Length: > 0 } p) { a.Add("--passcode"); a.Add(p); }
                if (f["out"] is { Length: > 0 } o) { a.Add("--out"); a.Add(o); }
                a.Add(f["gp4"]);
                a.Add(f["folder"]);
                return a.ToArray();
            },
        });

        // --------------------------------------------------------- repack
        cmds.Add(new CommandDef
        {
            Name = "repack", Group = "Build", Title = "Repack a PKG (extract + rebuild)",
            Description = "Extract a PKG and rebuild it in one step (extract → restructure → gp4gen → build). The original's compression policy is replayed automatically.",
            CliWord = "repack",
            Fields = [
                new CommandField { Id = "pkg", Label = "Input PKG", Kind = FieldKind.File,
                    Filter = "PS4 Packages (*.pkg)|*.pkg|All files (*.*)|*.*", Position = 0 },
                Out(),
                new CommandField { Id = "passcode", Label = "Passcode (32 hex)", Kind = FieldKind.Text,
                    Default = CommandDef.DefaultPasscode,
                    Hint = "Default: the fake-PKG passcode" },
                new CommandField { Id = "validate", Label = "Validate after build", Kind = FieldKind.Check,
                    Hint = "Run the 8-stage check on the result" },
                new CommandField { Id = "pfsc", Label = "PFSC mode", Kind = FieldKind.Combo,
                    Choices = ["store", "compressed"], Default = "compressed",
                    ChoiceRemarks = ["no compression", "replay original per-file policy (default)"] },
                new CommandField { Id = "workers", Label = "Workers", Kind = FieldKind.Text, Default = "1",
                    Hint = "Threads for PFSC compression (0 = all cores, default 1)" },
                new CommandField { Id = "title", Label = "Title", Kind = FieldKind.Text,
                    Hint = "Leave blank to keep original" },
                new CommandField { Id = "titleid", Label = "Title ID", Kind = FieldKind.Text,
                    Hint = "e.g. CUSA00001 — blank keeps original" },
                new CommandField { Id = "contentid", Label = "Content ID", Kind = FieldKind.Text,
                    Hint = "e.g. EP0001-CUSA00001_00-MYGAME000000001" },
                new CommandField { Id = "workdir", Label = "Work directory", Kind = FieldKind.Folder,
                    Hint = "Keep intermediate files here (optional)" },
                new CommandField { Id = "keepwork", Label = "Keep work dir", Kind = FieldKind.Check,
                    Hint = "Don't auto-delete intermediates on success" },
            ],
            BuildArgs = f =>
            {
                var a = new List<string>();
                if (f["passcode"] is { Length: > 0 } p) { a.Add("--passcode"); a.Add(p); }
                if (f["out"] is { Length: > 0 } o) { a.Add("--out"); a.Add(o); }
                if (f["validate"] == "1") a.Add("--validate");
                if (f["pfsc"] is { Length: > 0 } m) { a.Add("--pfsc-mode"); a.Add(m); }
                if (f["workers"] is { Length: > 0 } w) { a.Add("--workers"); a.Add(w); }
                if (f["title"] is { Length: > 0 } ti) { a.Add("--title"); a.Add(ti); }
                if (f["titleid"] is { Length: > 0 } tid) { a.Add("--title-id"); a.Add(tid); }
                if (f["contentid"] is { Length: > 0 } cid) { a.Add("--content-id"); a.Add(cid); }
                if (f["workdir"] is { Length: > 0 } wd) { a.Add("--work-dir"); a.Add(wd); }
                if (f["keepwork"] == "1") a.Add("--keep-work");
                a.Add(f["pkg"]);
                return a.ToArray();
            },
        });

        // --------------------------------------------------------- merge
        cmds.Add(new CommandDef
        {
            Name = "merge", Group = "Build", Title = "Merge base + update into one PKG",
            Description = "Extract base + update PKGs, overlay the update's files onto the base dump, and repack as a single base-app PKG at the update's version. Output is always sealed with the default all-zeros passcode. TITLE_IDs must match; base must be an app PKG.",
            CliWord = "merge",
            Fields = [
                new CommandField { Id = "basepkg", Label = "Base PKG", Kind = FieldKind.File,
                    Filter = "PS4 Packages (*.pkg)|*.pkg|All files (*.*)|*.*", Position = 0 },
                new CommandField { Id = "updpkg", Label = "Update PKG", Kind = FieldKind.File,
                    Filter = "PS4 Packages (*.pkg)|*.pkg|All files (*.*)|*.*", Position = 1 },
                Out(),
                new CommandField { Id = "passcode", Label = "Passcode (32 hex)", Kind = FieldKind.Text,
                    Default = CommandDef.DefaultPasscode,
                    Hint = "Shared passcode for both PKGs (default: fake-PKG passcode)" },
                new CommandField { Id = "basepass", Label = "Base passcode", Kind = FieldKind.Text,
                    Hint = "Only if base uses a different passcode (blank = shared)" },
                new CommandField { Id = "updpass", Label = "Update passcode", Kind = FieldKind.Text,
                    Hint = "Only if update uses a different passcode (blank = shared)" },
                new CommandField { Id = "validate", Label = "Validate after build", Kind = FieldKind.Check,
                    Hint = "Run the 8-stage check on the result" },
                new CommandField { Id = "pfsc", Label = "PFSC mode", Kind = FieldKind.Combo,
                    Choices = ["store", "compressed"], Default = "compressed",
                    ChoiceRemarks = ["no compression", "union of base + update per-file policy (default)"] },
                new CommandField { Id = "workers", Label = "Workers", Kind = FieldKind.Text, Default = "1",
                    Hint = "Threads for PFSC compression (0 = all cores, default 1)" },
                new CommandField { Id = "title", Label = "Title", Kind = FieldKind.Text,
                    Hint = "Leave blank to keep base title" },
                new CommandField { Id = "titleid", Label = "Title ID", Kind = FieldKind.Text,
                    Hint = "e.g. CUSA00001 — blank keeps base" },
                new CommandField { Id = "contentid", Label = "Content ID", Kind = FieldKind.Text,
                    Hint = "e.g. EP0001-CUSA00001_00-MYGAME000000001 — blank keeps base" },
                new CommandField { Id = "workdir", Label = "Work directory", Kind = FieldKind.Folder,
                    Hint = "Keep intermediate files here (optional)" },
                new CommandField { Id = "keepwork", Label = "Keep work dir", Kind = FieldKind.Check,
                    Hint = "Don't auto-delete intermediates on success" },
            ],
            BuildArgs = f =>
            {
                var a = new List<string>();
                if (f["passcode"] is { Length: > 0 } p) { a.Add("--passcode"); a.Add(p); }
                if (f["basepass"] is { Length: > 0 } bp) { a.Add("--base-passcode"); a.Add(bp); }
                if (f["updpass"] is { Length: > 0 } up) { a.Add("--update-passcode"); a.Add(up); }
                if (f["out"] is { Length: > 0 } o) { a.Add("--out"); a.Add(o); }
                if (f["validate"] == "1") a.Add("--validate");
                if (f["pfsc"] is { Length: > 0 } m) { a.Add("--pfsc-mode"); a.Add(m); }
                if (f["workers"] is { Length: > 0 } w) { a.Add("--workers"); a.Add(w); }
                if (f["title"] is { Length: > 0 } ti) { a.Add("--title"); a.Add(ti); }
                if (f["titleid"] is { Length: > 0 } tid) { a.Add("--title-id"); a.Add(tid); }
                if (f["contentid"] is { Length: > 0 } cid) { a.Add("--content-id"); a.Add(cid); }
                if (f["workdir"] is { Length: > 0 } wd) { a.Add("--work-dir"); a.Add(wd); }
                if (f["keepwork"] == "1") a.Add("--keep-work");
                a.Add(f["basepkg"]);
                a.Add(f["updpkg"]);
                return a.ToArray();
            },
        });

        // --------------------------------------------------------- gp4gen
        cmds.Add(new CommandDef
        {
            Name = "gp4gen", Group = "Build", Title = "Generate GP4 from a folder",
            Description = "Scan a folder and generate a GP4 project file. Metadata is read from sce_sys/param.sfo when present.",
            CliWord = "gp4gen",
            Fields = [
                new CommandField { Id = "folder", Label = "Folder (Image0)", Kind = FieldKind.Folder, Position = 0,
                    Hint = "Metadata read from sce_sys/param.sfo when present" },
                Out(),
                new CommandField { Id = "passcode", Label = "Passcode (32 hex)", Kind = FieldKind.Text,
                    Default = CommandDef.DefaultPasscode,
                    Hint = "Default: the fake-PKG passcode" },
                new CommandField { Id = "patch", Label = "Patch project", Kind = FieldKind.Check,
                    Hint = "Make a patch project instead of a base app" },
                new CommandField { Id = "title", Label = "Title", Kind = FieldKind.Text,
                    Hint = "Overrides the param.sfo title" },
                new CommandField { Id = "titleid", Label = "Title ID", Kind = FieldKind.Text, Default = "CUSA00001",
                    Hint = "e.g. CUSA00001 — overrides the param.sfo value" },
                new CommandField { Id = "contentid", Label = "Content ID", Kind = FieldKind.Text,
                    Hint = "e.g. EP0001-CUSA00001_00-MYGAME000000001" },
            ],
            BuildArgs = f =>
            {
                var a = new List<string>();
                if (f["patch"] == "1") a.Add("--patch");
                if (f["passcode"] is { Length: > 0 } p) { a.Add("--passcode"); a.Add(p); }
                if (f["title"] is { Length: > 0 } ti) { a.Add("--title"); a.Add(ti); }
                if (f["titleid"] is { Length: > 0 } tid) { a.Add("--title-id"); a.Add(tid); }
                if (f["contentid"] is { Length: > 0 } cid) { a.Add("--content-id"); a.Add(cid); }
                if (f["out"] is { Length: > 0 } o) { a.Add("--out"); a.Add(o); }
                a.Add(f["folder"]);
                return a.ToArray();
            },
        });

        // ---------------------------------------------------- restructure
        cmds.Add(new CommandDef
        {
            Name = "restructure", Group = "Build", Title = "Restructure an extracted dump",
            Description = "Tidy an extracted dump for building: move Sc0/ into Image0/sce_sys/ and remove files the build regenerates.",
            CliWord = "restructure",
            Fields = [new CommandField { Id = "folder", Label = "Dump folder", Kind = FieldKind.Folder, Position = 0,
                Hint = "Should contain Image0/ + Sc0/" }],
            BuildArgs = f => [f["folder"]],
        });

        // ---------------------------------------------------------- sweep
        cmds.Add(new CommandDef
        {
            Name = "sweep", Group = "Build", Title = "Batch check PKGs in a folder",
            Description = "Check every .pkg under a folder and write a TSV report.",
            CliWord = "sweep",
            Fields = [
                new CommandField { Id = "folder", Label = "Folder to scan", Kind = FieldKind.Folder, Position = 0,
                    Hint = "Every .pkg under this folder, recursively" },
                new CommandField { Id = "out", Label = "Report (.tsv)", Kind = FieldKind.SaveFile,
                    Filter = "TSV reports (*.tsv)|*.tsv|All files (*.*)|*.*",
                    Hint = "Default: sweep_report.tsv in the current directory" },
                new CommandField { Id = "list", Label = "List files", Kind = FieldKind.Check,
                    Hint = "Also list the files of each PKG" },
            ],
            BuildArgs = f =>
            {
                var a = new List<string>();
                if (f["list"] == "1") a.Add("--list");
                if (f["out"] is { Length: > 0 } o) { a.Add("--out"); a.Add(o); }
                a.Add(f["folder"]);
                return a.ToArray();
            },
        });

        // ----------------------------------------------------------- sfo
        cmds.Add(new CommandDef
        {
            Name = "sfo read", Group = "Metadata", Title = "Read a param.sfo",
            Description = "Read and display all entries of a param.sfo file.",
            CliWord = "sfo read",
            Fields = [new CommandField { Id = "file", Label = "param.sfo", Kind = FieldKind.File,
                Filter = "SFO files (*.sfo)|*.sfo|All files (*.*)|*.*", Position = 0,
                Hint = "Usually found at sce_sys/param.sfo" }],
            BuildArgs = f => [f["file"]],
        });
        cmds.Add(new CommandDef
        {
            Name = "sfo create", Group = "Metadata", Title = "Create a param.sfo",
            Description = "Create a new param.sfo (game, add-on or patch template).",
            CliWord = "sfo create",
            Fields = [
                new CommandField { Id = "out", Label = "Output (.sfo)", Kind = FieldKind.SaveFile,
                    Filter = "SFO files (*.sfo)|*.sfo|All files (*.*)|*.*", Position = 0 },
                new CommandField { Id = "title", Label = "Title", Kind = FieldKind.Text,
                    Hint = "Game title for TITLE/TITLE_00 entries" },
                new CommandField { Id = "titleid", Label = "Title ID", Kind = FieldKind.Text, Default = "CUSA00001",
                    Hint = "e.g. CUSA00001" },
                new CommandField { Id = "contentid", Label = "Content ID", Kind = FieldKind.Text,
                    Hint = "e.g. EP0001-CUSA00001_00-MYGAME000000001" },
                new CommandField { Id = "category", Label = "Category", Kind = FieldKind.Combo,
                    Choices = ["gd", "ac", "gp"], Default = "gd",
                    ChoiceRemarks = ["game (default)", "add-on / DLC", "patch"] },
            ],
            BuildArgs = f =>
            {
                var a = new List<string>();
                if (f["title"] is { Length: > 0 } ti) { a.Add("--title"); a.Add(ti); }
                if (f["titleid"] is { Length: > 0 } tid) { a.Add("--title-id"); a.Add(tid); }
                if (f["contentid"] is { Length: > 0 } cid) { a.Add("--content-id"); a.Add(cid); }
                if (f["category"] is { Length: > 0 } c) { a.Add("--category"); a.Add(c); }
                a.Add(f["out"]);
                return a.ToArray();
            },
        });
        cmds.Add(new CommandDef
        {
            Name = "sfo set", Group = "Metadata", Title = "Set an SFO entry",
            Description = "Change one key/value in a param.sfo (preserves field types).",
            CliWord = "sfo set",
            Fields = [
                new CommandField { Id = "file", Label = "param.sfo", Kind = FieldKind.File,
                    Filter = "SFO files (*.sfo)|*.sfo|All files (*.*)|*.*", Position = 0,
                    Hint = "The .sfo file to modify" },
                new CommandField { Id = "key", Label = "Key", Kind = FieldKind.Text, Position = 1,
                    Hint = "SFO entry name, e.g. TITLE" },
                new CommandField { Id = "value", Label = "Value", Kind = FieldKind.Text, Position = 2,
                    Hint = "New value for the key" },
                new CommandField { Id = "out", Label = "Write to", Kind = FieldKind.SaveFile,
                    Filter = "SFO files (*.sfo)|*.sfo|All files (*.*)|*.*",
                    Hint = "Blank = modify in place" },
            ],
            BuildArgs = f =>
            {
                // CLI expects: sfo set <file> <key> <value> [--out X]
                // (positionals at fixed indices 1-3 — flags must come after)
                var a = new List<string> { f["file"], f["key"], f["value"] };
                if (f["out"] is { Length: > 0 } o) { a.Add("--out"); a.Add(o); }
                return a.ToArray();
            },
        });
        cmds.Add(new CommandDef
        {
            Name = "sfo check", Group = "Metadata", Title = "Validate a param.sfo",
            Description = "Validate a param.sfo file's format.",
            CliWord = "sfo check",
            Fields = [new CommandField { Id = "file", Label = "param.sfo", Kind = FieldKind.File,
                Filter = "SFO files (*.sfo)|*.sfo|All files (*.*)|*.*", Position = 0,
                Hint = "The .sfo file to validate" }],
            BuildArgs = f => [f["file"]],
        });

        // ----------------------------------------------------------- trp
        cmds.Add(new CommandDef
        {
            Name = "trp list", Group = "Metadata", Title = "List trophy entries",
            Description = "List the entries of a trophy pack (.trp).",
            CliWord = "trp list",
            Fields = [new CommandField { Id = "file", Label = "Trophy pack (.trp)", Kind = FieldKind.File,
                Filter = "TRP files (*.trp)|*.trp|All files (*.*)|*.*", Position = 0,
                Hint = "The .trp file to list" }],
            BuildArgs = f => [f["file"]],
        });
        cmds.Add(new CommandDef
        {
            Name = "trp extract", Group = "Metadata", Title = "Extract a trophy pack",
            Description = "Extract a trophy pack (.trp) to a directory.",
            CliWord = "trp extract",
            Fields = [
                new CommandField { Id = "file", Label = "Trophy pack (.trp)", Kind = FieldKind.File,
                    Filter = "TRP files (*.trp)|*.trp|All files (*.*)|*.*", Position = 0,
                    Hint = "The .trp file to extract" },
                new CommandField { Id = "dir", Label = "Output directory", Kind = FieldKind.Folder, Position = 1,
                    Hint = "Where to extract trophy files" },
            ],
            BuildArgs = f =>
            {
                var a = new List<string>();
                a.Add(f["file"]);
                if (f["dir"] is { Length: > 0 } d) a.Add(d);
                return a.ToArray();
            },
        });
        cmds.Add(new CommandDef
        {
            Name = "trp create", Group = "Metadata", Title = "Create a trophy pack",
            Description = "Create a trophy pack (.trp) from files.",
            CliWord = "trp create",
            Fields = [
                new CommandField { Id = "out", Label = "Output (.trp)", Kind = FieldKind.SaveFile,
                    Filter = "TRP files (*.trp)|*.trp|All files (*.*)|*.*", Position = 0 },
                new CommandField { Id = "files", Label = "Input files", Kind = FieldKind.MultiText,
                    Hint = "Files to pack (space/comma-separated)" },
            ],
            BuildArgs = f =>
            {
                var a = new List<string>();
                a.Add(f["out"]);
                foreach (var p in f["files"].Split([' ', ',', ';'], StringSplitOptions.RemoveEmptyEntries))
                    a.Add(p.Trim('"'));
                return a.ToArray();
            },
        });

        // ------------------------------------------------------- selftest
        cmds.Add(new CommandDef
        {
            Name = "selftest", Group = "Tools", Title = "Validate embedded RSA keys",
            Description = "Check that the built-in RSA key constants are valid.",
            CliWord = "selftest",
            Fields = [],
            BuildArgs = _ => [],
        });

        // ---------------------------------------------------- emptypayload
        cmds.Add(new CommandDef
        {
            Name = "emptypayload", Group = "Tools", Title = "Write empty inner payload",
            Description = "Write an empty inner PFS payload file.",
            CliWord = "emptypayload",
            Fields = [new CommandField { Id = "out", Label = "Output (.pfsc)", Kind = FieldKind.SaveFile,
                Filter = "PFSC images (*.pfsc)|*.pfsc|All files (*.*)|*.*", Position = 0,
                Hint = "Where to write the empty payload" }],
            BuildArgs = f => [f["out"]],
        });

        // -------------------------------------------------------- hashtest
        cmds.Add(new CommandDef
        {
            Name = "hashtest", Group = "Tools", Title = "FPT hash reference values",
            Description = "Print FPT hash values for reference paths.",
            CliWord = "hashtest",
            Fields = [],
            BuildArgs = _ => [],
        });

        // ----------------------------------------------------- pfscompare
        cmds.Add(new CommandDef
        {
            Name = "pfscompare", Group = "Tools", Title = "Compare two PFS images",
            Description = "Byte-compare two raw PFS images (headers, inodes, dirents).",
            CliWord = "pfscompare",
            Fields = [
                new CommandField { Id = "ours", Label = "Our PFS", Kind = FieldKind.File,
                    Filter = "PFS images (*.pfs)|*.pfs|All files (*.*)|*.*", Position = 0,
                    Hint = "PFS built by our tool" },
                new CommandField { Id = "orbis", Label = "Orbis PFS", Kind = FieldKind.File,
                    Filter = "PFS images (*.pfs)|*.pfs|All files (*.*)|*.*", Position = 1,
                    Hint = "Reference PFS from orbis-pub-cmd" },
            ],
            BuildArgs = f => [f["ours"], f["orbis"]],
        });

        // ----------------------------------------------------- inflatecheck
        cmds.Add(new CommandDef
        {
            Name = "inflatecheck", Group = "Tools", Title = "Test PFSC block inflate",
            Description = "Try to inflate the first PFSC block.",
            CliWord = "inflatecheck",
            Fields = [
                new CommandField { Id = "file", Label = "PFSC file", Kind = FieldKind.File, Position = 0,
                    Hint = "A .pfsc container (e.g. from dumppfsc)" },
                new CommandField { Id = "out", Label = "Decoded output", Kind = FieldKind.SaveFile,
                    Hint = "Blank = <file>.dec next to the input" },
            ],
            BuildArgs = f =>
            {
                var a = new List<string> { f["file"] };
                if (f["out"] is { Length: > 0 } o) a.Add(o);
                return a.ToArray();
            },
        });

        // -------------------------------------------------------- leveltest
        cmds.Add(new CommandDef
        {
            Name = "leveltest", Group = "Tools", Title = "Compression level comparison",
            Description = "Compare deflate levels against an orbis PFSC block.",
            CliWord = "leveltest",
            Fields = [
                new CommandField { Id = "inner", Label = "Inner PFS file", Kind = FieldKind.File, Position = 0,
                    Hint = "Raw inner PFS (e.g. from dumpinner)" },
                new CommandField { Id = "orbis", Label = "Orbis PFSC file", Kind = FieldKind.File, Position = 1,
                    Hint = "Reference PFSC from orbis-pub-cmd" },
            ],
            BuildArgs = f => [f["inner"], f["orbis"]],
        });

        // -------------------------------------------------------- deftest
        cmds.Add(new CommandDef
        {
            Name = "deftest", Group = "Tools", Title = "Deflate test on block 0",
            Description = "Try raw deflate variants on the first PFSC block.",
            CliWord = "deftest",
            Fields = [new CommandField { Id = "file", Label = "PFSC file", Kind = FieldKind.File, Position = 0,
                Hint = "Try raw deflate variants on block 0" }],
            BuildArgs = f => [f["file"]],
        });

        // -------------------------------------------------------- buildtest
        cmds.Add(new CommandDef
        {
            Name = "buildtest", Group = "Tools", Title = "Build PKG with arbitrary payload",
            Description = "Build a PKG with an arbitrary pfs_image.dat payload.",
            CliWord = "buildtest",
            Fields = [
                new CommandField { Id = "gp4", Label = "Project (.gp4)", Kind = FieldKind.File,
                    Filter = "GP4 projects (*.gp4)|*.gp4|All files (*.*)|*.*", Position = 0 },
                new CommandField { Id = "folder", Label = "Source folder", Kind = FieldKind.Folder, Position = 1,
                    Hint = "Game files (Image0)" },
                new CommandField { Id = "data", Label = "Payload data file", Kind = FieldKind.File, Position = 2,
                    Hint = "Inner PFS, PFSC blob, or outer PFS — auto-detected" },
                new CommandField { Id = "out", Label = "Output (.pkg)", Kind = FieldKind.SaveFile,
                    Filter = "PS4 Packages (*.pkg)|*.pkg|All files (*.*)|*.*", Position = 3 },
            ],
            BuildArgs = f => [f["gp4"], f["folder"], f["data"], f["out"]],
        });

        return cmds;
    }
}
