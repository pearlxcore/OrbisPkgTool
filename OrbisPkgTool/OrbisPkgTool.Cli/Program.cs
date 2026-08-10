using OrbisPkgTool;
using OrbisPkgTool.Crypto;
using OrbisPkgTool.Pkg;

// Drop-in C# replacement for:
//   orbis-pub-cmd.exe img_file_list --passcode X --oformat long+original_size <pkg>
//   orbis-pub-cmd.exe img_extract   --passcode X <pkg>[:<entry>] <out_dir>
// plus `selftest` (validates embedded key constants).

var cmdArgs = Environment.GetCommandLineArgs().Skip(1).ToArray();
if (cmdArgs.Length == 0 || (cmdArgs.Length == 1 && cmdArgs[0] is "-h" or "--help"))
{
    PrintUsage();
    return;
}

// Per-command help: "list -h", "build --help", etc.
if (cmdArgs.Length >= 2 && cmdArgs[^1] is "-h" or "--help")
{
    PrintCommandHelp(cmdArgs[0].ToLowerInvariant());
    return;
}
if (cmdArgs[0] is "-h" or "--help" && cmdArgs.Length >= 2)
{
    PrintCommandHelp(cmdArgs[1].ToLowerInvariant());
    return;
}

try
{
    switch (cmdArgs[0].ToLowerInvariant())
    {
        case "img_file_list":
        case "img_list":
        case "list":
            RunList(ParseOptions(cmdArgs, out _));
            break;
        case "img_extract":
        case "extract":
            RunExtract(ParseOptions(cmdArgs, out _));
            break;
        case "selftest":
        case "self-test":
            RunSelfTest();
            break;
        case "inspect":
        case "debug":
            RunInspect(ParseOptions(cmdArgs, out _).Pkg);
            break;
        case "pkginfo":
        case "info":
            RunPkgInfo(ParseOptions(cmdArgs, out _));
            break;
        case "verify":
            RunVerify(ParseOptions(cmdArgs, out _).Pkg);
            break;
        case "validate":
            var vp = ParseOptions(cmdArgs, out _);
            RunValidate(vp.Pkg, vp.Passcode);
            break;
        case "entries":
            RunEntries(ParseOptions(cmdArgs, out _).Pkg);
            break;
        case "fixdigests":
            RunFixDigestsDebug(ParseOptions(cmdArgs, out _).Pkg);
            break;
        case "signverify":
            RunSignVerify(ParseOptions(cmdArgs, out _).Pkg);
            break;
        case "pfsblock":
            RunPfsBlock(ParseOptions(cmdArgs, out _).Pkg, cmdArgs[^1]);
            break;
        case "innerfpt":
            RunInnerFpt(ParseOptions(cmdArgs, out _).Pkg);
            break;
        case "iblock":
            RunInnerBlock(ParseOptions(cmdArgs, out _).Pkg, cmdArgs[^1]);
            break;
        case "pfsdump":
            if (cmdArgs.Length >= 3 && cmdArgs[1] == "--save-inner")
                RunDumpInnerFile(cmdArgs[2], cmdArgs[3]); // pkgPath, outPath
            else
                RunPfsDump(ParseOptions(cmdArgs, out _).Pkg);
            break;
        case "resignpfs":
            RunResignPfs(ParseOptions(cmdArgs, out _).Pkg, cmdArgs.Length > 2 ? int.Parse(cmdArgs[^1]) : int.MaxValue);
            break;
        case "buildtest":
            RunBuildTest(cmdArgs[1..]);
            break;
        case "emptypayload":
            {
                var inner = OrbisPkgTool.Pfs.PfsWriter.BuildInnerPfs([], 0);
                var pfsc = OrbisPkgTool.Pfs.PFSCWriter.Build(inner);
                File.WriteAllBytes(args[1], pfsc);
                Console.WriteLine($"empty inner payload: {pfsc.Length} bytes (inner {inner.Length})");
                break;
            }
        case "sweep":
            RunSweep(cmdArgs[1..]);
            break;
        case "sfo":
            RunSfo(cmdArgs[1..]);
            break;
        case "gp4gen":
        case "gp4":
            RunGp4Gen(cmdArgs[1..]);
            break;
        case "restructure":
        case "restruct":
            RunRestructure(cmdArgs[1..]);
            break;
        case "repack":
            RunRepack(cmdArgs[1..]);
            break;
        case "pfscompare":
        case "pfscmp":
            RunPfsCompare(cmdArgs[1..]);
            break;
        case "dumpinner":
        case "innerdump":
        case "extractinnerpfs":
            RunDumpInner(cmdArgs[1..]);
            break;
        case "dumpinner2":
            RunDumpInnerFile(cmdArgs[1], cmdArgs[2]);
            break;
        case "dumppfsc":
            RunDumpPfsc(cmdArgs[1], cmdArgs[2]);
            break;
        case "xtsdump":
            RunXtsDump(cmdArgs[1], cmdArgs[2]);
            break;
        case "deftest":
        {
            byte[] data = File.ReadAllBytes(cmdArgs[1]);
            // block 0: table[0]=0x10000, table[1] → read from header
            long t0 = (long)BitConverter.ToUInt64(data, 1024);
            long t1 = (long)BitConverter.ToUInt64(data, 1024 + 8);
            Console.WriteLine($"block0: off=0x{t0:X} size={t1 - t0}");
            byte[] blk = data[(int)t0..(int)t1];
            // try full raw deflate
            try {
                using var ms = new MemoryStream(blk);
                using var d = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress);
                var out1 = new MemoryStream(); d.CopyTo(out1);
                Console.WriteLine($"FULL raw deflate: {out1.Length} bytes, head={Convert.ToHexString(out1.ToArray().AsSpan(0,16))}");
            } catch (Exception e) { Console.WriteLine($"FULL raw deflate FAILED: {e.Message}"); }
            // try skip-2 raw deflate
            try {
                using var ms = new MemoryStream(blk, 2, blk.Length - 2);
                using var d = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress);
                var out1 = new MemoryStream(); d.CopyTo(out1);
                Console.WriteLine($"SKIP2 raw deflate: {out1.Length} bytes, head={Convert.ToHexString(out1.ToArray().AsSpan(0,16))}");
            } catch (Exception e) { Console.WriteLine($"SKIP2 raw deflate FAILED: {e.Message}"); }
            break;
        }
        case "inflatecheck":
        {
            byte[] data = File.ReadAllBytes(cmdArgs[1]);
            long t0 = (long)BitConverter.ToUInt64(data, 1024);
            long t1 = (long)BitConverter.ToUInt64(data, 1024 + 8);
            byte[] blk = data[(int)t0..(int)t1];
            Console.WriteLine($"block0: {blk.Length} bytes, head={Convert.ToHexString(blk.AsSpan(0, Math.Min(16, blk.Length)))}");
            // skip 2, inflate with SharpZipLib
            try {
                var inflater = new ICSharpCode.SharpZipLib.Zip.Compression.Inflater();
                inflater.SetInput(blk, 2, blk.Length - 2);
                var out1 = new byte[65536];
                int n = inflater.Inflate(out1);
                Console.WriteLine($"SharpZipLib Inflater (skip2): {n} bytes, finished={inflater.IsFinished}, head={Convert.ToHexString(out1.AsSpan(0,16))}");
            } catch (Exception e) { Console.WriteLine($"SharpZipLib Inflater FAILED: {e.Message}"); }
            // full inflate
            try {
                var inflater = new ICSharpCode.SharpZipLib.Zip.Compression.Inflater();
                inflater.SetInput(blk, 0, blk.Length);
                var out1 = new byte[65536];
                int n = inflater.Inflate(out1);
                Console.WriteLine($"SharpZipLib Inflater (full): {n} bytes, finished={inflater.IsFinished}, head={Convert.ToHexString(out1.AsSpan(0,16))}");
                // save decoded output
                File.WriteAllBytes(cmdArgs.Length > 2 ? cmdArgs[2] : cmdArgs[1] + ".dec", out1.AsSpan(0, n).ToArray());
            } catch (Exception e) { Console.WriteLine($"SharpZipLib Inflater (full) FAILED: {e.Message}"); }
            break;
        }
        case "leveltest":
        {
            byte[] inner = File.ReadAllBytes(cmdArgs[1]);
            byte[] block0 = inner.AsSpan(0, 0x10000).ToArray();
            byte[] orbisBlock = File.ReadAllBytes(cmdArgs[2]); // full orbis pfsc
            long t0 = (long)BitConverter.ToUInt64(orbisBlock, 1024);
            long t1 = (long)BitConverter.ToUInt64(orbisBlock, 1024 + 8);
            byte[] orb = orbisBlock[(int)t0..(int)t1];
            Console.WriteLine($"orbis block0: {orb.Length} bytes: {Convert.ToHexString(orb.AsSpan(0,24))}");
            for (int lvl = 0; lvl <= 9; lvl++)
            {
                var d = new ICSharpCode.SharpZipLib.Zip.Compression.Deflater(lvl, noZlibHeaderOrFooter: true);
                d.SetInput(block0); d.Finish();
                var buf = new byte[0x10000]; var ms = new MemoryStream();
                int n; while ((n = d.Deflate(buf)) > 0) ms.Write(buf, 0, n);
                var c = ms.ToArray();
                Console.WriteLine($"lvl {lvl}: {c.Length} bytes: {Convert.ToHexString(c.AsSpan(0, Math.Min(24, c.Length)))}");
            }
            break;
        }
        case "blkcount":
        {
            var reader = new PkgReader(cmdArgs[1], "00000000000000000000000000000000");
            _ = reader.ListFiles();
            var outer = reader.GetOuterPfs()!;
            var f = outer.FindFile("pfs_image.dat")!;
            // Count blocks the reader can enumerate
            Console.WriteLine($"file size: {f.Size}, blocks reported: {f.Blocks}");
            // Read through the file
            using var s = outer.OpenFileStream(f);
            byte[] buf = new byte[65536];
            long total = 0; int ok = 0, fail = 0;
            for (long b = 0; b < f.Blocks; b++)
            {
                s.Position = b * 65536;
                try { int n = s.Read(buf, 0, 65536); if (n > 0) ok++; else fail++; total += n; }
                catch (Exception e) { Console.WriteLine($"  FAIL block {b}: {e.Message}"); fail++; break; }
            }
            Console.WriteLine($"read ok={ok} fail={fail} total={total}");
            break;
        }
        case "hashtest":
            foreach (var p in new[] { "/a.bin", "uroot/a.bin", "/dir/c.bin", "uroot/dir/c.bin", "/sce_sys/keystone", "uroot/sce_sys/keystone", "/orbis.gp4", "uroot/orbis.gp4", "/dir", "uroot/dir", "/sce_sys", "uroot/sce_sys" })
            {
                uint h = 0;
                foreach (var c in p) h = (uint)char.ToUpper(c) + 31 * h;
                Console.WriteLine($"hash({p}) = 0x{h:X8}");
            }
            break;
        case "trp":
            RunTrp(cmdArgs[1..]);
            break;
        case "pkg":
        case "build":
            RunPkgBuild(cmdArgs[1..]);
            break;
        case "orbis-build":
            RunOrbisBuild(cmdArgs[1..]);
            break;
        case "bench":
            RunBench(ParseOptions(cmdArgs, out _).Pkg);
            break;
        case "xtstest":
            RunXtsTest(ParseOptions(cmdArgs, out _).Pkg);
            break;
        case "help":
        case "-h":
        case "--help":
            PrintUsage();
            break;
        default:
            Console.Error.WriteLine($"[error] Unknown command: {cmdArgs[0]}");
            PrintUsage();
            Environment.ExitCode = 2;
            break;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"[error] {ex.Message}");
    if (ex.StackTrace != null) Console.Error.WriteLine(ex.StackTrace.Split('\n').Take(6));
    Environment.ExitCode = 1;
}

static (string Pkg, string? Entry, string? OutDir, string Passcode, string Oformat) ParseOptions(string[] args, out int index)
{
    string? pkg = null, entry = null, outDir = null;
    string passcode = PkgReader.DefaultPasscode;
    string oformat = "long+original_size";
    index = 1;
    for (int i = 1; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--passcode" when i + 1 < args.Length:
                passcode = args[++i];
                break;
            case "--no_passcode":
                passcode = PkgReader.DefaultPasscode;
                break;
            case "--oformat" when i + 1 < args.Length:
                oformat = args[++i];
                break;
            case "--integrity_check":
            case "--format_check":
                if (i + 1 < args.Length) i++;
                break;
            default:
                if (args[i].StartsWith('-')) break;
                if (pkg == null)
                {
                    // pkg[:entry] : but don't treat a Windows drive letter ("C:\...")
                    // as the separator. Only split when a colon appears past index 1.
                    int colon = args[i].IndexOf(':', 2);
                    if (colon > 1 && args[i].Length > colon + 1)
                    {
                        pkg = args[i][..colon];
                        entry = args[i][(colon + 1)..];
                    }
                    else
                    {
                        pkg = args[i];
                    }
                }
                else outDir = args[i];
                break;
        }
    }
    if (pkg == null)
        throw new ArgumentException("No PKG path specified.");
    return (pkg, entry, outDir, passcode, oformat);
}

static void RunList((string Pkg, string? Entry, string? OutDir, string Passcode, string Oformat) o)
{
    using var reader = new PkgReader(o.Pkg, o.Passcode);
    bool longFormat = o.Oformat.Contains("long", StringComparison.OrdinalIgnoreCase);
    bool packedSize = o.Oformat.Contains("packed_size", StringComparison.OrdinalIgnoreCase);
    var files = reader.ListFiles()
        .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase);
    foreach (var f in files)
    {
        if (f.IsDirectory)
            Console.WriteLine($"D 0 {f.Path}");
        else
        {
            long size = packedSize ? f.PackedSize : f.Size;
            Console.WriteLine($"F {size} {f.Path}");
        }
    }
}

static void RunExtract((string Pkg, string? Entry, string? OutDir, string Passcode, string Oformat) o)
{
    bool verbose = Array.Exists(Environment.GetCommandLineArgs(), a => a is "--verbose" or "-v");
    if (o.OutDir == null)
        throw new ArgumentException("No output directory specified.");
    Directory.CreateDirectory(o.OutDir);
    using var reader = new PkgReader(o.Pkg, o.Passcode);
    if (reader.PasscodeStatus.StartsWith("passcode mismatch", StringComparison.Ordinal))
        Console.Error.WriteLine($"[warn] {reader.PasscodeStatus}");

    var sw = System.Diagnostics.Stopwatch.StartNew();
    if (o.Entry == null)
    {
        int filesDone = 0, filesTotal = 0;
        reader.ExtractAll(o.OutDir, new Progress<(int Current, int Total, string File)>(p =>
        {
            filesDone = p.Current; filesTotal = p.Total;
            if (verbose)
            {
                int pct = filesTotal > 0 ? (int)(100.0 * filesDone / filesTotal) : 0;
                string line = $"  [{pct,3}%] {filesDone}/{filesTotal}  {p.File}";
                // Pad to clear leftover chars from the previous (longer) line.
                // Console.WindowWidth throws when there is no console
                // (detached/automated runs) — fall back to a fixed width.
                int w = SafeWindowWidth();
                if (line.Length < w) line += new string(' ', w - line.Length);
                Console.Write($"\r{line}");
            }
        }));
        if (verbose)
        {
            string done = $"  [100%] {filesTotal}/{filesTotal}  done.";
            int w = SafeWindowWidth();
            if (done.Length < w) done += new string(' ', w - done.Length);
            Console.WriteLine($"\r{done}");
        }
        Console.WriteLine($"Extracted {filesTotal} files in {sw.Elapsed.TotalSeconds:F1}s.");
    }
    else
    {
        reader.ExtractFile(o.Entry, o.OutDir);
        Console.WriteLine($"Extracted '{o.Entry}' in {sw.Elapsed.TotalSeconds:F1}s.");
    }
}

static void RunPkgInfo((string Pkg, string? Entry, string? OutDir, string Passcode, string Oformat) o)
{
    using var reader = new PkgReader(o.Pkg, o.Passcode);
    var info = reader.GetInfo();
    Console.WriteLine($"Title        : {info.Title}");
    Console.WriteLine($"Title ID     : {info.TitleId}");
    Console.WriteLine($"Content ID   : {info.ContentId}");
    Console.WriteLine($"Type         : {info.Type}");
    Console.WriteLine($"Category     : {info.Category}");
    Console.WriteLine($"Content type : 0x{info.ContentType:X2}  flags 0x{info.ContentFlags:X8}");
    Console.WriteLine($"App version  : {info.AppVersion}");
    Console.WriteLine($"System ver   : {info.SystemVersion}");
    Console.WriteLine($"Passcode     : {reader.PasscodeStatus}");
}

/// <summary>
/// Sweeps a directory tree, testing every *.pkg with pkginfo (open + param.sfo
/// + type detection). Writes a tab-separated report; prints a summary.
/// Run it yourself : no need to watch the output.
/// </summary>
static void RunSweep(string[] args)
{
    string? dir = null, outFile = null;
    bool list = false;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--out" when i + 1 < args.Length: outFile = args[++i]; break;
            case "--list": list = true; break;
            default:
                if (!args[i].StartsWith('-')) dir = args[i];
                break;
        }
    }
    if (dir == null || !Directory.Exists(dir))
    {
        Console.Error.WriteLine("usage: sweep <pkg_folder> [--out report.tsv] [--list]");
        Environment.ExitCode = 2;
        return;
    }
    outFile ??= Path.Combine(Environment.CurrentDirectory, "sweep_report.tsv");

    var files = Directory.EnumerateFiles(dir, "*.pkg", SearchOption.AllDirectories)
        .OrderBy(f => f, StringComparer.OrdinalIgnoreCase)
        .ToArray();
    Console.WriteLine($"Sweeping {files.Length} PKGs under {dir}");
    Console.WriteLine($"Report: {outFile}");
    Console.WriteLine("Running... (this takes a few minutes for a large collection; Ctrl+C aborts safely)");

    using var writer = new StreamWriter(outFile, append: false, new System.Text.UTF8Encoding(true));
    writer.WriteLine("File\tResult\tType\tTitle\tTitleId\tContentId\tCategory\tPasscode\tError");

    int ok = 0, fail = 0;
    var failures = new List<string>();
    var typeCounts = new Dictionary<string, int>();
    var sw = System.Diagnostics.Stopwatch.StartNew();
    for (int i = 0; i < files.Length; i++)
    {
        string f = files[i];
        string result = "OK", type = "", title = "", titleId = "", contentId = "", category = "", passcode = "", error = "";
        try
        {
            using var reader = new PkgReader(f);
            var info = reader.GetInfo();
            type = info.Type.ToString();
            title = info.Title.Replace('\t', ' ');
            titleId = info.TitleId;
            contentId = info.ContentId;
            category = info.Category;
            passcode = reader.PasscodeStatus;
            if (list)
                _ = reader.ListFiles();
            typeCounts.TryGetValue(type, out int c);
            typeCounts[type] = c + 1;
        }
        catch (Exception ex)
        {
            result = "FAIL";
            error = ex.Message.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
            failures.Add(f);
            fail++;
        }
        if (result == "OK") ok++;
        writer.WriteLine($"{f}\t{result}\t{type}\t{title}\t{titleId}\t{contentId}\t{category}\t{passcode}\t{error}");
        if ((i + 1) % 100 == 0)
            Console.WriteLine($"  {i + 1}/{files.Length}...");
    }
    writer.Flush();
    sw.Stop();

    Console.WriteLine();
    Console.WriteLine($"DONE in {sw.Elapsed.TotalSeconds:F1} s : {ok} OK, {fail} FAILED");
    if (typeCounts.Count > 0)
    {
        Console.WriteLine("By type:");
        foreach (var kv in typeCounts.OrderByDescending(k => k.Value))
            Console.WriteLine($"  {kv.Key,-10} {kv.Value}");
    }
    if (failures.Count > 0)
    {
        Console.WriteLine("Failures:");
        foreach (var f in failures)
            Console.WriteLine($"  {f}");
        Environment.ExitCode = 1;
    }
}

static void RunVerify(string pkgPath)
{
    using var reader = new PkgReader(pkgPath);
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var failures = reader.VerifyIntegrity();
    sw.Stop();
    if (failures.Count == 0)
        Console.WriteLine($"Integrity OK : all {reader.Entries.Count - 1} entries verified in {sw.ElapsedMilliseconds} ms.");
    else
    {
        Console.WriteLine($"Integrity FAILED : {failures.Count} mismatch(es):");
        foreach (var f in failures.Take(20))
            Console.WriteLine($"  {f}");
    }
}

static void RunBench(string pkgPath)
{
    using var reader = new PkgReader(pkgPath);
    // Warm up + measure the entry-table-only listing (acceptance: <2 s on large PKGs).
    var sw = System.Diagnostics.Stopwatch.StartNew();
    var files = reader.ListFiles();
    sw.Stop();
    Console.WriteLine($"ListFiles: {files.Count} entries in {sw.ElapsedMilliseconds} ms " +
        $"({sw.Elapsed.TotalSeconds:F2} s) : PKG size {(reader.Header.PackageSize / 1024 / 1024 / 1024.0):F1} GB");
}

/// <summary>orbis-pub-sfo equivalent: create / read / edit / check param.sfo files.</summary>
static void RunSfo(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("usage: sfo <read|create|set|check> ...");
        Environment.ExitCode = 2;
        return;
    }
    try
    {
        switch (args[0].ToLowerInvariant())
        {
            case "read":
            {
                var sfo = OrbisPkgTool.Sfo.ParamSfo.Parse(File.ReadAllBytes(args[1]));
                foreach (var v in sfo.Values)
                    Console.WriteLine(v.Format == 0x0404
                        ? $"{v.Key} = 0x{v.IntValue:X8}"
                        : $"{v.Key} = {v.StringValue}");
                break;
            }
            case "create":
            {
                // sfo create <out.sfo> --title X --title-id X --content-id X [--category gd|ac|gp]
                string? outFile = null, title = "", titleId = "", contentId = "", category = "gd";
                for (int i = 1; i < args.Length; i++)
                {
                    switch (args[i])
                    {
                        case "--title" when i + 1 < args.Length: title = args[++i]; break;
                        case "--title-id" when i + 1 < args.Length: titleId = args[++i]; break;
                        case "--content-id" when i + 1 < args.Length: contentId = args[++i]; break;
                        case "--category" when i + 1 < args.Length: category = args[++i]; break;
                        default:
                            if (!args[i].StartsWith('-')) outFile = args[i];
                            break;
                    }
                }
                if (outFile == null) throw new ArgumentException("no output file");
                var sfo = category == "ac"
                    ? OrbisPkgTool.Sfo.ParamSfo.CreateAddonTemplate(title, titleId, contentId)
                    : OrbisPkgTool.Sfo.ParamSfo.CreateGameTemplate(title, titleId, contentId);
                File.WriteAllBytes(outFile, sfo.Serialize());
                Console.WriteLine($"Created {outFile}");
                break;
            }
            case "set":
            {
                // sfo set <file> <key> <value> [--out <file>]
                string file = args[1], key = args[2], value = args[3];
                string? outFile = null;
                for (int i = 4; i < args.Length; i++)
                    if (args[i] == "--out" && i + 1 < args.Length) outFile = args[++i];
                var sfo = OrbisPkgTool.Sfo.ParamSfo.Parse(File.ReadAllBytes(file));
                var existing = sfo[key];
                if (int.TryParse(value, out int iv) && existing is { Format: 0x0404 })
                    sfo.SetInt(key, iv);
                else if (existing != null)
                    sfo.SetString(key, value, existing.MaxLength); // preserve the field's max length
                else
                    sfo.SetString(key, value);
                File.WriteAllBytes(outFile ?? file, sfo.Serialize());
                Console.WriteLine($"Set {key} = {value}");
                break;
            }
            case "check":
            {
                var data = File.ReadAllBytes(args[1]);
                var sfo = OrbisPkgTool.Sfo.ParamSfo.Parse(data);
                bool valid = data.Length >= 0x14 && data[0] == 0 && data[1] == (byte)'P' &&
                             data[2] == (byte)'S' && data[3] == (byte)'F';
                Console.WriteLine(valid && sfo.Values.Count > 0
                    ? $"OK : {sfo.Values.Count} entries"
                    : "INVALID param.sfo");
                break;
            }
            default:
                Console.Error.WriteLine("unknown sfo subcommand");
                Environment.ExitCode = 2;
                break;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[error] {ex.Message}");
        if (ex.StackTrace != null) Console.Error.WriteLine(ex.StackTrace);
        Environment.ExitCode = 1;
    }
}

/// <summary>
/// Restructures extracted PKG dump to match what gp4gen expects.
/// Merges Sc0/* → Image0/sce_sys/, deletes Sc0/, removes files
/// that Sony's img_create regenerates (PlayGo, license, psreserved,
/// about/).  With --check, validates the dump structure first and
/// reports readiness without modifying anything.
///
/// Usage:
///   restructure <dump_folder>                   apply all fixes
///   restructure <dump_folder> --check           validate only (dry-run)
///   restructure <dump_folder> --verbose         show detail
/// </summary>
static void RunRestructure(string[] args)
{
    bool check = false, verbose = false;
    string? folder = null;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--check": check = true; break;
            case "--verbose": case "-v": verbose = true; break;
            default:
                if (!args[i].StartsWith('-')) folder = args[i];
                break;
        }
    }

    if (folder == null)
    {
        Console.Error.WriteLine("usage: restructure <dump_folder> [--check] [--verbose]");
        Environment.ExitCode = 2;
        return;
    }

    string root = Path.GetFullPath(folder);
    string img0 = Path.Combine(root, "Image0");
    string sc0  = Path.Combine(root, "Sc0");
    string sceSys = Path.Combine(img0, "sce_sys");

    // ── Validation ───────────────────────────────────────────────
    var issues = new List<string>();
    var warnings = new List<string>();

    if (!Directory.Exists(img0))
    {
        Console.Error.WriteLine($"[error] Image0 folder not found in {root}");
        if (!Directory.Exists(sc0))
            Console.Error.WriteLine("        Neither Image0/ nor Sc0/ exists — not an extracted dump?");
        else
            Console.Error.WriteLine("        Only Sc0/ found — this is an update/DLC PKG (no inner PFS).");
        Console.Error.WriteLine("        Cannot restructure without Image0/.");
        Environment.ExitCode = 1;
        return;
    }

    Console.WriteLine($"Image0: {root}\\Image0");

    // Count what we have
    int image0Files = Directory.GetFiles(img0, "*", SearchOption.AllDirectories).Length;
    int image0Dirs  = Directory.GetDirectories(img0, "*", SearchOption.AllDirectories).Length;
    Console.WriteLine($"  {image0Files} files, {image0Dirs} dirs");

    // Key files
    var keyFiles = new[] { "eboot.bin", "sce_sys/param.sfo", "sce_sys/keystone" };
    foreach (var kf in keyFiles)
    {
        var p = Path.Combine(img0, kf.Replace('/', Path.DirectorySeparatorChar));
        var found = File.Exists(p);
        if (verbose || !found)
            Console.WriteLine($"  {(found ? "[OK]" : "[MISSING]")} {kf}");
        if (!found) warnings.Add($"Missing {kf} (gp4gen will still work, but the rebuild may lack metadata)");
    }

    if (Directory.Exists(sc0))
    {
        int sc0Files = Directory.GetFiles(sc0, "*", SearchOption.AllDirectories).Length;
        Console.WriteLine($"Sc0:    {sc0Files} files (will be merged into Image0/sce_sys/)");
        bool hasParamSfo = File.Exists(Path.Combine(sc0, "param.sfo"));
        if (hasParamSfo) Console.WriteLine("  [OK] param.sfo present (required)");
        else issues.Add("Sc0/param.sfo is missing — build will lack metadata.");
    }
    else
    {
        Console.WriteLine("Sc0:    not present (already restructured, or update/DLC PKG)");
    }

    // Check for files Sony will regenerate (warn if present)
    var sonyRegen = new[] { "license.dat", "license.info", "psreserved.dat",
        "playgo-chunk.dat", "playgo-chunk.sha", "playgo-manifest.xml", "param.sfo.original" };
    var regenFound = sonyRegen.Where(f =>
            File.Exists(Path.Combine(sceSys, f)) ||
            File.Exists(Path.Combine(sc0, f)))
        .ToList();

    // About dir
    var aboutDir = Path.Combine(sceSys, "about");
    if (Directory.Exists(aboutDir))
        regenFound.Add("about/ (directory)");

    if (regenFound.Count > 0)
    {
        Console.WriteLine($"Sony-regenerated files found ({regenFound.Count}):");
        foreach (var f in regenFound)
            Console.WriteLine($"  [WILL DELETE] {f}");
        if (!check)
            Console.WriteLine("  These will be removed — Sony's img_create regenerates them.");
    }

    // Summary
    Console.WriteLine();
    if (issues.Count > 0)
    {
        Console.Error.WriteLine("=== STRUCTURAL ISSUES ===");
        foreach (var i in issues) Console.Error.WriteLine($"  [FAIL] {i}");
    }
    if (warnings.Count > 0)
    {
        Console.Error.WriteLine("=== WARNINGS ===");
        foreach (var w in warnings) Console.Error.WriteLine($"  [WARN] {w}");
    }
    if (issues.Count == 0 && warnings.Count == 0 && regenFound.Count == 0 && Directory.Exists(sc0))
        Console.WriteLine("Dump looks healthy — ready to restructure.");
    else if (issues.Count == 0)
        Console.WriteLine("Dump looks healthy (already restructured).");

    if (check)
    {
        Console.WriteLine();
        if (issues.Count == 0)
            Console.WriteLine("Check PASSED — dump is ready for gp4gen.");
        else
            Console.Error.WriteLine("Check FAILED — fix the issues above first.");
        Environment.ExitCode = issues.Count > 0 ? 1 : 0;
        return;
    }

    if (!Directory.Exists(sc0) && regenFound.Count == 0)
    {
        Console.WriteLine("Nothing to restructure — dump is already clean.");
        return;
    }

    // ── Apply ────────────────────────────────────────────────────

    // 1. Merge Sc0 → Image0/sce_sys
    if (Directory.Exists(sc0))
    {
        Console.WriteLine("--- Merging Sc0 → Image0/sce_sys ---");
        Directory.CreateDirectory(sceSys);
        foreach (var file in Directory.GetFiles(sc0, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sc0, file);
            string dest = Path.Combine(sceSys, rel);
            Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
            File.Move(file, dest, overwrite: true);
            Console.WriteLine($"  Move: Sc0/{rel} → Image0/sce_sys/{rel}");
        }
        Directory.Delete(sc0, recursive: true);
        Console.WriteLine("  Deleted Sc0/");
    }

    // 2. Delete Sony-regenerated files (even if Sc0 was already merged)
    Console.WriteLine("--- Cleaning Sony-regenerated files ---");
    foreach (string f in sonyRegen)
    {
        var path = Path.Combine(sceSys, f);
        var appPath = Path.Combine(sceSys, "app", f);
        foreach (string p in new[] { path, appPath })
        {
            if (File.Exists(p))
            {
                File.Delete(p);
                Console.WriteLine($"  Delete: {Path.GetRelativePath(root, p)}");
            }
        }
    }

    // 3. Delete about/ dir (Sony generates sce_sys/about/right.sprx)
    if (Directory.Exists(aboutDir))
    {
        Directory.Delete(aboutDir, recursive: true);
        Console.WriteLine($"  Delete: {Path.GetRelativePath(root, aboutDir)}");
    }

    Console.WriteLine();
    Console.WriteLine("Restructure complete. Ready for gp4gen.");
}

/// <summary>
/// Full extract→restructure→gp4gen→build→validate in a single command.
/// Paths with special characters (&amp;, [, ], spaces, Unicode) are handled
/// natively — no shell escaping needed.
///
/// Usage:
///   repack &lt;input.pkg&gt; [--out &lt;output.pkg&gt;] [--passcode X]
///          [--validate] [--pfsc-mode store|compressed]
///          [--title "Name"] [--title-id CUSA00001]
///          [--work-dir &lt;dir&gt;]
/// </summary>
static void RunRepack(string[] args)
{
    string? pkg = null, outFile = null, passcode = PkgBuilder.DefaultPasscode;
    string? title = null, titleId = null, contentId = null;
    string? workDir = null;
    bool validate = false;
    string pfscMode = "store";

    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--out"       when i + 1 < args.Length: outFile    = args[++i]; break;
            case "--passcode"   when i + 1 < args.Length: passcode   = args[++i]; break;
            case "--title"     when i + 1 < args.Length: title      = args[++i]; break;
            case "--title-id"  when i + 1 < args.Length: titleId    = args[++i]; break;
            case "--content-id" when i + 1 < args.Length: contentId  = args[++i]; break;
            case "--pfsc-mode" when i + 1 < args.Length: pfscMode   = args[++i]; break;
            case "--work-dir"  when i + 1 < args.Length: workDir    = args[++i]; break;
            case "--validate": validate = true; break;
            default:
                if (!args[i].StartsWith('-')) pkg = args[i];
                break;
        }
    }

    if (pkg == null || !File.Exists(pkg))
    {
        Console.Error.WriteLine("usage: repack <input.pkg> [--out <output.pkg>] [--passcode X]");
        Console.Error.WriteLine("              [--validate] [--pfsc-mode store|compressed]");
        Console.Error.WriteLine("              [--title \"Name\"] [--title-id CUSA00001]");
        Console.Error.WriteLine("              [--work-dir <dir>]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("  Full extract→restructure→gp4gen→build in one command.");
        Console.Error.WriteLine("  Paths with &, [, ], spaces, Unicode all pass through natively.");
        Environment.ExitCode = 2;
        return;
    }

    Console.WriteLine("===================================================================");
    Console.WriteLine("  OrbisPkgTool REPACK");
    Console.WriteLine("  Input : " + pkg);
    Console.WriteLine("===================================================================");
    Console.WriteLine();

    // ── Setup work directory ────────────────────────────────────
    if (workDir == null)
    {
        string baseName = Path.GetFileNameWithoutExtension(pkg);
        // Sanitise just enough for a folder name (replace truly hostile chars)
        var safe = new string(baseName.Select(c =>
                Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        workDir = Path.Combine(Path.GetTempPath(), $"pkg_repack_{safe}_{Guid.NewGuid():N}".Substring(0, 60));
    }
    Directory.CreateDirectory(workDir);
    string dumpDir   = Path.Combine(workDir, "dump");
    string image0Dir = Path.Combine(dumpDir, "Image0");
    string gp4Path   = Path.Combine(workDir, "project.gp4");

    if (outFile == null)
        outFile = Path.Combine(workDir, Path.GetFileNameWithoutExtension(pkg) + "_rebuilt.pkg");

    Console.WriteLine($"Work dir : {workDir}");
    Console.WriteLine($"Output   : {outFile}");
    Console.WriteLine();

    var overallSw = System.Diagnostics.Stopwatch.StartNew();

    try
    {
        // ── 1. Extract ──────────────────────────────────────────
        Console.WriteLine("[1/5] Extracting PKG...");
        using (var reader = new PkgReader(pkg, passcode))
        {
            if (reader.PasscodeStatus.StartsWith("passcode mismatch", StringComparison.Ordinal))
                Console.Error.WriteLine($"[warn] {reader.PasscodeStatus}");

            Directory.CreateDirectory(dumpDir);
            int done = 0, total = 0;
            reader.ExtractAll(dumpDir, new Progress<(int Current, int Total, string File)>(p =>
            {
                done = p.Current; total = p.Total;
                int pct = total > 0 ? (int)(100.0 * done / total) : 0;
                string line = $"  [{pct,3}%] {p.File}";
                int w = SafeWindowWidth();
                if (line.Length < w) line += new string(' ', w - line.Length);
                Console.Write($"\r{line}");
            }));
            if (total > 0) Console.WriteLine($"\r  [100%] {total} files extracted.");
        }

        // ── 2. Check for inner PFS ──────────────────────────────
        if (!Directory.Exists(image0Dir) ||
            (!File.Exists(Path.Combine(image0Dir, "eboot.bin")) &&
             Directory.GetFiles(image0Dir, "*", SearchOption.AllDirectories).Length == 0))
        {
            Console.Error.WriteLine("[error] No Image0 content extracted — this is an update/DLC PKG.");
            Console.Error.WriteLine("        Repack requires an app PKG with an inner PFS (game files).");
            Console.Error.WriteLine($"Work dir kept for inspection: {workDir}");
            Environment.ExitCode = 1;
            return;
        }

        // ── 3. Restructure ──────────────────────────────────────
        Console.WriteLine("[2/5] Restructuring dump...");
        RunRestructure([dumpDir]);

        // ── 4. Generate GP4 ─────────────────────────────────────
        Console.WriteLine("[3/5] Generating GP4 project...");
        var gp4Args = new List<string> { image0Dir, "--out", gp4Path };
        if (title != null)      { gp4Args.Add("--title");      gp4Args.Add(title); }
        if (titleId != null)    { gp4Args.Add("--title-id");   gp4Args.Add(titleId); }
        if (contentId != null)  { gp4Args.Add("--content-id"); gp4Args.Add(contentId); }
        if (passcode != PkgBuilder.DefaultPasscode) { gp4Args.Add("--passcode"); gp4Args.Add(passcode); }
        RunGp4Gen(gp4Args.ToArray());

        // ── 5. Build ────────────────────────────────────────────
        Console.WriteLine("[4/5] Building PKG (pure C#)...");
        var buildArgs = new List<string> { gp4Path, image0Dir, "--out", outFile, "--passcode", passcode };
        if (pfscMode != "store") { buildArgs.Add("--pfsc-mode"); buildArgs.Add(pfscMode); }
        RunPkgBuild(buildArgs.ToArray());
        var pkgSize = new FileInfo(outFile).Length;
        Console.WriteLine($"  Output: {outFile} ({pkgSize / 1024.0 / 1024.0:F1} MB)");

        // ── 6. Validate ─────────────────────────────────────────
        if (validate)
        {
            Console.WriteLine("[5/5] Validating rebuilt PKG...");
            RunValidate(outFile, passcode);
        }

        overallSw.Stop();
        Console.WriteLine();
        Console.WriteLine("===================================================================");
        Console.WriteLine($"  REPACK COMPLETE in {overallSw.Elapsed.TotalSeconds:F1}s");
        Console.WriteLine($"  Output: {outFile}");
        Console.WriteLine($"  Work:   {workDir}");
        Console.WriteLine("===================================================================");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine();
        Console.Error.WriteLine($"[error] Repack failed: {ex.Message}");
        if (ex.StackTrace != null)
            Console.Error.WriteLine(ex.StackTrace);
        Console.Error.WriteLine($"Work dir kept for debugging: {workDir}");
        Environment.ExitCode = 1;
    }
}

/// <summary>
/// Compares two raw PFS images byte-by-byte for debugging compatibility.
/// Usage: pfscompare <our.pfs> <orbis.pfs>
/// </summary>
static void RunPfsCompare(string[] args)
{
    if (args.Length < 2) { Console.Error.WriteLine("usage: pfscompare <our.pfs> <orbis.pfs>"); return; }
    byte[] ours = File.ReadAllBytes(args[0]), orbis = File.ReadAllBytes(args[1]);
    int minLen = Math.Min(ours.Length, orbis.Length);
    var h = Console.Out;

    // --- Header fields (LE) ---
    h.WriteLine("=== PFS HEADER ===");
    (string, int, int)[] hdrFields = {
        ("version", 0x00, 8), ("magic", 0x08, 4), ("id", 0x0C, 4),
        ("fmode", 0x10, 1), ("clean", 0x11, 1), ("ro", 0x12, 1), ("rsv", 0x13, 1),
        ("mode", 0x1C, 2), ("unk1", 0x1E, 2), ("blocksz", 0x20, 4),
        ("nbackup", 0x24, 4), ("nblock", 0x28, 8), ("ndinode", 0x30, 8),
        ("ndblock", 0x38, 8), ("ndinodeblock", 0x40, 8),
        ("superroot_ino", 0x48, 8), ("seed", 0x370, 16)
    };
    h.WriteLine($"{"Field",-20} {"Ours",-22} {"Orbis",-22} Match");
    foreach (var (name, off, sz) in hdrFields)
    {
        if (off + sz > minLen) continue;
        string ov = sz switch { 1 => $"{ours[off]:X2}", 2 => $"{BitConverter.ToUInt16(ours,off):X4}", 4 => $"{BitConverter.ToUInt32(ours,off):X8}", 8 => $"{BitConverter.ToUInt64(ours,off):X16}", 16 => Convert.ToHexString(ours.AsSpan((int)off,16)), _ => "" };
        string rv = sz switch { 1 => $"{orbis[off]:X2}", 2 => $"{BitConverter.ToUInt16(orbis,off):X4}", 4 => $"{BitConverter.ToUInt32(orbis,off):X8}", 8 => $"{BitConverter.ToUInt64(orbis,off):X16}", 16 => Convert.ToHexString(orbis.AsSpan((int)off,16)), _ => "" };
        h.WriteLine($"{name,-20} {ov,-22} {rv,-22} {(ov==rv?"SAME":"DIFF")}");
    }

    // --- Inode table (block 1, D32: 0xA8 each) ---
    h.WriteLine("\n=== INODE TABLE (D32, 0xA8 each, block 1 at offset 0x10000) ===");
    long inoOff = 0x10000;
    long ourNd = BitConverter.ToInt64(ours, 0x30);
    long orbNd = BitConverter.ToInt64(orbis, 0x30);
    long maxNd = Math.Min(ourNd, orbNd);
    for (long i = 0; i < maxNd; i++)
    {
        long o = inoOff + i * 0xA8;
        if (o + 0xA8 > minLen) break;
        h.WriteLine($"\n  --- Inode {i} ---");
        h.WriteLine($"  {"Field",-14} {"Ours",-22} {"Orbis",-22}");
        ushort om = BitConverter.ToUInt16(ours,(int)o), rm = BitConverter.ToUInt16(orbis,(int)o);
        ushort onk = BitConverter.ToUInt16(ours,(int)o+2), rnk = BitConverter.ToUInt16(orbis,(int)o+2);
        uint ofl = BitConverter.ToUInt32(ours,(int)o+4), rfl = BitConverter.ToUInt32(orbis,(int)o+4);
        long osz = BitConverter.ToInt64(ours,(int)o+8), rsz = BitConverter.ToInt64(orbis,(int)o+8);
        uint obl = BitConverter.ToUInt32(ours,(int)o+0x60), rbl = BitConverter.ToUInt32(orbis,(int)o+0x60);
        int odb0 = BitConverter.ToInt32(ours,(int)o+0x64), rdb0 = BitConverter.ToInt32(orbis,(int)o+0x64);
        h.WriteLine($"  {"mode",-14} 0x{om:X4},22 0x{rm:X4},22 {(om==rm?"SAME":"DIFF")}");
        h.WriteLine($"  {"nlink",-14} {onk,22} {rnk,22} {(onk==rnk?"SAME":"DIFF")}");
        h.WriteLine($"  {"flags",-14} 0x{ofl:X8},22 0x{rfl:X8},22 {(ofl==rfl?"SAME":"DIFF")}");
        h.WriteLine($"  {"size",-14} {osz,22} {rsz,22} {(osz==rsz?"SAME":"DIFF")}");
        h.WriteLine($"  {"blocks",-14} {obl,22} {rbl,22} {(obl==rbl?"SAME":"DIFF")}");
        h.WriteLine($"  {"db0",-14} {odb0,22} {rdb0,22} {(odb0==rdb0?"SAME":"DIFF")}");
        // Raw bytes
        bool same = ours.AsSpan((int)o, 0xA8).SequenceEqual(orbis.AsSpan((int)o, 0xA8));
        h.WriteLine($"  raw 0xA8 bytes: {(same?"SAME":"DIFF")}");
        if (!same) {
            for (int b=0; b<0xA8; b+=8) {
                string ohex = Convert.ToHexString(ours.AsSpan((int)o+b,Math.Min(8,(int)(0xA8-b))));
                string rhex = Convert.ToHexString(orbis.AsSpan((int)o+b,Math.Min(8,(int)(0xA8-b))));
                if (ohex!=rhex) h.WriteLine($"    +{b:X2}: ours={ohex} orbis={rhex}");
            }
        }
    }

    // --- Dirent blocks: superroot (ino0.db0), uroot (ino2.db0) ---
    h.WriteLine("\n=== DIRENT BLOCKS ===");
    void DumpDirents(byte[] pfs, string label, int blockNum) {
        h.WriteLine($"\n  --- {label} (block {blockNum}) ---");
        int bOff = blockNum * 0x10000;
        if (bOff + 0x10000 > pfs.Length) return;
        int off = 0;
        while (off + 16 <= 0x10000) {
            uint ino = BitConverter.ToUInt32(pfs, bOff+off);
            int type = BitConverter.ToInt32(pfs, bOff+off+4);
            int nlen = BitConverter.ToInt32(pfs, bOff+off+8);
            int esiz = BitConverter.ToInt32(pfs, bOff+off+12);
            if (esiz < 16 || ino == 0 && type == 0) break;
            string name = System.Text.Encoding.ASCII.GetString(pfs, bOff+off+16, Math.Min(nlen, esiz-16)).TrimEnd('\0');
            h.WriteLine($"    +{off:X4}: ino={ino} type={type} nlen={nlen} esiz={esiz} name=[{name}]");
            if (esiz <= 0) break;
            off += esiz;
            if (off >= 0x10000) break;
        }
    }
    // Find inode2's db0 block for uroot in our PFS (inode 0=superroot at 0, 1=FPT at 1, 2=uroot at 2)
    int GetDb0(byte[] pfs, int inoIdx) { return BitConverter.ToInt32(pfs, (int)(0x10000 + inoIdx*0xA8 + 0x64)); }
    DumpDirents(ours, "OUR superroot", GetDb0(ours, 0));
    DumpDirents(ours, "OUR uroot", GetDb0(ours, 2));
    DumpDirents(orbis, "ORB superroot", GetDb0(orbis, 0));
    DumpDirents(orbis, "ORB uroot", GetDb0(orbis, 2));
    // Also dump uroot raw bytes for orbis
    int orbUroot = GetDb0(orbis, 2);
    h.WriteLine($"\n  ORB uroot block {orbUroot} raw[0..127]:");
    for (int i=0;i<128;i+=32) h.WriteLine($"    +{i:X2}: {Convert.ToHexString(orbis.AsSpan(orbUroot*0x10000+i,Math.Min(32,0x10000-i)))}");

    // --- FPT ---
    h.WriteLine("\n=== FLAT PATH TABLE (block 3) ===");
    void DumpFpt(byte[] pfs, string label) {
        h.WriteLine($"  {label}:");
        // FPT is ino1's db0 block
        int fptBlock = GetDb0(pfs, 1);
        int fptOff = fptBlock * 0x10000;
        if (fptOff + 64 > pfs.Length) return;
        // FPT size from ino[1].size
        long fptSize = BitConverter.ToInt64(pfs, (int)(0x10000 + 1*0xA8 + 8));
        for (long i = 0; i < fptSize && i < 512; i += 8) {
            uint hash = BitConverter.ToUInt32(pfs, fptOff+(int)i);
            uint ino = BitConverter.ToUInt32(pfs, fptOff+(int)i+4);
            h.WriteLine($"    [{i/8,2}] hash=0x{hash:X8} ino={ino}");
        }
    }
    DumpFpt(ours, "OUR");
    DumpFpt(orbis, "ORB");

    // --- Block map ---
    h.WriteLine("\n=== BLOCK MAP ===");
    long ourNb = BitConverter.ToInt64(ours, 0x38); // ndblock at 0x38
    long orbNb = BitConverter.ToInt64(orbis, 0x38);
    h.WriteLine($"{"Block",6} {"OUR purpose",-30} {"ORB purpose",-30}");
    for (long b = 0; b < Math.Max(ourNb, orbNb); b++) {
        string ourUse = b switch { 0 => "Header", 1 => "Inodes", _ => "" };
        string orbUse = b switch { 0 => "Header", 1 => "Inodes", _ => "" };
        for (int io=0; io<maxNd; io++) {
            if (GetDb0(ours,io)==b && ourUse=="") ourUse=$"ino[{io}] data";
            if (GetDb0(orbis,io)==b && orbUse=="") orbUse=$"ino[{io}] data";
        }
        if (ourUse=="") { bool allZ=true; for(long j=b*0x10000;j<Math.Min(ours.Length,(b+1)*0x10000);j++) if(ours[j]!=0){allZ=false;break;} ourUse=allZ?"empty":"?data"; }
        if (orbUse=="") { bool allZ=true; for(long j=b*0x10000;j<Math.Min(orbis.Length,(b+1)*0x10000);j++) if(orbis[j]!=0){allZ=false;break;} orbUse=allZ?"empty":"?data"; }
        h.WriteLine($"{b,6} {ourUse,-30} {orbUse,-30}");
    }

    // --- First byte diff ---
    h.WriteLine("\n=== FIRST BYTE DIFFERENCE ===");
    for (int i = 0; i < minLen; i++) {
        if (ours[i] != orbis[i]) {
            h.WriteLine($"  offset 0x{i:X} (block {i/0x10000}+{i%0x10000:X}): ours=0x{ours[i]:X2} orbis=0x{orbis[i]:X2}");
            h.WriteLine($"  context ours[{-Math.Max(0,i-8)}..{i+16}]: {Convert.ToHexString(ours.AsSpan(Math.Max(0,i-8),Math.Min(32,ours.Length-Math.Max(0,i-8))))}");
            h.WriteLine($"  context orbis[{-Math.Max(0,i-8)}..{i+16}]: {Convert.ToHexString(orbis.AsSpan(Math.Max(0,i-8),Math.Min(32,orbis.Length-Math.Max(0,i-8))))}");
            break;
        }
    }
}

/// <summary>Extracts the raw decompressed inner PFS from a PKG (uses PkgReader.ExtractRawInnerPfs).</summary>
static void RunDumpInner(string[] args)
{
    if (args.Length < 2) { Console.Error.WriteLine("usage: dumpinner <pkg> <output.pfs>"); return; }
    RunDumpInnerFile(args[0], args[1]);
}

/// <summary>gengp4_app / gengp4_patch equivalent: generate a GP4 project from a folder.</summary>
static void RunGp4Gen(string[] args)
{
    string? folder = null, outFile = null, title = null, titleId = null, contentId = null, passcode = "";
    bool isPatch = false;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--patch": isPatch = true; break;
            case "--out" when i + 1 < args.Length: outFile = args[++i]; break;
            case "--title" when i + 1 < args.Length: title = args[++i]; break;
            case "--title-id" when i + 1 < args.Length: titleId = args[++i]; break;
            case "--content-id" when i + 1 < args.Length: contentId = args[++i]; break;
            case "--passcode" when i + 1 < args.Length: passcode = args[++i]; break;
            default:
                if (!args[i].StartsWith('-')) folder = args[i];
                break;
        }
    }
    if (folder == null || !Directory.Exists(folder))
    {
        Console.Error.WriteLine("usage: gp4gen <folder> [--patch] [--title X] [--title-id X] [--content-id X] [--passcode X] [--out file.gp4]");
        Environment.ExitCode = 2;
        return;
    }
    try
    {
        var proj = OrbisPkgTool.Gp4.Gp4Project.FromFolder(folder, isPatch, title, titleId, contentId, passcode);
        string xml = proj.Serialize();
        if (outFile != null)
        {
            File.WriteAllText(outFile, xml);
            Console.WriteLine($"Wrote {outFile} ({proj.Files.Count} files, {(isPatch ? "patch" : "app")})");
        }
        else
        {
            Console.WriteLine(xml);
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[error] {ex.Message}");
        Environment.ExitCode = 1;
    }
}

/// <summary>Dumps the PKG entry table: id, name, size (diagnostic).</summary>
static void RunEntries(string pkgPath)
{
    using var reader = new PkgReader(pkgPath);
    foreach (var e in reader.Entries)
    {
        string id = ((int)e.Id).ToString("X8");
        string flags = ((int)e.Flags1).ToString("X8");
        Console.WriteLine($"0x{id} 0x{flags} {e.Name ?? "-",-24} {e.DataSize,10} @0x{e.DataOffset:X8}");
    }
}

/// <summary>orbis-pub-trp equivalent: list / extract / create trophy pack (TRP) files.</summary>
static void RunTrp(string[] args)
{
    if (args.Length == 0)
    {
        Console.Error.WriteLine("usage: trp <list|extract|create> ...");
        Environment.ExitCode = 2;
        return;
    }
    try
    {
        switch (args[0].ToLowerInvariant())
        {
            case "list":
            {
                var entries = OrbisPkgTool.Trp.Trp.Read(args[1]);
                Console.WriteLine($"{entries.Count} entries:");
                foreach (var e in entries)
                    Console.WriteLine($"  {e.Name,-32} {e.DataSize} bytes @0x{e.DataOffset:X6}");
                break;
            }
            case "extract":
            {
                var entries = OrbisPkgTool.Trp.Trp.Read(args[1]);
                string dir = args.Length > 2 ? args[2] : ".";
                Directory.CreateDirectory(dir);
                foreach (var e in entries)
                {
                    string dest = Path.Combine(dir, e.Name);
                    File.WriteAllBytes(dest, e.Data);
                    Console.WriteLine($"Extracted {e.Name} ({e.DataSize} bytes)");
                }
                break;
            }
            case "create":
            {
                // trp create <out.trp> <file> [file...]
                if (args.Length < 3) throw new ArgumentException("no output or input files");
                string outFile = args[1];
                var entries = args[2..].Select(f => new OrbisPkgTool.Trp.TrpEntry
                {
                    Name = Path.GetFileName(f),
                    Data = File.ReadAllBytes(f),
                }).ToList();
                File.WriteAllBytes(outFile, OrbisPkgTool.Trp.Trp.Write(entries));
                Console.WriteLine($"Created {outFile} ({entries.Count} entries)");
                break;
            }
            default:
                Console.Error.WriteLine("unknown trp subcommand");
                Environment.ExitCode = 2;
                break;
        }
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[error] {ex.Message}");
        Environment.ExitCode = 1;
    }
}

/// <summary>orbis-pub-gen equivalent: build a fake PKG from a GP4 project + source folder.</summary>
static void RunPkgBuild(string[] args)
{
    // Support both "pkg build ..." and "build ..." (and bare "pkg ...").
    if (args.Length > 0 && args[0].Equals("build", StringComparison.OrdinalIgnoreCase))
        args = args[1..];
    string? gp4 = null, folder = null, outFile = null, passcode = OrbisPkgTool.Pkg.PkgBuilder.DefaultPasscode;
    bool validate = false;
    string pfscMode = "store";
    string? manifest = null;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--passcode" when i + 1 < args.Length: passcode = args[++i]; break;
            case "--out" when i + 1 < args.Length: outFile = args[++i]; break;
            case "--validate": validate = true; break;
            case "--pfsc-mode" when i + 1 < args.Length: pfscMode = args[++i]; break;
            case "--manifest" when i + 1 < args.Length: manifest = args[++i]; break;
            default:
                if (!args[i].StartsWith('-'))
                {
                    if (gp4 == null) gp4 = args[i];
                    else if (folder == null) folder = args[i];
                }
                break;
        }
    }
    if (gp4 == null || !File.Exists(gp4))
    {
        Console.Error.WriteLine("usage: pkg build <project.gp4> <source_folder> [--passcode X] [--out file.pkg]");
        Console.Error.WriteLine("       [--pfsc-mode store|compressed] [--manifest file.json] [--validate]");
        Environment.ExitCode = 2;
        return;
    }
    folder ??= Path.GetDirectoryName(Path.GetFullPath(gp4)) ?? ".";
    outFile ??= Path.ChangeExtension(Path.GetFileName(gp4), ".pkg");
    try
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        using var cts = new System.Threading.CancellationTokenSource();
        Console.CancelKeyPress += (s, e) => { e.Cancel = true; cts.Cancel(); };
        var options = new OrbisPkgTool.Pkg.BuildOptions
        {
            Passcode = passcode,
            PfscMode = pfscMode.Equals("compressed", StringComparison.OrdinalIgnoreCase)
                ? OrbisPkgTool.Pkg.PfscMode.Compressed : OrbisPkgTool.Pkg.PfscMode.Store,
            Validate = validate,
            ManifestPath = manifest,
            CancellationToken = cts.Token,
            Progress = (stage, done, total) =>
            {
                if (total <= 0) return;
                int pct = (int)(100.0 * done / total);
                string line = $"  [{pct,3}%] {stage} ({done / 1e6:F0}/{total / 1e6:F0} MB)";
                int w = SafeWindowWidth();
                if (line.Length < w) line += new string(' ', w - line.Length);
                Console.Write($"\r{line}");
            },
        };
        OrbisPkgTool.Pkg.PkgBuilder.Build(gp4, folder, outFile, options);
        sw.Stop();
        Console.WriteLine();
        long size = new FileInfo(outFile).Length;
        Console.WriteLine($"Built {outFile} ({size / 1024.0 / 1024.0:F1} MB) in {sw.Elapsed.TotalSeconds:F1} s");
        if (validate)
            RunValidate(outFile, passcode);
    }
    catch (OperationCanceledException)
    {
        Console.Error.WriteLine("\n[error] Build cancelled. Temporary files cleaned up.");
        Environment.ExitCode = 130;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[error] {ex.Message}");
        Environment.ExitCode = 1;
    }
}

/// <summary>
/// Console width with a fallback for detached/automated runs where
/// Console.WindowWidth throws ("The handle is invalid").
/// </summary>
static int SafeWindowWidth()
{
    try { return Math.Min(Console.WindowWidth - 1, 120); }
    catch { return 120; }
}

/// <summary>Structured 8-stage validation of a built PKG ("validate" / "--validate").</summary>
static void RunValidate(string pkgPath, string passcode)
{
    try
    {
        OrbisPkgTool.Pkg.PkgValidator.ValidatePkgFile(pkgPath, passcode,
            (stage, what) => Console.WriteLine($"  [{stage}/8] Validating {what}"));
        Console.WriteLine("Validation: PASS");
    }
    catch (OrbisPkgTool.Pkg.ValidationFailure vf)
    {
        Console.Error.WriteLine("Validation: FAIL");
        Console.Error.WriteLine($"  Stage: {vf.Stage}");
        Console.Error.WriteLine($"  Structure: {vf.Structure}");
        Console.Error.WriteLine($"  Offset: {vf.Offset}");
        Console.Error.WriteLine($"  Reason: {vf.Message}");
        Environment.ExitCode = 1;
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine("Validation: FAIL");
        Console.Error.WriteLine($"  Reason: {ex.Message}");
        Environment.ExitCode = 1;
    }
}

static void RunOrbisBuild(string[] args)
{
    string? gp4 = null, folder = null, outFile = null, passcode = OrbisPkgTool.Pkg.PkgBuilder.DefaultPasscode;
    for (int i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--passcode" when i + 1 < args.Length: passcode = args[++i]; break;
            case "--out" when i + 1 < args.Length: outFile = args[++i]; break;
            default:
                if (!args[i].StartsWith('-'))
                {
                    if (gp4 == null) gp4 = args[i];
                    else if (folder == null) folder = args[i];
                }
                break;
        }
    }
    if (gp4 == null || !File.Exists(gp4))
    {
        Console.Error.WriteLine("usage: orbis-build <project.gp4> <source_folder> [--passcode X] [--out file.pkg]");
        Environment.ExitCode = 2;
        return;
    }
    folder ??= Path.GetDirectoryName(Path.GetFullPath(gp4)) ?? ".";
    outFile ??= Path.ChangeExtension(Path.GetFileName(gp4), ".pkg");
    try
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        OrbisPkgTool.Pkg.PkgBuilder.OrbisBuild(gp4, folder, outFile, passcode);
        sw.Stop();
        long size = new FileInfo(outFile).Length;
        Console.WriteLine($"Built {outFile} ({size / 1024.0 / 1024.0:F1} MB) in {sw.Elapsed.TotalSeconds:F1} s");
    }
    catch (Exception ex)
    {
        Console.Error.WriteLine($"[error] {ex.Message}");
        Environment.ExitCode = 1;
    }
}

/// <summary>
/// Recomputed all header digests (0x100-0x17F, 0x440-0x47F, 0xFE0, 0x1000)
/// of an existing PKG in place : the PS4_Tools PkgBuilder algorithms.
/// Diagnostic: lets us patch a real FPKG and keep orbis's format check happy.
/// </summary>
static void RunFixDigests(string pkgPath)
{
    byte[] head = ReadAt(pkgPath, 0, 0x1000 + 256); // header + RSA signature
    int count = (int)ReadBe32(head, 0x10);
    uint tableOff = ReadBe32(head, 0x18);
    int scCount = ReadBe16(head, 0x14);
    byte[] table = ReadAt(pkgPath, tableOff, count * 32);
    uint entryKeysOff = 0, imageKeyOff = 0, genDigestsOff = 0, metasOff = 0, digestsOff = 0, digestsSize = 0;
    for (int i = 0; i < count; i++)
    {
        uint id = ReadBe32(table, i * 32);
        uint off = ReadBe32(table, i * 32 + 16);
        uint size = ReadBe32(table, i * 32 + 20);
        switch (id)
        {
            case 0x10: entryKeysOff = off; break;
            case 0x20: imageKeyOff = off; break;
            case 0x80: genDigestsOff = off; break;
            case 0x100: metasOff = off; break;
            case 0x1: digestsOff = off; digestsSize = size; break;
        }
    }
    byte[] digestsData = ReadAt(pkgPath, digestsOff, (int)digestsSize);
    // 1. entry digests (i = 1..count-1)
    for (int i = 1; i < count; i++)
    {
        uint off = ReadBe32(table, i * 32 + 16);
        uint size = ReadBe32(table, i * 32 + 20);
        uint flags1 = ReadBe32(table, i * 32 + 8);
        long stored = (flags1 & 0x80000000) != 0 ? (size + 15) & ~15L : size;
        var hash = Sha256Region(pkgPath, off, stored);
        Buffer.BlockCopy(hash, 0, digestsData, i * 32, 32);
    }
    // 2. sc_entries1_hash: EntryKeys || ImageKey || GenDigests || Metas || Digests
    var s1 = ConcatBytes(
        ReadAt(pkgPath, entryKeysOff, 0x800),
        ReadAt(pkgPath, imageKeyOff, 0x100),
        ReadAt(pkgPath, genDigestsOff, 0x180),
        ReadAt(pkgPath, metasOff, scCount * 32),
        digestsData);
    Buffer.BlockCopy(OrbisPkgTool.Crypto.PkgCrypto.Sha256(s1), 0, head, 0x100, 32);
    var s2 = ConcatBytes(
        ReadAt(pkgPath, entryKeysOff, 0x800),
        ReadAt(pkgPath, imageKeyOff, 0x100),
        ReadAt(pkgPath, genDigestsOff, 0x180),
        ReadAt(pkgPath, metasOff, scCount * 32));
    Buffer.BlockCopy(OrbisPkgTool.Crypto.PkgCrypto.Sha256(s2), 0, head, 0x120, 32);
    Buffer.BlockCopy(OrbisPkgTool.Crypto.PkgCrypto.Sha256(digestsData), 0, head, 0x140, 32);
    ulong bodyOff = ReadBe64(head, 0x20), bodySize = ReadBe64(head, 0x28);
    Buffer.BlockCopy(Sha256Region(pkgPath, (long)bodyOff, (long)bodySize), 0, head, 0x160, 32);
    // 3. pfs digests (streamed : can be many GB)
    ulong pfsOff = ReadBe64(head, 0x410), pfsSize = ReadBe64(head, 0x418);
    Buffer.BlockCopy(Sha256Region(pkgPath, (long)pfsOff, (long)pfsSize), 0, head, 0x440, 32);
    Buffer.BlockCopy(Sha256Region(pkgPath, (long)pfsOff, Math.Min(0x10000, (long)pfsSize)), 0, head, 0x460, 32);
    // 4. header digest + signature
    Buffer.BlockCopy(OrbisPkgTool.Crypto.PkgCrypto.Sha256(head.AsSpan(0, 0xFE0).ToArray()), 0, head, 0xFE0, 32);
    var headerSha = OrbisPkgTool.Crypto.PkgCrypto.Sha256(head.AsSpan(0, 0x1000).ToArray());
    var signature = OrbisPkgTool.Crypto.PkgCrypto.RSA2048EncryptKey(OrbisPkgTool.Crypto.PkgKeySet.PkgPublicKeys[3], headerSha);
    Buffer.BlockCopy(signature, 0, head, 0x1000, 256);
    // write back: header + digests entry data
    using (var fs = new FileStream(pkgPath, FileMode.Open, FileAccess.Write, FileShare.Read))
    {
        fs.Position = 0; fs.Write(head, 0, head.Length);
        fs.Position = digestsOff; fs.Write(digestsData, 0, digestsData.Length);
    }
    Console.WriteLine($"Digests recomputed: {count} entries, pfs @0x{pfsOff:X} ({pfsSize} bytes).");
}

static void RunFixDigestsDebug(string pkgPath)
{
    try { RunFixDigests(pkgPath); }
    catch (Exception ex) { Console.Error.WriteLine(ex.ToString()); }
}

static byte[] ReadAt(string path, long offset, int count)
{
    using var fs = File.OpenRead(path);
    fs.Position = offset;
    var b = new byte[count];
    int read = 0;
    while (read < count) { int n = fs.Read(b, read, count - read); if (n <= 0) break; read += n; }
    return b;
}

static byte[] Sha256Region(string path, long offset, long length)
{
    using var fs = File.OpenRead(path);
    fs.Position = offset;
    using var sha = System.Security.Cryptography.SHA256.Create();
    var buf = new byte[0x100000];
    long remaining = length;
    while (remaining > 0)
    {
        int n = fs.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
        if (n <= 0) break;
        sha.TransformBlock(buf, 0, n, null, 0);
        remaining -= n;
    }
    sha.TransformFinalBlock([], 0, 0);
    return sha.Hash;
}

static byte[] ConcatBytes(params byte[][] parts)
{
    int total = parts.Sum(p => p.Length);
    var r = new byte[total];
    int o = 0;
    foreach (var p in parts) { Buffer.BlockCopy(p, 0, r, o, p.Length); o += p.Length; }
    return r;
}

static uint ReadBe32(byte[] b, int o) =>
    (uint)(b[o] << 24 | b[o + 1] << 16 | b[o + 2] << 8 | b[o + 3]);
static int ReadBe16(byte[] b, int o) => b[o] << 8 | b[o + 1];
static ulong ReadBe64(byte[] b, int o)
{
    ulong v = 0;
    for (int i = 0; i < 8; i++) v = (v << 8) | b[o + i];
    return v;
}

/// <summary>
/// Builds a PKG with an arbitrary pfs_image.dat payload (diagnostic):
/// buildtest &lt;gp4&gt; &lt;folder&gt; &lt;datafile&gt; &lt;out.pkg&gt;
/// </summary>
static void RunBuildTest(string[] args)
{
    if (args.Length < 4)
    {
        Console.Error.WriteLine("usage: buildtest <gp4> <folder> <datafile> <out.pkg>");
        Environment.ExitCode = 2;
        return;
    }
    byte[] payload = File.ReadAllBytes(args[2]);

    // Detect payload type:
    //  - mode 0x8 at 0x1C   → raw inner PFS → wrap with PFSC + outer (or raw)
    //  - "PFSC" magic       → pfs_image.dat content → wrap with outer only
    //  - otherwise          → full outer PFS blob → use directly
    ushort mode = BitConverter.ToUInt16(payload, 0x1C); // mode at 0x1C (per pfsdump)
    bool isPfsc = payload.Length >= 4 && payload[0]=='P' && payload[1]=='F' && payload[2]=='S' && payload[3]=='C';
    var project = OrbisPkgTool.Gp4.Gp4Project.Parse(File.ReadAllText(args[0]));
    if (isPfsc)
    {
        // pfs_image.dat content (already PFSC) → wrap with our outer PFS
        var dk = new byte[7][];
        for (uint i = 0; i < 7; i++)
            dk[i] = OrbisPkgTool.Crypto.PkgCrypto.DeriveKey(project.ContentId, "00000000000000000000000000000000", i);
        var outer = OrbisPkgTool.Pfs.PfsWriter.BuildOuterPfs(payload, "pfs_image.dat", dk[1], OrbisPkgTool.Crypto.Keys.FakeKeySeed, 0);
        OrbisPkgTool.Pkg.PkgBuilder.BuildCs(args[0], args[1], args[3], outer, "00000000000000000000000000000000");
        Console.WriteLine($"Built {args[3]} with PFSC content ({payload.Length} bytes) + outer {outer.Length}");
        return;
    }
    bool rawInner = args.Length >= 5 && args[4] == "--raw";
    if (mode == 0x8)
    {
        // payload is a raw inner PFS → wrap with PFSC + outer PFS
        var dk = new byte[7][];
        for (uint i = 0; i < 7; i++)
            dk[i] = OrbisPkgTool.Crypto.PkgCrypto.DeriveKey(project.ContentId, "00000000000000000000000000000000", i);
        byte[] innerForOuter;
        if (rawInner)
            innerForOuter = payload; // pfs_image.dat = raw inner PFS (no PFSC)
        else
            innerForOuter = OrbisPkgTool.Pfs.PFSCWriter.Build(payload, storeAllRaw: args.Length >= 5 && args[4] == "--rawall");
        var outer = OrbisPkgTool.Pfs.PfsWriter.BuildOuterPfs(innerForOuter, "pfs_image.dat", dk[1], OrbisPkgTool.Crypto.Keys.FakeKeySeed, 0);
        OrbisPkgTool.Pkg.PkgBuilder.BuildCs(args[0], args[1], args[3], outer, "00000000000000000000000000000000");
        Console.WriteLine($"Built {args[3]} with inner PFS {(rawInner ? "RAW" : "PFSC")} ({innerForOuter.Length} bytes) + outer {outer.Length}");
    }
    else
    {
        // Simple test: build PKG with the given outer PFS blob + our header assembly.
        OrbisPkgTool.Pkg.PkgBuilder.BuildCs(args[0], args[1], args[3], payload, "00000000000000000000000000000000");
        Console.WriteLine($"Built {args[3]} with injected outer PFS ({payload.Length} bytes)");
    }
}

/// <summary>Recomputes all PFS block signatures of an existing PKG in place (block-wise, for large PFSes).</summary>
static void RunResignPfs(string pkgPath, int maxBlocks = int.MaxValue)
{
    using var reader = new PkgReader(pkgPath);
    _ = reader.ListFiles();
    var h = reader.Header;
    long pfs = (long)h.PfsImageOffset;
    byte[] seed = ReadAt(pkgPath, pfs + 0x370, 16);
    var ekpfs = reader.Ekpfs ?? throw new Exception("no ekpfs");
    byte[] signKey = HmacSha256x(ekpfs, ConcatBytes(Le32x(2), seed));
    var (tk, dk) = OrbisPkgTool.Pfs.PfsReader.DeriveXtsKeys(
        new OrbisPkgTool.Pfs.PfsHeader { Mode = OrbisPkgTool.Pfs.PfsMode.Signed | OrbisPkgTool.Pfs.PfsMode.Encrypted | OrbisPkgTool.Pfs.PfsMode.UnknownFlagAlwaysSet, Seed = seed },
        ekpfs);
    var fs = new FileStream(pkgPath, FileMode.Open, FileAccess.ReadWrite, FileShare.Read);
    byte[] plain(int block)
    {
        byte[] b = ReadAt(pkgPath, pfs + block * 0x10000, 0x10000);
        if (block != 4)
            for (int s = block * 16; s < block * 16 + 16; s++)
                OrbisPkgTool.Pfs.PfsReader.XtsDecryptSector(b, (s - block * 16) * 0x1000, (ulong)s, dk!, tk!);
        return b;
    }
    void signSlot(long slot, int block)
    {
        var sig = HmacSha256x(signKey, plain(block));
        fs.Position = pfs + slot;
        fs.Write(sig, 0, 32);
    }
    byte[] tbl = plain(1);
    for (int i = 0; i < 4; i++)
    {
        long inoOff = i * 0x2C8;
        for (int d = 0; d < 12; d++)
        {
            int blk = BitConverter.ToInt32(tbl, (int)inoOff + 0x64 + d * 36 + 32);
            if (blk <= 0) continue;
            signSlot(0x10000 + inoOff + 0x64 + d * 36, blk);
        }
        for (int d = 0; d < 5; d++)
        {
            int ibBlk = BitConverter.ToInt32(tbl, (int)inoOff + 0x64 + 12 * 36 + d * 36 + 32);
            if (ibBlk <= 0) continue;
            signSlot(0x10000 + inoOff + 0x64 + 12 * 36 + d * 36, ibBlk);
            byte[] ib = plain(ibBlk);
            for (int e = 0; e < 1820; e++)
            {
                int blk = BitConverter.ToInt32(ib, e * 36 + 32);
                if (blk <= 0) break;
                signSlot(ibBlk * 0x10000 + e * 36, blk);
            }
        }
    }
    byte[] hdrBlock = ReadAt(pkgPath, pfs, 0x5A0);
    Array.Clear(hdrBlock, 0x380, 32);
    fs.Position = pfs + 0x380;
    var hdrSig = HmacSha256x(signKey, hdrBlock);
    fs.Write(hdrSig, 0, 32);
    signSlot(0x50 + 0x68, 1);
    fs.Dispose();
    Console.WriteLine("PFS re-signed.");
}

/// <summary>Dumps the outer PFS structure in full: header, inodes, dirents, fpt (diagnostic).</summary>
static void RunPfsDump(string pkgPath)
{
    using var reader = new PkgReader(pkgPath);
    _ = reader.ListFiles(); // triggers the EKPFS chain
    var h = reader.Header;
    long pfs = (long)h.PfsImageOffset;
    byte[] seed = ReadAt(pkgPath, pfs + 0x370, 16);
    var (tk, dk) = OrbisPkgTool.Pfs.PfsReader.DeriveXtsKeys(
        new OrbisPkgTool.Pfs.PfsHeader { Mode = OrbisPkgTool.Pfs.PfsMode.Signed | OrbisPkgTool.Pfs.PfsMode.Encrypted | OrbisPkgTool.Pfs.PfsMode.UnknownFlagAlwaysSet, Seed = seed },
        reader.Ekpfs!);
    // header
    byte[] hdr = ReadAt(pkgPath, pfs, 0x400);
    Console.WriteLine($"pfs: off=0x{pfs:X} size=0x{h.PfsImageSize:X} seed={Convert.ToHexString(seed)}");
    Console.WriteLine($"hdr: version={BitConverter.ToInt64(hdr, 0)} magic={BitConverter.ToInt64(hdr, 8)} mode=0x{BitConverter.ToUInt16(hdr, 0x1C):X} blocksz=0x{BitConverter.ToUInt32(hdr, 0x20):X}");
    Console.WriteLine($"hdr: ndinode={BitConverter.ToInt64(hdr, 0x30)} ndblock={BitConverter.ToInt64(hdr, 0x38)} ndinodeblock={BitConverter.ToInt64(hdr, 0x40)} superroot_ino={BitConverter.ToInt64(hdr, 0x48)}");
    Console.WriteLine($"hdr dinode: mode=0x{BitConverter.ToUInt16(hdr, 0x50):X} nlink={BitConverter.ToUInt16(hdr, 0x52)} flags=0x{BitConverter.ToUInt32(hdr, 0x54):X} size={BitConverter.ToInt64(hdr, 0x58)} blocks(u32@0xB0)={BitConverter.ToUInt32(hdr, 0xB0)}");
    Console.WriteLine($"hdr db[0]: sig={Convert.ToHexString(hdr.AsSpan(0xB8, 8))}... block(u32@0xD8)={BitConverter.ToInt32(hdr, 0xD8)}");
    Console.WriteLine($"hdr 0x368: {Convert.ToHexString(hdr.AsSpan(0x368, 0x18))}");
    // inode table (block 1)
    byte[] tbl = ReadAt(pkgPath, pfs + 0x10000, 0x10000);
    for (int s = 16; s < 32; s++)
        OrbisPkgTool.Pfs.PfsReader.XtsDecryptSector(tbl, (s - 16) * 0x1000, (ulong)s, dk!, tk!);
    for (int i = 0; i < 4; i++)
    {
        byte[] ino = new byte[0x2C8];
        Buffer.BlockCopy(tbl, i * 0x2C8, ino, 0, 0x2C8);
        uint blocks = BitConverter.ToUInt32(ino, 0x60);
        var db = new List<string>();
        for (int d = 0; d < 12; d++) db.Add(BitConverter.ToInt32(ino, 0x64 + d * 36 + 32).ToString());
        var ib = new List<string>();
        for (int d = 0; d < 5; d++) ib.Add(BitConverter.ToInt32(ino, 0x64 + 12 * 36 + d * 36 + 32).ToString());
        Console.WriteLine($"ino[{i}]: mode=0x{BitConverter.ToUInt16(ino, 0):X} nlink={BitConverter.ToUInt16(ino, 2)} flags=0x{BitConverter.ToUInt32(ino, 4):X} size={BitConverter.ToInt64(ino, 8)} sizeUnc={BitConverter.ToInt64(ino, 0x10)} t1={BitConverter.ToInt64(ino, 0x18)} blocks={blocks} db=[{string.Join(",", db.Take(3))}..] ib=[{string.Join(",", ib.Take(3))}..]");
    }
    // dirents (block 2) + fpt (block 3)
    byte[] b2 = ReadAt(pkgPath, pfs + 0x20000, 0x10000);
    for (int s = 32; s < 48; s++)
        OrbisPkgTool.Pfs.PfsReader.XtsDecryptSector(b2, (s - 32) * 0x1000, (ulong)s, dk!, tk!);
    Console.WriteLine($"block2: {Convert.ToHexString(b2.AsSpan(0, 0x60)).ToLowerInvariant()}");
    byte[] b3 = ReadAt(pkgPath, pfs + 0x30000, 0x10000);
    for (int s = 48; s < 64; s++)
        OrbisPkgTool.Pfs.PfsReader.XtsDecryptSector(b3, (s - 48) * 0x1000, (ulong)s, dk!, tk!);
    Console.WriteLine($"block3: {Convert.ToHexString(b3.AsSpan(0, 0x20)).ToLowerInvariant()}");
    byte[] tbl2 = tbl;
    int fblk0 = BitConverter.ToInt32(tbl2, 3 * 0x2C8 + 0x64 + 32);
    int fblk1 = BitConverter.ToInt32(tbl2, 3 * 0x2C8 + 0x64 + 36 + 32);
    Console.WriteLine($"file inode db[0]={fblk0} db[1]={fblk1}");
    var inner = reader.InnerPfs;
    if (inner != null)
        Console.WriteLine("inner opened OK");
}

/// <summary>Saves the raw decompressed inner PFS from a PKG.</summary>
static void RunDumpInnerFile(string pkgPath, string outPath)
{
    using var reader = new PkgReader(pkgPath);
    reader.ExtractRawInnerPfs(outPath);
    // Streamed summary — the inner PFS can exceed 2 GB (never ReadAllBytes).
    long len = new FileInfo(outPath).Length;
    var hb = new byte[0x50];
    using (var rs = File.OpenRead(outPath))
        rs.ReadExactly(hb, 0, hb.Length);
    using var sha = System.Security.Cryptography.SHA256.Create();
    using (var rs = File.OpenRead(outPath))
    {
        var buf = new byte[1 << 20];
        int n;
        while ((n = rs.Read(buf, 0, buf.Length)) > 0)
            sha.TransformBlock(buf, 0, n, null, 0);
        sha.TransformFinalBlock([], 0, 0);
    }
    Console.WriteLine($"Inner PFS saved: {outPath} ({len} bytes, {len/0x10000} blocks)");
    Console.WriteLine($"SHA256: {Convert.ToHexString(sha.Hash!)}");
    Console.WriteLine($"First 16: {Convert.ToHexString(hb.AsSpan(0, 16))}");
    Console.WriteLine($"ndinode={BitConverter.ToInt64(hb,0x30)} ndblock={BitConverter.ToInt64(hb,0x38)} ndinodeblock={BitConverter.ToInt64(hb,0x40)}");
}

/// <summary>Extracts the raw PFSC-compressed pfs_image.dat from a PKG.</summary>
static void RunDumpPfsc(string pkgPath, string outPath)
{
    using var reader = new PkgReader(pkgPath, "00000000000000000000000000000000");
    _ = reader.InnerPfs; // ensure EKPFS
    var outer = reader.GetOuterPfs() ?? throw new Exception("no outer pfs");
    var f = outer.FindFile("pfs_image.dat") ?? throw new Exception("no pfs_image.dat");
    using var raw = outer.OpenFileStream(f);
    using var ofs = File.Create(outPath);
    raw.CopyTo(ofs);
    Console.WriteLine($"PFSC saved: {outPath} ({raw.Length} bytes)");
    // header fields
    var hb = new byte[0x30]; raw.Position = 0; raw.Read(hb, 0, 0x30);
    Console.WriteLine($"magic={System.Text.Encoding.ASCII.GetString(hb,0,4)} unk4={BitConverter.ToUInt32(hb,4)} unk8={BitConverter.ToUInt32(hb,8)} blockSz={BitConverter.ToUInt32(hb,0xC)} blockSz2={BitConverter.ToUInt64(hb,0x10)} tableOff={BitConverter.ToUInt64(hb,0x18)} dataOff={BitConverter.ToUInt64(hb,0x20)} rounded={BitConverter.ToUInt64(hb,0x28)}");
}

/// <summary>Decrypts ALL outer PFS blocks (XTS) and writes plaintext to a file.</summary>
static void RunXtsDump(string pkgPath, string outPath)
{
    using var reader = new PkgReader(pkgPath, "00000000000000000000000000000000");
    _ = reader.ListFiles(); // triggers EKPFS decrypt
    var h = reader.Header;
    long pfs = (long)h.PfsImageOffset;
    long nb = (long)h.PfsImageSize / 0x10000;
    byte[] seed = ReadAt(pkgPath, pfs + 0x370, 16);
    byte[] ekpfs = reader.Ekpfs ?? throw new Exception("no ekpfs");
    var (tk, dk) = OrbisPkgTool.Pfs.PfsReader.DeriveXtsKeys(
        new OrbisPkgTool.Pfs.PfsHeader { Mode = OrbisPkgTool.Pfs.PfsMode.Signed | OrbisPkgTool.Pfs.PfsMode.Encrypted | OrbisPkgTool.Pfs.PfsMode.UnknownFlagAlwaysSet, Seed = seed },
        ekpfs);
    using var ofs = File.Create(outPath);
    for (long b = 0; b < nb; b++)
    {
        byte[] block = ReadAt(pkgPath, pfs + b * 0x10000, 0x10000);
        // Skip XTS decrypt for plaintext blocks (0 and 4)
        bool plaintext = b == 0 || b == 4;
        if (!plaintext)
            for (int s = (int)(b * 16); s < (int)(b * 16) + 16; s++)
                OrbisPkgTool.Pfs.PfsReader.XtsDecryptSector(block, (s - (int)(b * 16)) * 0x1000, (ulong)s, dk!, tk!);
        ofs.Write(block);
    }
    Console.WriteLine($"Decrypted outer PFS written: {outPath} ({nb} blocks, {nb*0x10000} bytes)");
}

/// <summary>Dumps the inner PFS's flat path table as (hash, ino) pairs (diagnostic).</summary>
static void RunInnerFpt(string pkgPath)
{
    using var reader = new PkgReader(pkgPath);
    var files = reader.ListFiles();
    var inner = reader.InnerPfs;
    if (inner == null) { Console.WriteLine("no inner pfs"); return; }
    var fpt = inner.GetInode(1);
    if (fpt == null) { Console.WriteLine("no fpt inode"); return; }
    // dump the inner PFS header tail (0x360-0x390) for format comparison
    {
        using var fs = new FileStream(pkgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var hdr = ReadAt(pkgPath, 0, 0x5A0);
        _ = hdr;
        Console.WriteLine($"inner hdr 0x0-0x40: {Convert.ToHexString(inner.HeaderBytes.AsSpan(0x0, 0x40)).ToLowerInvariant()}");
        Console.WriteLine($"inner hdr 0x360-0x390: {Convert.ToHexString(inner.HeaderBytes.AsSpan(0x360, 0x30)).ToLowerInvariant()}");
        Console.WriteLine($"inner hdr 0x40-0xF0: {Convert.ToHexString(inner.HeaderBytes.AsSpan(0x40, 0xB0)).ToLowerInvariant()}");
        Console.WriteLine($"inner hdr decoded: version={BitConverter.ToInt64(inner.HeaderBytes, 0)} magic={BitConverter.ToInt64(inner.HeaderBytes, 8)} fmode={inner.HeaderBytes[0x18]} clean={inner.HeaderBytes[0x19]} ro={inner.HeaderBytes[0x1A]} rsv={inner.HeaderBytes[0x1B]} mode=0x{BitConverter.ToUInt16(inner.HeaderBytes, 0x1C):X} blocksz=0x{BitConverter.ToUInt32(inner.HeaderBytes, 0x20):X} nbackup={BitConverter.ToUInt32(inner.HeaderBytes, 0x24)} nblock={BitConverter.ToInt64(inner.HeaderBytes, 0x28)} ndinode={BitConverter.ToInt64(inner.HeaderBytes, 0x30)} ndblock={BitConverter.ToInt64(inner.HeaderBytes, 0x38)} ndinodeblock={BitConverter.ToInt64(inner.HeaderBytes, 0x40)}");
    }
    // dump the inner inode table + the unreferenced blocks 2 and 5
    for (int i = 0; i < Math.Min(4, inner.InodeCount); i++)
    {
        var ino = inner.GetInode((uint)i);
        if (ino == null) continue;
        Console.WriteLine($"  inner ino[{i}]: mode=0x{ino.Mode:X4} nlink={ino.Nlink} flags=0x{ino.Flags:X8} size={ino.Size} blocks={ino.Blocks} db0={ino.DirectBlocks[0]} db1={ino.DirectBlocks[1]}");
    }
    try
    {
        var b2 = inner.ReadBlockRaw(2);
        var b5 = inner.ReadBlockRaw(5);
        Console.WriteLine($"  inner block2 head: {Convert.ToHexString(b2.AsSpan(0, 16)).ToLowerInvariant()}");
        Console.WriteLine($"  inner block5 head: {Convert.ToHexString(b5.AsSpan(0, 16)).ToLowerInvariant()}");
    }
    catch (Exception ex) { Console.WriteLine($"  inner block dump failed: {ex.Message}"); }
    var uroot2 = inner.GetInode(2);
    if (uroot2 != null)
    {
        var ents = inner.ReadDirents(uroot2);
        Console.WriteLine($"inner uroot dirents: {ents.Count}");
        foreach (var d in ents.Take(6))
            Console.WriteLine($"  {d.Type} {d.Name} -> {d.InodeNumber}");
    }
    byte[] data = inner.ReadFileData(fpt);
    Console.WriteLine($"fpt size: {data.Length} bytes = {data.Length / 8} entries");
    for (int i = 0; i + 8 <= data.Length; i += 8)
    {
        uint hash = BitConverter.ToUInt32(data, i);
        uint ino = BitConverter.ToUInt32(data, i + 4);
        Console.WriteLine($"  {hash:X8} {ino & 0x0FFFFFFF} {ino >> 28}");
    }
}

/// <summary>Decrypts and hex-dumps one outer-PFS block (diagnostic).</summary>
/// <summary>Dumps one block of the DECOMPRESSED inner PFS (diagnostic).</summary>
static void RunInnerBlock(string pkgPath, string blockArg)
{
    int block = Convert.ToInt32(blockArg, 16);
    using var reader = new PkgReader(pkgPath);
    _ = reader.ListFiles(); // triggers inner PFS chain
    var inner = reader.InnerPfs;
    if (inner == null) { Console.Error.WriteLine("No inner PFS"); return; }
    var data = inner.ReadBlockRaw(block);
    for (int i = 0; i < 0x10000 && i < 0x200; i += 16)
        Console.WriteLine($"{i:X4}: {(Convert.ToHexString(data.AsSpan(i, Math.Min(16, data.Length - i))).ToLowerInvariant())}");
}

static void RunPfsBlock(string pkgPath, string blockArg)
{
    int block = Convert.ToInt32(blockArg, 16);
    using var reader = new PkgReader(pkgPath);
    _ = reader.ListFiles();
    var h = reader.Header;
    long pfs = (long)h.PfsImageOffset;
    byte[] seed = ReadAt(pkgPath, pfs + 0x370, 16);
    var (tk, dk) = OrbisPkgTool.Pfs.PfsReader.DeriveXtsKeys(
        new OrbisPkgTool.Pfs.PfsHeader { Mode = OrbisPkgTool.Pfs.PfsMode.Signed | OrbisPkgTool.Pfs.PfsMode.Encrypted | OrbisPkgTool.Pfs.PfsMode.UnknownFlagAlwaysSet, Seed = seed },
        reader.Ekpfs!);
    byte[] data = ReadAt(pkgPath, pfs + block * 0x10000, 0x10000);
    if (block > 0)
    {
        for (int s = block * 16; s < block * 16 + 16; s++)
            OrbisPkgTool.Pfs.PfsReader.XtsDecryptSector(data, (s - block * 16) * 0x1000, (ulong)s, dk!, tk!);
    }
    for (int i = 0; i < 0x10000 && i < 0x200; i += 16)
        Console.WriteLine($"{i:X4}: {(Convert.ToHexString(data.AsSpan(i, 16)).ToLowerInvariant())}");
}

/// <summary>
/// Verifies the PFS signature algorithm against a real FPKG: computes
/// HMAC-SHA256(signKey, block1) over plaintext and ciphertext and compares
/// with the stored superroot sig at PFS+0xB8.
/// </summary>
static void RunSignVerify(string pkgPath)
{
    using var reader = new PkgReader(pkgPath);
    _ = reader.ListFiles(); // triggers the lazy EKPFS chain
    var h = reader.Header;
    long pfs = (long)h.PfsImageOffset;
    byte[] seed = ReadAt(pkgPath, pfs + 0x370, 16);
    var ekpfs = reader.Ekpfs ?? throw new Exception("no ekpfs");
    byte[] signKey = HmacSha256x(ekpfs, ConcatBytes(Le32x(2), seed));
    byte[] storedSig = ReadAt(pkgPath, pfs + 0xB8, 32);
    byte[] block1c = ReadAt(pkgPath, pfs + 0x10000, 0x10000);
    var (tk, dk) = OrbisPkgTool.Pfs.PfsReader.DeriveXtsKeys(
        new OrbisPkgTool.Pfs.PfsHeader { Mode = OrbisPkgTool.Pfs.PfsMode.Signed | OrbisPkgTool.Pfs.PfsMode.Encrypted | OrbisPkgTool.Pfs.PfsMode.UnknownFlagAlwaysSet, Seed = seed },
        ekpfs);
    byte[] block1p = (byte[])block1c.Clone();
    for (int s = 16; s < 32; s++)
        OrbisPkgTool.Pfs.PfsReader.XtsDecryptSector(block1p, (s - 16) * 0x1000, (ulong)s, dk!, tk!);
    var hmacP = HmacSha256x(signKey, block1p);
    var hmacC = HmacSha256x(signKey, block1c);
    // also: signKey with index 1 and a few other candidates
    Console.WriteLine($"seed      = {Convert.ToHexString(seed)}");
    Console.WriteLine($"signKey   = {Convert.ToHexString(signKey[..8])}...");
    Console.WriteLine($"stored    = {Convert.ToHexString(storedSig)}");
    Console.WriteLine($"HMAC(plt) = {Convert.ToHexString(hmacP)}  match={hmacP.AsSpan().SequenceEqual(storedSig)}");
    Console.WriteLine($"HMAC(ct)  = {Convert.ToHexString(hmacC)}  match={hmacC.AsSpan().SequenceEqual(storedSig)}");
    // header sig at 0x380 vs HMAC of header[0..0x5A0] (sig region zeroed : it covers itself)
    byte[] storedHdr = ReadAt(pkgPath, pfs + 0x380, 32);
    byte[] hdrBlock = ReadAt(pkgPath, pfs, 0x5A0);
    for (int i = 0x380; i < 0x380 + 32; i++) hdrBlock[i] = 0;
    var hdrP = HmacSha256x(signKey, hdrBlock);
    Console.WriteLine($"hdr stored= {Convert.ToHexString(storedHdr)}");
    Console.WriteLine($"hdr HMAC0 = {Convert.ToHexString(hdrP)}  match={hdrP.AsSpan().SequenceEqual(storedHdr)}");

    // verify EVERY sig slot: header dinode db[0], all inode pointer sigs, indirect entries
    Console.WriteLine("verifying all sig slots...");
    try
    {
    CheckSig(pkgPath, pfs, signKey, tk, dk, "hdr db[0] (block 1)", 0x50 + 0x68, 1 * 0x10000);
    // inode table at block 1: S32 inodes at stride 0x2C8 (decrypt the block first)
    byte[] tblBlock = ReadAt(pkgPath, pfs + 0x10000, 0x10000);
    for (int s = 16; s < 32; s++)
        OrbisPkgTool.Pfs.PfsReader.XtsDecryptSector(tblBlock, (s - 16) * 0x1000, (ulong)s, dk!, tk!);
    for (int i = 0; i < 4; i++)
    {
        long tbl = pfs + 0x10000 + i * 0x2C8;
        byte[] ino = new byte[0x2C8];
        Buffer.BlockCopy(tblBlock, i * 0x2C8, ino, 0, 0x2C8);
        for (int d = 0; d < 12; d++)
        {
            int block = BitConverter.ToInt32(ino, 0x64 + d * 36 + 32);
            if (block <= 0) continue;
            CheckSig(pkgPath, pfs, signKey, tk, dk, $"ino[{i}] db[{d}] (block {block})", tbl + 0x64 + d * 36, block * 0x10000);
        }
        for (int d = 0; d < 5; d++)
        {
            int block = BitConverter.ToInt32(ino, 0x64 + 12 * 36 + d * 36 + 32);
            if (block <= 0) continue;
            CheckSig(pkgPath, pfs, signKey, tk, dk, $"ino[{i}] ib[{d}] (block {block})", tbl + 0x64 + 12 * 36 + d * 36, block * 0x10000);
        }
        // indirect block entries (36-byte stride) : decrypt the indirect block first
        for (int d = 0; d < 5; d++)
        {
            int ibBlock = BitConverter.ToInt32(ino, 0x64 + 12 * 36 + d * 36 + 32);
            if (ibBlock <= 0) continue;
            byte[] ib = ReadAt(pkgPath, pfs + ibBlock * 0x10000, 0x10000);
            for (int s = ibBlock * 16; s < ibBlock * 16 + 16; s++)
                OrbisPkgTool.Pfs.PfsReader.XtsDecryptSector(ib, (s - ibBlock * 16) * 0x1000, (ulong)s, dk!, tk!);
            for (int e = 0; e < 1820; e++)
            {
                int blk = BitConverter.ToInt32(ib, e * 36 + 32);
                if (blk <= 0) break;
                CheckSig(pkgPath, pfs, signKey, tk, dk, $"ib[{d}] entry[{e}] (block {blk})", pfs + ibBlock * 0x10000 + e * 36, blk * 0x10000);
            }
        }
    }
    Console.WriteLine("sig check done.");

    // brute-force the inode-pointer sig scheme on the first real inode sig:
    // try key indices 0..6 over plaintext and ciphertext of the referenced block.
    byte[] ino3 = new byte[0x2C8];
    Buffer.BlockCopy(tblBlock, 3 * 0x2C8, ino3, 0, 0x2C8);
    int targetBlock = BitConverter.ToInt32(ino3, 0x64 + 32); // db[0] of the file inode
    long tSlot = pfs + 0x10000 + 3 * 0x2C8 + 0x64;
    byte[] storedIno = ReadAt(pkgPath, pfs + tSlot, 32);
    byte[] rawBlock = ReadAt(pkgPath, pfs + targetBlock * 0x10000, 0x10000);
    byte[] plainBlock = (byte[])rawBlock.Clone();
    for (int s = targetBlock * 16; s < targetBlock * 16 + 16; s++)
        OrbisPkgTool.Pfs.PfsReader.XtsDecryptSector(plainBlock, (s - targetBlock * 16) * 0x1000, (ulong)s, dk!, tk!);
    // debug: ino[0] db[0] (block 2) : stored vs candidates
    {
        byte[] ino0 = new byte[0x2C8];
        Buffer.BlockCopy(tblBlock, 0, ino0, 0, 0x2C8);
        byte[] sig02 = new byte[32];
        Buffer.BlockCopy(ino0, 0x64, sig02, 0, 32);
        byte[] b2raw = ReadAt(pkgPath, pfs + 0x20000, 0x10000);
        byte[] b2p = (byte[])b2raw.Clone();
        for (int s = 32; s < 48; s++)
            OrbisPkgTool.Pfs.PfsReader.XtsDecryptSector(b2p, (s - 32) * 0x1000, (ulong)s, dk!, tk!);
        Console.WriteLine($"ino[0] db[0] stored: {Convert.ToHexString(sig02)}");
        Console.WriteLine($"  HMAC(idx2, plaintext)  = {Convert.ToHexString(HmacSha256x(signKey, b2p))}");
        Console.WriteLine($"  HMAC(idx2, ciphertext) = {Convert.ToHexString(HmacSha256x(signKey, b2raw))}");
        Console.WriteLine($"  HMAC(idx2, plain[0..0x38]) = {Convert.ToHexString(HmacSha256x(signKey, b2p.AsSpan(0, 0x38).ToArray()))}");
    }
    Console.WriteLine($"ino[3] db[0] stored sig: {Convert.ToHexString(storedIno)}");
    // debug for MY pkg: which block does the stored ino[3] db[0] sig cover?
    {
        Console.WriteLine($"  ino[3] db[0]: stored = {Convert.ToHexString(storedIno)}");
        byte[] b7raw = ReadAt(pkgPath, pfs + 7 * 0x10000, 0x10000);
        byte[] b7p = (byte[])b7raw.Clone();
        for (int s = 112; s < 128; s++)
            OrbisPkgTool.Pfs.PfsReader.XtsDecryptSector(b7p, (s - 112) * 0x1000, (ulong)s, dk!, tk!);
        Console.WriteLine($"  decrypted block 7 head: {Convert.ToHexString(b7p.AsSpan(0, 16))}");
        Console.WriteLine($"  HMAC(decrypted block 7) = {Convert.ToHexString(HmacSha256x(signKey, b7p))}");
        Console.WriteLine($"  HMAC(ciphertext block 7) = {Convert.ToHexString(HmacSha256x(signKey, b7raw))}");
        // also: HMAC over the plaintext with the XTS keys as HMAC key (weird variant)
        Console.WriteLine($"  HMAC(decrypted block 7, ekpfs key) = {Convert.ToHexString(HmacSha256x(ekpfs, b7p))}");
        // the reader's own view of the file's first block
        try {
            var img = reader.ExtractEntryBytes("Image0/pfs_image.dat");
            var imgFirst = img.AsSpan(0, Math.Min(0x10000, img.Length)).ToArray();
            Console.WriteLine($"  reader's pfs_image.dat[0..16]: {Convert.ToHexString(imgFirst.AsSpan(0, 16))}");
            Console.WriteLine($"  HMAC(reader's first block)    = {Convert.ToHexString(HmacSha256x(signKey, imgFirst))}");
        } catch { Console.WriteLine("  (inner PFS entry not found : orbis-built PKG)"); }
    }
    for (int ki = 0; ki < 7; ki++)
    {
        var k = HmacSha256x(ekpfs, ConcatBytes(Le32x(ki), seed));
        var hP = HmacSha256x(k, plainBlock);
        var hC = HmacSha256x(k, rawBlock);
        if (hP.AsSpan().SequenceEqual(storedIno)) Console.WriteLine($"  MATCH: key index {ki}, PLAINTEXT");
        if (hC.AsSpan().SequenceEqual(storedIno)) Console.WriteLine($"  MATCH: key index {ki}, CIPHERTEXT");
    }
    }
    catch (Exception ex) { Console.Error.WriteLine("sigcheck: " + ex); }
}

static void CheckSig(string pkgPath, long pfs, byte[] signKey, byte[] tk, byte[] dk, string what, long slot, long blockOffset)
{
    byte[] stored = ReadAt(pkgPath, pfs + slot, 32);
    byte[] data = ReadAt(pkgPath, pfs + blockOffset, 0x10000);
    int firstSector = (int)(blockOffset / 0x1000);
    for (int s = firstSector; s < firstSector + 16; s++)
        OrbisPkgTool.Pfs.PfsReader.XtsDecryptSector(data, (s - firstSector) * 0x1000, (ulong)s, dk!, tk!);
    var calc = HmacSha256x(signKey, data);
    if (!calc.AsSpan().SequenceEqual(stored))
        Console.WriteLine($"  SIG FAIL: {what} (slot 0x{slot:X}, block @0x{blockOffset:X})");
}

static byte[] HmacSha256x(byte[] key, byte[] data)
{
    using var h = new System.Security.Cryptography.HMACSHA256(key);
    return h.ComputeHash(data);
}

static byte[] Le32x(int v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };

static void RunSelfTest()
{
    Console.WriteLine("PkgKeySet self-test (n = p*q, e*d ≡ 1 mod phi):");
    bool dk3Ok = PkgKeySet.ValidateKeypair(PkgKeySet.Standard.DerivedKey3);
    bool fakeOk = PkgKeySet.ValidateKeypair(PkgKeySet.Standard.FakeKeyset);
    Console.WriteLine($"  DerivedKey3 (key index 3): {(dk3Ok ? "OK" : "FAILED")}");
    Console.WriteLine($"  FakeKeyset              : {(fakeOk ? "OK" : "FAILED")}");
    if (!dk3Ok || !fakeOk)
    {
        Console.Error.WriteLine("[error] Embedded RSA key constants are inconsistent.");
        Environment.ExitCode = 1;
        return;
    }

    // Derivation sanity: show a deterministic dk3 sample.
    Console.WriteLine("DeriveKey smoke test:");
    var dk = PkgCrypto.DeriveKey("EP0001-CUSA00001_00-0000000000000000", PkgReader.DefaultPasscode, 3);
    Console.WriteLine($"  dk3 = {Convert.ToHexString(dk[..8]).ToLowerInvariant()}... ({dk.Length} bytes)");
    Console.WriteLine("selftest complete.");
}

static void RunInspect(string pkgPath)
{
    using var reader = new PkgReader(pkgPath);
    var files = reader.ListFiles();
    Console.WriteLine($"  EKPFS recovered: {reader.EkpfsStatus}");
    Console.WriteLine($"  PFS layer error: {reader.LastPfsError ?? "(none)"}");
    Console.WriteLine($"Total entries: {files.Count} (dirs: {files.Count(f => f.IsDirectory)}, files: {files.Count(f => !f.IsDirectory)})");
    DumpRawPfs(reader);
    DumpInnerPfs(reader);
}

/// <summary>Dumps the inner PFS (Image0) header and a sample file inode.</summary>
static void DumpInnerPfs(PkgReader reader)
{
    try
    {
        var target = reader.ListFiles().FirstOrDefault(f => f.Path.Contains(".pos") || f.Path == "Image0/eboot.bin");
        if (target == null) return;
        byte[] data = reader.ExtractEntryBytes(target.Path);
        Console.WriteLine($"  Inner PFS file {target.Path}: extracted {data.Length} bytes (size reported {target.Size})");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Inner PFS file extraction failed: {ex}");
    }
    // Dump the inner PFS header + the target file's inode via reflection-free direct access
    try
    {
        using var fs = new FileStream(reader.PkgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var br = new OrbisPkgTool.Binary.BigEndianReader(fs);
        var h = reader.Header;
        // Re-open outer + inner to inspect
        var ekpfs = reader.Ekpfs;
        var outer = OrbisPkgTool.Pfs.PfsReader.Open(br, (long)h.PfsImageOffset, ekpfs);
        var innerFile = outer.FindFile("pfs_image.dat");
        var innerStream = outer.OpenFileStream(innerFile!);
        byte[] probe = new byte[4];
        innerStream.Position = 0;
        innerStream.Read(probe, 0, 4);
        if (probe[0] == (byte)'P') innerStream = new OrbisPkgTool.Pfs.PFSCStream(innerStream);
        var inner = OrbisPkgTool.Pfs.PfsReader.Open(new OrbisPkgTool.Binary.BigEndianReader(innerStream), 0, ekpfs);
        var ph = inner.Header;
        Console.WriteLine($"  Inner PFS: mode=0x{(ushort)ph.Mode:X} dinodes={ph.DinodeCount} ndblock={ph.Ndblock}");
        for (int i = 0; i < Math.Min(5, inner.InodeCount); i++)
        {
            var ino = inner.GetInode((uint)i);
            if (ino == null) continue;
            Console.WriteLine($"    inode[{i}]: mode=0x{ino.Mode:X4} flags=0x{ino.Flags:X8} size={ino.Size} blocks={ino.Blocks} db0={ino.DirectBlocks[0]} db1={ino.DirectBlocks[1]} ib0={ino.IndirectBlocks[0]}");
        }
        var uroot = inner.GetInode(2);
        if (uroot != null)
        {
            Console.WriteLine("    uroot dirents:");
            foreach (var d in inner.ReadDirents(uroot))
                Console.WriteLine($"      type={d.Type} ino={d.InodeNumber} \"{d.Name}\"");
        }
        for (int i = 3; i < Math.Min(7, inner.InodeCount); i++)
        {
            var dino = inner.GetInode((uint)i);
            if (dino == null || !dino.IsDirectory) continue;
            Console.WriteLine($"    dir inode[{i}] ({dino.DirectBlocks[0]}) dirents:");
            foreach (var d in inner.ReadDirents(dino))
                Console.WriteLine($"      type={d.Type} ino={d.InodeNumber} \"{d.Name}\"");
        }
        var posFile = inner.FindFile("Media/GI/level1/46/46abf687f084539c26cde7d2380ecb1d.pos");
        if (posFile != null)
            Console.WriteLine($"    .pos inode: mode=0x{posFile.Mode:X4} flags=0x{posFile.Flags:X8} size={posFile.Size} blocks={posFile.Blocks} db0={posFile.DirectBlocks[0]} db1={posFile.DirectBlocks[1]} db2={posFile.DirectBlocks[2]}");
        var eboot = inner.FindFile("eboot.bin");
        if (eboot != null)
            Console.WriteLine($"    eboot.bin inode: mode=0x{eboot.Mode:X4} flags=0x{eboot.Flags:X8} size={eboot.Size} sizeUnc={eboot.SizeCompressed} blocks={eboot.Blocks} db0={eboot.DirectBlocks[0]} db1={eboot.DirectBlocks[1]}");
        var arch = inner.FindFile("archive.psarc");
        if (arch != null)
            Console.WriteLine($"    archive.psarc inode: mode=0x{arch.Mode:X4} flags=0x{arch.Flags:X8} size={arch.Size} sizeUnc={arch.SizeCompressed} blocks={arch.Blocks}");
        // Dump the PFSC header from the RAW file data stream
        {
            var rawStream = outer.OpenFileStream(innerFile!);
            var pfscHdr = OrbisPkgTool.Pfs.PFSCHeader.Read(ReadBytesAt(rawStream, 0, 0x30));
            Console.WriteLine($"    PFSC(raw): blockSize=0x{pfscHdr.BlockSize:X} table@0x{pfscHdr.BlockTableOffset:X} data@0x{pfscHdr.BlockDataOffset:X} rounded={pfscHdr.RoundedFileSize}");
            var table = ReadBytesAt(rawStream, (long)pfscHdr.BlockTableOffset, 16);
            Console.WriteLine($"    PFSC table[0..1] = {BitConverter.ToInt64(table, 0)} {BitConverter.ToInt64(table, 8)}");
            var t4380 = ReadBytesAt(rawStream, (long)pfscHdr.BlockTableOffset + 4380 * 8, 16);
            Console.WriteLine($"    PFSC table[4380..4381] = {BitConverter.ToInt64(t4380, 0)} {BitConverter.ToInt64(t4380, 8)} (file len {rawStream.Length})");
        }
        // Dump the pfs_image.dat inode's block pointers
        {
            var imgIno = outer.GetInode(3);
            if (imgIno != null)
            {
                Console.WriteLine($"    pfs_image.dat inode: blocks={imgIno.Blocks} db={string.Join(",", imgIno.DirectBlocks.Take(3))} ib={string.Join(",", imgIno.IndirectBlocks.Take(3))}");
                // Read the first indirect block and dump its first pointers
                if (imgIno.IndirectBlocks[0] > 0)
                {
                    byte[] ibRaw = outer.ReadBlock(imgIno.IndirectBlocks[0]);
                    Console.WriteLine($"    indirect[0] first 64 bytes: {Convert.ToHexString(ibRaw[..64]).ToLowerInvariant()}");
                    Console.WriteLine($"    indirect[0] 4-byte stride: {BitConverter.ToInt32(ibRaw, 0)} {BitConverter.ToInt32(ibRaw, 4)} {BitConverter.ToInt32(ibRaw, 8)} {BitConverter.ToInt32(ibRaw, 12)} {BitConverter.ToInt32(ibRaw, 16)} {BitConverter.ToInt32(ibRaw, 20)}");
                    byte[] ib1 = outer.ReadBlock(imgIno.IndirectBlocks[1]);
                    Console.WriteLine($"    indirect[1] 36-stride: {BitConverter.ToInt32(ib1, 32)} {BitConverter.ToInt32(ib1, 68)} {BitConverter.ToInt32(ib1, 104)} {BitConverter.ToInt32(ib1, 140)} {BitConverter.ToInt32(ib1, 176)}");
                    Console.WriteLine($"    indirect[1] first 64: {Convert.ToHexString(ib1[..64]).ToLowerInvariant()}");
                }
            }
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Inner PFS dump failed: {ex.Message}");
    }
}

/// <summary>Opens the outer PFS directly and prints its header + first inodes (diagnostic).</summary>
static void DumpRawPfs(PkgReader reader)
{
    try
    {
        var h = reader.Header;
        using var fs = new FileStream(reader.PkgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        var pfs = OrbisPkgTool.Pfs.PfsReader.Open(new OrbisPkgTool.Binary.BigEndianReader(fs), (long)h.PfsImageOffset, reader.Ekpfs);
        var ph = pfs.Header;
        Console.WriteLine($"  Outer PFS: version={ph.Version} magic={ph.Magic} mode=0x{(ushort)ph.Mode:X} blockSize=0x{ph.BlockSize:X}");
        Console.WriteLine($"    dinodes={ph.DinodeCount} ndblock={ph.Ndblock} dinodeBlocks={ph.DinodeBlockCount} seed={Convert.ToHexString(ph.Seed)}");
        for (int i = 0; i < Math.Min(4, pfs.InodeCount); i++)
        {
            var ino = pfs.GetInode((uint)i);
            if (ino == null) continue;
            Console.WriteLine($"    inode[{i}]: mode=0x{ino.Mode:X4} flags=0x{ino.Flags:X8} size={ino.Size} blocks={ino.Blocks} db0={ino.DirectBlocks[0]} db1={ino.DirectBlocks[1]}");
        }
        // superroot dirents (block 2 via inode 0)
        var super = pfs.GetInode(0);
        if (super != null)
        {
            Console.WriteLine("    superroot dirents:");
            foreach (var d in pfs.ReadDirents(super))
                Console.WriteLine($"      {d.Type} {d.Name} -> inode {d.InodeNumber}");
        }
        // fpt content (block 3)
        var fpt = pfs.GetInode(1);
        if (fpt != null)
        {
            var fptData = pfs.ReadFileData(fpt);
            Console.WriteLine($"    fpt ({fptData.Length} bytes): {Convert.ToHexString(fptData).ToLowerInvariant()}");
        }
        // uroot dirents
        var root = pfs.GetInode(2);
        if (root != null)
        {
            Console.WriteLine($"    uroot dirents:");
            foreach (var d in pfs.ReadDirents(root))
                Console.WriteLine($"      {d.Type} {d.Name} -> inode {d.InodeNumber}");
        }
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  Raw PFS dump failed: {ex.Message}");
    }
}

/// <summary>
/// Brute-forces the AES-XTS parameters for the PFS layer: tries tweak
/// endianness, key-derivation index, and key split against the known
/// superroot inode signature (mode 0x416D == bytes 6D 41 at block 1).
/// </summary>
static void RunXtsTest(string pkgPath)
{
    using var reader = new PkgReader(pkgPath);
    _ = reader.ListFiles(); // triggers the lazy EKPFS chain
    if (reader.Ekpfs == null)
    {
        Console.WriteLine("[error] No EKPFS recovered.");
        return;
    }
    var h = reader.Header;
    using var fs = new FileStream(pkgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
    var br = new OrbisPkgTool.Binary.BigEndianReader(fs);
    byte[] block1 = br.ReadBytesAt((long)h.PfsImageOffset + 0x10000, 0x10000);
    var seed = br.ReadBytesAt((long)h.PfsImageOffset + 0x370, 16);
    // Scan decrypted inode table for known mode signatures to find the real inode stride.
    {
        var (tk, dk) = OrbisPkgTool.Pfs.PfsReader.DeriveXtsKeys(
            new OrbisPkgTool.Pfs.PfsHeader { Mode = OrbisPkgTool.Pfs.PfsMode.Encrypted | OrbisPkgTool.Pfs.PfsMode.Signed, Seed = seed },
            reader.Ekpfs);
        byte[] dec = (byte[])block1.Clone();
        for (int sector = 16; sector < 32; sector++)
            DecryptSector(dec, (sector - 16) * 0x1000, (ulong)sector, dk!, tk!, true);
        Console.WriteLine("  Inode-mode signatures in decrypted block 1:");
        for (int off = 0; off < dec.Length - 8; off += 2)
        {
            ushort m = (ushort)(dec[off] | (dec[off + 1] << 8));
            if (m == 0x416D || m == 0x816D)
                Console.WriteLine($"    offset 0x{off:X4}: mode=0x{m:X4} nlink={dec[off + 2] | (dec[off + 3] << 8)} flags=0x{(uint)(dec[off + 4] | (dec[off + 5] << 8) | (dec[off + 6] << 16) | (dec[off + 7] << 24)):X8}");
        }
        Console.WriteLine($"    dec block1[0..128]: {Convert.ToHexString(dec[..128]).ToLowerInvariant()}");
        Console.WriteLine($"    raw block1[0..64]:  {Convert.ToHexString(block1[..64]).ToLowerInvariant()}");
        // Shifted-tweak theory: encrypted region starts at block 1 with tweak 0
        foreach (int blk in new[] { 1, 2, 3, 4 })
        {
            byte[] b = br.ReadBytesAt((long)h.PfsImageOffset + (long)blk * 0x10000, 0x10000);
            for (int sector = 0; sector < 16; sector++)
                DecryptSector(b, sector * 0x1000, (ulong)((blk - 1) * 16 + sector), dk!, tk!, true);
            string ascii = System.Text.Encoding.ASCII.GetString(b);
            int pfsc = ascii.IndexOf("PFSC", StringComparison.Ordinal);
            int fpt = ascii.IndexOf("flat_path_table", StringComparison.Ordinal);
            int ur = ascii.IndexOf("uroot", StringComparison.Ordinal);
            ushort m0 = (ushort)(b[0] | (b[1] << 8));
            Console.WriteLine($"    shifted-tweak block {blk}: mode@0=0x{m0:X4} PFSC@{pfsc} flat_path_table@{fpt} uroot@{ur}");
        }
        // Try other HMAC indices as the XTS keys (sign key = index 2)
        foreach (int ki in new[] { 0, 2, 3 })
        {
            var k = Hmac(reader.Ekpfs!, Concat(Le(ki), seed));
            byte[] t4 = (byte[])block1.Clone();
            DecryptSector(t4, 0, 16, k[16..], k[..16], true);
            ushort m4 = (ushort)(t4[0] | (t4[1] << 8));
            Console.WriteLine($"    hmacIdx={ki} tweak16: mode=0x{m4:X4}");
        }
        // OEX test: P_j = C_j XOR AES_encrypt(key, LE128(byteOffset_j))
        foreach (string oexKey in new[] { "tweakKey", "dataKey" })
        {
            byte[] oex = (byte[])block1.Clone();
            DecryptOex(oex, 0x10000, oexKey == "tweakKey" ? tk! : dk!);
            ushort om = (ushort)(oex[0] | (oex[1] << 8));
            long osz = BitConverter.ToInt64(oex, 8);
            long oszu = BitConverter.ToInt64(oex, 16);
            Console.WriteLine($"    OEX({oexKey}): mode=0x{om:X4} size={osz} sizeUnc={oszu} time1={BitConverter.ToInt64(oex, 0x18)}");
        }
        // Dump the PFS header qwords for layout clues
        {
            byte[] hdr0 = br.ReadBytesAt((long)h.PfsImageOffset, 0x80);
            Console.WriteLine($"    header qwords: {Convert.ToHexString(hdr0).ToLowerInvariant()}");
            long srIno = BitConverter.ToInt64(hdr0, 0x48);
            Console.WriteLine($"    superroot_ino (0x48) = {srIno}");
        }
        // What tweak value produces the valid inode? Try sector 0..32, block index, etc.
        foreach (ulong tw in new ulong[] { 0, 1, 2, 15, 16, 17, 32, 256, 257 })
        {
            byte[] t3 = (byte[])block1.Clone();
            DecryptSector(t3, 0, tw, dk!, tk!, true);
            ushort m3 = (ushort)(t3[0] | (t3[1] << 8));
            Console.WriteLine($"    tweak={tw}: mode=0x{m3:X4} bytes={Convert.ToHexString(t3[..8]).ToLowerInvariant()}");
        }
        // Scan the first 2 MB of the PFS image for the PFSC magic after standard XTS
        {
            byte[] pfsc = System.Text.Encoding.ASCII.GetBytes("PFSC");
            byte[] buf = new byte[0x200000];
            for (long off = 0; off < buf.Length; off += 0x10000)
            {
                byte[] blk = br.ReadBytesAt((long)h.PfsImageOffset + off, 0x10000);
                for (int sector = (int)(off / 0x10000 * 16); sector < (int)(off / 0x10000 * 16 + 16); sector++)
                    DecryptSector(blk, (sector - (int)(off / 0x10000 * 16)) * 0x1000, (ulong)sector, dk!, tk!, true);
                Buffer.BlockCopy(blk, 0, buf, (int)off, 0x10000);
            }
            int pfscAt = FindBytes(buf, pfsc);
            Console.WriteLine($"    PFSC magic found at 0x{pfscAt:X} in decrypted PFS region");
        }
        // Dump the header's super_root_dinode (plaintext at 0x50) : sdi64 layout
        {
            byte[] hd = br.ReadBytesAt((long)h.PfsImageOffset + 0x50, 0x310);
            Console.WriteLine($"    header superroot: mode=0x{ReadLe16x(hd, 0):X4} nlink={ReadLe16x(hd, 2)} flags=0x{BitConverter.ToUInt32(hd, 4):X8} size={BitConverter.ToInt64(hd, 8)} sizeUnc={BitConverter.ToInt64(hd, 16)}");
            Console.WriteLine($"      ctime={BitConverter.ToInt64(hd, 0x30)} blockCount(sdi64)={BitConverter.ToInt64(hd, 0x60 + 96)}");
            // sdi64: 0x60 top + 96-byte header... direct blocks at 0x60+0x08? per TYPE_BEGIN sdinode64: block_count @0x00, direct @0x08
            for (int i = 0; i < 3; i++)
            {
                int off = 0x60 + 0x08 + i * 40;
                long blk = BitConverter.ToInt64(hd, off + 32);
                Console.WriteLine($"      direct[{i}].block = {blk}");
            }
        }
        // Rescan block 1 at 0x268 stride (PFS_SDINODE32_STRUCT_SIZE : the real on-disk size)
        {
            byte[] b1 = (byte[])block1.Clone();
            for (int sector = 16; sector < 32; sector++)
                DecryptSector(b1, (sector - 16) * 0x1000, (ulong)sector, dk!, tk!, true);
            for (int i = 0; i < 8; i++)
            {
                int off = i * 0x268;
                ushort m = (ushort)(b1[off] | (b1[off + 1] << 8));
                int nl = b1[off + 2] | (b1[off + 3] << 8);
                long sz = BitConverter.ToInt64(b1, off + 8);
                Console.WriteLine($"    inode[{i}] @0x{off:X4}: mode=0x{m:X4} nlink={nl} size={sz}");
            }
        }
        // PFS header InodeBlockSig (S64 at header offset 0x50) : points to the real inode table
        {
            byte[] hdr = br.ReadBytesAt((long)h.PfsImageOffset, 0x400);
            long hdrOff = 0x50;
            Console.WriteLine($"    header InodeBlockSig: mode=0x{ReadLe16x(hdr, (int)hdrOff):X4} nlink={ReadLe16x(hdr, (int)hdrOff + 2)} size={BitConverter.ToInt64(hdr, (int)hdrOff + 8)} blocks={BitConverter.ToInt64(hdr, (int)hdrOff + 96)}");
            for (int i = 0; i < 6; i++)
            {
                int off = (int)hdrOff + 108 + i * 40; // S64: 104 header + 4 pad; pairs of sig(32)+block(8)
                long blk = BitConverter.ToInt64(hdr, off + 32);
                Console.WriteLine($"      db[{i}] block = {blk}");
            }
            for (int i = 0; i < 3; i++)
            {
                int off = (int)hdrOff + 108 + 12 * 40 + i * 40;
                long blk = BitConverter.ToInt64(hdr, off + 32);
                Console.WriteLine($"      ib[{i}] block = {blk}");
            }
        }
        foreach (int off in new[] { 0x2C8, 0x590, 0x858, 0xB20 })
            Console.WriteLine($"    @0x{off:X4}: {Convert.ToHexString(dec[off..(off + 32)]).ToLowerInvariant()}");
        // Ground truth: superroot dirents at block 2 contain ASCII "flat_path_table"/"uroot".
        {
            byte[] block2 = br.ReadBytesAt((long)h.PfsImageOffset + 0x20000, 0x10000);
            byte[] dec2 = (byte[])block2.Clone();
            for (int sector = 32; sector < 48; sector++)
                DecryptSector(dec2, (sector - 32) * 0x1000, (ulong)sector, dk!, tk!, true);
            string ascii = System.Text.Encoding.ASCII.GetString(dec2);
            int fpt = ascii.IndexOf("flat_path_table", StringComparison.Ordinal);
            int ur = ascii.IndexOf("uroot", StringComparison.Ordinal);
            Console.WriteLine($"    block2 std-XTS: flat_path_table@{fpt} uroot@{ur}");
            byte[] hex = Convert.FromHexString("666c6174"); // "flat"
            int raw = FindBytes(block2, hex);
            Console.WriteLine($"    block2 raw 'flat' @ {raw}");
        }
        // Recover the REQUIRED tweak for block 1: T1 = C1 XOR D_K2(P1),
        // where P1's first 8 bytes = size_compressed = 0x0000000000010000.
        {
            byte[] c1 = new byte[16];
            Buffer.BlockCopy(block1, 16, c1, 0, 16);
            using var da = System.Security.Cryptography.Aes.Create();
            da.Mode = System.Security.Cryptography.CipherMode.ECB;
            da.Padding = System.Security.Cryptography.PaddingMode.None;
            da.Key = dk;
            byte[] p1 = new byte[16];
            p1[0] = 0; p1[1] = 0; p1[2] = 1; p1[3] = 0; p1[4] = 0; p1[5] = 0; p1[6] = 0; p1[7] = 0;
            byte[] dP1;
            using (var daDec = da.CreateDecryptor()) dP1 = daDec.TransformFinalBlock(p1, 0, 16);
            byte[] t1 = new byte[16];
            for (int i = 0; i < 16; i++) t1[i] = (byte)(c1[i] ^ dP1[i]);
            Console.WriteLine($"    required T1 = {Convert.ToHexString(t1).ToLowerInvariant()}");
            // T0 = AES_K1(LE128(16))
            byte[] t0 = new byte[16];
            t0[0] = 16;
            using (var ta = System.Security.Cryptography.Aes.Create())
            {
                ta.Mode = System.Security.Cryptography.CipherMode.ECB;
                ta.Padding = System.Security.Cryptography.PaddingMode.None;
                ta.Key = tk;
                using (var enc = ta.CreateEncryptor()) enc.TransformBlock(t0, 0, 16, t0, 0);
            }
            Console.WriteLine($"    T0 (AES(16)) = {Convert.ToHexString(t0).ToLowerInvariant()}");
            // T0 * x (standard GF advance)
            byte[] t0x = (byte[])t0.Clone();
            byte msb = (byte)(t0x[0] >> 7);
            for (int i = 0; i < 15; i++) t0x[i] = (byte)((t0x[i] << 1) | (t0x[i + 1] >> 7));
            t0x[15] = (byte)(t0x[15] << 1);
            if (msb == 1) t0x[15] ^= 0x87;
            Console.WriteLine($"    T0*x        = {Convert.ToHexString(t0x).ToLowerInvariant()}");
            bool same = t1.AsSpan().SequenceEqual(t0x);
            Console.WriteLine($"    T1 == T0*x ? {same}");
            // Brute-force: which tweak construction produces the required T1?
            byte[] candidate = new byte[16];
            // a) T0 * x^k for k = 1..2048
            byte[] tpow = (byte[])t0.Clone();
            for (int k = 1; k <= 2048; k++)
            {
                byte msb2 = (byte)(tpow[0] >> 7);
                for (int i = 0; i < 15; i++) tpow[i] = (byte)((tpow[i] << 1) | (tpow[i + 1] >> 7));
                tpow[15] = (byte)(tpow[15] << 1);
                if (msb2 == 1) tpow[15] ^= 0x87;
                if (tpow.AsSpan().SequenceEqual(t1)) { Console.WriteLine($"    T1 == T0 * x^{k}"); break; }
            }
            // b) AES(16+k) LE for k = 1..2048
            using (var ta2 = System.Security.Cryptography.Aes.Create())
            {
                ta2.Mode = System.Security.Cryptography.CipherMode.ECB;
                ta2.Padding = System.Security.Cryptography.PaddingMode.None;
                ta2.Key = tk;
                using var enc = ta2.CreateEncryptor();
                for (int k = 1; k <= 2048; k++)
                {
                    byte[] inp = new byte[16];
                    ulong v = (ulong)(16 + k);
                    for (int i = 0; i < 8; i++) inp[i] = (byte)(v >> (8 * i));
                    enc.TransformBlock(inp, 0, 16, candidate, 0);
                    if (candidate.AsSpan().SequenceEqual(t1)) { Console.WriteLine($"    T1 == AES(16+{k})"); break; }
                }
            }
            // c) AES(sector*16+j) LE : absolute 16-byte block index
            using (var ta3 = System.Security.Cryptography.Aes.Create())
            {
                ta3.Mode = System.Security.Cryptography.CipherMode.ECB;
                ta3.Padding = System.Security.Cryptography.PaddingMode.None;
                ta3.Key = tk;
                using var enc = ta3.CreateEncryptor();
                for (int k = 1; k <= 2048; k++)
                {
                    byte[] inp = new byte[16];
                    ulong v = (ulong)(16 * 16 + k);
                    for (int i = 0; i < 8; i++) inp[i] = (byte)(v >> (8 * i));
                    enc.TransformBlock(inp, 0, 16, candidate, 0);
                    if (candidate.AsSpan().SequenceEqual(t1)) { Console.WriteLine($"    T1 == AES(block {16 * 16 + k})"); break; }
                }
            }
        }
        // Test alternate per-block tweak advances: decrypt inode[0] region
        // and check size_compressed == 0x10000 (bytes 16-23 = 00 00 01 00 00 00 00 00).
        foreach (string variant in new[] { "noAdvance", "mulxRev", "add1", "add16", "xor1", "xexAdd", "ctr16", "reencryptBlockLE", "reencryptBlockBE" })
        {
            byte[] test2 = (byte[])block1.Clone();
            DecryptSectorVariant(test2, 16, dk!, tk!, variant);
            bool ok = test2[16] == 0 && test2[17] == 0 && test2[18] == 1 && test2[19] == 0 && test2[20] == 0 && test2[21] == 0 && test2[22] == 0 && test2[23] == 0;
            Console.WriteLine($"    variant {variant}: size_compressed={BitConverter.ToInt64(test2, 16)} {(ok ? "*** MATCH ***" : "")}");
            if (ok)
                Console.WriteLine($"    inode[0] full: {Convert.ToHexString(test2[..32]).ToLowerInvariant()}");
        }
    }

    Console.WriteLine($"seed = {Convert.ToHexString(seed).ToLowerInvariant()}");
    Console.WriteLine($"ekpfs = {Convert.ToHexString(reader.Ekpfs).ToLowerInvariant()}");

    foreach (int hmacIndex in new[] { 0, 1, 2, 3 })
    {
        var encKey = Hmac(reader.Ekpfs, Concat(Le(hmacIndex), seed));
        foreach (bool leTweak in new[] { true, false })
        {
            foreach (int split in new[] { 16, 32 })
            {
                byte[] tweakKey = encKey[..split];
                byte[] dataKey = encKey[split..];
                if (dataKey.Length < 16) continue;
                if (split == 32 && dataKey.Length < 32) continue;
                byte[] test = (byte[])block1.Clone();
                DecryptSector(test, 0, 16, dataKey, tweakKey, leTweak);
                ushort mode = (ushort)(test[0] | (test[1] << 8));
                bool sig = mode == 0x416D;
                Console.WriteLine($"idx={hmacIndex} tweak={(leTweak ? "LE" : "BE")} split={split}: mode=0x{mode:X4} {(sig ? "*** SIGNATURE MATCH ***" : "")}");
                if (sig)
                {
                    Console.WriteLine($"  inode[0] bytes: {Convert.ToHexString(test[..32]).ToLowerInvariant()}");
                }
            }
        }
    }
}

static void DecryptSectorVariant(byte[] data, int sector, byte[] dataKey, byte[] tweakKey, string variant)
{
    // Decrypts the 0x1000-byte sector starting at (sector*0x1000 - 0x10000)
    // with alternate per-block tweak advances. Caller passes the PFS sector
    // index and data is a block starting at pfsOffset.
    using var tweakAes = System.Security.Cryptography.Aes.Create();
    tweakAes.Mode = System.Security.Cryptography.CipherMode.ECB;
    tweakAes.Padding = System.Security.Cryptography.PaddingMode.None;
    tweakAes.Key = tweakKey;
    using var dataAes = System.Security.Cryptography.Aes.Create();
    dataAes.Mode = System.Security.Cryptography.CipherMode.ECB;
    dataAes.Padding = System.Security.Cryptography.PaddingMode.None;
    dataAes.Key = dataKey;
    using var dec = dataAes.CreateDecryptor();
    var block = new byte[16];
    byte[] baseTweak = new byte[16];
    for (int i = 0; i < 8; i++) baseTweak[i] = (byte)(sector >> (8 * i));
    using (var enc = tweakAes.CreateEncryptor())
        enc.TransformBlock(baseTweak, 0, 16, baseTweak, 0);

    int sectorOff = (sector - 16) * 0x1000; // within the block buffer
    for (int j = 0; j < 256; j++)
    {
        byte[] tweak = new byte[16];
        switch (variant)
        {
            case "noAdvance":
                Buffer.BlockCopy(baseTweak, 0, tweak, 0, 16);
                break;
            case "add1":
                Buffer.BlockCopy(baseTweak, 0, tweak, 0, 16);
                AddLe(tweak, j);
                break;
            case "add16":
                Buffer.BlockCopy(baseTweak, 0, tweak, 0, 16);
                AddLe(tweak, j * 16);
                break;
            case "xor1":
                Buffer.BlockCopy(baseTweak, 0, tweak, 0, 16);
                tweak[0] ^= (byte)j;
                break;
            case "xexAdd":
                Buffer.BlockCopy(baseTweak, 0, tweak, 0, 16);
                for (int k = 0; k < 8; k++) tweak[k] ^= (byte)(j >> (8 * k));
                break;
            case "ctr16":
                Buffer.BlockCopy(baseTweak, 0, tweak, 0, 16);
                for (int k = 0; k < 8; k++) tweak[k] ^= (byte)((j * 16) >> (8 * k));
                break;
            case "reencryptBlockLE":
            case "reencryptBlockBE":
            {
                ulong idx = (ulong)sector * 16 + (ulong)j;
                if (variant.EndsWith("LE"))
                    for (int i = 0; i < 8; i++) tweak[i] = (byte)(idx >> (8 * i));
                else
                    for (int i = 0; i < 8; i++) tweak[15 - i] = (byte)(idx >> (8 * i));
                using (var enc = tweakAes.CreateEncryptor())
                    enc.TransformBlock(tweak, 0, 16, tweak, 0);
                break;
            }
            case "mulxRev":
            {
                // standard multiply-by-x but shifting the other direction
                Buffer.BlockCopy(baseTweak, 0, tweak, 0, 16);
                for (int k = 0; k < j; k++)
                {
                    byte msb = (byte)(tweak[15] >> 7);
                    for (int i = 15; i > 0; i--) tweak[i] = (byte)((tweak[i] << 1) | (tweak[i - 1] >> 7));
                    tweak[0] = (byte)(tweak[0] << 1);
                    if (msb == 1) tweak[0] ^= 0x87;
                }
                break;
            }
            default:
                break;
        }
        int off = sectorOff + j * 16;
        for (int i = 0; i < 16; i++) block[i] = (byte)(data[off + i] ^ tweak[i]);
        dec.TransformBlock(block, 0, 16, block, 0);
        for (int i = 0; i < 16; i++) data[off + i] = (byte)(block[i] ^ tweak[i]);
    }
}

static void DecryptOex(byte[] data, ulong startOffset, byte[] key)
{
    using var aes = System.Security.Cryptography.Aes.Create();
    aes.Mode = System.Security.Cryptography.CipherMode.ECB;
    aes.Padding = System.Security.Cryptography.PaddingMode.None;
    aes.Key = key;
    using var enc = aes.CreateEncryptor();
    var tweak = new byte[16];
    ulong offset = startOffset;
    for (int j = 0; j < data.Length / 16; j++)
    {
        Array.Clear(tweak);
        for (int i = 0; i < 8; i++) tweak[i] = (byte)(offset >> (8 * i));
        enc.TransformBlock(tweak, 0, 16, tweak, 0);
        for (int i = 0; i < 16; i++) data[j * 16 + i] ^= tweak[i];
        offset += 16;
    }
}

static byte[] ReadBytesAt(Stream s, long offset, int count)
{
    long old = s.Position;
    s.Position = offset;
    var b = new byte[count];
    int read = 0;
    while (read < count) { int n = s.Read(b, read, count - read); if (n <= 0) break; read += n; }
    s.Position = old;
    return b;
}

static ushort ReadLe16x(byte[] b, int off) => (ushort)(b[off] | (b[off + 1] << 8));

static int FindBytes(byte[] haystack, byte[] needle)
{
    for (int i = 0; i <= haystack.Length - needle.Length; i++)
    {
        bool ok = true;
        for (int j = 0; j < needle.Length; j++)
            if (haystack[i + j] != needle[j]) { ok = false; break; }
        if (ok) return i;
    }
    return -1;
}

static void AddLe(byte[] v, long add)
{
    for (int k = 0; k < 8 && add > 0; k++) { v[k] += (byte)add; add >>= 8; }
}

static byte[] Hmac(byte[] key, byte[] data)
{
    using var h = new System.Security.Cryptography.HMACSHA256(key);
    return h.ComputeHash(data);
}

static byte[] Concat(byte[] a, byte[] b)
{
    var r = new byte[a.Length + b.Length];
    Buffer.BlockCopy(a, 0, r, 0, a.Length);
    Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
    return r;
}

static byte[] Le(int v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };

static void DecryptSector(byte[] data, int offset, ulong sector, byte[] dataKey, byte[] tweakKey, bool leTweak)
{
    byte[] tweak = new byte[16];
    if (leTweak)
    {
        for (int i = 0; i < 8; i++) tweak[i] = (byte)(sector >> (8 * i));
    }
    else
    {
        for (int i = 0; i < 8; i++) tweak[15 - i] = (byte)(sector >> (8 * i));
    }
    using var tweakAes = System.Security.Cryptography.Aes.Create();
    tweakAes.Mode = System.Security.Cryptography.CipherMode.ECB;
    tweakAes.Padding = System.Security.Cryptography.PaddingMode.None;
    tweakAes.Key = tweakKey;
    using (var enc = tweakAes.CreateEncryptor())
        enc.TransformBlock(tweak, 0, 16, tweak, 0);

    using var dataAes = System.Security.Cryptography.Aes.Create();
    dataAes.Mode = System.Security.Cryptography.CipherMode.ECB;
    dataAes.Padding = System.Security.Cryptography.PaddingMode.None;
    dataAes.Key = dataKey;
    using var dec = dataAes.CreateDecryptor();
    var block = new byte[16];
    for (int off = offset; off < offset + 0x1000; off += 16)
    {
        for (int i = 0; i < 16; i++) block[i] = (byte)(data[off + i] ^ tweak[i]);
        dec.TransformBlock(block, 0, 16, block, 0);
        for (int i = 0; i < 16; i++) data[off + i] = (byte)(block[i] ^ tweak[i]);
        // GF multiply by x (LE-first convention: carry XORed at byte 0)
        int fb = 0;
        for (int k = 0; k < 16; k++)
        {
            byte t2 = tweak[k];
            tweak[k] = (byte)((tweak[k] << 1) | fb);
            fb = (t2 & 0x80) >> 7;
        }
        if (fb != 0) tweak[0] ^= 0x87;
    }
}

static void PrintUsage()
{
    Console.WriteLine(@"
OrbisPkgTool.Cli : PS4 PKG command-line tool
  Default passcode: 00000000000000000000000000000000

  Commands:
    list       : List files in a PKG              (list -h for details)
    extract    : Extract files from a PKG         (extract -h for details)
    verify     : Verify PKG hashes/signatures
    info       : Show PKG metadata
    inspect    : Full PFS tree dump
    build      : Build a fake PKG from GP4        (build -h for details)
    gp4gen     : Generate GP4 from a folder       (gp4gen -h for details)
    sweep      : Batch verify PKGs in a folder
    bench      : Benchmark listing speed
    selftest   : Validate RSA keys
    sfo        : param.sfo tools                  (sfo -h for details)
    trp        : Trophy TRP tools                 (trp -h for details)

  Diagnostic:
    signverify, pfsdump, pfsblock, innerfpt, iblock,
    fixdigests, resignpfs, xtstest, buildtest, emptypayload
");
}

static void PrintCommandHelp(string cmd)
{
    var h = Console.Out;
    switch (cmd)
    {
        case "list": case "img_list": case "img_file_list":
            h.WriteLine(@"
list : List all files and directories in a PKG

  Usage:
    list [--passcode <32ch>] [--oformat short|long+original_size|packed_size] <pkg>

  Examples:
    list game.pkg
    list --oformat long+original_size game.pkg
    list --passcode 00000000000000000000000000000000 game.pkg
"); break;
        case "extract": case "img_extract":
            h.WriteLine(@"
extract : Extract files from a PKG

  Usage:
    extract [--passcode <32ch>] [--verbose|-v] <pkg> <out_dir>
    extract [--passcode <32ch>] <pkg>:<entry_path> <out_dir>

  Options:
    --verbose, -v   Show per-file progress and percentage

  Examples:
    extract game.pkg out/
    extract --verbose game.pkg out/
    extract game.pkg:Sc0/param.sfo out/
    extract game.pkg:Image0/eboot.bin out/
"); break;
        case "verify":
            h.WriteLine(@"
verify : Verify all PKG header hashes and signatures (fast, CPU only)

  Usage:  verify <pkg>
"); break;
        case "info": case "pkginfo":
            h.WriteLine(@"
info : Show PKG metadata (title, content ID, category, version, size)

  Usage:  info <pkg>
"); break;
        case "inspect": case "debug":
            h.WriteLine(@"
inspect : Full PFS tree dump (outer + inner), useful for debugging

  Usage:  inspect [--passcode <32ch>] <pkg>
"); break;
        case "build": case "pkg":
            h.WriteLine(@"
build : Build a fake PKG from a GP4 project
  Generates orbis-compatible GP4 + param.sfo, delegates to orbis-pub-cmd img_create.

  Usage:
    build <project.gp4> <source_folder> [--passcode <32ch>] [--out <file.pkg>]

  Examples:
    build game.gp4 ./files --out game.pkg
    build game.gp4 ./files --passcode 00000000000000000000000000000000
"); break;
        case "gp4gen": case "gp4":
            h.WriteLine(@"
gp4gen : Scan a folder and generate a GP4 project file

  Usage:
    gp4gen <folder> [--patch] [--title ""Name""] [--title-id CUSA00001]
            [--content-id EP0001-CUSA00001_00-MYGAME000000001]
            [--passcode <32ch>] [--out <file.gp4>]

  Example:
    gp4gen ./game --title ""My Game"" --title-id CUSA00001 --out game.gp4
"); break;
        case "sweep":
            h.WriteLine(@"
sweep : Batch verify all .pkg files in a folder

  Usage:  sweep <folder> [--list] [--out <results.tsv>]
"); break;
        case "sfo":
            h.WriteLine(@"
sfo : param.sfo tools

  sfo read <file.sfo>              Read and display SFO entries
  sfo create <out.sfo> [options]   Create new param.sfo
       --title ""Name"" --title-id CUSA00001
       --content-id EP0001-CUSA00001_00-MYGAME000000001
       --category gd|ac|gp
  sfo set <file.sfo> <key> <val>   Set SFO entry
  sfo check <file.sfo>             Validate SFO format
"); break;
        case "trp":
            h.WriteLine(@"
trp : Trophy TRP tools

  trp list <file.trp>              List TRP entries
  trp extract <file.trp> [dir]     Extract TRP to directory
  trp create <out.trp> <files..>   Create TRP from files
"); break;
        default:
            h.WriteLine($"No detailed help for '{cmd}'. Run without arguments to see all commands.");
            break;
    }
}
