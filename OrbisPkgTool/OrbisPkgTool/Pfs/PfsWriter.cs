using OrbisPkgTool.Util;

namespace OrbisPkgTool.Pfs;

/// <summary>
/// PFS image writer â€” builds the inner (unencrypted D32) game filesystem and
/// the outer (signed+encrypted S32) PFS that contains pfs_image.dat.
/// Mirrors the structure of real fake PKGs (validated against them):
/// block 0 header, block 1 inode table, superroot dirents, flat path table,
/// an empty block, indirect blocks, uroot dirents, then contiguous data.
/// </summary>
/// <summary>
/// Disk-backed source file descriptor for streaming builds. Metadata only —
/// the file CONTENTS are opened lazily from <see cref=”PfsSourceFile.SourcePath”/>
/// while the inner PFS is written, keeping memory bounded for multi-GB games.
/// </summary>
public sealed record PfsSourceFile(string TargetPath, string SourcePath, long Length)
{
    /// <summary>Optional in-memory content for small generated files (e.g. keystone).</summary>
    public byte[]? Data { get; init; }
}

public static class PfsWriter
{
    // Central, origin-classified format constants — see PfsFormat.cs.
    public const long BlockSize = PfsFormat.BlockSize;
    public const int XtsSectorSize = PfsFormat.XtsSectorSize;

    // ------------------------------------------------------------------
    // Inner PFS (mode 0x8, D32 inodes) â€” the game filesystem
    // ------------------------------------------------------------------

    /// <summary>Internal uniform file input: memory-backed or disk-backed.</summary>
    private sealed class PfsFileInput
    {
        public required string Path;
        public required long Length;
        public byte[]? Data;      // memory-backed (small/generated files)
        public string? SourcePath; // disk-backed (large files)
    }

    /// <summary>
    /// Builds the inner PFS image containing the given files (memory-backed).
    /// <paramref name=”files”/>: target path (e.g. “eboot.bin”) â†’ data.
    /// Layout: block 0 header, 1 inodes, 2 superroot dirents, 3 fpt, 4 empty,
    /// then dir dirent blocks, then contiguous file data.
    /// </summary>
    public static void BuildInnerPfsToStream(List<(string Path, byte[] Data)> files, long fileTime, Stream output,
        System.Threading.CancellationToken ct = default, Action<long, long>? progress = null)
    {
        var inputs = files
            .OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase)
            .Select(f => new PfsFileInput { Path = f.Path, Length = f.Data.Length, Data = f.Data })
            .ToList();
        BuildInnerPfsCore(inputs, fileTime, output, ct, progress);
    }

    /// <summary>
    /// Builds the inner PFS image from disk-backed source descriptors (streaming;
    /// supports &gt;2GB games without loading file contents into memory).
    /// Byte-identical output to the memory-backed overload for the same files.
    /// </summary>
    public static void BuildInnerPfsToStream(IReadOnlyList<PfsSourceFile> files, long fileTime, Stream output,
        System.Threading.CancellationToken ct = default, Action<long, long>? progress = null)
    {
        var inputs = files
            .OrderBy(f => f.TargetPath, StringComparer.OrdinalIgnoreCase)
            .Select(f => new PfsFileInput { Path = f.TargetPath, Length = f.Length, Data = f.Data, SourcePath = f.SourcePath })
            .ToList();
        BuildInnerPfsCore(inputs, fileTime, output, ct, progress);
    }

    private static void BuildInnerPfsCore(List<PfsFileInput> files, long fileTime, Stream output,
        System.Threading.CancellationToken ct, Action<long, long>? progress)
    {
        // Directory tree
        var root = new DirNode { Name = "uroot" };
        foreach (var input in files)
        {
            var parts = input.Path.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var dir = root;
            for (int i = 0; i < parts.Length - 1; i++)
            {
                var child = dir.Dirs.FirstOrDefault(d => d.Name == parts[i]);
                if (child == null)
                {
                    child = new DirNode { Name = parts[i], Parent = dir };
                    dir.Dirs.Add(child);
                }
                dir = child;
            }
            dir.Files.Add(new FileNode { Name = parts[^1], Input = input, Parent = dir });
        }

        var dirs = AllDirs(root).ToList();
        var fileNodes = AllFiles(root).ToList();

        // FPT hash collision detection (before inode numbering — a collision
        // shifts the layout by one structural inode, OpenOrbis reference).
        var seen = new HashSet<uint>();
        bool hasCollision = false;
        foreach (var d in dirs) if (!seen.Add(FptHash(FullPath(d)))) hasCollision = true;
        foreach (var f in fileNodes) if (!seen.Add(FptHash(FullPath(f)))) hasCollision = true;

        // Inode numbering (matches reader + outer PFS):
        //   0 = superroot, 1 = flat_path_table, [2 = collision_resolver],
        //   then uroot, subdirs, files.  The resolver exists ONLY when a FPT
        //   hash collision was found (OpenOrbis/LibOrbisPkg reference).
        uint next = hasCollision ? 4u : 3u;
        // uroot's own inode number — MUST be set: the `..` dirent of every
        // first-level dir references it (d.Parent.Number) and would otherwise
        // be 0 (unset), which breaks readers that stop at ino==0 (shadPS4
        // PKG::Extract) and is invalid PFS (a PS4 would reject it too).
        root.Number = hasCollision ? 3u : 2u;
        foreach (var d in dirs) d.Number = next++;
        foreach (var f in fileNodes) f.Number = next++;
        int dinodeCount = (int)next;

        // Inode-table block count: D32 inodes are 0xA8 bytes, packed with the
        // skip rule (an inode never straddles a block boundary — verified
        // against the real Digimon: 739 inodes → 2 blocks, inode 390 at block 2).
        long inodeBlocks;
        {
            long p = BlockSize;
            for (int i = 0; i < dinodeCount; i++)
            {
                if (p % BlockSize > BlockSize - PfsFormat.D32InodeSize) p += BlockSize - (p % BlockSize);
                p += PfsFormat.D32InodeSize;
            }
            inodeBlocks = (p - BlockSize + BlockSize - 1) / BlockSize;
        }

        // FPT size and block count needed for the layout below.
        long fptSize = (dirs.Count + fileNodes.Count) * 8;

        // Block layout (matches real orbis): 0=header, 1..N=inode table,
        //   N+1=superroot dirents, N+2..N+2+fptBlocks-1=FPT (can span multiple
        //   blocks — games with >8192 files need 2+ blocks),
        //   N+2+fptBlocks=empty/collision_resolver, N+3+fptBlocks=uroot
        //   dirents, then dirs, then files.
        // Precompute per-directory dirent byte counts (needed for both
        // block allocation and inode sizing).
        long DirSize(DirNode d)
        {
            long s = DirentSize(".") + DirentSize("..");
            foreach (var c in d.Dirs) s += DirentSize(c.Name);
            foreach (var c in d.Files) s += DirentSize(c.Name);
            return s;
        }
        long urootDirentSize = DirSize(root);

        long superBlock = 1 + inodeBlocks;
        long fptBlock = superBlock + 1;
        long crBlock   = fptBlock + CeilDiv(fptSize, (int)BlockSize);
        long urootBlock = crBlock + 1;
        long nextBlock = urootBlock + CeilDiv(urootDirentSize, (int)BlockSize);
        foreach (var d in dirs)
        {
            d.DirentsBlock = nextBlock;
            long db = CeilDiv(DirSize(d), (int)BlockSize);
            if (db < 1) db = 1;
            d.DirentBlocks = db;
            nextBlock += db;
        }
        foreach (var f in fileNodes)
        {
            f.StartBlock = nextBlock;
            // Empty files must still occupy one block — allocating 0 blocks
            // makes the next file share this StartBlock (block overlap).
            long blocks = CeilDiv(f.Input.Length, (int)BlockSize);
            if (blocks < 1) blocks = 1;
            nextBlock += blocks;
        }
        long ndblock = nextBlock;

        // Collision resolver layout: per collided hash (FPT sorted order), the
        // dirents of its colliding nodes (full-path names), then 0x18 padding.
        // FPT entries for collided hashes hold 0x80000000 | running byte offset
        // into the resolver.
        var colOffsets = new Dictionary<uint, long>();
        long resolverSize = 0;
        if (hasCollision)
        {
            var byHash = new SortedDictionary<uint, List<(bool IsDir, uint Ino, string Path)>>();
            foreach (var d in dirs)
            {
                uint h = FptHash(FullPath(d));
                if (!byHash.TryGetValue(h, out var list)) byHash[h] = list = [];
                list.Add((true, d.Number, FullPath(d)));
            }
            foreach (var f in fileNodes)
            {
                uint h = FptHash(FullPath(f));
                if (!byHash.TryGetValue(h, out var list)) byHash[h] = list = [];
                list.Add((false, f.Number, FullPath(f)));
            }
            long off = 0;
            foreach (var kv in byHash)
            {
                if (kv.Value.Count < 2) continue;
                colOffsets[kv.Key] = off;
                foreach (var (_, _, path) in kv.Value)
                    off += DirentSize(path);
                off += 0x18;
            }
            resolverSize = off;
        }

        // Zero-fill the output up to the full image size (sparse support for files).
        output.SetLength(ndblock * BlockSize);
        var w = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true);

        // ---- Block 0: header (D32, mode 0x8) ----
        WritePfsHeader(w, PfsMode.UnknownFlagAlwaysSet, dinodeCount, ndblock, fileTime, seed: null,
            dinodeBlockCount: inodeBlocks);

        // ---- Inode table (D32, 0xA8 each, packed) ----
        long inodePos = BlockSize;
        void NextInode() { if (inodePos % BlockSize > BlockSize - PfsFormat.D32InodeSize) inodePos += BlockSize - (inodePos % BlockSize); }

        // Inode 0: superroot (directory, db[0] = superroot block)
        NextInode();
        WriteD32Inode(w, inodePos, 0x416D, 1, 0x00020010, BlockSize, 1, superBlock); inodePos += 0xA8;

        // Inode 1: flat_path_table (regular file, db[0] = fpt block).
        // For games with >8192 files the FPT spans multiple blocks — the
        // blocks field must reflect the actual allocation (was hardcoded 1,
        // which broke orbis-imposed FPT lookups on Bloodborne-scale trees).
        long fptBlocks = CeilDiv(fptSize, (int)BlockSize);
        NextInode();
        WriteD32Inode(w, inodePos, 0x816D, 1, 0x00020010, fptSize, fptBlocks, fptBlock); inodePos += 0xA8;

        // Inode 2: collision_resolver (only when hasCollision)
        if (hasCollision)
        {
            NextInode();
            WriteD32Inode(w, inodePos, 0x816D, 1, 0x00020010, resolverSize,
                CeilDiv(resolverSize, (int)BlockSize), crBlock);
            inodePos += 0xA8;
        }

        // uroot (user root directory, db[0] = uroot block)
        // nlink = 3 + subdirectories (matches LibOrbisPkg: uroot starts at 3,
        // +1 per subdir; verified against real orbis output)
        int urootNlink = 3 + root.Dirs.Count;
        long urootBlocks = CeilDiv(urootDirentSize, (int)BlockSize);
        if (urootBlocks < 1) urootBlocks = 1;
        NextInode();
        WriteD32Inode(w, inodePos, 0x416D, (ushort)urootNlink, 0x00000010,
            urootBlocks * BlockSize, urootBlocks, urootBlock); inodePos += 0xA8;

        // Remaining subdirectories
        foreach (var d in dirs)
        {
            // nlink = 2 + subdirectories (POSIX convention)
            ushort nlink = (ushort)(2 + d.Dirs.Count);
            long db = d.DirentBlocks;
            NextInode();
            WriteD32Inode(w, inodePos, 0x416D, nlink, 0x00000010,
                db * BlockSize, db, d.DirentsBlock);
            inodePos += 0xA8;
        }
        // Files
        foreach (var f in fileNodes)
        {
            NextInode();
            long blocks = CeilDiv(f.Input.Length, (int)BlockSize);
            if (blocks < 1) blocks = 1; // empty files still occupy one block
            WriteD32Inode(w, inodePos, 0x816D, 1, 0x00000010, f.Input.Length,
                blocks, f.StartBlock);
            inodePos += 0xA8;
        }

        // ---- Superroot dirents (flat_path_table[, collision_resolver], uroot) ----
        long superPos = superBlock * BlockSize;
        WriteDirent(w, ref superPos, 1, PfsDirentType.File, "flat_path_table");    // ino 1
        if (hasCollision)
            WriteDirent(w, ref superPos, 2, PfsDirentType.File, "collision_resolver"); // ino 2
        WriteDirent(w, ref superPos, hasCollision ? 3u : 2u, PfsDirentType.Directory, "uroot");

        // ---- Flat path table (sorted by hash, 8 bytes/entry) ----
        // Upper 4 bits of inode field = flags (0=file, 2=directory);
        // collided hashes use 0x80000000 | resolver byte offset (OpenOrbis).
        long fptPos = fptBlock * BlockSize;
        var fptEntries = new List<(uint Hash, uint Value)>();
        foreach (var d in dirs)
        {
            uint h = FptHash(FullPath(d));
            fptEntries.Add((h, colOffsets.TryGetValue(h, out var off) ? 0x80000000u | (uint)off : d.Number | 0x20000000));
        }
        foreach (var f in fileNodes)
        {
            uint h = FptHash(FullPath(f));
            fptEntries.Add((h, colOffsets.TryGetValue(h, out var off) ? 0x80000000u | (uint)off : f.Number));
        }
        fptEntries.Sort((a, b) => a.Hash.CompareTo(b.Hash));
        foreach (var (hash, value) in fptEntries)
        {
            w.BaseStream.Position = fptPos;
            WriteLe(w, hash);
            WriteLe(w, value);
            fptPos += 8;
        }

        // ---- Block: empty (no collision) or collision_resolver (collision) ----
        if (hasCollision)
        {
            long crPos = crBlock * BlockSize;
            var byHash = new SortedDictionary<uint, List<(bool IsDir, uint Ino, string Path)>>();
            foreach (var d in dirs)
            {
                uint h = FptHash(FullPath(d));
                if (!byHash.TryGetValue(h, out var list)) byHash[h] = list = [];
                list.Add((true, d.Number, FullPath(d)));
            }
            foreach (var f in fileNodes)
            {
                uint h = FptHash(FullPath(f));
                if (!byHash.TryGetValue(h, out var list)) byHash[h] = list = [];
                list.Add((false, f.Number, FullPath(f)));
            }
            foreach (var kv in byHash)
            {
                if (kv.Value.Count < 2) continue;
                foreach (var (isDir, ino, path) in kv.Value)
                    WriteDirent(w, ref crPos, ino, isDir ? PfsDirentType.Directory : PfsDirentType.File, path);
                crPos += 0x18;
            }
        }

        // ---- uroot dirents (populated, with . and .. like real inner FPKGs) ----
        // . and .. reference the uroot's ACTUAL inode (2, or 3 with a collision
        // resolver) — hardcoding 2 would point at the resolver when present.
        long pos = urootBlock * BlockSize;
        WriteDirent(w, ref pos, root.Number, PfsDirentType.Dot, ".");
        WriteDirent(w, ref pos, root.Number, PfsDirentType.DotDot, "..");
        foreach (var d in root.Dirs)
            WriteDirent(w, ref pos, d.Number, PfsDirentType.Directory, d.Name);
        foreach (var f in root.Files)
            WriteDirent(w, ref pos, f.Number, PfsDirentType.File, f.Name);

        // ---- dir dirents + file data ----
        foreach (var d in dirs)
        {
            long dpos = d.DirentsBlock * BlockSize;
            long parentIno = d.Parent?.Number ?? 2; // uroot is the default parent
            WriteDirent(w, ref dpos, d.Number, PfsDirentType.Dot, ".");
            WriteDirent(w, ref dpos, parentIno, PfsDirentType.DotDot, "..");
            foreach (var sd in d.Dirs)
                WriteDirent(w, ref dpos, sd.Number, PfsDirentType.Directory, sd.Name);
            foreach (var f in d.Files)
                WriteDirent(w, ref dpos, f.Number, PfsDirentType.File, f.Name);
        }
        long dataTotal = fileNodes.Sum(f => f.Input.Length);
        long dataDone = 0;
        foreach (var f in fileNodes)
        {
            ct.ThrowIfCancellationRequested();
            output.Position = f.StartBlock * BlockSize;
            if (f.Input.Data != null)
            {
                output.Write(f.Input.Data, 0, f.Input.Data.Length);
            }
            else
            {
                // Disk-backed: stream one file at a time with a bounded buffer.
                using var input = new FileStream(f.Input.SourcePath!, FileMode.Open, FileAccess.Read,
                    FileShare.Read, 1 << 20, FileOptions.SequentialScan);
                input.CopyTo(output, 1 << 20);
            }
            dataDone += f.Input.Length;
            progress?.Invoke(dataDone, dataTotal);
        }
    }

    public static byte[] BuildInnerPfs(List<(string Path, byte[] Data)> files, long fileTime)
    {
        // Estimate; fall back to a temp file for images that don't fit in a byte[].
        long estSize = 6 * BlockSize + files.Sum(f =>
        {
            long blocks = CeilDiv(f.Data.Length, (int)BlockSize);
            return Math.Max(1, blocks) * BlockSize;
        });
        if (estSize <= int.MaxValue - 1024)
        {
            using var ms = new MemoryStream((int)estSize);
            BuildInnerPfsToStream(files, fileTime, ms);
            return ms.ToArray();
        }
        throw new InvalidOperationException(
            $"Inner PFS too large for in-memory build ({estSize} bytes). Use the file-based build path.");
    }

    // ------------------------------------------------------------------
    // Outer PFS (mode 0xD, S32 inodes + XTS) â€” contains pfs_image.dat
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds the outer PFS image containing a single file <paramref name="fileName"/>
    /// (e.g. "pfs_image.dat") and XTS-encrypts sectors 16+.
    /// </summary>
    public static byte[] BuildOuterPfs(byte[] fileData, string fileName, byte[] ekpfs, byte[] seed, long fileTime,
        out long dataStartBlock)
    {
        long dataBlocks = CeilDiv(fileData.Length, (int)BlockSize);
        // Block layout matches LibOrbisPkg: 0 header, 1 inodes, 2 superroot,
        // 3 fpt, 4 empty (plaintext), 5 ib0/singly-indirect, 6 ib1/doubly-indirect,
        // 7.. more indirect, uroot, data.
        long indirect1 = dataBlocks > 12 ? 1 : 0;
        long indirect2 = dataBlocks > 12 + 1820 ? 1 : 0;
        long nIndirect = dataBlocks > 12 + 1820 ? CeilDiv(dataBlocks - 12 - 1820, 1820L) : 0;
        long urootBlock = 5 + indirect1 + indirect2 + nIndirect;
        dataStartBlock = urootBlock + 1;
        long dataStart = dataStartBlock;
        long ndblock = dataStart + dataBlocks;
        long emptyBlock = 4;  // LibOrbisPkg: empty block right after FPT, stays plaintext

        var image = new byte[ndblock * BlockSize];
        var w = new BinaryWriter(new MemoryStream(image));

        // ---- Block 0: header (mode 0xD, seed) ----
        WritePfsHeader(w, PfsMode.Signed | PfsMode.Encrypted | PfsMode.UnknownFlagAlwaysSet,
            4, ndblock, fileTime, seed);

        // ---- Block 1: inode table (S32, 0x2C8 each) ----
        long inode0 = BlockSize;              // superroot
        long inode1 = BlockSize + 0x2C8;      // flat_path_table
        long inode2 = BlockSize + 2 * 0x2C8;  // uroot
        long inode3 = BlockSize + 3 * 0x2C8;  // pfs_image.dat
        WriteS32Inode(w, inode0, 0x416D, 1, 0x0002000C, BlockSize, BlockSize, 1, 2, fileTime: fileTime);
        WriteS32Inode(w, inode1, 0x816D, 1, 0x0002000C, 8, 8, 1, 3, fileTime: fileTime);
        // uroot: nlink=1 (self only, no subdirectories)
        WriteS32Inode(w, inode2, 0x416D, 3, 0x0000000C, BlockSize, BlockSize, 1, urootBlock, fileTime: fileTime);
        // pfs_image.dat: size = on-disk (PFSC) length; sizeUnc = the PFSC's
        // rounded decompressed size (real FPKGs: 7356526446 vs 11971723264).
        // sizeUnc = decompressed size. For PFSC-wrapped inner PFS, read the
        // PFSC rounded_file_size (offset 0x28); otherwise use the raw length.
        bool isPfsc = fileData.Length >= 4 && fileData[0]=='P' && fileData[1]=='F' && fileData[2]=='S' && fileData[3]=='C';
        long uncompressed = isPfsc && fileData.Length >= 0x30
            ? (long)BitConverter.ToUInt64(fileData, 0x28) // PFSC rounded_file_size
            : fileData.Length;
        WriteS32Inode(w, inode3, 0x816D, 1, 0x0000000D, fileData.Length, uncompressed,
            dataBlocks, dataStart,
            indirect1: indirect1 > 0 ? 5 : 0, indirect2: indirect2 > 0 ? 6 : 0, fileTime: fileTime);

        // ---- Block 2: superroot dirents ----
        long superPos = 2 * BlockSize;
        WriteDirent(w, ref superPos, 1, PfsDirentType.File, "flat_path_table");
        WriteDirent(w, ref superPos, 2, PfsDirentType.Directory, "uroot");

        // ---- Block 3: flat path table (real FPKGs: a single entry for
        //      pfs_image.dat; the inode's size field (8) exposes one entry) ----
        long fptPos = 3 * BlockSize;
        WriteFptEntry(w, ref fptPos, "pfs_image.dat", 3);

        // ---- Block 4: empty (stays plaintext) ----

        // ---- Indirect blocks ----
        // ib0 (block 5): singly-indirect — flat list of data block pointers
        // ib1 (block 6): doubly-indirect — table of pointers to child indirect blocks (7, 8, ...)
        long dataPos = 0;
        if (indirect1 > 0)
        {
            WriteIndirectS32(w, 5 * BlockSize, 12, dataStart, ref dataPos, dataBlocks);
            if (indirect2 > 0)
            {
                long childIbBlock = 7;
                long remaining = dataBlocks - 12 - 1820;
                long ib1EntryIdx = 0;
                while (remaining > 0)
                {
                    // A. Populate child indirect block with data pointers
                    long dataStartIdx = 12 + 1820 + (ib1EntryIdx * 1820);
                    WriteIndirectS32(w, childIbBlock * BlockSize, dataStartIdx, dataStart, ref dataPos, dataBlocks);
                    // B. Write pointer entry inside ib1 (Block 6) pointing to the child block
                    long ib1EntryOff = 6 * BlockSize + (ib1EntryIdx * 36);
                    w.BaseStream.Position = ib1EntryOff;
                    w.Write(new byte[32]); // sig placeholder
                    WriteLe(w, (int)childIbBlock);
                    childIbBlock++;
                    ib1EntryIdx++;
                    remaining -= 1820;
                }
            }
        }

        // ---- uroot dirents: populated (matches real orbis output) ----
        // Verified: orbis outer uroot contains ".", "..", "pfs_image.dat".
        long urootPos = urootBlock * BlockSize;
        WriteDirent(w, ref urootPos, 2, PfsDirentType.Dot, ".");
        WriteDirent(w, ref urootPos, 2, PfsDirentType.DotDot, "..");
        WriteDirent(w, ref urootPos, 3, PfsDirentType.File, fileName);

        // ---- data ----
        Buffer.BlockCopy(fileData, 0, image, (int)(dataStart * BlockSize), fileData.Length);

        // ---- Signing (BOTTOM-UP: data → child indirect → ib1 → ib0 → inode) ----
        byte[] signKey = HmacSha256(ekpfs, Concat(Le32(2), seed));

        WriteOuterPfsSignatures(w, image, signKey, dataStart, urootBlock, dataBlocks,
            indirect1, indirect2, inode0, inode1, inode2, inode3, ndblock, fileTime);

        // ---- XTS-encrypt sectors 16+ (block 0 = plaintext header, block 4 = plaintext empty) ----
        var (tweakKey, dataKey) = PfsReader.DeriveXtsKeys(
            new PfsHeader { Mode = PfsMode.Signed | PfsMode.Encrypted | PfsMode.UnknownFlagAlwaysSet, Seed = seed },
            ekpfs);
        for (int sector = 16; sector < ndblock * 16; sector++)
        {
            if (sector >= emptyBlock * 16 && sector < (emptyBlock + 1) * 16)
                continue; // the empty block is stored plaintext
            PfsReader.XtsEncryptSector(image, sector * XtsSectorSize, (ulong)sector, dataKey!, tweakKey!);
        }
        return image;
    }

    /// <summary>Outer PFS builder for images that don't fit in a byte[] (stream based).</summary>
    public static void BuildOuterPfsToStream(Stream fileData, string fileName, byte[] ekpfs, byte[] seed,
        long fileTime, Stream output, out long dataStartBlock,
        System.Threading.CancellationToken ct = default,
        Action<long, long>? progress = null)
    {
        long dataBlocks = CeilDiv(fileData.Length, (int)BlockSize);
        long indirect1 = dataBlocks > 12 ? 1 : 0;
        long indirect2 = dataBlocks > 12 + 1820 ? 1 : 0;
        long nIndirect = dataBlocks > 12 + 1820 ? CeilDiv(dataBlocks - 12 - 1820, 1820L) : 0;
        long urootBlock = 5 + indirect1 + indirect2 + nIndirect;
        dataStartBlock = urootBlock + 1;
        long dataStart = dataStartBlock;
        long ndblock = dataStart + dataBlocks;
        long emptyBlock = 4;

        output.SetLength(ndblock * BlockSize);
        var w = new BinaryWriter(output, System.Text.Encoding.ASCII, leaveOpen: true);

        WritePfsHeader(w, PfsMode.Signed | PfsMode.Encrypted | PfsMode.UnknownFlagAlwaysSet, 4, ndblock, fileTime, seed);

        long inode0 = BlockSize;
        long inode1 = BlockSize + 0x2C8;
        long inode2 = BlockSize + 2 * 0x2C8;
        long inode3 = BlockSize + 3 * 0x2C8;
        WriteS32Inode(w, inode0, 0x416D, 1, 0x0002000C, BlockSize, BlockSize, 1, 2, fileTime: fileTime);
        WriteS32Inode(w, inode1, 0x816D, 1, 0x0002000C, 8, 8, 1, 3, fileTime: fileTime);
        WriteS32Inode(w, inode2, 0x416D, 3, 0x0000000C, BlockSize, BlockSize, 1, urootBlock, fileTime: fileTime);
        // sizeUnc = decompressed inner PFS size = PFSC header rounded_file_size
        // (offset 0x28, LE). pfs_image.dat holds the PFSC container, so its
        // stored length is the container size, not the inner PFS size.
        long uncompressed;
        if (fileData.Length >= 0x30)
        {
            long savePos = fileData.Position;
            fileData.Position = 0;
            var magic = new byte[4];
            fileData.ReadExactly(magic, 0, 4);
            fileData.Position = 0x28;
            var rbuf = new byte[8];
            fileData.ReadExactly(rbuf, 0, 8);
            fileData.Position = savePos;
            bool isPfsc = magic[0] == 'P' && magic[1] == 'F' && magic[2] == 'S' && magic[3] == 'C';
            uncompressed = isPfsc ? (long)BitConverter.ToUInt64(rbuf, 0) : fileData.Length;
        }
        else
        {
            uncompressed = fileData.Length;
        }
        WriteS32Inode(w, inode3, 0x816D, 1, 0x0000000D, fileData.Length, uncompressed,
            dataBlocks, dataStart,
            indirect1: indirect1 > 0 ? 5 : 0, indirect2: indirect2 > 0 ? 6 : 0, fileTime: fileTime);

        long superPos = 2 * BlockSize;
        WriteDirent(w, ref superPos, 1, PfsDirentType.File, "flat_path_table");
        WriteDirent(w, ref superPos, 2, PfsDirentType.Directory, "uroot");

        long fptPos = 3 * BlockSize;
        WriteFptEntry(w, ref fptPos, fileName, 3);

        long dataPos = 0;
        if (indirect1 > 0)
        {
            WriteIndirectS32(w, 5 * BlockSize, 12, dataStart, ref dataPos, dataBlocks);
            if (indirect2 > 0)
            {
                long childIbBlock = 7;
                long remaining = dataBlocks - 12 - 1820;
                long ib1EntryIdx = 0;
                while (remaining > 0)
                {
                    long dataStartIdx = 12 + 1820 + (ib1EntryIdx * 1820);
                    WriteIndirectS32(w, childIbBlock * BlockSize, dataStartIdx, dataStart, ref dataPos, dataBlocks);
                    long ib1EntryOff = 6 * BlockSize + (ib1EntryIdx * 36);
                    w.BaseStream.Position = ib1EntryOff;
                    w.Write(new byte[32]);
                    WriteLe(w, (int)childIbBlock);
                    childIbBlock++;
                    ib1EntryIdx++;
                    remaining -= 1820;
                }
            }
        }

        long urootPos = urootBlock * BlockSize;
        WriteDirent(w, ref urootPos, 2, PfsDirentType.Dot, ".");
        WriteDirent(w, ref urootPos, 2, PfsDirentType.DotDot, "..");
        WriteDirent(w, ref urootPos, 3, PfsDirentType.File, fileName);

        // Copy file data into place
        output.Position = dataStart * BlockSize;
        fileData.Position = 0;
        fileData.CopyTo(output);

        // Signing over plaintext, then XTS encrypt — both via stream-safe helpers
        byte[] signKey = HmacSha256(ekpfs, Concat(Le32(2), seed));
        WriteOuterPfsSignaturesStream(w, output, signKey, dataStart, urootBlock, dataBlocks,
            indirect1, indirect2, inode0, inode1, inode2, inode3, ndblock);

        var (tweakKey, dataKey) = PfsReader.DeriveXtsKeys(
            new PfsHeader { Mode = PfsMode.Signed | PfsMode.Encrypted | PfsMode.UnknownFlagAlwaysSet, Seed = seed },
            ekpfs);
        var sector = new byte[XtsSectorSize];
        long totalSectors = (ndblock - 1) * 16;
        long sectorDone = 0;
        for (long s = 16; s < ndblock * 16; s++)
        {
            ct.ThrowIfCancellationRequested();
            long blk = s / 16;
            if (blk == emptyBlock) continue; // empty block stays plaintext
            output.Position = s * XtsSectorSize;
            output.Read(sector, 0, XtsSectorSize);
            PfsReader.XtsEncryptSector(sector, 0, (ulong)s, dataKey!, tweakKey!);
            output.Position = s * XtsSectorSize;
            output.Write(sector, 0, XtsSectorSize);
            sectorDone++;
            if ((sectorDone & 0x3FF) == 0)
                progress?.Invoke(sectorDone * XtsSectorSize, totalSectors * XtsSectorSize);
        }
        progress?.Invoke(totalSectors * XtsSectorSize, totalSectors * XtsSectorSize);
    }

    /// <summary>Shared signing pass for the byte[] outer PFS.</summary>
    private static void WriteOuterPfsSignatures(BinaryWriter w, byte[] image, byte[] signKey,
        long dataStart, long urootBlock, long dataBlocks, long indirect1, long indirect2,
        long inode0, long inode1, long inode2, long inode3, long ndblock, long fileTime)
    {
        long ibBase = inode3 + 0x214; // indirect slots start at 0x214 in S32 inode

        // 1. Sign data blocks referenced by direct db[] entries (content blocks)
        for (int i = 0; i < Math.Min(dataBlocks, 12); i++)
            WriteBlockSig(w, inode3 + 0x64 + 36 * i, signKey, image, (dataStart + i) * BlockSize);
        WriteBlockSig(w, inode0 + 0x64, signKey, image, 2 * BlockSize);
        WriteBlockSig(w, inode1 + 0x64, signKey, image, 3 * BlockSize);
        WriteBlockSig(w, inode2 + 0x64, signKey, image, urootBlock * BlockSize);

        if (indirect1 > 0)
        {
            // 2. Sign data blocks referenced by ib0 (block 5) entries
            SignIndirectEntries(w, 5 * BlockSize, signKey, image, dataStart, 12, dataBlocks);

            if (indirect2 > 0)
            {
                // 3. Sign data blocks referenced by child indirect blocks (7, 8, ...)
                long childIbBlock = 7;
                long remaining = dataBlocks - 12 - 1820;
                long ib1EntryIdx = 0;
                while (remaining > 0)
                {
                    long dataStartIdx = 12 + 1820 + (ib1EntryIdx * 1820);
                    SignIndirectEntries(w, childIbBlock * BlockSize, signKey, image, dataStart, dataStartIdx, dataBlocks);
                    // 4. Sign ib1 (Block 6) entry pointing to this child indirect block
                    long ib1EntryOff = 6 * BlockSize + (ib1EntryIdx * 36);
                    WriteBlockSig(w, ib1EntryOff, signKey, image, childIbBlock * BlockSize);
                    childIbBlock++;
                    ib1EntryIdx++;
                    remaining -= 1820;
                }
                // 5. Sign ib0 (Block 5) and ib1 (Block 6) entries in the inode's ib slots
                WriteBlockSig(w, ibBase + 0 * 36, signKey, image, 5 * BlockSize); // ib0 sig
                WriteBlockSig(w, ibBase + 1 * 36, signKey, image, 6 * BlockSize); // ib1 sig
            }
            else
            {
                // Only ib0 — sign its entry in the inode
                WriteBlockSig(w, ibBase + 0 * 36, signKey, image, 5 * BlockSize);
            }
        }

        // 6. inode-table block sig at the header dinode's db[0]
        WriteBlockSig(w, 0x50 + 0x68, signKey, image, BlockSize);
        // 7. header sig at 0x380, covering header[0..0x5A0]
        WriteBlockSig(w, 0x380, signKey, image, 0, 0x5A0);
    }

    /// <summary>Writes sig = HMAC(signKey, image[offset .. offset+size]) at <paramref name="slot"/>.</summary>
    private static void WriteBlockSig(BinaryWriter w, long slot, byte[] signKey, byte[] image, long offset, int size = (int)BlockSize)
    {
        var sig = HmacSha256(signKey, image.AsSpan((int)offset, size).ToArray());
        w.BaseStream.Position = slot;
        w.Write(sig);
    }

    /// <summary>Stream-based signing pass for the outer PFS.</summary>
    private static void WriteOuterPfsSignaturesStream(BinaryWriter w, Stream output, byte[] signKey,
        long dataStart, long urootBlock, long dataBlocks, long indirect1, long indirect2,
        long inode0, long inode1, long inode2, long inode3, long ndblock)
    {
        long ibBase = inode3 + 0x214;

        for (int i = 0; i < Math.Min(dataBlocks, 12); i++)
            WriteBlockSigStream(w, output, signKey, inode3 + 0x64 + 36 * i, (dataStart + i) * BlockSize);
        WriteBlockSigStream(w, output, signKey, inode0 + 0x64, 2 * BlockSize);
        WriteBlockSigStream(w, output, signKey, inode1 + 0x64, 3 * BlockSize);
        WriteBlockSigStream(w, output, signKey, inode2 + 0x64, urootBlock * BlockSize);

        if (indirect1 > 0)
        {
            SignIndirectEntriesStream(w, output, signKey, 5 * BlockSize, dataStart, 12, dataBlocks);
            if (indirect2 > 0)
            {
                long childIbBlock = 7;
                long remaining = dataBlocks - 12 - 1820;
                long ib1EntryIdx = 0;
                while (remaining > 0)
                {
                    long dataStartIdx = 12 + 1820 + (ib1EntryIdx * 1820);
                    SignIndirectEntriesStream(w, output, signKey, childIbBlock * BlockSize, dataStart, dataStartIdx, dataBlocks);
                    long ib1EntryOff = 6 * BlockSize + (ib1EntryIdx * 36);
                    WriteBlockSigStream(w, output, signKey, ib1EntryOff, childIbBlock * BlockSize);
                    childIbBlock++;
                    ib1EntryIdx++;
                    remaining -= 1820;
                }
                WriteBlockSigStream(w, output, signKey, ibBase + 0 * 36, 5 * BlockSize);
                WriteBlockSigStream(w, output, signKey, ibBase + 1 * 36, 6 * BlockSize);
            }
            else
            {
                WriteBlockSigStream(w, output, signKey, ibBase + 0 * 36, 5 * BlockSize);
            }
        }

        WriteBlockSigStream(w, output, signKey, 0x50 + 0x68, BlockSize);
        WriteBlockSigStream(w, output, signKey, 0x380, 0, 0x5A0);
    }

    private static void WriteBlockSigStream(BinaryWriter w, Stream output, byte[] signKey, long slot, long offset, int size = (int)BlockSize)
    {
        output.Position = offset;
        var buf = new byte[size];
        int read = 0;
        while (read < size)
        {
            int n = output.Read(buf, read, size - read);
            if (n <= 0) break;
            read += n;
        }
        var sig = HmacSha256(signKey, buf);
        w.BaseStream.Position = slot;
        w.Write(sig);
    }

    private static void SignIndirectEntriesStream(BinaryWriter w, Stream output, byte[] signKey,
        long blockOffset, long dataStart, long firstDataIndex, long totalBlocks)
    {
        int count = 0;
        for (int i = 0; i < 1820 && firstDataIndex + i < totalBlocks; i++)
        {
            long block = dataStart + firstDataIndex + i;
            WriteBlockSigStream(w, output, signKey, blockOffset + 36 * count, block * BlockSize);
            count++;
        }
    }

    /// <summary>Signs the (sig, block) entries of an indirect block pointing at data blocks.</summary>
    private static void SignIndirectEntries(BinaryWriter w, long blockOffset, byte[] signKey, byte[] image,
        long dataStart, long firstDataIndex, long totalBlocks)
    {
        int count = 0;
        for (int i = 0; i < 1820 && firstDataIndex + i < totalBlocks; i++)
        {
            long block = dataStart + firstDataIndex + i;
            WriteBlockSig(w, blockOffset + 36 * count, signKey, image, block * BlockSize);
            count++;
        }
    }

    // ------------------------------------------------------------------
    // low-level writers
    // ------------------------------------------------------------------

    private static void WritePfsHeader(BinaryWriter w, PfsMode mode, long dinodeCount, long ndblock,
        long fileTime, byte[]? seed, long dinodeBlockCount = 1)
    {
        w.BaseStream.Position = 0;
        WriteLe(w, 1L);                    // version
        WriteLe(w, 20130315L);             // magic
        WriteLe(w, 0L);                    // id
        w.Write((byte)0); w.Write((byte)0); w.Write((byte)1); w.Write((byte)0); // fmode/clean/ro/rsv
        WriteLe(w, (ushort)mode);
        WriteLe(w, (ushort)0);
        WriteLe(w, 0x10000u);              // block size
        WriteLe(w, 0u);                    // n_backup
        WriteLe(w, 1L);                    // n_block
        WriteLe(w, dinodeCount);
        WriteLe(w, ndblock);
        WriteLe(w, dinodeBlockCount);      // dinode_block_count (real orbis: 2 for 739 inodes)
        WriteLe(w, 0L);                    // superroot_ino
        // Inode-table dinode at 0x50:
        // mode=0, nlink=1, flags=0x10 (unseeded inner) or 0 (seeded outer)
        long t = fileTime != 0 ? fileTime : DefaultFileTime;
        w.BaseStream.Position = 0x50;
        WriteLe(w, (ushort)0);             // mode
        WriteLe(w, (ushort)1);             // nlink
        WriteLe(w, seed == null ? 0x10u : 0u); // flags
        long inodeTableSize = dinodeBlockCount * BlockSize;
        WriteLe(w, inodeTableSize);        // size (real orbis: 131072 for 2 blocks)
        WriteLe(w, inodeTableSize);        // size uncompressed
        WriteLe(w, t); WriteLe(w, t); WriteLe(w, t); WriteLe(w, t);
        WriteLe(w, 0u); WriteLe(w, 0u); WriteLe(w, 0u); WriteLe(w, 0u);
        WriteLe(w, 0u); WriteLe(w, 0u); WriteLe(w, 0u); WriteLe(w, 0u);
        WriteLe(w, 0u); WriteLe(w, 0u);
        uint hdrBlocks = (uint)dinodeBlockCount;
        WriteLe(w, hdrBlocks);             // blocks at offset 0xB0
        WriteLe(w, 0u);                    // padding at offset 0xB4
        // Header dinode db[0] points to the inode table (block 1).
        // The header dinode at 0x50 ALWAYS uses the S32 pointer layout
        // (32-byte sig + 4-byte block = 36 bytes per slot), regardless of
        // whether the PFS is signed or unsigned. Real FPKG inner PFS
        // (mode 0x8, unsigned) has db[0].block at 0xD8.
        w.BaseStream.Position = 0x50 + 0x68;  // db[0] at 0xB8
        w.Write(new byte[32]);                // sig (zero for unsigned)
        WriteLe(w, 1);                        // block 1 = inode table
        // Seeded header: UnknownIndex at 0x36C, seed at 0x370.
        // Unseeded (inner D32) header: the 1 goes at 0x368 (matches real FPKGs).
        if (seed != null)
        {
            if (seed.Length != 16)
                throw new ArgumentException("PFS seed must be exactly 16 bytes", nameof(seed));
            w.BaseStream.Position = 0x36C;
            WriteLe(w, 1);
            w.BaseStream.Position = 0x370;
            w.Write(seed);
        }
        else
        {
            w.BaseStream.Position = 0x368;
            WriteLe(w, 1);
        }
        w.BaseStream.Position = w.BaseStream.Length;
    }

    private static void WriteD32Inode(BinaryWriter w, long pos, ushort mode, ushort nlink, uint flags,
        long size, long blocks, long db0, long ib0 = 0, long fileTime = 0)
    {
        w.BaseStream.Position = pos;
        long t = fileTime != 0 ? fileTime : DefaultFileTime;
        WriteLe(w, mode);
        WriteLe(w, nlink);
        WriteLe(w, flags);
        WriteLe(w, size);
        WriteLe(w, size); // size uncompressed
        WriteLe(w, t); WriteLe(w, t); WriteLe(w, t); WriteLe(w, t);
        WriteLe(w, 0u); WriteLe(w, 0u); WriteLe(w, 0u); WriteLe(w, 0u);
        WriteLe(w, 0u); WriteLe(w, 0u);
        WriteLe(w, 0UL); WriteLe(w, 0UL);
        WriteLe(w, (uint)blocks);
        // Explicit first pointer; db[1] = -1 marks a contiguous run for
        // multi-block files (as in real FPKGs), 0 for unused slots.
        WriteLe(w, (int)db0);
        WriteLe(w, blocks > 1 ? -1 : 0);
        for (int i = 2; i < 12; i++) WriteLe(w, 0);
        WriteLe(w, (int)ib0);
        for (int i = 1; i < 5; i++) WriteLe(w, 0);
    }

    /// <summary>
    /// Writes a signed-32 dinode (0x2C8 bytes) as found in the inode TABLE:
    /// mode(2) nlink(2) flags(4) size(8) sizeUnc(8) times(32+16) uid(4) gid(4)
    /// unk1(8) unk2(8) blocks(u32 @0x60), then direct pointers at +0x64 and
    /// indirect at +0x214 â€” each (32-byte sig + 4-byte block). (The header's
    /// 0x50 dinode uses the +0x68 variant instead.)
    /// </summary>
    private static void WriteS32Inode(BinaryWriter w, long pos, ushort mode, ushort nlink, uint flags,
        long size, long sizeUncompressed, long blocks, long db0,
        long indirect1 = 0, long indirect2 = 0, long fileTime = 0)
    {
        w.BaseStream.Position = pos;
        long t = fileTime != 0 ? fileTime : DefaultFileTime;
        WriteLe(w, mode);
        WriteLe(w, nlink);
        WriteLe(w, flags);
        WriteLe(w, size);
        WriteLe(w, sizeUncompressed);
        WriteLe(w, t); WriteLe(w, t); WriteLe(w, t); WriteLe(w, t);
        WriteLe(w, 0u); WriteLe(w, 0u); WriteLe(w, 0u); WriteLe(w, 0u); // nsec (4 Ã— uint)
        WriteLe(w, 0u);                                                  // uid
        WriteLe(w, 0u);                                                  // gid
        WriteLe(w, 0UL);                                                 // unk1
        WriteLe(w, 0UL);                                                 // unk2
        WriteLe(w, (uint)blocks);
        // sdi32 pointers: (sig 32 + block 4) â€” exactly matches LibOrbisPkg
        // DinodeS32 layout: blocks@0x60, db[0].sig@0x64, db[0].block@0x84.
        for (int i = 0; i < 12; i++)
        {
            w.Write(new byte[32]);
            WriteLe(w, i < blocks ? (int)(db0 + i) : 0);
        }
        for (int i = 0; i < 5; i++)
        {
            w.Write(new byte[32]);
            WriteLe(w, i == 0 ? (int)indirect1 : i == 1 ? (int)indirect2 : 0);
        }
    }

    /// <summary>Writes an indirect block with explicit data pointers starting at dataStart + dataPos.</summary>
    private static void WriteIndirectS32(BinaryWriter w, long pos, long firstDataIndex, long dataStart,
        ref long dataPos, long totalBlocks)
    {
        w.BaseStream.Position = pos;
        // Block numbers MUST match SignIndirectEntries: block = dataStart + firstDataIndex + i.
        // (The old code wrote dataStart + dataPos, overlapping the direct blocks.)
        int written = 0;
        for (int i = 0; i < 1820 && firstDataIndex + i < totalBlocks; i++)
        {
            w.Write(new byte[32]);
            WriteLe(w, (int)(dataStart + firstDataIndex + i));
            written++;
        }
        dataPos += written;
    }

    private static void WriteS32Pointer(BinaryWriter w, long pos, long block)
    {
        w.BaseStream.Position = pos;
        w.Write(new byte[32]);
        WriteLe(w, (int)block);
    }

    /// <summary>
    /// Writes one dirent at <paramref name="pos"/> and advances it past the
    /// entry, so consecutive calls pack entries into the same block.
    /// Layout: ino(u32) type(s32) nameLength(s32) entSize(s32) name(bytes).
    /// </summary>
    private static void WriteDirent(BinaryWriter w, ref long pos, long ino, PfsDirentType type, string name)
    {
        w.BaseStream.Position = pos;
        int entSize = DirentSize(name);
        WriteLe(w, (uint)ino);
        WriteLe(w, (int)type);
        WriteLe(w, name.Length);
        WriteLe(w, entSize);
        w.Write(System.Text.Encoding.ASCII.GetBytes(name));
        // Note: name is NOT null-terminated (matches original orbis behavior)
        pos += entSize;
    }

    /// <summary>On-disk size of a dirent: 16-byte header + name + padding to 8.</summary>
    private static int DirentSize(string name)
    {
        int entSize = name.Length + 17;
        if (entSize % 8 != 0)
            entSize += 8 - (entSize % 8);
        return entSize;
    }

    /// <summary>
    /// Computes the flat-path-table hash: h*31 + uppercase(c).
    /// The name already includes the leading "/" (e.g. "/eboot.bin").
    /// (hash("/eboot.bin") = 0x768C03E1, verified against real FPKGs).
    /// </summary>
    private static uint FptHash(string name)
    {
        uint hash = 0;
        foreach (var c in name)
            hash = (uint)char.ToUpper(c) + 31 * hash;
        return hash;
    }

    /// <summary>
    /// Writes one flat-path-table entry. The name is a bare filename
    /// ("pfs_image.dat"); the hash covers "/" + name uppercased.
    /// </summary>
    private static void WriteFptEntry(BinaryWriter w, ref long pos, string name, long ino)
    {
        w.BaseStream.Position = pos;
        WriteLe(w, FptHash("/" + name));
        WriteLe(w, (uint)ino);
        pos += 8;
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    /// <summary>Fixed timestamp used when no file time is given (2016-11-16, like real FPKGs).</summary>
    private const long DefaultFileTime = 1479250800;

    private static byte[] HmacSha256(byte[] key, byte[] data)
    {
        using var h = new System.Security.Cryptography.HMACSHA256(key);
        return h.ComputeHash(data);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    private static byte[] Le32(int v) => new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };

    private static long CeilDiv(long a, long b) => a / b + (a % b == 0 ? 0 : 1);

    private static IEnumerable<DirNode> AllDirs(DirNode root)
    {
        foreach (var d in root.Dirs)
        {
            yield return d;
            foreach (var c in AllDirs(d))
                yield return c;
        }
    }

    private static IEnumerable<FileNode> AllFiles(DirNode root)
    {
        foreach (var f in root.Files)
            yield return f;
        foreach (var d in root.Dirs)
            foreach (var f in AllFiles(d))
                yield return f;
    }

    private static int CountNodes(DirNode root) => AllDirs(root).Count() + AllFiles(root).Count();

    private static void WriteLe(BinaryWriter w, ushort v) => w.Write(BitConverter.GetBytes(v));
    private static void WriteLe(BinaryWriter w, uint v) => w.Write(BitConverter.GetBytes(v));
    private static void WriteLe(BinaryWriter w, int v) => w.Write(BitConverter.GetBytes(v));
    private static void WriteLe(BinaryWriter w, long v) => w.Write(BitConverter.GetBytes(v));
    private static void WriteLe(BinaryWriter w, ulong v) => w.Write(BitConverter.GetBytes(v));

    private sealed class DirNode
    {
        public string Name = "";
        public DirNode? Parent;
        public uint Number;
        public long DirentsBlock;
        public long DirentBlocks = 1;
        public readonly List<DirNode> Dirs = [];
        public readonly List<FileNode> Files = [];
    }

    private sealed class FileNode
    {
        public string Name = "";
        public DirNode? Parent;
        public uint Number;
        public long StartBlock;
        public PfsFileInput Input = new() { Path = "", Length = 0 };
    }

    /// <summary>Full "/"-separated path of a node from the filesystem root (the
    /// root node itself contributes nothing â€” "eboot.bin" â†’ "/eboot.bin").</summary>
    private static string FullPath(DirNode n) =>
        n.Parent == null ? "" : FullPath(n.Parent) + "/" + n.Name;
    private static string FullPath(FileNode n) =>
        n.Parent == null ? n.Name : FullPath(n.Parent) + "/" + n.Name;
}

public enum PfsDirentType
{
    File = 2,
    Directory = 3,
    Dot = 4,
    DotDot = 5,
}
