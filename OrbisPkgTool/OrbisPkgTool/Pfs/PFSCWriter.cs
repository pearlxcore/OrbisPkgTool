namespace OrbisPkgTool.Pfs;

/// <summary>
/// PFSC container writer — wraps a PFS image into the compressed-PFS format.
/// Each compressed block is a COMPLETE RFC1950 zlib stream, verified against
/// real orbis output:
///   0x48 0x89            zlib header (CMF=0x48 → CINFO=4, 4KiB window; FLG=0x89)
///   <raw deflate>        level-6 deflate, no zlib wrapper
///   <4 bytes BE Adler32> of the decompressed block (big-endian, RFC1950 trailer)
/// Blocks that do not compress (stream size >= block size) are stored raw.
/// The block table holds absolute offsets; all header fields are little-endian
/// (validated against real FPKGs).
/// </summary>
public static class PFSCWriter
{
    /// <summary>Stream-based PFSC build for images that don't fit in memory.</summary>
    public static void BuildToStream(Stream pfsImage, Stream output, bool storeAllRaw = true,
        System.Threading.CancellationToken ct = default, Action<long, long>? progress = null)
    {
        const int blockSize = (int)PfsFormat.BlockSize;
        long total = pfsImage.Length;
        int blockCount = (int)((total + blockSize - 1) / blockSize);
        ulong rounded = (ulong)((long)blockCount * blockSize);

        int tableOffset = PfsFormat.PfscTableOffset;
        // dataOffset must clear the block table: align(table end, 0x10000).
        // Small PFSCs land exactly on 0x10000 (verified orbis output); large
        // ones (>8063 blocks) need the table to fit before the data.
        long dataOffset = ((tableOffset + (blockCount + 1) * (long)PfsFormat.PfscTableEntrySize + PfsFormat.BlockSize - 1)
            / PfsFormat.BlockSize) * PfsFormat.BlockSize;

        // Header
        output.Position = 0;
        output.Write(new byte[] { (byte)'P', (byte)'F', (byte)'S', (byte)'C' });
        WriteLe(output, 0u);
        WriteLe(output, 6u);
        WriteLe(output, (uint)blockSize);
        WriteLe(output, (long)blockSize);
        WriteLe(output, (ulong)tableOffset);
        WriteLe(output, (ulong)dataOffset);
        WriteLe(output, rounded);

        // Single-pass construction: compress/store each block exactly once,
        // record its end offset, write the table afterwards. (The old two-pass
        // version compressed every block TWICE — once to size the table, once
        // to write — doubling the cost of the slowest build stage. Deflate is
        // deterministic, so offsets and bytes are identical.)
        pfsImage.Position = 0;
        if (storeAllRaw)
        {
            output.Position = dataOffset;
            var copyBuf = new byte[1 << 20];
            long copied = 0;
            int cn;
            while ((cn = pfsImage.Read(copyBuf, 0, copyBuf.Length)) > 0)
            {
                ct.ThrowIfCancellationRequested();
                output.Write(copyBuf, 0, cn);
                copied += cn;
                progress?.Invoke(copied, total);
            }
            // Table: each raw block occupies its exact bytes (the last block is
            // partial) — offsets are cumulative from dataOffset.
            for (int i = 0; i <= blockCount; i++)
            {
                output.Position = tableOffset + i * 8;
                WriteLe(output, (ulong)(dataOffset + Math.Min((long)i * blockSize, total)));
            }
        }
        else
        {
            var table = new ulong[blockCount + 1];
            var raw = new byte[blockSize];
            long dataPos = dataOffset;
            table[0] = (ulong)dataPos;
            output.Position = dataOffset;
            for (int i = 0; i < blockCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(dataPos - dataOffset, total);
                long remain = total - (long)i * blockSize;
                int len = (int)Math.Min((long)blockSize, remain);
                pfsImage.Read(raw, 0, len);
                var deflater = new ICSharpCode.SharpZipLib.Zip.Compression.Deflater(6, noZlibHeaderOrFooter: true);
                deflater.SetInput(raw, 0, len);
                deflater.Finish();
                using var z = new MemoryStream();
                var compBuf = new byte[blockSize];
                int n;
                while ((n = deflater.Deflate(compBuf)) > 0) z.Write(compBuf, 0, n);
                var comp = z.ToArray();
                if (comp.Length + 6 >= blockSize)
                {
                    output.Write(raw, 0, blockSize);
                    dataPos += blockSize;
                }
                else
                {
                    output.WriteByte(0x48); output.WriteByte(0x89);
                    output.Write(comp, 0, comp.Length);
                    WriteBe(output, Adler32(raw.AsSpan(0, len)));
                    dataPos += comp.Length + 6;
                }
                table[i + 1] = (ulong)dataPos;
            }
            // Table (entry 0 = dataOffset, then each block's end offset).
            for (int i = 0; i <= blockCount; i++)
            {
                output.Position = tableOffset + i * 8;
                WriteLe(output, table[i]);
            }
        }
    }

    private static void WriteLe(Stream s, uint v) =>
        s.Write(new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) }, 0, 4);
    private static void WriteLe(Stream s, long v) => WriteLe(s, (ulong)v);
    private static void WriteLe(Stream s, ulong v) =>
        s.Write(new[]
        {
            (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24),
            (byte)(v >> 32), (byte)(v >> 40), (byte)(v >> 48), (byte)(v >> 56),
        }, 0, 8);

    public static byte[] Build(byte[] pfsImage, bool storeAllRaw = false)
    {
        const int blockSize = (int)PfsFormat.BlockSize;
        int blockCount = (pfsImage.Length + blockSize - 1) / blockSize;
        ulong rounded = (ulong)((long)blockCount * blockSize);

        int tableOffset = PfsFormat.PfscTableOffset; // aligned like real FPKGs
        // dataOffset clears the block table (see BuildToStream).
        long dataOffset = ((tableOffset + (blockCount + 1) * (long)PfsFormat.PfscTableEntrySize + PfsFormat.BlockSize - 1)
            / PfsFormat.BlockSize) * PfsFormat.BlockSize;
        var compressedBlocks = new byte[blockCount][];
        long dataSize = 0;
        for (int i = 0; i < blockCount; i++)
        {
            int len = Math.Min(blockSize, pfsImage.Length - i * blockSize);
            using var z = new MemoryStream();
            // RAW deflate (no zlib wrapper). Verified against real orbis output:
            // orbis PFSC blocks start with raw deflate (e.g. 0x48 0x89), NOT a
            // zlib header (0x78 0x9C). LibOrbisPkg's reader skips 2 bytes then
            // raw-deflates, which matches this format.
            // Real orbis PFSC: blocks that don't compress (compressed size >=
            // block size) are stored RAW (uncompressed, exactly blockSize bytes).
            // Compressed blocks use zlib's raw deflate WITHOUT a zlib header —
            // orbis output starts with BFINAL=0 dynamic-Huffman streams
            // (first bytes 0x48 0x89 ...). .NET DeflateStream produces BFINAL=1
            // streams that orbis's decoder rejects, so we use SharpZipLib's
            // raw Deflater with the same zlib semantics.
            if (storeAllRaw)
            {
                // Diagnostic: store every block raw (no compression).
                compressedBlocks[i] = new byte[blockSize];
                Buffer.BlockCopy(pfsImage, i * blockSize, compressedBlocks[i], 0, len);
                dataSize += compressedBlocks[i].Length;
                continue;
            }
            // Raw deflate (no zlib header), matching orbis PFSC block format.
            // Level 6 = zlib default — verified byte-identical to orbis output
            // (orbis blocks = 0x48 0x89 + level-6 raw deflate).
            var deflater = new ICSharpCode.SharpZipLib.Zip.Compression.Deflater(6, noZlibHeaderOrFooter: true);
            deflater.SetInput(pfsImage, i * blockSize, len);
            deflater.Finish();
            var compBuf = new byte[blockSize];
            int n;
            using (z)
            {
                while ((n = deflater.Deflate(compBuf)) > 0)
                    z.Write(compBuf, 0, n);
            }
            var comp = z.ToArray();
            if (comp.Length + 6 >= blockSize)
            {
                compressedBlocks[i] = new byte[blockSize];
                Buffer.BlockCopy(pfsImage, i * blockSize, compressedBlocks[i], 0, len);
            }
            else
            {
                // Complete zlib stream: 0x48 0x89 header + raw deflate +
                // big-endian Adler32 of the decompressed block (orbis format,
                // verified against real orbis PFSC sectors).
                compressedBlocks[i] = new byte[comp.Length + 6];
                compressedBlocks[i][0] = 0x48; compressedBlocks[i][1] = 0x89;
                Buffer.BlockCopy(comp, 0, compressedBlocks[i], 2, comp.Length);
                uint adler = Adler32(pfsImage.AsSpan(i * blockSize, len));
                compressedBlocks[i][comp.Length + 2] = (byte)(adler >> 24);
                compressedBlocks[i][comp.Length + 3] = (byte)(adler >> 16);
                compressedBlocks[i][comp.Length + 4] = (byte)(adler >> 8);
                compressedBlocks[i][comp.Length + 5] = (byte)adler;
            }
            dataSize += compressedBlocks[i].Length;
        }
        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        // Header — all multi-byte fields are LITTLE-endian. The magic is raw ASCII.
        w.Write((byte)'P'); w.Write((byte)'F'); w.Write((byte)'S'); w.Write((byte)'C');
        WriteLe(w, 0u);              // Unk4 = 0 (required by LibOrbisPkg PFSCReader)
        WriteLe(w, 6u);              // Unk8 = 6 (MkPFS/LibOrbisPkg value)
        WriteLe(w, (uint)blockSize); // BlockSz (uint32)
        WriteLe(w, (long)blockSize); // BlockSz2 (int64, must equal BlockSz per LibOrbisPkg validation)
        WriteLe(w, (ulong)tableOffset);
        WriteLe(w, (ulong)dataOffset);
        WriteLe(w, rounded);

        // Block table at tableOffset: absolute file offsets (entry 0 = the data start).
        ms.Position = tableOffset;
        long pos = dataOffset;
        for (int i = 0; i <= blockCount; i++)
        {
            WriteLe(w, (ulong)pos);
            if (i < blockCount)
                pos += compressedBlocks[i].Length;
        }

        // Data.
        ms.Position = dataOffset;
        foreach (var b in compressedBlocks)
            ms.Write(b, 0, b.Length);
        return ms.ToArray();
    }

    /// <summary>RFC1950 Adler-32 of the decompressed block (stored big-endian).</summary>
    private static uint Adler32(ReadOnlySpan<byte> data)
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

    private static void WriteBe(Stream s, uint v) =>
        s.Write(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v }, 0, 4);

    private static void WriteLe(BinaryWriter w, uint v) =>
        w.Write(new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) });

    private static void WriteLe(BinaryWriter w, ulong v) =>
        w.Write(new[]
        {
            (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24),
            (byte)(v >> 32), (byte)(v >> 40), (byte)(v >> 48), (byte)(v >> 56),
        });
}
