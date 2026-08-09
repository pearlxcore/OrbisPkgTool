using System.Reflection;
using LibOrbisPkg;
using LibOrbisPkg.GP4;
using LibOrbisPkg.PKG;
using LibOrbisPkg.PFS;

namespace OrbisPkgTool.Pkg;

/// <summary>
/// Wraps LibOrbisPkg's proven PFS/PKG builder. Pure C#, no orbis-pub-cmd.
/// </summary>
public static class LibOrbisBuilder
{
    public static void Build(string gp4Path, string projectFolder, string outputPath,
        string passcode = "00000000000000000000000000000000")
    {
        // Pre-process GP4 for LibOrbisPkg compatibility
        string gp4Xml = File.ReadAllText(gp4Path);
        bool changed = false;

        // 1. Fix content_id to exactly 36 chars (LibOrbisPkg requirement)
        string cidTag = "content_id=\"";
        int cidIdx = gp4Xml.IndexOf(cidTag);
        if (cidIdx > 0)
        {
            int cidStart = cidIdx + cidTag.Length;
            int cidEnd = gp4Xml.IndexOf('"', cidStart);
            string cid = gp4Xml[cidStart..cidEnd];
            if (cid.Length != 36)
            {
                while (cid.Length < 36) cid += "0";
                if (cid.Length > 36) cid = cid[..36];
                gp4Xml = gp4Xml[..cidStart] + cid + gp4Xml[cidEnd..];
                changed = true;
            }
        }

        // 2. Keep img_no attribute — LibOrbisPkg might need it

        // 3. Add rootdir entries for any subdirectories
        if (!gp4Xml.Contains("<dir ") && gp4Xml.Contains("targ_path=\""))
        {
            var dirs = new HashSet<string>();
            int pos = 0;
            while (true)
            {
                int i = gp4Xml.IndexOf("targ_path=\"", pos);
                if (i < 0) break;
                i += 11;
                int e = gp4Xml.IndexOf('"', i);
                string path = gp4Xml[i..e];
                int slash = path.LastIndexOf('/');
                if (slash > 0) dirs.Add(path[..slash]);
                pos = e;
            }
            string dirXml = string.Join("", dirs.Select(d => $"<dir targ_name=\"{d}\"/>"));
            gp4Xml = gp4Xml.Replace("<rootdir/>", $"<rootdir>{dirXml}</rootdir>");
            changed = true;
        }

        if (changed)
        {
            gp4Path = Path.Combine(Path.GetTempPath(), "_orbis_fixed.gp4");
            File.WriteAllText(gp4Path, gp4Xml);
        }

        // Parse GP4 and build PKG
        Gp4Project gp4;
        using (var fs = File.OpenRead(gp4Path))
            gp4 = Gp4Project.ReadFrom(fs);

        var props = PkgProperties.FromGp4(gp4, projectFolder);
        props.Passcode = passcode;
        if (props.TimeStamp == default)
            props.TimeStamp = DateTime.UtcNow;

        var builder = new LibOrbisPkg.PKG.PkgBuilder(props);
        builder.Write(outputPath, s => {
            if (!s.Contains("innerpfs]") && !s.Contains("outerpfs]"))
                Console.WriteLine($"[liborbis] {s}");
        });
    }
}
