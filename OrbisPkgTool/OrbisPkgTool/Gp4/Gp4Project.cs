using System.Xml.Linq;

namespace OrbisPkgTool.Gp4;

/// <summary>GP4 package volume types (the &lt;volume_type&gt; element).</summary>
public enum VolumeType
{
    PkgPs4App,
    PkgPs4Patch,
    PkgPs4Remaster,
    PkgPs4AcData,
    PkgPs4AcNodata,
    PkgPs4SfTheme,
    PkgPs4Theme,
}

public static class VolumeTypes
{
    public static string ToXml(VolumeType t) => t switch
    {
        VolumeType.PkgPs4App => "pkg_ps4_app",
        VolumeType.PkgPs4Patch => "pkg_ps4_patch",
        VolumeType.PkgPs4Remaster => "pkg_ps4_remaster",
        VolumeType.PkgPs4AcData => "pkg_ps4_ac_data",
        VolumeType.PkgPs4AcNodata => "pkg_ps4_ac_nodata",
        VolumeType.PkgPs4SfTheme => "pkg_ps4_sf_theme",
        _ => "pkg_ps4_theme",
    };

    public static VolumeType FromXml(string s) => s switch
    {
        "pkg_ps4_app" => VolumeType.PkgPs4App,
        "pkg_ps4_patch" => VolumeType.PkgPs4Patch,
        "pkg_ps4_remaster" => VolumeType.PkgPs4Remaster,
        "pkg_ps4_ac_data" => VolumeType.PkgPs4AcData,
        "pkg_ps4_ac_nodata" => VolumeType.PkgPs4AcNodata,
        "pkg_ps4_sf_theme" => VolumeType.PkgPs4SfTheme,
        _ => VolumeType.PkgPs4Theme,
    };
}

/// <summary>
/// GP4 project file — the input format for the PS4 PKG builder
/// (gengp4_app / gengp4_patch generate these; orbis-pub-gen consumes them).
/// </summary>
public sealed class Gp4Project
{
    public VolumeType VolumeType = VolumeType.PkgPs4App;
    public string ContentId = "";
    public string Passcode = "";
    public string StorageType = "digital25";
    public string AppType = "full";
    public string Version = "01.00";
    public string TitleId = "";
    public string Title = "";
    public string AppVersion = "01.00";

    /// <summary>Target path (inside the PKG) → source path (relative to the GP4 file).</summary>
    public List<(string EntryPath, string OrigPath)> Files = [];

    public static Gp4Project Parse(string xml)
    {
        // gengp4_app writes an XML 1.1 declaration which .NET's parser
        // rejects — strip any declaration and parse the document body.
        int declEnd = xml.IndexOf("?>");
        if (xml.TrimStart().StartsWith("<?xml") && declEnd >= 0)
            xml = xml[(declEnd + 2)..];
        var doc = XDocument.Parse(xml);
        var proj = new Gp4Project();
        var vol = doc.Descendants("volume").FirstOrDefault();
        if (vol != null)
        {
            var vt = vol.Element("volume_type")?.Value;
            if (!string.IsNullOrEmpty(vt))
                proj.VolumeType = VolumeTypes.FromXml(vt.Trim());
            var pkg = vol.Element("package");
            if (pkg != null)
            {
                // Support both child-element and attribute forms
                proj.ContentId = (pkg.Element("content_id")?.Value ?? pkg.Attribute("content_id")?.Value)?.Trim() ?? "";
                proj.Passcode = (pkg.Element("passcode")?.Value ?? pkg.Attribute("passcode")?.Value)?.Trim() ?? "";
                proj.StorageType = (pkg.Element("storage_type")?.Value ?? pkg.Attribute("storage_type")?.Value)?.Trim() ?? proj.StorageType;
                proj.AppType = (pkg.Element("app_type")?.Value ?? pkg.Attribute("app_type")?.Value)?.Trim() ?? proj.AppType;
                proj.Version = (pkg.Element("version")?.Value ?? pkg.Attribute("version")?.Value)?.Trim() ?? proj.Version;
                proj.TitleId = (pkg.Element("title_id")?.Value ?? pkg.Attribute("title_id")?.Value)?.Trim() ?? "";
                proj.Title = (pkg.Element("title")?.Value ?? pkg.Attribute("title")?.Value)?.Trim() ?? "";
                proj.AppVersion = (pkg.Element("app_version")?.Value ?? pkg.Attribute("app_version")?.Value)?.Trim() ?? proj.AppVersion;
            }
        }
        foreach (var file in doc.Descendants("file"))
        {
            // Our format: <file><entry path="..."/><orig_path>...</orig_path></file>
            // orbis format: <file targ_path="..." orig_path="..."/>
            string entry = file.Element("entry")?.Attribute("path")?.Value
                ?? file.Attribute("targ_path")?.Value ?? "";
            string orig = file.Element("orig_path")?.Value
                ?? file.Attribute("orig_path")?.Value ?? "";
            if (entry.Length > 0)
                proj.Files.Add((entry, orig.Length > 0 ? orig : entry));
        }
        return proj;
    }

    /// <summary>
    /// Serializes the project to GP4 XML in the canonical gengp4_app format
    /// (verified against orbis-pub-cmd 3.87 output):
    ///   <psproject fmt="gp4" version="1000">
    ///     <volume>
    ///       <volume_type>pkg_ps4_app</volume_type>
    ///       <volume_id>PS4VOLUME</volume_id>
    ///       <volume_ts>...</volume_ts>
    ///       <package content_id=... passcode=... storage_type=... app_type=... />
    ///       <chunk_info>...</chunk_info>
    ///     </volume>
    ///     <files>
    ///       <file targ_path="..." orig_path="..." pfs_compression="enable" />
    ///     </files>
    ///     <rootdir />
    ///   </psproject>
    /// </summary>
    public string Serialize()
    {
        var ts = DateTime.UtcNow.ToString("yyyy-MM-dd HH:mm:ss");
        // gengp4_app writes the 32-zero passcode explicitly when none is set.
        var pass = string.IsNullOrEmpty(Passcode) ? "00000000000000000000000000000000" : Passcode;
        var doc = new XDocument(
            new XElement("psproject",
                new XAttribute("fmt", "gp4"),
                new XAttribute("version", "1000"),
                new XElement("volume",
                    new XElement("volume_type", VolumeTypes.ToXml(VolumeType)),
                    new XElement("volume_id", "PS4VOLUME"),
                    new XElement("volume_ts", ts),
                    new XElement("package",
                        new XAttribute("content_id", ContentId ?? ""),
                        new XAttribute("passcode", pass),
                        new XAttribute("storage_type", StorageType),
                        new XAttribute("app_type", AppType)),
                    new XElement("chunk_info",
                        new XAttribute("chunk_count", "1"),
                        new XAttribute("scenario_count", "1"),
                        new XElement("chunks",
                            new XElement("chunk",
                                new XAttribute("id", "0"),
                                new XAttribute("layer_no", "0"),
                                new XAttribute("label", "Chunk #0"))),
                        new XElement("scenarios",
                            new XAttribute("default_id", "0"),
                            new XElement("scenario",
                                new XAttribute("id", "0"),
                                new XAttribute("type", "sp"),
                                new XAttribute("initial_chunk_count", "1")))),
                    new XElement("files",
                        Files.Select(f =>
                            new XElement("file",
                                new XAttribute("targ_path", f.EntryPath),
                                new XAttribute("orig_path", f.OrigPath),
                                // enable PFSC compression on game content
                                // (mirrors gengp4_app; sce_sys/sce_module/eboot
                                //  are written uncompressed by the tool — we
                                //  enable for everything except sce_sys, matching
                                //  the observed output)
                                new XAttribute("pfs_compression",
                                    f.EntryPath.StartsWith("sce_sys/") ||
                                    f.EntryPath.StartsWith("sce_module/") ||
                                    f.EntryPath == "eboot.bin"
                                        ? "disable" : "enable"))))),
                new XElement("rootdir")));
        return "<?xml version=\"1.0\" encoding=\"utf-8\" standalone=\"yes\"?>\n" + doc.ToString();
    }

    /// <summary>
    /// Generates a GP4 project from a folder tree (gengp4_app/gengp4_patch equivalent):
    /// every file becomes a &lt;file&gt; entry with its relative path.
    /// </summary>
    public static Gp4Project FromFolder(string folder, bool isPatch, string? title = null,
        string? titleId = null, string? contentId = null, string passcode = "")
    {
        // Read metadata from the embedded param.sfo if present and not
        // overridden on the command line.
        TryReadSfo(folder, ref title, ref titleId, ref contentId);

        var proj = new Gp4Project
        {
            VolumeType = isPatch ? VolumeType.PkgPs4Patch : VolumeType.PkgPs4App,
            Passcode = passcode,
            Title = title ?? Path.GetFileName(folder.TrimEnd('/', '\\')),
            TitleId = titleId ?? "",
            ContentId = contentId ?? "",
        };
        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            string rel = Path.GetRelativePath(folder, f).Replace('\\', '/');
            proj.Files.Add((rel, rel));
        }
        return proj;
    }

    /// <summary>
    /// Fills title / title-id / content-id from sce_sys/param.sfo when
    /// the caller did not supply explicit overrides.
    /// </summary>
    private static void TryReadSfo(string folder, ref string? title, ref string? titleId, ref string? contentId)
    {
        // Standard locations after extract or restructure.
        string[] candidates = [
            Path.Combine(folder, "sce_sys", "param.sfo"),
            Path.Combine(folder, "..", "Sc0", "param.sfo"),  // dump before restructure
        ];
        string? sfoPath = candidates.FirstOrDefault(File.Exists);
        if (sfoPath == null) return;

        try
        {
            var sfo = OrbisPkgTool.Sfo.ParamSfo.Parse(File.ReadAllBytes(sfoPath));
            if (title == null)
                title = sfo.Values.FirstOrDefault(v => v.Key == "TITLE")?.StringValue;
            if (titleId == null)
                titleId = sfo.Values.FirstOrDefault(v => v.Key == "TITLE_ID")?.StringValue;
            if (contentId == null)
                contentId = sfo.Values.FirstOrDefault(v => v.Key == "CONTENT_ID")?.StringValue;
        }
        catch { /* best-effort — missing or unreadable SFO is not fatal */ }
    }
}
