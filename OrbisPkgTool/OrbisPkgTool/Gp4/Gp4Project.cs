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

    /// <summary>Serializes the project back to GP4 XML.</summary>
    public string Serialize()
    {
        var doc = new XDocument(
            new XElement("psproject",
                new XAttribute("fmt", "gp4"),
                new XAttribute("version", "1.0"),
                new XElement("volume",
                    new XElement("volume_type", VolumeTypes.ToXml(VolumeType)),
                    new XElement("package",
                        new XElement("content_id", ContentId),
                        new XElement("passcode", Passcode),
                        new XElement("storage_type", StorageType),
                        new XElement("app_type", AppType),
                        new XElement("version", Version),
                        new XElement("title_id", TitleId),
                        new XElement("title", Title),
                        new XElement("app_version", AppVersion))),
                new XElement("files",
                    Files.Select(f =>
                        new XElement("file",
                            new XElement("entry", new XAttribute("path", f.EntryPath)),
                            new XElement("orig_path", f.OrigPath))))));
        return doc.Declaration?.ToString() + "\n" + doc.ToString();
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
