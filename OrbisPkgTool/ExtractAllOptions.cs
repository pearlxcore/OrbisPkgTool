namespace OrbisPkgTool;

/// <summary>Options for <see cref="PkgReader.ExtractAll(string, IProgress{ValueTuple{int, int, string}}, ExtractAllOptions)"/>.</summary>
public sealed class ExtractAllOptions
{
    /// <summary>
    /// When true (default), a per-file failure (I/O error, crypto failure,
    /// corrupt entry) is recorded and extraction continues with the remaining
    /// files. When false, the first failure aborts the whole extraction.
    /// Path-traversal rejections and cancellation always throw regardless.
    /// </summary>
    public bool ContinueOnError { get; set; } = true;

    /// <summary>Abort extraction cooperatively; throws <see cref="OperationCanceledException"/>.</summary>
    public System.Threading.CancellationToken CancellationToken { get; set; }
}

/// <summary>One file that failed to extract, with the reason.</summary>
public sealed record ExtractionFailure(string Path, Exception Exception);
