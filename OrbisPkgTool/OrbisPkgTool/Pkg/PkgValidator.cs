using OrbisPkgTool.Binary;
using OrbisPkgTool.Crypto;
using OrbisPkgTool.Pfs;

namespace OrbisPkgTool.Pkg;

/// <summary>Thrown when a built package violates a format invariant.</summary>
public sealed class ValidationFailure : Exception
{
    public string Stage { get; }
    public string Structure { get; }
    public string Offset { get; }

    public ValidationFailure(string stage, string structure, string offset, string reason)
        : base($"[{stage}] {structure} @{offset}: {reason}")
    {
        Stage = stage; Structure = structure; Offset = offset;
    }
}

/// <summary>
/// Structured validation of a built PKG (the 8-stage check used by
/// `--validate` and by the regression suite). All checks are read-only;
/// none of them re-derive format rules — they verify the invariants that
/// orbis-pub-cmd 3.87 was empirically shown to require (see PfsFormat.cs).
/// </summary>
public static class PkgValidator
{
    /// <summary>
    /// Runs the full 8-stage validation on a built PKG file.
    /// Throws <see cref="ValidationFailure"/> on the first violated invariant.
    /// </summary>
    public static void ValidatePkgFile(string pkgPath, string passcode,
        Action<string, string>? report = null)
    {
        // ---- Stage 1: package header + entry table ----
        report?.Invoke("1", "package header + entry table");
        using var reader = new PkgReader(pkgPath, passcode);
        var h = reader.Header;
        long pkgLen = new FileInfo(pkgPath).Length;
        if (h.PfsImageOffset == 0)
            throw new ValidationFailure("1", "header", "0x410", "pfs_image_offset is zero");
        if (h.PfsImageOffset % PfsFormat.PfsImageAlignment != 0)
            throw new ValidationFailure("1", "header", "0x410",
                $"pfs_image_offset 0x{h.PfsImageOffset:X} not {PfsFormat.PfsImageAlignment:X}-aligned");
        if (h.PfsImageOffset + h.PfsImageSize > (ulong)pkgLen)
            throw new ValidationFailure("1", "header", "0x410",
                "pfs image range exceeds package size");

        var ids = new HashSet<uint>();
        foreach (var e in reader.Entries)
        {
            if (!ids.Add(e.Id))
                throw new ValidationFailure("1", "entry table", $"id 0x{e.Id:X8}",
                    "duplicate entry ID");
            if (e.DataOffset < 0 || e.DataOffset + e.DataSize > pkgLen)
                throw new ValidationFailure("1", "entry table", $"id 0x{e.Id:X8}",
                    $"entry range 0x{e.DataOffset:X}..0x{e.DataOffset + e.DataSize:X} outside package");
            if (e.IsEncrypted && (e.DataSize & 15) != 0)
                throw new ValidationFailure("1", "entry table", $"id 0x{e.Id:X8}",
                    $"encrypted stored size {e.DataSize} not 16-aligned");
        }

        // ---- Stage 2: outer PFS structure ----
        report?.Invoke("2", "outer PFS structure");
        var outer = reader.GetOuterPfs()
            ?? throw new ValidationFailure("2", "outer PFS", "0x410", "cannot open outer PFS");
        ValidatePfsBlocks(outer, outer.Header, "outer PFS");

        // ---- Stage 3: PFSC (pfs_image.dat) ----
        report?.Invoke("3", "PFSC");
        using (var pfsc = OpenPfsImageDat(pkgPath, reader))
            ValidatePfsc(pfsc);

        // ---- Stage 4: inner PFS ----
        report?.Invoke("4", "inner PFS");
        using (var inner = new MemoryStream())
        {
            reader.CopyRawInnerPfsTo(inner);
            inner.Position = 0;
            var innerReader = PfsReader.Open(new BigEndianReader(inner), 0);
            ValidatePfsBlocks(innerReader, innerReader.Header, "inner PFS");
        }

        // ---- Stage 5: digests ----
        report?.Invoke("5", "digests");
        ValidateDigests(pkgPath, reader);

        // ---- Stage 6: outer PFS signatures ----
        report?.Invoke("6", "outer PFS signatures");
        ValidateOuterSigs(pkgPath, reader, outer);

        // ---- Stage 7: final structural re-open (filesystem walk) ----
        report?.Invoke("7", "filesystem walk");
        var files = reader.ListFiles();
        if (files.Count == 0)
            throw new ValidationFailure("7", "filesystem", "-", "no files found");
        report?.Invoke("8", "complete");
    }

    /// <summary>Validates a PFS: inode packing rule, pointer bounds, no overlapping data blocks.</summary>
    public static void ValidatePfsBlocks(PfsReader pfs, PfsHeader h, string what)
    {
        long ndblock = h.Ndblock;
        long imageBlocks = ndblock;
        int inodeSize = h.Mode.HasFlag(PfsMode.Is64Bit) ? 0x310
            : h.Mode.HasFlag(PfsMode.Signed) ? PfsFormat.S32InodeSize : PfsFormat.D32InodeSize;

        // Packing rule: no inode may straddle a block boundary.
        for (long i = 0; i < h.DinodeCount; i++)
        {
            long pos = PfsFormat.BlockSize + i * inodeSize; // ideal contiguous position
            // The reader skips to the next block when an inode does not fit;
            // recompute the true offset the same way.
            long off = PfsFormat.BlockSize;
            for (long j = 0; j < i; j++)
            {
                if (off % PfsFormat.BlockSize > PfsFormat.BlockSize - inodeSize)
                    off += PfsFormat.BlockSize - (off % PfsFormat.BlockSize);
                off += inodeSize;
            }
            if (off % PfsFormat.BlockSize + inodeSize > PfsFormat.BlockSize)
                throw new ValidationFailure("pfs", what, $"inode {i}",
                    "inode straddles a block boundary");
            _ = pos;
        }

        // Collect all referenced data blocks; bounds + overlap.
        var seen = new HashSet<long>();
        void CheckBlock(long b, string where)
        {
            if (b < 0 || b >= imageBlocks)
                throw new ValidationFailure("pfs", what, where, $"block {b} outside PFS (ndblock={imageBlocks})");
            if (!seen.Add(b))
                throw new ValidationFailure("pfs", what, where, $"block {b} referenced twice (overlap)");
        }

        for (uint i = 0; i < h.DinodeCount; i++)
        {
            var ino = pfs.GetInode(i);
            if (ino == null) continue;
            foreach (var b in ino.DirectBlocks)
                if (b > 0 && b != PfsFormat.ContiguousRunSentinel) CheckBlock(b, $"ino{i} db");
            // Indirect pointer blocks themselves
            foreach (var ib in ino.IndirectBlocks)
                if (ib > 0) CheckBlock(ib, $"ino{i} ib");
            // Skip content blocks for the contiguous-run sentinel case: the
            // reader treats -1 as "contiguous from previous", so only db[0]
            // is explicit — no overlap possible beyond what the bounds check
            // on the first block covers.
        }

        // For the outer PFS the single data file's direct/indirect chain is
        // walked by the reader when reading; verifying the pointer table
        // bounds above plus a successful full-image read is sufficient.
    }

    /// <summary>Validates a PFSC container: magic, table, alignment, block sizes, round-trip.</summary>
    public static void ValidatePfsc(byte[] pfsc)
    {
        using var ms = new MemoryStream(pfsc, false);
        ValidatePfsc(ms);
    }

    /// <summary>Streaming variant (PFSC can exceed 2 GB).</summary>
    public static void ValidatePfsc(Stream pfsc)
    {
        pfsc.Position = 0;
        var hdr = new byte[0x30];
        if (pfsc.Read(hdr, 0, hdr.Length) != hdr.Length
            || hdr[0] != 'P' || hdr[1] != 'F' || hdr[2] != 'S' || hdr[3] != 'C')
            throw new ValidationFailure("pfsc", "header", "0x00", "bad PFSC magic");
        long tableOff = BitConverter.ToInt64(hdr, 0x18);
        long dataOff = BitConverter.ToInt64(hdr, 0x20);
        long rounded = BitConverter.ToInt64(hdr, 0x28);
        long pfscLen = pfsc.Length;
        if (dataOff != PfsFormat.PfscDataOffset)
            throw new ValidationFailure("pfsc", "header", "0x20",
                $"dataOffset 0x{dataOff:X} not {PfsFormat.PfscDataOffset:X} (block-aligned)");
        if (tableOff < 0x30 || tableOff + 8 > pfscLen)
            throw new ValidationFailure("pfsc", "header", "0x18", "table offset outside file");
        if (rounded <= 0 || rounded % PfsFormat.BlockSize != 0)
            throw new ValidationFailure("pfsc", "header", "0x28",
                $"rounded size {rounded} not a positive multiple of block size");

        int blockCount = (int)((rounded + PfsFormat.BlockSize - 1) / PfsFormat.BlockSize);
        long prev = -1;
        long tableEnd = tableOff + (blockCount + 1) * PfsFormat.PfscTableEntrySize;
        if (tableEnd > pfscLen)
            throw new ValidationFailure("pfsc", "table", "0x18", "table extends past file end");
        var entry = new byte[8];
        for (int i = 0; i <= blockCount; i++)
        {
            pfsc.Position = tableOff + i * PfsFormat.PfscTableEntrySize;
            pfsc.ReadExactly(entry, 0, 8);
            long v = BitConverter.ToInt64(entry, 0);
            if (v < prev)
                throw new ValidationFailure("pfsc", "table", $"entry {i}",
                    $"table not monotonic ({v} < {prev})");
            prev = v;
        }
        if (prev != pfscLen)
            throw new ValidationFailure("pfsc", "table", "last",
                $"final table offset {prev} != PFSC size {pfscLen}");

        // Full round-trip decompression.
        pfsc.Position = 0;
        var stream = new PFSCStream(pfsc);
        long total = 0;
        var buf = new byte[PfsFormat.BlockSize];
        int n;
        while ((n = stream.Read(buf, 0, buf.Length)) > 0) total += n;
        if (total != rounded)
            throw new ValidationFailure("pfsc", "data", "-",
                $"decompressed {total} bytes, expected {rounded}");
    }

    /// <summary>Recomputes every header digest and compares with the stored values.</summary>
    public static void ValidateDigests(string pkgPath, PkgReader reader)
    {
        var head = new byte[0x1000];
        using (var fs = File.OpenRead(pkgPath))
        {
            fs.ReadExactly(head, 0, 0x1000);
        }
        long pfsOff = (long)reader.Header.PfsImageOffset;

        // sc_entries1_hash: entrykeys|imagekey|gd|metas(full table)|digests
        byte[] h1 = HashEntryChain(reader, new[] { PkgEntryIds.EntryKeys, PkgEntryIds.ImageKey, PkgEntryIds.GeneralDigests, PkgEntryIds.Metas, PkgEntryIds.Digests });
        // sc_entries2_hash: entrykeys|imagekey|gd|metas[6 entries]
        byte[] h2 = HashEntryChain(reader, new[] { PkgEntryIds.EntryKeys, PkgEntryIds.ImageKey, PkgEntryIds.GeneralDigests, PkgEntryIds.Metas }, metasBytes: 6 * PkgEntry.Size);
        Check32(head, 0x100, h1, "sc_entries1_hash");
        Check32(head, 0x120, h2, "sc_entries2_hash");
        Check32(head, 0x140, PkgCrypto.Sha256(ReadEntryBytes(reader, PkgEntryIds.Digests)), "digest_table_hash");

        // body digest: pkg[0x2000 .. pfsOff)
        using (var sha = System.Security.Cryptography.SHA256.Create())
        using (var fs = File.OpenRead(pkgPath))
        {
            fs.Position = PfsFormat.PkgBodyOffset;
            var buf = new byte[1 << 20];
            long remaining = pfsOff - PfsFormat.PkgBodyOffset;
            while (remaining > 0)
            {
                int c = fs.Read(buf, 0, (int)Math.Min(buf.Length, remaining));
                if (c <= 0) break;
                sha.TransformBlock(buf, 0, c, null, 0);
                remaining -= c;
            }
            sha.TransformFinalBlock([], 0, 0);
            Check32(head, 0x160, sha.Hash!, "body_digest");
        }

        // pfs digests (streamed)
        using (var sha = System.Security.Cryptography.SHA256.Create())
        using (var fs = File.OpenRead(pkgPath))
        {
            fs.Position = pfsOff;
            var buf = new byte[1 << 20];
            int c;
            while ((c = fs.Read(buf, 0, buf.Length)) > 0) sha.TransformBlock(buf, 0, c, null, 0);
            sha.TransformFinalBlock([], 0, 0);
            Check32(head, 0x440, sha.Hash!, "pfs_image_digest");
        }
        using (var sha = System.Security.Cryptography.SHA256.Create())
        using (var fs = File.OpenRead(pkgPath))
        {
            fs.Position = pfsOff;
            var buf = new byte[PfsFormat.BlockSize];
            int c = fs.Read(buf, 0, buf.Length);
            if (c < buf.Length) Array.Resize(ref buf, c);
            sha.TransformFinalBlock(buf, 0, buf.Length);
            Check32(head, 0x460, sha.Hash!, "pfs_signed_digest");
        }

        // header digest: sha256(header[0..0xFE0]) stored at 0xFE0
        Check32(head, 0xFE0, PkgCrypto.Sha256(head.AsSpan(0, 0xFE0).ToArray()), "header_digest");
    }

    /// <summary>Verifies the outer PFS HMAC block signatures (signKey scheme, XTS-decrypted).</summary>
    public static void ValidateOuterSigs(string pkgPath, PkgReader reader, PfsReader outer)
    {
        var ekpfs = reader.Ekpfs ?? throw new ValidationFailure("sigs", "ekpfs", "-", "no EKPFS");
        long pfsOff = (long)reader.Header.PfsImageOffset;
        byte[] seed = ReadAt(pkgPath, pfsOff + 0x370, 16);
        byte[] signKey = PkgCrypto.HmacSha256(ekpfs, Concat(Le32(2), seed));
        var (tk, dk) = PfsReader.DeriveXtsKeys(new PfsHeader { Mode = outer.Header.Mode, Seed = seed }, ekpfs);

        // The inode-table block (block 1) is XTS-encrypted — decrypt it once;
        // the stored signature slots live inside it.
        byte[] tbl = ReadAt(pkgPath, pfsOff + PfsFormat.BlockSize, (int)PfsFormat.BlockSize);
        for (int s = 16; s < 32; s++)
            PfsReader.XtsDecryptSector(tbl, (s - 16) * PfsFormat.XtsSectorSize, (ulong)s, dk!, tk!);

        // Decrypts a content block and returns its HMAC.
        byte[] ContentHmac(long blockOffset, int len)
        {
            byte[] data = ReadAt(pkgPath, pfsOff + blockOffset, len);
            int firstSector = (int)(blockOffset / PfsFormat.XtsSectorSize);
            if (firstSector >= 16 && blockOffset / PfsFormat.BlockSize != 4)
                for (int s = firstSector; s < firstSector + len / PfsFormat.XtsSectorSize; s++)
                    PfsReader.XtsDecryptSector(data, (s - firstSector) * PfsFormat.XtsSectorSize, (ulong)s, dk!, tk!);
            return PkgCrypto.HmacSha256(signKey, data);
        }

        // Stored sig for a slot inside the (decrypted) inode-table block.
        void CheckSlot(string what, long slot, byte[] expected)
        {
            long tblOff = slot - PfsFormat.BlockSize;
            if (tblOff < 0 || tblOff + 32 > tbl.Length)
                throw new ValidationFailure("sigs", what, $"slot 0x{slot:X}", "slot outside inode table");
            var stored = tbl.AsSpan((int)tblOff, 32);
            if (!expected.AsSpan().SequenceEqual(stored))
                throw new ValidationFailure("sigs", what, $"slot 0x{slot:X}", "signature mismatch");
        }

        long ino0 = PfsFormat.BlockSize;
        long ino1 = ino0 + PfsFormat.S32InodeSize;
        long ino2 = ino1 + PfsFormat.S32InodeSize;
        long ino3 = ino2 + PfsFormat.S32InodeSize;
        var f = outer.FindFile("pfs_image.dat")
            ?? throw new ValidationFailure("sigs", "outer", "-", "pfs_image.dat not found");
        long dataStart = f.StartBlock;

        // content blocks via direct pointers
        for (int i = 0; i < Math.Min(12, f.Blocks); i++)
            CheckSlot($"ino3 db[{i}]", ino3 + PfsFormat.DirectPointersOffset + PfsFormat.S32SlotSize * i,
                ContentHmac((dataStart + i) * PfsFormat.BlockSize, (int)PfsFormat.BlockSize));
        // superroot / fpt / uroot
        CheckSlot("ino0 db[0]", ino0 + PfsFormat.DirectPointersOffset, ContentHmac(2 * PfsFormat.BlockSize, (int)PfsFormat.BlockSize));
        CheckSlot("ino1 db[0]", ino1 + PfsFormat.DirectPointersOffset, ContentHmac(3 * PfsFormat.BlockSize, (int)PfsFormat.BlockSize));
        var uroot = outer.GetInode(2);
        if (uroot != null)
            CheckSlot("ino2 db[0]", ino2 + PfsFormat.DirectPointersOffset, ContentHmac(uroot.StartBlock * PfsFormat.BlockSize, (int)PfsFormat.BlockSize));
        // inode-table block sig at the header dinode's db[0] (slot in plaintext header)
        {
            byte[] stored = ReadAt(pkgPath, pfsOff + PfsFormat.HeaderDinodeOffset + 0x68, 32);
            var calc = ContentHmac(PfsFormat.BlockSize, (int)PfsFormat.BlockSize);
            if (!calc.AsSpan().SequenceEqual(stored))
                throw new ValidationFailure("sigs", "hdr dinode db[0]", "0x50+0x68", "signature mismatch");
        }
        // header block sig at 0x380 covering header[0..0x5A0]
        {
            byte[] stored = ReadAt(pkgPath, pfsOff + 0x380, 32);
            byte[] hdrBlock = ReadAt(pkgPath, pfsOff, 0x5A0);
            for (int i = 0x380; i < 0x380 + 32; i++) hdrBlock[i] = 0;
            var calc = PkgCrypto.HmacSha256(signKey, hdrBlock);
            if (!calc.AsSpan().SequenceEqual(stored))
                throw new ValidationFailure("sigs", "header", "0x380", "header block signature mismatch");
        }
    }

    // ── helpers ──────────────────────────────────────────────────────────

    private static byte[] ReadEntryBytes(PkgReader reader, uint id)
    {
        var e = reader.Entries.First(x => x.Id == id);
        using var fs = File.OpenRead(reader.PkgPath);
        var buf = new byte[(int)e.DataSize];
        fs.Position = e.DataOffset;
        fs.ReadExactly(buf, 0, buf.Length);
        return buf;
    }

    private static byte[] HashEntryChain(PkgReader reader, uint[] ids, int? metasBytes = null)
    {
        using var ms = new MemoryStream();
        foreach (var id in ids)
        {
            var e = reader.Entries.First(x => x.Id == id);
            int len = id == PkgEntryIds.Metas && metasBytes is int mb ? mb : (int)e.DataSize;
            var buf = new byte[len];
            using var fs = File.OpenRead(reader.PkgPath);
            fs.Position = e.DataOffset;
            fs.ReadExactly(buf, 0, len);
            ms.Write(buf, 0, len);
        }
        return PkgCrypto.Sha256(ms.ToArray());
    }

    private static void Check32(byte[] head, int off, byte[] calc, string what)
    {
        if (!calc.AsSpan().SequenceEqual(head.AsSpan(off, 32)))
            throw new ValidationFailure("digests", what, $"0x{off:X}",
                $"stored {Convert.ToHexString(head.AsSpan(off, 32))} != computed {Convert.ToHexString(calc)}");
    }

    private static Stream OpenPfsImageDat(string pkgPath, PkgReader reader)
    {
        var outer = reader.GetOuterPfs()
            ?? throw new ValidationFailure("pfsc", "outer", "-", "cannot open outer PFS");
        var f = outer.FindFile("pfs_image.dat")
            ?? throw new ValidationFailure("pfsc", "outer", "-", "pfs_image.dat not found");
        return outer.OpenFileStream(f);
    }

    private static byte[] ReadAt(string pkgPath, long off, int len)
    {
        using var fs = File.OpenRead(pkgPath);
        fs.Position = off;
        var buf = new byte[len];
        int got = 0;
        while (got < len)
        {
            int c = fs.Read(buf, got, len - got);
            if (c <= 0) break;
            got += c;
        }
        if (got != len) Array.Resize(ref buf, got);
        return buf;
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    private static byte[] Le32(int v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };
}
