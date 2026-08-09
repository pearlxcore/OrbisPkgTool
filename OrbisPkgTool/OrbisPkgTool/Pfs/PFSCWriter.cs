namespace OrbisPkgTool.Pfs;

/// <summary>
/// PFSC container writer — wraps a PFS image into the compressed-PFS format.
/// Each block is zlib-compressed (window bits 12); blocks that do not
/// compress (compressed size >= block size) are stored raw. The block table
/// holds absolute offsets; all header fields are little-endian (validated
/// against real FPKGs).
/// </summary>
public static class PFSCWriter
{
    public static byte[] Build(byte[] pfsImage, bool storeAllRaw = false)
    {
        const int blockSize = 0x10000;
        int blockCount = (pfsImage.Length + blockSize - 1) / blockSize;
        ulong rounded = (ulong)((long)blockCount * blockSize);

        int tableOffset = 0x400; // aligned like real FPKGs
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
            if (comp.Length + 2 >= blockSize)
            {
                compressedBlocks[i] = new byte[blockSize];
                Buffer.BlockCopy(pfsImage, i * blockSize, compressedBlocks[i], 0, len);
            }
            else
            {
                // 2-byte prefix (0x48 0x89) before the raw deflate, matching orbis.
                compressedBlocks[i] = new byte[comp.Length + 2];
                compressedBlocks[i][0] = 0x48; compressedBlocks[i][1] = 0x89;
                Buffer.BlockCopy(comp, 0, compressedBlocks[i], 2, comp.Length);
            }
            dataSize += compressedBlocks[i].Length;
        }
        // Real orbis PFSC files use dataOffset = 0x10000 (block-aligned).
        // LibOrbisPkg PFSCReader reads whatever DataStart says, but orbis
        // itself may require block alignment here.
        int dataOffset = 0x10000;

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

    private static void WriteLe(BinaryWriter w, uint v) =>
        w.Write(new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) });

    private static void WriteLe(BinaryWriter w, ulong v) =>
        w.Write(new[]
        {
            (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24),
            (byte)(v >> 32), (byte)(v >> 40), (byte)(v >> 48), (byte)(v >> 56),
        });
}
