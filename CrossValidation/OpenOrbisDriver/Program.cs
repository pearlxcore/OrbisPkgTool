using System.IO.MemoryMappedFiles;
using System.Reflection;
using LibOrbisPkg.PKG;
using LibOrbisPkg.PFS;

// OpenOrbisDriver — test-only cross-validation participant (C).
// Drives the OpenOrbis fork of LibOrbisPkg (local LibOrbisPkg.Core.dll) as an
// INDEPENDENT implementation for reading/extracting/validating PKGs.

// ── top-level entry ─────────────────────────────────────────────────────

if (args.Length == 0)
{
    Console.Error.WriteLine("usage: OpenOrbisDriver <info|list|extract|extract-inner|validate|dumpapi> <pkg> [...]");
    return 1;
}

string cmd = args[0].ToLowerInvariant();
if (cmd == "dumpapi") return Driver.DumpApi();
if (cmd == "build") return Driver.Build(args);

if (args.Length < 2)
{
    Console.Error.WriteLine("usage: OpenOrbisDriver <info|list|extract|extract-inner|validate|build> <pkg|gp4> [...]");
    return 1;
}

string pkgPath = args[1];

try
{
    using var holder = new PkgHolder(pkgPath);
    switch (cmd)
    {
        case "info": return Driver.Info(holder.Pkg);
        case "list": return Driver.List(holder.Inner);
        case "extract": return Driver.Extract(holder.Inner, args.Length > 2 ? args[2] : ".");
        case "extract-inner": return Driver.ExtractInner(holder, args[2]);
        case "validate": return Driver.Validate(holder.Pkg, pkgPath);
        default:
            Console.Error.WriteLine($"unknown command: {cmd}");
            return 2;
    }
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex}");
    return 1;
}

// ── types ────────────────────────────────────────────────────────────────

/// <summary>Keeps the MemoryMappedFile + accessor alive for the reader's lifetime.</summary>
sealed class PkgHolder : IDisposable
{
    public Pkg Pkg;
    public PfsReader Outer;
    public PfsReader Inner;
    private readonly MemoryMappedFile _mmf;
    private readonly MemoryMappedViewAccessor _acc;

    public PkgHolder(string pkgPath)
    {
        // FileShare.ReadWrite so other validation tools can hold the file too.
        var shared = new FileStream(pkgPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        _mmf = MemoryMappedFile.CreateFromFile(shared, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, leaveOpen: false);
        using (var s = _mmf.CreateViewStream(0, 0, MemoryMappedFileAccess.Read))
            Pkg = new PkgReader(s).ReadPkg();
        byte[] ekpfs = Pkg.GetEkpfs();
        long off = (long)Pkg.Header.pfs_image_offset;
        long size = (long)Pkg.Header.pfs_image_size;
        _acc = _mmf.CreateViewAccessor(off, size, MemoryMappedFileAccess.Read);
        Outer = new PfsReader(_acc, Pkg.Header.pfs_flags, ekpfs, null, null);
        var innerFile = Outer.GetFile("pfs_image.dat")
            ?? throw new InvalidOperationException("pfs_image.dat not found");
        Inner = new PfsReader(new PFSCReader(innerFile.GetView()));
    }

    public void Dispose()
    {
        _acc.Dispose();
        _mmf.Dispose();
    }
}

static class Driver
{
    public static int Info(Pkg pkg)
    {
        Console.WriteLine($"contentId: {pkg.Header.content_id}");
        Console.WriteLine($"entryCount: {pkg.Header.entry_count}");
        if (pkg.Metas?.Metas != null)
            foreach (var m in pkg.Metas.Metas)
                Console.WriteLine($"  0x{(uint)m.id:X8} flags1=0x{m.Flags1:X8} flags2=0x{m.Flags2:X8} off=0x{m.DataOffset:X8} size={m.DataSize} enc={m.Encrypted} key={m.KeyIndex} id={m.id}");
        return 0;
    }

    public static int List(PfsReader inner)
    {
        int n = 0;
        void Walk(PfsReader.Dir dir, string prefix)
        {
            foreach (var child in dir.children)
            {
                string path = prefix.Length == 0 ? child.name : $"{prefix}/{child.name}";
                Console.WriteLine($"{(child is PfsReader.Dir ? "D" : "F")}  {path}");
                if (child is PfsReader.Dir d) Walk(d, path);
                n++;
            }
        }
        var root = inner.GetURoot();
        if (root != null) Walk(root, "");
        Console.Error.WriteLine($"files: {n}");
        return 0;
    }

    public static int Extract(PfsReader inner, string outDir)
    {
        Directory.CreateDirectory(outDir);
        int n = 0;
        void Walk(PfsReader.Dir dir, string prefix)
        {
            foreach (var child in dir.children)
            {
                string rel = prefix.Length == 0 ? child.name : $"{prefix}/{child.name}";
                if (child is PfsReader.Dir d) Walk(d, rel);
                else if (child is PfsReader.File f)
                {
                    string dest = Path.Combine(outDir, rel.Replace('/', Path.DirectorySeparatorChar));
                    Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                    f.Save(dest, false);
                    n++;
                }
            }
        }
        var root = inner.GetURoot();
        if (root != null) Walk(root, "");
        Console.Error.WriteLine($"extracted: {n}");
        return 0;
    }

    public static int ExtractInner(PkgHolder holder, string outPath)
    {
        // Raw decompressed inner PFS via the PFSC reader (mirrors pkg_extractinnerpfs).
        var innerFile = holder.Outer.GetFile("pfs_image.dat")
            ?? throw new InvalidOperationException("pfs_image.dat not found");
        using var v = innerFile.GetView();
        using var pfsc = new PFSCReader(v);
        using var f = File.Create(outPath);
        var buf = new byte[1 << 20];
        long wrote = 0;
        long size = innerFile.compressed_size;
        while (wrote < size)
        {
            int toWrite = (int)Math.Min(size - wrote, buf.Length);
            pfsc.Read(wrote, buf, 0, toWrite);
            f.Write(buf, 0, toWrite);
            wrote += toWrite;
        }
        Console.Error.WriteLine($"inner: {wrote} bytes");
        return 0;
    }

    public static int Validate(Pkg pkg, string pkgPath)
    {
        using var fs = new FileStream(pkgPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var results = new PkgValidator(pkg).Validate(fs).ToList();
        int fail = 0;
        foreach (var (validation, res) in results)
        {
            Console.WriteLine($"  {res} {validation.Type} {validation.Name} @0x{validation.Location:X} {validation.Description}");
            if (res != LibOrbisPkg.PKG.PkgValidator.ValidationResult.Ok) fail++;
        }
        Console.Error.WriteLine($"validations: {results.Count} failures: {fail}");
        return fail == 0 ? 0 : 1;
    }

    /// <summary>build &lt;gp4&gt; &lt;source_folder&gt; &lt;out.pkg&gt; [passcode] — OpenOrbis PkgBuilder.</summary>
    public static int Build(string[] args)
    {
        if (args.Length < 4)
        {
            Console.Error.WriteLine("usage: OpenOrbisDriver build <project.gp4> <source_folder> <out.pkg> [passcode]");
            return 1;
        }
        string gp4Path = args[1], folder = args[2], outPath = args[3];
        string passcode = args.Length > 4 ? args[4] : "00000000000000000000000000000000";
        using var fs = File.OpenRead(gp4Path);
        var gp4 = LibOrbisPkg.GP4.Gp4Project.ReadFrom(fs);
        var props = LibOrbisPkg.PKG.PkgProperties.FromGp4(gp4, folder);
        props.Passcode = passcode;
        if (props.TimeStamp == default) props.TimeStamp = DateTime.UtcNow;
        var builder = new LibOrbisPkg.PKG.PkgBuilder(props);
        builder.Write(outPath, s => Console.Error.WriteLine(s));
        Console.Error.WriteLine($"built: {new FileInfo(outPath).Length} bytes");
        return 0;
    }

    public static int DumpApi()
    {
        var asm = typeof(Pkg).Assembly;
        foreach (var t in asm.GetExportedTypes().OrderBy(t => t.FullName))
        {
            var members = t.GetMembers(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static | BindingFlags.DeclaredOnly)
                .Where(m => m.MemberType is MemberTypes.Method or MemberTypes.Field or MemberTypes.Property)
                .Select(m => m.MemberType switch
                {
                    MemberTypes.Method => $"M {(m as MethodInfo)!.ReturnType.Name} {m.Name}({(string.Join(",", (m as MethodInfo)!.GetParameters().Select(p => p.ParameterType.Name)))})\n",
                    MemberTypes.Field => $"F {(m as FieldInfo)!.FieldType.Name} {m.Name}\n",
                    MemberTypes.Property => $"P {(m as PropertyInfo)!.PropertyType.Name} {m.Name}\n",
                    _ => ""
                });
            Console.WriteLine($"TYPE {t.FullName} : {t.BaseType?.Name}");
            foreach (var m in members) Console.Write($"  {m}");
        }
        return 0;
    }
}
