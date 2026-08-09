using OrbisPkgTool.Util;

namespace OrbisPkgTool.Pfs;

/// <summary>
/// PFS image writer â€” builds the inner (unencrypted D32) game filesystem and
/// the outer (signed+encrypted S32) PFS that contains pfs_image.dat.
/// Mirrors the structure of real fake PKGs (validated against them):
/// block 0 header, block 1 inode table, superroot dirents, flat path table,
/// an empty block, indirect blocks, uroot dirents, then contiguous data.
/// </summary>
public static class PfsWriter
{
    public const long BlockSize = 0x10000;
    public const int XtsSectorSize = 0x1000;

    // ------------------------------------------------------------------
    // Inner PFS (mode 0x8, D32 inodes) â€” the game filesystem
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds the inner PFS image containing the given files.
    /// <paramref name="files"/>: target path (e.g. "eboot.bin") â†’ data.
    /// Layout: block 0 header, 1 inodes, 2 superroot dirents, 3 fpt, 4 empty,
    /// then dir dirent blocks, then contiguous file data.
    /// </summary>
    public static byte[] BuildInnerPfs(List<(string Path, byte[] Data)> files, long fileTime)
    {
        files = files.OrderBy(f => f.Path, StringComparer.OrdinalIgnoreCase).ToList();

        // Directory tree
        var root = new DirNode { Name = "uroot" };
        foreach (var (path, data) in files)
        {
            var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
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
            dir.Files.Add(new FileNode { Name = parts[^1], Data = data, Parent = dir });
        }

        // Inode numbering (matches reader + outer PFS):
        //   0 = superroot, 1 = flat_path_table, 2 = uroot,
        //   then subdirs, then files.
        uint next = 3;
        var dirs = AllDirs(root).ToList();
        foreach (var d in dirs) d.Number = next++;
        var fileNodes = AllFiles(root).ToList();
        foreach (var f in fileNodes) f.Number = next++;
        int dinodeCount = (int)next;

        // Block layout: 0=header, 1=inodes, 2=superroot dirents,
        //   3=fpt, 4=empty, 5=uroot dirents, then dirs, then files.
        long nextBlock = 6;
        foreach (var d in dirs)
            d.DirentsBlock = nextBlock++;
        foreach (var f in fileNodes)
        {
            f.StartBlock = nextBlock;
            nextBlock += CeilDiv(f.Data.Length, (int)BlockSize);
        }
        long ndblock = nextBlock;

        var image = new byte[ndblock * BlockSize];
        var w = new BinaryWriter(new MemoryStream(image));

        // ---- Block 0: header (D32, mode 0x8) ----
        WritePfsHeader(w, PfsMode.UnknownFlagAlwaysSet, dinodeCount, ndblock, fileTime, seed: null);

        // ---- Block 1: inode table (D32, 0xA8 each) ----
        // FPT records: 8 bytes each (uint32 hash + uint32 inode)
        long fptSize = (dirs.Count + fileNodes.Count) * 8;
        long inodePos = BlockSize;

        // Inode 0: superroot (directory, db[0] = block 2)
        WriteD32Inode(w, inodePos, 0x416D, 1, 0x00020010, BlockSize, 1, 2); inodePos += 0xA8;

        // Inode 1: flat_path_table (regular file, db[0] = block 3)
        WriteD32Inode(w, inodePos, 0x816D, 1, 0x00020010, fptSize, 1, 3); inodePos += 0xA8;

        // Inode 2: uroot (user root directory, db[0] = block 5)
        // nlink = 3 + subdirectories (matches LibOrbisPkg: uroot starts at 3,
        // +1 per subdir; verified against real orbis output)
        int urootNlink = 3 + root.Dirs.Count;
        WriteD32Inode(w, inodePos, 0x416D, (ushort)urootNlink, 0x00000010, BlockSize, 1, 5); inodePos += 0xA8;

        // Remaining subdirectories
        foreach (var d in dirs)
        {
            // nlink = 2 + subdirectories (POSIX convention)
            ushort nlink = (ushort)(2 + d.Dirs.Count);
            WriteD32Inode(w, inodePos, 0x416D, nlink, 0x00000010, BlockSize, 1, d.DirentsBlock);
            inodePos += 0xA8;
        }
        // Files
        foreach (var f in fileNodes)
        {
            WriteD32Inode(w, inodePos, 0x816D, 1, 0x00000010, f.Data.Length,
                CeilDiv(f.Data.Length, (int)BlockSize), f.StartBlock);
            inodePos += 0xA8;
        }

        // ---- Block 2: superroot dirents (flat_path_table, uroot) ----
        long superPos = 2 * BlockSize;
        WriteDirent(w, ref superPos, 1, PfsDirentType.File, "flat_path_table");    // ino 1
        WriteDirent(w, ref superPos, 2, PfsDirentType.Directory, "uroot");          // ino 2

        // ---- Block 3: flat path table (sorted by hash, 8 bytes/entry) ----
        // Upper 4 bits of inode field = flags (0=file, 2=directory)
        // Reader masks with 0x0FFFFFFF to get actual inode number.
        long fptPos = 3 * BlockSize;
        var fptEntries = new List<(uint Hash, uint Value)>();
        foreach (var d in dirs)
            fptEntries.Add((FptHash(FullPath(d)), d.Number | 0x20000000)); // flag 2 = directory
        foreach (var f in fileNodes)
            fptEntries.Add((FptHash(FullPath(f)), f.Number)); // flag 0 = file
        fptEntries.Sort((a, b) => a.Hash.CompareTo(b.Hash));
        foreach (var (hash, value) in fptEntries)
        {
            w.BaseStream.Position = fptPos;
            WriteLe(w, hash);
            WriteLe(w, value);
            fptPos += 8;
        }

        // ---- Block 4: empty (zeros, like real FPKGs) ----

        // ---- Block 5: uroot dirents (populated, with . and .. like real inner FPKGs) ----
        long pos = 5 * BlockSize;
        WriteDirent(w, ref pos, 2, PfsDirentType.Dot, ".");
        WriteDirent(w, ref pos, 2, PfsDirentType.DotDot, "..");
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
        foreach (var f in fileNodes)
            Buffer.BlockCopy(f.Data, 0, image, (int)(f.StartBlock * BlockSize), f.Data.Length);

        return image;
    }

    // ------------------------------------------------------------------
    // Outer PFS (mode 0xD, S32 inodes + XTS) â€” contains pfs_image.dat
    // ------------------------------------------------------------------

    /// <summary>
    /// Builds the outer PFS image containing a single file <paramref name="fileName"/>
    /// (e.g. "pfs_image.dat") and XTS-encrypts sectors 16+.
    /// </summary>
    public static byte[] BuildOuterPfs(byte[] fileData, string fileName, byte[] ekpfs, byte[] seed, long fileTime)
    {
        long dataBlocks = CeilDiv(fileData.Length, (int)BlockSize);
        // Block layout matches LibOrbisPkg: 0 header, 1 inodes, 2 superroot,
        // 3 fpt, 4 empty (plaintext), 5 ib0/singly-indirect, 6 ib1/doubly-indirect,
        // 7.. more indirect, uroot, data.
        long indirect1 = dataBlocks > 12 ? 1 : 0;
        long indirect2 = dataBlocks > 12 + 1820 ? 1 : 0;
        long nIndirect = dataBlocks > 12 + 1820 ? CeilDiv(dataBlocks - 12 - 1820, 1820L) : 0;
        long urootBlock = 5 + indirect1 + indirect2 + nIndirect;
        long dataStart = urootBlock + 1;
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

    /// <summary>Writes sig = HMAC(signKey, image[offset .. offset+size]) at <paramref name="slot"/>.</summary>
    private static void WriteBlockSig(BinaryWriter w, long slot, byte[] signKey, byte[] image, long offset, int size = (int)BlockSize)
    {
        var sig = HmacSha256(signKey, image.AsSpan((int)offset, size).ToArray());
        w.BaseStream.Position = slot;
        w.Write(sig);
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
        long fileTime, byte[]? seed)
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
        WriteLe(w, 1L);                    // dinode_block_count = 1 (matches orbis)
        WriteLe(w, 0L);                    // superroot_ino
        // Inode-table dinode at 0x50:
        // mode=0, nlink=1, flags=0x10 (unseeded inner) or 0 (seeded outer)
        long t = fileTime != 0 ? fileTime : DefaultFileTime;
        w.BaseStream.Position = 0x50;
        WriteLe(w, (ushort)0);             // mode
        WriteLe(w, (ushort)1);             // nlink
        WriteLe(w, seed == null ? 0x10u : 0u); // flags
        long inodeTableSize = 0x10000;     // always 1 block (matches orbis)
        WriteLe(w, inodeTableSize);        // size
        WriteLe(w, inodeTableSize);        // size uncompressed
        WriteLe(w, t); WriteLe(w, t); WriteLe(w, t); WriteLe(w, t);
        WriteLe(w, 0u); WriteLe(w, 0u); WriteLe(w, 0u); WriteLe(w, 0u);
        WriteLe(w, 0u); WriteLe(w, 0u); WriteLe(w, 0u); WriteLe(w, 0u);
        WriteLe(w, 0u); WriteLe(w, 0u);
        // blocks = 1 (matches orbis inner PFS: exactly 1 inode-table block)
        uint hdrBlocks = 1;
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
        int entSize = name.Length + 17;
        if (entSize % 8 != 0)
            entSize += 8 - (entSize % 8);
        WriteLe(w, (uint)ino);
        WriteLe(w, (int)type);
        WriteLe(w, name.Length);
        WriteLe(w, entSize);
        w.Write(System.Text.Encoding.ASCII.GetBytes(name));
        // Note: name is NOT null-terminated (matches original orbis behavior)
        pos += entSize;
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
        public readonly List<DirNode> Dirs = [];
        public readonly List<FileNode> Files = [];
    }

    private sealed class FileNode
    {
        public string Name = "";
        public DirNode? Parent;
        public uint Number;
        public long StartBlock;
        public byte[] Data = [];
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
