using System.Text;
using OrbisPkgTool.Sfo;

namespace OrbisPkgTool.Tests;

/// <summary>
/// Tests for the legacy-shaped <see cref="ParamSfo.Tables"/> accessor
/// (SfoTable rows): int rows must render as decimal strings, string rows
/// as UTF-8 text, and Parse/Serialize must round-trip through Tables.
/// </summary>
public class SfoTableTests
{
    [Fact]
    public void Tables_IntRowsRenderAsDecimalStrings()
    {
        var sfo = new ParamSfo();
        sfo.SetInt("SYSTEM_VER", 0x02700000); // 40894464 decimal
        sfo.SetInt("ATTRIBUTE", 0x00800002); // 8388608+2 = 8388610

        var rows = sfo.Tables;
        Assert.Equal(2, rows.Count);

        var sys = Assert.Single(rows, r => r.Name == "SYSTEM_VER");
        Assert.Equal("40894464", sys.Value);
        Assert.Equal(ParamSfo.FormatInt, sys.Format);

        var attr = Assert.Single(rows, r => r.Name == "ATTRIBUTE");
        Assert.Equal("8388610", attr.Value);
        Assert.Equal(ParamSfo.FormatInt, attr.Format);
    }

    [Fact]
    public void Tables_StringRowsRenderAsUtf8()
    {
        var sfo = new ParamSfo();
        sfo.SetString("TITLE", "Rückwärts Café ☕", 0x80);
        sfo.SetString("CATEGORY", "gd", 0x4);

        var rows = sfo.Tables;
        Assert.Equal("Rückwärts Café ☕", Assert.Single(rows, r => r.Name == "TITLE").Value);
        Assert.Equal("gd", Assert.Single(rows, r => r.Name == "CATEGORY").Value);
    }

    [Fact]
    public void Tables_ParseRealTemplate_RendersIntRowsAsDecimal()
    {
        // The template mixes string and int rows; Parse(Serialize()) must
        // produce a Tables view where SYSTEM_VER/PUBTOOLVER/APP_TYPE are
        // decimal strings — never the raw 4-byte little-endian garbage that
        // GetString returns for int rows.
        var sfo = ParamSfo.CreateGameTemplate("TablesTest", "CUSA00001",
            "EP0001-CUSA00001_00-TABLES0000000001");
        var parsed = ParamSfo.Parse(sfo.Serialize());

        var rows = parsed.Tables;
        Assert.NotEmpty(rows);

        var sys = Assert.Single(rows, r => r.Name == "SYSTEM_VER");
        Assert.Equal(0x02700000.ToString(), sys.Value); // "41261056", not "??\u0002"
        Assert.Equal(0x02700000, parsed.GetInt("SYSTEM_VER"));

        var appType = Assert.Single(rows, r => r.Name == "APP_TYPE");
        Assert.Equal("1", appType.Value);

        var title = Assert.Single(rows, r => r.Name == "TITLE");
        Assert.Equal("TablesTest", title.Value);

        // Row count parity: every serialized value appears exactly once.
        Assert.Equal(parsed.Values.Count, rows.Count);
        Assert.Equal(sfo.Values.Count, parsed.Values.Count);
    }

    [Fact]
    public void Tables_EmptySfo_ReturnsEmptyList()
    {
        var sfo = new ParamSfo();
        Assert.Empty(sfo.Tables);
    }

    [Fact]
    public void Tables_Format0x0004_RendersAsString()
    {
        // Format 0x0004 (UTF-8 special) must use StringValue, not IntValue —
        // only 0x0404 is the int format.
        var sfo = new ParamSfo();
        sfo.Values.Add(new SfoValue
        {
            Key = "SPECIAL",
            Format = ParamSfo.FormatUtf8Special,
            MaxLength = 0x10,
            Data = Encoding.UTF8.GetBytes("hello\0"),
        });
        var row = Assert.Single(sfo.Tables);
        Assert.Equal("hello", row.Value);
        Assert.Equal(ParamSfo.FormatUtf8Special, row.Format);
    }

    [Fact]
    public void Tables_IntRowWithShortData_RendersZero()
    {
        // Defensive: an int row whose data blob is < 4 bytes (corrupt SFO)
        // renders as "0" via IntValue's length guard.
        var sfo = new ParamSfo();
        sfo.Values.Add(new SfoValue
        {
            Key = "STUB",
            Format = ParamSfo.FormatInt,
            MaxLength = 4,
            Data = [1, 2],
        });
        Assert.Equal("0", Assert.Single(sfo.Tables).Value);
    }
}
