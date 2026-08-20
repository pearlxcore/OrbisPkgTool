using OrbisPkgTool.Binary;
using OrbisPkgTool.Crypto;
using OrbisPkgTool.Pfs;
using OrbisPkgTool.Pkg;

namespace OrbisPkgTool;

/// <summary>
/// Native C# replacement for orbis-pub-cmd.exe's img_file_list and
/// img_extract commands. Pure managed code — no native interop, no
/// external PKG libraries.
///
/// Reads a PS4 PKG, exposes the Sc0 filesystem (PKG entry table) and the
/// Image0 filesystem (inner PFS image), and extracts entries with the
/// passcode-derived AES decryption.
/// </summary>
public sealed class PkgReader : IDisposable
{
    public const string DefaultPasscode = "00000000000000000000000000000000";

    private readonly string _pkgPath;
    private readonly string _passcode;
    private readonly FileStream _stream;
    private readonly BigEndianReader _reader;
    private readonly PkgHeader _header;
    private readonly List<PkgEntry> _entries = [];
    private readonly Dictionary<uint, string> _nameTable = [];
    private readonly PkgKeySet _keySet;

    // Derived keys dk0..dk6 computed from the passcode.
    private readonly byte[][] _derivedKeys;

    // Lazy PFS layer state.
    private List<PkgFileEntry>? _image0Files;
    private byte[]? _ekpfs;
    private PfsReader? _innerPfs;

    /// <summary>Diagnostic: last PFS-layer error (null when the Image0 layer worked).</summary>
    public string? LastPfsError { get; private set; }

    /// <summary>Diagnostic: EKPFS recovery status.</summary>
    public string EkpfsStatus { get; private set; } = "not attempted";

    /// <summary>The recovered PFS encryption key (null when recovery failed).</summary>
    public byte[]? Ekpfs => _ekpfs;

    /// <summary>The opened inner PFS (Image0 layer), null when absent or unreadable.</summary>
    public Pfs.PfsReader? InnerPfs => _innerPfs;

    /// <summary>How the decryption keys were obtained (passcode vs RSA-recovered dk3).</summary>
    public string PasscodeStatus { get; private set; } = "not checked";

    /// <summary>Parsed Sc0/param.sfo (null when absent or unreadable).</summary>
    public Sfo.ParamSfo? ParamSfo { get; private set; }

    /// <summary>
    /// Reads Sc0/param.sfo and builds package metadata, including the
    /// DLC/theme/avatar/wallpaper add-on distinction.
    /// </summary>
    public PkgInfo GetInfo()
    {
        var info = new PkgInfo
        {
            ContentId = _header.ContentId,
            ContentType = _header.ContentType,
            ContentFlags = _header.ContentFlags,
        };
        var sfo = ReadParamSfo();
        if (sfo != null)
        {
            info.Title = sfo.GetString("TITLE");
            info.TitleId = sfo.GetString("TITLE_ID");
            info.AppVersion = sfo.GetString("APP_VER");
            // SYSTEM_VER is a packed u32 (e.g. 0x05050000) — format as hex.
            var sysVer = sfo["SYSTEM_VER"];
            info.SystemVersion = sysVer != null && sysVer.Format == 0x0404
                ? $"0x{sysVer.IntValue:X8}"
                : sfo.GetString("SYSTEM_VER");
            info.Category = sfo.GetString("CATEGORY");
        }
        info.Type = DetectType(info);
        return info;
    }

    /// <summary>
    /// Verifies the SHA256 digest of every entry against the PKG digests table
    /// (entry id 0x1) — the managed equivalent of orbis-pub-cmd's
    /// --integrity_check. Returns a list of mismatches (path or entry id).
    /// </summary>
    public List<string> VerifyIntegrity()
    {
        var failures = new List<string>();
        var digestsEntry = _entries.FirstOrDefault(e => e.Id == PkgEntryIds.Digests);
        if (digestsEntry == null)
        {
            failures.Add("no digests entry");
            return failures;
        }
        byte[] digests = _reader.ReadBytesAt(digestsEntry.DataOffset, (int)Math.Min(digestsEntry.DataSize, _entries.Count * 32));
        for (int i = 1; i < _entries.Count; i++)
        {
            var entry = _entries[i];
            if (entry.DataOffset + entry.DataSize > _stream.Length)
                continue;
            // Encrypted entries are stored as 16-byte-aligned ciphertext (the
            // digest covers the aligned region); plaintext entries are stored
            // exactly at DataSize.
            long storedSize = entry.IsEncrypted
                ? (entry.DataSize + 15) & ~15L
                : entry.DataSize;
            long nextEntry = _entries.Where(e => e.DataOffset > entry.DataOffset)
                .Select(e => e.DataOffset).DefaultIfEmpty(_stream.Length).Min();
            storedSize = Math.Min(storedSize, nextEntry - entry.DataOffset);
            byte[] data = _reader.ReadBytesAt(entry.DataOffset, (int)storedSize);
            byte[] hash = Crypto.PkgCrypto.Sha256(data);
            byte[] expected = new byte[32];
            Buffer.BlockCopy(digests, i * 32, expected, 0, 32);
            if (!hash.AsSpan().SequenceEqual(expected))
            {
                string name = ResolveName(entry) ?? $"entry 0x{entry.Id:X8}";
                failures.Add(name);
            }
        }
        return failures;
    }

    /// <summary>Reads and caches the Sc0/param.sfo entry (decrypted if needed).</summary>
    public Sfo.ParamSfo? ReadParamSfo()
    {
        if (ParamSfo != null)
            return ParamSfo;
        var entry = _entries.FirstOrDefault(e => e.Id == PkgEntryIds.ParamSfo);
        if (entry == null)
            return null;
        try
        {
            ParamSfo = Sfo.ParamSfo.Parse(ReadEntryData(entry));
        }
        catch
        {
            ParamSfo = null;
        }
        return ParamSfo;
    }

    private static PkgType DetectType(PkgInfo info)
    {
        // param.sfo CATEGORY is the authoritative discriminator when present
        // (patches can carry content type 0x1A like games).
        string category = info.Category.ToLowerInvariant();
        if (category.Length > 0)
        {
            switch (category)
            {
                case "gd": return PkgType.Game;
                case "gp": return PkgType.Patch;
                case "th": return PkgType.Theme;
                case "av": return PkgType.Avatar;
                case "wa": return PkgType.Wallpaper;
                case "ac":
                case "al":
                    return ClassifyAddon(info);
                default:
                    return PkgType.Addon;
            }
        }
        // Fall back to the PKG header content type.
        // 0x1A GD (game), 0x1E DP (patch), 0x1B AC (addon content), 0x1C AL (addon, no data).
        return info.ContentType switch
        {
            0x1A => PkgType.Game,
            0x1E => PkgType.Patch,
            _ => ClassifyAddon(info),
        };
    }

    /// <summary>
    /// Distinguishes DLC from themes/avatars/wallpapers among add-on packages:
    /// official PS4 themes use the IP9100 content-id prefix, and scene/fake
    /// themes carry "theme" in the title.
    /// </summary>
    private static PkgType ClassifyAddon(PkgInfo info)
    {
        if (info.ContentId.StartsWith("IP9100", StringComparison.OrdinalIgnoreCase) ||
            info.ContentId.StartsWith("IP9102", StringComparison.OrdinalIgnoreCase) ||
            info.ContentId.StartsWith("IP9104", StringComparison.OrdinalIgnoreCase))
            return PkgType.Theme;
        if (info.Title.Contains("Theme", StringComparison.OrdinalIgnoreCase))
            return PkgType.Theme;
        return PkgType.Dlc;
    }

    /// <summary>True when the file opened as a valid PS4 PKG.</summary>
    public bool IsValidPkg => _header.IsValid;

    public PkgHeader Header => _header;

    /// <summary>The PKG file path this reader was opened with.</summary>
    public string PkgPath => _pkgPath;

    /// <summary>Raw entry table (Sc0 system entries).</summary>
    public IReadOnlyList<PkgEntry> Entries => _entries;

    public PkgReader(string pkgPath, string passcode = DefaultPasscode, PkgKeySet? keySet = null)
    {
        if (!File.Exists(pkgPath))
            throw new FileNotFoundException("PKG file not found", pkgPath);
        if (passcode.Length != 32)
            throw new ArgumentException("Passcode must be exactly 32 characters", nameof(passcode));

        _pkgPath = pkgPath;
        _passcode = passcode;
        _keySet = keySet ?? PkgKeySet.Standard;
        _stream = new FileStream(pkgPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            _reader = new BigEndianReader(_stream);
            _header = PkgHeader.Read(_reader);
            if (!_header.IsValid)
                throw new InvalidDataException("Not a PS4 PKG file (bad magic).");

            ReadEntryTable();
            _derivedKeys = new byte[7][];
            for (uint i = 0; i < 7; i++)
                _derivedKeys[i] = PkgCrypto.DeriveKey(_header.ContentId, _passcode, i);
            ValidatePasscode();
        }
        catch
        {
            _stream.Dispose();
            throw;
        }
    }

    // ------------------------------------------------------------------
    // img_file_list equivalent
    // ------------------------------------------------------------------

    /// <summary>
    /// Lists all files and directories in the PKG: Sc0 system entries from
    /// the PKG entry table plus Image0 game files from the inner PFS.
    /// </summary>
    public List<PkgFileEntry> ListFiles()
    {
        var result = new List<PkgFileEntry>();
        result.AddRange(ListSc0Files());
        result.AddRange(ListImage0Files());
        return result;
    }

    private List<PkgFileEntry> ListSc0Files()
    {
        var result = new List<PkgFileEntry>();
        foreach (var e in _entries)
        {
            // Meta/table entries are not content files — never expose them.
            if (e.Id is PkgEntryIds.Digests or PkgEntryIds.EntryKeys
                or PkgEntryIds.ImageKey or PkgEntryIds.GeneralDigests
                or PkgEntryIds.Metas or PkgEntryIds.EntryNames)
                continue;
            string? name = ResolveName(e);
            string path;
            if (string.IsNullOrEmpty(name))
            {
                // Unnamed entries (e.g. delta-info 0x0408, playgo 0x1008) are
                // carried through with a synthetic name so no content is lost.
                path = $"Sc0/entry_{e.Id:X4}.bin";
            }
            else
            {
                path = PrefixSc0(name);
            }
            result.Add(new PkgFileEntry
            {
                Path = path,
                Name = Path.GetFileName(path.Replace('/', Path.DirectorySeparatorChar)),
                Size = e.DataSize,
                PackedSize = e.DataSize,
                IsDirectory = false,
                IsEncrypted = e.IsEncrypted,
                EntryId = (int)e.Id,
                Offset = e.DataOffset,
            });
        }
        // Synthesize directory entries (all path prefixes).
        AddParentDirectories(result);
        return result;
    }

    private List<PkgFileEntry> ListImage0Files()
    {
        if (_image0Files != null)
            return _image0Files;

        var result = new List<PkgFileEntry>();
        var inner = OpenInnerPfs();
        if (inner != null)
        {
            // Inner PFS root (uroot) contains the game tree.
            // (Inode 2 can be a collision_resolver when FPT hashes collide —
            // then uroot is inode 3.)
            var rootIno = inner.GetInode(inner.UrootInode);
            if (rootIno != null)
                WalkPfsTree(inner, rootIno, "", result);
        }
        AddParentDirectories(result);
        _image0Files = result;
        return result;
    }

    private static void WalkPfsTree(PfsReader pfs, PfsInode dir, string prefix, List<PkgFileEntry> result,
        HashSet<uint>? visited = null)
    {
        visited ??= [];
        foreach (var d in pfs.ReadDirents(dir))
        {
            if (d.Name is "." or ".." or "flat_path_table")
                continue;
            if (d.InodeNumber >= pfs.InodeCount)
                continue;
            var ino = pfs.GetInode(d.InodeNumber);
            if (ino == null) continue;
            string path = prefix.Length == 0 ? d.Name : prefix + "/" + d.Name;
            if (ino.IsDirectory)
            {
                // Cycle protection: skip if already visited on this branch
                if (!visited.Add(d.InodeNumber))
                    continue;
                result.Add(new PkgFileEntry
                {
                    Path = "Image0/" + path,
                    Name = d.Name,
                    IsDirectory = true,
                    IsEncrypted = false,
                });
                WalkPfsTree(pfs, ino, path, result, visited);
                visited.Remove(d.InodeNumber);
            }
            else
            {
                long size = ino.SizeCompressed > 0 && ino.SizeCompressed != ino.Size ? ino.SizeCompressed : ino.Size;
                result.Add(new PkgFileEntry
                {
                    Path = "Image0/" + path,
                    Name = d.Name,
                    Size = size,
                    PackedSize = ino.Size,
                    IsDirectory = false,
                    IsEncrypted = false,
                    Offset = ino.StartBlock > 0 ? pfs.PfsOffset + ino.StartBlock * 0x10000L : 0,
                });
            }
        }
    }

    // ------------------------------------------------------------------
    // img_extract equivalents
    // ------------------------------------------------------------------

    /// <summary>Extracts one entry (file or directory) to the output directory.</summary>
    public void ExtractFile(string entryPath, string outputDirectory)
    {
        entryPath = NormalizeEntryPath(entryPath);
        if (entryPath.StartsWith("Image0/", StringComparison.OrdinalIgnoreCase))
        {
            ExtractImage0(entryPath["Image0/".Length..], outputDirectory);
            return;
        }
        if (entryPath.StartsWith("Sc0/", StringComparison.OrdinalIgnoreCase))
        {
            ExtractSc0(entryPath["Sc0/".Length..], outputDirectory);
            return;
        }
        throw new FileNotFoundException($"Entry not found: {entryPath}");
    }

    /// <summary>Extracts a single entry and returns the raw bytes.</summary>
    public byte[] ExtractEntryBytes(string entryPath)
    {
        entryPath = NormalizeEntryPath(entryPath);
        if (entryPath.StartsWith("Image0/", StringComparison.OrdinalIgnoreCase))
        {
            var inner = OpenInnerPfs() ?? throw new FileNotFoundException($"Entry not found: {entryPath}");
            var node = inner.FindFile(entryPath["Image0/".Length..]);
            if (node == null)
                throw new FileNotFoundException($"Entry not found: {entryPath}");
            if (node.Size > int.MaxValue) {
                using var src = inner.OpenFileStream(node);
                var dst = new MemoryStream();
                src.CopyTo(dst);
                return dst.ToArray();
            }
            return inner.ReadFileData(node);
        }
        if (entryPath.StartsWith("Sc0/", StringComparison.OrdinalIgnoreCase))
        {
            var e = FindSc0Entry(entryPath["Sc0/".Length..])
                ?? throw new FileNotFoundException($"Entry not found: {entryPath}");
            return ReadEntryData(e);
        }
        throw new FileNotFoundException($"Entry not found: {entryPath}");
    }

    /// <summary>Extracts all files (Sc0 + Image0) to the output directory.</summary>
    public void ExtractAll(string outputDirectory, IProgress<(int Current, int Total, string CurrentFile)>? progress = null)
        => ExtractAll(outputDirectory, progress, new ExtractAllOptions());

    /// <summary>Extracts all files (Sc0 + Image0) to the output directory.</summary>
    public List<ExtractionFailure> ExtractAll(string outputDirectory,
        IProgress<(int Current, int Total, string CurrentFile)>? progress,
        ExtractAllOptions options)
    {
        var failures = new List<ExtractionFailure>();
        var all = ListFiles();
        // Empty directories (e.g. patch PKGs' Media/Plugins, mono/etc/) exist
        // only as tree nodes — materialize them so the dump preserves the
        // full tree and the rebuild carries them through.
        foreach (var d in all.Where(f => f.IsDirectory))
        {
            try
            {
                Directory.CreateDirectory(SanitizeExtractPath(outputDirectory, d.Path));
            }
            catch (Exception ex) when (options.ContinueOnError)
            {
                failures.Add(new ExtractionFailure(d.Path, ex));
            }
        }
        var files = all.Where(f => !f.IsDirectory).ToList();
        int i = 0;
        foreach (var f in files)
        {
            options.CancellationToken.ThrowIfCancellationRequested();
            progress?.Report((i++, files.Count, f.Path));
            try
            {
                string dest = SanitizeExtractPath(outputDirectory, f.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                if (f.Path.StartsWith("Image0/", StringComparison.OrdinalIgnoreCase))
                {
                    ExtractImage0FileTo(f.Path, dest);
                }
                else
                {
                    // Prefer the exact entry by ID (unnamed entries are listed
                    // with synthetic "entry_XXXX.bin" names that FindSc0Entry
                    // cannot resolve by name).
                    var entry = _entries.FirstOrDefault(e => e.Id == (uint)f.EntryId)
                        ?? FindSc0Entry(Unprefix(f.Path));
                    if (entry == null)
                        throw new InvalidDataException(
                            $"Cannot resolve PKG entry for '{f.Path}' (entry id={f.EntryId}). " +
                            "The package may be corrupt or use an unnamed entry without a synthetic name.");
                    var data = ReadEntryData(entry);
                    File.WriteAllBytes(dest, data);
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) when (options.ContinueOnError)
            {
                failures.Add(new ExtractionFailure(f.Path, ex));
            }
        }
        return failures;
    }

    private void ExtractImage0FileTo(string entryPath, string destPath)
    {
        entryPath = NormalizeEntryPath(entryPath);
        var inner = OpenInnerPfs() ?? throw new FileNotFoundException($"Entry not found: {entryPath}");
        var node = inner.FindFile(entryPath["Image0/".Length..])
            ?? throw new FileNotFoundException($"Entry not found: {entryPath}");
        // Stream large files directly to disk, small files via memory
        if (node.Size > 512 * 1024 * 1024) // 512 MB threshold
        {
            using var src = inner.OpenFileStream(node);
            using var dst = File.Create(destPath);
            src.CopyTo(dst);
        }
        else
        {
            File.WriteAllBytes(destPath, inner.ReadFileData(node));
        }
    }

    // ------------------------------------------------------------------
    // Sc0 layer
    // ------------------------------------------------------------------

    /// <summary>
    /// Verifies the passcode against the ENTRY_KEYS digests
    /// (digest[i] = SHA256(dk_i) XOR dk_i), matching orbis-pub-cmd's
    /// "Passcode mismatch." behavior. When the passcode does not match
    /// (official/retail PKGs), falls back to RSA-recovering dk3 from
    /// ENTRY_KEYS[3] with the leaked key-3 private key — the same path
    /// scene tools use to read official PKGs without the passcode.
    /// </summary>
    private void ValidatePasscode()
    {
        var entryKeys = _entries.FirstOrDefault(e => e.Id == PkgEntryIds.EntryKeys);
        if (entryKeys == null || entryKeys.DataSize < 32 + 7 * 32)
            return;
        byte[] data = _reader.ReadBytesAt(entryKeys.DataOffset, (int)Math.Min(entryKeys.DataSize, 32 + 7 * 32 + 7 * 256));
        for (int i = 0; i < 7; i++)
        {
            if (DigestMatches(data, i, _derivedKeys[i]))
            {
                PasscodeStatus = "passcode verified";
                return; // passcode matches at least one key
            }
        }

        // Passcode mismatch: try RSA-recovering dk3 from ENTRY_KEYS[3].
        var enc = new byte[256];
        Buffer.BlockCopy(data, 32 + 7 * 32 + 3 * 256, enc, 0, 256);
        var recovered = PkgCrypto.TryRsaDecrypt(enc, _keySet.DerivedKey3);
        if (recovered is { Length: 32 } && DigestMatches(data, 3, recovered))
        {
            _derivedKeys[3] = recovered;
            PasscodeStatus = "passcode mismatch; using RSA-recovered dk3 (official PKG)";
            return;
        }
        throw new InvalidDataException("Passcode mismatch.");
    }

    private static bool DigestMatches(byte[] entryKeysData, int index, byte[] dk)
    {
        var expected = new byte[32];
        Buffer.BlockCopy(entryKeysData, 32 + index * 32, expected, 0, 32);
        var actual = (byte[])Crypto.PkgCrypto.Sha256(dk).Clone();
        for (int j = 0; j < 32; j++)
            actual[j] ^= dk[j];
        return actual.AsSpan().SequenceEqual(expected);
    }

    private void ReadEntryTable()
    {
        if (_header.EntryTableOffset + (long)_header.EntryCount * PkgEntry.Size > _stream.Length)
            throw new InvalidDataException("Entry table is out of bounds.");

        _reader.Position = _header.EntryTableOffset;
        PkgEntry? namesEntry = null;
        for (uint i = 0; i < _header.EntryCount; i++)
        {
            var e = PkgEntry.Read(_reader);
            _entries.Add(e);
            if (e.Id == PkgEntryIds.EntryNames)
                namesEntry = e;
        }
        if (namesEntry != null)
            ReadNameTable(namesEntry);
    }

    private void ReadNameTable(PkgEntry namesEntry)
    {
        if (namesEntry.DataOffset + namesEntry.DataSize > _stream.Length)
            return;
        _reader.Position = namesEntry.DataOffset;
        long end = namesEntry.DataOffset + namesEntry.DataSize;
        int offset = 0;
        while (_reader.Position < end)
        {
            string name = _reader.ReadAsciiNullTerminated(2048);
            if (name.Length > 0 && !_nameTable.ContainsKey((uint)offset))
                _nameTable[(uint)offset] = name;
            offset += name.Length + 1;
            if (name.Length == 0 && offset > 1)
                break; // padding
        }
    }

    private string? ResolveName(PkgEntry e)
    {
        if (_nameTable.TryGetValue(e.NameTableOffset, out var n))
            return n;
        return PkgEntryNames.TryGetName(e.Id);
    }

    private PkgEntry? FindSc0Entry(string name)
    {
        foreach (var e in _entries)
        {
            string? n = ResolveName(e);
            if (n == null) continue;
            if (string.Equals(n, name, StringComparison.OrdinalIgnoreCase))
                return e;
        }
        return null;
    }

    private void ExtractSc0(string name, string outputDirectory)
    {
        var e = FindSc0Entry(name);
        if (e == null)
            throw new FileNotFoundException($"Entry not found: Sc0/{name}");
        string dest = SanitizeExtractPath(outputDirectory, "Sc0/" + name);
        Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
        File.WriteAllBytes(dest, ReadEntryData(e));
    }

    private byte[] ReadEntryData(PkgEntry e)
    {
        // Encrypted entries store the FULL 16-aligned ciphertext region while
        // the table DataSize is the LOGICAL size (verified: the original
        // Digimon's npbind.dat is 532 with 544 stored bytes). Reading only
        // DataSize would truncate the last AES block and corrupt the tail.
        long stored = e.IsEncrypted ? (e.DataSize + 15) & ~15L : e.DataSize;
        if (e.DataOffset + stored > _stream.Length)
            throw new InvalidDataException("Entry data is out of bounds.");
        byte[] data = _reader.ReadBytesAt(e.DataOffset, (int)stored);
        if (e.IsEncrypted)
        {
            var dk = _derivedKeys[e.KeyIndex & 7];
            var (iv, key) = PkgCrypto.DeriveEntryKey(e, dk);
            data = PkgCrypto.DecryptAesCbc(key, iv, data, (int)e.DataSize);
        }
        return data;
    }

    // ------------------------------------------------------------------
    // Image0 layer (inner PFS)
    // ------------------------------------------------------------------

    /// <summary>Returns the outer PFS reader (for diagnostic tools like dumpinner).</summary>
    public PfsReader? GetOuterPfs()
    {
        // Ensure EKPFS is decrypted first
        OpenInnerPfs();
        if (_header.PfsImageOffset > 0 && _header.PfsImageOffset + _header.PfsImageSize <= (ulong)_stream.Length)
        {
            try { return PfsReader.Open(_reader, (long)_header.PfsImageOffset, _ekpfs); }
            catch { return null; }
        }
        return null;
    }

    /// <summary>Decrypts EKPFS from IMAGE_KEY if not already done.</summary>
    private void EnsureEkpfs()
    {
        var imageKeyEntry = _entries.FirstOrDefault(e => e.Id == PkgEntryIds.ImageKey);
        if (imageKeyEntry != null && _ekpfs == null)
        {
            byte[] imageKeyData = _reader.ReadBytesAt(imageKeyEntry.DataOffset, (int)imageKeyEntry.DataSize);
            _ekpfs = PkgCrypto.DecryptEkpfs(imageKeyEntry, imageKeyData, _derivedKeys[3], _keySet);
            EkpfsStatus = _ekpfs != null
                ? $"OK ({_ekpfs.Length} bytes)"
                : "FAILED (RSA decrypt with FakeKeyset returned null)";
        }
        else if (imageKeyEntry == null)
        {
            EkpfsStatus = "no IMAGE_KEY entry in PKG";
        }
    }

    /// <summary>
    /// Copies the raw decompressed inner PFS (starting with the PFS header, NOT "PFSC")
    /// to the given destination stream. Reuses the exact chain from OpenInnerPfs().
    /// </summary>
    public void CopyRawInnerPfsTo(Stream destination)
    {
        EnsureEkpfs();
        if (_header.PfsImageOffset == 0 || _ekpfs == null)
            throw new InvalidOperationException($"Cannot open inner PFS (offset={_header.PfsImageOffset}, ekpfs={_ekpfs != null})");

        var outer = PfsReader.Open(_reader, (long)_header.PfsImageOffset, _ekpfs);
        var innerFile = FindPfsFile(outer, "pfs_image.dat")
            ?? throw new InvalidOperationException("pfs_image.dat not found in outer PFS");
        Stream innerStream = outer.OpenFileStream(innerFile);

        // pfs_image.dat is a PFSC-compressed image; unwrap it.
        var probe = new byte[4];
        innerStream.Position = 0;
        if (innerStream.Read(probe, 0, 4) == 4 &&
            probe[0] == (byte)'P' && probe[1] == (byte)'F' &&
            probe[2] == (byte)'S' && probe[3] == (byte)'C')
        {
            innerStream = new PFSCStream(innerStream);
        }
        else
        {
            innerStream.Position = 0;
        }
        innerStream.CopyTo(destination);
    }

    /// <summary>Saves the raw decompressed inner PFS to a file.</summary>
    public void ExtractRawInnerPfs(string outputPath)
    {
        using var output = File.Create(outputPath);
        CopyRawInnerPfsTo(output);
    }

    /// <summary>
    /// Opens the RAW PFSC container stream (pfs_image.dat bytes, still
    /// XTS-encrypted-in-outer-PFS but decrypted by the outer PFS layer),
    /// positioned at 0. Returns null when the package has no PFSC
    /// (e.g. pfs_image.dat stored uncompressed) — callers then have no
    /// compression policy to profile.
    /// The returned stream shares the reader's underlying file handle;
    /// do not use it after disposing the PkgReader.
    /// </summary>
    public Stream? OpenRawPfscStream()
    {
        EnsureEkpfs();
        if (_header.PfsImageOffset == 0 || _ekpfs == null)
            return null;

        var outer = PfsReader.Open(_reader, (long)_header.PfsImageOffset, _ekpfs);
        var innerFile = FindPfsFile(outer, "pfs_image.dat")
            ?? throw new InvalidOperationException("pfs_image.dat not found in outer PFS");
        var s = outer.OpenFileStream(innerFile);
        var probe = new byte[4];
        s.Position = 0;
        if (s.Read(probe, 0, 4) == 4 &&
            probe[0] == (byte)'P' && probe[1] == (byte)'F' &&
            probe[2] == (byte)'S' && probe[3] == (byte)'C')
        {
            s.Position = 0;
            return s; // PFSC container
        }
        s.Dispose();
        return null;
    }

    private PfsReader? OpenInnerPfs()
    {
        if (_innerPfs != null)
            return _innerPfs;

        EnsureEkpfs();

        // Outer PFS at header.pfs_image_offset contains pfs_image.dat = inner PFS.
        if (_header.PfsImageOffset > 0 && _header.PfsImageOffset + _header.PfsImageSize <= (ulong)_stream.Length)
        {
            try
            {
                var outer = PfsReader.Open(_reader, (long)_header.PfsImageOffset, _ekpfs);
                var innerFile = FindPfsFile(outer, "pfs_image.dat");
                if (innerFile != null)
                {
                    // Stream lives as long as _innerPfs (it holds the reader).
                    Stream innerStream = outer.OpenFileStream(innerFile);
                    // pfs_image.dat is a PFSC-compressed image; unwrap it.
                    var probe = new byte[4];
                    innerStream.Position = 0;
                    if (innerStream.Read(probe, 0, 4) == 4 &&
                        probe[0] == (byte)'P' && probe[1] == (byte)'F' &&
                        probe[2] == (byte)'S' && probe[3] == (byte)'C')
                    {
                        innerStream = new PFSCStream(innerStream);
                    }
                    _innerPfs = PfsReader.Open(new BigEndianReader(innerStream), 0, _ekpfs);
                }
            }
            catch (Exception ex)
            {
                // Not fatal: Image0 listing simply stays empty.
                LastPfsError = ex.Message;
                _innerPfs = null;
            }
        }
        return _innerPfs;
    }

    private static PfsInode? FindPfsFile(PfsReader pfs, string name)
    {
        // Real FPKGs leave the uroot dirents empty — the lookup goes through
        // the flat path table: hash("/" + name) → inode.
        var fpt = pfs.GetInode(1); // flat_path_table
        if (fpt != null)
        {
            uint want = 0;
            foreach (var c in "/" + name)
                want = (uint)char.ToUpper(c) + 31 * want;
            var data = pfs.ReadFileData(fpt);
            for (int i = 0; i + 8 <= data.Length; i += 8)
            {
                uint hash = BitConverter.ToUInt32(data, i);
                uint ino = BitConverter.ToUInt32(data, i + 4);
                if (hash == want && (ino & 0x0FFFFFFF) < pfs.InodeCount)
                    return pfs.GetInode(ino & 0x0FFFFFFF);
            }
        }
        // fallback: scan the uroot dirents
        var root = pfs.GetInode(pfs.UrootInode);
        if (root != null)
            foreach (var d in pfs.ReadDirents(root))
                if (string.Equals(d.Name, name, StringComparison.OrdinalIgnoreCase) && d.InodeNumber < pfs.InodeCount)
                    return pfs.GetInode(d.InodeNumber);
        return null;
    }

    private void ExtractImage0(string path, string outputDirectory)
    {
        var inner = OpenInnerPfs() ?? throw new FileNotFoundException($"Entry not found: Image0/{path}");
        var files = new List<PkgFileEntry>();
        var node = inner.FindFile(path);
        if (node == null)
            throw new FileNotFoundException($"Entry not found: Image0/{path}");
        if (node.IsDirectory)
        {
            WalkPfsTree(inner, node, path, files);
            files = files.Where(f => !f.IsDirectory).ToList();
            foreach (var f in files)
            {
                string rel = Unprefix(f.Path);
                string dest = SanitizeExtractPath(outputDirectory, f.Path);
                Directory.CreateDirectory(Path.GetDirectoryName(dest)!);
                var fileIno = inner.FindFile(rel)!;
                if (fileIno.Size > int.MaxValue) {
                    // Large file — stream to disk
                    using var src = inner.OpenFileStream(fileIno);
                    using var dst = File.Create(dest);
                    src.CopyTo(dst);
                } else {
                    File.WriteAllBytes(dest, inner.ReadFileData(fileIno));
                }
            }
        }
        else
        {
            string dest = SanitizeExtractPath(outputDirectory, "Image0/" + Path.GetFileName(path));
            if (node.Size > int.MaxValue) {
                using var src = inner.OpenFileStream(node);
                using var dst = File.Create(dest);
                src.CopyTo(dst);
            } else {
                File.WriteAllBytes(dest, inner.ReadFileData(node));
            }
        }
    }

    // ------------------------------------------------------------------
    // helpers
    // ------------------------------------------------------------------

    private static void AddParentDirectories(List<PkgFileEntry> files)
    {
        var existing = new HashSet<string>(files.Select(f => f.Path), StringComparer.OrdinalIgnoreCase);
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var f in files)
        {
            string path = f.Path;
            int idx;
            while ((idx = path.LastIndexOf('/')) > 0)
            {
                path = path[..idx];
                dirs.Add(path);
            }
        }
        foreach (var d in dirs.OrderBy(x => x.Length))
        {
            if (existing.Contains(d))
                continue;
            files.Add(new PkgFileEntry
            {
                Path = d,
                Name = d[(d.LastIndexOf('/') + 1)..],
                IsDirectory = true,
            });
        }
    }

    private static string PrefixSc0(string name) =>
        name.StartsWith("Sc0/", StringComparison.OrdinalIgnoreCase) ? name : "Sc0/" + name;

    private static string NormalizeEntryPath(string path) =>
        path.Trim().TrimEnd('/').Replace('\\', '/');

    /// <summary>
    /// Maps an entry path (e.g. "Image0/app0/data.bin") to an absolute path
    /// inside <paramref name="outputDirectory"/>, rejecting anything that
    /// would escape it. PFS dirent names come straight from the package —
    /// a malicious or corrupt PKG could carry "../.." components, absolute
    /// paths (which Path.Combine keeps!), or drive-relative paths, any of
    /// which could write outside the extraction directory.
    /// </summary>
    private static string SanitizeExtractPath(string outputDirectory, string entryPath)
    {
        string relative = entryPath.Replace('/', Path.DirectorySeparatorChar);
        string full = Path.GetFullPath(Path.Combine(outputDirectory, relative));
        string root = Path.GetFullPath(outputDirectory);
        if (!root.EndsWith(Path.DirectorySeparatorChar))
            root += Path.DirectorySeparatorChar;
        if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Path traversal detected: entry '{entryPath}' resolves outside the output directory.");
        return full;
    }

    private static string Unprefix(string path) =>
        path.StartsWith("Sc0/", StringComparison.OrdinalIgnoreCase) ? path["Sc0/".Length..]
        : path.StartsWith("Image0/", StringComparison.OrdinalIgnoreCase) ? path["Image0/".Length..]
        : path;

    public void Dispose()
    {
        _innerPfs?.Dispose();
        _stream.Dispose();
    }
}
