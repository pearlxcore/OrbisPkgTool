namespace OrbisPkgTool.Pkg;

/// <summary>PFSC block storage mode.</summary>
public enum PfscMode
{
    /// <summary>Every block stored raw at its 0x10000 slot (stable default — proven on console-format fixtures).</summary>
    Store,

    /// <summary>Compressed blocks (48 89 + raw deflate + BE Adler32) with raw fallback — proven against orbis-pub-cmd.</summary>
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

    /// <summary>PFSC storage mode. Default: Store (stable until console testing).</summary>
    public PfscMode PfscMode { get; set; } = PfscMode.Store;

    /// <summary>Run the 8-stage structured validation on the finished PKG.</summary>
    public bool Validate { get; set; }

    /// <summary>When set, writes a build manifest (JSON) to this path.</summary>
    public string? ManifestPath { get; set; }

    /// <summary>Progress callback: (stage, bytes done, bytes total). long-based.</summary>
    public Action<BuildStage, long, long>? Progress { get; set; }

    /// <summary>Cancellation token — abort mid-build with clean temp cleanup.</summary>
    public System.Threading.CancellationToken CancellationToken { get; set; }
}
