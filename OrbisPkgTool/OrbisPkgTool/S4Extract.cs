using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using OrbisPkgTool.Crypto;

// S4Extract — exact replica of shadPS4Plus PKG::Extract() + ExtractFiles()
// (extractor 1.1 source, pkg.cpp) WITHOUT the safety guards my earlier sims
// added. The real code:
//   * shares ONE ent_size across blocks AND across the uroot/dinode loops
//   * does std::string(dirent.name, dirent.namelen) with UNCHECKED namelen
//     -> garbage namelen = bad_alloc (the observed crash)
//   * once dinode_reached, scans EVERY subsequent block incl. file data
//   * ends only when (ndinode_counter + 1) == ndinode EXACTLY
//   * ExtractFiles: iNodeBuf[inode] / sectorMap[loc+j] unchecked -> OOB
//
// Purpose: run on the WORKING orbis build and our CRASHING build from the
// same Sony GP4 — the first divergence tells us exactly what crashes.
//
// Usage: s4extract <pkg> [--out <dir>]   (--out extracts files like shadPS4)
namespace OrbisPkgTool;

static class S4Extract
{
    const int InodeSize = 0x68;    // shadPS4 reads 0x68 bytes of the D32 inode
    const int InodeStride = 0xA8;  // D32 inode stride

    // ---- real zlib (same as shadPS4's DecompressPFSC: inflateInit, zlib stream) ----
    const string ZlibPath = @"C:/Program Files/Git/mingw64/bin/zlib1.dll";
    [DllImport(ZlibPath)] static extern int inflateInit_(ref ZStream strm, string version, int stream_size);
    [DllImport(ZlibPath)] static extern int inflate(ref ZStream strm, int flush);
    [DllImport(ZlibPath)] static extern int inflateEnd(ref ZStream strm);
    [StructLayout(LayoutKind.Sequential)]
    struct ZStream
    {
        public IntPtr next_in; public uint avail_in; public uint total_in;
        public IntPtr next_out; public uint avail_out; public uint total_out;
        public IntPtr msg; public IntPtr state;
        public IntPtr zalloc; public IntPtr zfree; public IntPtr opaque;
        public int data_type; public uint adler; public uint reserved;
    }
    static bool _zlibOk = File.Exists(ZlibPath);

    // Decompresses a full PFSC block the shadPS4 way: zlib inflate on the
    // complete stream (incl. the 2-byte header), 0x10000 output cap.
    // Returns false if inflate fails (shadPS4 ignores the error -> stale buffer).
    // `rc` receives the zlib return code for diagnostics (0 = Z_OK, 1 = Z_STREAM_END,
    // negative = error).
    static bool ZInflate(byte[] comp, byte[] decomp, out int rc)
    {
        rc = -99;
        Array.Clear(decomp, 0, decomp.Length); // deterministic: partial output visible as partial
        if (!_zlibOk)
        {
            // Fallback: .NET raw deflate, skip 2-byte zlib header
            try
            {
                using var ms = new MemoryStream(comp, 2, comp.Length - 2);
                using var ds = new System.IO.Compression.DeflateStream(ms, System.IO.Compression.CompressionMode.Decompress);
                var outBuf = new MemoryStream();
                ds.CopyTo(outBuf);
                var dec = outBuf.ToArray();
                Array.Copy(dec, 0, decomp, 0, Math.Min(dec.Length, decomp.Length));
                rc = 1;
                return dec.Length > 0;
            }
            catch { return false; }
        }
        var z = new ZStream();
        // inflateInit_ requires the exact ZLIB_VERSION string of the DLL
        bool initOk = false;
        foreach (var ver in new[] { "1.2.13", "1.2.12", "1.2.11", "1.3", "1.3.1", "1.2.8" })
            if (inflateInit_(ref z, ver, Marshal.SizeOf<ZStream>()) == 0) { initOk = true; break; }
        if (!initOk) { rc = -98; return false; }
        var inPin = GCHandle.Alloc(comp, GCHandleType.Pinned);
        var outPin = GCHandle.Alloc(decomp, GCHandleType.Pinned);
        try
        {
            z.next_in = inPin.AddrOfPinnedObject(); z.avail_in = (uint)comp.Length;
            z.next_out = outPin.AddrOfPinnedObject(); z.avail_out = (uint)decomp.Length;
            // inflate with Z_FINISH until Z_STREAM_END or a terminal error.
            // (Z_OK means "made progress, call again".)
            rc = 0;
            while (rc == 0)
                rc = inflate(ref z, 4 /* Z_FINISH */);
            return rc == 1 /* Z_STREAM_END */;
        }
        finally
        {
            outPin.Free(); inPin.Free();
            inflateEnd(ref z);
        }
    }

    public static int Run(string pkgPath, string? outDir, int debugBlock = -1)
    {
        if (debugBlock >= 0)
            return DebugBlock(pkgPath, debugBlock);

        Console.WriteLine($"=== S4EXTRACT: {Path.GetFileName(pkgPath)} ===");
        using var fs = File.OpenRead(pkgPath);
        long fileLen = fs.Length;

        byte[] hdr = new byte[0x1100];
        fs.ReadExactly(hdr);
        uint entryCount  = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0x10));
        uint tableOffset = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0x18));
        ulong pfsImageOff = BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(0x410));
        uint pfsCacheSz  = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0x43C));
        uint length      = pfsCacheSz * 2;
        Console.WriteLine($"  pfs_cache_size=0x{pfsCacheSz:X} length=0x{length:X} pfs_image_offset=0x{pfsImageOff:X}");

        // ---- entries ----
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

        // ---- keys (same chain as s4crypto) ----
        byte[] ekData = new byte[entries.First(x => x.Id == 0x10).Size];
        fs.Position = entries.First(x => x.Id == 0x10).Offset; fs.ReadExactly(ekData);
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

        // ---- decrypt cache window ----
        // shadPS4: file.Read(pfs_encrypted) — a short read leaves the rest
        // zero-filled (vector initialized to length). Mirror that.
        byte[] enc = new byte[length];
        fs.Position = (long)pfsImageOff;
        int got = fs.Read(enc, 0, enc.Length);
        for (int r = got; r < enc.Length; r++) enc[r] = 0;
        byte[] decrypted = XtsDecrypt(enc, dataKey, tweakKey);

        // ---- GetPFSCOffset (scan 0x20000 in 0x10000 steps) ----
        long pfscOff = -1;
        for (long i = 0x20000; i + 4 <= decrypted.Length; i += 0x10000)
            if (BinaryPrimitives.ReadUInt32LittleEndian(decrypted.AsSpan((int)i, 4)) == 0x43534650) { pfscOff = i; break; }
        Console.WriteLine($"  PFSC offset = 0x{(pfscOff < 0 ? "NOT FOUND" : pfscOff.ToString("X"))}");
        if (pfscOff < 0) return 1;
        long availablePFSC = length - pfscOff;
        Console.WriteLine($"  available PFSC = 0x{availablePFSC:X}");

        long blockSz2   = BinaryPrimitives.ReadInt64LittleEndian(decrypted.AsSpan((int)pfscOff + 0x10, 8));
        long blockTable = BinaryPrimitives.ReadInt64LittleEndian(decrypted.AsSpan((int)pfscOff + 0x18, 8));
        long dataLength = BinaryPrimitives.ReadInt64LittleEndian(decrypted.AsSpan((int)pfscOff + 0x28, 8));
        int numBlocks = (int)(dataLength / blockSz2);
        Console.WriteLine($"  data_length={dataLength} num_blocks={numBlocks}");

        var sectorMap = new ulong[numBlocks + 1];
        for (int i = 0; i <= numBlocks; i++)
            sectorMap[i] = BinaryPrimitives.ReadUInt64LittleEndian(decrypted.AsSpan((int)(pfscOff + blockTable + i * 8), 8));

        // pfsc buffer = the cache window only (shadPS4: pfsc(length); memcpy length-pfsc_offset)
        // NOTE: reads of pfsc.data()+sectorOffset for sectorOffset >= availablePFSC are OOB in shadPS4 too
        var pfsc = decrypted.AsSpan((int)pfscOff, (int)availablePFSC);

        // ---- Extract() metadata scan: EXACT state machine, no guards ----
        var iNodeBuf = new List<Inode>();
        var fsTable = new List<(string Name, int Inode, int Type)>();
        var extractPaths = new Dictionary<int, string>();
        uint ndinode = 0;
        int ndinodeCounter = 0;
        bool dinodeReached = false, urootReached = false, endReached = false;
        uint entSize = 0;                       // ONE shared variable
        byte[] decompressedData = new byte[0x10000];
        int scanFileDataStart = -1;             // first block where the scan runs on non-dirent content
        int badNameLenAt = -1;                  // first garbage namelen (bad_alloc in real code)
        string? badNameLenName = null;
        int firstInodeOob = -1;                 // dirent referencing inode beyond iNodeBuf
        int counterOverrun = 0;
        int totalType23 = 0;

        for (int i = 0; i < numBlocks && !endReached; i++)
        {
            ulong sOff = sectorMap[i];
            ulong sSize = sectorMap[i + 1] - sectorMap[i];

            // shadPS4: memcpy(compressedData.data(), pfsc.data() + sectorOffset, sectorSize)
            // pfsc buffer is only availablePFSC bytes -> OOB read when beyond
            bool readOob = (long)(sOff + sSize) > availablePFSC;
            if (readOob && i < 40)
                Console.WriteLine($"  blk {i}: WARN pfsc read at 0x{sOff:X}+{sSize} beyond cache window 0x{availablePFSC:X}");

            // shadPS4 reuses ONE decompressedData(0x10000) buffer; failed
            // inflate or OOB read leaves it STALE (previous block content).
            if (sSize == 0x10000)
            {
                if ((long)sOff + 0x10000 <= availablePFSC)
                    pfsc.Slice((int)sOff, 0x10000).CopyTo(decompressedData);
                else
                    Console.WriteLine($"  blk {i}: RAW block 0x{sOff:X} beyond cache — shadPS4 memcpy reads OOB (garbage)");
            }
            else if (sSize < 0x10000)
            {
                if ((long)(sOff + sSize) <= availablePFSC && sSize >= 2)
                {
                    byte[] comp = pfsc.Slice((int)sOff, (int)sSize).ToArray();
                    // REAL zlib inflate (identical to shadPS4 DecompressPFSC).
                    // On failure decompressedData stays STALE, exactly like shadPS4.
                    ZInflate(comp, decompressedData, out int rc);
                    if (i < 25 && rc != 1)
                        Console.WriteLine($"  blk {i}: zlib rc={rc} (not STREAM_END) size={sSize}");
                    if (i == 17)
                    {
                        Console.WriteLine($"  blk 17 first64 after inflate rc={rc}: {Convert.ToHexString(decompressedData, 0, 64)}");
                        Console.WriteLine($"  blk 17 comp len={comp.Length} first16={Convert.ToHexString(comp, 0, Math.Min(16, comp.Length))}");
                    }
                }
                // else: reads OOB in shadPS4 -> decompressedData stays stale
            }
            else if (i < 40)
                Console.WriteLine($"  blk {i}: UNUSUAL sectorSize={sSize} (>0x10000) — shadPS4 skips decompress, block stale");
            var block = decompressedData;

            if (i == 0)
                ndinode = BitConverter.ToUInt32(block, 0x30);

            int occupied = (int)((ndinode * 0xA8) / 0x10000);
            if ((ndinode * 0xA8) % 0x10000 != 0) occupied++;

            if (i >= 1 && i <= occupied)
            {
                for (int p = 0; p + InodeSize <= 0x10000; p += InodeStride)
                {
                    ushort mode = BitConverter.ToUInt16(block, p);
                    if (mode == 0) break;
                    iNodeBuf.Add(new Inode(
                        mode,
                        BitConverter.ToUInt32(block, p + 0x60),
                        BitConverter.ToUInt32(block, p + 0x64),
                        BitConverter.ToInt64(block, p + 8)));
                }
            }

            // uroot detection — re-checked EVERY block (real code has no guard)
            if (block[0x10] == 'f' && block[0x11] == 'l' && block[0x12] == 'a' && block[0x13] == 't')
            {
                string fpt = System.Text.Encoding.ASCII.GetString(block, 0x10, 15);
                if (fpt == "flat_path_table") urootReached = true;
            }

            // uroot loop — EXACT order: ent_size = entsize FIRST, then ino check.
            // Terminator dirent is all-zeros (ino==0) -> clean exit.
            // Only ino!=0 with entsize==0 is an infinite loop in real code.
            if (urootReached)
            {
                for (int j = 0; j < 0x10000; j += (int)entSize)
                {
                    if (j + 16 > 0x10000) { if (i < 40) Console.WriteLine($"  blk {i}: uroot loop reads past block end (j=0x{j:X})"); break; }
                    int ino = BitConverter.ToInt32(block, j);
                    int es = BitConverter.ToInt32(block, j + 12);
                    entSize = (uint)es;
                    if (ino != 0)
                    {
                        if (es == 0)
                        {
                            if (i < 40) Console.WriteLine($"  blk {i}: uroot loop ino!=0 entsize=0 at j=0x{j:X} -> INFINITE LOOP in shadPS4");
                            break;
                        }
                        ndinodeCounter++;
                    }
                    else
                    {
                        extractPaths[ndinodeCounter] = "EXTRACT_PATH"; // extract_path/parent-title-id
                        urootReached = false;
                        break;
                    }
                }
            }

            // dinode detection
            if (!dinodeReached && block[0x10] == '.' && block[0x28] == '.' && block[0x29] == '.')
                dinodeReached = true;

            // dirent loop — EXACT: namelen UNCHECKED (bad_alloc), ent_size updated BEFORE next step
            if (dinodeReached)
            {
                for (int j = 0; j < 0x10000; j += (int)entSize)
                {
                    if (j + 16 > 0x10000) break;
                    int ino = BitConverter.ToInt32(block, j);
                    if (ino == 0) break;
                    int nlen = BitConverter.ToInt32(block, j + 8);
                    int es = BitConverter.ToInt32(block, j + 12);
                    entSize = (uint)es;
                    if (es <= 0 || j + es > 0x10000)
                    {
                        if (i < 40 || badNameLenAt < 0)
                            Console.WriteLine($"  blk {i}: BAD entsize={es} at j=0x{j:X} -> shadPS4 reads garbage from here");
                        break;
                    }
                    if (nlen < 0 || nlen > 0x1000)
                    {
                        if (badNameLenAt < 0)
                        {
                            badNameLenAt = i;
                            badNameLenName = $"(ino={ino} type={BitConverter.ToInt32(block, j + 4)} namelen={nlen} j=0x{j:X})";
                            Console.WriteLine($"  >>> CRASH: std::string(name, {nlen}) at blk {i} j=0x{j:X} ino={ino} type={BitConverter.ToInt32(block, j + 4)} -> bad_alloc");
                        }
                        break;
                    }
                    string name = (nlen > 0 && j + 16 < 0x10000 && es > 16)
                        ? System.Text.Encoding.ASCII.GetString(block, j + 16, Math.Min(nlen, Math.Min(es - 16, 0x10000 - j - 16)))
                        : "";
                    int type = BitConverter.ToInt32(block, j + 4);
                    fsTable.Add((name, ino, type));

                    // PFS_CURRENT_DIR -> current_dir = extractPaths[ino] (missing key = EMPTY path)
                    if (type == 4)
                    {
                        if (!extractPaths.ContainsKey(ino))
                            extractPaths[ino] = "";
                        currentDir = extractPaths[ino];
                    }
                    if (type == 2 || type == 3)
                    {
                        extractPaths[ino] = currentDir.Length > 0 ? currentDir + "/" + name : name;
                        if (type == 3 && outDir != null)
                            Directory.CreateDirectory(Path.Combine(outDir, extractPaths[ino].Replace('/', Path.DirectorySeparatorChar)));
                        totalType23++;
                        ndinodeCounter++;
                        if (ndinodeCounter + 1 == ndinode) { endReached = true; break; }
                        if (ndinodeCounter + 1 > ndinode) counterOverrun++;
                    }
                    else if (type == 5)
                    {
                        // '..' — real code does NOT touch extractPaths for it (current_dir only on type 4)
                    }
                }
            }

            if (i < 30)
                Console.WriteLine($"  blk {i,2}: sector=0x{sOff:X} size={sSize,6} count={ndinodeCounter} dinode={dinodeReached} uroot={urootReached} end={endReached}");

            // DEBUG: dump every dirent the scan sees in blocks 14..30
            if (i >= 14 && i <= 30 && dinodeReached)
            {
                int dj = 0;
                while (dj + 16 <= 0x10000)
                {
                    int dino = BitConverter.ToInt32(block, dj);
                    if (dino == 0) break;
                    int dtype = BitConverter.ToInt32(block, dj + 4);
                    int dnlen = BitConverter.ToInt32(block, dj + 8);
                    int des = BitConverter.ToInt32(block, dj + 12);
                    string dname = (dnlen > 0 && dnlen < 200) ? System.Text.Encoding.ASCII.GetString(block, dj + 16, dnlen) : $"(nlen={dnlen})";
                    Console.WriteLine($"    blk {i} dirent j=0x{dj:X4} ino={dino} type={dtype} es={des} name={dname}");
                    if (des <= 0 || dj + des > 0x10000) break;
                    dj += des;
                }
            }
        }

        Console.WriteLine($"  ndinode={ndinode} iNodeBuf={iNodeBuf.Count} fsTable={fsTable.Count} " +
                          $"({fsTable.Count(x => x.Type == 2)} files, {fsTable.Count(x => x.Type == 3)} dirs)");
        Console.WriteLine($"  ndinodeCounter={ndinodeCounter} end_reached={endReached} counter_overrun={counterOverrun}");
        Console.WriteLine($"  first bad namelen (bad_alloc): {(badNameLenAt >= 0 ? $"blk {badNameLenAt} {badNameLenName}" : "none")}");

        // ---- inodes declared but never referenced by any type-2/3 dirent ----
        // (these are the ones shadPS4's counter never reaches -> overrun)
        var referenced = new HashSet<int>();
        foreach (var f in fsTable)
            if (f.Type == 2 || f.Type == 3) referenced.Add(f.Inode);
        var missing = new List<int>();
        for (int m = 1; m < ndinode; m++)
            if (!referenced.Contains(m)) missing.Add(m);
        Console.WriteLine($"  unreferenced inodes (1..{ndinode - 1}): {missing.Count}");
        if (missing.Count > 0 && missing.Count <= 60)
            Console.WriteLine($"    {string.Join(", ", missing)}");
        // inode<->path mapping: our writer numbers 0=superroot 1=fpt 2=uroot,
        // 3.. = dirs sorted by FullPath, then files sorted by FullPath.
        // Rebuild from the fsTable entries themselves (dirs from type-3 blocks):
        var inodeNames = new Dictionary<int, string>();
        foreach (var f in fsTable)
            if (!inodeNames.ContainsKey(f.Inode))
                inodeNames[f.Inode] = $"{f.Name} (t{f.Type})";
        foreach (var m in missing.Take(40))
            inodeNames.TryGetValue(m, out var nm);
        if (missing.Count <= 60)
            foreach (var m in missing)
                Console.WriteLine($"    unreferenced ino {m}: {(inodeNames.TryGetValue(m, out var n2) ? n2 : "no fsTable name")}");

        // ---- ExtractFiles replica: per-file crash checks ----
        Console.WriteLine("-- ExtractFiles checks --");
        int crashCount = 0, checkedFiles = 0;
        int firstCrashIdx = -1;
        string? firstCrashReason = null;
        var inodeOobEntries = new List<string>();

        for (int idx = 0; idx < fsTable.Count; idx++)
        {
            var (name, ino, type) = fsTable[idx];
            if (type != 2) continue; // PFS_FILE only
            checkedFiles++;

            string? reason = null;
            if (ino < 0 || ino >= iNodeBuf.Count)
                reason = $"iNodeBuf[{ino}] OOB (size {iNodeBuf.Count})";
            else
            {
                if (!extractPaths.ContainsKey(ino))
                    reason = $"extractPaths[{ino}] MISSING -> fopen(\"\") -> fwrite(NULL) segfault";
                else
                {
                    var path = extractPaths[ino];
                    if (path.Length == 0)
                        reason = $"extractPaths[{ino}] EMPTY -> fopen(\"\") -> fwrite(NULL) segfault";
                    else
                    {
                        var node = iNodeBuf[ino];
                        int loc = (int)node.Loc, nblocks = (int)node.Blocks;
                        long bsize = node.Size;
                        if (nblocks <= 0 || loc < 0)
                            reason = $"loc={loc} blocks={nblocks} negative/zero";
                        else if (loc + nblocks + 1 > sectorMap.Length)
                            reason = $"sectorMap[loc+j] OOB: loc={loc} blocks={nblocks} mapLen={sectorMap.Length}";
                        else
                        {
                            ulong first = sectorMap[loc];
                            ulong lastEnd = sectorMap[loc + nblocks];
                            if (lastEnd < first)
                                reason = $"sectorMap not monotonic at {loc}";
                            else
                            {
                                // ExtractFiles reads from the PKG FILE directly:
                                // fileOffset = pfs_image_offset + pfsc_offset + sectorOffset.
                                // Bounds: 0 <= fileOffset - previousData, read 0x11000 bytes.
                                long fileOff = (long)pfsImageOff + pfscOff + (long)first;
                                long readEnd = fileOff - ((fileOff + pfscOff) & 0xFFF) + 0x11000;
                                if (fileOff < 0 || readEnd > fileLen)
                                    reason = $"read [0x{fileOff:X}..0x{readEnd:X}] beyond PKG size 0x{fileLen:X}";
                            }
                        }
                    }
                }
            }

            if (reason != null)
            {
                crashCount++;
                if (firstCrashIdx < 0)
                {
                    firstCrashIdx = idx;
                    firstCrashReason = $"FILE[{idx}] '{name}' ino={ino} type={type}: {reason}";
                    Console.WriteLine($"  >>> CRASH: {firstCrashReason}");
                }
                if (reason.StartsWith("iNodeBuf"))
                    inodeOobEntries.Add($"{name}(ino={ino})");
            }
        }
        Console.WriteLine($"  files checked={checkedFiles} crash candidates={crashCount}");
        if (firstCrashIdx >= 0)
            Console.WriteLine($"  FIRST CRASH: {firstCrashReason}");
        if (inodeOobEntries.Count > 0)
            Console.WriteLine($"  inode-OOB entries ({inodeOobEntries.Count}): {string.Join(", ", inodeOobEntries.Take(20))}");
        Console.WriteLine(crashCount == 0 ? "  RESULT: no crash candidates" : $"  RESULT: {crashCount} crash candidates");
        return 0;
    }

    // Debug: decompress ONE PFSC block with real zlib and hexdump it.
    static int DebugBlock(string pkgPath, int blockIdx)
    {
        using var fs = File.OpenRead(pkgPath);
        byte[] hdr = new byte[0x1100]; fs.ReadExactly(hdr);
        ulong pfsImageOff = BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(0x410));
        uint pfsCacheSz = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0x43C));
        uint length = pfsCacheSz * 2;
        var entries = new List<(uint Id, uint F1, uint F2, uint Offset, uint Size)>();
        fs.Position = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0x18));
        for (int i = 0; i < BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0x10)); i++)
        {
            byte[] e = new byte[32]; fs.ReadExactly(e);
            entries.Add((BinaryPrimitives.ReadUInt32BigEndian(e), BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(8)),
                BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(12)), BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(16)),
                BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(20))));
        }
        byte[] ekData = new byte[entries.First(x => x.Id == 0x10).Size];
        fs.Position = entries.First(x => x.Id == 0x10).Offset; fs.ReadExactly(ekData);
        var dk3 = PkgCrypto.TryRsaDecrypt(ekData.AsSpan(32 + 7 * 32 + 3 * 256, 256).ToArray(), PkgKeySet.Standard.DerivedKey3)!;
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
        byte[] enc = new byte[length];
        fs.Position = (long)pfsImageOff; fs.ReadExactly(enc);
        byte[] decrypted = XtsDecrypt(enc, dataKey, tweakKey);
        long pfscOff = -1;
        for (long i = 0x20000; i + 4 <= decrypted.Length; i += 0x10000)
            if (BinaryPrimitives.ReadUInt32LittleEndian(decrypted.AsSpan((int)i, 4)) == 0x43534650) { pfscOff = i; break; }
        if (pfscOff < 0) { Console.WriteLine("PFSC not found"); return 1; }
        long blockTable = BinaryPrimitives.ReadInt64LittleEndian(decrypted.AsSpan((int)pfscOff + 0x18, 8));
        long dataLength = BinaryPrimitives.ReadInt64LittleEndian(decrypted.AsSpan((int)pfscOff + 0x28, 8));
        int numBlocks = (int)(dataLength / BinaryPrimitives.ReadInt64LittleEndian(decrypted.AsSpan((int)pfscOff + 0x10, 8)));
        if (blockIdx > numBlocks) { Console.WriteLine($"block {blockIdx} > num_blocks {numBlocks}"); return 1; }
        var sectorMap = new ulong[numBlocks + 1];
        for (int i = 0; i <= numBlocks; i++)
            sectorMap[i] = BinaryPrimitives.ReadUInt64LittleEndian(decrypted.AsSpan((int)(pfscOff + blockTable + i * 8), 8));
        ulong sOff = sectorMap[blockIdx], sSize = sectorMap[blockIdx + 1] - sOff;
        Console.WriteLine($"block {blockIdx}: sector=0x{sOff:X} size={sSize} (raw={sSize == 0x10000})");
        byte[] outBuf = new byte[0x10000];
        if (sSize == 0x10000)
        {
            Array.Copy(decrypted, (int)(pfscOff + (long)sOff), outBuf, 0, 0x10000);
        }
        else
        {
            byte[] comp = new byte[sSize];
            Array.Copy(decrypted, (int)(pfscOff + (long)sOff), comp, 0, (int)sSize);
            bool ok = ZInflate(comp, outBuf, out _);
            Console.WriteLine($"zlib inflate ok={ok}");
            Console.WriteLine($"  comp[0..16]: {Convert.ToHexString(comp, 0, Math.Min(16, comp.Length))}");
        }
        for (int row = 0; row < 8; row++)
        {
            int off = row * 16;
            string hex = Convert.ToHexString(outBuf, off, 16);
            var asc = new StringBuilder();
            for (int k = off; k < off + 16; k++) asc.Append(outBuf[k] >= 32 && outBuf[k] < 127 ? (char)outBuf[k] : '.');
            Console.WriteLine($"  {off:X4}  {hex}  {asc}");
        }
        return 0;
    }

    static string currentDir = "";

    readonly record struct Inode(ushort Mode, uint Blocks, uint Loc, long Size);

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
