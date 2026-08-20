using System.Diagnostics;
using System.Text;

namespace OrbisPkgTool.Gui;

public sealed class MainForm : Form
{
    // ------------------------------------------------------------------
    // Layout constants
    // ------------------------------------------------------------------
    private const int OutputHeight = 240;

    private readonly PackageBar _packageBar = new();
    private readonly OperationsPane _pkgOps = new();
    private readonly OperationsPane _toolOps = new();
    private readonly OutputConsole _output = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _pkgStatusLabel = new();
    private readonly ToolStripStatusLabel _statusLabel = new();
    private readonly ToolStripStatusLabel _lastRunLabel = new();

    private readonly SplitContainer _outputSplit = new();
    private readonly TabControl _tabControl = new();

    private Process? _proc;
    private CancellationTokenSource? _cts;
    private OperationsPane? _activePane;
    private readonly string _settingsPath;

    public MainForm()
    {
        Text = "OrbisPkgTool GUI";
        Width = 1180;
        Height = 820;
        MinimumSize = new Size(820, 540);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        _settingsPath = Path.Combine(AppContext.BaseDirectory, "orbispkgtool.gui.settings.txt");

        BuildLayout();
        WireEvents();
        PopulateOps();
        LoadSettings();
    }

    // ------------------------------------------------------------------
    // Layout
    // ------------------------------------------------------------------
    private void BuildLayout()
    {
        // Package bar (top)
        _packageBar.Dock = DockStyle.Top;

        // TabControl (Package | Build & Tools) — each tab holds an OperationsPane
        _tabControl.Dock = DockStyle.Fill;
        _tabControl.Padding = new Point(8, 4);

        var pkgTab = new TabPage("Package")
        {
            Padding = new Padding(4),
        };
        pkgTab.Controls.Add(_pkgOps);
        _tabControl.TabPages.Add(pkgTab);

        var toolTab = new TabPage("Build & Tools")
        {
            Padding = new Padding(4),
        };
        toolTab.Controls.Add(_toolOps);
        _tabControl.TabPages.Add(toolTab);

        // Output console (bottom)
        _output.Dock = DockStyle.Fill;

        // Vertical splitter: tabs on top, output console on bottom
        _outputSplit.Dock = DockStyle.Fill;
        _outputSplit.Orientation = Orientation.Horizontal;
        _outputSplit.SplitterDistance = 0; // set in OnLoad
        _outputSplit.Panel1.Controls.Add(_tabControl);
        _outputSplit.Panel2.Controls.Add(_output);
        _outputSplit.Panel2MinSize = 120;

        // Status bar
        _pkgStatusLabel.Text = "No package selected.";
        _pkgStatusLabel.Spring = true;
        _pkgStatusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _pkgStatusLabel.Margin = new Padding(2, 3, 8, 2);
        _statusLabel.Text = "Ready.";
        _statusLabel.TextAlign = ContentAlignment.MiddleLeft;
        _statusLabel.Margin = new Padding(0, 3, 16, 2);
        _lastRunLabel.Text = "";
        _lastRunLabel.TextAlign = ContentAlignment.MiddleRight;
        _lastRunLabel.Margin = new Padding(0, 3, 2, 2);
        _status.Items.Add(_pkgStatusLabel);
        _status.Items.Add(_statusLabel);
        _status.Items.Add(_lastRunLabel);

        Controls.Add(_outputSplit);
        Controls.Add(_packageBar);
        Controls.Add(_status);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        // Apply saved window bounds, splitters, last tab
        // (Loaded from settings file in LoadSettings())
        _outputSplit.SplitterDistance = Math.Max(200, Height - OutputHeight - _packageBar.Height - _status.Height - 30);
        _pkgOps.Split.SplitterDistance = 220;
        _toolOps.Split.SplitterDistance = 220;
        // The OnLoad default values are overwritten by LoadSettings, but
        // LoadSettings is called from the constructor — which runs before
        // OnLoad — so we re-apply saved bounds here as a final pass.
        ApplySavedBoundsIfAny();
    }

    private void ApplySavedBoundsIfAny()
    {
        if (_savedWidth > 0 && _savedHeight > 0)
        {
            Width = _savedWidth;
            Height = _savedHeight;
            if (_savedLeft != int.MinValue && _savedTop != int.MinValue)
            {
                StartPosition = FormStartPosition.Manual;
                Left = _savedLeft;
                Top = _savedTop;
            }
        }
        if (_savedPkgSplit > 0) _pkgOps.Split.SplitterDistance = _savedPkgSplit;
        if (_savedToolSplit > 0) _toolOps.Split.SplitterDistance = _savedToolSplit;
        if (_savedOutputSplit > 0)
            _outputSplit.SplitterDistance = _savedOutputSplit;
        if (_savedTab >= 0 && _savedTab < _tabControl.TabCount)
            _tabControl.SelectedIndex = _savedTab;
    }

    private int _savedWidth, _savedHeight, _savedLeft = int.MinValue, _savedTop = int.MinValue;
    private int _savedPkgSplit, _savedToolSplit, _savedOutputSplit, _savedTab;

    // ------------------------------------------------------------------
    // Events + ops population
    // ------------------------------------------------------------------
    private void WireEvents()
    {
        _packageBar.PackageChanged += (_, _) => UpdatePackageStatus();

        _pkgOps.RunRequested += (cmd, values) => OnRunRequested(_pkgOps, cmd, values);
        _pkgOps.CancelRequested += () => _cts?.Cancel();
        _pkgOps.SelectionChanged += cmd => _statusLabel.Text = cmd != null ? $"Selected: {cmd.CliWord}" : "Ready.";

        _toolOps.RunRequested += (cmd, values) => OnRunRequested(_toolOps, cmd, values);
        _toolOps.CancelRequested += () => _cts?.Cancel();
        _toolOps.SelectionChanged += cmd => _statusLabel.Text = cmd != null ? $"Selected: {cmd.CliWord}" : "Ready.";

        _tabControl.SelectedIndexChanged += (_, _) =>
        {
            _activePane = _tabControl.SelectedIndex == 0 ? _pkgOps : _toolOps;
        };
        _activePane = _pkgOps; // default to Package tab
    }

    private void PopulateOps()
    {
        _pkgOps.SetCommands(CommandRegistry.PackageOps);
        _toolOps.SetCommands(CommandRegistry.ToolOps);
    }

    private void UpdatePackageStatus()
    {
        string path = _packageBar.PackagePath;
        if (path.Length > 0)
            _pkgStatusLabel.Text = $"PKG: {Path.GetFileName(path)}    Passcode: {_packageBar.Passcode}";
        else
            _pkgStatusLabel.Text = "No package selected.";
    }

    // ------------------------------------------------------------------
    // Run pipeline (shared by both tabs)
    // ------------------------------------------------------------------
    private async void OnRunRequested(OperationsPane pane, CommandDef cmd, IReadOnlyDictionary<string, string> values)
    {
        // Only one run at a time — disable both panes for the duration
        _pkgOps.SetRunning(true);
        _toolOps.SetRunning(true);

        string? exe = ResolveCli();
        if (exe == null)
        {
            MessageBox.Show(this,
                "OrbisPkgTool.exe not found.\nBuild the OrbisPkgTool project (it's copied next to the GUI automatically).",
                "CLI not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _pkgOps.SetRunning(false);
            _toolOps.SetRunning(false);
            return;
        }

        // For Package-tab ops, inject pkg + passcode from the bar
        var effectiveValues = new Dictionary<string, string>(values);
        bool isPkgOp = pane == _pkgOps;
        if (isPkgOp)
        {
            effectiveValues["pkg"] = _packageBar.PackagePath;
            effectiveValues["passcode"] = _packageBar.Passcode;
            if (string.IsNullOrWhiteSpace(effectiveValues["pkg"]))
            {
                MessageBox.Show(this, "Select a PKG file in the package bar first.", "No package",
                    MessageBoxButtons.OK, MessageBoxIcon.Information);
                _pkgOps.SetRunning(false);
                _toolOps.SetRunning(false);
                return;
            }
        }

        // Build args
        string[] args;
        try
        {
            args = cmd.BuildArgs(effectiveValues);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Cannot build arguments: {ex.Message}", "Argument error",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            _pkgOps.SetRunning(false);
            _toolOps.SetRunning(false);
            return;
        }

        // Prepend the CLI word(s)
        var cliWords = cmd.CliWord.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        var fullArgs = new string[cliWords.Length + args.Length];
        Array.Copy(cliWords, 0, fullArgs, 0, cliWords.Length);
        Array.Copy(args, 0, fullArgs, cliWords.Length, args.Length);

        // New tab for this run
        var page = _output.StartRun(cmd.Name);

        _cts = new CancellationTokenSource();
        _statusLabel.Text = $"Running: {cmd.CliWord}";
        _output.AppendLine(page, $"> {Path.GetFileName(exe)} {string.Join(' ', fullArgs)}");
        _output.AppendLine(page, "");

        var sw = Stopwatch.StartNew();
        try
        {
            int exit = await RunProcessAsync(exe, fullArgs, page, _cts.Token);
            sw.Stop();
            bool success = exit == 0;
            _output.FinishRun(page, success, sw.Elapsed.TotalSeconds);
            _statusLabel.Text = success ? $"Done in {sw.Elapsed.TotalSeconds:F1}s" : $"Failed (exit {exit})";
            _lastRunLabel.Text = $"Last run: {cmd.Name} {(success ? "✓" : "✗")} {sw.Elapsed.TotalSeconds:F1}s  {DateTime.Now:HH:mm}";

            // Remember the PKG if this was a Package-tab op
            if (isPkgOp && success)
                _packageBar.AddRecent(_packageBar.PackagePath);
        }
        catch (OperationCanceledException)
        {
            _output.CancelRun(page);
            _statusLabel.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            _output.ErrorRun(page, ex.Message);
            _statusLabel.Text = "Error";
        }
        finally
        {
            _proc = null;
            _pkgOps.SetRunning(false);
            _toolOps.SetRunning(false);
        }
    }

    private async Task<int> RunProcessAsync(string exe, string[] args, TabPage page, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        foreach (var a in args) psi.ArgumentList.Add(a);

        using var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        _proc = proc;

        proc.OutputDataReceived += (_, e) => { if (e.Data != null) _output.AppendLine(page, e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) _output.AppendLine(page, e.Data); };

        if (!proc.Start())
            throw new InvalidOperationException("Process failed to start.");

        proc.BeginOutputReadLine();
        proc.BeginErrorReadLine();

        try
        {
            await proc.WaitForExitAsync(ct);
        }
        catch (OperationCanceledException)
        {
            try { proc.Kill(entireProcessTree: true); } catch { }
            throw;
        }
        return proc.ExitCode;
    }

    // ------------------------------------------------------------------
    // CLI resolution (auto-detect OrbisPkgTool.exe next to GUI exe)
    // ------------------------------------------------------------------
    private string? ResolveCli()
    {
        // 1. Same directory as GUI exe (the CopyCliExe MSBuild target puts it here)
        var probe = Path.Combine(AppContext.BaseDirectory, "OrbisPkgTool.exe");
        if (File.Exists(probe)) return probe;

        // 2. Walk up from the GUI bin dir
        var baseDir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            probe = Path.Combine(baseDir, "OrbisPkgTool.exe");
            if (File.Exists(probe)) return probe;
            probe = Path.Combine(baseDir, "OrbisPkgTool", "bin", "Debug", "net10.0-windows", "OrbisPkgTool.exe");
            if (File.Exists(probe)) return probe;
            probe = Path.Combine(baseDir, "OrbisPkgTool", "bin", "Release", "net10.0-windows", "OrbisPkgTool.exe");
            if (File.Exists(probe)) return probe;
            baseDir = Path.GetDirectoryName(baseDir) ?? "";
            if (baseDir.Length == 0) break;
        }

        // 3. Scan repo root's bin/ dirs
        var root = FindRepoRoot();
        if (root != null)
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "bin", SearchOption.AllDirectories))
            {
                probe = Path.Combine(dir, "OrbisPkgTool.exe");
                if (File.Exists(probe)) return probe;
            }
        }
        return null;
    }

    private string? FindRepoRoot()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            if (Directory.Exists(Path.Combine(dir, "OrbisPkgTool")))
                return dir;
            dir = Path.GetDirectoryName(dir) ?? "";
            if (dir.Length == 0) break;
        }
        return null;
    }

    // ------------------------------------------------------------------
    // Settings persistence
    // ------------------------------------------------------------------
    private void LoadSettings()
    {
        try
        {
            if (!File.Exists(_settingsPath)) return;
            var lines = File.ReadAllLines(_settingsPath);
            var recents = new List<string>();
            foreach (var l in lines)
            {
                if (l.StartsWith("pkg=", StringComparison.OrdinalIgnoreCase))
                    _packageBar.SetPackagePath(l[4..]);
                else if (l.StartsWith("passcode=", StringComparison.OrdinalIgnoreCase))
                {
                    var pc = l["passcode=".Length..];
                    if (pc.Length == 32)
                        _packageBar.SetPasscode(pc);
                }
                else if (l.StartsWith("recent", StringComparison.OrdinalIgnoreCase) && l.Contains('='))
                    recents.Add(l[(l.IndexOf('=') + 1)..]);
                else if (l.StartsWith("width=", StringComparison.OrdinalIgnoreCase) && int.TryParse(l[6..], out var w))
                    _savedWidth = w;
                else if (l.StartsWith("height=", StringComparison.OrdinalIgnoreCase) && int.TryParse(l[7..], out var h))
                    _savedHeight = h;
                else if (l.StartsWith("left=", StringComparison.OrdinalIgnoreCase) && int.TryParse(l[5..], out var lf))
                    _savedLeft = lf;
                else if (l.StartsWith("top=", StringComparison.OrdinalIgnoreCase) && int.TryParse(l[4..], out var tp))
                    _savedTop = tp;
                else if (l.StartsWith("pkgsplit=", StringComparison.OrdinalIgnoreCase) && int.TryParse(l[9..], out var ps))
                    _savedPkgSplit = ps;
                else if (l.StartsWith("toolsplit=", StringComparison.OrdinalIgnoreCase) && int.TryParse(l[10..], out var ts))
                    _savedToolSplit = ts;
                else if (l.StartsWith("outsplit=", StringComparison.OrdinalIgnoreCase) && int.TryParse(l[9..], out var os))
                    _savedOutputSplit = os;
                else if (l.StartsWith("tab=", StringComparison.OrdinalIgnoreCase) && int.TryParse(l[4..], out var tb2))
                    _savedTab = tb2;
            }
            _packageBar.SetRecents(recents);
        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            var sb = new StringBuilder();
            sb.Append("pkg=").AppendLine(_packageBar.PackagePath);
            sb.Append("passcode=").AppendLine(_packageBar.Passcode);
            foreach (var r in _packageBar.GetRecents())
                sb.Append("recent=").AppendLine(r);
            sb.Append("width=").AppendLine(Width.ToString());
            sb.Append("height=").AppendLine(Height.ToString());
            sb.Append("left=").AppendLine(Left.ToString());
            sb.Append("top=").AppendLine(Top.ToString());
            sb.Append("pkgsplit=").AppendLine(_pkgOps.Split.SplitterDistance.ToString());
            sb.Append("toolsplit=").AppendLine(_toolOps.Split.SplitterDistance.ToString());
            sb.Append("outsplit=").AppendLine(_outputSplit.SplitterDistance.ToString());
            sb.Append("tab=").AppendLine(_tabControl.SelectedIndex.ToString());
            File.WriteAllText(_settingsPath, sb.ToString());
        }
        catch { }
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (_proc is { HasExited: false })
        {
            var r = MessageBox.Show(this, "A command is still running. Kill it and exit?",
                "Exit", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) { e.Cancel = true; return; }
            try { _proc.Kill(entireProcessTree: true); } catch { }
        }
        SaveSettings();
        base.OnFormClosing(e);
    }
}
