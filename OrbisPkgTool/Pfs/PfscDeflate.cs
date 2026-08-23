using System.Runtime.InteropServices;

namespace OrbisPkgTool.Pfs;

/// <summary>
/// Raw-deflate compressor with a 4 KiB history window, backed by zlib1.dll.
///
/// PFSC compressed blocks declare zlib CMF=0x48 (CINFO=4 → 4096-byte window)
/// in their two-byte header. A deflate stream produced with the default
/// 32 KiB window (SharpZipLib) can emit back-references at distances up to
/// 32 KiB, which a decoder that strictly honors the declared window would
/// reject. zlib's deflateInit2(windowBits: -12) emits raw deflate that can
/// never reference more than 4096 bytes back, making the stream formally
/// valid for the declared header.
///
/// DLL discovery order: ORBISPKG_ZLIB env var → zlib1.dll next to the
/// executable → Git for Windows mingw64 → the system PATH. When no zlib is
/// found, <see cref="IsAvailable"/> is false and callers fall back to
/// SharpZipLib (the previously proven behavior — zlib-family decoders accept
/// 32 KiB-window streams in practice because inflate only rejects distances
/// beyond its CONFIGURED window, not the declared one).
/// </summary>
public static class PfscDeflate
{
    // z_stream (Windows ABI: unsigned long = 32 bits) — layout verified
    // against the working inflate P/Invoke in the CLI's shadPS4 replica.
    [StructLayout(LayoutKind.Sequential)]
    private struct ZStream
    {
        public IntPtr next_in;
        public uint avail_in;
        public uint total_in;
        public IntPtr next_out;
        public uint avail_out;
        public uint total_out;
        public IntPtr msg;
        public IntPtr state;
        public IntPtr zalloc;
        public IntPtr zfree;
        public IntPtr opaque;
        public int data_type;
        public uint adler;
        public uint reserved;
    }

    private const int ZOk = 0;
    private const int ZStreamEnd = 1;
    private const int ZFinish = 4;
    private const int ZDeflated = 8;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DeflateInit2(ref ZStream strm, int level, int method, int windowBits,
        int memLevel, int strategy, [MarshalAs(UnmanagedType.LPStr)] string version, int streamSize);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int Deflate(ref ZStream strm, int flush);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int DeflateEnd(ref ZStream strm);

    private static readonly Lazy<bool> _loaded = new(Load, LazyThreadSafetyMode.ExecutionAndPublication);
    private static DeflateInit2? _deflateInit2;
    private static Deflate? _deflate;
    private static DeflateEnd? _deflateEnd;

    /// <summary>True when zlib1.dll was found and its exports resolved.</summary>
    public static bool IsAvailable => _loaded.Value;

    private static bool Load()
    {
        try
        {
            IntPtr lib = IntPtr.Zero;
            string? env = Environment.GetEnvironmentVariable("ORBISPKG_ZLIB");
            if (!string.IsNullOrEmpty(env) && File.Exists(env))
                NativeLibrary.TryLoad(env, out lib);
            if (lib == IntPtr.Zero)
                NativeLibrary.TryLoad(Path.Combine(AppContext.BaseDirectory, "zlib1.dll"), out lib);
            if (lib == IntPtr.Zero)
                NativeLibrary.TryLoad(@"C:\Program Files\Git\mingw64\bin\zlib1.dll", out lib);
            if (lib == IntPtr.Zero)
                NativeLibrary.TryLoad("zlib1", out lib);
            if (lib == IntPtr.Zero) return false;

            _deflateInit2 = Marshal.GetDelegateForFunctionPointer<DeflateInit2>(
                NativeLibrary.GetExport(lib, "deflateInit2_"));
            _deflate = Marshal.GetDelegateForFunctionPointer<Deflate>(
                NativeLibrary.GetExport(lib, "deflate"));
            _deflateEnd = Marshal.GetDelegateForFunctionPointer<DeflateEnd>(
                NativeLibrary.GetExport(lib, "deflateEnd"));
            return true;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Compresses <paramref name="input"/>[<paramref name="offset"/>,
    /// offset+<paramref name="count"/>) as RAW deflate with a 4 KiB window
    /// (windowBits = -12), level 6. Returns the number of bytes written to
    /// <paramref name="output"/>, or -1 when zlib is unavailable or the
    /// output did not fit (the caller should then store the block raw).
    /// </summary>
    public static int TryDeflate4K(byte[] input, int offset, int count, byte[] output)
    {
        if (!_loaded.Value || _deflateInit2 == null || _deflate == null || _deflateEnd == null)
            return -1;
        if (offset < 0 || count < 0 || offset + count > input.Length)
            throw new ArgumentOutOfRangeException(nameof(offset));
        // Each call creates its own z_stream — no shared mutable state — so
        // concurrent calls are safe without a lock. The function pointers
        // (_deflateInit2, _deflate, _deflateEnd) are immutable after Load().
        var z = new ZStream();
        // zlib only compares the major version char and sizeof(z_stream).
        if (_deflateInit2(ref z, PfsFormat.PfscDeflateLevel, ZDeflated, -12, 8, 0,
                "1.2.13", Marshal.SizeOf<ZStream>()) != ZOk)
            return -1;
        var inPin = GCHandle.Alloc(input, GCHandleType.Pinned);
        var outPin = GCHandle.Alloc(output, GCHandleType.Pinned);
        try
        {
            z.next_in = IntPtr.Add(inPin.AddrOfPinnedObject(), offset);
            z.avail_in = (uint)count;
            z.next_out = outPin.AddrOfPinnedObject();
            z.avail_out = (uint)output.Length;
            int rc = _deflate(ref z, ZFinish);
            while (rc == ZOk && z.avail_out > 0)
                rc = _deflate(ref z, ZFinish);
            if (rc != ZStreamEnd)
                return -1; // ran out of output — treat as incompressible
            return (int)z.total_out;
        }
        finally
        {
            outPin.Free();
            inPin.Free();
            _deflateEnd(ref z);
        }
    }
}
