using System.Buffers.Binary;
using System.Security.Cryptography;
using OrbisPkgTool.Crypto;

// S4Trace — exact replication of shadPS4Plus PKG::Extract() allocation and
// bounds path (pkg.cpp), logging every allocation request and every PFSC
// sector consumed by the metadata scan. Purpose: find which operation can
// produce the observed std::bad_alloc on rebuilt FPKGs.
//
// Usage: s4trace <pkg>
namespace OrbisPkgTool;

static class S4Trace
{
    public static int Run(string pkgPath)
    {
        Console.WriteLine($"=== S4TRACE: {Path.GetFileName(pkgPath)} ===");
        using var fs = File.OpenRead(pkgPath);
        long fileLen = fs.Length;

        byte[] hdr = new byte[0x1100];
        fs.ReadExactly(hdr);
        uint entryCount  = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0x10));
        uint tableOffset = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0x18));
        ulong pfsImageOff = BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(0x410));
        ulong pfsImageSize = BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(0x418));
        uint pfsCacheSz  = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0x43C));
        uint length      = pfsCacheSz * 2;
        Console.WriteLine($"  pfs_cache_size     = 0x{pfsCacheSz:X} ({pfsCacheSz})");
        Console.WriteLine($"  length = cache*2   = 0x{length:X} ({length})");
        Console.WriteLine($"  pfs_image_offset   = 0x{pfsImageOff:X}");
        Console.WriteLine($"  pfs_image_size     = 0x{pfsImageSize:X} ({pfsImageSize})");
        Console.WriteLine($"  file_size          = 0x{fileLen:X} ({fileLen})");

        // ---- Entry table ----
        var entries = new List<(uint Id, uint F1, uint F2, uint Offset, uint Size)>();
        fs.Position = tableOffset;
        for (int i = 0; i < entryCount; i++)
        {
            byte[] e = new byte[32]; fs.ReadExactly(e);
            entries.Add((
                BinaryPrimitives.ReadUInt32BigEndian(e),
                BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(8)),
                BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(12)),
                BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(16)),
                BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(20))));
        }

        // ---- 6. NP over-read confirmation ----
        Console.WriteLine("-- NP entry over-read (shadPS4 pkg.cpp 223-252) --");
        foreach (var id in new uint[] { 0x400, 0x401, 0x402, 0x403 })
        {
            var e = entries.First(x => x.Id == id);
            int rsize = (int)e.Size;
            int msize = rsize;
            if (msize % 16 != 0) msize = msize - msize % 16 + 16;
            // physical bytes available at entry offset: next entry's offset - this offset
            uint nextOff = entries.Where(x => x.Offset > e.Offset).Select(x => x.Offset).DefaultIfEmpty((uint)fileLen).Min();
            long physical = nextOff - e.Offset;
            Console.WriteLine($"  entry 0x{id:X4}: entry.size={rsize} msize={msize} offset=0x{e.Offset:X} " +
                              $"physical_bytes_available={physical} OVERREAD={(msize > physical ? msize - physical : 0)} bytes");
            if (msize > physical)
            {
                // the 12 bytes past the read region (actual PKG bytes)
                fs.Position = e.Offset + rsize;
                byte[] past = new byte[Math.Min(16, (int)(fileLen - (e.Offset + rsize)))];
                fs.ReadExactly(past);
                Console.WriteLine($"    bytes at entry+size.. : {Convert.ToHexString(past)}");
            }
        }

        // ---- 1. Post-Sc0 allocation sequence ----
        Console.WriteLine("-- allocations (exact sequence) --");
        Console.WriteLine($"  pfsc:            alloc {length} bytes");
        Console.WriteLine($"  pfs_encrypted:   alloc {length} bytes");
        Console.WriteLine($"  pfs_decrypted:   alloc {length} bytes");

        // ---- Crypto chain (same as s4crypto) ----
        var ek = entries.First(x => x.Id == 0x10);
        byte[] ekData = new byte[ek.Size];
        fs.Position = ek.Offset; fs.ReadExactly(ekData);
        byte[] key1_3 = ekData.AsSpan(32 + 7 * 32 + 3 * 256, 256).ToArray();
        var dk3 = PkgCrypto.TryRsaDecrypt(key1_3, PkgKeySet.Standard.DerivedKey3)!;

        var imgE = entries.First(x => x.Id == 0x20);
        byte[] entryStruct = new byte[32];
        BinaryPrimitives.WriteUInt32BigEndian(entryStruct.AsSpan(0), imgE.Id);
        BinaryPrimitives.WriteUInt32BigEndian(entryStruct.AsSpan(8), imgE.F1);
        BinaryPrimitives.WriteUInt32BigEndian(entryStruct.AsSpan(12), imgE.F2);
        BinaryPrimitives.WriteUInt32BigEndian(entryStruct.AsSpan(16), imgE.Offset);
        BinaryPrimitives.WriteUInt32BigEndian(entryStruct.AsSpan(20), imgE.Size);
        byte[] ivKey = SHA256.HashData(entryStruct.Concat(dk3).ToArray());
        byte[] imgData = new byte[imgE.Size];
        fs.Position = imgE.Offset; fs.ReadExactly(imgData);
        byte[] imgKey;
        using (var aes = Aes.Create())
        {
            aes.Key = ivKey[16..32]; aes.IV = ivKey[0..16]; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.None;
            imgKey = aes.CreateDecryptor().TransformFinalBlock(imgData, 0, imgData.Length);
        }
        var ekpfs = PkgCrypto.TryRsaDecrypt(imgKey, PkgKeySet.Standard.FakeKeyset)!;
        fs.Position = (long)pfsImageOff + 0x370;
        byte[] seed = new byte[16]; fs.ReadExactly(seed);
        byte[] hmac = PkgCrypto.HmacSha256(ekpfs, new byte[] { 1, 0, 0, 0 }.Concat(seed).ToArray());
        byte[] tweakKey = hmac[0..16], dataKey = hmac[16..32];

        // ---- Decrypt initial `length` bytes ----
        byte[] enc = new byte[length];
        fs.Position = (long)pfsImageOff; fs.ReadExactly(enc);
        byte[] decrypted = XtsDecrypt(enc, dataKey, tweakKey);

        // ---- GetPFSCOffset ----
        uint pfscMagic = 0x43534650;
        long pfscOff = -1;
        for (long i = 0x20000; i + 4 <= decrypted.Length; i += 0x10000)
            if (BinaryPrimitives.ReadUInt32LittleEndian(decrypted.AsSpan((int)i, 4)) == pfscMagic) { pfscOff = i; break; }
        Console.WriteLine($"  PFSC offset       = 0x{(pfscOff < 0 ? "NOT FOUND" : pfscOff.ToString("X"))}");
        if (pfscOff < 0) return 1;
        long availablePFSC = length - pfscOff;
        Console.WriteLine($"  available PFSC    = 0x{availablePFSC:X} ({availablePFSC})  [length - pfsc_offset]");

        // ---- PFSC header ----
        long blockSz2   = BinaryPrimitives.ReadInt64LittleEndian(decrypted.AsSpan((int)pfscOff + 0x10, 8));
        long blockTable = BinaryPrimitives.ReadInt64LittleEndian(decrypted.AsSpan((int)pfscOff + 0x18, 8));
        long dataLength = BinaryPrimitives.ReadInt64LittleEndian(decrypted.AsSpan((int)pfscOff + 0x28, 8));
        int numBlocks = (int)(dataLength / blockSz2);
        Console.WriteLine($"  PFSC data_length  = {dataLength}");
        Console.WriteLine($"  PFSC block_sz2    = {blockSz2}");
        Console.WriteLine($"  num_blocks        = {numBlocks}");
        Console.WriteLine($"  block_table@      = 0x{blockTable:X}");

        // ---- sectorMap ----
        var sectorMap = new ulong[numBlocks + 1];
        for (int i = 0; i <= numBlocks; i++)
            sectorMap[i] = BinaryPrimitives.ReadUInt64LittleEndian(decrypted.AsSpan((int)(pfscOff + blockTable + i * 8), 8));
        Console.WriteLine($"  sectorMap entries = {numBlocks + 1}");
        Console.WriteLine($"  sectorMap bytes   = {(numBlocks + 1) * 8}");
        long last = (long)sectorMap[numBlocks];
        Console.WriteLine($"  sectorMap[{numBlocks}] = 0x{last:X}  (pfsc file size should be ~this)");

        // ---- 4. underflow check: sectorMap[i+1] >= sectorMap[i] ----
        int underflows = 0;
        for (int i = 0; i < numBlocks; i++)
            if (sectorMap[i + 1] < sectorMap[i]) { underflows++; if (underflows < 5) Console.WriteLine($"  UNDERFLOW at {i}: [{i+1}]=0x{sectorMap[i+1]:X} < [{i}]=0x{sectorMap[i]:X}"); }
        Console.WriteLine($"  sector underflows = {underflows}");

        // ---- 2. Metadata scan with cache-window check ----
        Console.WriteLine("-- metadata scan (exact shadPS4 state machine) --");
        var iNodeBuf = new List<(ushort Mode, uint Blocks, uint Loc, long Size)>();
        var fsTable = new List<(string Name, int Inode, int Type)>();
        uint ndinode = 0;
        int ndinodeCounter = 0;
        bool dinodeReached = false, urootReached = false, endReached = false;
        uint entSize = 0;   // ONE shared variable, exactly like shadPS4
        int firstCacheOob = -1, cacheOobCount = 0;
        int firstBadAlloc = -1;

        for (int i = 0; i < numBlocks && !endReached; i++)
        {
            ulong sOff = sectorMap[i];
            ulong sSize = sectorMap[i + 1] - sectorMap[i];

            // THE critical cache-window assertion: sectorMap[i+1] <= availablePFSC
            bool inCache = (long)sectorMap[i + 1] <= availablePFSC;
            if (!inCache && firstCacheOob < 0) firstCacheOob = i;
            if (!inCache) cacheOobCount++;

            // decompress or copy block (shadPS4 DecompressPFSC; we use .NET deflate on raw)
            byte[] block = new byte[0x10000];
            bool decompressed = false;
            if (sSize == 0x10000)
            {
                Array.Copy(decrypted, (int)(pfscOff + (long)sOff), block, 0, 0x10000);
                decompressed = true;
            }
            else if (sSize < 0x10000)
            {
                // data must be within the decrypted buffer (cache window) for a valid read
                long readStart = pfscOff + (long)sOff;
                long readEnd = pfscOff + (long)sectorMap[i + 1];
                if (readEnd <= decrypted.Length)
                {
                    try
                    {
                        byte[] comp = new byte[sSize];
                        Array.Copy(decrypted, (int)readStart, comp, 0, (int)sSize);
                        using var ms = new MemoryStream(comp, 2, comp.Length - 2);
                        using var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress);
                        var outBuf = new MemoryStream();
                        ds.CopyTo(outBuf);
                        var dec = outBuf.ToArray();
                        Array.Copy(dec, 0, block, 0, Math.Min(dec.Length, 0x10000));
                        decompressed = true;
                    }
                    catch { /* shadPS4 ignores inflate errors — block stays stale/zeros */ }
                }
                else
                {
                    // read would exceed the cache window -> shadPS4 reads garbage
                    if (i < 10) Console.WriteLine($"  blk {i}: READ BEYOND CACHE (sector 0x{sOff:X} end 0x{readEnd:X} > buf 0x{decrypted.Length:X})");
                }
            }

            // block 0: ndinode
            if (i == 0)
                ndinode = BitConverter.ToUInt32(block, 0x30);

            int occupied = (int)((ndinode * 0xA8) / 0x10000);
            if ((ndinode * 0xA8) % 0x10000 != 0) occupied++;

            // inodes from blocks 1..occupied
            if (i >= 1 && i <= occupied)
            {
                for (int p = 0; p + 0x68 <= 0x10000; p += 0xA8)
                {
                    ushort mode = BitConverter.ToUInt16(block, p);
                    if (mode == 0) break;
                    iNodeBuf.Add((mode, BitConverter.ToUInt32(block, p + 0x60), BitConverter.ToUInt32(block, p + 0x64), BitConverter.ToInt64(block, p + 8)));
                }
            }

            // uroot detection
            if (!urootReached && block.Length > 0x20)
            {
                string fpt = System.Text.Encoding.ASCII.GetString(block, 0x10, 15).TrimEnd('\0');
                if (fpt == "flat_path_table") urootReached = true;
            }
            if (urootReached)
            {
                for (int j = 0; j < 0x10000; j += (int)entSize)
                {
                    if (j + 16 > 0x10000) break;
                    int ino = BitConverter.ToInt32(block, j);
                    int es = BitConverter.ToInt32(block, j + 12);
                    if (es <= 0 || j + es > 0x10000) break;
                    entSize = (uint)es;
                    if (ino != 0) ndinodeCounter++;
                    else { urootReached = false; break; }
                }
            }

            // dinode detection
            if (!dinodeReached && block[0x10] == '.' && block[0x28] == '.' && block[0x29] == '.')
                dinodeReached = true;

            // dirent loop
            if (dinodeReached)
            {
                for (int j = 0; j < 0x10000; j += (int)entSize)
                {
                    if (j + 16 > 0x10000) break;
                    int ino = BitConverter.ToInt32(block, j);
                    if (ino == 0) break;
                    int nlen = BitConverter.ToInt32(block, j + 8);
                    int es = BitConverter.ToInt32(block, j + 12);
                    if (es <= 0 || j + es > 0x10000) break;
                    entSize = (uint)es;
                    // conceptual allocation: std::string(name, nlen)
                    if (nlen > 0x10000 || nlen < 0)
                    {
                        if (firstBadAlloc < 0) firstBadAlloc = i;
                        if (i < 30) Console.WriteLine($"  blk {i}: BAD namelen={nlen} -> std::string(ptr,{nlen}) would bad_alloc");
                        break;
                    }
                    string name = nlen > 0 ? System.Text.Encoding.ASCII.GetString(block, j + 16, Math.Min(nlen, es - 16)) : "";
                    int type = BitConverter.ToInt32(block, j + 4);
                    fsTable.Add((name, ino, type));
                    ndinodeCounter++;
                    if (ndinodeCounter + 1 == ndinode) { endReached = true; break; }
                }
            }

            // per-block progress on the first 30 blocks
            if (i < 30)
            {
                Console.WriteLine($"  blk {i,2}: sector=0x{sOff:X} size={sSize,6} cache_ok={inCache} " +
                                  $"entSize={entSize} count={ndinodeCounter} dinode={dinodeReached} uroot={urootReached} end={endReached}");
            }
        }

        Console.WriteLine($"  iNodeBuf          = {iNodeBuf.Count}");
        Console.WriteLine($"  fsTable           = {fsTable.Count} ({fsTable.Count(x => x.Type == 2)} files, {fsTable.Count(x => x.Type == 3)} dirs)");
        Console.WriteLine($"  first cache OOB   = {firstCacheOob}  count={cacheOobCount}");
        Console.WriteLine($"  first bad alloc   = {firstBadAlloc}");

        // ---- 5. fsTable inode bounds ----
        int oob = 0;
        foreach (var f in fsTable.Where(x => x.Type == 2))
            if (f.Inode >= iNodeBuf.Count) { oob++; if (oob < 5) Console.WriteLine($"  OOB file '{f.Name}' inode={f.Inode} >= {iNodeBuf.Count}"); }
        Console.WriteLine($"  OOB file inodes   = {oob}");

        // ---- 3. largest allocation request ----
        long maxAlloc = Math.Max(length, (numBlocks + 1) * 8L);
        Console.WriteLine($"  largest alloc     = {maxAlloc} bytes (sectorMap or cache buffers)");
        return 0;
    }

    static byte[] XtsDecrypt(byte[] data, byte[] dataKey, byte[] tweakKey)
    {
        var result = (byte[])data.Clone();
        using var aesT = Aes.Create();
        aesT.Key = tweakKey; aesT.Mode = CipherMode.ECB; aesT.Padding = PaddingMode.None;
        using var aesD = Aes.Create();
        aesD.Key = dataKey; aesD.Mode = CipherMode.ECB; aesD.Padding = PaddingMode.None;
        for (int sectorStart = 0; sectorStart + 0x1000 <= data.Length; sectorStart += 0x1000)
        {
            ulong sector = (ulong)(sectorStart / 0x1000);
            byte[] tweak = new byte[16];
            BinaryPrimitives.WriteUInt64LittleEndian(tweak, sector);
            byte[] encTweak = aesT.CreateEncryptor().TransformFinalBlock(tweak, 0, 16);
            for (int off = sectorStart; off < sectorStart + 0x1000; off += 16)
            {
                byte[] block = data.AsSpan(off, 16).ToArray();
                for (int b = 0; b < 16; b++) block[b] ^= encTweak[b];
                block = aesD.CreateDecryptor().TransformFinalBlock(block, 0, 16);
                for (int b = 0; b < 16; b++) result[off + b] = (byte)(block[b] ^ encTweak[b]);
                XtsMult(encTweak);
            }
        }
        return result;
    }

    static void XtsMult(byte[] tweak)
    {
        byte carry = 0;
        for (int i = 0; i < 16; i++)
        {
            byte nextCarry = (byte)(tweak[i] >> 7);
            tweak[i] = (byte)((tweak[i] << 1) | carry);
            carry = nextCarry;
        }
        if (carry != 0) tweak[0] ^= 0x87;
    }
}
