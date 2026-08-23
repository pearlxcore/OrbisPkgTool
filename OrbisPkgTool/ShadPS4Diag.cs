using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using OrbisPkgTool.Pfs;
using OrbisPkgTool.Pkg;

// ShadPS4 PKG Reader Diagnostic — mirrors shadPS4's exact reading logic
// step by step, logging every operation.  Run against original and rebuilt
// PKGs to find exactly where the shadPS4 installer diverges.
//
// Usage: OrbisPkgTool.exe shadps4diag <pkg> [--passcode X]

namespace OrbisPkgTool;

static class ShadPS4Diag
{
    static int _seq;
    static StreamWriter? _log;

    static void Log(string msg)
    {
        int n = Interlocked.Increment(ref _seq);
        _log?.WriteLine($"[{n:D6}] {msg}");
        _log?.Flush();
        Console.WriteLine($"[{n:D6}] {msg}");
    }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct S4Inode { public ushort Mode, Nlink; public uint Flags; public long Size, SizeCompressed;
        public long T1s, T2s, T3s, T4s; public uint T1n, T2n, T3n, T4n; public uint Uid, Gid;
        public ulong Unk1, Unk2; public uint Blocks, Loc; }

    [StructLayout(LayoutKind.Sequential, Pack = 1)]
    struct S4Dirent { public int Ino, Type, Namelen, Entsize; }

    public static int Run(string pkgPath, string? dumpDir, string passcode)
    {
        var logPath = pkgPath + ".shadps4diag.log";
        using (_log = new StreamWriter(logPath, false, new UTF8Encoding(false)) { AutoFlush = true })
        {
            Log("=== ShadPS4 PKG Reader Diagnostic ===");
            Log($"PKG  : {pkgPath}");

            if (!File.Exists(pkgPath)) { Log("ERROR: PKG not found"); return 1; }

            try
            {
                using var fs = File.OpenRead(pkgPath);
                long pkgSize = fs.Length;
                Log($"PKG_SIZE {pkgSize} ({pkgSize/1e6:F1} MB)");

                // === PKG header (BE) ===
                byte[] hdr = new byte[0x1100]; fs.Position = 0; fs.ReadExactly(hdr);
                uint magic = BinaryPrimitives.ReadUInt32BigEndian(hdr);
                if (magic != 0x7F434E54) { Log($"ERROR: Bad magic 0x{magic:X8}"); return 1; }

                uint  entryCount     = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0x10));
                uint  tableOffset    = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0x18));
                ulong pfsImageOffset = BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(0x410));
                ulong pfsImageSize   = BinaryPrimitives.ReadUInt64BigEndian(hdr.AsSpan(0x418));
                uint  pfsCacheSize   = BinaryPrimitives.ReadUInt32BigEndian(hdr.AsSpan(0x43C));
                ulong shadLength     = (ulong)pfsCacheSize * 2;
                Log($"  entries={entryCount} tableOff=0x{tableOffset:X} pfsOff=0x{pfsImageOffset:X} pfsSize={pfsImageSize} cache*2=0x{shadLength:X}");

                // === Entry table ===
                Log("STEP: Entry table");
                fs.Position = tableOffset;
                for (int i = 0; i < entryCount; i++)
                {
                    byte[] eb = new byte[32]; fs.ReadExactly(eb);
                    uint id     = (uint)(eb[0]<<24|eb[1]<<16|eb[2]<<8|eb[3]);
                    uint flags1 = (uint)(eb[8]<<24|eb[9]<<16|eb[10]<<8|eb[11]);
                    uint flags2 = (uint)(eb[12]<<24|eb[13]<<16|eb[14]<<8|eb[15]);
                    uint off    = (uint)(eb[16]<<24|eb[17]<<16|eb[18]<<8|eb[19]);
                    uint size   = (uint)(eb[20]<<24|eb[21]<<16|eb[22]<<8|eb[23]);
                    if (i < 30 || id is 0x1000 or 0x400 or 0x401 or 0x402 or 0x403)
                        Log($"  [{i}] id=0x{id:X4} flags1=0x{flags1:X8} flags2=0x{flags2:X8} off=0x{off:X8} size={size}");
                }

                // === Extract inner PFS via PkgReader ===
                Log("STEP: Extract inner PFS via PkgReader");
                string tmpInner = Path.GetTempFileName();
                try
                {
                    using var reader = new PkgReader(pkgPath, passcode);
                    reader.ExtractRawInnerPfs(tmpInner);
                    long innerLen = new FileInfo(tmpInner).Length;
                    Log($"  Inner PFS: {innerLen} bytes ({innerLen/0x10000} blocks)");

                    if (innerLen == 0) { Log("DONE — no inner PFS"); return 0; }

                    byte[] pfsc = new byte[innerLen];
                    using (var ifs = File.OpenRead(tmpInner)) ifs.ReadExactly(pfsc);

                    long blockSize = 0x10000;
                    int numBlocks = (int)(innerLen / blockSize);

                    // Read ndinode from inner PFS header block 0 offset 0x30
                    int ndinode = (int)BitConverter.ToInt64(pfsc, 0x30);
                    Log($"  ndinode(inner PFS) = {ndinode}");

                    int occupiedBlocks = (int)((ndinode * 0xA8 + blockSize - 1) / blockSize);
                    Log($"  occupied_blocks = {occupiedBlocks} (shadPS4: ndinode*0xA8/0x10000)");

                    // Create synthetic sector map (raw blocks at 0x10000 each)
                    ulong[] sectorMap = new ulong[numBlocks + 1];
                    for (int i = 0; i <= numBlocks; i++) sectorMap[i] = (ulong)(i * blockSize);

                    // === Process blocks — shadPS4 inode/dirent logic ===
                    Log("STEP: shadPS4 inode/dirent processing");
                    var iNodeBuf = new List<S4Inode>();
                    var fsTable = new List<(string Name, int Inode, int Type)>();
                    var extractPaths = new Dictionary<int, string>();
                    string currentDir = "";
                    bool dinodeReached = false, urootReached = false;
                    int ndinodeCounter = 0, entSize = 0;

                    for (int i = 0; i < numBlocks; i++)
                    {
                        if (i * blockSize + blockSize > pfsc.Length) break;
                        var block = pfsc.AsSpan((int)(i * blockSize), (int)Math.Min(blockSize, pfsc.Length - i * blockSize));

                        // Read inodes (shadPS4 lines 326-334)
                        if (i >= 1 && i <= occupiedBlocks)
                        {
                            for (int p = 0; p + 0x6C <= block.Length; p += 0xA8)
                            {
                                var node = MemoryMarshal.Read<S4Inode>(block.Slice(p, 0x6C));
                                if (node.Mode == 0) break;
                                iNodeBuf.Add(node);
                            }
                        }

                        // Detect uroot (shadPS4 line 339-368)
                        if (!urootReached && block.Length > 0x20)
                        {
                            string fpt = Encoding.ASCII.GetString(block.Slice(0x10, Math.Min(15, block.Length - 0x10))).TrimEnd('\0');
                            if (fpt == "flat_path_table") { urootReached = true; /* Log($"  Block {i}: uroot found"); */ }
                        }
                        if (urootReached)
                        {
                            for (int j = 0; j + 16 <= block.Length; j += entSize)
                            {
                                var de = MemoryMarshal.Read<S4Dirent>(block.Slice(j, 16));
                                entSize = de.Entsize; if (entSize <= 0 || j + entSize > block.Length) break;
                                if (de.Ino != 0) ndinodeCounter++;
                                else { extractPaths[ndinodeCounter] = "ROOT"; urootReached = false; break; }
                            }
                        }

                        // Detect '.'/'..' (shadPS4 line 370-374)
                        if (!dinodeReached && block.Length > 0x30 &&
                            block[0x10] == '.' && block[0x28] == '.' && block[0x29] == '.')
                            dinodeReached = true;

                        // Read dirents (shadPS4 lines 377-410)
                        bool endReached = false;
                        if (dinodeReached)
                        {
                            for (int j = 0; j + 16 <= block.Length; j += entSize)
                            {
                                var de = MemoryMarshal.Read<S4Dirent>(block.Slice(j, 16));
                                if (de.Ino == 0) break;
                                entSize = de.Entsize; if (entSize <= 0 || j + entSize > block.Length) break;
                                int nlen = Math.Min(de.Namelen, entSize - 16);
                                string name = nlen > 0 ? Encoding.ASCII.GetString(block.Slice(j + 16, nlen)).TrimEnd('\0') : "";

                                if (de.Type == 4) currentDir = extractPaths.GetValueOrDefault(de.Ino, name);
                                if (de.Type == 2 || de.Type == 3)
                                {
                                    extractPaths[de.Ino] = (currentDir.Length > 0 ? currentDir + "/" : "") + name;
                                    fsTable.Add((extractPaths[de.Ino], de.Ino, de.Type));
                                }
                                ndinodeCounter++;
                                if ((ndinodeCounter + 1) >= ndinode) { endReached = true; break; }
                            }
                        }
                        if (endReached) break;
                    }

                    Log($"  Inodes read: {iNodeBuf.Count}, ndinode: {ndinode}, ndinodeCounter: {ndinodeCounter}");
                    Log($"  fsTable: {fsTable.Count} entries ({fsTable.Count(f=>f.Type==2)} files, {fsTable.Count(f=>f.Type==3)} dirs)");

                    // === Simulate file extraction ===
                    Log("STEP: Simulate shadPS4 ExtractFiles");
                    int failed = 0;
                    foreach (var (name, inoIdx, type) in fsTable)
                    {
                        if (type != 2) continue;
                        if (inoIdx >= iNodeBuf.Count)
                        {
                            Log($"  CRASH: inode {inoIdx} >= {iNodeBuf.Count} — OOB on {name}");
                            failed++; continue;
                        }
                        var ino = iNodeBuf[inoIdx];
                        int loc = (int)ino.Loc, nblocks = (int)ino.Blocks;
                        if (loc + nblocks > sectorMap.Length - 1)
                        {
                            Log($"  CRASH: loc({loc})+nblocks({nblocks}) > {sectorMap.Length} — OOB on {name}");
                            failed++; continue;
                        }
                    }

                    Log("");
                    Log("=== STRUCTURAL VALUES ===");
                    Log($"  ndinode(inner)     = {ndinode}");
                    Log($"  occupied_blocks    = {occupiedBlocks}");
                    Log($"  inodes_read        = {iNodeBuf.Count}");
                    Log($"  fsTable_entries    = {fsTable.Count}");
                    Log($"  dirs               = {fsTable.Count(f=>f.Type==3)}");
                    Log($"  files              = {fsTable.Count(f=>f.Type==2)}");
                    Log($"  numBlocks(inner)   = {numBlocks}");
                    Log($"  pfs_cache_size     = 0x{pfsCacheSize:X}");
                    Log($"  shad_cache*2       = 0x{shadLength:X}");
                    Log($"  pfs_image_size     = {pfsImageSize}");
                    Log($"  entry_count        = {entryCount}");
                    Log($"  CRASH_POINTS       = {failed}");

                    Log(failed > 0 ? "RESULT: POTENTIAL CRASH FOUND" : "RESULT: No structural issues");
                    return failed > 0 ? 1 : 0;
                }
                finally { try { File.Delete(tmpInner); } catch { } }
            }
            catch (Exception ex) { Log($"EXCEPTION: {ex}"); return 1; }
        }
    }
}
