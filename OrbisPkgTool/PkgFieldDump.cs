using System.Buffers.Binary;
using System.Text;

// Full structural field dump of a PKG — every offset/size/flag value
// shadPS4 and other readers consume. Run on original and rebuilt, diff
// the outputs. Usage: pkgfields <pkg> [--entries] [--pfs]
namespace OrbisPkgTool;

static class PkgFieldDump
{
    public static int Run(string[] args)
    {
        string? pkg = null;
        bool entries = false, pfs = false;
        foreach (var a in args)
        {
            switch (a)
            {
                case "--entries": entries = true; break;
                case "--pfs": pfs = true; break;
                default:
                    if (!a.StartsWith('-')) pkg = a;
                    break;
            }
        }
        if (pkg == null || !File.Exists(pkg)) { Console.Error.WriteLine("usage: pkgfields <pkg> [--entries] [--pfs]"); return 2; }

        using var fs = File.OpenRead(pkg);
        long fileLen = fs.Length;
        byte[] h = new byte[0x1100];
        fs.ReadExactly(h);

        Console.WriteLine($"=== PKG: {Path.GetFileName(pkg)} ===");
        Console.WriteLine($"  file_size           = {fileLen} (0x{fileLen:X})");

        // ---- Header (big-endian) ----
        Console.WriteLine("-- PKG header --");
        Dump32("magic", 0x00, h); Dump32("flags", 0x04, h);
        Dump32("unk_0x08", 0x08, h); Dump32("unk_0x0C", 0x0C, h);
        Dump32("entry_count", 0x10, h);
        Dump16("sc_entry_count", 0x14, h); Dump16("entry_count_2", 0x16, h);
        Dump32("entry_table_offset", 0x18, h); Dump32("main_ent_data_size", 0x1C, h);
        Dump64("body_offset", 0x20, h); Dump64("body_size", 0x28, h);
        var cid = Encoding.ASCII.GetString(h, 0x40, 0x30).TrimEnd('\0');
        Console.WriteLine($"  content_id[0x40]    = {cid}");
        Dump32("drm_type[0x70]", 0x70, h); Dump32("content_type[0x74]", 0x74, h);
        Dump32("content_flags[0x78]", 0x78, h);
        Dump32("promote_size[0x7C]", 0x7C, h);
        Dump32("version_date[0x80]", 0x80, h); Dump32("version_hash[0x84]", 0x84, h);
        Dump32("iro_tag[0x98]", 0x98, h); Dump32("ekc_version[0x9C]", 0x9C, h);
        Dump32("unk_0x400", 0x400, h); Dump32("pfs_image_count[0x404]", 0x404, h);
        Dump64("pfs_flags[0x408]", 0x408, h);
        Dump64("pfs_image_offset[0x410]", 0x410, h);
        Dump64("pfs_image_size[0x418]", 0x418, h);
        Dump64("mount_image_offset[0x420]", 0x420, h);
        Dump64("mount_image_size[0x428]", 0x428, h);
        Dump64("package_size[0x430]", 0x430, h);
        Dump32("pfs_signed_size[0x438]", 0x438, h);
        Dump32("pfs_cache_size[0x43C]", 0x43C, h);
        Dump64("pfs_split_size_nth_0[0x480]", 0x480, h);
        Dump64("pfs_split_size_nth_1[0x488]", 0x488, h);

        // ---- Entry table ----
        uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(0x10));
        uint tableOff = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(0x18));
        if (entries)
        {
            Console.WriteLine("-- entry table --");
            fs.Position = tableOff;
            for (int i = 0; i < entryCount; i++)
            {
                byte[] e = new byte[32]; fs.ReadExactly(e);
                uint id = BinaryPrimitives.ReadUInt32BigEndian(e);
                uint nameOff = BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(4));
                uint f1 = BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(8));
                uint f2 = BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(12));
                uint off = BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(16));
                uint sz = BinaryPrimitives.ReadUInt32BigEndian(e.AsSpan(20));
                bool enc = (f1 & 0x80000000) != 0;
                int keyIdx = enc ? (int)((f2 >> 12) & 7) : 0;
                long stored = enc ? (sz + 15) & ~15L : sz;
                string name = PkgEntryName(id);
                Console.WriteLine($"  [{i,2}] id=0x{id:X4} {name,-28} off=0x{off:X8} size={sz,7} stored={stored,7} flags1=0x{f1:X8} flags2=0x{f2:X8} enc={enc} key={keyIdx}");
            }
        }

        // ---- PFS / PFSC ----
        if (pfs)
        {
            ulong pfsOff = BinaryPrimitives.ReadUInt64BigEndian(h.AsSpan(0x410));
            ulong pfsSize = BinaryPrimitives.ReadUInt64BigEndian(h.AsSpan(0x418));
            uint cacheSz = BinaryPrimitives.ReadUInt32BigEndian(h.AsSpan(0x43C));
            Console.WriteLine("-- PFS/PFSC --");
            Console.WriteLine($"  pfs_image_offset    = 0x{pfsOff:X}");
            Console.WriteLine($"  pfs_image_size      = {pfsSize} (0x{pfsSize:X})");
            Console.WriteLine($"  pfs_cache_size      = 0x{cacheSz:X} (shadPS4 reads {cacheSz*2} bytes)");
            Console.WriteLine($"  cache*2             = 0x{cacheSz*2:X}");

            // outer PFS header (inside pfs_image, first 0x50 bytes)
            fs.Position = (long)pfsOff;
            byte[] ph = new byte[0x50];
            fs.ReadExactly(ph);
            Console.WriteLine("  outer PFS header (encrypted view — structure only if decryptable):");
            Dump64("  version[0x00]", 0x00, ph); Dump64("  magic[0x08]", 0x08, ph);
            Dump16("  mode[0x1C]", 0x1C, ph); Dump32("  blocksz[0x20]", 0x20, ph);
            Dump64("  nblock[0x28]", 0x28, ph); Dump64("  ndinode[0x30]", 0x30, ph);
            Dump64("  ndblock[0x38]", 0x38, ph); Dump64("  ndinodeblock[0x40]", 0x40, ph);
            Dump64("  superroot_ino[0x48]", 0x48, ph);
        }
        return 0;
    }

    static void Dump32(string label, int off, byte[] b)
    {
        if (off + 4 > b.Length) return;
        uint v = BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(off, 4));
        Console.WriteLine($"  {label,-26} = 0x{v:X8} ({v})");
    }
    static void Dump16(string label, int off, byte[] b)
    {
        if (off + 2 > b.Length) return;
        ushort v = BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(off, 2));
        Console.WriteLine($"  {label,-26} = 0x{v:X4} ({v})");
    }
    static void Dump64(string label, int off, byte[] b)
    {
        if (off + 8 > b.Length) return;
        ulong v = BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(off, 8));
        Console.WriteLine($"  {label,-26} = 0x{v:X16} ({v})");
    }

    static string PkgEntryName(uint id) => id switch
    {
        0x0001 => "digests",
        0x0010 => "entry_keys",
        0x0020 => "image_key",
        0x0080 => "general_digests",
        0x0100 => "metas",
        0x0200 => "entry_names",
        0x0400 => "license.dat",
        0x0401 => "license.info",
        0x0402 => "nptitle.dat",
        0x0403 => "npbind.dat",
        0x0404 => "selfinfo.dat",
        0x0406 => "imageinfo.dat",
        0x0409 => "psreserved.dat",
        0x1000 => "param.sfo",
        0x1001 => "playgo-chunk.dat",
        0x1002 => "playgo-chunk.sha",
        0x1003 => "playgo-manifest.xml",
        0x1004 => "pronunciation.xml",
        0x1005 => "pronunciation.sig",
        0x1006 => "pic1.png",
        0x100B => "shareparam.json",
        0x100C => "shareoverlayimage.png",
        0x1200 => "icon0.png",
        0x1220 => "pic0.png",
        0x1240 => "snd0.at9",
        0x1260 => "changeinfo.xml",
        0x1280 => "icon0.dds",
        0x12A0 => "pic0.dds",
        0x12C0 => "pic1.dds",
        0x1400 => "trophy00.trp",
        _ => "",
    };
}
