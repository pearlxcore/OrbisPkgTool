using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using OrbisPkgTool.Crypto;
using OrbisPkgTool.Gp4;
using OrbisPkgTool.Pfs;
using OrbisPkgTool.Sfo;

namespace OrbisPkgTool.Pkg;

/// <summary>
/// PS4 PKG builder — two backends:
///   Build()  = pure C# assembler (no dependencies)
///   OrbisBuild() = generates GP4 and delegates to orbis-pub-cmd img_create
/// </summary>
public static class PkgBuilder
{
    public const string DefaultPasscode = "00000000000000000000000000000000";
    private const uint TableOffset = PfsFormat.PkgTableOffset;
    private const uint BodyOffset = PfsFormat.PkgBodyOffset;

    /// <summary>
    /// Builds a PKG from a GP4 project + source folder.
    /// Uses LibOrbisPkg's proven PFS builder + our own PKG header assembly.
    /// Pure C#, no orbis dependency.
    /// </summary>
    public static void Build(string gp4Path, string projectFolder, string outputPath,
        string passcode = DefaultPasscode)
        => Build(gp4Path, projectFolder, outputPath, new BuildOptions { Passcode = passcode });

    /// <summary>Builds a PKG with full options (PFSC mode, validation, progress, cancellation, manifest).</summary>
    public static void Build(string gp4Path, string projectFolder, string outputPath, BuildOptions options)
    {
        string passcode = options.Passcode;
        var project = Gp4Project.Parse(File.ReadAllText(gp4Path));

        // Separate Image0 (inner PFS) from Sc0 (PKG entries) based on source path
        var pfsFiles = new List<(string Path, byte[] Data)>();
        var sc0Files = new List<(string Path, byte[] Data)>();
        long totalSize = 0;
        foreach (var (entryPath, origPath) in project.Files)
        {
            if (entryPath == "sce_sys/param.sfo") continue;
            string src = ResolveSource(projectFolder, origPath);
            if (!File.Exists(src)) continue;
            byte[] data = File.ReadAllBytes(src);
            totalSize += data.Length;
            if (entryPath.StartsWith("sce_sys/", StringComparison.OrdinalIgnoreCase))
                sc0Files.Add((entryPath["sce_sys/".Length..], data));
            else
            {
                string pfsPath = entryPath.StartsWith("Image0/", StringComparison.OrdinalIgnoreCase)
                    ? entryPath["Image0/".Length..] : entryPath;
                pfsFiles.Add((pfsPath, data));
            }
        }

        if (project.VolumeType == VolumeType.PkgPs4App &&
            !pfsFiles.Any(f => f.Path.Equals("sce_sys/keystone", StringComparison.OrdinalIgnoreCase)))
            pfsFiles.Add(("sce_sys/keystone", PkgCrypto.CreateKeystone(passcode)));

        // Mirror orbis-pub-cmd: scan the filesystem sce_sys/ for extra files
        // that are NOT in the GP4 and add them as Sc0 entries. (Verified:
        // orbis adds icon0.dds/pic0.dds/pic1.dds/save_data.png this way.)
        AddMissingSc0FromFolder(projectFolder, sc0Files);

        var dk = new byte[7][];
        for (uint i = 0; i < 7; i++) dk[i] = PkgCrypto.DeriveKey(project.ContentId, passcode, i);

        // Large games: stream everything through temp files (byte[] limited to 2GB)
        if (totalSize > PfsFormat.StreamingThreshold)
        {
            BuildLarge(project, pfsFiles, sc0Files, dk, passcode, outputPath, options);
            return;
        }

        options.CancellationToken.ThrowIfCancellationRequested();
        var inner = PfsWriter.BuildInnerPfs(pfsFiles, 0);
        var pfsc = PFSCWriter.Build(inner, storeAllRaw: options.PfscMode != PfscMode.Compressed);
        var outer = PfsWriter.BuildOuterPfs(pfsc, "pfs_image.dat", dk[1], Keys.FakeKeySeed, 0, out long outerDataStartMem);

        var pkg = Assemble(project, pfsFiles, outer, passcode, dk, inner.Length, sc0Files, outerDataStartMem);
        File.WriteAllBytes(outputPath, pkg);
        if (options.Validate)
            PkgValidator.ValidatePkgFile(outputPath, passcode);
        if (options.ManifestPath != null)
            WriteManifest(options.ManifestPath, project, pfsFiles, sc0Files, inner.Length, pfsc.Length, outer.Length,
                pkg.Length, dk, passcode, outputPath, options);
    }

    /// <summary>Streams the build through temp files for games whose inner PFS exceeds 2 GB.</summary>
    private static void BuildLarge(Gp4Project project, List<(string Path, byte[] Data)> pfsFiles,
        List<(string Path, byte[] Data)> sc0Files, byte[][] dk, string passcode, string outputPath,
        BuildOptions options)
    {
        var ct = options.CancellationToken;

        // Pre-flight disk-space estimate (temp pipeline is ~3.2× inner + output).
        long estInner = pfsFiles.Sum(f => (f.Data.Length + PfsFormat.BlockSize - 1) / PfsFormat.BlockSize * PfsFormat.BlockSize);
        long required = (long)(estInner * PfsFormat.TempDiskMultiplier) + estInner / 4;
        var drive = new DriveInfo(Path.GetPathRoot(Path.GetFullPath(outputPath)) ?? ".");
        if (drive.AvailableFreeSpace < required)
            throw new InvalidOperationException(
                $"Estimated temporary disk requirement: {required / 1e9:F1} GB. " +
                $"Available on {drive.Name}: {drive.AvailableFreeSpace / 1e9:F1} GB. Aborting before build.");

        string tmpDir = Path.Combine(Path.GetTempPath(), $"pkgbuild_{Guid.NewGuid():N}");
        Directory.CreateDirectory(tmpDir);
        string innerPath = Path.Combine(tmpDir, "inner.pfs");
        string pfscPath = Path.Combine(tmpDir, "inner.pfsc");
        string outerPath = Path.Combine(tmpDir, "outer.pfs");
        try
        {
            // 1. Inner PFS → file
            using (var innerFs = new FileStream(innerPath, FileMode.Create, FileAccess.ReadWrite))
                PfsWriter.BuildInnerPfsToStream(pfsFiles, 0, innerFs, ct,
                    (done, total) => options.Progress?.Invoke(BuildStage.InnerPfs, done, total));
            long innerSize = new FileInfo(innerPath).Length;
            options.Progress?.Invoke(BuildStage.InnerPfs, innerSize, innerSize);

            // 2. PFSC → file
            using (var innerIn = new FileStream(innerPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var pfscFs = new FileStream(pfscPath, FileMode.Create, FileAccess.ReadWrite))
                PFSCWriter.BuildToStream(innerIn, pfscFs,
                    storeAllRaw: options.PfscMode != PfscMode.Compressed, ct,
                    (done, total) => options.Progress?.Invoke(BuildStage.Pfsc, done, total));

            // 3. Outer PFS → file (signing + XTS)
            long outerDataStart = 0;
            using (var pfscIn = new FileStream(pfscPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (var outerFs = new FileStream(outerPath, FileMode.Create, FileAccess.ReadWrite))
                PfsWriter.BuildOuterPfsToStream(pfscIn, "pfs_image.dat", dk[1], Keys.FakeKeySeed, 0, outerFs,
                    out outerDataStart, ct,
                    (done, total) => options.Progress?.Invoke(BuildStage.OuterPfs, done, total));

            // 4. Assemble PKG → output file
            AssembleToFile(project, pfsFiles, outerPath, passcode, dk, innerSize, sc0Files, outputPath, ct,
                (done, total) => options.Progress?.Invoke(BuildStage.Assemble, done, total),
                outerDataStart: outerDataStart);

            if (options.Validate)
                PkgValidator.ValidatePkgFile(outputPath, passcode);
            if (options.ManifestPath != null)
            {
                long outerSize = new FileInfo(outerPath).Length;
                long pfscSize = new FileInfo(pfscPath).Length;
                WriteManifest(options.ManifestPath, project, pfsFiles, sc0Files, innerSize, pfscSize, outerSize,
                    new FileInfo(outputPath).Length, dk, passcode, outputPath, options);
            }
        }
        finally
        {
            try { Directory.Delete(tmpDir, recursive: true); } catch { }
        }
    }

    /// <summary>Writes the optional build manifest (build.json).</summary>
    private static void WriteManifest(string path, Gp4Project project, List<(string Path, byte[] Data)> pfsFiles,
        List<(string Path, byte[] Data)> sc0Files, long innerPfsSize, long pfscSize, long outerPfsSize,
        long pkgSize, byte[][] dk, string passcode, string outputPath, BuildOptions options)
    {
        int dirCount = pfsFiles.Select(f => f.Path.Contains('/') ? f.Path[..f.Path.LastIndexOf('/')] : "")
            .Where(d => d.Length > 0).Distinct().Count();
        int inodeCount = 3 + dirCount + pfsFiles.Count;
        int inodeBlocks = (int)((inodeCount * PfsFormat.D32InodeSize + PfsFormat.BlockSize - 1) / PfsFormat.BlockSize);
        int sc0Count = sc0Files.Count + 13; // fixed system entries + provided Sc0 files

        byte[] sha256;
        using (var sha = SHA256.Create())
        using (var fs = File.OpenRead(outputPath))
        {
            var buf = new byte[1 << 20];
            int n;
            while ((n = fs.Read(buf, 0, buf.Length)) > 0) sha.TransformBlock(buf, 0, n, null, 0);
            sha.TransformFinalBlock([], 0, 0);
            sha256 = sha.Hash!;
        }

        var manifest = new System.Text.Json.Nodes.JsonObject
        {
            ["tool"] = "OrbisPkgTool",
            ["version"] = typeof(PkgBuilder).Assembly.GetName().Version?.ToString() ?? "0.0.0",
            ["buildTimestamp"] = DateTime.UtcNow.ToString("o"),
            ["contentId"] = project.ContentId,
            ["titleId"] = project.TitleId,
            ["title"] = project.Title,
            ["packageType"] = project.VolumeType.ToString(),
            ["pfscMode"] = options.PfscMode.ToString(),
            ["fileCount"] = pfsFiles.Count,
            ["directoryCount"] = dirCount,
            ["innerPfsSize"] = innerPfsSize,
            ["pfscSize"] = pfscSize,
            ["outerPfsSize"] = outerPfsSize,
            ["pkgSize"] = pkgSize,
            ["inodeCount"] = inodeCount,
            ["inodeBlockCount"] = inodeBlocks,
            ["sc0EntryCount"] = sc0Count,
            ["sha256"] = Convert.ToHexString(sha256),
        };
        File.WriteAllText(path, manifest.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
    }

    private static void AssembleToFile(Gp4Project project, List<(string Path, byte[] Data)> files,
        string outerPfsPath, string passcode, byte[][] dk, long innerPfsSize,
        List<(string Path, byte[] Data)>? sc0Files, string outputPath,
        System.Threading.CancellationToken ct = default, Action<long, long>? progress = null,
        long outerDataStart = 0)
    {
        // Build the entry list + table in memory (entry data is small).
        var entries = BuildAssembleEntries(project, files, passcode, dk, innerPfsSize, sc0Files);
        entries = entries.OrderBy(e => e.Id).ToList();
        int count = entries.Count;

        // Name table
        var namesEntry = entries.First(e => e.Id == PkgEntryIds.EntryNames);
        var named = entries.Where(e => e.Name != null).ToList();
        var nameOffsets = new Dictionary<string, int> { [""] = 0 };
        using (var nb = new MemoryStream())
        {
            nb.WriteByte(0);
            int off = 1;
            foreach (var e in named)
            {
                nameOffsets[e.Name!] = off;
                nb.Write(Encoding.ASCII.GetBytes(e.Name!));
                nb.WriteByte(0);
                off += e.Name!.Length + 1;
            }
            namesEntry.Data = nb.ToArray();
        }
        namesEntry.DataSize = (uint)namesEntry.Data.Length;

        var digestsEntry = entries.First(e => e.Id == PkgEntryIds.Digests);
        digestsEntry.Data = new byte[count * 32];
        digestsEntry.DataSize = (uint)digestsEntry.Data.Length;

        uint nextOffset = TableOffset + (uint)(count * 32);
        foreach (var e in entries)
        {
            switch (e.Id)
            {
                case PkgEntryIds.EntryKeys: e.DataOffset = 0x2000; e.DataSize = 2048; continue;
                case PkgEntryIds.ImageKey: e.DataOffset = 0x2800; e.DataSize = 256; continue;
                case PkgEntryIds.GeneralDigests: e.DataOffset = 0x2900; e.DataSize = 0x180; continue;
                case PkgEntryIds.Metas: e.DataOffset = TableOffset; e.DataSize = (uint)(count * 32); continue;
                default:
                    e.DataOffset = nextOffset;
                    // Table DataSize = LOGICAL size (verified: the original
                    // Digimon stores npbind.dat as 532, not 544). Encrypted
                    // entries occupy the 16-aligned region on disk — the
                    // offset-advance below aligns it.
                    e.DataSize = (uint)e.Data.Length;
                    break;
            }
            nextOffset += e.DataSize;
            if (nextOffset % 16 != 0) nextOffset += 16 - (nextOffset % 16);
        }

        var table = new byte[count * 32];
        for (int i = 0; i < count; i++)
        {
            var e = entries[i];
            uint flags1 = e.Id switch
            {
                PkgEntryIds.Digests => 0x40000000,
                PkgEntryIds.EntryKeys => 0x60000000,
                PkgEntryIds.ImageKey => 0xE0000000,
                PkgEntryIds.GeneralDigests => 0x60000000,
                PkgEntryIds.Metas => 0x60000000,
                PkgEntryIds.EntryNames => 0x40000000,
                _ => e.Encrypted ? 0x80000000u : 0u,
            };
            uint flags2 = e.Id switch
            {
                PkgEntryIds.ImageKey => 3u << 12,
                _ => e.Encrypted ? (uint)(e.KeyIndex << 12) : 0u,
            };
            WriteBe32(table, i * 32 + 0, e.Id);
            WriteBe32(table, i * 32 + 4, e.Name != null && nameOffsets.TryGetValue(e.Name, out var no) ? (uint)no : 0);
            WriteBe32(table, i * 32 + 8, flags1);
            WriteBe32(table, i * 32 + 12, flags2);
            WriteBe32(table, i * 32 + 16, e.DataOffset);
            WriteBe32(table, i * 32 + 20, e.Id == PkgEntryIds.Metas ? (uint)table.Length : e.DataSize);
        }

        var metasEntry = entries.First(e => e.Id == PkgEntryIds.Metas);
        metasEntry.Data = table;
        metasEntry.DataSize = (uint)table.Length;

        foreach (var e in entries)
        {
            if (e.Id == PkgEntryIds.ImageKey) continue;
            if (e.Encrypted)
            {
                var ivKey = EntryIvKey(table, entries.IndexOf(e), dk[e.KeyIndex]);
                // EncryptAesCbc returns the FULL padded ciphertext (16-aligned);
                // the table DataSize stays the LOGICAL size (see offset pass).
                e.Data = PkgCrypto.EncryptAesCbc(ivKey.Key, ivKey.Iv, e.Data, e.Data.Length);
            }
        }
        var imageKeyEntry = entries.First(e => e.Id == PkgEntryIds.ImageKey);
        byte[] rsaImageKey = PkgCrypto.RSA2048EncryptKey(PkgKeySet.Standard.FakeKeyset.Modulus, dk[1]);
        var imageIvKey = EntryIvKey(table, entries.IndexOf(imageKeyEntry), dk[3]);
        imageKeyEntry.Data = PkgCrypto.EncryptAesCbc(imageIvKey.Key, imageIvKey.Iv, rsaImageKey, 256);

        var digestBuf = digestsEntry.Data;
        for (int i = 1; i < count; i++)
        {
            var e = entries[i];
            long stored = e.Encrypted ? (e.DataSize + 15) & ~15L : e.DataSize;
            var padded = new byte[stored];
            Buffer.BlockCopy(e.Data, 0, padded, 0, Math.Min(e.Data.Length, padded.Length));
            byte[] hash = PkgCrypto.Sha256(padded);
            Buffer.BlockCopy(hash, 0, digestBuf, i * 32, 32);
        }

        long pfsOffset = Math.Max((nextOffset + PfsFormat.PfsImageAlignment - 1) & ~(PfsFormat.PfsImageAlignment - 1), PfsFormat.PfsImageAlignment);
        long outerSize = new FileInfo(outerPfsPath).Length;
        long pkgSize = pfsOffset + outerSize;
        const long MinPkgSize = 0x100000;
        const long PkgAlign = 0x8000;
        if (pkgSize < MinPkgSize) pkgSize = MinPkgSize;
        if (pkgSize % PkgAlign != 0) pkgSize += PkgAlign - (pkgSize % PkgAlign);

        // Fail fast on structural invariants before writing anything.
        ValidateAssemblyInvariants(entries, pfsOffset, outerSize, pkgSize);

        using var pkg = new FileStream(outputPath, FileMode.Create, FileAccess.ReadWrite, FileShare.Read);
        pkg.SetLength(pkgSize);

        // Body entries
        foreach (var e in entries)
            if (e.Data.Length > 0)
            {
                pkg.Position = e.DataOffset;
                pkg.Write(e.Data, 0, e.Data.Length);
            }

        // Outer PFS — stream copy
        using (var outer = new FileStream(outerPfsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            pkg.Position = pfsOffset;
            var copyBuf = new byte[1 << 20];
            long copied = 0;
            int cn;
            while ((cn = outer.Read(copyBuf, 0, copyBuf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                pkg.Write(copyBuf, 0, cn);
                copied += cn;
                progress?.Invoke(copied, outerSize);
            }
        }

        // Header (0x1000) + RSA signature (0x1000..0x1100) — the in-memory
        // buffer must cover BOTH, or the signature BlockCopy overflows.
        var hdr = new byte[0x1100];
        WriteBe32(hdr, 0x00, 0x7F434E54);
        WriteBe32(hdr, 0x04, 0x00000001);
        WriteBe32(hdr, 0x0C, 0x0000000F);
        WriteBe32(hdr, 0x10, (uint)count);
        WriteBe16(hdr, 0x14, 6);
        WriteBe16(hdr, 0x16, (ushort)count);
        WriteBe32(hdr, 0x18, TableOffset);
        WriteBe32(hdr, 0x1C, 0x800 + 0x100 + 0x180 + 2u * (uint)(count * 32));
        WriteBe64(hdr, 0x20, BodyOffset);
        WriteBe64(hdr, 0x28, (ulong)(pfsOffset - BodyOffset));
        var cid = Encoding.ASCII.GetBytes(project.ContentId);
        Buffer.BlockCopy(cid, 0, hdr, 0x40, Math.Min(cid.Length, 48));
        WriteBe32(hdr, 0x70, 0x0000000F);
        WriteBe32(hdr, 0x74, (uint)(project.VolumeType == VolumeType.PkgPs4Patch ? 0x1E : project.VolumeType == VolumeType.PkgPs4App ? 0x1A : 0x1B));
        WriteBe32(hdr, 0x78, project.VolumeType == VolumeType.PkgPs4Patch ? 0x48000000u : 0x0A000000u);
        // promote_size = pfs_image_offset — verified against original FPKGs
        // (Children of Morta 0xD00000, Digimon 0xB00000, Disgaea 0x80000,
        // Adventure Time 0x2780000 — always equals the PFS image offset).
        WriteBe32(hdr, 0x7C, (uint)pfsOffset);
        WriteBe32(hdr, 0x80, 0x20161020);
        WriteBe32(hdr, 0x84, 0x01738551);
        WriteBe32(hdr, 0x98, 0);
        WriteBe32(hdr, 0x9C, 1);
        WriteBe32(hdr, 0x400, 1);
        WriteBe32(hdr, 0x404, 1);
        WriteBe64(hdr, 0x408, 0x8000000000000000UL | 0x3CC);
        WriteBe64(hdr, 0x410, (ulong)pfsOffset);
        WriteBe64(hdr, 0x418, (ulong)outerSize);
        WriteBe64(hdr, 0x420, 0);
        WriteBe64(hdr, 0x428, (ulong)pkgSize);
        WriteBe64(hdr, 0x430, (ulong)pkgSize);
        WriteBe32(hdr, 0x438, 0x10000);
        // pfs_cache_size: shadPS4 reads cache*2 bytes from pfs_image_offset and
        // scans for the PFSC magic (GetPFSCOffset) within that window. It MUST
        // cover the pfs_image.dat data start block, or a false PFSC magic at a
        // lower block (e.g. uroot data) is found and the sector map is garbage
        // -> bad_alloc in shadPS4Plus. Orbis uses ~(dataStart+19)*0x8000.
        long cacheSz = Math.Max(0x140000, (outerDataStart + 16) * 0x8000);
        WriteBe32(hdr, 0x43C, (uint)cacheSz);

        // sc_entries1_hash / sc_entries2_hash / digest_table_hash (in-memory)
        using (var ms = new MemoryStream())
        {
            foreach (var eid in new[] { PkgEntryIds.EntryKeys, PkgEntryIds.ImageKey, PkgEntryIds.GeneralDigests, PkgEntryIds.Metas, PkgEntryIds.Digests })
            {
                var e = entries.First(x => x.Id == eid);
                ms.Write(e.Data, 0, e.Data.Length);
            }
            Buffer.BlockCopy(PkgCrypto.Sha256(ms.ToArray()), 0, hdr, 0x100, 32);
        }
        using (var ms = new MemoryStream())
        {
            const int scCount = 6;
            foreach (var eid in new[] { PkgEntryIds.EntryKeys, PkgEntryIds.ImageKey, PkgEntryIds.GeneralDigests, PkgEntryIds.Metas })
            {
                var e = entries.First(x => x.Id == eid);
                int len = eid == PkgEntryIds.Metas ? scCount * 32 : e.Data.Length;
                ms.Write(e.Data, 0, len);
            }
            Buffer.BlockCopy(PkgCrypto.Sha256(ms.ToArray()), 0, hdr, 0x120, 32);
        }
        Buffer.BlockCopy(PkgCrypto.Sha256(digestsEntry.Data), 0, hdr, 0x140, 32);

        // body_digest: SHA256 of pkg[0x2000 .. pfsOffset)
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            pkg.Position = BodyOffset;
            long bodyLen = pfsOffset - BodyOffset;
            var buf = new byte[1 << 20];
            long remaining = bodyLen;
            while (remaining > 0)
            {
                int n = pkg.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
                if (n <= 0) break;
                sha.TransformBlock(buf, 0, n, null, 0);
                remaining -= n;
            }
            sha.TransformFinalBlock([], 0, 0);
            Buffer.BlockCopy(sha.Hash!, 0, hdr, 0x160, 32);
        }

        // pfs_image_digest / pfs_signed_digest: SHA256 of the outer PFS file
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            using (var outer = new FileStream(outerPfsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var buf = new byte[1 << 20];
                int n;
                while ((n = outer.Read(buf, 0, buf.Length)) > 0)
                    sha.TransformBlock(buf, 0, n, null, 0);
            }
            sha.TransformFinalBlock([], 0, 0);
            Buffer.BlockCopy(sha.Hash!, 0, hdr, 0x440, 32);
        }
        using (var sha = System.Security.Cryptography.SHA256.Create())
        {
            using (var outer = new FileStream(outerPfsPath, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var buf = new byte[0x10000];
                int n = outer.Read(buf, 0, buf.Length);
                if (n < buf.Length) Array.Resize(ref buf, n);
                sha.TransformFinalBlock(buf, 0, buf.Length);
            }
            Buffer.BlockCopy(sha.Hash!, 0, hdr, 0x460, 32);
        }

        // Entry table + header (signature area still zero at this point)
        pkg.Position = TableOffset;
        pkg.Write(table, 0, table.Length);
        pkg.Position = 0;
        pkg.Write(hdr, 0, 0x1100);

        // Header digest + RSA signature
        var headerDigest = PkgCrypto.Sha256(hdr.AsSpan(0, 0xFE0).ToArray());
        Buffer.BlockCopy(headerDigest, 0, hdr, 0xFE0, 32);
        var headerSha = PkgCrypto.Sha256(hdr.AsSpan(0, 0x1000).ToArray());
        var signature = PkgCrypto.RSA2048EncryptKey(PkgKeySet.PkgPublicKeys[3], headerSha);
        Buffer.BlockCopy(signature, 0, hdr, 0x1000, 256);
        pkg.Position = 0;
        pkg.Write(hdr, 0, 0x1100);
    }

    /// <summary>
    /// Resolves a GP4 orig_path to a source file. gengp4_app writes absolute
    /// paths ("C:\...\Image0\CONTENT/x") while our gp4gen writes paths relative
    /// to the GP4. Path.Combine(folder, absolute) already yields the absolute
    /// path, so this handles both — but normalize separators first.
    /// </summary>
    private static string ResolveSource(string projectFolder, string origPath)
    {
        var norm = origPath.Replace('/', Path.DirectorySeparatorChar);
        var combined = Path.Combine(projectFolder, norm);
        // If origPath was absolute, Path.GetFullPath keeps it absolute.
        return Path.GetFullPath(combined);
    }

    /// <summary>
    /// Scans the filesystem sce_sys/ directory and adds any known Sc0 files
    /// that the GP4 did not list — mirroring orbis-pub-cmd, which adds
    /// icon0.dds/pic0.dds/pic1.dds/save_data.png etc. from disk. Without
    /// this the built PKG has fewer Sc0 entries than an orbis build.
    /// </summary>
    private static void AddMissingSc0FromFolder(string projectFolder, List<(string Path, byte[] Data)> sc0Files)
    {
        string sceSys = Path.Combine(projectFolder, "sce_sys");
        if (!Directory.Exists(sceSys)) return;

        var present = new HashSet<string>(sc0Files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(sceSys, "*", SearchOption.AllDirectories))
        {
            string rel = Path.GetRelativePath(sceSys, file).Replace('\\', '/');
            if (present.Contains(rel)) continue;
            // Only add files with a known entry ID (param.sfo handled separately).
            uint id = PkgEntryNames.Known.FirstOrDefault(kv => kv.Value == rel).Key;
            if (id == 0) continue;
            sc0Files.Add((rel, File.ReadAllBytes(file)));
            present.Add(rel);
        }
    }

    /// <summary>
    /// Fail-fast compatibility invariants, checked before any output is written.
    /// Every rule here is an orbis-pub-cmd 3.87 requirement (see PfsFormat.cs).
    /// </summary>
    private static void ValidateAssemblyInvariants(List<BuildEntry> entries, long pfsOffset, long outerSize, long pkgSize)
    {
        if (pfsOffset % PfsFormat.PfsImageAlignment != 0)
            throw new InvalidOperationException(
                $"pfs_image_offset 0x{pfsOffset:X} is not {PfsFormat.PfsImageAlignment:X}-aligned");
        if (pfsOffset + outerSize > pkgSize)
            throw new InvalidOperationException("PFS image range exceeds package size");

        var ids = new HashSet<uint>();
        foreach (var e in entries)
        {
            if (!ids.Add(e.Id))
                throw new InvalidOperationException($"Duplicate entry ID 0x{e.Id:X8} in entry table");
            // Encrypted entries occupy the 16-aligned region on disk.
            long eEnd = (long)e.DataOffset + (e.Encrypted ? (e.DataSize + 15) & ~15L : e.DataSize);
            if (eEnd > pkgSize)
                throw new InvalidOperationException(
                    $"Entry 0x{e.Id:X8} range 0x{e.DataOffset:X}..0x{eEnd:X} outside package (size 0x{pkgSize:X})");
            if (e.Encrypted && (e.DataOffset & 15) != 0)
                throw new InvalidOperationException(
                    $"Entry 0x{e.Id:X8} encrypted data offset 0x{e.DataOffset:X} is not 16-aligned");
        }
    }

    /// <summary>Builds the PKG entry list (shared with Assemble).</summary>
    private static List<BuildEntry> BuildAssembleEntries(Gp4Project project, List<(string Path, byte[] Data)> files,
        string passcode, byte[][] dk, long innerPfsSize, List<(string Path, byte[] Data)>? sc0Files)
    {
        var entries = new List<BuildEntry>();

        entries.Add(new BuildEntry { Id = PkgEntryIds.Digests, Data = new byte[0] });
        entries.Add(new BuildEntry { Id = PkgEntryIds.EntryKeys, Data = BuildEntryKeys(project.ContentId, dk, passcode) });
        entries.Add(new BuildEntry { Id = PkgEntryIds.ImageKey, Data = new byte[256] });
        entries.Add(new BuildEntry { Id = PkgEntryIds.GeneralDigests, Data = new byte[0x180] });
        entries.Add(new BuildEntry { Id = PkgEntryIds.Metas, Data = new byte[0] });
        entries.Add(new BuildEntry { Id = PkgEntryIds.EntryNames, Data = new byte[0] });

        entries.Add(new BuildEntry { Id = PkgEntryIds.LicenseDat, Data = new byte[0x400], KeyIndex = 3, Encrypted = true });
        entries.Add(new BuildEntry { Id = PkgEntryIds.LicenseInfo, Data = new byte[0x200], KeyIndex = 2, Encrypted = true });
        entries.Add(new BuildEntry { Id = PkgEntryIds.PsReservedDat, Data = new byte[0x2000] });

        var sfo = new ParamSfo();
        sfo.SetInt("APP_TYPE", 1);
        sfo.SetString("APP_VER", project.AppVersion, 0x8);
        sfo.SetInt("ATTRIBUTE", 0x00800002);
        sfo.SetInt("ATTRIBUTE2", 0);
        sfo.SetString("CATEGORY", project.VolumeType == VolumeType.PkgPs4Patch ? "gp" : "gd", 0x4);
        sfo.SetString("CONTENT_ID", project.ContentId, 0x30);
        sfo.SetInt("DEV_FLAG", 0);
        sfo.SetInt("DOWNLOAD_DATA_SIZE", 0);
        sfo.SetString("FORMAT", "obs", 0x4);
        sfo.SetInt("PARENTAL_LEVEL", 5);
        sfo.SetInt("REMOTE_PLAY_KEY_ASSIGN", 0);
        for (int i = 1; i <= 7; i++) sfo.SetString($"SERVICE_ID_ADDCONT_ADD_{i}", "", 0x14);
        sfo.SetInt("SYSTEM_VER", 0x02700000);
        sfo.SetString("TITLE", project.Title, 0x80);
        sfo.SetString("TITLE_ID", project.TitleId, 0xC);
        for (int i = 1; i <= 4; i++) sfo.SetInt($"USER_DEFINED_PARAM_{i}", 0);
        sfo.SetString("VERSION", project.Version, 0x8);
        long img0Mb = innerPfsSize / (1000 * 1000);
        sfo.SetString("PUBTOOLINFO", $"c_date={DateTime.UtcNow:yyyyMMdd},img0_l0_size={img0Mb},img0_l1_size=0,img0_sc_ksize=512,img0_pc_ksize=832", 0x200);
        sfo.SetInt("PUBTOOLVER", 0x02890000);
        entries.Add(new BuildEntry { Id = PkgEntryIds.ParamSfo, Name = "param.sfo", Data = sfo.Serialize() });

        entries.Add(new BuildEntry { Id = PkgEntryIds.PlaygoChunkDat, Name = "playgo-chunk.dat", Data = MakePlayGoChunkDat(1) });
        entries.Add(new BuildEntry { Id = PkgEntryIds.PlaygoChunkSha, Name = "playgo-chunk.sha", Data = new byte[4] });
        entries.Add(new BuildEntry { Id = PkgEntryIds.PlaygoManifestXml, Name = "playgo-manifest.xml", Data = MakePlayGoManifest() });

        sc0Files ??= [];
        foreach (var (name, data) in sc0Files)
        {
            uint id = PkgEntryNames.Known.FirstOrDefault(kv => kv.Value == name).Key;
            if (id == 0) continue;
            bool enc = id is PkgEntryIds.LicenseDat or PkgEntryIds.LicenseInfo
                or PkgEntryIds.NpBindDat or PkgEntryIds.NpTitleDat
                or PkgEntryIds.SelfInfoDat or PkgEntryIds.ImageInfoDat;
            // If the entry already exists (fixed placeholders like license.dat,
            // license.info, psreserved.dat), REPLACE its data with the real file
            // content — duplicate entry IDs make orbis reject the whole PKG.
            var existing = entries.FirstOrDefault(e => e.Id == id);
            if (existing != null)
            {
                existing.Name = name;
                existing.Data = data;
                continue;
            }
            entries.Add(new BuildEntry { Id = id, Name = name, Data = data,
                Encrypted = enc, KeyIndex = enc ? 3 : 0 });
        }
        return entries;
    }

    /// <summary>
    /// Builds a PKG using our pure C# assembler with a pre-built outer PFS blob.
    /// Used by buildtest for testing and comparison.
    /// </summary>
    public static void BuildCs(string gp4Path, string projectFolder, string outputPath,
        byte[] outerPfs, string passcode = DefaultPasscode)
    {
        var project = Gp4Project.Parse(File.ReadAllText(gp4Path));
        if (passcode.Length != 32)
            throw new ArgumentException("Passcode must be exactly 32 characters");
        if (project.ContentId.Length == 0)
            throw new ArgumentException("GP4 is missing content_id");

        var fileData = new List<(string Path, byte[] Data)>();
        foreach (var (entryPath, origPath) in project.Files)
        {
            if (entryPath == "sce_sys/param.sfo") continue;
            string src = ResolveSource(projectFolder, origPath);
            if (!File.Exists(src)) continue;
            // Strip "Image0/" prefix — the inner PFS IS Image0, files inside don't need it
            string pfsPath = entryPath.StartsWith("Image0/", StringComparison.OrdinalIgnoreCase)
                ? entryPath["Image0/".Length..] : entryPath;
            fileData.Add((pfsPath, File.ReadAllBytes(src)));
        }

        if (project.VolumeType == VolumeType.PkgPs4App &&
            !fileData.Any(f => f.Path.Equals("sce_sys/keystone", StringComparison.OrdinalIgnoreCase)))
            fileData.Add(("sce_sys/keystone", PkgCrypto.CreateKeystone(passcode)));

        var dk = new byte[7][];
        for (uint i = 0; i < 7; i++)
            dk[i] = PkgCrypto.DeriveKey(project.ContentId, passcode, i);

        var pkg = Assemble(project, fileData, outerPfs, passcode, dk, 0);
        File.WriteAllBytes(outputPath, pkg);
    }

    /// <summary>
    /// Builds a PKG using orbis-pub-cmd's img_create as the assembly backend.
    /// </summary>
    public static void OrbisBuild(string gp4Path, string projectFolder, string outputPath,
        string passcode = DefaultPasscode, string? orbisPath = null)
    {
        orbisPath ??= FindOrbisPubCmd();
        if (orbisPath == null || !File.Exists(orbisPath))
            throw new FileNotFoundException("orbis-pub-cmd.exe not found.");

        if (!Directory.Exists(Path.Combine(Path.GetDirectoryName(orbisPath)!, "ext")))
            throw new DirectoryNotFoundException("orbis-pub-cmd needs an 'ext' folder nearby.");

        var project = Gp4Project.Parse(File.ReadAllText(gp4Path));
        var orbisContentId = MakeValidContentId(project.ContentId);
        var sfoDir = Path.Combine(projectFolder, "sce_sys");
        Directory.CreateDirectory(sfoDir);

        // Create param.sfo
        var sfo = new ParamSfo();
        sfo.SetInt("APP_TYPE", 1);
        sfo.SetString("APP_VER", project.AppVersion, 0x8);
        sfo.SetInt("ATTRIBUTE", 0x00800002);
        sfo.SetInt("ATTRIBUTE2", 0);
        sfo.SetString("CATEGORY", project.VolumeType == VolumeType.PkgPs4Patch ? "gp" : "gd", 0x4);
        sfo.SetString("CONTENT_ID", orbisContentId, 0x30);
        sfo.SetInt("DEV_FLAG", 0);
        sfo.SetInt("DOWNLOAD_DATA_SIZE", 0);
        sfo.SetString("FORMAT", "obs", 0x4);
        sfo.SetInt("PARENTAL_LEVEL", 5);
        sfo.SetInt("REMOTE_PLAY_KEY_ASSIGN", 0);
        for (int i = 1; i <= 7; i++) sfo.SetString($"SERVICE_ID_ADDCONT_ADD_{i}", "", 0x14);
        sfo.SetInt("SYSTEM_VER", 0x02700000);
        sfo.SetString("TITLE", project.Title, 0x80);
        sfo.SetString("TITLE_ID", "CUSA00001", 0xC);
        for (int i = 1; i <= 4; i++) sfo.SetInt($"USER_DEFINED_PARAM_{i}", 0);
        sfo.SetString("VERSION", project.Version, 0x8);
        sfo.SetString("PUBTOOLINFO", $"c_date={DateTime.UtcNow:yyyyMMdd},img0_l0_size=0,img0_l1_size=0,img0_sc_ksize=512,img0_pc_ksize=832", 0x200);
        sfo.SetInt("PUBTOOLVER", 0x02890000);
        File.WriteAllBytes(Path.Combine(sfoDir, "param.sfo"), sfo.Serialize());

        // Generate orbis-compatible GP4
        var orbisGp4 = Path.Combine(projectFolder, "_orbis_build.gp4");
        using (var w = new StreamWriter(orbisGp4, false, Encoding.UTF8))
        {
            w.WriteLine("<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>");
            w.WriteLine("<psproject fmt=\"gp4\" version=\"1000\">");
            w.WriteLine("  <volume>");
            w.WriteLine($"    <volume_type>{project.VolumeType.ToGp4String()}</volume_type>");
            w.WriteLine("    <volume_id></volume_id>");
            w.WriteLine($"    <volume_ts>{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss}</volume_ts>");
            var escCid = System.Security.SecurityElement.Escape(orbisContentId);
            w.WriteLine($"    <package content_id=\"{escCid}\" passcode=\"{passcode}\" storage_type=\"digital50\" app_type=\"full\" />");
            w.WriteLine("    <chunk_info chunk_count=\"1\" scenario_count=\"1\">");
            w.WriteLine("      <chunks><chunk id=\"0\" layer_no=\"0\" label=\"Chunk #0\" /></chunks>");
            w.WriteLine("      <scenarios default_id=\"0\"><scenario id=\"0\" type=\"sp\" initial_chunk_count=\"1\" label=\"Scenario #0\">0</scenario></scenarios>");
            w.WriteLine("    </chunk_info>");
            w.WriteLine("  </volume>");
            w.WriteLine("  <files img_no=\"0\">");
            w.WriteLine("    <file targ_path=\"sce_sys/param.sfo\" orig_path=\"sce_sys/param.sfo\" />");
            foreach (var (entryPath, origPath) in project.Files)
            {
                if (entryPath == "sce_sys/param.sfo") continue;
                var safePath = SanitizeForOrbis(entryPath, projectFolder);
                var esc = System.Security.SecurityElement.Escape(safePath);
                w.WriteLine($"    <file targ_path=\"{esc}\" orig_path=\"{esc}\" />");
            }
            w.WriteLine("  </files>");
            w.WriteLine("  <rootdir />");
            w.WriteLine("</psproject>");
        }

        // Invoke orbis-pub-cmd
        var psi = new ProcessStartInfo
        {
            FileName = orbisPath,
            Arguments = $"img_create \"{orbisGp4}\" \"{outputPath}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        if (proc.ExitCode != 0)
            throw new Exception($"orbis-pub-cmd img_create failed (exit {proc.ExitCode}): {proc.StandardError.ReadToEnd()}");
    }

    // ─── C# PKG assembler ───────────────────────────────────────────

    private sealed class BuildEntry
    {
        public uint Id;
        public string? Name;
        public int KeyIndex;
        public bool Encrypted;
        public byte[] Data = [];
        public uint DataOffset;
        public uint DataSize;
    }

    private static byte[] Assemble(Gp4Project project, List<(string Path, byte[] Data)> files,
        byte[] outerPfs, string passcode, byte[][] dk, long innerPfsSize,
        List<(string Path, byte[] Data)>? sc0Files = null, long outerDataStart = 0)
    {
        var entries = new List<BuildEntry>();

        // Meta entries
        entries.Add(new BuildEntry { Id = PkgEntryIds.Digests, Data = new byte[0] });
        entries.Add(new BuildEntry { Id = PkgEntryIds.EntryKeys, Data = BuildEntryKeys(project.ContentId, dk, passcode) });
        entries.Add(new BuildEntry { Id = PkgEntryIds.ImageKey, Data = new byte[256] });
        entries.Add(new BuildEntry { Id = PkgEntryIds.GeneralDigests, Data = new byte[0x180] });
        entries.Add(new BuildEntry { Id = PkgEntryIds.Metas, Data = new byte[0] });
        entries.Add(new BuildEntry { Id = PkgEntryIds.EntryNames, Data = new byte[0] });

        // System entries
        entries.Add(new BuildEntry { Id = PkgEntryIds.LicenseDat, Data = new byte[0x400], KeyIndex = 3, Encrypted = true });
        entries.Add(new BuildEntry { Id = PkgEntryIds.LicenseInfo, Data = new byte[0x200], KeyIndex = 2, Encrypted = true });
        entries.Add(new BuildEntry { Id = PkgEntryIds.PsReservedDat, Data = new byte[0x2000] });

        // param.sfo
        var sfo = new ParamSfo();
        sfo.SetInt("APP_TYPE", 1);
        sfo.SetString("APP_VER", project.AppVersion, 0x8);
        sfo.SetInt("ATTRIBUTE", 0x00800002);
        sfo.SetInt("ATTRIBUTE2", 0);
        sfo.SetString("CATEGORY", project.VolumeType == VolumeType.PkgPs4Patch ? "gp" : "gd", 0x4);
        sfo.SetString("CONTENT_ID", project.ContentId, 0x30);
        sfo.SetInt("DEV_FLAG", 0);
        sfo.SetInt("DOWNLOAD_DATA_SIZE", 0);
        sfo.SetString("FORMAT", "obs", 0x4);
        sfo.SetInt("PARENTAL_LEVEL", 5);
        sfo.SetInt("REMOTE_PLAY_KEY_ASSIGN", 0);
        for (int i = 1; i <= 7; i++) sfo.SetString($"SERVICE_ID_ADDCONT_ADD_{i}", "", 0x14);
        sfo.SetInt("SYSTEM_VER", 0x02700000);
        sfo.SetString("TITLE", project.Title, 0x80);
        sfo.SetString("TITLE_ID", project.TitleId, 0xC);
        for (int i = 1; i <= 4; i++) sfo.SetInt($"USER_DEFINED_PARAM_{i}", 0);
        sfo.SetString("VERSION", project.Version, 0x8);
        long img0Mb = innerPfsSize / (1000 * 1000);
        sfo.SetString("PUBTOOLINFO", $"c_date={DateTime.UtcNow:yyyyMMdd},img0_l0_size={img0Mb},img0_l1_size=0,img0_sc_ksize=512,img0_pc_ksize=832", 0x200);
        sfo.SetInt("PUBTOOLVER", 0x02890000);
        entries.Add(new BuildEntry { Id = PkgEntryIds.ParamSfo, Name = "param.sfo", Data = sfo.Serialize() });

        // PlayGo entries
        entries.Add(new BuildEntry { Id = PkgEntryIds.PlaygoChunkDat, Name = "playgo-chunk.dat", Data = MakePlayGoChunkDat(1) });
        entries.Add(new BuildEntry { Id = PkgEntryIds.PlaygoChunkSha, Name = "playgo-chunk.sha", Data = new byte[4] });
        entries.Add(new BuildEntry { Id = PkgEntryIds.PlaygoManifestXml, Name = "playgo-manifest.xml", Data = MakePlayGoManifest() });

        // User files go into the inner PFS only, NOT as PKG entries.
        // (LibOrbisPkg does the same — only known Sc0 system files get PKG entries.)

        // GP4-provided Sc0 files become PKG entries with known IDs
        sc0Files ??= [];
        foreach (var (name, data) in sc0Files)
        {
            uint id = PkgEntryNames.Known.FirstOrDefault(kv => kv.Value == name).Key;
            if (id == 0) continue; // skip unknown files
            bool enc = id is PkgEntryIds.LicenseDat or PkgEntryIds.LicenseInfo
                or PkgEntryIds.NpBindDat or PkgEntryIds.NpTitleDat
                or PkgEntryIds.SelfInfoDat or PkgEntryIds.ImageInfoDat;
            // Replace fixed placeholders (license.dat, license.info, psreserved.dat)
            // with the real file content — duplicate IDs make orbis reject the PKG.
            var existing = entries.FirstOrDefault(e => e.Id == id);
            if (existing != null)
            {
                existing.Name = name;
                existing.Data = data;
                continue;
            }
            entries.Add(new BuildEntry { Id = id, Name = name, Data = data,
                Encrypted = enc, KeyIndex = enc ? 3 : 0 });
        }

        // Sort by ID
        entries = entries.OrderBy(e => e.Id).ToList();
        int count = entries.Count;

        // Build name table
        var namesEntry = entries.First(e => e.Id == PkgEntryIds.EntryNames);
        var named = entries.Where(e => e.Name != null).ToList();
        var nameOffsets = new Dictionary<string, int> { [""] = 0 };
        using (var nb = new MemoryStream())
        {
            nb.WriteByte(0);
            int off = 1;
            foreach (var e in named)
            {
                nameOffsets[e.Name!] = off;
                nb.Write(Encoding.ASCII.GetBytes(e.Name!));
                nb.WriteByte(0);
                off += e.Name!.Length + 1;
            }
            namesEntry.Data = nb.ToArray();
        }
        namesEntry.DataSize = (uint)namesEntry.Data.Length;

        // Digests placeholder
        var digestsEntry = entries.First(e => e.Id == PkgEntryIds.Digests);
        digestsEntry.Data = new byte[count * 32];
        digestsEntry.DataSize = (uint)digestsEntry.Data.Length;

        // Assign offsets
        uint nextOffset = TableOffset + (uint)(count * 32);
        foreach (var e in entries)
        {
            switch (e.Id)
            {
                case PkgEntryIds.EntryKeys: e.DataOffset = 0x2000; e.DataSize = 2048; continue;
                case PkgEntryIds.ImageKey: e.DataOffset = 0x2800; e.DataSize = 256; continue;
                case PkgEntryIds.GeneralDigests: e.DataOffset = 0x2900; e.DataSize = 0x180; continue;
                case PkgEntryIds.Metas: e.DataOffset = TableOffset; e.DataSize = (uint)(count * 32); continue;
                default:
                    e.DataOffset = nextOffset;
                    // Table DataSize = LOGICAL size (verified: the original
                    // Digimon stores npbind.dat as 532, not 544). Encrypted
                    // entries occupy the 16-aligned region on disk — the
                    // offset-advance below aligns it.
                    e.DataSize = (uint)e.Data.Length;
                    break;
            }
            nextOffset += e.DataSize;
            if (nextOffset % 16 != 0) nextOffset += 16 - (nextOffset % 16);
        }

        // Build entry table
        var table = new byte[count * 32];
        for (int i = 0; i < count; i++)
        {
            var e = entries[i];
            uint flags1 = e.Id switch
            {
                PkgEntryIds.Digests => 0x40000000,
                PkgEntryIds.EntryKeys => 0x60000000,
                PkgEntryIds.ImageKey => 0xE0000000,
                PkgEntryIds.GeneralDigests => 0x60000000,
                PkgEntryIds.Metas => 0x60000000,
                PkgEntryIds.EntryNames => 0x40000000,
                _ => e.Encrypted ? 0x80000000u : 0u,
            };
            uint flags2 = e.Id switch
            {
                PkgEntryIds.ImageKey => 3u << 12,
                _ => e.Encrypted ? (uint)(e.KeyIndex << 12) : 0u,
            };
            WriteBe32(table, i * 32 + 0, e.Id);
            WriteBe32(table, i * 32 + 4, e.Name != null && nameOffsets.TryGetValue(e.Name, out var no) ? (uint)no : 0);
            WriteBe32(table, i * 32 + 8, flags1);
            WriteBe32(table, i * 32 + 12, flags2);
            WriteBe32(table, i * 32 + 16, e.DataOffset);
            WriteBe32(table, i * 32 + 20, e.Id == PkgEntryIds.Metas ? (uint)table.Length : e.DataSize);
        }

        // Metas = table itself
        var metasEntry = entries.First(e => e.Id == PkgEntryIds.Metas);
        metasEntry.Data = table;
        metasEntry.DataSize = (uint)table.Length;

        // Encrypt entries
        foreach (var e in entries)
        {
            if (e.Id == PkgEntryIds.ImageKey) continue;
            if (e.Encrypted)
            {
                var ivKey = EntryIvKey(table, entries.IndexOf(e), dk[e.KeyIndex]);
                // EncryptAesCbc returns the FULL padded ciphertext (16-aligned);
                // the table DataSize stays the LOGICAL size (see offset pass).
                e.Data = PkgCrypto.EncryptAesCbc(ivKey.Key, ivKey.Iv, e.Data, e.Data.Length);
            }
        }
        var imageKeyEntry = entries.First(e => e.Id == PkgEntryIds.ImageKey);
        byte[] rsaImageKey = PkgCrypto.RSA2048EncryptKey(PkgKeySet.Standard.FakeKeyset.Modulus, dk[1]);
        var imageIvKey = EntryIvKey(table, entries.IndexOf(imageKeyEntry), dk[3]);
        imageKeyEntry.Data = PkgCrypto.EncryptAesCbc(imageIvKey.Key, imageIvKey.Iv, rsaImageKey, 256);

        // Compute entry digests
        var digestBuf = digestsEntry.Data;
        for (int i = 1; i < count; i++)
        {
            var e = entries[i];
            long stored = e.Encrypted ? (e.DataSize + 15) & ~15L : e.DataSize;
            var padded = new byte[stored];
            Buffer.BlockCopy(e.Data, 0, padded, 0, Math.Min(e.Data.Length, padded.Length));
            byte[] hash = PkgCrypto.Sha256(padded);
            Buffer.BlockCopy(hash, 0, digestBuf, i * 32, 32);
        }

        // Assemble body
        // orbis requires pfs_image_offset aligned to 0x80000 (LibOrbisPkg:
        // body_size = Align(body_offset + bodySize, 0x80000) - body_offset).
        // A misaligned offset makes orbis reject the whole PKG.
        long pfsOffset = Math.Max((nextOffset + PfsFormat.PfsImageAlignment - 1) & ~(PfsFormat.PfsImageAlignment - 1), PfsFormat.PfsImageAlignment);
        long rawSize = pfsOffset + outerPfs.Length;
        const long MinPkgSize = 0x100000;
        const long PkgAlign = 0x8000;
        if (rawSize < MinPkgSize) rawSize = MinPkgSize;
        if (rawSize % PkgAlign != 0) rawSize += PkgAlign - (rawSize % PkgAlign);

        // Fail fast on structural invariants before writing anything.
        ValidateAssemblyInvariants(entries, pfsOffset, outerPfs.Length, rawSize);
        var pkg = new byte[rawSize];

        foreach (var e in entries)
            if (e.Data.Length > 0)
                Buffer.BlockCopy(e.Data, 0, pkg, (int)e.DataOffset, e.Data.Length);
        Buffer.BlockCopy(outerPfs, 0, pkg, (int)pfsOffset, outerPfs.Length);

        // Header
        WriteBe32(pkg, 0x00, 0x7F434E54);
        WriteBe32(pkg, 0x04, 0x00000001);
        WriteBe32(pkg, 0x0C, 0x0000000F);
        WriteBe32(pkg, 0x10, (uint)count);
        WriteBe16(pkg, 0x14, 6);
        WriteBe16(pkg, 0x16, (ushort)count);
        WriteBe32(pkg, 0x18, TableOffset);
        // main_ent_data_size = EntryKeys(0x800) + ImageKey(0x100) + GeneralDigests(0x180) + Metas(count*32) + Digests(count*32)
        WriteBe32(pkg, 0x1C, 0x800 + 0x100 + 0x180 + 2u * (uint)(count * 32));
        WriteBe64(pkg, 0x20, BodyOffset);
        WriteBe64(pkg, 0x28, (ulong)(pfsOffset - BodyOffset));
        var cid = Encoding.ASCII.GetBytes(project.ContentId);
        Buffer.BlockCopy(cid, 0, pkg, 0x40, Math.Min(cid.Length, 48));
        WriteBe32(pkg, 0x70, 0x0000000F);
        WriteBe32(pkg, 0x74, (uint)(project.VolumeType == VolumeType.PkgPs4Patch ? 0x1E : project.VolumeType == VolumeType.PkgPs4App ? 0x1A : 0x1B));
        WriteBe32(pkg, 0x78, project.VolumeType == VolumeType.PkgPs4Patch ? 0x48000000u : 0x0A000000u);
        // promote_size = pfs_image_offset (matches original FPKGs)
        WriteBe32(pkg, 0x7C, (uint)pfsOffset);
        WriteBe32(pkg, 0x80, 0x20161020);
        WriteBe32(pkg, 0x84, 0x01738551);
        WriteBe32(pkg, 0x98, 0);
        WriteBe32(pkg, 0x9C, 1);
        WriteBe32(pkg, 0x400, 1);
        WriteBe32(pkg, 0x404, 1);
        WriteBe64(pkg, 0x408, 0x8000000000000000UL | 0x3CC);
        WriteBe64(pkg, 0x410, (ulong)pfsOffset);
        WriteBe64(pkg, 0x418, (ulong)outerPfs.Length);
        WriteBe64(pkg, 0x420, 0);
        WriteBe64(pkg, 0x428, (ulong)pkg.Length);
        WriteBe64(pkg, 0x430, (ulong)pkg.Length);
        WriteBe32(pkg, 0x438, 0x10000);
        WriteBe32(pkg, 0x43C, (uint)Math.Max(0x140000, (outerDataStart + 16) * 0x8000));

        // Digests
        using (var ms = new MemoryStream())
        {
            foreach (var eid in new[] { PkgEntryIds.EntryKeys, PkgEntryIds.ImageKey, PkgEntryIds.GeneralDigests, PkgEntryIds.Metas, PkgEntryIds.Digests })
            {
                var e = entries.First(x => x.Id == eid);
                ms.Write(e.Data, 0, e.Data.Length);
            }
            Buffer.BlockCopy(PkgCrypto.Sha256(ms.ToArray()), 0, pkg, 0x100, 32);
        }
        using (var ms = new MemoryStream())
        {
            const int scCount = 6;
            foreach (var eid in new[] { PkgEntryIds.EntryKeys, PkgEntryIds.ImageKey, PkgEntryIds.GeneralDigests, PkgEntryIds.Metas })
            {
                var e = entries.First(x => x.Id == eid);
                int len = eid == PkgEntryIds.Metas ? scCount * 32 : e.Data.Length;
                ms.Write(e.Data, 0, len);
            }
            Buffer.BlockCopy(PkgCrypto.Sha256(ms.ToArray()), 0, pkg, 0x120, 32);
        }
        Buffer.BlockCopy(PkgCrypto.Sha256(digestsEntry.Data), 0, pkg, 0x140, 32);
        Buffer.BlockCopy(PkgCrypto.Sha256(pkg.AsSpan((int)BodyOffset, (int)(pfsOffset - BodyOffset)).ToArray()), 0, pkg, 0x160, 32);
        Buffer.BlockCopy(PkgCrypto.Sha256(outerPfs), 0, pkg, 0x440, 32);
        Buffer.BlockCopy(PkgCrypto.Sha256(outerPfs.AsSpan(0, Math.Min(0x10000, outerPfs.Length)).ToArray()), 0, pkg, 0x460, 32);

        // Entry table
        Buffer.BlockCopy(table, 0, pkg, (int)TableOffset, table.Length);

        // Header digest + signature
        var headerDigest = PkgCrypto.Sha256(pkg.AsSpan(0, 0xFE0).ToArray());
        Buffer.BlockCopy(headerDigest, 0, pkg, 0xFE0, 32);
        var headerSha = PkgCrypto.Sha256(pkg.AsSpan(0, 0x1000).ToArray());
        var signature = PkgCrypto.RSA2048EncryptKey(PkgKeySet.PkgPublicKeys[3], headerSha);
        Buffer.BlockCopy(signature, 0, pkg, 0x1000, 256);
        return pkg;
    }

    private static byte[] BuildEntryKeys(string contentId, byte[][] dk, string passcode)
    {
        var seedDigest = PkgCrypto.Sha256(Encoding.ASCII.GetBytes(contentId.PadRight(48, '\0')));
        var data = new byte[32 + 7 * 32 + 7 * 256];
        Buffer.BlockCopy(seedDigest, 0, data, 0, 32);
        for (int i = 0; i < 7; i++)
        {
            var digest = (byte[])PkgCrypto.Sha256(dk[i]).Clone();
            for (int j = 0; j < 32; j++) digest[j] ^= dk[i][j];
            Buffer.BlockCopy(digest, 0, data, 32 + i * 32, 32);
            byte[] value = i == 0 ? Encoding.ASCII.GetBytes(passcode) : dk[i];
            byte[] enc = PkgCrypto.RSA2048EncryptKey(PkgKeySet.PkgPublicKeys[i], value);
            Buffer.BlockCopy(enc, 0, data, 32 + 7 * 32 + i * 256, 256);
        }
        return data;
    }

    private static (byte[] Iv, byte[] Key) EntryIvKey(byte[] table, int index, byte[] dk)
    {
        var material = new byte[64];
        Buffer.BlockCopy(table, index * 32, material, 0, 32);
        Buffer.BlockCopy(dk, 0, material, 32, 32);
        var hash = PkgCrypto.Sha256(material);
        return (hash[..16], hash[16..]);
    }

    private static byte[] MakePlayGoChunkDat(int chunkCount)
    {
        var data = new byte[4 + chunkCount * 12];
        WriteBe32(data, 0, (uint)chunkCount);
        for (int i = 0; i < chunkCount; i++)
        {
            WriteBe32(data, 4 + i * 12, 0);
            WriteBe32(data, 8 + i * 12, 1);
            WriteBe32(data, 12 + i * 12, 0);
        }
        return data;
    }

    private static byte[] MakePlayGoManifest() => Encoding.ASCII.GetBytes(
        "<?xml version=\"1.0\" encoding=\"utf-8\"?>\n" +
        "<playgo_manifest>\n" +
        "  <scenarios default_id=\"0\">\n" +
        "    <scenario id=\"0\" type=\"sp\" initial_chunk_count=\"1\" label=\"Scenario #0\">0</scenario>\n" +
        "  </scenarios>\n" +
        "</playgo_manifest>\n");

    // ─── Helpers ─────────────────────────────────────────────────────

    private static void WriteBe32(byte[] b, int o, uint v)
    {
        b[o] = (byte)(v >> 24); b[o + 1] = (byte)(v >> 16);
        b[o + 2] = (byte)(v >> 8); b[o + 3] = (byte)v;
    }
    private static void WriteBe16(byte[] b, int o, ushort v)
    {
        b[o] = (byte)(v >> 8); b[o + 1] = (byte)v;
    }
    private static void WriteBe64(byte[] b, int o, ulong v)
    {
        for (int i = 0; i < 8; i++) b[o + i] = (byte)(v >> (56 - 8 * i));
    }

    /// <summary>Copies file to ASCII-safe path if original has non-ASCII chars orbis can't handle.</summary>
    private static string SanitizeForOrbis(string entryPath, string projectFolder)
    {
        var src = Path.Combine(projectFolder, entryPath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(src)) return entryPath;
        bool needs = false;
        foreach (var c in entryPath) if (c > 127) { needs = true; break; }
        if (!needs) return entryPath;
        var safe = System.Text.RegularExpressions.Regex.Replace(entryPath, @"[^\x00-\x7F]",
            m => "_x" + ((int)m.Value[0]).ToString("X2") + "_");
        var dst = Path.Combine(projectFolder, safe.Replace('/', Path.DirectorySeparatorChar));
        var dstDir = Path.GetDirectoryName(dst);
        if (dstDir != null) Directory.CreateDirectory(dstDir);
        if (!File.Exists(dst)) File.Copy(src, dst);
        return safe;
    }

    private static string MakeValidContentId(string cid)
    {
        if (cid.Length == 36 && cid[6] == '-' && cid[16] == '_' && cid[19] == '-') return cid;
        var hash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(
            Encoding.ASCII.GetBytes(cid))).ToLowerInvariant();
        return $"EP0001-CUSA00001_00-{hash[..16].ToUpperInvariant()}";
    }

    private static string? FindOrbisPubCmd()
    {
        var candidates = new[] {
            Path.Combine(AppContext.BaseDirectory, "orbis-pub-cmd.exe"),
            Path.Combine(Environment.CurrentDirectory, "orbis-pub-cmd.exe"),
        };
        var dir = Environment.CurrentDirectory;
        for (int i = 0; i < 5; i++)
        {
            var p = Path.Combine(dir, "orbis-pub-cmd.exe");
            if (File.Exists(p)) return p;
            dir = Path.GetDirectoryName(dir);
            if (dir == null) break;
        }
        return candidates.FirstOrDefault(File.Exists);
    }
}

internal static class VolumeTypeExtensions
{
    public static string ToGp4String(this VolumeType t) => t switch
    {
        VolumeType.PkgPs4App => "pkg_ps4_app",
        VolumeType.PkgPs4Patch => "pkg_ps4_patch",
        VolumeType.PkgPs4AcData => "pkg_ps4_ac_data",
        _ => "pkg_ps4_app"
    };
}
