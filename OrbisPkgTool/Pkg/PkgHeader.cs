using OrbisPkgTool.Binary;

namespace OrbisPkgTool.Pkg;

/// <summary>
/// PS4 PKG file header (0x0000 - 0x05A0).
/// Layout reverse-engineered from orbis-pub-cmd.exe and the public PS4 PKG
/// format documentation; all integers are big-endian.
/// </summary>
public sealed class PkgHeader
{
    /// <summary>Magic bytes: 7F 43 4E 54 ("\x7FCNT").</summary>
    public const uint Magic = 0x7F434E54;

    public const int Size = 0x5A0;
    public const int ContentIdSize = 0x30;

    public uint MagicValue;
    public uint Flags;
    public uint Unk0x08;
    public uint Unk0x0C;
    public uint EntryCount;
    public ushort ScEntryCount;
    public ushort EntryCount2;
    public uint EntryTableOffset;
    public uint MainEntryDataSize;
    public ulong BodyOffset;
    public ulong BodySize;
    public string ContentId = "";
    public uint DrmType;
    public uint ContentType;
    public uint ContentFlags;
    public uint PromoteSize;
    public uint VersionDate;
    public uint VersionHash;
    public uint IroTag;
    public uint EkcVersion;

    public byte[] ScEntries1Hash = [];
    public byte[] ScEntries2Hash = [];
    public byte[] DigestTableHash = [];
    public byte[] BodyDigest = [];

    public uint PfsImageCount;
    public ulong PfsFlags;
    public ulong PfsImageOffset;
    public ulong PfsImageSize;
    public ulong MountImageOffset;
    public ulong MountImageSize;
    public ulong PackageSize;
    public uint PfsSignedSize;
    public uint PfsCacheSize;
    public byte[] PfsImageDigest = [];
    public byte[] PfsSignedDigest = [];
    public ulong PfsSplitSizeNth0;
    public ulong PfsSplitSizeNth1;

    /// <summary>Bit 31 of the flags field: package is finalized.</summary>
    public bool IsFinalized => (Flags & 0x80000000) != 0;

    /// <summary>True when the magic matches a valid PKG.</summary>
    public bool IsValid => MagicValue == Magic;

    public static PkgHeader Read(BigEndianReader r)
    {
        var h = new PkgHeader
        {
            MagicValue = r.ReadUInt32At(0x00),
            Flags = r.ReadUInt32At(0x04),
            Unk0x08 = r.ReadUInt32At(0x08),
            Unk0x0C = r.ReadUInt32At(0x0C),
            EntryCount = r.ReadUInt32At(0x10),
            EntryTableOffset = r.ReadUInt32At(0x18),
            MainEntryDataSize = r.ReadUInt32At(0x1C),
            BodyOffset = ReadUInt64At(r, 0x20),
            BodySize = ReadUInt64At(r, 0x28),
            DrmType = r.ReadUInt32At(0x70),
            ContentType = r.ReadUInt32At(0x74),
            ContentFlags = r.ReadUInt32At(0x78),
            PromoteSize = r.ReadUInt32At(0x7C),
            VersionDate = r.ReadUInt32At(0x80),
            VersionHash = r.ReadUInt32At(0x84),
            IroTag = r.ReadUInt32At(0x98),
            EkcVersion = r.ReadUInt32At(0x9C),
        };

        // 0x14: sc_entry_count (u16), 0x16: entry_count_2 (u16)
        h.ScEntryCount = r.ReadUInt16();
        h.EntryCount2 = r.ReadUInt16();

        // Content ID: 36 ASCII chars padded with nulls to 48 bytes at 0x40
        h.ContentId = ReadContentId(r);

        // Digest table
        h.ScEntries1Hash = r.ReadBytesAt(0x100, 32);
        h.ScEntries2Hash = r.ReadBytesAt(0x120, 32);
        h.DigestTableHash = r.ReadBytesAt(0x140, 32);
        h.BodyDigest = r.ReadBytesAt(0x160, 32);

        // PFS image info
        h.PfsImageCount = r.ReadUInt32At(0x404);
        h.PfsFlags = ReadUInt64At(r, 0x408);
        h.PfsImageOffset = ReadUInt64At(r, 0x410);
        h.PfsImageSize = ReadUInt64At(r, 0x418);
        h.MountImageOffset = ReadUInt64At(r, 0x420);
        h.MountImageSize = ReadUInt64At(r, 0x428);
        h.PackageSize = ReadUInt64At(r, 0x430);
        h.PfsSignedSize = r.ReadUInt32At(0x438);
        h.PfsCacheSize = r.ReadUInt32At(0x43C);
        h.PfsImageDigest = r.ReadBytesAt(0x440, 32);
        h.PfsSignedDigest = r.ReadBytesAt(0x460, 32);
        h.PfsSplitSizeNth0 = ReadUInt64At(r, 0x480);
        h.PfsSplitSizeNth1 = ReadUInt64At(r, 0x488);

        return h;
    }

    private static ulong ReadUInt64At(BigEndianReader r, long offset)
    {
        long old = r.Position;
        r.Position = offset;
        try { return r.ReadUInt64(); }
        finally { r.Position = old; }
    }

    private static string ReadContentId(BigEndianReader r)
    {
        long old = r.Position;
        r.Position = 0x40;
        try { return r.ReadAsciiNullTerminated(ContentIdSize).TrimEnd('\0'); }
        finally { r.Position = old; }
    }
}
