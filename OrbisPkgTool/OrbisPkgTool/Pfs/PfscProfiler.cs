using System.Text.Json;
using OrbisPkgTool.Binary;

namespace OrbisPkgTool.Pfs;

/// <summary>Effective compression decision for one file, inferred from its
/// PFSC blocks: any compressed block → Enable; all allocated blocks raw →
/// Disable; no data blocks → None (empty files / directories have no policy).
/// This is the EFFECTIVE policy the original builder used, not necessarily
/// the GP4 attribute it was built from (an enabled-but-incompressible file
/// and a disabled file are indistinguishable from the final PFSC).</summary>
public enum PfscPolicy
{
    None = 0,
    Enable,
    Disable,
}

/// <summary>Per-file allocation and effective compression decision.</summary>
public sealed record PfscFilePolicy(string Path, long StartBlock, long BlockCount, PfscPolicy Policy);

/// <summary>Whole-image PFSC statistics (matches pfsc_profile.py output).</summary>
public sealed class PfscStats
{
    public long BlockSize { get; set; }
    public long BlockCount { get; set; }
    public long RoundedSize { get; set; }
    public long RawBlocks { get; set; }
    public long RawBytes { get; set; }
    public long CompressedBlocks { get; set; }
    public long CompressedBytes { get; set; }
    public long DataBytes => RawBytes + CompressedBytes;
}

/// <summary>
/// Extracts the original package's effective per-file compression policy.
///
/// Pipeline: PKG → outer PFS → pfs_image.dat (PFSC container, still
/// XTS-encrypted inside the outer PFS) → PFSC block table (per-block stored
/// size) + inner-PFS inode walk (path → allocated block range). A block is
/// "raw" iff its stored size equals the PFSC header's BlockSize.
///
/// The reader chain deliberately reuses PkgReader's proven outer-PFS +
/// XTS-decryption path (PfsFileStream) — the profiler only reads the PFSC
/// block TABLE (plaintext inside pfs_image.dat) plus inode metadata, never
/// the compressed payloads, so profiling a 30 GB game is cheap.
/// </summary>
public static class PfscProfiler
{
    /// <summary>
    /// Profiles the PFSC inside a PKG and infers the per-file policy.
    /// Returns null when the package has no PFSC-compressed inner PFS
    /// (e.g. a fully raw pfs_image.dat has no policy to replay).
    /// </summary>
    public static List<PfscFilePolicy>? Profile(string pkgPath, string passcode,
        out PfscStats stats, out string? profileError)
    {
        stats = new PfscStats();
        profileError = null;
        try
        {
            using var reader = new PkgReader(pkgPath, passcode);
            using var pfscStream = reader.OpenRawPfscStream();
            if (pfscStream == null)
            {
                profileError = "no PFSC-compressed pfs_image.dat in this package";
                return null;
            }
            // Open the inner PFS THROUGH this PFSC stream (the same stream the
            // block table is read from — PFSCStream seeks as needed).
            var inner = PfsReader.Open(new BigEndianReader(new PFSCStream(pfscStream)), 0);
            return Profile(pfscStream, inner, out stats);
        }
        catch (Exception ex)
        {
            profileError = ex.Message;
            return null;
        }
    }

    /// <summary>Core overload: a PFSC container stream + the already-open inner PFS reader.</summary>
    public static List<PfscFilePolicy> Profile(Stream pfsc, PfsReader inner, out PfscStats stats)
    {
        pfsc.Position = 0;
        var hdr = new byte[0x30];
        ReadFully(pfsc, hdr);
        if (hdr[0] != 'P' || hdr[1] != 'F' || hdr[2] != 'S' || hdr[3] != 'C')
            throw new InvalidDataException("not a PFSC container");
        long blockSize = BitConverter.ToInt64(hdr, 0x0C) & 0xFFFFFFFF;
        if (blockSize <= 0 || (blockSize & (blockSize - 1)) != 0)
            throw new InvalidDataException($"invalid PFSC block size {blockSize}");
        long tableOff = BitConverter.ToInt64(hdr, 0x18);
        long rounded = BitConverter.ToInt64(hdr, 0x28);
        long blockCount = rounded / blockSize;

        // Block table: (blockCount + 1) LE64 absolute offsets.
        var table = new byte[(blockCount + 1) * 8];
        pfsc.Position = tableOff;
        ReadFully(pfsc, table);
        var stored = new long[blockCount];
        for (long i = 0; i < blockCount; i++)
        {
            stored[i] = BitConverter.ToInt64(table, (int)(i * 8 + 8)) - BitConverter.ToInt64(table, (int)(i * 8));
            if (stored[i] < 0 || stored[i] > blockSize)
                throw new InvalidDataException($"PFSC block {i}: stored size {stored[i]} invalid");
        }

        stats = BuildStats(blockSize, blockCount, rounded, stored);

        // Inode walk: every regular file inode → (path, start, count, any-compressed).
        var result = new List<PfscFilePolicy>();
        var visited = new HashSet<uint>();
        WalkInner(inner, inner.UrootInode, "", result, stored, blockSize, blockCount, visited);
        return result;
    }

    private static PfscStats BuildStats(long blockSize, long blockCount, long rounded, long[] stored)
    {
        var s = new PfscStats
        {
            BlockSize = blockSize,
            BlockCount = blockCount,
            RoundedSize = rounded,
        };
        for (long i = 0; i < blockCount; i++)
        {
            if (stored[i] == blockSize) { s.RawBlocks++; s.RawBytes += stored[i]; }
            else { s.CompressedBlocks++; s.CompressedBytes += stored[i]; }
        }
        return s;
    }

    private static void WalkInner(PfsReader pfs, uint dirInode, string prefix,
        List<PfscFilePolicy> result, long[] stored, long blockSize, long blockCount, HashSet<uint> visited)
    {
        if (!visited.Add(dirInode)) return; // hard-link / cycle protection
        var dir = pfs.GetInode(dirInode);
        if (dir == null) return;
        foreach (var d in pfs.ReadDirents(dir))
        {
            if (d.Name is "." or ".." or "flat_path_table") continue;
            if (d.InodeNumber >= pfs.InodeCount) continue;
            var ino = pfs.GetInode(d.InodeNumber);
            if (ino == null) continue;
            string path = prefix.Length == 0 ? d.Name : prefix + "/" + d.Name;
            if (ino.IsDirectory)
            {
                WalkInner(pfs, d.InodeNumber, path, result, stored, blockSize, blockCount, visited);
                continue;
            }
            // Regular file: blocks = allocation count. Direct db[0] + run
            // sentinels cover our layout; full indirect walks for exotic
            // originals are handled by EnumerateFileBlocks.
            long start = ino.StartBlock;
            long count = ino.Blocks;
            PfscPolicy policy;
            if (count <= 0 || start <= 0)
                policy = PfscPolicy.None;
            else
            {
                bool anyCompressed = false;
                for (long b = start; b < start + count && b < blockCount; b++)
                {
                    if (stored[b] != blockSize) { anyCompressed = true; break; }
                }
                policy = anyCompressed ? PfscPolicy.Enable : PfscPolicy.Disable;
                if (start + count > blockCount)
                    policy = PfscPolicy.None; // allocation outside the PFSC — do not trust
            }
            result.Add(new PfscFilePolicy(path, start, count, policy));
        }
    }

    // ── profile (de)serialization ─────────────────────────────────────

    /// <summary>Normalizes an inner-PFS path for profile keying: lowercase,
    /// forward slashes, no Image0/ prefix, no trailing slash.</summary>
    public static string NormalizeKey(string path)
    {
        string p = path.Trim().Replace('\\', '/').TrimEnd('/');
        if (p.StartsWith("image0/", StringComparison.OrdinalIgnoreCase))
            p = p["image0/".Length..];
        return p.ToLowerInvariant();
    }

    /// <summary>Serializes the per-file policy list to the sidecar JSON
    /// format: {"version":1,"files":{"eboot.bin":"enable",...}}.</summary>
    public static string ToJson(IEnumerable<PfscFilePolicy> files)
    {
        var map = new Dictionary<string, string>();
        foreach (var f in files.Where(f => f.Policy != PfscPolicy.None))
            map[NormalizeKey(f.Path)] = f.Policy == PfscPolicy.Enable ? "enable" : "disable";
        return JsonSerializer.Serialize(new ProfileJson { Version = 1, Files = map },
            new JsonSerializerOptions { WriteIndented = true });
    }

    /// <summary>Parses a profile JSON back into normalized-path → policy.</summary>
    public static Dictionary<string, PfscPolicy> ParseJson(string json)
    {
        var doc = JsonSerializer.Deserialize<ProfileJson>(json)
            ?? throw new InvalidDataException("empty PFSC profile");
        var map = new Dictionary<string, PfscPolicy>();
        foreach (var (key, value) in doc.Files)
        {
            map[NormalizeKey(key)] = value switch
            {
                "enable" => PfscPolicy.Enable,
                "disable" => PfscPolicy.Disable,
                _ => throw new InvalidDataException($"unknown policy '{value}' for '{key}'"),
            };
        }
        return map;
    }

    private sealed class ProfileJson
    {
        public int Version { get; set; }
        public Dictionary<string, string> Files { get; set; } = [];
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
}
