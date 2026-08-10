using OrbisPkgTool.Binary;

namespace OrbisPkgTool.Pkg;

/// <summary>
/// A single 32-byte record in the PKG entry table.
/// </summary>
public sealed class PkgEntry
{
    public const int Size = 32;

    public uint Id;
    public uint NameTableOffset;
    public uint Flags1;
    public uint Flags2;
    public long DataOffset;
    public long DataSize;

    /// <summary>Raw 32-byte entry record — used as input to the per-entry key derivation.</summary>
    public byte[] Raw = new byte[Size];

    /// <summary>Resolved file name from the name table (e.g. "param.sfo"), or null.</summary>
    public string? Name;

    /// <summary>Bit 31 of Flags1: entry data is encrypted.</summary>
    public bool IsEncrypted => (Flags1 & 0x80000000) != 0;

    /// <summary>Bits 12-15 of Flags2: selects which of the 7 derived keys decrypts this entry.</summary>
    public int KeyIndex => (int)((Flags2 & 0xF000) >> 12);

    public static PkgEntry Read(BigEndianReader r)
    {
        var e = new PkgEntry
        {
            Raw = r.ReadBytes(Size),
        };
        e.Id = ReadUInt32BE(e.Raw, 0);
        e.NameTableOffset = ReadUInt32BE(e.Raw, 4);
        e.Flags1 = ReadUInt32BE(e.Raw, 8);
        e.Flags2 = ReadUInt32BE(e.Raw, 12);
        e.DataOffset = ReadUInt32BE(e.Raw, 16);
        e.DataSize = ReadUInt32BE(e.Raw, 20);
        return e;
    }

    private static uint ReadUInt32BE(byte[] b, int off) =>
        (uint)((b[off] << 24) | (b[off + 1] << 16) | (b[off + 2] << 8) | b[off + 3]);
}

/// <summary>
/// Well-known entry IDs (as seen in the PKG entry table of official and fake PKGs).
/// </summary>
public static class PkgEntryIds
{
    public const uint Digests = 0x00000001;
    public const uint EntryKeys = 0x00000010;
    public const uint ImageKey = 0x00000020;
    public const uint GeneralDigests = 0x00000080;
    public const uint Metas = 0x00000100;
    public const uint EntryNames = 0x00000200;

    public const uint LicenseDat = 0x00000400;
    public const uint LicenseInfo = 0x00000401;
    public const uint NpTitleDat = 0x00000402;
    public const uint NpBindDat = 0x00000403;
    public const uint SelfInfoDat = 0x00000404;
    public const uint ImageInfoDat = 0x00000406;
    public const uint PsReservedDat = 0x00000409;

    public const uint ParamSfo = 0x00001000;
    public const uint PlaygoChunkDat = 0x00001001;
    public const uint PlaygoChunkSha = 0x00001002;
    public const uint PlaygoManifestXml = 0x00001003;
    // Entry IDs below verified against the original Digimon PKG's entry table
    // (built by orbis-pub-cmd 3.87) — NOT the older psdevwiki values.
    public const uint PronunciationXml = 0x00001004;
    public const uint PronunciationSig = 0x00001005;
    public const uint Pic1Png = 0x00001006;      // psdevwiki said 0x1241 — real orbis uses 0x1006
    public const uint ShareParamJson = 0x0000100B;
    public const uint ShareOverlayImagePng = 0x0000100C;

    public const uint Icon0Png = 0x00001200;
    public const uint Pic0Png = 0x00001220;
    public const uint Snd0At9 = 0x00001240;
    public const uint ChangeInfoXml = 0x00001260;
    public const uint Icon0Dds = 0x00001280;
    public const uint Pic0Dds = 0x000012A0;
    public const uint Pic1Dds = 0x000012C0;
    public const uint Trophy00Trp = 0x00001400;
    public const uint UserFileBase = 0x00002000;
}

/// <summary>
/// Fallback name map for entries whose name-table offset is zero or unresolvable.
/// Covers the standard Sc0 system entries.
/// </summary>
public static class PkgEntryNames
{
    public static readonly Dictionary<uint, string> Known = new()
    {
        [PkgEntryIds.LicenseDat] = "license.dat",
        [PkgEntryIds.LicenseInfo] = "license.info",
        [PkgEntryIds.NpTitleDat] = "nptitle.dat",
        [PkgEntryIds.NpBindDat] = "npbind.dat",
        [PkgEntryIds.SelfInfoDat] = "selfinfo.dat",
        [PkgEntryIds.ImageInfoDat] = "imageinfo.dat",
        [PkgEntryIds.PsReservedDat] = "psreserved.dat",
        [PkgEntryIds.ParamSfo] = "param.sfo",
        [PkgEntryIds.PlaygoChunkDat] = "playgo-chunk.dat",
        [PkgEntryIds.PlaygoChunkSha] = "playgo-chunk.sha",
        [PkgEntryIds.PlaygoManifestXml] = "playgo-manifest.xml",
        [PkgEntryIds.PronunciationXml] = "pronunciation.xml",
        [PkgEntryIds.PronunciationSig] = "pronunciation.sig",
        [PkgEntryIds.Pic1Png] = "pic1.png",
        [PkgEntryIds.ShareParamJson] = "shareparam.json",
        [PkgEntryIds.ShareOverlayImagePng] = "shareoverlayimage.png",
        [PkgEntryIds.Icon0Png] = "icon0.png",
        [PkgEntryIds.Pic0Png] = "pic0.png",
        [PkgEntryIds.Snd0At9] = "snd0.at9",
        [PkgEntryIds.ChangeInfoXml] = "changeinfo/changeinfo.xml",
        [PkgEntryIds.Icon0Dds] = "icon0.dds",
        [PkgEntryIds.Pic0Dds] = "pic0.dds",
        [PkgEntryIds.Pic1Dds] = "pic1.dds",
        [PkgEntryIds.Trophy00Trp] = "trophy/trophy00.trp",
    };

    public static string? TryGetName(uint id) => Known.GetValueOrDefault(id);
}
