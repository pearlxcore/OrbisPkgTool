using System.Buffers.Binary;
using System.Text;

namespace OrbisPkgTool.Binary;

/// <summary>
/// Big-endian binary reader over a stream. The PS4 PKG format is big-endian
/// (all multi-byte integers stored MSB first).
/// </summary>
public sealed class BigEndianReader
{
    private readonly Stream _stream;
    private readonly byte[] _buf = new byte[8];

    public BigEndianReader(Stream stream) => _stream = stream;

    public long Position
    {
        get => _stream.Position;
        set => _stream.Position = value;
    }

    public long Length => _stream.Length;

    public long Seek(long offset) => _stream.Seek(offset, SeekOrigin.Begin);

    public byte ReadUInt8()
    {
        int b = _stream.ReadByte();
        if (b < 0) throw new EndOfStreamException();
        return (byte)b;
    }

    public ushort ReadUInt16() => BinaryPrimitives.ReadUInt16BigEndian(ReadBytes(2));
    public uint ReadUInt32() => BinaryPrimitives.ReadUInt32BigEndian(ReadBytes(4));
    public ulong ReadUInt64() => BinaryPrimitives.ReadUInt64BigEndian(ReadBytes(8));

    public uint ReadUInt32At(long offset)
    {
        long old = _stream.Position;
        _stream.Position = offset;
        try { return ReadUInt32(); }
        finally { _stream.Position = old; }
    }

    public byte[] ReadBytes(int count)
    {
        var data = new byte[count];
        int read = 0;
        while (read < count)
        {
            int n = _stream.Read(data, read, count - read);
            if (n <= 0) throw new EndOfStreamException();
            read += n;
        }
        return data;
    }

    public byte[] ReadBytesAt(long offset, int count)
    {
        long old = _stream.Position;
        _stream.Position = offset;
        try { return ReadBytes(count); }
        finally { _stream.Position = old; }
    }

    /// <summary>Reads exactly <paramref name="count"/> bytes at an absolute
    /// offset into a caller-provided buffer, avoiding a temporary allocation.</summary>
    public void ReadBytesAt(long offset, byte[] buffer, int bufferOffset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (bufferOffset < 0 || count < 0 || bufferOffset > buffer.Length - count)
            throw new ArgumentOutOfRangeException(nameof(count));

        long old = _stream.Position;
        _stream.Position = offset;
        try
        {
            int read = 0;
            while (read < count)
            {
                int n = _stream.Read(buffer, bufferOffset + read, count - read);
                if (n <= 0) throw new EndOfStreamException();
                read += n;
            }
        }
        finally { _stream.Position = old; }
    }

    /// <summary>Reads a null-terminated ASCII string, bounded by <paramref name="maxLength"/>.</summary>
    public string ReadAsciiNullTerminated(int maxLength = 512)
    {
        var sb = new StringBuilder(maxLength);
        int b;
        while (sb.Length < maxLength && (b = _stream.ReadByte()) > 0)
            sb.Append((char)b);
        return sb.ToString();
    }
}
