using System.Text;

namespace OrbisPkgTool.Sfo;

/// <summary>
/// A single param.sfo (PSF) value.
/// </summary>
public sealed class SfoValue
{
    public string Key = "";
    public ushort Format;
    public byte[] Data = [];
    public int MaxLength;

    /// <summary>UTF-8 string value (for format 0x0204 / 0x0004).</summary>
    public string StringValue =>
        Encoding.UTF8.GetString(Data).TrimEnd('\0');

    /// <summary>Integer value (for format 0x0404).</summary>
    public int IntValue =>
        Data.Length >= 4 ? BitConverter.ToInt32(Data, 0) : 0;
}

/// <summary>
/// PS4 param.sfo parser (the PSF format: "\0PSF" magic, little-endian).
/// </summary>
public sealed class ParamSfo
{
    public const string Magic = "\0PSF";

    public List<SfoValue> Values = [];

    /// <summary>Gets a value by key (case-insensitive), or null.</summary>
    public SfoValue? this[string key] =>
        Values.FirstOrDefault(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase));

    public string GetString(string key) => this[key]?.StringValue ?? "";
    public int GetInt(string key) => this[key]?.IntValue ?? 0;

    public static ParamSfo Parse(byte[] data)
    {
        var sfo = new ParamSfo();
        if (data.Length < 0x14)
            return sfo;
        if (data[0] != 0 || data[1] != (byte)'P' || data[2] != (byte)'S' || data[3] != (byte)'F')
            return sfo; // not a PSF

        uint keyTableOffset = BitConverter.ToUInt32(data, 0x08);
        uint dataTableOffset = BitConverter.ToUInt32(data, 0x0C);
        uint entryCount = BitConverter.ToUInt32(data, 0x10);
        if (entryCount > 4096 || keyTableOffset + 0x10 > data.Length)
            return sfo;

        for (uint i = 0; i < entryCount; i++)
        {
            int off = 0x14 + (int)i * 0x10;
            if (off + 0x10 > data.Length) break;
            ushort keyOffset = BitConverter.ToUInt16(data, off + 0x00);
            ushort format = BitConverter.ToUInt16(data, off + 0x02);
            uint length = BitConverter.ToUInt32(data, off + 0x04);
            uint maxLength = BitConverter.ToUInt32(data, off + 0x08);
            uint dataOffset = BitConverter.ToUInt32(data, off + 0x0C);

            string key = ReadCString(data, keyTableOffset + keyOffset);
            var value = new SfoValue
            {
                Key = key,
                Format = format,
                MaxLength = (int)maxLength,
            };
            long dataPos = dataTableOffset + dataOffset;
            if (dataPos >= 0 && dataPos + length <= data.Length)
            {
                value.Data = new byte[length];
                Buffer.BlockCopy(data, (int)dataPos, value.Data, 0, (int)length);
            }
            sfo.Values.Add(value);
        }
        return sfo;
    }

    private static string ReadCString(byte[] data, uint offset)
    {
        if (offset >= data.Length) return "";
        int end = (int)offset;
        while (end < data.Length && data[end] != 0) end++;
        return Encoding.UTF8.GetString(data, (int)offset, end - (int)offset);
    }

    // ------------------------------------------------------------------
    // Writing (orbis-pub-sfo equivalent)
    // ------------------------------------------------------------------

    public const ushort FormatUtf8 = 0x0204;
    public const ushort FormatUtf8Special = 0x0004;
    public const ushort FormatInt = 0x0404;

    /// <summary>Sets a UTF-8 string value (format 0x0204), replacing any existing value.</summary>
    public void SetString(string key, string value, int maxLength = 0x400)
    {
        Values.RemoveAll(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase));
        Values.Add(new SfoValue
        {
            Key = key,
            Format = FormatUtf8,
            MaxLength = maxLength,
            Data = Encoding.UTF8.GetBytes(value + "\0"),
        });
    }

    /// <summary>Sets a 32-bit integer value (format 0x0404), replacing any existing value.</summary>
    public void SetInt(string key, int value)
    {
        Values.RemoveAll(v => string.Equals(v.Key, key, StringComparison.OrdinalIgnoreCase));
        Values.Add(new SfoValue
        {
            Key = key,
            Format = FormatInt,
            MaxLength = 4,
            Data = BitConverter.GetBytes(value),
        });
    }

    /// <summary>
    /// Serializes the values back to the PSF binary format:
    /// "\0PSF" header, 0x10-byte entries (sorted by key), key table, data table.
    /// Each value occupies MaxLength bytes in the data table (the region is
    /// zero-filled and 4-aligned) — orbis's strict parser requires the
    /// max_length spacing, not tight packing.
    /// </summary>
    public byte[] Serialize()
    {
        var ordered = Values.OrderBy(v => v.Key, StringComparer.Ordinal).ToList();
        int entryCount = ordered.Count;
        int keyTableOffset = 0x14 + entryCount * 0x10;
        int keyTableSize = ordered.Sum(v => v.Key.Length + 1);
        int dataTableOffset = keyTableOffset + keyTableSize;
        if (dataTableOffset % 4 != 0)
            dataTableOffset += 4 - (dataTableOffset % 4);
        int dataSize = ordered.Sum(v => Math.Max(v.MaxLength, v.Format == FormatInt ? 4 : 1));

        using var ms = new MemoryStream(new byte[dataTableOffset + dataSize]); // zero-filled
        var w = new BinaryWriter(ms);
        w.Write((byte)0);
        w.Write(Encoding.ASCII.GetBytes("PSF"));
        w.Write(0x101); // version
        w.Write(keyTableOffset);
        w.Write(dataTableOffset);
        w.Write(entryCount);

        int keyOffset = 0;
        int dataOffset = 0;
        int index = 0;
        foreach (var v in ordered)
        {
            w.BaseStream.Position = 0x14 + 0x10 * index++;
            w.Write((ushort)keyOffset);
            w.Write(v.Format);
            int length = Math.Max(v.Data.Length, v.Format == FormatInt ? 4 : 1);
            w.Write(length);
            w.Write(Math.Max(v.MaxLength, length));
            w.Write(dataOffset);
            keyOffset += v.Key.Length + 1;
            dataOffset += Math.Max(v.MaxLength, v.Format == FormatInt ? 4 : 1);
        }

        // Key table
        w.BaseStream.Position = keyTableOffset;
        foreach (var v in ordered)
        {
            w.Write(Encoding.ASCII.GetBytes(v.Key));
            w.Write((byte)0);
        }
        // Data table (each value at its data offset, spaced by max length)
        dataOffset = 0;
        foreach (var v in ordered)
        {
            w.BaseStream.Position = dataTableOffset + dataOffset;
            var data = v.Data;
            if (v.Format == FormatInt && data.Length < 4)
                Array.Resize(ref data, 4);
            w.Write(data);
            dataOffset += Math.Max(v.MaxLength, v.Format == FormatInt ? 4 : 1);
        }
        return ms.ToArray();
    }

    /// <summary>Creates a default game (GD) param.sfo template.</summary>
    public static ParamSfo CreateGameTemplate(string title, string titleId, string contentId)
    {
        var sfo = new ParamSfo();
        sfo.SetInt("APP_TYPE", 1);
        sfo.SetString("APP_VER", "01.00", 0x8);
        sfo.SetInt("ATTRIBUTE", 0x00800002);
        sfo.SetInt("ATTRIBUTE2", 0);
        sfo.SetString("CATEGORY", "gd", 0x4);
        sfo.SetString("CONTENT_ID", contentId, 0x30);
        sfo.SetInt("DEV_FLAG", 0);
        sfo.SetInt("DOWNLOAD_DATA_SIZE", 0);
        sfo.SetString("FORMAT", "obs", 0x4);
        sfo.SetInt("PARENTAL_LEVEL", 5);
        sfo.SetInt("REMOTE_PLAY_KEY_ASSIGN", 0);
        for (int i = 1; i <= 7; i++) sfo.SetString($"SERVICE_ID_ADDCONT_ADD_{i}", "", 0x14);
        sfo.SetInt("SYSTEM_VER", 0x02700000);
        sfo.SetString("TITLE", title, 0x80);
        sfo.SetString("TITLE_ID", titleId, 0xC);
        for (int i = 1; i <= 4; i++) sfo.SetInt($"USER_DEFINED_PARAM_{i}", 0);
        sfo.SetString("VERSION", "01.00", 0x8);
        sfo.SetString("PUBTOOLINFO", $"c_date={DateTime.UtcNow:yyyyMMdd},img0_l0_size=0,img0_l1_size=0,img0_sc_ksize=512,img0_pc_ksize=832", 0x200);
        sfo.SetInt("PUBTOOLVER", 0x02890000);
        return sfo;
    }

    /// <summary>Creates a default add-on (AC) param.sfo template.</summary>
    public static ParamSfo CreateAddonTemplate(string title, string titleId, string contentId)
    {
        var sfo = CreateGameTemplate(title, titleId, contentId);
        sfo.SetString("CATEGORY", "ac");
        return sfo;
    }

    /// <summary>Creates a default theme param.sfo template.</summary>
    public static ParamSfo CreateThemeTemplate(string title, string titleId, string contentId)
    {
        var sfo = CreateGameTemplate(title, titleId, contentId);
        sfo.SetString("CATEGORY", "ac");
        sfo.SetString("FORMAT", "obs");
        return sfo;
    }
}
