namespace OrbisPkgTool;

/// <summary>High-level package kind, derived from the PKG content type + param.sfo CATEGORY.</summary>
public enum PkgType
{
    Unknown,
    Game,
    Patch,
    /// <summary>Add-on content (DLC) — CATEGORY "ac".</summary>
    Dlc,
    /// <summary>Theme — CATEGORY "th".</summary>
    Theme,
    /// <summary>Avatar — CATEGORY "av".</summary>
    Avatar,
    /// <summary>Wallpaper — CATEGORY "wa".</summary>
    Wallpaper,
    /// <summary>Other add-on (addon content type but unrecognized CATEGORY).</summary>
    Addon,
}

/// <summary>
/// Metadata about a package: parsed from the PKG header (content id, types)
/// and the Sc0/param.sfo entry (title, title id, category, versions).
/// </summary>
public sealed class PkgInfo
{
    public string ContentId = "";
    public string Title = "";
    public string TitleId = "";
    public string AppVersion = "";
    public string SystemVersion = "";
    public string Category = "";
    public uint ContentType;
    public uint ContentFlags;
    public PkgType Type = PkgType.Unknown;

    public override string ToString() =>
        $"{Type} | {Title} | {TitleId} | {ContentId} | app {AppVersion} sys {SystemVersion}";
}
