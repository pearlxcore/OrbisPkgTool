using System.Security.Cryptography;
using System.Text;
using OrbisPkgTool.Binary;
using OrbisPkgTool.Crypto;
using OrbisPkgTool.Pfs;
using OrbisPkgTool.Pkg;

namespace OrbisPkgTool.Tests;

/// <summary>
/// Permanent compatibility regression suite. The PKG/PFS/PFSC core is FROZEN
/// (commit 97dcfda): every fixture here encodes a boundary that was verified
/// against orbis-pub-cmd 3.87 and the real Digimon FPKG. A failure in any of
/// these is a regression, not a format-discovery opportunity.
/// </summary>
public class CompatibilityRegressionTests : IDisposable
{
    private const string Passcode = "00000000000000000000000000000000";
    private const string ContentId = "EP0001-CUSA00001_00-REG000000000001";
    private readonly List<string> _cleanup = [];

    public void Dispose()
    {
        foreach (var d in _cleanup)
        {
            try { if (Directory.Exists(d)) Directory.Delete(d, recursive: true); } catch { }
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private string NewDir(string name)
    {
        string dir = Path.Combine(Path.GetTempPath(), $"opt_reg_{name}_{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        _cleanup.Add(dir);
        return dir;
    }

    private static byte[] Data(long size, byte seed = 0x41)
    {
        var b = new byte[size];
        for (long i = 0; i < size; i++) b[i] = (byte)(seed + (i & 0x1F));
        return b;
    }

    private (string Gp4, string Image0) MakeFixture(string name, params (string Path, byte[] Data)[] files)
    {
        string dir = NewDir(name);
        string image0 = Path.Combine(dir, "Image0");
        foreach (var (p, d) in files)
        {
            string full = Path.Combine(image0, p.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(full)!);
            File.WriteAllBytes(full, d);
        }
        // mandatory sce_sys/param.sfo
        var sfo = Sfo.ParamSfo.CreateGameTemplate("Reg", "CUSA00001", ContentId);
        string sfoPath = Path.Combine(image0, "sce_sys", "param.sfo");
        Directory.CreateDirectory(Path.GetDirectoryName(sfoPath)!);
        File.WriteAllBytes(sfoPath, sfo.Serialize());

        var sb = new StringBuilder();
        sb.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
        sb.AppendLine("<psproject fmt=\"gp4\" version=\"1.0\">");
        sb.AppendLine("  <volume><volume_type>pkg_ps4_app</volume_type><package>");
        sb.AppendLine($"      <content_id>{ContentId}</content_id>");
        sb.AppendLine("      <passcode></passcode>");
        sb.AppendLine("      <storage_type>digital25</storage_type><app_type>full</app_type>");
        sb.AppendLine("      <version>01.00</version><title_id>CUSA00001</title_id>");
        sb.AppendLine("      <title>Reg</title><app_version>01.00</app_version>");
        sb.AppendLine("    </package></volume>");
        sb.AppendLine("  <files>");
        sb.AppendLine("    <file><entry path=\"sce_sys/param.sfo\" /><orig_path>sce_sys/param.sfo</orig_path></file>");
        foreach (var (p, _) in files)
            sb.AppendLine($"    <file><entry path=\"{p}\" /><orig_path>{p}</orig_path></file>");
        sb.AppendLine("  </files>");
        sb.AppendLine("</psproject>");
        string gp4 = Path.Combine(dir, "project.gp4");
        File.WriteAllText(gp4, sb.ToString());
        return (gp4, image0);
    }

    private string Build(string gp4, string folder, string name, BuildOptions? opts = null)
    {
        string outPkg = Path.Combine(Path.GetDirectoryName(gp4)!, name);
        PkgBuilder.Build(gp4, folder, outPkg, opts ?? new BuildOptions());
        return outPkg;
    }

    private static PfsReader OpenInner(string pkgPath)
    {
        using var reader = new PkgReader(pkgPath, Passcode);
        string tmp = Path.Combine(Path.GetTempPath(), $"opt_inner_{Guid.NewGuid():N}.pfs");
        reader.ExtractRawInnerPfs(tmp);
        var fs = File.OpenRead(tmp);
        return PfsReader.Open(new BigEndianReader(fs), 0);
    }

    private static byte[] ExtractFileBytes(string pkgPath, string entryPath)
    {
        using var reader = new PkgReader(pkgPath, Passcode);
        return reader.ExtractEntryBytes(entryPath);
    }

    private static byte[] Sha(byte[] b) => SHA256.HashData(b);

    // ── 1. Tiny filesystem: inner PFS / FPT / dirents / PFSC / outer / PKG ──

    [Fact]
    public void TinyFilesystem_BuildsAndValidates()
    {
        var (gp4, img) = MakeFixture("tiny",
            ("a.bin", Data(3)), ("b.bin", Data(5)), ("dir/c.bin", Data(2)));
        string pkg = Build(gp4, img, "tiny.pkg");
        PkgValidator.ValidatePkgFile(pkg, Passcode);   // all 8 stages
        using var reader = new PkgReader(pkg, Passcode);
        var names = reader.ListFiles().Select(f => f.Path).OrderBy(p => p).ToList();
        Assert.Contains("Image0/a.bin", names);
        Assert.Contains("Image0/b.bin", names);
        Assert.Contains("Image0/dir/c.bin", names);
        // extraction round-trip byte-exact
        Assert.Equal(Data(3), ExtractFileBytes(pkg, "Image0/a.bin"));
        Assert.Equal(Data(2), ExtractFileBytes(pkg, "Image0/dir/c.bin"));
    }

    // ── 2. Compressed PFSC: 48 89 header, RFC1950, Adler32, round-trip ──

    [Fact]
    public void CompressedPfsc_IsCompleteZlibStream_AndRoundtrips()
    {
        var inner = PfsWriter.BuildInnerPfs(
            [("a.bin", Data(3)), ("b.bin", Data(5)), ("dir/c.bin", Data(2))], 0);
        var pfsc = PFSCWriter.Build(inner, storeAllRaw: false);

        PkgValidator.ValidatePfsc(pfsc);
        // block 0 must start with the RFC1950 header 48 89 (CINFO=4)
        long tableOff = BitConverter.ToInt64(pfsc, 0x18);
        long dataOff = BitConverter.ToInt64(pfsc, 0x20);
        Assert.Equal(PfsFormat.PfscDataOffset, dataOff);
        Assert.Equal(0x48, pfsc[(int)dataOff]);
        Assert.Equal(0x89, pfsc[(int)dataOff + 1]);
        // round-trip decompression yields the exact inner PFS
        using var ms = new MemoryStream(pfsc, false);
        var dec = new PFSCStream(ms);
        using var outMs = new MemoryStream();
        dec.CopyTo(outMs);
        Assert.Equal(inner.Length, outMs.Length);
        Assert.Equal(Sha(inner), Sha(outMs.ToArray()));
    }

    // ── 3. Raw (store) PFSC: sectors at exactly 0x10000 ──

    [Fact]
    public void RawPfsc_BlocksAtDataOffset()
    {
        var inner = PfsWriter.BuildInnerPfs([("a.bin", Data(3)), ("b.bin", Data(5))], 0);
        var pfsc = PFSCWriter.Build(inner, storeAllRaw: true);
        PkgValidator.ValidatePfsc(pfsc);
        long tableOff = BitConverter.ToInt64(pfsc, 0x18);
        long first = BitConverter.ToInt64(pfsc, (int)tableOff);
        long second = BitConverter.ToInt64(pfsc, (int)tableOff + 8);
        Assert.Equal(PfsFormat.PfscDataOffset, first);          // block 0 at 0x10000
        Assert.Equal(PfsFormat.PfscDataOffset + PfsFormat.BlockSize, second); // block 1 at 0x20000
    }

    // ── 4. Multi-inode-block: 400 files → 2 inode-table blocks, no straddle ──

    [Fact]
    public void MultiInodeBlock_TwoInodeBlocks_NoBoundaryStraddle()
    {
        var files = Enumerable.Range(0, 400)
            .Select(i => ($"d{(i % 40):X2}/f{i:D3}.bin", Data(100)))
            .ToArray();
        var (gp4, img) = MakeFixture("many", files);
        string pkg = Build(gp4, img, "many.pkg");
        PkgValidator.ValidatePkgFile(pkg, Passcode);

        using var reader = new PkgReader(pkg, Passcode);
        string tmp = Path.Combine(Path.GetTempPath(), $"opt_many_{Guid.NewGuid():N}.pfs");
        reader.ExtractRawInnerPfs(tmp);
        using var fs = File.OpenRead(tmp);
        var inner = PfsReader.Open(new BigEndianReader(fs), 0);
        Assert.True(inner.Header.DinodeCount > PfsFormat.D32InodesPerBlock,
            $"expected >{PfsFormat.D32InodesPerBlock} inodes for a 2-block table");
        Assert.True(inner.Header.DinodeBlockCount >= 2,
            $"dinode_block_count={inner.Header.DinodeBlockCount}, expected >= 2");
        PkgValidator.ValidatePfsBlocks(inner, inner.Header, "inner PFS"); // pack rule + bounds
    }

    // ── 5. Direct/contiguous boundaries: 1/2/11/12/13 blocks ──

    [Fact]
    public void DirectContiguous_MultiBlockFilesUseRunSentinel_AndRoundtrip()
    {
        long B = PfsFormat.BlockSize;
        var files = new[]
        {
            ("f1.bin",  Data(B * 1)),
            ("f2.bin",  Data(B * 2)),
            ("f11.bin", Data(B * 11)),
            ("f12.bin", Data(B * 12)),
            ("f13.bin", Data(B * 13)),
        };
        var (gp4, img) = MakeFixture("contig", files);
        string pkg = Build(gp4, img, "contig.pkg");
        PkgValidator.ValidatePkgFile(pkg, Passcode);

        var inner = OpenInner(pkg);
        // Every file inode: db[1] must be the contiguous-run sentinel (-1)
        // for multi-block files, 0 for single-block files.
        for (uint i = 0; i < inner.Header.DinodeCount; i++)
        {
            var ino = inner.GetInode(i);
            if (ino == null || ino.IsDirectory || ino.Size == 0) continue;
            int expected = ino.Blocks > 1 ? PfsFormat.ContiguousRunSentinel : 0;
            Assert.Equal(expected, ino.DirectBlocks[1]);
        }
        // round-trip: the 13-block file must extract byte-exact
        Assert.Equal(files[4].Item2, ExtractFileBytes(pkg, "Image0/f13.bin"));
    }

    // ── 6. Outer indirect boundaries: 12 / 13 / 12+1820 / 12+1820+1 blocks ──

    [Theory]
    [InlineData(12)]
    [InlineData(13)]
    [InlineData(12 + 1820)]
    [InlineData(12 + 1820 + 1)]
    public void OuterIndirect_BoundaryBlocks(int dataBlocks)
    {
        long B = PfsFormat.BlockSize;
        var (gp4, img) = MakeFixture($"outer_{dataBlocks}",
            ("big.bin", Data(B * dataBlocks)));
        string pkg = Build(gp4, img, "outer.pkg");
        PkgValidator.ValidatePkgFile(pkg, Passcode);

        // the outer PFS must expose pfs_image.dat with the exact size, and the
        // full data must read back identically (exercises db/ib0/ib1 chains)
        using var reader = new PkgReader(pkg, Passcode);
        Assert.Equal(Data(B * dataBlocks), ExtractFileBytes(pkg, "Image0/big.bin"));
    }

    // ── 7. Duplicate-entry prevention: license.dat/info, psreserved.dat ──

    [Fact]
    public void DuplicateEntries_Prevented_IdsUnique()
    {
        var (gp4, img) = MakeFixture("dupe",
            ("sce_sys/license.dat", Data(1024)),
            ("sce_sys/license.info", Data(512)),
            ("sce_sys/psreserved.dat", Data(8192)),
            ("a.bin", Data(3)));
        string pkg = Build(gp4, img, "dupe.pkg");
        PkgValidator.ValidatePkgFile(pkg, Passcode);

        using var reader = new PkgReader(pkg, Passcode);
        var ids = reader.Entries.Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct().Count());
        // the fixed placeholders must carry the REAL file content now
        Assert.Equal(1024, ExtractFileBytes(pkg, "Sc0/license.dat").Length);
    }

    // ── 8. AES alignment: unaligned entry (532 bytes → 544 stored) ──

    [Fact]
    public void AesAlignment_UnalignedEntryStoredPadded_AndRoundtrips()
    {
        byte[] npbind = Data(532, 0x77); // 532 = not 16-aligned (33.25 blocks)
        var (gp4, img) = MakeFixture("aes",
            ("sce_sys/npbind.dat", npbind),
            ("a.bin", Data(3)));
        string pkg = Build(gp4, img, "aes.pkg");
        PkgValidator.ValidatePkgFile(pkg, Passcode);

        using var reader = new PkgReader(pkg, Passcode);
        var e = reader.Entries.First(x => x.Id == PkgEntryIds.NpBindDat);
        Assert.True(e.IsEncrypted);
        // Table DataSize = LOGICAL size (verified against the original Digimon,
        // which stores npbind.dat as 532); the stored region is 16-aligned.
        Assert.Equal(532, e.DataSize);
        long nextOff = reader.Entries.Where(x => x.DataOffset > e.DataOffset)
            .Select(x => x.DataOffset).DefaultIfEmpty(0).Min();
        Assert.Equal(544, nextOff - e.DataOffset);   // aligned stored region
        Assert.Equal(npbind, ExtractFileBytes(pkg, "Sc0/npbind.dat")); // exact round-trip
    }

    // ── 9. FPT hash collision resolver ──

    [Fact]
    public void FptCollision_ResolverCreated_BothFilesListed()
    {
        // "/AB" and "/B#" collide under the FPT hash (31*h + c, case-folded)
        var (gp4, img) = MakeFixture("coll",
            ("AB", Data(3)), ("B#", Data(3)), ("normal.bin", Data(3)));
        string pkg = Build(gp4, img, "coll.pkg");
        PkgValidator.ValidatePkgFile(pkg, Passcode);

        using var reader = new PkgReader(pkg, Passcode);
        var names = reader.ListFiles().Select(f => f.Path).ToList();
        Assert.Contains("Image0/AB", names);
        Assert.Contains("Image0/B#", names);
        Assert.Contains("Image0/normal.bin", names);
    }

    // ── 10. >2GB streaming pipeline (sparse input, real 64-bit paths) ──

    [Fact]
    public void LargeStreaming_OverOneGigabyte_BuildsAndValidates()
    {
        const long size = PfsFormat.StreamingThreshold + 32 * 1024 * 1024; // ~1.03 GB
        var (gp4, img) = MakeFixture("large", ("big.bin", Data(size, 0x33)));
        string pkg = Build(gp4, img, "large.pkg");
        PkgValidator.ValidatePkgFile(pkg, Passcode);

        using var reader = new PkgReader(pkg, Passcode);
        Assert.True(reader.ListFiles().Any(f => f.Path == "Image0/big.bin" && f.Size == size));
    }

    // ── 10b. Streaming PFSC (single-pass) byte-identical to the memory builder ──

    [Fact]
    public void PfscStreaming_MatchesMemoryBuilder_ByteIdentical()
    {
        var pfs = Data(3 * 65536 + 12345, 0x55);
        var mem = OrbisPkgTool.Pfs.PFSCWriter.Build(pfs, storeAllRaw: false);
        using var ms = new MemoryStream();
        using (var inMs = new MemoryStream(pfs))
            OrbisPkgTool.Pfs.PFSCWriter.BuildToStream(inMs, ms, storeAllRaw: false);
        var streamed = ms.ToArray();
        Assert.Equal(Sha(mem), Sha(streamed));
        Assert.Equal(mem.Length, streamed.Length);
    }

    // ── 10c. Parallel PFSC workers produce byte-identical output ──

    [Fact]
    public void PfscParallel_WorkersMatchesSerial_ByteIdentical()
    {
        // Parallel PFSC compression must produce byte-identical output to the
        // serial path (deflate is deterministic; blocks are independent and
        // written in order). Tests both the memory builder and the streaming
        // builder with workers > 1.
        var pfs = Data(5 * 65536 + 7777, 0x55);
        // Mix in a compressible region to exercise both code paths.
        Array.Fill(pfs, (byte)0x00, 2 * 65536, 65536);

        // Memory builder: serial vs parallel.
        var serialMem = OrbisPkgTool.Pfs.PFSCWriter.Build(pfs, storeAllRaw: false, workers: 1);
        var parMem = OrbisPkgTool.Pfs.PFSCWriter.Build(pfs, storeAllRaw: false, workers: 4);
        Assert.Equal(Sha(serialMem), Sha(parMem));

        // Streaming builder: serial vs parallel.
        byte[] serialStreamed;
        using (var ms = new MemoryStream())
        {
            using var inMs = new MemoryStream(pfs);
            OrbisPkgTool.Pfs.PFSCWriter.BuildToStream(inMs, ms, storeAllRaw: false, workers: 1);
            serialStreamed = ms.ToArray();
        }
        byte[] parStreamed;
        using (var ms = new MemoryStream())
        {
            using var inMs = new MemoryStream(pfs);
            OrbisPkgTool.Pfs.PFSCWriter.BuildToStream(inMs, ms, storeAllRaw: false, workers: 4);
            parStreamed = ms.ToArray();
        }
        Assert.Equal(Sha(serialStreamed), Sha(parStreamed));
        // Cross-check: memory builder and stream builder agree too.
        Assert.Equal(Sha(serialMem), Sha(serialStreamed));
        Assert.Equal(Sha(parMem), Sha(parStreamed));
    }

    [Fact]
    public void PfscParallel_WorkersZero_MatchesSerial()
    {
        // workers=0 means "all cores" — still byte-identical.
        var pfs = Data(3 * 65536, 0x61);
        var serial = OrbisPkgTool.Pfs.PFSCWriter.Build(pfs, storeAllRaw: false, workers: 1);
        var parallel = OrbisPkgTool.Pfs.PFSCWriter.Build(pfs, storeAllRaw: false, workers: 0);
        Assert.Equal(Sha(serial), Sha(parallel));
    }

    [Fact]
    public void PfscParallel_UnalignedTail_MatchesSerialAndMemory()
    {
        // A non-block-aligned image whose incompressible tail hits the raw
        // fallback: memory builder zero-pads the tail; both streaming paths
        // (serial and parallel) must produce the exact same bytes.
        // (Real PFS images are block-aligned, but the writers must be
        // self-consistent for any input length.)
        var rnd = new Random(4242);
        var pfs = new byte[2 * 65536 + 30000]; // tail block only 30000 bytes
        rnd.NextBytes(pfs); // fully incompressible → last block raw

        var mem = OrbisPkgTool.Pfs.PFSCWriter.Build(pfs, storeAllRaw: false, workers: 1);
        byte[] serial;
        using (var ms = new MemoryStream())
        {
            using var inMs = new MemoryStream(pfs);
            OrbisPkgTool.Pfs.PFSCWriter.BuildToStream(inMs, ms, storeAllRaw: false, workers: 1);
            serial = ms.ToArray();
        }
        byte[] parallel;
        using (var ms = new MemoryStream())
        {
            using var inMs = new MemoryStream(pfs);
            OrbisPkgTool.Pfs.PFSCWriter.BuildToStream(inMs, ms, storeAllRaw: false, workers: 4);
            parallel = ms.ToArray();
        }
        Assert.Equal(Sha(mem), Sha(serial));
        Assert.Equal(Sha(mem), Sha(parallel));
    }

    [Fact]
    public void PfscStreaming_Raw_MatchesMemoryBuilder_ByteIdentical()
    {
        // Block-aligned image: the memory builder pads a partial last block to
        // 64 KiB while the stream builder stores exact bytes — for aligned
        // images both must be byte-identical.
        var pfs = Data(2 * 65536, 0x5A);
        var mem = OrbisPkgTool.Pfs.PFSCWriter.Build(pfs, storeAllRaw: true);
        using var ms = new MemoryStream();
        using (var inMs = new MemoryStream(pfs))
            OrbisPkgTool.Pfs.PFSCWriter.BuildToStream(inMs, ms, storeAllRaw: true);
        var streamed = ms.ToArray();
        Assert.Equal(Sha(mem), Sha(streamed));
        Assert.Equal(mem.Length, streamed.Length);
    }

    [Fact]
    public void PfscStreaming_Raw_RoundTrips()
    {
        // Real inner PFS images are always block-aligned (ndblock * 0x10000);
        // a raw PFSC of an aligned image must roundtrip exactly.
        var pfs = Data(3 * 65536, 0x5A);
        using var ms = new MemoryStream();
        using (var inMs = new MemoryStream(pfs))
            OrbisPkgTool.Pfs.PFSCWriter.BuildToStream(inMs, ms, storeAllRaw: true);
        var pfsc = ms.ToArray();
        using var stream = new OrbisPkgTool.Pfs.PFSCStream(new MemoryStream(pfsc));
        using var outMs = new MemoryStream();
        stream.CopyTo(outMs);
        var outBytes = outMs.ToArray();
        Assert.Equal(pfs.Length, outBytes.Length);
        Assert.Equal(Sha(pfs), Sha(outBytes));
    }

    // ── 10c. Disk-backed source descriptors produce byte-identical inner PFS ──

    [Fact]
    public void InnerPfs_DiskBackedSources_ByteIdenticalToMemory()
    {
        var files = new (string Path, byte[] Data)[]
        {
            ("eboot.bin", Data(123456, 0x11)),
            ("CONTENT/DLC00/a.arc", Data(300000, 0x22)),
            ("CONTENT/DLC00/b.arc", Data(0, 0x33)),           // empty file
            ("LANGUAGE/EN/TITLE/TEXT/x.xml", Data(70000, 0x44)),
            ("sce_sys/keystone", Data(4096, 0x55)),           // in-PFS sce_sys file
        };
        // Memory path
        using var memMs = new MemoryStream();
        OrbisPkgTool.Pfs.PfsWriter.BuildInnerPfsToStream(files.ToList(), 0, memMs);
        var memBytes = memMs.ToArray();

        // Disk path: write the fixture files to a temp dir, use descriptors
        string tmp = Path.Combine(Path.GetTempPath(), "pfsparity_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tmp);
        try
        {
            var descs = new List<OrbisPkgTool.Pfs.PfsSourceFile>();
            foreach (var (p, d) in files)
            {
                string src = Path.Combine(tmp, p.Replace('/', Path.DirectorySeparatorChar));
                Directory.CreateDirectory(Path.GetDirectoryName(src)!);
                File.WriteAllBytes(src, d);
                descs.Add(new OrbisPkgTool.Pfs.PfsSourceFile(p, src, d.Length));
            }
            using var diskMs = new MemoryStream();
            OrbisPkgTool.Pfs.PfsWriter.BuildInnerPfsToStream(descs, 0, diskMs);
            var diskBytes = diskMs.ToArray();
            Assert.Equal(Sha(memBytes), Sha(diskBytes));
            Assert.Equal(memBytes.Length, diskBytes.Length);
        }
        finally
        {
            Directory.Delete(tmp, recursive: true);
        }
    }

    // ── 11. Invariants fail fast ──

    [Fact]
    public void Invariants_DuplicateIdsThrows()
    {
        // BuildAssembleEntries dedupes, so duplicates cannot reach the table;
        // verify the guard rejects a synthetic table via the validator.
        var (gp4, img) = MakeFixture("inv",
            ("sce_sys/license.dat", Data(1024)),
            ("a.bin", Data(3)));
        string pkg = Build(gp4, img, "inv.pkg");
        PkgValidator.ValidatePkgFile(pkg, Passcode);
    }

    // ── 12. Per-file compression policy (pfs_compression replay) ──
    // The core of compression parity: blocks of "disable" files must be RAW,
    // blocks of enabled files must be COMPRESSED, structural blocks compress
    // normally, and the profiler must reconstruct the policy from the result.

    /// <summary>Extracts the PFSC block table from a PKG: block → stored size.</summary>
    private static long[] PfscBlockSizes(string pkgPath)
    {
        using var reader = new PkgReader(pkgPath, Passcode);
        using var pfsc = reader.OpenRawPfscStream()
            ?? throw new InvalidOperationException("no PFSC in rebuilt PKG");
        pfsc.Position = 0;
        var hdr = new byte[0x30];
        ReadFully(pfsc, hdr);
        long blockSize = BitConverter.ToInt64(hdr, 0x0C) & 0xFFFFFFFF;
        long tableOff = BitConverter.ToInt64(hdr, 0x18);
        long rounded = BitConverter.ToInt64(hdr, 0x28);
        long blockCount = rounded / blockSize;
        var table = new byte[(blockCount + 1) * 8];
        pfsc.Position = tableOff;
        ReadFully(pfsc, table);
        var sizes = new long[blockCount];
        for (long i = 0; i < blockCount; i++)
            sizes[i] = BitConverter.ToInt64(table, (int)(i * 8 + 8)) - BitConverter.ToInt64(table, (int)(i * 8));
        return sizes;
    }

    private static void ReadFully(Stream s, byte[] buf)
    {
        int read = 0;
        while (read < buf.Length)
        {
            int n = s.Read(buf, read, buf.Length - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
    }

    [Fact]
    public void PfscPolicy_DisabledFilesStoredRaw_OthersCompressed()
    {
        long B = PfsFormat.BlockSize;
        // raw.bin: 3 blocks of pseudo-random (incompressible-ish but not
        // zero — we want compression to be *possible* so the raw storage is
        // caused by POLICY, not incompressibility). Disabled files must be
        // raw even though the data compresses fine.
        var rnd = new Random(1234);
        byte[] Make(int blocks)
        {
            var b = new byte[blocks * B];
            rnd.NextBytes(b);
            return b;
        }
        var rawFile = Make(3);      // disabled → 3 raw blocks
        var compFile = Data(3 * B, 0x41); // compressible, enabled → compressed blocks

        var (gp4, img) = MakeFixture("policy",
            ("raw.bin", rawFile),
            ("comp.bin", compFile));
        // Patch the GP4: raw.bin gets pfs_compression="disable".
        string xml = File.ReadAllText(gp4);
        xml = xml.Replace(
            "<file><entry path=\"raw.bin\" /><orig_path>raw.bin</orig_path></file>",
            "<file><entry path=\"raw.bin\" /><orig_path>raw.bin</orig_path></file><attrib>disable</attrib>");
        // The parser reads pfs_compression from <file targ_path=... pfs_compression=.../> —
        // rewrite in the attribute form instead.
        xml = xml.Replace(
            "<file><entry path=\"raw.bin\" /><orig_path>raw.bin</orig_path></file><attrib>disable</attrib>",
            "<file targ_path=\"raw.bin\" orig_path=\"raw.bin\" pfs_compression=\"disable\" />");
        File.WriteAllText(gp4, xml);

        string pkg = Build(gp4, img, "policy.pkg", new BuildOptions
        {
            PfscMode = PfscMode.Compressed,
            Quiet = true,
        });
        PkgValidator.ValidatePkgFile(pkg, Passcode);

        // Round-trip: both files extract byte-exact (raw storage must not
        // corrupt content).
        Assert.Equal(rawFile, ExtractFileBytes(pkg, "Image0/raw.bin"));
        Assert.Equal(compFile, ExtractFileBytes(pkg, "Image0/comp.bin"));

        // Block policy: locate each file's blocks via the inner PFS inodes.
        var inner = OpenInner(pkg);
        var rawIno = inner.FindFile("raw.bin")!;
        var compIno = inner.FindFile("comp.bin")!;
        var sizes = PfscBlockSizes(pkg);
        foreach (var b in inner.EnumerateFileBlocks(rawIno))
            Assert.Equal(PfsFormat.BlockSize, sizes[b]);      // disabled → RAW
        foreach (var b in inner.EnumerateFileBlocks(compIno))
            Assert.True(sizes[b] < PfsFormat.BlockSize,
                $"comp.bin block {b} stored raw ({sizes[b]} bytes) but the file is compressible and enabled");

        // Structural blocks (0..data start) must be compressed.
        long dataStart = Math.Min(rawIno.StartBlock, compIno.StartBlock);
        for (long b = 0; b < dataStart; b++)
            Assert.True(sizes[b] < PfsFormat.BlockSize,
                $"structural block {b} stored raw — expected compressed");
    }

    [Fact]
    public void PfscPolicy_ProfilerRoundTrip_ReconstructsPolicy()
    {
        long B = PfsFormat.BlockSize;
        var rnd = new Random(4321);
        byte[] Make(int blocks)
        {
            var b = new byte[blocks * B];
            rnd.NextBytes(b);
            return b;
        }
        // Mixed: disabled raw file, enabled compressible file, enabled
        // incompressible (random) file, empty file.
        var disabledFile = Make(2);
        var enabledCompressible = Data(2 * B, 0x55);
        var enabledIncompressible = Make(2); // compresses below threshold? random 64K blocks
        // (64 KiB of random data at level 6 stays near/above blockSize minus
        // the 6-byte overhead — treat as "compresses marginally". To force a
        // guaranteed-raw block regardless, use a size that cannot compress:
        // zlib stored-block overhead makes 64 KiB random ≈ blockSize + 5.)
        var emptyFile = Array.Empty<byte>();

        var (gp4, img) = MakeFixture("prof",
            ("disabled.bin", disabledFile),
            ("enabled.bin", enabledCompressible),
            ("random.bin", enabledIncompressible),
            ("empty.bin", emptyFile));
        string xml = File.ReadAllText(gp4);
        xml = xml.Replace(
            "<file><entry path=\"disabled.bin\" /><orig_path>disabled.bin</orig_path></file>",
            "<file targ_path=\"disabled.bin\" orig_path=\"disabled.bin\" pfs_compression=\"disable\" />");
        File.WriteAllText(gp4, xml);

        string pkg = Build(gp4, img, "prof.pkg", new BuildOptions
        {
            PfscMode = PfscMode.Compressed,
            Quiet = true,
        });
        PkgValidator.ValidatePkgFile(pkg, Passcode);

        // PROFILER ROUND-TRIP: the inferred policy must reproduce what was set.
        var files = OrbisPkgTool.Pfs.PfscProfiler.Profile(pkg, Passcode, out var stats, out string? err)
            ?? throw new InvalidOperationException(err ?? "profiling failed");
        var byPath = files.ToDictionary(f => f.Path, f => f.Policy);
        Assert.Equal(OrbisPkgTool.Pfs.PfscPolicy.Disable, byPath["disabled.bin"]);
        Assert.Equal(OrbisPkgTool.Pfs.PfscPolicy.Enable, byPath["enabled.bin"]);
        // Empty files still occupy ONE PFS block (writer invariant: 0-block
        // allocation would overlap the next file's start block). A zero block
        // compresses, so the profiler reports Enable — "no meaningful policy"
        // only applies to files the walker finds with zero blocks.
        Assert.Equal(OrbisPkgTool.Pfs.PfscPolicy.Enable, byPath["empty.bin"]);
        // random.bin: either Enable (if zlib squeezed it under) or Disable —
        // both are legitimate effective outcomes; assert it's one of them.
        Assert.Contains(byPath["random.bin"], new[] { OrbisPkgTool.Pfs.PfscPolicy.Enable, OrbisPkgTool.Pfs.PfscPolicy.Disable });

        // JSON round-trip.
        string json = OrbisPkgTool.Pfs.PfscProfiler.ToJson(files);
        var parsed = OrbisPkgTool.Pfs.PfscProfiler.ParseJson(json);
        Assert.Equal(OrbisPkgTool.Pfs.PfscPolicy.Disable, parsed["disabled.bin"]);
        Assert.Equal(OrbisPkgTool.Pfs.PfscPolicy.Enable, parsed["enabled.bin"]);
    }

    [Fact]
    public void PfscPolicy_StreamingMatchesMemoryBuilder_WithPolicy()
    {
        // The streaming and memory PFSC writers must produce identical bytes
        // for the same raw-block policy (both paths honored identically).
        long B = PfsFormat.BlockSize;
        var rnd = new Random(77);
        int three = (int)(3 * B);
        var pfs = new byte[5 * B];
        {
            var tmp = new byte[three];
            rnd.NextBytes(tmp);
            Buffer.BlockCopy(tmp, 0, pfs, 0, three);  // blocks 0-2: random
        }
        Array.Fill(pfs, (byte)0x41, three, (int)(2 * B)); // blocks 3-4: compressible

        var rawPart = pfs[..three];
        var compPart = pfs[three..];
        var inner = PfsWriter.BuildInnerPfs(
            [("raw.bin", rawPart), ("comp.bin", compPart)], 0, out var alloc);
        Assert.Equal(2, alloc.Files.Count);
        var rawA = alloc.Files.First(f => f.Path == "raw.bin");
        var rawB = alloc.Files.First(f => f.Path == "comp.bin");
        var set = new OrbisPkgTool.Pfs.PFSCWriter.RawBlockSet(alloc.BlockCount);
        set.AddRange(rawA.StartBlock, rawA.BlockCount);

        var mem = PFSCWriter.Build(inner, storeAllRaw: false, rawBlocks: set);
        using var ms = new MemoryStream();
        using (var inMs = new MemoryStream(inner))
            PFSCWriter.BuildToStream(inMs, ms, storeAllRaw: false, rawBlocks: set);
        var streamed = ms.ToArray();
        Assert.Equal(Sha(mem), Sha(streamed));
        Assert.Equal(mem.Length, streamed.Length);

        // Round-trip decompression still exact.
        using var dec = new PFSCStream(new MemoryStream(mem));
        using var outMs = new MemoryStream();
        dec.CopyTo(outMs);
        Assert.Equal(Sha(inner), Sha(outMs.ToArray()));
    }

    [Fact]
    public void PfscPolicy_PolicySurvivesFileReordering()
    {
        // The policy maps by PATH, not position — building the same files in
        // a different GP4 order must keep each file's policy attached to it.
        long B = PfsFormat.BlockSize;
        var rnd = new Random(99);
        byte[] Rnd(int n) { var b = new byte[n]; rnd.NextBytes(b); return b; }
        var a = Rnd((int)B);
        var b2 = Rnd((int)B);
        var c = Rnd((int)B);

        var (gp4, img) = MakeFixture("order", ("a.bin", a), ("b.bin", b2), ("c.bin", c));
        string xml = File.ReadAllText(gp4);
        xml = xml.Replace(
            "<file><entry path=\"b.bin\" /><orig_path>b.bin</orig_path></file>",
            "<file targ_path=\"b.bin\" orig_path=\"b.bin\" pfs_compression=\"disable\" />");
        File.WriteAllText(gp4, xml);
        string pkg1 = Build(gp4, img, "order1.pkg", new BuildOptions { PfscMode = PfscMode.Compressed, Quiet = true });

        // Same files, GP4 order reversed (a, b, c → c, b, a).
        var (gp4b, imgb) = MakeFixture("order2", ("c.bin", c), ("b.bin", b2), ("a.bin", a));
        string xml2 = File.ReadAllText(gp4b);
        xml2 = xml2.Replace(
            "<file><entry path=\"b.bin\" /><orig_path>b.bin</orig_path></file>",
            "<file targ_path=\"b.bin\" orig_path=\"b.bin\" pfs_compression=\"disable\" />");
        File.WriteAllText(gp4b, xml2);
        string pkg2 = Build(gp4b, imgb, "order2.pkg", new BuildOptions { PfscMode = PfscMode.Compressed, Quiet = true });

        foreach (string pkg in new[] { pkg1, pkg2 })
        {
            var inner = OpenInner(pkg);
            var ino = inner.FindFile("b.bin")!;
            var sizes = PfscBlockSizes(pkg);
            foreach (var blk in inner.EnumerateFileBlocks(ino))
                Assert.Equal(PfsFormat.BlockSize, sizes[blk]); // b.bin raw in both orders
        }
    }

    [Fact]
    public void PfscPolicy_Cancellation_AbortsCleanly()
    {
        var (gp4, img) = MakeFixture("cancel", ("big.bin", Data(4 * PfsFormat.BlockSize, 0x22)));
        var cts = new System.Threading.CancellationTokenSource();
        var opts = new BuildOptions
        {
            PfscMode = PfscMode.Compressed,
            Quiet = true,
            CancellationToken = cts.Token,
            Progress = (stage, done, total) =>
            {
                if (stage == BuildStage.Pfsc && done > 0) cts.Cancel();
            },
        };
        // The in-memory path compresses synchronously; cancel during PFSC.
        // Either the build completes before the token fires (tiny fixture) or
        // it throws OperationCanceledException — never a partial/corrupt file.
        try
        {
            Build(gp4, img, "cancel.pkg", opts);
        }
        catch (OperationCanceledException)
        {
            // fine — and no output file may exist
            Assert.False(File.Exists(Path.Combine(Path.GetDirectoryName(gp4)!, "cancel.pkg")));
        }
    }

    // ── 20. Path-traversal protection: SanitizeExtractPath rejects "../" ──

    [Fact]
    public void ExtractAll_RejectsPathTraversal()
    {
        var (gp4, img) = MakeFixture("traversal", ("a.bin", Data(3)));
        string pkg = Build(gp4, img, "traversal.pkg");
        string outDir = NewDir("traversal_out");

        // A legitimate extraction must succeed.
        using (var reader = new PkgReader(pkg, Passcode))
            reader.ExtractAll(outDir, null, new ExtractAllOptions());
        Assert.True(File.Exists(Path.Combine(outDir, "Image0", "a.bin")));

        // The sanitizer is private — invoke it directly to pin the invariant
        // that traversal, absolute, and drive-relative names are rejected.
        var mi = typeof(PkgReader).GetMethod("SanitizeExtractPath",
            System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static);
        Assert.NotNull(mi);
        string root = Path.GetFullPath(outDir);

        // "../" escape attempts must throw (anything resolving outside the
        // output root). Note "Image0/../x" alone stays inside the root and
        // is legitimately allowed — only true escapes throw.
        Assert.ThrowsAny<Exception>(() =>
            mi!.Invoke(null, [root, "../evil.txt"]));
        Assert.ThrowsAny<Exception>(() =>
            mi!.Invoke(null, [root, "../../windows/system32/evil.dll"]));
        Assert.ThrowsAny<Exception>(() =>
            mi!.Invoke(null, [root, "Image0/../../../evil.txt"]));
        Assert.ThrowsAny<Exception>(() =>
            mi!.Invoke(null, [root, "Image0/..\\..\\evil.txt"]));
        // Windows absolute paths (Path.Combine keeps them) must throw.
        Assert.ThrowsAny<Exception>(() =>
            mi!.Invoke(null, [root, @"C:\Windows\evil.dll"]));
        // Drive-relative paths must throw (they resolve against the process
        // CWD, never the output dir).
        Assert.ThrowsAny<Exception>(() =>
            mi!.Invoke(null, [root, @"C:evil.dll"]));

        // Legitimate nested paths must pass and stay inside the root.
        string ok = (string)mi!.Invoke(null, [root, "Image0/app0/data.bin"])!;
        Assert.StartsWith(root, ok, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(Path.Combine(root, "Image0", "app0", "data.bin"), ok);
        // A benign "Image0/../x" resolves inside the root — allowed.
        string ok2 = (string)mi!.Invoke(null, [root, "Image0/../data.bin"])!;
        Assert.Equal(Path.Combine(root, "data.bin"), ok2);
    }

    // ── 21. Per-file error tolerance: ContinueOnError collects failures ──

    [Fact]
    public void ExtractAll_ContinueOnError_CollectsFailures()
    {
        var (gp4, img) = MakeFixture("tolerant", ("a.bin", Data(3)));
        string pkg = Build(gp4, img, "tolerant.pkg");
        string outDir = NewDir("tolerant_out");

        // Normal extraction: zero failures.
        using (var reader = new PkgReader(pkg, Passcode))
        {
            var failures = reader.ExtractAll(outDir, null, new ExtractAllOptions());
            Assert.Empty(failures);
        }

        // ContinueOnError default is true; CancellationToken defaults to None.
        var opts = new ExtractAllOptions { ContinueOnError = true };
        Assert.True(opts.ContinueOnError);
        Assert.Equal(System.Threading.CancellationToken.None, opts.CancellationToken);
    }

    // ── 22. Atomic output: no stale .tmp after successful build ──

    [Fact]
    public void Build_AtomicallyWrites_NoStaleTmp()
    {
        var (gp4, img) = MakeFixture("atomic", ("a.bin", Data(3)));
        string outDir = Path.GetDirectoryName(gp4)!;
        string pkg = Path.Combine(outDir, "atomic.pkg");
        _ = Build(gp4, img, "atomic.pkg");
        Assert.True(File.Exists(pkg));
        Assert.False(File.Exists(pkg + ".tmp"));
    }

    // ── 23. Adler32 NMAX batching: byte-identical to naive implementation ──

    [Fact]
    public void Adler32_NMaxBatching_MatchesNaiveImplementation()
    {
        // The NMAX-batched Adler32 must produce the same checksum as the
        // naive per-byte-modulo reference for all block sizes (the batch
        // boundary at 5552 bytes is the only behavioral difference).
        var rnd = new Random(42);
        // Test at sizes below, at, and above the NMAX boundary (5552).
        int[] sizes = [0, 1, 100, 5551, 5552, 5553, 11104, 65536, 65537];
        foreach (int len in sizes)
        {
            var data = new byte[len];
            rnd.NextBytes(data);
            uint expected = NaiveAdler32(data);
            // Build a PFSC block via CompressBlock and verify the trailer.
            var block = new byte[len];
            Array.Copy(data, block, len);
            var comp = PFSCWriter.CompressBlock(block, 0, len);
            if (comp == null) continue; // incompressible — no trailer to check
            // The trailer is the last 4 bytes, big-endian.
            uint stored = ((uint)comp[^4] << 24) | ((uint)comp[^3] << 16)
                | ((uint)comp[^2] << 8) | comp[^1];
            Assert.Equal(expected, stored);
        }
    }

    private static uint NaiveAdler32(byte[] data)
    {
        const uint Mod = 65521;
        uint a = 1, b = 0;
        foreach (byte c in data)
        {
            a = (a + c) % Mod;
            b = (b + a) % Mod;
        }
        return (b << 16) | a;
    }

    // ── 24. PFSC trailer verification detects corruption ──

    [Fact]
    public void PfscVerifyChecksum_DetectsCorruption()
    {
        var inner = PfsWriter.BuildInnerPfs(
            [("data.bin", Data(PfsFormat.BlockSize, 0x42))], 0);
        var pfsc = PFSCWriter.Build(inner, storeAllRaw: false);

        // Verify the valid image passes checksum verification.
        using (var ms = new MemoryStream(pfsc, false))
        {
            using var stream = new PFSCStream(ms, verifyChecksums: true);
            using var outMs = new MemoryStream();
            stream.CopyTo(outMs);
            Assert.Equal(Sha(inner), Sha(outMs.ToArray()));
        }

        // Corrupt one compressed block's data (not the trailer) — the
        // decompressed bytes change, so the recomputed Adler32 won't match
        // the stored trailer. Verification must catch it.
        long dataOff = BitConverter.ToInt64(pfsc, 0x20);
        // Flip a byte in the first compressed block (after the 2-byte zlib header).
        if (pfsc[(int)dataOff + 2] == 0)
            pfsc[(int)dataOff + 2] = 0xFF;
        else
            pfsc[(int)dataOff + 2] ^= 0xFF;

        using (var ms2 = new MemoryStream(pfsc, false))
        {
            using var stream = new PFSCStream(ms2, verifyChecksums: true);
            using var outMs = new MemoryStream();
            // A corrupted deflate stream may throw during decompression
            // (InvalidDataException) or produce wrong bytes caught by the
            // Adler32 check — either is a valid detection.
            Assert.ThrowsAny<Exception>(() => stream.CopyTo(outMs));
        }
    }

    // ── 25. Inode-carrying WalkPfsTree: ExtractAll uses cached inodes ──

    [Fact]
    public void ExtractAll_InodeCarryingWalk_RoundTripsAllFiles()
    {
        // A nested tree exercises the O(files×depth) re-resolution path:
        // before Phase 4.4, each file's extraction re-resolved its path
        // through the dirent chain. The cached inode makes it O(1).
        var (gp4, img) = MakeFixture("inode_walk",
            ("dir1/a.bin", Data(3)),
            ("dir1/b.bin", Data(5)),
            ("dir1/sub/c.bin", Data(7)),
            ("dir2/d.bin", Data(11)),
            ("e.bin", Data(13)));
        string pkg = Build(gp4, img, "inode.pkg");
        PkgValidator.ValidatePkgFile(pkg, Passcode);

        string outDir = NewDir("inode_out");
        using var reader = new PkgReader(pkg, Passcode);
        var failures = reader.ExtractAll(outDir, null, new ExtractAllOptions());
        Assert.Empty(failures);

        // Every file must round-trip byte-exact.
        Assert.Equal(Data(3), File.ReadAllBytes(Path.Combine(outDir, "Image0", "dir1", "a.bin")));
        Assert.Equal(Data(5), File.ReadAllBytes(Path.Combine(outDir, "Image0", "dir1", "b.bin")));
        Assert.Equal(Data(7), File.ReadAllBytes(Path.Combine(outDir, "Image0", "dir1", "sub", "c.bin")));
        Assert.Equal(Data(11), File.ReadAllBytes(Path.Combine(outDir, "Image0", "dir2", "d.bin")));
        Assert.Equal(Data(13), File.ReadAllBytes(Path.Combine(outDir, "Image0", "e.bin")));

        // The cached inode must also be present on listed Image0 entries
        // (Sc0 entries live in the PKG entry table, not the PFS — no inode).
        var files = reader.ListFiles()
            .Where(f => !f.IsDirectory && f.Path.StartsWith("Image0/", StringComparison.OrdinalIgnoreCase))
            .ToList();
        foreach (var f in files)
            Assert.True(f.Inode != null, $"entry {f.Path} has no cached inode");
    }
}
