using System.Diagnostics;
using OrbisPkgTool;

namespace OrbisPkgTool.Gui;

/// <summary>
/// Top-of-window bar that holds the PKG path + recents dropdown + passcode,
/// and renders an in-process metadata summary (via <see cref="PkgReader.GetInfo"/>).
/// The PKG handle is opened → read → disposed immediately on every load — never
/// held open, so in-place ops (fixdigests/resignpfs) still work on the file.
/// </summary>
public sealed class PackageBar : UserControl
{
    private readonly TextBox _pathBox = new();
    private readonly Button _browseBtn = new();
    private readonly ComboBox _recentsCombo = new();
    private readonly TextBox _passcodeBox = new();
    private readonly Label _summaryLabel = new();
    private readonly Label _badgeLabel = new();
    private readonly Label _warnLabel = new();

    private readonly List<string> _recents = [];
    private CancellationTokenSource? _summaryCts;

    public event EventHandler? PackageChanged;

    public PackageBar()
    {
        Height = 162;
        Dock = DockStyle.Top;
        BackColor = Color.FromArgb(244, 244, 248);
        Padding = new Padding(8, 6, 8, 6);
        BuildLayout();
    }

    // ------------------------------------------------------------------
    // Layout — plain Dock rows, stacked bottom-up so they dock correctly:
    //   [warn ..........................]  (fill)
    //   [badge | summary ...............]  (top, 56)
    //   [Passcode: | passcode ..........]  (top, 32)
    //   [Package: | path | ... | recents]  (top, 32)
    // ------------------------------------------------------------------
    private void BuildLayout()
    {
        // ---- warn row (fixed-height top row, always visible) ------------
        _warnLabel.Dock = DockStyle.Top;
        _warnLabel.Height = 26;
        _warnLabel.AutoEllipsis = true;
        _warnLabel.TextAlign = ContentAlignment.TopLeft;
        _warnLabel.ForeColor = Color.Firebrick;
        _warnLabel.Font = new Font("Segoe UI", 9f);
        _warnLabel.Text = "";
        _warnLabel.Padding = new Padding(4, 2, 4, 0);

        // ---- summary row: badge + summary --------------------------------
        var summaryRow = new Panel { Dock = DockStyle.Top, Height = 56, Padding = new Padding(0, 2, 0, 2) };

        _badgeLabel.Dock = DockStyle.Left;
        _badgeLabel.Width = 75;
        _badgeLabel.TextAlign = ContentAlignment.MiddleCenter;
        _badgeLabel.BackColor = Color.FromArgb(220, 220, 220);
        _badgeLabel.ForeColor = Color.White;
        _badgeLabel.Font = new Font("Segoe UI", 10f, FontStyle.Bold);
        _badgeLabel.Text = "—";

        _summaryLabel.Dock = DockStyle.Fill;
        _summaryLabel.AutoEllipsis = true;
        _summaryLabel.Font = new Font("Segoe UI", 9f);
        _summaryLabel.Text = "Select a .pkg to load its metadata summary.";
        _summaryLabel.Padding = new Padding(6, 2, 6, 2);

        summaryRow.Controls.Add(_summaryLabel);   // fill first (z-order)
        summaryRow.Controls.Add(_badgeLabel);      // left second

        // ---- passcode row -------------------------------------------------
        var passRow = new Panel { Dock = DockStyle.Top, Height = 32 };

        var pcLabel = new Label
        {
            Text = "Passcode:",
            Dock = DockStyle.Left,
            Width = 75,
            TextAlign = ContentAlignment.MiddleLeft,
        };
        _passcodeBox.Dock = DockStyle.Fill;
        _passcodeBox.Text = CommandDef.DefaultPasscode;
        _passcodeBox.MaxLength = 32;
        _passcodeBox.Font = new Font("Cascadia Mono", 9f);
        _passcodeBox.TextChanged += (_, _) => PackageChanged?.Invoke(this, EventArgs.Empty);

        passRow.Controls.Add(_passcodeBox);
        passRow.Controls.Add(pcLabel);

        // ---- package row ----------------------------------------------------
        var pkgRow = new Panel { Dock = DockStyle.Top, Height = 32 };

        var pkgLabel = new Label
        {
            Text = "Package:",
            Dock = DockStyle.Left,
            Width = 75,
            TextAlign = ContentAlignment.MiddleLeft,
        };

        _recentsCombo.Dock = DockStyle.Right;
        _recentsCombo.Width = 200;
        _recentsCombo.DropDownStyle = ComboBoxStyle.DropDownList;
        _recentsCombo.SelectedIndexChanged += (_, _) =>
        {
            if (_recentsCombo.SelectedIndex >= 0 && _recentsCombo.SelectedIndex < _recents.Count)
            {
                _pathBox.Text = _recents[_recentsCombo.SelectedIndex];
            }
        };

        _browseBtn.Text = "...";
        _browseBtn.Dock = DockStyle.Right;
        _browseBtn.Width = 30;
        _browseBtn.Click += (_, _) => BrowseForPkg();

        _pathBox.Dock = DockStyle.Fill;
        _pathBox.TextChanged += (_, _) => OnPathChanged();

        pkgRow.Controls.Add(_pathBox);
        pkgRow.Controls.Add(pkgLabel);
        pkgRow.Controls.Add(_browseBtn);
        pkgRow.Controls.Add(_recentsCombo);

        // Dock order: add fill/lowest first so Top rows stack top→bottom
        Controls.Add(_warnLabel);
        Controls.Add(summaryRow);
        Controls.Add(passRow);
        Controls.Add(pkgRow);
    }

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------
    public string PackagePath => _pathBox.Text.Trim();
    public string Passcode => _passcodeBox.Text.Trim();

    /// <summary>Restores the passcode field from settings (called once on load).</summary>
    public void SetPasscode(string passcode) => _passcodeBox.Text = passcode;

    public void SetRecents(IEnumerable<string> paths)
    {
        // Snapshot FIRST — callers may pass _recents itself (AddRecent does),
        // and Clear() below would empty the input before it is copied.
        var items = paths.ToList();
        _recents.Clear();
        _recents.AddRange(items);
        _recentsCombo.Items.Clear();
        foreach (var p in items)
            _recentsCombo.Items.Add(Path.GetFileName(p) + "  —  " + p);
    }

    public void AddRecent(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return;
        _recents.RemoveAll(p => string.Equals(p, path, StringComparison.OrdinalIgnoreCase));
        _recents.Insert(0, path);
        if (_recents.Count > 12) _recents.RemoveRange(12, _recents.Count - 12);
        SetRecents(_recents);
        if (_recentsCombo.Items.Count > 0)
            _recentsCombo.SelectedIndex = 0;
    }

    public IReadOnlyList<string> GetRecents() => _recents;

    public void SetPackagePath(string path)
    {
        if (_pathBox.Text == path) OnPathChanged();
        else _pathBox.Text = path;
    }

    // ------------------------------------------------------------------
    // Browse
    // ------------------------------------------------------------------
    private void BrowseForPkg()
    {
        using var dlg = new OpenFileDialog
        {
            Title = "Select PKG file",
            Filter = "PS4 Packages (*.pkg)|*.pkg|All files (*.*)|*.*",
        };
        if (dlg.ShowDialog(FindForm()) == DialogResult.OK)
            _pathBox.Text = dlg.FileName;
    }

    private void OnPathChanged()
    {
        PackageChanged?.Invoke(this, EventArgs.Empty);
        if (File.Exists(_pathBox.Text))
            LoadSummaryAsync(_pathBox.Text.Trim());
        else
            ClearSummary();
    }

    // ------------------------------------------------------------------
    // In-process metadata summary
    // ------------------------------------------------------------------
    private void ClearSummary()
    {
        _summaryLabel.Text = "Select a .pkg to load its metadata summary.";
        _badgeLabel.Text = "—";
        _badgeLabel.BackColor = Color.FromArgb(220, 220, 220);
        _warnLabel.Text = "";
    }

    private async void LoadSummaryAsync(string pkgPath)
    {
        _summaryCts?.Cancel();
        _summaryCts = new CancellationTokenSource();
        var token = _summaryCts.Token;

        _summaryLabel.Text = "Loading metadata...";
        _warnLabel.Text = "";

        string pc = Passcode;
        if (pc.Length != 32)
        {
            // PkgReader rejects passcodes != 32 chars. Fall back to default so
            // the summary still loads; the run pipeline will surface the error.
            pc = CommandDef.DefaultPasscode;
        }

        try
        {
            string title = "", contentId = "", titleId = "", appVer = "", category = "", sysVer = "", passcodeStatus = "";
            PkgType type = PkgType.Unknown;
            await Task.Run(() =>
            {
                // Open → read → dispose: never held open. In-place ops like
                // fixdigests/resignpfs can run on the file after this returns.
                using var reader = new PkgReader(pkgPath, pc);
                var info = reader.GetInfo();
                title = info.Title;
                contentId = info.ContentId;
                titleId = info.TitleId;
                appVer = info.AppVersion;
                category = info.Category;
                sysVer = info.SystemVersion;
                type = info.Type;
                passcodeStatus = reader.PasscodeStatus;
            }, token);

            if (token.IsCancellationRequested) return;

            var fi = new FileInfo(pkgPath);
            _summaryLabel.Text =
                $"Title: {title}    Title ID: {titleId}    Content ID: {contentId}\n" +
                $"Category: {category}    App Ver: {appVer}    System: {sysVer}    Size: {fi.Length / 1024.0 / 1024.0:F1} MB    {passcodeStatus}";

            _badgeLabel.Text = type.ToString();
            _badgeLabel.BackColor = type switch
            {
                PkgType.Game    => Color.FromArgb(46, 125, 50),   // green
                PkgType.Patch   => Color.FromArgb(21, 101, 192),  // blue
                PkgType.Dlc     => Color.FromArgb(123, 31, 162),   // purple
                PkgType.Theme   => Color.FromArgb(255, 111, 0),   // orange
                PkgType.Avatar  => Color.FromArgb(191, 54, 12),    // deep orange
                PkgType.Wallpaper => Color.FromArgb(173, 20, 87),  // pink
                _               => Color.FromArgb(96, 96, 96),    // gray (Unknown/Addon)
            };

            if (passcodeStatus.StartsWith("passcode mismatch", StringComparison.Ordinal))
            {
                _warnLabel.Text = "⚠ Passcode mismatch — using RSA-recovered dk3 (read-only).\n" +
                                  "In-place ops (fixdigests/resignpfs) will likely fail.";
            }
        }
        catch (OperationCanceledException) { /* expected when a newer load supersedes us */ }
        catch (Exception ex)
        {
            _summaryLabel.Text = "Failed to load metadata.";
            _badgeLabel.Text = "!";
            _badgeLabel.BackColor = Color.Firebrick;
            _warnLabel.Text = $"⚠ {ex.GetType().Name}: {ex.Message}\n" +
                              "Run remains enabled — CLI diagnostics may still work on this file.";
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _summaryCts?.Cancel();
        base.Dispose(disposing);
    }
}
