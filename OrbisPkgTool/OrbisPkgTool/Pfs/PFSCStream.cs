using System.IO.Compression;

namespace OrbisPkgTool.Pfs;

/// <summary>
/// PFSC (compressed PFS) header — the format of the pfs_image.dat file
/// inside a PS4 PKG's outer PFS. Blocks of <see cref="BlockSize"/> are
/// zlib-compressed (window bits 12); blocks whose compressed size equals
/// the block size are stored raw.
/// </summary>
public sealed class PFSCHeader
{
    public const uint Magic = 0x43534650; // "PFSC"
    public const int HeaderSize = 0x30;

    public uint MagicValue;
    public uint Unk1;
    public uint Unk2;
    public uint BlockSize;
    public uint Alignment;
    public uint Unk3;
    public ulong BlockTableOffset;
    public ulong BlockDataOffset;
    public ulong RoundedFileSize;

    public static PFSCHeader Read(byte[] data, int offset = 0)
    {
        return new PFSCHeader
        {
            MagicValue = ReadLe32(data, offset + 0x00),
            Unk1 = ReadLe32(data, offset + 0x04),
            Unk2 = ReadLe32(data, offset + 0x08),
            BlockSize = ReadLe32(data, offset + 0x0C),
            Alignment = ReadLe32(data, offset + 0x10),
            Unk3 = ReadLe32(data, offset + 0x14),
            BlockTableOffset = ReadLe64(data, offset + 0x18),
            BlockDataOffset = ReadLe64(data, offset + 0x20),
            RoundedFileSize = ReadLe64(data, offset + 0x28),
        };
    }

    private static uint ReadLe32(byte[] b, int off) =>
        (uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24));
    private static ulong ReadLe64(byte[] b, int off) =>
        ReadLe32(b, off) | ((ulong)ReadLe32(b, off + 4) << 32);
}

/// <summary>
/// A seekable read-only stream that decompresses a PFSC image on demand,
/// exposing the uncompressed inner PFS image.
/// </summary>
public sealed class PFSCStream : Stream
{
    private readonly Stream _source;
    private readonly PFSCHeader _header;
    private readonly long[] _blockOffsets;
    private readonly int _blockCount;
    private readonly long _length;
    private long _position;
    private readonly byte[]?[] _cache;
    private const int MetaCacheSlots = 64; // cache first 64 blocks only (inode table + dirents)

    public PFSCStream(Stream source)
    {
        _source = source;
        var headerBytes = new byte[PFSCHeader.HeaderSize];
        source.Position = 0;
        ReadFully(source, headerBytes);
        _header = PFSCHeader.Read(headerBytes);
        if (_header.MagicValue != PFSCHeader.Magic)
            throw new InvalidDataException("Not a PFSC image (bad magic).");

        int blockSize = (int)_header.BlockSize;
        _blockCount = (int)((_header.RoundedFileSize + (ulong)blockSize - 1) / (ulong)blockSize);
        _blockOffsets = new long[_blockCount + 1];
        source.Position = (long)_header.BlockTableOffset;
        var table = new byte[(_blockCount + 1) * 8];
        ReadFully(source, table);
        for (int i = 0; i <= _blockCount; i++)
            _blockOffsets[i] = (long)ReadLe64(table, i * 8);
        _length = (long)_header.RoundedFileSize;
        _cache = new byte[MetaCacheSlots][];
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
            long blockIndex = _position / _header.BlockSize;
            int inBlock = (int)(_position % _header.BlockSize);
            byte[] block = ReadBlock((int)blockIndex);
            int take = (int)Math.Min(Math.Min(count, block.Length - inBlock), _length - _position);
            if (take <= 0) break; // guard against zero-length blocks
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

    private byte[] ReadBlock(int index)
    {
        // Cache metadata blocks (first 64 blocks: header, inodes, dirents)
        if (index < MetaCacheSlots && _cache[index] != null)
            return _cache[index]!;

        long start = _blockOffsets[index];
        long end = _blockOffsets[index + 1];
        int compressedSize = (int)(end - start);
        int blockSize = (int)_header.BlockSize;

        _source.Position = start;
        var compressed = new byte[compressedSize];
        try
        {
            ReadFully(_source, compressed);
        }
        catch (EndOfStreamException)
        {
            throw new EndOfStreamException(
                $"PFSC block {index}: read {compressedSize} bytes at 0x{start:X} " +
                $"(file length {_source.Length}); offsets may be relative to the outer PFS.");
        }

        if (compressedSize == blockSize)
            return compressed; // stored raw

        // Last block may be shorter than blockSize after decompression.
        long totalBlocks = _blockCount;
        int expected = (index + 1 < totalBlocks)
            ? blockSize
            : (int)(_header.RoundedFileSize - (ulong)((totalBlocks - 1) * blockSize));
        var output = new byte[expected];
        // Detect format: zlib-wrapped (RFC 1950) starts with 0x78; raw deflate doesn't.
        using var decompressed = new MemoryStream(compressed);
        bool isZlib = compressed.Length >= 2 && (compressed[0] & 0x0F) == 0x08;
        using var decompressor = isZlib
            ? (Stream)new ZLibStream(decompressed, CompressionMode.Decompress, leaveOpen: true)
            : new DeflateStream(decompressed, CompressionMode.Decompress, leaveOpen: true);
        int read = 0;
        while (read < expected)
        {
            int n = decompressor.Read(output, read, expected - read);
            if (n <= 0) break;
            read += n;
        }
        if (read < expected)
            Array.Resize(ref output, read);
        // Only cache first 64 blocks (metadata: header, inodes, dirents)
        if (index < MetaCacheSlots) _cache[index] = output;
        return output;
    }

    private static void ReadFully(Stream s, byte[] buffer)
    {
        int read = 0;
        while (read < buffer.Length)
        {
            int n = s.Read(buffer, read, buffer.Length - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
    }

    private static ulong ReadLe64(byte[] b, int off) =>
        (ulong)(uint)(b[off] | (b[off + 1] << 8) | (b[off + 2] << 16) | (b[off + 3] << 24)) |
        ((ulong)(uint)(b[off + 4] | (b[off + 5] << 8) | (b[off + 6] << 16) | (b[off + 7] << 24)) << 32);
}
