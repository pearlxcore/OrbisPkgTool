namespace OrbisPkgTool.Pfs;

/// <summary>
/// Central registry of empirically verified PS4 PKG/PFS/PFSC format constants.
///
/// Origin classification:
///   [Sony]      verified against orbis-pub-cmd 3.87 output and real FPKGs
///               (Digimon World: Next Order, MediEvil, Adventure Time)
///   [OpenOrbis] matches the OpenOrbis/LibOrbisPkg independent implementation
///   [Derived]   follows from the format definition / arithmetic
///   [Choice]    our implementation choice (not required by the format)
///
/// The compatibility core is FROZEN (commit 97dcfda): do not change any
/// [Sony] value without a regression or console test proving a defect.
/// </summary>
public static class PfsFormat
{
    // ── PFS ──────────────────────────────────────────────────────────────
    /// <summary>PFS block size (all PFS variants). [Sony]</summary>
    public const long BlockSize = 0x10000;

    /// <summary>XTS data-unit (sector) size. [Sony]</summary>
    public const int XtsSectorSize = 0x1000;

    /// <summary>PFS header version. [Sony]</summary>
    public const long PfsVersion = 1;

    /// <summary>PFS magic (20130315 decimal). [Sony]</summary>
    public const long PfsMagic = 20130315;

    /// <summary>Inner PFS mode (unseeded, plaintext, D32 inodes). [Sony]</summary>
    public const ushort InnerMode = 0x8;

    /// <summary>Outer PFS mode (seeded, XTS-encrypted, signed, S32 inodes). [Sony]</summary>
    public const ushort OuterMode = 0xD;

    /// <summary>Inode-table dinode lives at header offset 0x50. [Sony]</summary>
    public const int HeaderDinodeOffset = 0x50;

    /// <summary>Inode table starts at block 1 (both PFS variants). [Sony]</summary>
    public const long InodeTableStartBlock = 1;

    /// <summary>D32 inode size (inner PFS). [Sony]</summary>
    public const int D32InodeSize = 0xA8;

    /// <summary>S32 inode size (outer PFS). [Sony]</summary>
    public const int S32InodeSize = 0x2C8;

    /// <summary>Direct-block pointer slots per inode. [Derived]</summary>
    public const int DirectBlockCount = 12;

    /// <summary>Indirect-block pointer slots per inode. [Derived]</summary>
    public const int IndirectBlockCount = 5;

    /// <summary>Direct pointers start at inode + 0x64. [Sony]</summary>
    public const int DirectPointersOffset = 0x64;

    /// <summary>Indirect pointers start at inode + 0x94 (D32) / +0x214 (S32). [Sony]</summary>
    public const int D32IndirectPointersOffset = 0x94;
    public const int S32IndirectPointersOffset = 0x214;

    /// <summary>S32 slot stride (32-byte signature + 4-byte block). [Sony]</summary>
    public const int S32SlotSize = 36;

    /// <summary>D32 slot stride (4-byte block only). [Sony]</summary>
    public const int D32SlotSize = 4;

    /// <summary>FPT record size (u32 hash + u32 inode|flags). [Sony]</summary>
    public const int FptEntrySize = 8;

    /// <summary>FPT inode-field flag bits: upper 4 bits (dir = 0x2, file = 0). [Sony]</summary>
    public const uint FptFlagMask = 0xF0000000;
    public const uint FptDirFlag = 0x20000000;

    /// <summary>
    /// Contiguous-run sentinel for file data pointers: 0xFFFFFFFF means
    /// "the remaining blocks follow contiguously from the previous pointer".
    /// Real orbis uses this in the inner PFS for multi-block files
    /// (verified: archive.psarc, 3629 blocks, db[0] only + -1 sentinels).
    /// [Sony]
    /// </summary>
    public const int ContiguousRunSentinel = -1; // 0xFFFFFFFF

    /// <summary>
    /// Inode-table packing rule: an inode never straddles a block boundary;
    /// if it does not fit in the remaining space, skip to the next block.
    /// (Verified against the real Digimon: inode 390 sits at block 2.)
    /// [Sony]
    /// </summary>
    public static bool InodeFitsInBlock(long pos, int inodeSize) =>
        pos % BlockSize <= BlockSize - inodeSize;

    /// <summary>Inodes per D32 block (65536 / 0xA8 = 390, 16 bytes wasted). [Derived]</summary>
    public const int D32InodesPerBlock = (int)(BlockSize / D32InodeSize);

    /// <summary>Pointers per indirect block (65536 / 4 for D32). [Derived]</summary>
    public const int PointersPerIndirectBlock = (int)(BlockSize / D32SlotSize);

    // ── PFSC ─────────────────────────────────────────────────────────────
    /// <summary>PFSC magic "PFSC". [Sony]</summary>
    public static ReadOnlySpan<byte> PfscMagicBytes => "PFSC"u8;

    /// <summary>PFSC unk4 = 0 (required by LibOrbisPkg PFSCReader). [OpenOrbis]</summary>
    public const uint PfscUnk4 = 0;

    /// <summary>PFSC unk8 = 6 (MkPFS/LibOrbisPkg value). [OpenOrbis]</summary>
    public const uint PfscUnk8 = 6;

    /// <summary>PFSC block table offset. [Sony]</summary>
    public const int PfscTableOffset = 0x400;

    /// <summary>PFSC data start offset — must be block-aligned (0x10000). [Sony]</summary>
    public const long PfscDataOffset = 0x10000;

    /// <summary>PFSC block table entry size. [Derived]</summary>
    public const int PfscTableEntrySize = 8;

    /// <summary>
    /// Compressed PFSC sector = COMPLETE RFC1950 zlib stream:
    /// 0x48 0x89 (CMF/FLG, CINFO=4 → 4KiB window) + raw deflate + 4-byte
    /// big-endian Adler32 of the decompressed block. [Sony]
    /// </summary>
    public const int PfscZlibHeaderSize = 2;
    public const int PfscAdler32Size = 4;
    public static ReadOnlySpan<byte> PfscZlibHeader => [0x48, 0x89];

    /// <summary>zlib deflate level matching orbis output byte-for-byte. [Sony]</summary>
    public const int PfscDeflateLevel = 6;

    // ── PKG ──────────────────────────────────────────────────────────────
    /// <summary>PKG magic 0x7F434E54 ("\x7fCNT"). [Sony]</summary>
    public const uint PkgMagic = 0x7F434E54;

    /// <summary>PKG entry table offset. [Sony]</summary>
    public const uint PkgTableOffset = 0x2A80;

    /// <summary>PKG body (entry data) start. [Sony]</summary>
    public const uint PkgBodyOffset = 0x2000;

    /// <summary>EntryKeys entry offset/size. [Sony]</summary>
    public const uint EntryKeysOffset = 0x2000;
    public const uint EntryKeysSize = 2048;

    /// <summary>ImageKey entry offset/size. [Sony]</summary>
    public const uint ImageKeyOffset = 0x2800;
    public const uint ImageKeySize = 256;

    /// <summary>GeneralDigests entry offset/size. [Sony]</summary>
    public const uint GeneralDigestsOffset = 0x2900;
    public const uint GeneralDigestsSize = 0x180;

    /// <summary>PKG entry record size. [Derived]</summary>
    public const int PkgEntrySize = 32;

    /// <summary>PKG header size (excluding RSA signature). [Derived]</summary>
    public const int PkgHeaderSize = 0x1000;

    /// <summary>RSA signature size at header + 0x1000. [Derived]</summary>
    public const int PkgSignatureSize = 256;

    /// <summary>pfs_image_offset must be 0x80000-aligned. [Sony]</summary>
    public const long PfsImageAlignment = 0x80000;

    /// <summary>Minimum PKG size. [Derived]</summary>
    public const long MinPkgSize = 0x100000;

    /// <summary>Final PKG size alignment. [Derived]</summary>
    public const long PkgSizeAlignment = 0x8000;

    /// <summary>main_ent_data_size = EntryKeys + ImageKey + GeneralDigests + 2×table. [Derived]</summary>
    public static uint MainEntDataSize(int entryCount) =>
        EntryKeysSize + ImageKeySize + GeneralDigestsSize + 2u * (uint)(entryCount * PkgEntrySize);

    // Digest / signature slots in the PKG header [Sony — offsets verified by
    // fixdigests parity with PS4_Tools PkgBuilder]
    public const int HdrScEntries1Hash = 0x100;   // SHA256(entrykeys|imagekey|gd|metas|digests)
    public const int HdrScEntries2Hash = 0x120;   // SHA256(entrykeys|imagekey|gd|metas[6*32])
    public const int HdrDigestTableHash = 0x140;  // SHA256(digests entry data)
    public const int HdrBodyDigest = 0x160;       // SHA256(pkg[0x2000 .. pfsOffset))
    public const int HdrPfsImageDigest = 0x440;   // SHA256(whole outer PFS)
    public const int HdrPfsSignedDigest = 0x460;  // SHA256(outer PFS first 0x10000)
    public const int HdrHeaderDigest = 0xFE0;     // SHA256(header[0..0xFE0))
    public const int HdrSignature = 0x1000;       // RSA2048(header[0..0x1000] sha256)

    // ── Build pipeline [Choice] ──────────────────────────────────────────
    /// <summary>Files bigger than this use the temp-file streaming pipeline.</summary>
    public const long StreamingThreshold = 1_000_000_000L;

    /// <summary>Peak temp-disk multiplier over the inner PFS size (inner+PFSC+outer). [Choice]</summary>
    public const double TempDiskMultiplier = 3.2;
}
