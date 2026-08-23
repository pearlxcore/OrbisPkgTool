namespace OrbisPkgTool.Gui;

/// <summary>Field kinds rendered by the options panel.</summary>
public enum FieldKind
{
    /// <summary>Existing file (OpenFileDialog).</summary>
    File,
    /// <summary>New file path (SaveFileDialog).</summary>
    SaveFile,
    /// <summary>Existing folder (FolderBrowserDialog).</summary>
    Folder,
    /// <summary>Plain text box.</summary>
    Text,
    /// <summary>Text box that accepts comma/space-separated values.</summary>
    MultiText,
    /// <summary>Drop-down with fixed choices.</summary>
    Combo,
    /// <summary>Boolean checkbox.</summary>
    Check,
}

/// <summary>One input field of a command.</summary>
public sealed class CommandField
{
    public required string Id { get; init; }
    public required string Label { get; init; }
    public required FieldKind Kind { get; init; }
    /// <summary>OpenFileDialog filter or SaveFileDialog filter.</summary>
    public string Filter { get; init; } = "";
    /// <summary>Combo choices.</summary>
    public string[] Choices { get; init; } = [];
    /// <summary>Default text value.</summary>
    public string Default { get; init; } = "";
    /// <summary>Placeholder text (text boxes) or checkbox label / combo tooltip.</summary>
    public string Hint { get; init; } = "";
    /// <summary>Per-choice descriptions for Combo: item shown as "value — remark".</summary>
    public string[]? ChoiceRemarks { get; init; }
    /// <summary>Positional argument index (fields with Index >= 0 are appended in order).</summary>
    public int Position { get; init; } = -1;
}

/// <summary>One CLI command: metadata + how to build its argument vector.</summary>
public sealed class CommandDef
{
    public required string Name { get; init; }
    public required string Group { get; init; }
    public required string Title { get; init; }
    public string Description { get; init; } = "";
    /// <summary>First word passed to the CLI ("list", "build", "sfo read", ...).</summary>
    public required string CliWord { get; init; }
    public required CommandField[] Fields { get; init; }
    /// <summary>Builds CLI args from field values (id -> value, "" when empty).</summary>
    public required Func<IReadOnlyDictionary<string, string>, string[]> BuildArgs { get; init; }

    /// <summary>Shared default passcode.</summary>
    public const string DefaultPasscode = "00000000000000000000000000000000";
}
