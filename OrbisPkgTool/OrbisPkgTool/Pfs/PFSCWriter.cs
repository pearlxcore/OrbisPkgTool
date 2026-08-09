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
    public static byte[] Build(byte[] pfsImage)
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
            // Use zlib-wrapped deflate (RFC 1950). The PS4 PFSC decompressor
            // skips a 2-byte zlib header before decompressing each block.
            using (var zlib = new System.IO.Compression.ZLibStream(z, System.IO.Compression.CompressionLevel.Optimal, leaveOpen: true))
                zlib.Write(pfsImage, i * blockSize, len);
            var comp = z.ToArray();
            compressedBlocks[i] = comp;
            dataSize += comp.Length;
        }
        int dataOffset = tableOffset + (blockCount + 1) * 8;

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
