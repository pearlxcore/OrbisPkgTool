using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OrbisPkgTool.Psn;

namespace OrbisPkgTool.Tests;

/// <summary>
/// Tests for the PSN update-check module. The HMAC key, URL format, and
/// XML/JSON parsing are pinned to the decompiled PS4_Tools behavior:
/// np_{titleId} → HMAC-SHA256 with the retail ShellCore key → hex-lower
/// digest → embedded in the titlepatch URL.
/// </summary>
public class UpdateCheckTests
{
    // Regression pin: HMAC-SHA256 of "np_CUSA00419" with the retail key
    // produces this exact 64-char hex string (verified at planning time).
    private const string Cusa00419HmacHexLower =
        "d5b7a32456606df00438f055e5af051b4eed76427ec299d9da6f5b13b0996c1f";

    private const string Cusa00419Url =
        "http://gs-sec.ww.np.dl.playstation.net/plo/np/CUSA00419/" +
        Cusa00419HmacHexLower + "/CUSA00419-ver.xml";

    [Fact]
    public void BuildUpdateUrl_ProducesLegacyFormat()
    {
        string url = UpdateCheck.BuildUpdateUrl("CUSA00419");
        Assert.Equal(Cusa00419Url, url);
    }

    [Fact]
    public void BuildUpdateUrl_HmacIsDeterministic_LowercaseHex()
    {
        // HMAC-SHA256 is deterministic: the same title id always yields the
        // same hex digest. Re-running the builder must produce the same URL.
        var url1 = UpdateCheck.BuildUpdateUrl("CUSA00042");
        var url2 = UpdateCheck.BuildUpdateUrl("CUSA00042");
        Assert.Equal(url1, url2);

        // The digest path segment is exactly 64 lowercase hex chars.
        var parts = url1.Split('/');
        string digest = parts[^2];
        Assert.Matches("^[0-9a-f]{64}$", digest);
    }

    [Fact]
    public void BuildUpdateUrl_EmptyTitleId_Throws()
    {
        Assert.ThrowsAny<ArgumentException>(() => UpdateCheck.BuildUpdateUrl(""));
        Assert.ThrowsAny<ArgumentException>(() => UpdateCheck.BuildUpdateUrl("   "));
    }

    [Fact]
    public void BuildUpdateUrl_TitleIdAppearsTwice()
    {
        const string id = "CUSA12345";
        string url = UpdateCheck.BuildUpdateUrl(id);
        // The title id appears once in the path and once in the filename.
        Assert.Equal(2, url.Split(id, StringSplitOptions.None).Length - 1);
    }

    [Fact]
    public void BuildUpdateUrl_DirectHmacMatchesLegacyKey()
    {
        // Re-derive the HMAC independently and compare the URL's digest
        // segment — pins the embedded key bytes against the extracted one.
        string titleId = "CUSA00419";
        using var hmac = new HMACSHA256(UpdateCheck.HmacSha256PatchPkgUrlKey);
        byte[] mac = hmac.ComputeHash(Encoding.ASCII.GetBytes("np_" + titleId));
        var expectedHex = new StringBuilder(mac.Length * 2);
        foreach (byte b in mac) expectedHex.Append(b.ToString("x2"));
        string url = UpdateCheck.BuildUpdateUrl(titleId);
        string urlDigest = url.Split('/')[^2];
        Assert.Equal(expectedHex.ToString(), urlDigest);
        Assert.Equal(Cusa00419HmacHexLower, urlDigest);
    }

    [Fact]
    public void ParseTitlepatchXml_ExtractsAllFields()
    {
        const string xml = @"<?xml version=""1.0""?>
<titlepatch titleid=""CUSA00419"">
  <tag name=""1"" mandatory=""false"">
    <package version=""1.05"" size=""12345678"" digest=""abc123""
             manifest_url=""http://manifest"" content_id=""EP0001-CUSA00419_00-APP0000000000001""
             system_ver=""0x2500000"" type=""patch"" remaster=""false"" patchgo=""true""/>
  </tag>
</titlepatch>";
        var patch = UpdateCheck.ParseTitlepatchXml(xml);
        Assert.NotNull(patch);
        Assert.Equal("CUSA00419", patch!.TitleId);
        Assert.NotNull(patch.Tag);
        Assert.Equal("1", patch.Tag!.Name);
        Assert.Equal("false", patch.Tag.Mandatory);
        var pkg = patch.Tag.Package;
        Assert.NotNull(pkg);
        Assert.Equal("1.05", pkg!.Version);
        Assert.Equal("12345678", pkg.Size);
        Assert.Equal("abc123", pkg.Digest);
        Assert.Equal("http://manifest", pkg.ManifestUrl);
        Assert.Equal("EP0001-CUSA00419_00-APP0000000000001", pkg.ContentId);
        Assert.Equal("0x2500000", pkg.SystemVer);
        Assert.Equal("patch", pkg.Type);
        Assert.Equal("false", pkg.Remaster);
        Assert.Equal("true", pkg.Patchgo);
    }

    [Fact]
    public void ParseTitlepatchXml_MalformedXml_ReturnsNull()
    {
        // Legacy swallowed all exceptions and returned null.
        Assert.Null(UpdateCheck.ParseTitlepatchXml("not xml"));
        Assert.Null(UpdateCheck.ParseTitlepatchXml(""));
    }

    [Fact]
    public void ParseTitlepatchXml_MissingPackageElement_ReturnsNull()
    {
        // Without a package element the legacy code threw a NullReference
        // inside the try/catch and returned null — preserve that.
        const string xml = @"<?xml version=""1.0""?>
<titlepatch titleid=""CUSA00419"">
  <tag name=""1"" mandatory=""false""></tag>
</titlepatch>";
        Assert.Null(UpdateCheck.ParseTitlepatchXml(xml));
    }

    [Fact]
    public void ParseManifestJson_ParsesPieces()
    {
        const string json = @"{
            ""originalFileSize"": 123456,
            ""packageDigest"": ""deadbeef"",
            ""numberOfSplitFiles"": 2,
            ""pieces"": [
                { ""url"": ""http://a"", ""fileOffset"": 0, ""fileSize"": 4096, ""hashValue"": ""h1"" },
                { ""url"": ""http://b"", ""fileOffset"": 4096, ""fileSize"": 8192, ""hashValue"": ""h2"" }
            ]
        }";
        var manifest = UpdateCheck.ParseManifestJson(json);
        Assert.NotNull(manifest);
        Assert.Equal(123456, manifest!.OriginalFileSize);
        Assert.Equal("deadbeef", manifest.PackageDigest);
        Assert.Equal(2, manifest.NumberOfSplitFiles);
        Assert.Equal(2, manifest.Pieces.Count);
        var p = manifest.Pieces[0];
        Assert.Equal("http://a", p.Url);
        Assert.Equal(0, p.FileOffset);
        Assert.Equal(4096, p.FileSize);
        Assert.Equal("h1", p.HashValue);
    }

    [Fact]
    public void ParseManifestJson_MalformedJson_ReturnsNull()
    {
        Assert.Null(UpdateCheck.ParseManifestJson("not json"));
        Assert.Null(UpdateCheck.ParseManifestJson(""));
    }

    [Fact]
    public async Task CheckForUpdateAsync_NetworkError_ReturnsNull()
    {
        // A handler that always throws — CheckForUpdate must swallow the
        // error and return null, matching the legacy swallow-all-catch.
        var client = new HttpClient(new ThrowingHandler());
        var patch = await UpdateCheck.CheckForUpdateAsync("CUSA00419", client);
        Assert.Null(patch);
    }

    [Fact]
    public async Task CheckForUpdateAsync_ValidResponses_FillsManifest()
    {
        // End-to-end with canned responses: the titlepatch XML references a
        // manifest_url which must be fetched and parsed into ManifestItem.
        var xml = @"<?xml version=""1.0""?>
<titlepatch titleid=""CUSA00419"">
  <tag name=""1"" mandatory=""false"">
    <package version=""1.05"" size=""1"" digest=""d"" manifest_url=""http://stub/manifest.json""
             content_id=""c"" system_ver=""0x2500000"" type=""patch"" remaster=""false"" patchgo=""true""/>
  </tag>
</titlepatch>";
        const string json = @"{""originalFileSize"":10,""packageDigest"":""pd"",
""numberOfSplitFiles"":1,
""pieces"":[{""url"":""http://p1"",""fileOffset"":0,""fileSize"":10,""hashValue"":""h""}]}";

        var handler = new StubHandler(xml, "http://gs-sec.ww.np.dl.playstation.net/plo/np/CUSA00419/");
        handler.Add("http://stub/manifest.json", json);
        using var client = new HttpClient(handler);

        var patch = await UpdateCheck.CheckForUpdateAsync("CUSA00419", client);
        Assert.NotNull(patch);
        Assert.Equal("1.05", patch!.Tag!.Package!.Version);
        Assert.NotNull(patch.Tag.Package.ManifestItem);
        Assert.Single(patch.Tag.Package.ManifestItem!.Pieces);
        Assert.Equal("http://p1", patch.Tag.Package.ManifestItem.Pieces[0].Url);
        // User-agent must match the legacy "Only a test!".
        Assert.Equal("Only a test!", handler.LastUserAgent);
    }

    private sealed class ThrowingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
            => throw new HttpRequestException("stub network failure");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Dictionary<string, string> _responses = [];
        private readonly string _defaultBody;
        private readonly string _defaultUrlPrefix;

        public string? LastUserAgent { get; private set; }

        public StubHandler(string defaultBody, string defaultUrlPrefix)
        {
            _defaultBody = defaultBody;
            _defaultUrlPrefix = defaultUrlPrefix;
        }

        public void Add(string url, string body) => _responses[url] = body;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastUserAgent = request.Headers.UserAgent.ToString();
            string url = request.RequestUri!.ToString();
            string body = _responses.TryGetValue(url, out var b)
                ? b
                : url.StartsWith(_defaultUrlPrefix, StringComparison.Ordinal) ? _defaultBody : "";
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                Content = new StringContent(body),
            });
        }
    }
}
