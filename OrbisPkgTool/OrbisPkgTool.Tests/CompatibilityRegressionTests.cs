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
}
