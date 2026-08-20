namespace OrbisPkgTool.Pkg;

/// <summary>PFSC block storage mode.</summary>
public enum PfscMode
{
    /// <summary>Every block stored raw at its 0x10000 slot (explicit opt-in;
    /// produces a very large PKG for compressed games).</summary>
    Store,

    /// <summary>Compressed blocks (48 89 + raw deflate + BE Adler32) with raw
    /// fallback for incompressible data and per-file policy replay — proven
    /// against orbis-pub-cmd and shadPS4. This is the default: running
    /// `build`/`repack` without --pfsc-mode must never silently emit a fully
    /// uncompressed multi-GB package.</summary>
    Compressed,
}

/// <summary>Build pipeline stage (for progress reporting).</summary>
public enum BuildStage
{
    InnerPfs,
    Pfsc,
    OuterPfs,
    Assemble,
}

/// <summary>
/// Options for <see cref="PkgBuilder.Build(string, string, string, BuildOptions)"/>.
/// All fields are optional; defaults reproduce the proven pipeline exactly.
/// </summary>
public sealed class BuildOptions
{
    public string Passcode { get; set; } = PkgBuilder.DefaultPasscode;

    /// <summary>
    /// PFSC storage mode. Default: Compressed (matches orbis-pub-cmd behavior;
    /// the old Store default silently emitted fully-uncompressed PKGs).
    /// Per-file policy (GP4 pfs_compression / PfscProfile) applies only in
    /// Compressed mode.
    /// </summary>
    public PfscMode PfscMode { get; set; } = PfscMode.Compressed;

    /// <summary>
    /// Optional per-file compression policy captured from the ORIGINAL
    /// package (path → enable/disable), produced by PfscProfiler.Profile and
    /// fed to the builder at repack time. Overrides the GP4 attribute for
    /// the paths it covers; used only in Compressed mode.
    /// </summary>
    public IReadOnlyDictionary<string, OrbisPkgTool.Pfs.PfscPolicy>? PfscProfile { get; set; }

    /// <summary>
    /// Optional content_type (0x74) override — repack carries the original
    /// PKG's value through. Sony patches keep content_type=0x1A with patch
    /// FLAGS rather than switching to 0x1E.
    /// </summary>
    public uint? ContentTypeOverride { get; set; }

    /// <summary>Optional content_flags (0x78) override (repack carries it).</summary>
    public uint? ContentFlagsOverride { get; set; }

    /// <summary>Run the 8-stage structured validation on the finished PKG.</summary>
    public bool Validate { get; set; }

    /// <summary>When set, writes a build manifest (JSON) to this path.</summary>
    public string? ManifestPath { get; set; }

    /// <summary>Suppress the stderr policy/diagnostic notes.</summary>
    public bool Quiet { get; set; }

    /// <summary>Progress callback: (stage, bytes done, bytes total). long-based.</summary>
    public Action<BuildStage, long, long>? Progress { get; set; }

    /// <summary>
    /// Number of worker threads for parallelizable build stages (currently:
    /// PFSC compression). Default 1 = fully serial (the proven path). 0 =
    /// Environment.ProcessorCount. Values >1 compress PFSC blocks concurrently
    /// with bounded memory; output is byte-identical to the serial path
    /// (deflate is deterministic and blocks are written in order).
    /// </summary>
    public int Workers { get; set; } = 1;

    /// <summary>Cancellation token — abort mid-build with clean temp cleanup.</summary>
    public System.Threading.CancellationToken CancellationToken { get; set; }
}
