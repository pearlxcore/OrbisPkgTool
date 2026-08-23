namespace OrbisPkgTool;

/// <summary>
/// One file or directory inside a PS4 PKG — the managed equivalent of an
/// orbis-pub-cmd img_file_list line.
/// </summary>
public sealed class PkgFileEntry
{
    /// <summary>Full path inside the PKG, e.g. "Image0/sce_sys/param.sfo" or "Sc0/npbind.dat".</summary>
    public string Path { get; set; } = "";

    /// <summary>File or directory name only, e.g. "param.sfo".</summary>
    public string Name { get; set; } = "";

    /// <summary>Original (decrypted) size in bytes.</summary>
    public long Size { get; set; }

    /// <summary>Size as stored inside the PKG (may differ when compressed).</summary>
    public long PackedSize { get; set; }

    public bool IsDirectory { get; set; }

    /// <summary>True when the entry data is AES-encrypted in the PKG.</summary>
    public bool IsEncrypted { get; set; }

    /// <summary>Numeric entry ID from the PKG entry table (0 for PFS-level files).</summary>
    public int EntryId { get; set; }

    /// <summary>Byte offset of the data in the PKG file (0 for synthesized directories).</summary>
    public long Offset { get; set; }

    /// <summary>
    /// The resolved PFS inode for Image0 entries — set during the tree walk
    /// so extraction reuses it instead of re-resolving the path through the
    /// dirent chain (O(1) instead of O(depth) per file, and dirent blocks
    /// beyond the PFSC metadata cache no longer re-decompress per file).
    /// Null for Sc0 entries and synthesized parent directories; callers
    /// must fall back to FindFile. The inode belongs to the reader's cached
    /// inner PFS and stays valid until the PkgReader is disposed.
    /// </summary>
    internal Pfs.PfsInode? Inode { get; set; }
}
