using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace OrbisPkgTool.Trp;

/// <summary>One entry in a PS4 trophy pack (TRP).</summary>
public sealed class TrpEntry
{
    public string Name = "";
    public uint DataOffset;
    public uint DataSize;
    public byte[] Data = [];
}

/// <summary>
/// PS4 trophy pack (TRP) reader/writer — the orbis-pub-trp equivalent.
/// Format (big-endian, validated against real trophy00.trp files):
///   0x60-byte header (v3): magic DCA24D00, version 3, file_size u64,
///   entry_count, element_size 0x40, SHA1 of the whole file, "010",
///   then count × 0x40 entries (name[32], offset u32, size u32, flags 16)
///   then raw data, each entry 16-byte aligned (not compressed).
/// </summary>
public static class Trp
{
    public const uint Magic = 0xDCA24D00;
    public const int HeaderSize = 0x60;
    public const int EntrySize = 0x40;

    public static List<TrpEntry> Read(string path) => Read(File.ReadAllBytes(path));

    public static List<TrpEntry> Read(byte[] data)
    {
        if (data.Length < HeaderSize || ReadBe32(data, 0) != Magic)
            throw new InvalidDataException("Not a PS4 TRP file (bad magic).");
        int count = (int)ReadBe32(data, 0x10);
        if (count <= 0 || count > 4096 || HeaderSize + (long)count * EntrySize > data.Length)
            throw new InvalidDataException("Invalid TRP entry count.");

        var entries = new List<TrpEntry>(count);
        for (int i = 0; i < count; i++)
        {
            int off = HeaderSize + i * EntrySize;
            string name = Encoding.ASCII.GetString(data, off, 32).TrimEnd('\0');
            uint offset = ReadBe32(data, off + 0x24);
            uint size = ReadBe32(data, off + 0x2C);
            if (offset + size > data.Length)
                throw new InvalidDataException($"TRP entry '{name}' is out of bounds.");
            entries.Add(new TrpEntry
            {
                Name = name,
                DataOffset = offset,
                DataSize = size,
                Data = data[(int)offset..(int)(offset + size)],
            });
        }
        return entries;
    }

    /// <summary>Writes a TRP file (version 3, "010") with raw 16-aligned data blocks.</summary>
    public static byte[] Write(List<TrpEntry> entries)
    {
        var ordered = SortEntries(entries);
        int count = ordered.Count;

        long tableEnd = HeaderSize + (long)count * EntrySize;
        var offsets = new uint[count];
        long pos = tableEnd;
        for (int i = 0; i < count; i++)
        {
            offsets[i] = (uint)pos;
            pos += Align16(ordered[i].Data.Length);
        }
        long totalSize = pos;

        using var ms = new MemoryStream();
        var w = new BinaryWriter(ms);
        WriteBe32(w, Magic);
        WriteBe32(w, 3);                    // version
        WriteBe64(w, (ulong)totalSize);     // file size
        WriteBe32(w, (uint)count);
        WriteBe32(w, EntrySize);            // element size
        WriteBe32(w, 0);                    // dev flag
        w.Write(new byte[20]);              // SHA1 (patched below)
        w.Write(Encoding.ASCII.GetBytes("010\0"));
        w.Write(new byte[0x60 - 0x34]);     // padding to 0x60

        for (int i = 0; i < count; i++)
        {
            var e = ordered[i];
            var name = Encoding.ASCII.GetBytes(e.Name);
            Array.Resize(ref name, 32);
            w.Write(name);
            WriteBe32(w, 0);
            WriteBe32(w, offsets[i]);
            WriteBe32(w, 0);
            WriteBe32(w, (uint)e.Data.Length);
            // 16-byte flags block: 3 for ESFM, 1 for SFM, 0 otherwise (big-endian u32 + zeros)
            var flags = new byte[16];
            if (e.Name.EndsWith(".ESFM", StringComparison.OrdinalIgnoreCase))
                flags[3] = 3;
            else if (e.Name.EndsWith(".SFM", StringComparison.OrdinalIgnoreCase))
                flags[3] = 1;
            w.Write(flags);
        }

        for (int i = 0; i < count; i++)
        {
            w.Write(ordered[i].Data);
            int pad = Align16(ordered[i].Data.Length) - ordered[i].Data.Length;
            w.Write(new byte[pad]);
        }

        var file = ms.ToArray();
        // SHA1 of the whole file (hash field zeroed) at 0x1C.
        var hash = SHA1.HashData(file);
        Buffer.BlockCopy(hash, 0, file, 0x1C, 20);
        return file;
    }

    /// <summary>Orders entries the way the console expects: TROPCONF, TROP*.SFM/ESFM, ICON*, GR*, TROPxxx.PNG.</summary>
    public static List<TrpEntry> SortEntries(IEnumerable<TrpEntry> entries)
    {
        string[] patterns =
        {
            @"^TROPCONF\.(E?SFM)$",
            @"^TROP\.(E?SFM)$",
            @"^TROP_\d+\.(E?SFM)$",
            @"^ICON0\.PNG$",
            @"^ICON0_\d+\.PNG$",
            @"^GR\d+\.PNG$",
            @"^GR\d+_\d+\.PNG$",
            @"^TROP\d+\.PNG$",
        };
        var result = new List<TrpEntry>();
        foreach (var p in patterns)
            result.AddRange(entries
                .Where(e => Regex.IsMatch(e.Name, p, RegexOptions.IgnoreCase))
                .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase));
        // Append any entries that didn't match any pattern
        var matched = new HashSet<string>(result.Select(e => e.Name));
        result.AddRange(entries
            .Where(e => !matched.Contains(e.Name))
            .OrderBy(e => e.Name, StringComparer.OrdinalIgnoreCase));
        return result;
    }

    private static int Align16(int n) => (n + 15) & ~15;

    private static uint ReadBe32(byte[] b, int o) =>
        (uint)((b[o] << 24) | (b[o + 1] << 16) | (b[o + 2] << 8) | b[o + 3]);

    private static void WriteBe32(BinaryWriter w, uint v) =>
        w.Write(new[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });

    private static void WriteBe64(BinaryWriter w, ulong v) =>
        w.Write(new[]
        {
            (byte)(v >> 56), (byte)(v >> 48), (byte)(v >> 40), (byte)(v >> 32),
            (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v,
        });
}
