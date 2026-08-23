using System.Diagnostics;
using OrbisPkgTool.Gp4;
using OrbisPkgTool.Pfs;
using OrbisPkgTool.Pkg;
using OrbisPkgTool.Sfo;

namespace OrbisPkgTool;

/// <summary>Options for merging a base game PKG with a patch PKG.</summary>
public sealed record class PkgMergeRequest
{
    public required string BasePkgPath { get; init; }
    public required string UpdatePkgPath { get; init; }
    public string? OutputPkgPath { get; init; }
    public string? BasePasscode { get; init; }
    public string? UpdatePasscode { get; init; }
    public bool ValidateAfterBuild { get; init; }
    public PfscMode PfscMode { get; init; } = PfscMode.Compressed;
    public int WorkerCount { get; init; } = 1;
    /// <summary>
    /// Parent directory for an API-owned, unique merge work directory. The
    /// parent itself is never deleted. When null, the system temp directory is
    /// used as the parent.
    /// </summary>
    public string? WorkDirectory { get; init; }
    public bool KeepWorkDirectory { get; init; }
    public string? Title { get; init; }
    public string? TitleId { get; init; }
    public string? ContentId { get; init; }
    public IProgress<PkgMergeProgress>? Progress { get; init; }
    public CancellationToken CancellationToken { get; init; }
}

/// <summary>A progress update emitted by <see cref="PkgMergeService"/>.</summary>
public sealed record PkgMergeProgress(string Stage, int CurrentItem = 0,
    int TotalItems = 0, string? CurrentFile = null, long CurrentBytes = 0,
    long TotalBytes = 0)
{
    public double? Percentage => TotalBytes > 0 ? 100d * CurrentBytes / TotalBytes :
        TotalItems > 0 ? 100d * CurrentItem / TotalItems : null;
}

/// <summary>The result of a successful package merge.</summary>
public sealed record PkgMergeResult(string OutputPkgPath, string WorkDirectory,
    long OutputSize, TimeSpan Elapsed, bool WorkDirectoryKept);

/// <summary>Creates a base-game PKG by overlaying a patch PKG onto its base.</summary>
public sealed class PkgMergeService
{
    public PkgMergeResult Merge(PkgMergeRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        ValidateRequest(request);
        var ct = request.CancellationToken;
        ct.ThrowIfCancellationRequested();

        string basePath = Path.GetFullPath(request.BasePkgPath);
        string updatePath = Path.GetFullPath(request.UpdatePkgPath);
        string basePasscode = string.IsNullOrWhiteSpace(request.BasePasscode) ? PkgBuilder.DefaultPasscode : request.BasePasscode;
        string updatePasscode = string.IsNullOrWhiteSpace(request.UpdatePasscode) ? PkgBuilder.DefaultPasscode : request.UpdatePasscode;
        var baseInfo = ReadInfo(basePath, basePasscode);
        var updateInfo = ReadInfo(updatePath, updatePasscode);
        ValidatePackages(basePath, updatePath, baseInfo, updateInfo);

        string workDir = CreateWorkDirectory(basePath, request.WorkDirectory);
        string outputPath = request.OutputPkgPath is { Length: > 0 }
            ? Path.GetFullPath(request.OutputPkgPath)
            : Path.Combine(workDir, Path.GetFileNameWithoutExtension(basePath) + "_merged.pkg");
        EnsureDistinctOutput(outputPath, basePath, updatePath);
        Directory.CreateDirectory(workDir);
        string dumpBase = Path.Combine(workDir, "dump_base");
        string dumpUpdate = Path.Combine(workDir, "dump_upd");
        string image0 = Path.Combine(dumpBase, "Image0");
        var stopwatch = Stopwatch.StartNew();
        try
        {
            Report(request, "Extracting base PKG");
            Extract(basePath, basePasscode, dumpBase, request, "Extracting base PKG");
            var baseProfile = request.PfscMode == PfscMode.Compressed ? Profile(basePath, basePasscode) : null;

            Report(request, "Extracting update PKG");
            Extract(updatePath, updatePasscode, dumpUpdate, request, "Extracting update PKG");
            var updateProfile = request.PfscMode == PfscMode.Compressed ? Profile(updatePath, updatePasscode) : null;

            Report(request, "Overlaying update files");
            Overlay(dumpBase, dumpUpdate, updateInfo.AppVersion, ct);
            TryDeleteUpdateDump(dumpUpdate, request);

            Report(request, "Restructuring merged files");
            Restructure(dumpBase, ct);
            var profile = MergeProfiles(baseProfile, updateProfile);

            Report(request, "Generating GP4 project");
            string gp4Path = Path.Combine(workDir, "project.gp4");
            var project = Gp4Project.FromFolder(image0, false, request.Title, request.TitleId,
                request.ContentId, PkgBuilder.DefaultPasscode, profile);
            File.WriteAllText(gp4Path, project.Serialize());

            Report(request, "Building merged PKG");
            PkgBuilder.Build(gp4Path, image0, outputPath, new BuildOptions
            {
                Passcode = PkgBuilder.DefaultPasscode,
                PfscMode = request.PfscMode,
                PfscProfile = profile,
                ContentTypeOverride = baseInfo.ContentType == 0 ? null : baseInfo.ContentType,
                ContentFlagsOverride = baseInfo.ContentFlags == 0 ? null : baseInfo.ContentFlags,
                Workers = request.WorkerCount,
                Quiet = true,
                CancellationToken = ct,
                Progress = (stage, done, total) => Report(request, $"Building: {stage}", currentBytes: done, totalBytes: total),
            });
            if (!File.Exists(outputPath)) throw new InvalidOperationException("PKG build completed without creating an output package.");

            if (request.ValidateAfterBuild)
            {
                Report(request, "Validating merged PKG");
                PkgValidator.ValidatePkgFile(outputPath, PkgBuilder.DefaultPasscode,
                    report: (stage, item) => Report(request, $"Validating: {stage}", currentFile: item));
            }
            stopwatch.Stop();
            bool kept = KeepOrCleanup(workDir, outputPath, request.KeepWorkDirectory);
            return new PkgMergeResult(outputPath, workDir, new FileInfo(outputPath).Length, stopwatch.Elapsed, kept);
        }
        catch
        {
            // Deliberately retain all intermediates: they are essential for diagnosis.
            throw;
        }
    }

    internal static void ValidateRequest(PkgMergeRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.BasePkgPath)) throw new ArgumentException("BasePkgPath is required.", nameof(request));
        if (string.IsNullOrWhiteSpace(request.UpdatePkgPath)) throw new ArgumentException("UpdatePkgPath is required.", nameof(request));
        if (!File.Exists(request.BasePkgPath)) throw new FileNotFoundException("Base PKG was not found.", request.BasePkgPath);
        if (!File.Exists(request.UpdatePkgPath)) throw new FileNotFoundException("Update PKG was not found.", request.UpdatePkgPath);
        if (request.WorkerCount < 0) throw new ArgumentOutOfRangeException(nameof(request.WorkerCount));
        if (request.OutputPkgPath is { Length: > 0 }) EnsureDistinctOutput(Path.GetFullPath(request.OutputPkgPath), Path.GetFullPath(request.BasePkgPath), Path.GetFullPath(request.UpdatePkgPath));
    }

    private static PkgInfo ReadInfo(string path, string passcode) { using var reader = new PkgReader(path, passcode); return reader.GetInfo(); }
    internal static void ValidatePackages(string basePath, string updatePath, PkgInfo baseInfo, PkgInfo updateInfo)
    {
        if (baseInfo.Type != PkgType.Game) throw new InvalidOperationException($"Base PKG must be a base Game PKG; '{basePath}' is {baseInfo.Type}.");
        if (updateInfo.Type != PkgType.Patch) throw new InvalidOperationException($"Update PKG must be a Patch PKG; '{updatePath}' is {updateInfo.Type}.");
        if (!string.Equals(baseInfo.TitleId, updateInfo.TitleId, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException($"TITLE_ID mismatch: base={baseInfo.TitleId} update={updateInfo.TitleId}.");
    }
    private static void EnsureDistinctOutput(string output, string basePath, string updatePath)
    {
        if (string.Equals(output, basePath, StringComparison.OrdinalIgnoreCase) || string.Equals(output, updatePath, StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("OutputPkgPath must not be either input PKG.", nameof(output));
    }
    internal static string CreateWorkDirectory(string basePath, string? parentDirectory)
    {
        string safe = new string(Path.GetFileNameWithoutExtension(basePath).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c).ToArray());
        string parent = string.IsNullOrWhiteSpace(parentDirectory) ? Path.GetTempPath() : Path.GetFullPath(parentDirectory);
        return Path.Combine(parent,
            $"pkg_merge_{safe[..Math.Min(40, safe.Length)]}_{Guid.NewGuid().ToString("N")[..12]}");
    }
    private static void Extract(string pkg, string passcode, string output, PkgMergeRequest request, string stage)
    {
        using var reader = new PkgReader(pkg, passcode); Directory.CreateDirectory(output);
        var failures = reader.ExtractAll(output,
            new Progress<(int Current, int Total, string CurrentFile)>(p => Report(request, stage, p.Current, p.Total, p.CurrentFile)),
            new ExtractAllOptions { CancellationToken = request.CancellationToken });
        if (failures.Count > 0) throw new InvalidOperationException($"{failures.Count} file(s) failed to extract: {string.Join("; ", failures.Take(3).Select(f => f.Path))}");
    }
    private static List<PfscFilePolicy>? Profile(string pkg, string passcode)
    {
        try { return PfscProfiler.Profile(pkg, passcode, out _, out _); } catch { return null; }
    }
    private static Dictionary<string, PfscPolicy>? MergeProfiles(List<PfscFilePolicy>? baseFiles, List<PfscFilePolicy>? updateFiles)
    {
        if (baseFiles == null) return null;
        var profile = baseFiles.ToDictionary(f => f.Path, f => f.Policy, StringComparer.OrdinalIgnoreCase);
        if (updateFiles != null) foreach (var file in updateFiles) profile[file.Path] = file.Policy;
        return profile;
    }
    private static void Overlay(string baseDump, string updateDump, string updateVersion, CancellationToken ct)
    {
        foreach (string area in new[] { "Image0", "Sc0" })
        {
            string source = Path.Combine(updateDump, area); if (!Directory.Exists(source)) continue;
            string target = Path.Combine(baseDump, area);
            foreach (string file in Directory.GetFiles(source, "*", SearchOption.AllDirectories))
            {
                ct.ThrowIfCancellationRequested(); string relative = Path.GetRelativePath(source, file).Replace('\\', '/');
                bool sfo = area == "Sc0" && relative.Equals("param.sfo", StringComparison.OrdinalIgnoreCase) || area == "Image0" && relative.Equals("sce_sys/param.sfo", StringComparison.OrdinalIgnoreCase);
                if (sfo) continue;
                string destination = Path.Combine(target, relative); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Move(file, destination, true);
            }
        }
        if (string.IsNullOrEmpty(updateVersion)) return;
        foreach (string sfoPath in new[] { Path.Combine(baseDump, "Sc0", "param.sfo"), Path.Combine(baseDump, "Image0", "sce_sys", "param.sfo") })
        {
            if (!File.Exists(sfoPath)) continue; var sfo = ParamSfo.Parse(File.ReadAllBytes(sfoPath)); sfo.SetString("APP_VER", updateVersion, 8); sfo.SetString("VERSION", updateVersion, 8); File.WriteAllBytes(sfoPath, sfo.Serialize());
        }
    }
    private static void Restructure(string dump, CancellationToken ct)
    {
        string image0 = Path.Combine(dump, "Image0"), sc0 = Path.Combine(dump, "Sc0"), sceSys = Path.Combine(image0, "sce_sys");
        if (!Directory.Exists(image0)) throw new InvalidOperationException("Base extraction does not contain Image0.");
        if (Directory.Exists(sc0)) { Directory.CreateDirectory(sceSys); foreach (var file in Directory.GetFiles(sc0, "*", SearchOption.AllDirectories)) { ct.ThrowIfCancellationRequested(); string destination = Path.Combine(sceSys, Path.GetRelativePath(sc0, file)); Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Move(file, destination, true); } Directory.Delete(sc0, true); }
        string original = Path.Combine(sceSys, "param.sfo.original"); if (File.Exists(original)) File.Delete(original);
    }
    private static void TryDeleteUpdateDump(string dumpUpdate, PkgMergeRequest request)
    {
        // The update has already been moved over the base at this stage. This
        // deletion only releases disk space before the rebuild, so a Windows
        // filesystem cleanup error must not discard an otherwise valid merge.
        try
        {
            if (Directory.Exists(dumpUpdate))
                Directory.Delete(dumpUpdate, recursive: true);
        }
        catch (IOException ex)
        {
            Report(request, "Update dump retained for cleanup", currentFile: ex.Message);
        }
        catch (UnauthorizedAccessException ex)
        {
            Report(request, "Update dump retained for cleanup", currentFile: ex.Message);
        }
    }
    private static bool KeepOrCleanup(string workDir, string output, bool keep)
    {
        string work = Path.TrimEndingDirectorySeparator(Path.GetFullPath(workDir)) + Path.DirectorySeparatorChar;
        if (keep || Path.GetFullPath(output).StartsWith(work, StringComparison.OrdinalIgnoreCase)) return true;
        try { Directory.Delete(workDir, true); return false; } catch { return true; }
    }
    private static void Report(PkgMergeRequest r, string stage, int current = 0, int total = 0, string? currentFile = null, long currentBytes = 0, long totalBytes = 0) => r.Progress?.Report(new PkgMergeProgress(stage, current, total, currentFile, currentBytes, totalBytes));
}
