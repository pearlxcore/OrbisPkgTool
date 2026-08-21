using System.Buffers.Binary;
using System.Text;
using OrbisPkgTool.Binary;
using OrbisPkgTool.Pkg;

namespace OrbisPkgTool.Pkg;

/// <summary>
/// Produces the legacy 46-row (Type, Value) dump of a PKG header that
/// PS4_Tools.PKG.SceneRelated.PS4_Struct exposed via DisplayType() /
/// DisplayValue(). The rows are read from the raw PKG header bytes
/// (big-endian) using the exact field order of the decompiled
/// <c>ReadHeader</c>, so the grid shown by PS4PKGTool's "Advanced PKG
/// Header" panel is byte-identical before and after the migration.
/// </summary>
public static class PkgHeaderDump
{
    // The PKG header region read by the legacy decoder: 0x000..0x0A0
    // followed by the digest table at 0x100, the unk/pfs block at
    // 0x400, and the tail digest at 0xFE0. We require 0x1100 bytes
    // (PkgFieldDump's window) so every row is in scope.
    public const int HeaderBytes = 0x1100;

    /// <summary>The 46 legacy row labels, in order (matches DisplayType,
    /// including its quirks: "pkg_body_offset:" and "pkg_body_size: 0x").</summary>
    public static IReadOnlyList<string> RowTypes { get; } = new[]
    {
        "pkg_magic", "pkg_type", "pkg_0x008", "pkg_file_count",
        "pkg_entry_count", "pkg_sc_entry_count", "pkg_entry_count_2",
        "pkg_table_offset", "pkg_entry_data_size",
        "pkg_body_offset:", "pkg_body_size: 0x",
        "pkg_content_offset", "pkg_content_size",
        "pkg_content_id", "pkg_padding",
        "pkg_drm_type", "pkg_content_type", "pkg_content_flags",
        "pkg_promote_size", "pkg_version_date", "pkg_version_hash",
        "pkg_0x088", "pkg_0x08C", "pkg_0x090", "pkg_0x094",
        "pkg_iro_tag", "ekc_version",
        "digest_entries1", "digest_entries2", "digest_table_digest",
        "digest_body_digest",
        "unk_0x400",
        "pfs_image_count", "pfs_image_flags", "pfs_image_offset",
        "pfs_image_size", "mount_image_offset", "mount_image_size",
        "pkg_size", "pfs_signed_size", "pfs_cache_size",
        "pfs_image_digest", "pfs_signed_digest",
        "pfs_split_size_nth_0", "pfs_split_size_nth_1",
        "pkg_digest",
    };

    /// <summary>
    /// Returns the (Type, Value) rows for <paramref name="header"/> using the
    /// raw header bytes <paramref name="h"/> (at least <see cref="HeaderBytes"/>
    /// long). Values are formatted exactly as the legacy DisplayValue did:
    /// magic as ASCII, hex fields with "0x" prefix or bare X, integer counts
    /// as decimal, byte arrays as lower-case hex strings, drm_type as
    /// "PS4"/"Unknown", iro_tag as "None" when 0.
    /// </summary>
    public static List<(string Type, string Value)> Rows(PkgHeader header, byte[] h)
    {
        if (h is null) throw new ArgumentNullException(nameof(h));
        if (h.Length < HeaderBytes)
            throw new ArgumentException(
                $"header buffer must be at least 0x{HeaderBytes:X} bytes", nameof(h));

        var rows = new List<(string, string)>(47);

        // 0x00..0x0C — magic, type/flags, 0x008, file_count.
        rows.Add(("pkg_magic", Ascii(h, 0x00, 4)));
        rows.Add(("pkg_type", U32(h, 0x04).ToString()));
        rows.Add(("pkg_0x008", U32(h, 0x08).ToString("X")));
        rows.Add(("pkg_file_count", U32(h, 0x0C).ToString()));

        // 0x10..0x1C — entry_count, sc_entry_count, entry_count_2,
        // table_offset, entry_data_size.
        rows.Add(("pkg_entry_count", U32(h, 0x10).ToString()));
        rows.Add(("pkg_sc_entry_count", U16(h, 0x14).ToString()));
        rows.Add(("pkg_entry_count_2", U16(h, 0x16).ToString()));
        rows.Add(("pkg_table_offset", "0x" + U32(h, 0x18).ToString("X")));
        rows.Add(("pkg_entry_data_size", "0x" + U32(h, 0x1C).ToString("X")));

        // 0x20..0x38 — body_offset, body_size, content_offset, content_size.
        rows.Add(("pkg_body_offset:", "0x" + U64(h, 0x20).ToString("X")));
        rows.Add(("pkg_body_size: 0x", "0x" + U64(h, 0x28).ToString("X")));
        rows.Add(("pkg_content_offset", "0x" + U64(h, 0x30).ToString("X")));
        rows.Add(("pkg_content_size", "0x" + U64(h, 0x38).ToString("X")));

        // 0x40..0x6C — content_id (36 ASCII), padding (12 ASCII).
        rows.Add(("pkg_content_id", Ascii(h, 0x40, 36)));
        rows.Add(("pkg_padding", Ascii(h, 0x64, 12)));

        // 0x70..0x7C — drm_type, content_type, content_flags, promote_size.
        uint drmType = U32(h, 0x70);
        rows.Add(("pkg_drm_type", drmType == 15 ? "PS4" : "Unkown"));
        rows.Add(("pkg_content_type", U32(h, 0x74).ToString()));
        rows.Add(("pkg_content_flags", U32(h, 0x78).ToString()));
        rows.Add(("pkg_promote_size", U32(h, 0x7C).ToString("X")));

        // 0x80..0x9C — version_date, version_hash, 0x088..0x094, iro_tag, ekc.
        rows.Add(("pkg_version_date", U32(h, 0x80).ToString("X")));
        rows.Add(("pkg_version_hash", U32(h, 0x84).ToString("X")));
        rows.Add(("pkg_0x088", U32(h, 0x88).ToString("X")));
        rows.Add(("pkg_0x08C", U32(h, 0x8C).ToString("X")));
        rows.Add(("pkg_0x090", U32(h, 0x90).ToString("X")));
        rows.Add(("pkg_0x094", U32(h, 0x94).ToString("X")));
        uint iro = U32(h, 0x98);
        rows.Add(("pkg_iro_tag", iro == 0 ? "None" : iro.ToString()));
        rows.Add(("ekc_version", U32(h, 0x9C).ToString("X")));

        // 0x100..0x180 — digest table (4 × 32 bytes).
        rows.Add(("digest_entries1", Hex(h, 0x100, 32)));
        rows.Add(("digest_entries2", Hex(h, 0x120, 32)));
        rows.Add(("digest_table_digest", Hex(h, 0x140, 32)));
        rows.Add(("digest_body_digest", Hex(h, 0x160, 32)));

        // 0x400..0x404 — the 4-byte unk region PS4_Tools labels "unk_0x400".
        rows.Add(("unk_0x400", Ascii(h, 0x400, 4)));

        // 0x404..0x498 — pfs block.
        rows.Add(("pfs_image_count", U32(h, 0x404).ToString()));
        rows.Add(("pfs_image_flags", "0x" + U64(h, 0x408).ToString("X")));
        rows.Add(("pfs_image_offset", "0x" + U64(h, 0x410).ToString("X")));
        rows.Add(("pfs_image_size", "0x" + U64(h, 0x418).ToString("X")));
        rows.Add(("mount_image_offset", "0x" + U64(h, 0x420).ToString("X")));
        rows.Add(("mount_image_size", "0x" + U64(h, 0x428).ToString("X")));
        rows.Add(("pkg_size", "0x" + U64(h, 0x430).ToString("X")));
        rows.Add(("pfs_signed_size", "0x" + U32(h, 0x438).ToString("X")));
        rows.Add(("pfs_cache_size", "0x" + U32(h, 0x43C).ToString("X")));

        // 0x440/0x460 — digests.
        rows.Add(("pfs_image_digest", Hex(h, 0x440, 32)));
        rows.Add(("pfs_signed_digest", Hex(h, 0x460, 32)));

        // 0x480/0x488 — split sizes.
        rows.Add(("pfs_split_size_nth_0", U64(h, 0x480).ToString("X")));
        rows.Add(("pfs_split_size_nth_1", "0x" + U64(h, 0x488).ToString("X")));

        // 0xFE0 — tail digest.
        rows.Add(("pkg_digest", Hex(h, 0xFE0, 32)));

        return rows;
    }

    /// <summary>
    /// Convenience: reads the raw header bytes from <paramref name="reader"/>
    /// (without disturbing its position) and returns the dump rows.
    /// </summary>
    public static List<(string Type, string Value)> Rows(PkgHeader header, BigEndianReader reader)
    {
        if (reader is null) throw new ArgumentNullException(nameof(reader));
        var h = reader.ReadBytesAt(0, HeaderBytes);
        return Rows(header, h);
    }

    // ── readers ───────────────────────────────────────────────────────

    private static uint U32(byte[] h, int off) =>
        BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(off, 4));
    private static ulong U64(byte[] h, int off) =>
        BinaryPrimitives.ReadUInt64BigEndian(h.AsSpan(off, 8));
    private static ushort U16(byte[] h, int off) =>
        BinaryPrimitives.ReadUInt16BigEndian(h.AsSpan(off, 2));

    private static string Ascii(byte[] h, int off, int len)
    {
        // Legacy uses Encoding.ASCII.GetString on the raw bytes, which keeps
        // trailing NULs and any non-ASCII replacement chars — preserve that.
        return Encoding.ASCII.GetString(h, off, len);
    }

    private static string Hex(byte[] h, int off, int len) =>
        Convert.ToHexString(h, off, len);
}
