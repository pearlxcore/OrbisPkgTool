namespace OrbisPkgTool.Gui;

/// <summary>
/// Complete registry of every OrbisPkgTool CLI command, with the exact
/// option surface of each. Built from the CLI's Program.cs dispatch table.
///
/// Split into two lists for the two-tab GUI workflow:
///   - <see cref="PackageOps"/>: 21 PKG-centric operations (pkg + passcode are
///     supplied by the Package bar, NOT listed as fields — the GUI injects
///     them at run time).
///   - <see cref="ToolOps"/>: 19 self-contained non-PKG operations (build,
///     SFO/TRP, standalone tools).
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
            Description = "List all files and directories in a PKG (orbis img_file_list equivalent).",
            CliWord = "list",
            Fields = [new CommandField
            {
                Id = "oformat", Label = "Output format", Kind = FieldKind.Combo,
                Choices = ["short", "long+original_size", "packed_size"],
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
            Description = "Show PKG metadata (title, content ID, category, version, size).",
            CliWord = "info",
            Fields = [],
            BuildArgs = f => [f["pkg"]],
        });

        // -------------------------------------------------------- verify
        cmds.Add(new CommandDef
        {
            Name = "verify", Group = "Inspect", Title = "Verify PKG hashes/signatures",
            Description = "Verify all PKG header hashes and signatures (fast, CPU only).",
            CliWord = "verify",
            Fields = [],
            BuildArgs = f => [f["pkg"]],
        });

        // ------------------------------------------------------ validate
        cmds.Add(new CommandDef
        {
            Name = "validate", Group = "Inspect", Title = "Structured 8-stage validation",
            Description = "Structured 8-stage validation of a PKG (header/entries/outer PFS/PFSC/inner PFS/digests/signatures).",
            CliWord = "validate",
            Fields = [],
            BuildArgs = f =>
            {
                var a = new List<string>();
                if (f["passcode"] is { Length: > 0 } p) { a.Add("--passcode"); a.Add(p); }
                a.Add(f["pkg"]);
                return a.ToArray();
            },
        });

        // ---------------------------------------------------------- bench
        cmds.Add(new CommandDef
        {
            Name = "bench", Group = "Inspect", Title = "Benchmark listing speed",
            Description = "Measure entry-table-only listing time (acceptance: <2 s on large PKGs).",
            CliWord = "bench",
            Fields = [],
            BuildArgs = f => [f["pkg"]],
        });

        // ------------------------------------------------------ entries
        cmds.Add(new CommandDef
        {
            Name = "entries", Group = "Inspect", Title = "Dump PKG entry table",
            Description = "Dump the PKG entry table: id, name, size, offset (diagnostic).",
            CliWord = "entries",
            Fields = [],
            BuildArgs = f => [f["pkg"]],
        });

        // ------------------------------------------------------- inspect
        cmds.Add(new CommandDef
        {
            Name = "inspect", Group = "Inspect", Title = "Full PFS tree dump",
            Description = "Full PFS tree dump (outer + inner), useful for debugging.",
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
            Description = "Extract a PKG (or a single entry like pkg:Sc0/param.sfo) to a folder.",
            CliWord = "extract",
            Fields = [
                new CommandField
                {
                    Id = "entry", Label = "Entry path (optional, pkg:entry)", Kind = FieldKind.Text,
                },
                new CommandField
                {
                    Id = "outdir", Label = "Output directory", Kind = FieldKind.Folder, Position = 1,
                },
                new CommandField
                {
                    Id = "verbose", Label = "Verbose per-file progress", Kind = FieldKind.Check,
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
            Description = "Extract the raw decompressed inner PFS to a file (streaming, >2GB safe).",
            CliWord = "dumpinner",
            Fields = [
                new CommandField { Id = "out", Label = "Output (.pfs)", Kind = FieldKind.SaveFile,
                    Filter = "PFS images (*.pfs)|*.pfs|All files (*.*)|*.*", Position = 1 }],
            BuildArgs = f => [f["pkg"], f["out"]],
        });

        // ------------------------------------------------------ dumppfsc
        cmds.Add(new CommandDef
        {
            Name = "dumppfsc", Group = "Extract", Title = "Extract raw PFSC container",
            Description = "Extract the raw PFSC-compressed pfs_image.dat to a file.",
            CliWord = "dumppfsc",
            Fields = [
                new CommandField { Id = "out", Label = "Output (.pfsc)", Kind = FieldKind.SaveFile,
                    Filter = "PFSC images (*.pfsc)|*.pfsc|All files (*.*)|*.*", Position = 1 }],
            BuildArgs = f => [f["pkg"], f["out"]],
        });

        // ------------------------------------------------------- xtsdump
        cmds.Add(new CommandDef
        {
            Name = "xtsdump", Group = "Extract", Title = "Dump XTS-decrypted data",
            Description = "Decrypt and dump a region of the PKG image (XTS sectors).",
            CliWord = "xtsdump",
            Fields = [
                new CommandField { Id = "out", Label = "Output", Kind = FieldKind.SaveFile, Position = 1 }],
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
                Id = "saveinner", Label = "Also save inner PFS (out.pfs)", Kind = FieldKind.SaveFile,
                Filter = "PFS images (*.pfs)|*.pfs|All files (*.*)|*.*",
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
            Description = "Verify the outer PFS HMAC signature slots (diagnostic).",
            CliWord = "signverify",
            Fields = [],
            BuildArgs = f => [f["pkg"]],
        });

        // ------------------------------------------------------ innerfpt
        cmds.Add(new CommandDef
        {
            Name = "innerfpt", Group = "Diagnose", Title = "Dump inner PFS flat path table",
            Description = "Dump the inner PFS flat path table (diagnostic).",
            CliWord = "innerfpt",
            Fields = [],
            BuildArgs = f => [f["pkg"]],
        });

        // ------------------------------------------------------ pfsblock
        cmds.Add(new CommandDef
        {
            Name = "pfsblock", Group = "Diagnose", Title = "Dump a PFS block",
            Description = "Dump a specific outer PFS block (diagnostic).",
            CliWord = "pfsblock",
            Fields = [new CommandField
            {
                Id = "block", Label = "Block number", Kind = FieldKind.Text, Position = 1,
            }],
            BuildArgs = f => [f["pkg"], f["block"]],
        });

        // ---------------------------------------------------------- iblock
        cmds.Add(new CommandDef
        {
            Name = "iblock", Group = "Diagnose", Title = "Dump an inner PFS block",
            Description = "Dump a specific inner PFS block (diagnostic).",
            CliWord = "iblock",
            Fields = [new CommandField
            {
                Id = "block", Label = "Block number", Kind = FieldKind.Text, Position = 1,
            }],
            BuildArgs = f => [f["pkg"], f["block"]],
        });

        // ------------------------------------------------------- xtstest
        cmds.Add(new CommandDef
        {
            Name = "xtstest", Group = "Diagnose", Title = "XTS encryption test",
            Description = "XTS sector encrypt/decrypt round-trip test (diagnostic).",
            CliWord = "xtstest",
            Fields = [],
            BuildArgs = f => [f["pkg"]],
        });

        // -------------------------------------------------------- blkcount
        cmds.Add(new CommandDef
        {
            Name = "blkcount", Group = "Diagnose", Title = "Count pfs_image.dat blocks",
            Description = "Enumerate and count outer-PFS pfs_image.dat blocks (diagnostic).",
            CliWord = "blkcount",
            Fields = [],
            BuildArgs = f => [f["pkg"]],
        });

        // ------------------------------------------------------ resignpfs
        cmds.Add(new CommandDef
        {
            Name = "resignpfs", Group = "Diagnose", Title = "Resign outer PFS",
            Description = "Recompute and rewrite the outer PFS HMAC signatures in place (diagnostic).",
            CliWord = "resignpfs",
            Fields = [new CommandField
            {
                Id = "maxblocks", Label = "Max blocks (blank = all)", Kind = FieldKind.Text,
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
            Description = "Recompute all header digests of an existing PKG in place (diagnostic; keeps orbis format check happy after patching).",
            CliWord = "fixdigests",
            Fields = [],
            BuildArgs = f => [f["pkg"]],
        });

        return cmds;
    }

    // ==================================================================
    // Tool ops: 19 — self-contained (fields include every CLI input).
    // ==================================================================
    static List<CommandDef> BuildToolOps()
    {
        var cmds = new List<CommandDef>();

        // ----------------------------------------------------------- build
        cmds.Add(new CommandDef
        {
            Name = "build", Group = "Build", Title = "Build a fake PKG from GP4 (pure C#)",
            Description = "Build a fake PKG from a GP4 project + source folder, entirely with our C# code (no orbis-pub-cmd).",
            CliWord = "build",
            Fields = [
                new CommandField { Id = "gp4", Label = "Project (.gp4)", Kind = FieldKind.File,
                    Filter = "GP4 projects (*.gp4)|*.gp4|All files (*.*)|*.*", Position = 0 },
                new CommandField { Id = "folder", Label = "Source folder (Image0)", Kind = FieldKind.Folder, Position = 1 },
                Out(),
                new CommandField { Id = "passcode", Label = "Passcode (32 hex)", Kind = FieldKind.Text,
                    Default = CommandDef.DefaultPasscode },
                new CommandField { Id = "pfsc", Label = "PFSC mode", Kind = FieldKind.Combo,
                    Choices = ["store", "compressed"] },
                new CommandField { Id = "manifest", Label = "Write build manifest (.json)", Kind = FieldKind.Check },
                new CommandField { Id = "validate", Label = "Run 8-stage validation after build", Kind = FieldKind.Check },
            ],
            BuildArgs = f =>
            {
                var a = new List<string>();
                if (f["passcode"] is { Length: > 0 } p) { a.Add("--passcode"); a.Add(p); }
                if (f["out"] is { Length: > 0 } o) { a.Add("--out"); a.Add(o); }
                if (f["pfsc"] is { Length: > 0 } m) { a.Add("--pfsc-mode"); a.Add(m); }
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
            Description = "Build a PKG by delegating to orbis-pub-cmd img_create (reference path).",
            CliWord = "orbis-build",
            Fields = [
                new CommandField { Id = "gp4", Label = "Project (.gp4)", Kind = FieldKind.File,
                    Filter = "GP4 projects (*.gp4)|*.gp4|All files (*.*)|*.*", Position = 0 },
                new CommandField { Id = "folder", Label = "Source folder (Image0)", Kind = FieldKind.Folder, Position = 1 },
                Out(),
                new CommandField { Id = "passcode", Label = "Passcode (32 hex)", Kind = FieldKind.Text,
                    Default = CommandDef.DefaultPasscode },
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

        // --------------------------------------------------------- gp4gen
        cmds.Add(new CommandDef
        {
            Name = "gp4gen", Group = "Build", Title = "Generate GP4 from a folder",
            Description = "Scan a folder and generate a GP4 project file (gengp4_app/gengp4_patch equivalent).",
            CliWord = "gp4gen",
            Fields = [
                new CommandField { Id = "folder", Label = "Folder (Image0)", Kind = FieldKind.Folder, Position = 0 },
                Out(),
                new CommandField { Id = "passcode", Label = "Passcode (32 hex)", Kind = FieldKind.Text,
                    Default = CommandDef.DefaultPasscode },
                new CommandField { Id = "patch", Label = "Patch project (not app)", Kind = FieldKind.Check },
                new CommandField { Id = "title", Label = "Title", Kind = FieldKind.Text },
                new CommandField { Id = "titleid", Label = "Title ID (CUSAxxxxx)", Kind = FieldKind.Text, Default = "CUSA00001" },
                new CommandField { Id = "contentid", Label = "Content ID", Kind = FieldKind.Text },
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
            Description = "Move Sc0/* to Image0/sce_sys/, delete Sc0/, remove PlayGo files — prepares a dump for gp4gen.",
            CliWord = "restructure",
            Fields = [new CommandField { Id = "folder", Label = "Dump folder (with Image0/ + Sc0/)", Kind = FieldKind.Folder, Position = 0 }],
            BuildArgs = f => [f["folder"]],
        });

        // ---------------------------------------------------------- sweep
        cmds.Add(new CommandDef
        {
            Name = "sweep", Group = "Build", Title = "Batch verify PKGs in a folder",
            Description = "Verify all .pkg files under a folder; writes a TSV report.",
            CliWord = "sweep",
            Fields = [
                new CommandField { Id = "folder", Label = "Folder to scan", Kind = FieldKind.Folder, Position = 0 },
                new CommandField { Id = "out", Label = "Report (.tsv)", Kind = FieldKind.SaveFile, Filter = "TSV reports (*.tsv)|*.tsv|All files (*.*)|*.*" },
                new CommandField { Id = "list", Label = "Also list files of each PKG", Kind = FieldKind.Check },
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
                Filter = "SFO files (*.sfo)|*.sfo|All files (*.*)|*.*", Position = 0 }],
            BuildArgs = f => [f["file"]],
        });
        cmds.Add(new CommandDef
        {
            Name = "sfo create", Group = "Metadata", Title = "Create a param.sfo",
            Description = "Create a new param.sfo (game or add-on template).",
            CliWord = "sfo create",
            Fields = [
                new CommandField { Id = "out", Label = "Output (.sfo)", Kind = FieldKind.SaveFile,
                    Filter = "SFO files (*.sfo)|*.sfo|All files (*.*)|*.*", Position = 0 },
                new CommandField { Id = "title", Label = "Title", Kind = FieldKind.Text },
                new CommandField { Id = "titleid", Label = "Title ID", Kind = FieldKind.Text, Default = "CUSA00001" },
                new CommandField { Id = "contentid", Label = "Content ID", Kind = FieldKind.Text },
                new CommandField { Id = "category", Label = "Category", Kind = FieldKind.Combo, Choices = ["gd", "ac", "gp"] },
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
            Description = "Set a key/value in an existing param.sfo (preserves field types).",
            CliWord = "sfo set",
            Fields = [
                new CommandField { Id = "file", Label = "param.sfo", Kind = FieldKind.File,
                    Filter = "SFO files (*.sfo)|*.sfo|All files (*.*)|*.*", Position = 0 },
                new CommandField { Id = "key", Label = "Key", Kind = FieldKind.Text, Position = 1 },
                new CommandField { Id = "value", Label = "Value", Kind = FieldKind.Text, Position = 2 },
                new CommandField { Id = "out", Label = "Write to (default: in-place)", Kind = FieldKind.SaveFile,
                    Filter = "SFO files (*.sfo)|*.sfo|All files (*.*)|*.*" },
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
            Description = "Validate an SFO file's format.",
            CliWord = "sfo check",
            Fields = [new CommandField { Id = "file", Label = "param.sfo", Kind = FieldKind.File,
                Filter = "SFO files (*.sfo)|*.sfo|All files (*.*)|*.*", Position = 0 }],
            BuildArgs = f => [f["file"]],
        });

        // ----------------------------------------------------------- trp
        cmds.Add(new CommandDef
        {
            Name = "trp list", Group = "Metadata", Title = "List TRP entries",
            Description = "List entries of a trophy pack (.trp).",
            CliWord = "trp list",
            Fields = [new CommandField { Id = "file", Label = "Trophy pack (.trp)", Kind = FieldKind.File,
                Filter = "TRP files (*.trp)|*.trp|All files (*.*)|*.*", Position = 0 }],
            BuildArgs = f => [f["file"]],
        });
        cmds.Add(new CommandDef
        {
            Name = "trp extract", Group = "Metadata", Title = "Extract a TRP",
            Description = "Extract a trophy pack to a directory.",
            CliWord = "trp extract",
            Fields = [
                new CommandField { Id = "file", Label = "Trophy pack (.trp)", Kind = FieldKind.File,
                    Filter = "TRP files (*.trp)|*.trp|All files (*.*)|*.*", Position = 0 },
                new CommandField { Id = "dir", Label = "Output directory", Kind = FieldKind.Folder, Position = 1 },
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
            Name = "trp create", Group = "Metadata", Title = "Create a TRP",
            Description = "Create a trophy pack from files.",
            CliWord = "trp create",
            Fields = [
                new CommandField { Id = "out", Label = "Output (.trp)", Kind = FieldKind.SaveFile,
                    Filter = "TRP files (*.trp)|*.trp|All files (*.*)|*.*", Position = 0 },
                new CommandField { Id = "files", Label = "Input files (space-separated)", Kind = FieldKind.MultiText },
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
            Description = "Validate the embedded key constants.",
            CliWord = "selftest",
            Fields = [],
            BuildArgs = _ => [],
        });

        // ---------------------------------------------------- emptypayload
        cmds.Add(new CommandDef
        {
            Name = "emptypayload", Group = "Tools", Title = "Write empty inner payload",
            Description = "Write an empty inner PFS payload file (diagnostic).",
            CliWord = "emptypayload",
            Fields = [new CommandField { Id = "out", Label = "Output (.pfsc)", Kind = FieldKind.SaveFile,
                Filter = "PFSC images (*.pfsc)|*.pfsc|All files (*.*)|*.*", Position = 0 }],
            BuildArgs = f => [f["out"]],
        });

        // -------------------------------------------------------- hashtest
        cmds.Add(new CommandDef
        {
            Name = "hashtest", Group = "Tools", Title = "FPT hash reference values",
            Description = "Print FPT hash values for reference paths (diagnostic).",
            CliWord = "hashtest",
            Fields = [],
            BuildArgs = _ => [],
        });

        // ----------------------------------------------------- pfscompare
        cmds.Add(new CommandDef
        {
            Name = "pfscompare", Group = "Tools", Title = "Compare two PFS images",
            Description = "Byte-compare two raw PFS images (headers, inodes, dirents) for compatibility debugging.",
            CliWord = "pfscompare",
            Fields = [
                new CommandField { Id = "ours", Label = "Our PFS", Kind = FieldKind.File,
                    Filter = "PFS images (*.pfs)|*.pfs|All files (*.*)|*.*", Position = 0 },
                new CommandField { Id = "orbis", Label = "Orbis PFS", Kind = FieldKind.File,
                    Filter = "PFS images (*.pfs)|*.pfs|All files (*.*)|*.*", Position = 1 },
            ],
            BuildArgs = f => [f["ours"], f["orbis"]],
        });

        // ----------------------------------------------------- inflatecheck
        cmds.Add(new CommandDef
        {
            Name = "inflatecheck", Group = "Tools", Title = "Test PFSC block inflate",
            Description = "Try to inflate the first PFSC block (diagnostic).",
            CliWord = "inflatecheck",
            Fields = [
                new CommandField { Id = "file", Label = "PFSC file", Kind = FieldKind.File, Position = 0 },
                new CommandField { Id = "out", Label = "Decoded output (optional)", Kind = FieldKind.SaveFile },
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
            Description = "Compare deflate levels against an orbis PFSC block (diagnostic).",
            CliWord = "leveltest",
            Fields = [
                new CommandField { Id = "inner", Label = "Inner PFS file", Kind = FieldKind.File, Position = 0 },
                new CommandField { Id = "orbis", Label = "Orbis PFSC file", Kind = FieldKind.File, Position = 1 },
            ],
            BuildArgs = f => [f["inner"], f["orbis"]],
        });

        // -------------------------------------------------------- deftest
        cmds.Add(new CommandDef
        {
            Name = "deftest", Group = "Tools", Title = "Deflate test on block 0",
            Description = "Try raw deflate variants on the first PFSC block (diagnostic).",
            CliWord = "deftest",
            Fields = [new CommandField { Id = "file", Label = "PFSC file", Kind = FieldKind.File, Position = 0 }],
            BuildArgs = f => [f["file"]],
        });

        // -------------------------------------------------------- buildtest
        cmds.Add(new CommandDef
        {
            Name = "buildtest", Group = "Tools", Title = "Build PKG with arbitrary payload",
            Description = "Build a PKG with an arbitrary pfs_image.dat payload (diagnostic).",
            CliWord = "buildtest",
            Fields = [
                new CommandField { Id = "gp4", Label = "Project (.gp4)", Kind = FieldKind.File,
                    Filter = "GP4 projects (*.gp4)|*.gp4|All files (*.*)|*.*", Position = 0 },
                new CommandField { Id = "folder", Label = "Source folder", Kind = FieldKind.Folder, Position = 1 },
                new CommandField { Id = "data", Label = "Payload data file", Kind = FieldKind.File, Position = 2 },
                new CommandField { Id = "out", Label = "Output (.pkg)", Kind = FieldKind.SaveFile,
                    Filter = "PS4 Packages (*.pkg)|*.pkg|All files (*.*)|*.*", Position = 3 },
            ],
            BuildArgs = f => [f["gp4"], f["folder"], f["data"], f["out"]],
        });

        return cmds;
    }
}
