using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Xml.Linq;

namespace OrbisPkgTool.Psn;

/// <summary>One piece (split file) of an official update manifest.</summary>
public sealed class UpdateManifestPiece
{
    [JsonPropertyName("url")] public string Url { get; set; } = "";
    [JsonPropertyName("fileOffset")] public long FileOffset { get; set; }
    [JsonPropertyName("fileSize")] public long FileSize { get; set; }
    [JsonPropertyName("hashValue")] public string HashValue { get; set; } = "";
}

/// <summary>The JSON manifest referenced by a titlepatch's manifest_url.</summary>
public sealed class UpdateManifest
{
    [JsonPropertyName("originalFileSize")] public long OriginalFileSize { get; set; }
    [JsonPropertyName("packageDigest")] public string PackageDigest { get; set; } = "";
    [JsonPropertyName("numberOfSplitFiles")] public long NumberOfSplitFiles { get; set; }
    [JsonPropertyName("pieces")] public List<UpdateManifestPiece> Pieces { get; set; } = [];
}

/// <summary>The &lt;package&gt; element of a titlepatch.</summary>
public sealed class UpdatePackage
{
    public string Version { get; set; } = "";
    public string Size { get; set; } = "";
    public string Digest { get; set; } = "";
    public string ManifestUrl { get; set; } = "";
    public UpdateManifest? ManifestItem { get; set; }
    public string ContentId { get; set; } = "";
    public string SystemVer { get; set; } = "";
    public string Type { get; set; } = "";
    public string Remaster { get; set; } = "";
    public string Patchgo { get; set; } = "";
}

/// <summary>The &lt;tag&gt; element of a titlepatch.</summary>
public sealed class UpdateTag
{
    public string Name { get; set; } = "";
    public string Mandatory { get; set; } = "";
    public UpdatePackage? Package { get; set; }
}

/// <summary>The root of a &lt;titlepatch&gt; document.</summary>
public sealed class TitlePatch
{
    public string TitleId { get; set; } = "";
    public UpdateTag? Tag { get; set; }
}

/// <summary>
/// PSN official-update checker — managed replacement for
/// PS4_Tools.PKG.Official.CheckForUpdate. Builds the HMAC-SHA256
/// authenticated titlepatch URL for a title ID, downloads the
/// {titleid}-ver.xml, and resolves the JSON manifest with its download
/// pieces.
/// </summary>
public static class UpdateCheck
{
    /// <summary>
    /// The retail ShellCore HMAC-SHA256 key used to authenticate patch
    /// package URLs (np_&lt;titleid&gt; → hex digest path segment).
    /// Extracted from PS4_Tools' PS4Keys.ShellCore_Keys.Retail.
    /// </summary>
    public static readonly byte[] HmacSha256PatchPkgUrlKey =
    [
        173, 98, 227, 127, 144, 94, 6, 188, 25, 89,
        49, 66, 40, 28, 17, 44, 236, 14, 126, 195,
        233, 126, 253, 202, 239, 205, 186, 175, 166, 55,
        141, 132,
    ];

    private const string UserAgent = "Only a test!";

    /// <summary>
    /// Builds the titlepatch URL for <paramref name="titleId"/>:
    /// http://gs-sec.ww.np.dl.playstation.net/plo/np/{id}/{hmac-hex-lower}/{id}-ver.xml
    /// </summary>
    public static string BuildUpdateUrl(string titleId)
    {
        if (string.IsNullOrWhiteSpace(titleId))
            throw new ArgumentException("titleId must not be empty", nameof(titleId));
        byte[] mac;
        using (var hmac = new HMACSHA256(HmacSha256PatchPkgUrlKey))
        {
            mac = hmac.ComputeHash(Encoding.ASCII.GetBytes("np_" + titleId));
        }
        var sb = new StringBuilder(mac.Length * 2);
        foreach (byte b in mac)
            sb.Append(b.ToString("x2"));
        return $"http://gs-sec.ww.np.dl.playstation.net/plo/np/{titleId}/{sb}/{titleId}-ver.xml";
    }

    /// <summary>
    /// Parses a titlepatch XML document. Returns null when the document
    /// lacks the titlepatch/tag/package structure (mirroring the legacy
    /// swallow-and-return-null behavior).
    /// </summary>
    public static TitlePatch? ParseTitlepatchXml(string xml)
    {
        try
        {
            var doc = XDocument.Parse(xml);
            var patch = doc.Descendants("titlepatch").FirstOrDefault();
            var tag = patch?.Descendants("tag").FirstOrDefault();
            var package = tag?.Descendants("package").FirstOrDefault();
            if (patch == null || tag == null || package == null)
                return null;

            return new TitlePatch
            {
                TitleId = (string?)patch.Attribute("titleid") ?? "",
                Tag = new UpdateTag
                {
                    Name = (string?)tag.Attribute("name") ?? "",
                    Mandatory = (string?)tag.Attribute("mandatory") ?? "",
                    Package = new UpdatePackage
                    {
                        Version = (string?)package.Attribute("version") ?? "",
                        Size = (string?)package.Attribute("size") ?? "",
                        Digest = (string?)package.Attribute("digest") ?? "",
                        ManifestUrl = (string?)package.Attribute("manifest_url") ?? "",
                        ContentId = (string?)package.Attribute("content_id") ?? "",
                        SystemVer = (string?)package.Attribute("system_ver") ?? "",
                        Type = (string?)package.Attribute("type") ?? "",
                        Remaster = (string?)package.Attribute("remaster") ?? "",
                        Patchgo = (string?)package.Attribute("patchgo") ?? "",
                    },
                },
            };
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Parses a manifest JSON document. Returns null on failure (mirroring
    /// the legacy per-field try/catch, which left Manifest_item null).
    /// </summary>
    public static UpdateManifest? ParseManifestJson(string json)
    {
        try
        {
            return JsonSerializer.Deserialize<UpdateManifest>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Checks for an official update: builds the titlepatch URL, downloads
    /// the XML, and (when it references a manifest_url) downloads and parses
    /// the JSON manifest. Returns null on any failure — the legacy contract.
    /// </summary>
    public static async Task<TitlePatch?> CheckForUpdateAsync(
        string titleId,
        HttpClient? client = null,
        CancellationToken cancellationToken = default)
    {
        HttpClient? owned = null;
        client ??= owned = new HttpClient();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, BuildUpdateUrl(titleId));
            req.Headers.TryAddWithoutValidation("user-agent", UserAgent);
            using var resp = await client.SendAsync(req, cancellationToken).ConfigureAwait(false);
            resp.EnsureSuccessStatusCode();
            string xml = await resp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            var patch = ParseTitlepatchXml(xml);
            if (patch == null)
                return null;

            var manifestUrl = patch.Tag?.Package?.ManifestUrl;
            if (!string.IsNullOrEmpty(manifestUrl))
            {
                try
                {
                    using var mreq = new HttpRequestMessage(HttpMethod.Get, manifestUrl);
                    mreq.Headers.TryAddWithoutValidation("user-agent", UserAgent);
                    using var mresp = await client.SendAsync(mreq, cancellationToken).ConfigureAwait(false);
                    mresp.EnsureSuccessStatusCode();
                    string json = await mresp.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
                    patch.Tag!.Package!.ManifestItem = ParseManifestJson(json);
                }
                catch
                {
                    // Manifest fetch is best-effort: the patch data without
                    // pieces is still returned (legacy behavior).
                }
            }
            return patch;
        }
        catch
        {
            return null;
        }
        finally
        {
            owned?.Dispose();
        }
    }

    /// <summary>Synchronous wrapper over <see cref="CheckForUpdateAsync"/>.</summary>
    public static TitlePatch? CheckForUpdate(string titleId, HttpClient? client = null)
        => CheckForUpdateAsync(titleId, client).GetAwaiter().GetResult();
}
