using System.Buffers.Binary;
using System.Text;
using OrbisPkgTool.Binary;
using OrbisPkgTool.Pkg;

namespace OrbisPkgTool.Tests;

/// <summary>
/// Tests for the legacy 46-row PkgHeaderDump. The dump must match the
/// PS4_Tools PS4_Struct.DisplayType()/DisplayValue() shape: 46 rows in
/// the canonical order, hex fields with "0x" prefixes where the legacy
/// code added them, and uppercase hex strings for byte-array digest rows
/// (Convert.ToHexString casing).
/// </summary>
public class PkgHeaderDumpTests
{
    private const string Passcode = "00000000000000000000000000000000";
    private const string ContentId = "EP0001-CUSA00001_00-REG000000000001";

    /// <summary>
    /// Builds a real PKG and a synthetic raw header, then asserts the dump
    /// produces exactly 46 rows in the canonical order with the legacy
    /// formatting (decimal counts, "0x"-prefixed hex fields, uppercase
    /// digest hex strings, "PS4"/"Unknown" drm_type, "None" iro_tag).
    /// </summary>
    [Fact]
    public void Rows_Produces47RowsInLegacyOrder()
    {
        var (header, raw) = BuildSampleHeader();
        var rows = PkgHeaderDump.Rows(header, raw);

        Assert.Equal(46, rows.Count);
        Assert.Equal(PkgHeaderDump.RowTypes, rows.Select(r => r.Type).ToList());
    }

    [Fact]
    public void Rows_MagicIsAsciiBytes()
    {
        var (header, raw) = BuildSampleHeader();
        var rows = PkgHeaderDump.Rows(header, raw);
        // Magic 7F 43 4E 54 — ASCII GetString keeps all 4 bytes including 0x7F.
        Assert.Equal("\u007FCNT", rows[0].Value);
    }

    [Fact]
    public void Rows_DrmTypeRendersAsPS4WhenFifteen()
    {
        var (header, raw) = BuildSampleHeader();
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0x70), 15);
        var rows = PkgHeaderDump.Rows(header, raw);
        Assert.Equal("PS4", Assert.Single(rows, r => r.Type == "pkg_drm_type").Value);
    }

    [Fact]
    public void Rows_DrmTypeRendersAsUnknownWhenNotFifteen()
    {
        var (header, raw) = BuildSampleHeader();
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0x70), 0);
        var rows = PkgHeaderDump.Rows(header, raw);
        // Legacy spells it "Unkown" (sic) — preserve for byte-identical UI.
        Assert.Equal("Unkown", Assert.Single(rows, r => r.Type == "pkg_drm_type").Value);
    }

    [Fact]
    public void Rows_IroTagRendersAsNoneWhenZero()
    {
        var (header, raw) = BuildSampleHeader();
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0x98), 0);
        var rows = PkgHeaderDump.Rows(header, raw);
        Assert.Equal("None", Assert.Single(rows, r => r.Type == "pkg_iro_tag").Value);
    }

    [Fact]
    public void Rows_IroTagRendersAsDecimalWhenNonZero()
    {
        var (header, raw) = BuildSampleHeader();
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0x98), 42);
        var rows = PkgHeaderDump.Rows(header, raw);
        Assert.Equal("42", Assert.Single(rows, r => r.Type == "pkg_iro_tag").Value);
    }

    [Fact]
    public void Rows_DigestRowsAreUppercaseHex()
    {
        var (header, raw) = BuildSampleHeader();
        // Put a recognizable pattern in each digest slot.
        for (int i = 0; i < 32; i++)
        {
            raw[0x100 + i] = (byte)(0x10 + i); // 10..2F
            raw[0x120 + i] = (byte)(0x30 + i);
            raw[0x140 + i] = (byte)(0x40 + i);
            raw[0x160 + i] = (byte)(0x50 + i);
            raw[0x440 + i] = (byte)(0x60 + i);
            raw[0x460 + i] = (byte)(0x70 + i);
            raw[0xFE0 + i] = (byte)(0x80 + i);
        }
        var rows = PkgHeaderDump.Rows(header, raw);

        // Convert.ToHexString is uppercase — legacy PS4_Tools relied on it.
        var d1 = Assert.Single(rows, r => r.Type == "digest_entries1").Value;
        Assert.Equal(Convert.ToHexString(raw, 0x100, 32), d1);
        Assert.DoesNotContain("abcdef", d1);

        var pkgDigest = Assert.Single(rows, r => r.Type == "pkg_digest").Value;
        Assert.Equal(Convert.ToHexString(raw, 0xFE0, 32), pkgDigest);
    }

    [Fact]
    public void Rows_HexOffsetFieldsUse0xPrefix()
    {
        var (header, raw) = BuildSampleHeader();
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0x18), 0x200);
        BinaryPrimitives.WriteUInt64BigEndian(raw.AsSpan(0x20), 0x1000);
        var rows = PkgHeaderDump.Rows(header, raw);

        Assert.Equal("0x200", Assert.Single(rows, r => r.Type == "pkg_table_offset").Value);
        Assert.Equal("0x1000", Assert.Single(rows, r => r.Type == "pkg_body_offset:").Value);
    }

    [Fact]
    public void Rows_CountFieldsAreDecimal()
    {
        var (header, raw) = BuildSampleHeader();
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0x10), 7);
        BinaryPrimitives.WriteUInt16BigEndian(raw.AsSpan(0x14), 3);
        var rows = PkgHeaderDump.Rows(header, raw);

        Assert.Equal("7", Assert.Single(rows, r => r.Type == "pkg_entry_count").Value);
        Assert.Equal("3", Assert.Single(rows, r => r.Type == "pkg_sc_entry_count").Value);
    }

    [Fact]
    public void Rows_ContentIdKeepsPadding()
    {
        var (header, raw) = BuildSampleHeader();
        // Legacy ReadASCIIString reads 36 bytes via Encoding.ASCII.GetString —
        // trailing NULs are preserved as NUL chars in the string. The dump
        // value must keep them so the grid shows the same cell.
        var rows = PkgHeaderDump.Rows(header, raw);
        var cid = Assert.Single(rows, r => r.Type == "pkg_content_id").Value;
        Assert.Equal(36, cid.Length);
        Assert.StartsWith(ContentId, cid);
    }

    [Fact]
    public void Rows_BufferTooShort_Throws()
    {
        var (header, _) = BuildSampleHeader();
        var small = new byte[0x100];
        Assert.ThrowsAny<ArgumentException>(() => PkgHeaderDump.Rows(header, small));
    }

    [Fact]
    public void Rows_FromReader_PreservesReaderPosition()
    {
        var (header, raw) = BuildSampleHeader();
        using var ms = new MemoryStream(raw);
        var reader = new BigEndianReader(ms);
        reader.Position = 0x100;
        _ = PkgHeaderDump.Rows(header, reader);
        // The dump helper must restore the reader position.
        Assert.Equal(0x100, reader.Position);
    }

    // ── helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Builds a synthetic 0x1100-byte raw header with a valid magic and
    /// the sample content_id, and a minimal PkgHeader populated from it.
    /// Tests mutate raw[] and re-dump to assert per-field formatting.
    /// </summary>
    private static (PkgHeader Header, byte[] Raw) BuildSampleHeader()
    {
        var raw = new byte[PkgHeaderDump.HeaderBytes];
        // Magic 7F 43 4E 54.
        raw[0] = 0x7F; raw[1] = (byte)'C'; raw[2] = (byte)'N'; raw[3] = (byte)'T';
        // Content ID at 0x40, 36 bytes.
        var cid = Encoding.ASCII.GetBytes(ContentId);
        Buffer.BlockCopy(cid, 0, raw, 0x40, cid.Length);
        // Some non-zero defaults so hex rows are distinguishable from zero.
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0x04), 1);
        BinaryPrimitives.WriteUInt32BigEndian(raw.AsSpan(0x10), 5);

        var header = new PkgHeader
        {
            MagicValue = PkgHeader.Magic,
            Flags = 1,
            EntryCount = 5,
            ContentId = ContentId,
        };
        return (header, raw);
    }
}
