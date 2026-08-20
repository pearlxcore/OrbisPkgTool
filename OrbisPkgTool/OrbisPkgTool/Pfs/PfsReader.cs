using System.Security.Cryptography;
using OrbisPkgTool.Binary;

namespace OrbisPkgTool.Pfs;

/// <summary>
/// PFS (PlayStation File System) reader — the filesystem that holds the
/// Image0 game data inside a PS4 PKG. Pure managed implementation of the
/// same structures the native tool walks via its embedded OpenSSL.
///
/// Layout (block size 0x10000, XTS sectors of 0x1000):
///   block 0            : PFS header
///   block 1..          : inode table
///   next block         : super-root dirents (flat_path_table, uroot)
///   next blocks        : flat path table, empty block
///   remaining blocks   : file/directory data
/// </summary>
public sealed class PfsReader : IDisposable
{
    private const long BlockSize = 0x10000;
    private const int XtsSectorSize = 0x1000;

    private readonly BigEndianReader _reader; // used as a low-level byte reader (LE fields read manually)
    private readonly long _pfsOffset;
    private readonly PfsHeader _header;
    private readonly PfsInode[] _inodes;
    private readonly byte[]? _tweakKey;
    private readonly byte[]? _dataKey;
    private readonly XtsTransforms? _xts; // cached transforms for the decrypt hot path
    private bool _disposed;

    public PfsHeader Header => _header;
    public int InodeCount => _inodes.Length;
    public long PfsOffset => _pfsOffset;

    /// <summary>
    /// Releases the cached XTS transforms. The underlying stream is owned by
    /// the caller and is NOT disposed here (see <see cref="Open"/>: the
    /// BigEndianReader wraps a caller-owned stream).
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _xts?.Dispose();
    }

    public PfsInode? GetInode(uint number) =>
        number < _inodes.Length ? _inodes[number] : null;

    /// <summary>
    /// The uroot inode: 2 normally, or 3 when inode 2 is a collision_resolver
    /// file (FPT hash-collision layout — OpenOrbis reference).
    /// </summary>
    public uint UrootInode => GetInode(2)?.IsDirectory == true ? 2u : 3u;

    /// <summary>Resolves a slash-separated path (e.g. "sce_sys/param.sfo") from the uroot inode.</summary>
    public PfsInode? FindFile(string path)
    {
        var current = GetInode(UrootInode);
        if (current == null) return null;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        for (int i = 0; i < parts.Length; i++)
        {
            bool last = i == parts.Length - 1;
            PfsInode? next = null;
            foreach (var d in ReadDirents(current))
            {
                if (string.Equals(d.Name, parts[i], StringComparison.OrdinalIgnoreCase))
                {
                    next = GetInode(d.InodeNumber);
                    break;
                }
            }
            if (next == null) return null;
            if (last) return next;
            if (!next.IsDirectory) return null;
            current = next;
        }
        return current;
    }

    private PfsReader(BigEndianReader r, long pfsOffset, PfsHeader header, PfsInode[] inodes, byte[]? tweakKey, byte[]? dataKey)
    {
        _reader = r;
        _pfsOffset = pfsOffset;
        _header = header;
        _inodes = inodes;
        _tweakKey = tweakKey;
        _dataKey = dataKey;
        _xts = (dataKey != null && tweakKey != null) ? XtsTransforms.Create(dataKey, tweakKey) : null;
    }

    /// <summary>
    /// Opens a PFS image at <paramref name="pfsOffset"/> inside the package stream.
    /// <paramref name="ekpfs"/> is required only when the PFS is encrypted.
    /// </summary>
    public static PfsReader Open(BigEndianReader r, long pfsOffset, byte[]? ekpfs = null)
    {
        r.Position = pfsOffset;
        var header = ReadHeader(r);
        (byte[]? tweakKey, byte[]? dataKey) = DeriveXtsKeys(header, ekpfs);
        var inodes = ReadInodes(r, pfsOffset, header, tweakKey, dataKey);
        return new PfsReader(r, pfsOffset, header, inodes, tweakKey, dataKey)
        {
            HeaderBytes = ReadHeaderBytes(r, pfsOffset),
        };
    }

    /// <summary>Raw header block bytes (diagnostic).</summary>
    public byte[] HeaderBytes { get; private set; } = [];

    private static byte[] ReadHeaderBytes(BigEndianReader r, long pfsOffset)
    {
        long old = r.Position;
        r.Position = pfsOffset;
        var b = r.ReadBytes(0x400);
        r.Position = old;
        return b;
    }

    /// <summary>
    /// Derives the AES-XTS (tweak, data) key pair from the EKPFS and the PFS
    /// header seed: encKey = HMAC-SHA256(EKPFS, LE32(1) || seed),
    /// tweakKey = encKey[0..16], dataKey = encKey[16..32].
    /// </summary>
    public static (byte[]? TweakKey, byte[]? DataKey) DeriveXtsKeys(PfsHeader header, byte[]? ekpfs)
    {
        if (!header.Mode.HasFlag(PfsMode.Encrypted))
            return (null, null);
        if (ekpfs == null)
            throw new InvalidDataException("PFS is encrypted but no EKPFS was provided.");
        var encKey = HmacSha256(ekpfs, Concat(LeBytes(1), header.Seed));
        return (encKey[..16], encKey[16..]);
    }

    /// <summary>
    /// Returns the raw (decrypted) data of <paramref name="ino"/>.
    /// Only suitable for files that fit in memory — use <see cref="OpenFileStream"/>
    /// for large files (e.g. the inner PFS image).
    /// </summary>
    public byte[] ReadFileData(PfsInode ino)
    {
        long size = ino.Size;
        if (size > int.MaxValue)
            throw new InvalidOperationException("File is too large to read into memory; use OpenFileStream().");
        var output = new byte[(int)size];
        int written = 0;
        foreach (int block in EnumerateBlocks(ino))
        {
            if (block <= 0 || written >= size) break;
            byte[] data = ReadBlock(block);
            int take = (int)Math.Min(data.Length, size - written);
            Buffer.BlockCopy(data, 0, output, written, take);
            written += take;
            if (written >= size) break;
        }
        return output;
    }

    /// <summary>
    /// Returns a seekable stream over the decrypted data of <paramref name="ino"/>.
    /// The stream exposes <paramref name="ino"/>.Size bytes (the on-disk data);
    /// callers of compressed (PFSC) files wrap it in <see cref="PFSCStream"/>.
    /// </summary>
    public Stream OpenFileStream(PfsInode ino)
    {
        return new PfsFileStream(this, ino, ino.Size);
    }

    /// <summary>
    /// Enumerates the data block numbers of an inode (direct + indirect).
    /// Standard Unix indirection: db[0..11] direct; ib[0] single-indirect;
    /// ib[1] doubly-indirect; ib[2] triply-indirect, etc.
    /// A pointer value of 0xFFFFFFFF (-1) marks a contiguous run: the
    /// following blocks are consecutive from the previous pointer.
    /// </summary>
    private List<int> EnumerateBlocks(PfsInode ino)
    {
        var blocks = new List<int>();
        int lastBlock = -1;
        foreach (int block in ino.DirectBlocks)
        {
            if (block == -1)
            {
                if (lastBlock <= 0) break;
                // Contiguous run: extend for the remaining blocks of the file.
                while (blocks.Count < ino.Blocks)
                {
                    lastBlock++;
                    blocks.Add(lastBlock);
                }
                return blocks;
            }
            if (block <= 0) break;
            lastBlock = block;
            blocks.Add(block);
        }
        for (int level = 1; level <= ino.IndirectBlocks.Length; level++)
        {
            int ibBlock = ino.IndirectBlocks[level - 1];
            if (ibBlock <= 0) break;
            CollectIndirect(ibBlock, level, blocks, ref lastBlock);
        }
        return blocks;
    }

    /// <summary>
    /// Public read-only view of an inode's allocated data blocks (direct +
    /// indirect + contiguous runs), ordered by file offset. Used by the PFSC
    /// profiler to map file allocations onto PFSC block ranges.
    /// </summary>
    public IReadOnlyList<int> EnumerateFileBlocks(PfsInode ino) => EnumerateBlocks(ino);

    /// <summary>
    /// Collects the data block numbers reachable through <paramref name="depth"/>
    /// levels of indirection starting at block <paramref name="block"/>.
    /// </summary>
    private void CollectIndirect(int block, int depth, List<int> blocks, ref int lastBlock)
    {
        if (block <= 0 || depth <= 0)
            return;
        byte[] data = ReadBlock(block);
        int ptrSize = _header.Mode.HasFlag(PfsMode.Is64Bit) ? 8 : 4;
        int entrySize = _header.Mode.HasFlag(PfsMode.Signed) ? 32 + ptrSize : ptrSize;
        int count = data.Length / entrySize;
        for (int i = 0; i < count; i++)
        {
            int off = i * entrySize;
            int ptr = ptrSize == 8
                ? (int)ReadLe64(data, off + 32)
                : (int)ReadLe32(data, off + (_header.Mode.HasFlag(PfsMode.Signed) ? 32 : 0));
            if (ptr == -1)
            {
                // Contiguous run from the previous pointer.
                if (lastBlock <= 0) continue;
                while (i < count - 1)
                {
                    lastBlock++;
                    blocks.Add(lastBlock);
                    i++;
                }
                return;
            }
            if (ptr <= 0) continue;
            lastBlock = ptr;
            if (depth == 1)
                blocks.Add(ptr);
            else
                CollectIndirect(ptr, depth - 1, blocks, ref lastBlock);
        }
    }

    /// <summary>
    /// A seekable read-only stream over a PFS file's decrypted data.
    /// Reads map to (block, offset) pairs, XTS-decrypting each block on demand.
    /// </summary>
    private sealed class PfsFileStream : Stream
    {
        private readonly PfsReader _pfs;
        private readonly int[] _blocks;
        private readonly long _length;
        private long _position;

        public PfsFileStream(PfsReader pfs, PfsInode ino, long length)
        {
            _pfs = pfs;
            _blocks = pfs.EnumerateBlocks(ino).Where(b => b > 0).ToArray();
            _length = length;
        }

        public override bool CanRead => true;
        public override bool CanSeek => true;
        public override bool CanWrite => false;
        public override long Length => _length;
        public override long Position { get => _position; set => _position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            if (_position >= _length) return 0;
            int total = 0;
            while (count > 0 && _position < _length)
            {
                long blockIndex = _position / BlockSize;
                if (blockIndex >= _blocks.Length) break;
                int inBlock = (int)(_position % BlockSize);
                byte[] block = _pfs.ReadBlock(_blocks[blockIndex]);
                int take = (int)Math.Min(Math.Min(count, block.Length - inBlock), _length - _position);
                Buffer.BlockCopy(block, inBlock, buffer, offset, take);
                offset += take;
                _position += take;
                count -= take;
                total += take;
            }
            return total;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            _position = origin switch
            {
                SeekOrigin.Begin => offset,
                SeekOrigin.Current => _position + offset,
                _ => _length + offset,
            };
            return _position;
        }

        public override void Flush() { }
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    /// <summary>Reads one 0x10000-byte block, XTS-decrypting it when the PFS is encrypted.</summary>
    public byte[] ReadBlockRaw(int block) =>
        _reader.ReadBytesAt(_pfsOffset + (long)block * BlockSize, (int)BlockSize);

    /// <summary>Reads one 0x10000-byte block, XTS-decrypting it when the PFS is encrypted.</summary>
    public byte[] ReadBlock(int block)
    {
        byte[] data = _reader.ReadBytesAt(_pfsOffset + (long)block * BlockSize, (int)BlockSize);
        if (_xts != null)
        {
            for (int sector = block * 16; sector < block * 16 + 16; sector++)
            {
                if (sector >= 16) // header block is never encrypted
                    XtsDecryptSector(data, (sector - block * 16) * XtsSectorSize, (ulong)sector,
                        _xts.DataDecryptor, _xts.TweakEncryptor);
            }
        }
        return data;
    }

    /// <summary>Reads the dirents of a directory inode, returning child entries.</summary>
    public List<PfsDirent> ReadDirents(PfsInode dir)
    {
        var result = new List<PfsDirent>();
        // EnumerateBlocks expands contiguous runs (db[0]=first, db[1..]=-1):
        // a directory whose dirents span multiple blocks would otherwise lose
        // every continuation block (db[1] = -1 breaks the raw array walk).
        // Each block is terminated by its padding (invalid entsize / ino==0);
        // the block list itself is bounded by ino.Blocks. dir.Size is the
        // ROUNDED allocation (blocks * 0x10000), NOT a dirent byte budget, so
        // it must not be used to stop the walk mid-directory.
        //
        // Sony packs a multi-block dir as ONE CONTIGUOUS dirent stream: entries
        // may STRADDLE block boundaries (the name continues in the next block).
        // Our own writer (and LibOrbisPkg) instead never straddle and zero-pad
        // each block tail. Both layouts are handled: per-block parsing starts at
        // offset 0, and a dirent whose name would cross the block end has its
        // continuation spliced from the next block.
        var blocks = EnumerateBlocks(dir).Where(b => b > 0).ToList();
        byte[]? pending = null;   // next block already loaded for a splice
        int pendingOff = 0;       // where parsing continues inside it
        for (int bi = 0; bi < blocks.Count; bi++)
        {
            byte[] data = pending ?? ReadBlock(blocks[bi]);
            int off = pending != null ? pendingOff : 0;
            pending = null;
            while (off + 16 <= data.Length)
            {
                uint inodeNumber = ReadLe32(data, off);
                int type = ReadLe32Signed(data, off + 4);
                int nameLength = ReadLe32Signed(data, off + 8);
                int entSize = ReadLe32Signed(data, off + 12);
                if (entSize < 16 + nameLength || entSize > 0x400 || nameLength < 0 || nameLength > 0x400)
                    break; // padding or corrupt
                string name;
                if (off + 16 + nameLength > data.Length)
                {
                    // Dirent straddles the block boundary — splice the name from
                    // the continuation block (Sony contiguous-stream layout).
                    int firstPart = data.Length - (off + 16);
                    var nameBytes = new byte[nameLength];
                    Array.Copy(data, off + 16, nameBytes, 0, firstPart);
                    byte[] next = bi + 1 < blocks.Count ? ReadBlock(blocks[bi + 1])
                        : new byte[nameLength - firstPart];
                    int take = Math.Min(nameLength - firstPart, next.Length);
                    Array.Copy(next, 0, nameBytes, firstPart, take);
                    name = System.Text.Encoding.ASCII.GetString(nameBytes).TrimEnd('\0');
                    // The next dirent starts after this entry's name remainder +
                    // padding inside the continuation block.
                    pending = next;
                    pendingOff = entSize - 16 - firstPart;
                    result.Add(new PfsDirent(inodeNumber, type, name));
                    break;
                }
                name = System.Text.Encoding.ASCII.GetString(data, off + 16, nameLength)
                    .TrimEnd('\0'); // PFS names are null-terminated
                result.Add(new PfsDirent(inodeNumber, type, name));
                off += entSize;
            }
        }
        return result;
    }

    // ---- PFS header / inode parsing (little-endian fields) ----

    private static PfsHeader ReadHeader(BigEndianReader r)
    {
        byte[] b = r.ReadBytes(0x400);
        var h = new PfsHeader
        {
            Version = (long)ReadLe64(b, 0x00),
            Magic = (long)ReadLe64(b, 0x08),
            Id = (long)ReadLe64(b, 0x10),
            Fmode = b[0x18],
            Clean = b[0x19],
            ReadOnly = b[0x1A],
            Rsv = b[0x1B],
            Mode = (PfsMode)ReadLe16(b, 0x1C),
            Unk1 = ReadLe16(b, 0x1E),
            BlockSize = ReadLe32(b, 0x20),
            NBackup = ReadLe32(b, 0x24),
            NBlock = (long)ReadLe64(b, 0x28),
            DinodeCount = (long)ReadLe64(b, 0x30),
            Ndblock = (long)ReadLe64(b, 0x38),
            DinodeBlockCount = (long)ReadLe64(b, 0x40),
        };
        h.Seed = b[0x370..0x380];
        return h;
    }

    private static PfsInode[] ReadInodes(BigEndianReader r, long pfsOffset, PfsHeader h, byte[]? tweakKey, byte[]? dataKey)
    {
        long inodeSize = h.Mode.HasFlag(PfsMode.Is64Bit) ? 0x310 : h.Mode.HasFlag(PfsMode.Signed) ? 0x2C8 : 0xA8;
        long pos = pfsOffset + BlockSize; // inode table starts at block 1
        var inodes = new PfsInode[(int)h.DinodeCount];
        byte[]? block = null;
        long blockIndex = -1;
        using var xts = (dataKey != null && tweakKey != null) ? XtsTransforms.Create(dataKey, tweakKey) : null;
        for (long i = 0; i < h.DinodeCount; i++)
        {
            long inodeBlock = (pos - pfsOffset) / BlockSize;
            if (inodeBlock != blockIndex)
            {
                blockIndex = inodeBlock;
                r.Position = pfsOffset + inodeBlock * BlockSize;
                block = r.ReadBytes((int)BlockSize);
                if (xts != null)
                {
                    for (int sector = (int)(inodeBlock * 16); sector < (int)(inodeBlock * 16 + 16); sector++)
                    {
                        if (sector >= 16)
                            XtsDecryptSector(block, (sector - (int)(inodeBlock * 16)) * XtsSectorSize, (ulong)sector,
                                xts.DataDecryptor, xts.TweakEncryptor);
                    }
                }
            }
            long inBlockOff = (pos - pfsOffset) - inodeBlock * BlockSize;
            var b = new byte[inodeSize];
            Buffer.BlockCopy(block!, (int)inBlockOff, b, 0, (int)inodeSize);
            inodes[i] = ParseInode(b, h, inodeSize);
            pos += inodeSize;
            if (pos % BlockSize > BlockSize - inodeSize)
                pos += BlockSize - (pos % BlockSize); // pack rule: skip to next block when one inode doesn't fit
        }
        return inodes;
    }

    private static PfsInode ParseInode(byte[] b, PfsHeader h, long inodeSize)
    {
        bool is64 = h.Mode.HasFlag(PfsMode.Is64Bit);
        bool signed = h.Mode.HasFlag(PfsMode.Signed);
        var ino = new PfsInode
        {
            Mode = ReadLe16(b, 0),
            Nlink = ReadLe16(b, 2),
            Flags = ReadLe32(b, 4),
            Size = (long)ReadLe64(b, 8),
            SizeCompressed = (long)ReadLe64(b, 16),
            Blocks = is64 ? (long)ReadLe64(b, 96) : ReadLe32(b, 96),
        };
        int ptrSize = is64 ? 8 : 4;
        int header = is64 ? 104 : 100; // inode-table entries: db[0] at 0x64; header dinode is separate
        for (int i = 0; i < 12; i++)
        {
            int off = header + i * (32 * (signed ? 1 : 0) + ptrSize);
            ino.DirectBlocks[i] = signed
                ? (is64 ? (int)ReadLe64(b, off + 32) : (int)ReadLe32(b, off + 32))
                : (is64 ? (int)ReadLe64(b, off) : (int)ReadLe32(b, off));
        }
        int ibStart = header + 12 * (32 * (signed ? 1 : 0) + ptrSize);
        for (int i = 0; i < 5; i++)
        {
            int off = ibStart + i * (32 * (signed ? 1 : 0) + ptrSize);
            ino.IndirectBlocks[i] = signed
                ? (is64 ? (int)ReadLe64(b, off + 32) : (int)ReadLe32(b, off + 32))
                : (is64 ? (int)ReadLe64(b, off) : (int)ReadLe32(b, off));
        }
        return ino;
    }

    // ---- AES-XTS (IEEE 1679 / NIST SP 800-38E) ----

    /// <summary>
    /// AES-XTS encryption of one 0x1000-byte data unit in place: C = E(P ^ T) ^ T,
    /// with the sector index as 128-bit LE tweak, advanced per 16-byte block.
    /// Creates its AES transforms per call — use the transform-accepting overload
    /// in hot loops (16+ sectors per PFS block) to avoid the per-call setup.
    /// </summary>
    public static void XtsEncryptSector(byte[] data, int offset, ulong sector, byte[] dataKey, byte[] tweakKey)
    {
        using var tweakAes = Aes.Create();
        tweakAes.Mode = CipherMode.ECB;
        tweakAes.Padding = PaddingMode.None;
        tweakAes.Key = tweakKey;
        using var tweakEnc = tweakAes.CreateEncryptor();

        using var dataAes = Aes.Create();
        dataAes.Mode = CipherMode.ECB;
        dataAes.Padding = PaddingMode.None;
        dataAes.Key = dataKey;
        using var dataEnc = dataAes.CreateEncryptor();

        XtsEncryptSector(data, offset, sector, dataEnc, tweakEnc);
    }

    /// <summary>
    /// AES-XTS encryption of one 0x1000-byte data unit in place, using
    /// caller-supplied ECB transforms (see <see cref="XtsTransforms.Create"/>).
    ///
    /// ECB has no inter-block feedback, so the per-block loop
    /// C_i = E(P_i ^ T_i) ^ T_i is evaluated batched: build the full tweak
    /// schedule T_0..T_255, XOR it over the sector, run ONE TransformBlock
    /// across all 4096 bytes, XOR the schedule back. Byte-identical to the
    /// classic per-block loop but ~2x faster (one interop transition per
    /// sector instead of 256).
    /// </summary>
    public static void XtsEncryptSector(byte[] data, int offset, ulong sector,
        ICryptoTransform dataEncryptor, ICryptoTransform tweakEncryptor)
    {
        byte[] tweak = MakeTweak(sector);
        tweakEncryptor.TransformBlock(tweak, 0, 16, tweak, 0);
        byte[] schedule = BuildTweakSchedule(tweak);

        // XOR sector ^ schedule into scratch, one batched ECB pass, XOR back.
        // (scratch avoids aliasing questions in TransformBlock.)
        byte[] scratch = new byte[XtsSectorSize];
        for (int i = 0; i < XtsSectorSize; i++)
            scratch[i] = (byte)(data[offset + i] ^ schedule[i]);
        dataEncryptor.TransformBlock(scratch, 0, XtsSectorSize, scratch, 0);
        for (int i = 0; i < XtsSectorSize; i++)
            data[offset + i] = (byte)(scratch[i] ^ schedule[i]);
    }

    /// <summary>
    /// Decrypts one 0x1000-byte data unit in place. The tweak is the sector
    /// index as a 128-bit little-endian integer, advanced per 16-byte block.
    /// Creates its AES transforms per call — use the transform-accepting overload
    /// in hot loops.
    /// </summary>
    public static void XtsDecryptSector(byte[] data, int offset, ulong sector, byte[] dataKey, byte[] tweakKey)
    {
        using var tweakAes = Aes.Create();
        tweakAes.Mode = CipherMode.ECB;
        tweakAes.Padding = PaddingMode.None;
        tweakAes.Key = tweakKey;
        using var tweakEnc = tweakAes.CreateEncryptor();

        using var dataAes = Aes.Create();
        dataAes.Mode = CipherMode.ECB;
        dataAes.Padding = PaddingMode.None;
        dataAes.Key = dataKey;
        using var dataDec = dataAes.CreateDecryptor();

        XtsDecryptSector(data, offset, sector, dataDec, tweakEnc);
    }

    /// <summary>
    /// AES-XTS decryption of one 0x1000-byte data unit in place, using
    /// caller-supplied ECB transforms (see <see cref="XtsTransforms.Create"/>).
    /// Batched for the same reason as the encrypt overload — see the remarks
    /// on <see cref="XtsEncryptSector(byte[],int,ulong,ICryptoTransform,ICryptoTransform)"/>.
    /// </summary>
    public static void XtsDecryptSector(byte[] data, int offset, ulong sector,
        ICryptoTransform dataDecryptor, ICryptoTransform tweakEncryptor)
    {
        byte[] tweak = MakeTweak(sector);
        tweakEncryptor.TransformBlock(tweak, 0, 16, tweak, 0);
        byte[] schedule = BuildTweakSchedule(tweak);

        byte[] scratch = new byte[XtsSectorSize];
        for (int i = 0; i < XtsSectorSize; i++)
            scratch[i] = (byte)(data[offset + i] ^ schedule[i]);
        dataDecryptor.TransformBlock(scratch, 0, XtsSectorSize, scratch, 0);
        for (int i = 0; i < XtsSectorSize; i++)
            data[offset + i] = (byte)(scratch[i] ^ schedule[i]);
    }

    /// <summary>
    /// The raw 16-byte tweak for a sector: the sector index as a
    /// little-endian 128-bit integer.
    /// </summary>
    private static byte[] MakeTweak(ulong sector)
    {
        var tweak = new byte[16];
        for (int i = 0; i < 8; i++)
            tweak[i] = (byte)(sector >> (8 * i));
        return tweak;
    }

    /// <summary>
    /// Expands the encrypted tweak into the full 0x1000-byte schedule
    /// T_0 || T_1 || ... || T_255 by repeatedly multiplying by x (GF(2^128)).
    /// </summary>
    private static byte[] BuildTweakSchedule(byte[] tweak)
    {
        var schedule = new byte[XtsSectorSize];
        for (int blk = 0; blk < XtsSectorSize / 16; blk++)
        {
            Buffer.BlockCopy(tweak, 0, schedule, blk * 16, 16);
            GfMulByX(tweak);
        }
        return schedule;
    }

    /// <summary>
    /// Multiplies a 128-bit GF(2^128) value by x — the XTS tweak advance.
    /// The PS4 PFS uses the little-endian-first convention (as in GameArchives'
    /// XtsCryptStream): bits shift toward byte 15 and the reduction 0x87 is
    /// XORed into byte 0 (the least significant byte).
    /// </summary>
    private static void GfMulByX(byte[] v)
    {
        int feedback = 0;
        for (int k = 0; k < 16; k++)
        {
            byte tmp = v[k];
            v[k] = (byte)((v[k] << 1) | feedback);
            feedback = (tmp & 0x80) >> 7;
        }
        if (feedback != 0)
            v[0] ^= 0x87;
    }

    // ---- helpers ----

    private static byte[] HmacSha256(byte[] key, byte[] data)
    {
        using var hmac = new HMACSHA256(key);
        return hmac.ComputeHash(data);
    }

    private static byte[] Concat(byte[] a, byte[] b)
    {
        var r = new byte[a.Length + b.Length];
        Buffer.BlockCopy(a, 0, r, 0, a.Length);
        Buffer.BlockCopy(b, 0, r, a.Length, b.Length);
        return r;
    }

    private static byte[] LeBytes(uint v) =>
        new[] { (byte)v, (byte)(v >> 8), (byte)(v >> 16), (byte)(v >> 24) };

    private static ushort ReadLe16(byte[] b, int off) => (ushort)(b[off] | (b[off + 1] << 8));
    private static uint ReadLe32(byte[] b, int off) =>
        (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
    private static int ReadLe32Signed(byte[] b, int off) =>
        b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24);
    private static ulong ReadLe64(byte[] b, int off) =>
        ReadLe32(b, off) | ((ulong)ReadLe32(b, off + 4) << 32);
}

[Flags]
public enum PfsMode : ushort
{
    None = 0,
    Signed = 0x1,
    Is64Bit = 0x2,
    Encrypted = 0x4,
    UnknownFlagAlwaysSet = 0x8,
}

public sealed class PfsHeader
{
    public long Version;
    public long Magic;
    public long Id;
    public byte Fmode;
    public byte Clean;
    public byte ReadOnly;
    public byte Rsv;
    public PfsMode Mode;
    public ushort Unk1;
    public uint BlockSize;
    public uint NBackup;
    public long NBlock;
    public long DinodeCount;
    public long Ndblock;
    public long DinodeBlockCount;
    public byte[] Seed = new byte[16];
}

public sealed class PfsInode
{
    public ushort Mode;
    public ushort Nlink;
    public uint Flags;
    public long Size;
    public long SizeCompressed;
    public long Blocks;
    public readonly int[] DirectBlocks = new int[12];
    public readonly int[] IndirectBlocks = new int[5];

    public bool IsDirectory => (Mode & 0x4000) != 0;
    public int StartBlock => DirectBlocks[0];
}

public sealed class PfsDirent
{
    public uint InodeNumber;
    public int Type;
    public string Name;

    public PfsDirent(uint inodeNumber, int type, string name)
    {
        InodeNumber = inodeNumber;
        Type = type;
        Name = name;
    }
}

/// <summary>
/// Holds the three AES-ECB transforms needed for XTS encrypt/decrypt of an
/// entire PFS image: the tweak encryptor (always encryption of the raw sector
/// number), plus the data encryptor and data decryptor for the caller's
/// direction. Create once per PFS reader/writer and pass to the batched
/// <c>XtsEncryptSector</c>/<c>XtsDecryptSector</c> overloads.
/// </summary>
public sealed class XtsTransforms : IDisposable
{
    public readonly ICryptoTransform DataEncryptor;
    public readonly ICryptoTransform DataDecryptor;
    public readonly ICryptoTransform TweakEncryptor;

    private XtsTransforms(ICryptoTransform enc, ICryptoTransform dec, ICryptoTransform tweak)
    {
        DataEncryptor = enc;
        DataDecryptor = dec;
        TweakEncryptor = tweak;
    }

    /// <summary>Creates a bundle for the given data/tweak key pair.</summary>
    public static XtsTransforms Create(byte[] dataKey, byte[] tweakKey)
    {
        using var tweakAes = Aes.Create();
        tweakAes.Mode = CipherMode.ECB;
        tweakAes.Padding = PaddingMode.None;
        tweakAes.Key = tweakKey;

        using var dataAes = Aes.Create();
        dataAes.Mode = CipherMode.ECB;
        dataAes.Padding = PaddingMode.None;
        dataAes.Key = dataKey;

        // Transforms copy the key material — the Aes objects can be released.
        return new XtsTransforms(
            dataAes.CreateEncryptor(),
            dataAes.CreateDecryptor(),
            tweakAes.CreateEncryptor());
    }

    public void Dispose()
    {
        DataEncryptor.Dispose();
        DataDecryptor.Dispose();
        TweakEncryptor.Dispose();
    }
}
