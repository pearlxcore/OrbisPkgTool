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
///
/// Per-file policy: a PfsBuildResult allocation manifest maps files to their
/// PFS block ranges. Files marked raw (e.g. GP4 pfs_compression="disable",
/// replayed from the original package's profile) have EVERY allocated block
/// stored uncompressed; structural blocks (header/inodes/dirents/FPT) and
/// enabled files compress normally with raw fallback for incompressible data.
/// </summary>
public static class PFSCWriter
{
    /// <summary>
    /// A set of PFS block indexes that must be stored RAW regardless of
    /// compressibility. Thread-safe bitmap (long bits per element).
    /// </summary>
    public sealed class RawBlockSet
    {
        private readonly long[] _bits;

        public RawBlockSet(long blockCount)
        {
            _bits = new long[(blockCount >> 6) + 1];
        }

        public void AddRange(long start, long count)
        {
            for (long b = start; b < start + count; b++)
                if (b >= 0)
                    _bits[b >> 6] |= 1L << (int)(b & 63);
        }

        public bool Contains(long block) =>
            block >= 0 && (block >> 6) < _bits.Length && (_bits[block >> 6] & (1L << (int)(block & 63))) != 0;
    }

    /// <summary>
    /// Builds the raw-block set from an allocation manifest + per-file policy:
    /// every block allocated to a "disable" file is stored raw.
    /// </summary>
    public static RawBlockSet BuildRawBlockSet(PfsBuildResult allocation,
        IReadOnlyDictionary<string, PfscPolicy> policy)
    {
        var set = new RawBlockSet(allocation.BlockCount);
        foreach (var f in allocation.Files)
        {
            if (!policy.TryGetValue(PfscProfiler.NormalizeKey(f.Path), out var p) || p != PfscPolicy.Disable)
                continue;
            set.AddRange(f.StartBlock, f.BlockCount);
        }
        return set;
    }

    /// <summary>Stream-based PFSC build for images that don't fit in memory.</summary>
    public static void BuildToStream(Stream pfsImage, Stream output, bool storeAllRaw = true,
        System.Threading.CancellationToken ct = default, Action<long, long>? progress = null,
        RawBlockSet? rawBlocks = null, int workers = 1)
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
            return;
        }

        {
            var table = new ulong[blockCount + 1];
            long dataPos = dataOffset;
            table[0] = (ulong)dataPos;
            output.Position = dataOffset;

            // workers <= 0 means "one per core" (normalized here so library
            // callers can pass 0); 1 keeps the proven serial path.
            int effectiveWorkers = workers <= 0 ? Environment.ProcessorCount : workers;

            if (effectiveWorkers > 1 && !storeAllRaw && blockCount > 0)
            {
                // Parallel path: identical bytes, bounded memory. The image is
                // processed in fixed-size chunks: read the chunk sequentially,
                // compress its blocks concurrently (each worker slices the
                // shared chunk buffer — CompressBlock takes an offset), then
                // write the chunk's blocks in order. Peak memory stays at
                // ~chunk bytes + compressed results no matter the image size.
                const int chunkBlocks = 256; // 256 × 64 KiB = 16 MiB
                var parallelOptions = new System.Threading.Tasks.ParallelOptions
                {
                    MaxDegreeOfParallelism = effectiveWorkers,
                    CancellationToken = ct,
                };
                var chunkBuf = new byte[Math.Min(chunkBlocks, blockCount) * blockSize];
                byte[]?[] results = new byte[chunkBlocks][];
                for (int chunkStart = 0; chunkStart < blockCount; chunkStart += chunkBlocks)
                {
                    ct.ThrowIfCancellationRequested();
                    int chunkCount = Math.Min(chunkBlocks, blockCount - chunkStart);
                    int chunkBytes = (int)Math.Min((long)chunkCount * blockSize,
                        total - (long)chunkStart * blockSize);
                    pfsImage.Position = (long)chunkStart * blockSize;
                    pfsImage.ReadExactly(chunkBuf, 0, chunkBytes);
                    // Non-aligned images (never produced by the PFS writer, but
                    // accepted here): the raw-fallback tail beyond chunkBytes
                    // must be zeros like the memory builder, not stale bytes
                    // from the previous chunk.
                    int chunkCapacity = chunkCount * blockSize;
                    if (chunkBytes < chunkCapacity)
                        Array.Clear(chunkBuf, chunkBytes, chunkCapacity - chunkBytes);
                    System.Threading.Tasks.Parallel.For(0, chunkCount, parallelOptions, j =>
                    {
                        results[j] = null; // null = store raw (slice of chunkBuf)
                        bool forceRaw = rawBlocks?.Contains(chunkStart + j) ?? false;
                        if (!forceRaw)
                        {
                            int len = (int)Math.Min((long)blockSize,
                                total - (long)(chunkStart + j) * blockSize);
                            results[j] = CompressBlock(chunkBuf, j * blockSize, len);
                        }
                    });
                    // Sequential write — byte-for-byte the serial path's layout.
                    for (int j = 0; j < chunkCount; j++)
                    {
                        var comp = results[j];
                        if (comp != null)
                        {
                            output.Write(comp, 0, comp.Length);
                            dataPos += comp.Length;
                        }
                        else
                        {
                            // Raw fallback: full blockSize always (the PFS image
                            // is block-aligned, so the last block is exactly
                            // blockSize too).
                            output.Write(chunkBuf, j * blockSize, blockSize);
                            dataPos += blockSize;
                        }
                        table[chunkStart + j + 1] = (ulong)dataPos;
                        results[j] = null; // release before the next chunk fills it
                        progress?.Invoke(dataPos - dataOffset, total);
                    }
                }
                // Table (entry 0 = dataOffset, then each block's end offset).
                for (int i = 0; i <= blockCount; i++)
                {
                    output.Position = tableOffset + i * 8;
                    WriteLe(output, table[i]);
                }
                return;
            }

            // Serial path (workers == 1, storeAllRaw, or empty image).
            var raw = new byte[blockSize];
            for (int i = 0; i < blockCount; i++)
            {
                ct.ThrowIfCancellationRequested();
                progress?.Invoke(dataPos - dataOffset, total);
                long remain = total - (long)i * blockSize;
                int len = (int)Math.Min((long)blockSize, remain);
                pfsImage.ReadExactly(raw, 0, len);
                // Non-aligned images (never produced by the PFS writer): the
                // raw-fallback tail beyond len must be zeros like the memory
                // builder, not stale bytes from the previous block.
                if (len < blockSize)
                    Array.Clear(raw, len, blockSize - len);
                bool forceRaw = rawBlocks?.Contains(i) ?? false;
                if (!forceRaw)
                {
                    var comp = CompressBlock(raw, 0, len);
                    if (comp != null)
                    {
                        output.Write(comp, 0, comp.Length);
                        dataPos += comp.Length;
                        table[i + 1] = (ulong)dataPos;
                        continue;
                    }
                }
                // Raw fallback: full blockSize always (the PFS image is block-aligned,
                // so the last block is exactly blockSize too).
                output.Write(raw, 0, blockSize);
                dataPos += blockSize;
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

    public static byte[] Build(byte[] pfsImage, bool storeAllRaw = false, RawBlockSet? rawBlocks = null,
        int workers = 1)
    {
        const int blockSize = (int)PfsFormat.BlockSize;
        int blockCount = (pfsImage.Length + blockSize - 1) / blockSize;
        ulong rounded = (ulong)((long)blockCount * blockSize);

        int tableOffset = PfsFormat.PfscTableOffset; // aligned like real FPKGs
        // dataOffset clears the block table (see BuildToStream).
        long dataOffset = ((tableOffset + (blockCount + 1) * (long)PfsFormat.PfscTableEntrySize + PfsFormat.BlockSize - 1)
            / PfsFormat.BlockSize) * PfsFormat.BlockSize;
        var compressedBlocks = new byte[blockCount][];
        int effectiveWorkers = workers <= 0 ? Environment.ProcessorCount : workers;

        if (effectiveWorkers > 1 && !storeAllRaw && blockCount > 0)
        {
            var parallelOptions = new System.Threading.Tasks.ParallelOptions
            {
                MaxDegreeOfParallelism = effectiveWorkers,
            };
            System.Threading.Tasks.Parallel.For(0, blockCount, parallelOptions, i =>
            {
                int len = Math.Min(blockSize, pfsImage.Length - i * blockSize);
                bool forceRaw = rawBlocks?.Contains(i) ?? false;
                if (forceRaw)
                {
                    compressedBlocks[i] = new byte[blockSize];
                    Buffer.BlockCopy(pfsImage, i * blockSize, compressedBlocks[i], 0, len);
                    return;
                }
                var comp = CompressBlock(pfsImage, i * blockSize, len);
                if (comp != null)
                    compressedBlocks[i] = comp;
                else
                {
                    compressedBlocks[i] = new byte[blockSize];
                    Buffer.BlockCopy(pfsImage, i * blockSize, compressedBlocks[i], 0, len);
                }
            });
        }
        else
        {
        for (int i = 0; i < blockCount; i++)
        {
            int len = Math.Min(blockSize, pfsImage.Length - i * blockSize);
            if (storeAllRaw)
            {
                // Diagnostic: store every block raw (no compression).
                compressedBlocks[i] = new byte[blockSize];
                Buffer.BlockCopy(pfsImage, i * blockSize, compressedBlocks[i], 0, len);
                continue;
            }
            // Per-file policy: blocks belonging to "disable" files are stored
            // raw without even attempting compression (matches orbis behavior
            // for pfs_compression="disable" files).
            bool forceRaw = rawBlocks?.Contains(i) ?? false;
            if (forceRaw)
            {
                compressedBlocks[i] = new byte[blockSize];
                Buffer.BlockCopy(pfsImage, i * blockSize, compressedBlocks[i], 0, len);
                continue;
            }
            // Complete zlib stream: 0x48 0x89 header + raw deflate +
            // big-endian Adler32 of the decompressed block (orbis format,
            // verified against real orbis PFSC sectors). Blocks that fail to
            // compress below the block size fall back to raw.
            var comp = CompressBlock(pfsImage, i * blockSize, len);
            if (comp != null)
            {
                compressedBlocks[i] = comp;
            }
            else
            {
                compressedBlocks[i] = new byte[blockSize];
                Buffer.BlockCopy(pfsImage, i * blockSize, compressedBlocks[i], 0, len);
            }
        }
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

    /// <summary>
    /// Compresses one block into the complete PFSC zlib stream (0x48 0x89 +
    /// raw deflate + BE Adler32). Returns null when the block does not
    /// compress (>= blockSize after wrapping) — the caller stores it raw.
    /// Prefers zlib's 4 KiB window (deflateInit2 windowBits=-12) so the
    /// stream is formally valid for the declared CMF window; falls back to
    /// SharpZipLib when zlib1.dll is unavailable.
    /// </summary>
    internal static byte[]? CompressBlock(byte[] block, int offset, int count)
    {
        // Try the 4 KiB-window zlib first (formally valid for the 0x48 header).
        // Output buffer has headroom for stored-block overhead so zlib always
        // completes and can itself decide compressibility.
        if (PfscDeflate.IsAvailable)
        {
            var zbuf = new byte[count + 128];
            int n = PfscDeflate.TryDeflate4K(block, offset, count, zbuf);
            if (n > 0 && n + 6 < count)
                return WrapZlib(zbuf, n, block, offset, count);
            if (n >= 0)
                return null; // zlib ran: incompressible → raw
            // n < 0: zlib failed to init (shouldn't happen) → SharpZipLib
        }

        var deflater = new ICSharpCode.SharpZipLib.Zip.Compression.Deflater(PfsFormat.PfscDeflateLevel, noZlibHeaderOrFooter: true);
        deflater.SetInput(block, offset, count);
        deflater.Finish();
        using var z = new MemoryStream();
        var compBuf = new byte[count];
        int n2;
        while ((n2 = deflater.Deflate(compBuf)) > 0)
            z.Write(compBuf, 0, n2);
        var comp = z.ToArray();
        if (comp.Length + 6 >= count)
            return null; // incompressible → raw fallback
        return WrapZlib(comp, comp.Length, block, offset, count);
    }

    private static byte[]? CompressBlock(byte[] block, int count) => CompressBlock(block, 0, count);

    private static byte[] WrapZlib(byte[] deflate, int deflateLen, byte[] block, int offset, int count)
    {
        var result = new byte[deflateLen + 6];
        result[0] = 0x48; result[1] = 0x89;
        Buffer.BlockCopy(deflate, 0, result, 2, deflateLen);
        uint adler = Adler32(block.AsSpan(offset, count));
        result[deflateLen + 2] = (byte)(adler >> 24);
        result[deflateLen + 3] = (byte)(adler >> 16);
        result[deflateLen + 4] = (byte)(adler >> 8);
        result[deflateLen + 5] = (byte)adler;
        return result;
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
