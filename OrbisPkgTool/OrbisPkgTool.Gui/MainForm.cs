using System.Diagnostics;
using System.Text;

namespace OrbisPkgTool.Gui;

public sealed class MainForm : Form
{
    // Layout constants
    private const int TreeWidth = 240;

    private readonly TreeView _tree = new();
    private readonly FlowLayoutPanel _fieldsPanel = new();
    private readonly Label _descLabel = new();
    private readonly Button _runButton = new();
    private readonly Button _cancelButton = new();
    private readonly Button _clearButton = new();
    private readonly TextBox _output = new();
    private readonly StatusStrip _status = new();
    private readonly ToolStripStatusLabel _statusLabel = new();

    private readonly Dictionary<string, Control> _fieldControls = new();
    private readonly Dictionary<string, CommandDef> _commands = new();
    private readonly List<string> _recentPkgs = [];
    private CommandDef? _current;
    private Process? _proc;
    private CancellationTokenSource? _cts;
    private readonly string _settingsPath;

    public MainForm()
    {
        Text = "OrbisPkgTool GUI";
        Width = 1100;
        Height = 760;
        MinimumSize = new Size(760, 480);
        StartPosition = FormStartPosition.CenterScreen;
        Font = new Font("Segoe UI", 9f);

        _settingsPath = Path.Combine(AppContext.BaseDirectory, "orbispkgtool.gui.settings.txt");

        BuildLayout();
        PopulateTree();
        LoadSettings();
        SelectFirstCommand();
    }

    // ------------------------------------------------------------------
    // Layout
    // ------------------------------------------------------------------
    private void BuildLayout()
    {
        // Top bar: CLI path + Run/Cancel/Clear
        var top = new TableLayoutPanel
        {
            Dock = DockStyle.Top,
            Height = 38,
            ColumnCount = 6,
            Padding = new Padding(6, 4, 6, 4),
        };
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 60));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));
        top.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 90));

        var cliLabel = new Label { Text = "CLI:", Anchor = AnchorStyles.Left, AutoSize = true };
        _cliPathBox = new TextBox { Dock = DockStyle.Fill };
        _cliPathBox.TextChanged += (_, _) => SaveSettings();
        var browse = new Button { Text = "...", Dock = DockStyle.Fill };
        browse.Click += (_, _) =>
        {
            using var dlg = new OpenFileDialog
            {
                Title = "Select OrbisPkgTool.Cli.exe",
                Filter = "OrbisPkgTool CLI (*.exe)|OrbisPkgTool.Cli.exe|All files (*.*)|*.*",
            };
            if (dlg.ShowDialog(this) == DialogResult.OK) _cliPathBox.Text = dlg.FileName;
        };
        _runButton.Text = "Run";
        _runButton.Dock = DockStyle.Fill;
        _runButton.Click += async (_, _) => await RunAsync();
        _cancelButton.Text = "Cancel";
        _cancelButton.Dock = DockStyle.Fill;
        _cancelButton.Enabled = false;
        _cancelButton.Click += (_, _) => _cts?.Cancel();
        _clearButton.Text = "Clear";
        _clearButton.Dock = DockStyle.Fill;
        _clearButton.Click += (_, _) => _output.Clear();

        top.Controls.Add(cliLabel, 0, 0);
        top.Controls.Add(_cliPathBox, 1, 0);
        top.Controls.Add(browse, 2, 0);
        top.Controls.Add(_runButton, 3, 0);
        top.Controls.Add(_cancelButton, 4, 0);
        top.Controls.Add(_clearButton, 5, 0);

        // Left: command tree
        _tree.Dock = DockStyle.Fill;
        _tree.HideSelection = false;
        _tree.AfterSelect += (_, e) => SelectCommand(e.Node);

        // Right: description + dynamic fields
        _descLabel.Dock = DockStyle.Top;
        _descLabel.AutoEllipsis = true;
        _descLabel.Padding = new Padding(4);
        _descLabel.BackColor = Color.FromArgb(240, 240, 245);
        _descLabel.Height = 44;

        _fieldsPanel.Dock = DockStyle.Fill;
        _fieldsPanel.AutoScroll = true;
        _fieldsPanel.FlowDirection = FlowDirection.TopDown;
        _fieldsPanel.WrapContents = false;

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };
        right.Controls.Add(_fieldsPanel);
        right.Controls.Add(_descLabel);

        // Splitter: tree | right (min sizes applied in OnLoad — setting
        // them here throws before the form is sized)
        var split = new SplitContainer { Dock = DockStyle.Fill };
        _split = split;
        split.Panel1.Controls.Add(_tree);
        split.Panel2.Controls.Add(right);

        // Output console (bottom)
        _output.Dock = DockStyle.Fill;
        _output.Multiline = true;
        _output.ReadOnly = true;
        _output.ScrollBars = ScrollBars.Both;
        _output.WordWrap = false;
        _output.Font = new Font("Cascadia Mono", 9f);
        _output.BackColor = Color.FromArgb(30, 30, 30);
        _output.ForeColor = Color.FromArgb(220, 220, 220);

        var outputPanel = new Panel { Dock = DockStyle.Bottom, Height = 260, Padding = new Padding(6) };
        outputPanel.Controls.Add(_output);

        // Status bar
        _statusLabel.Text = "Ready";
        _status.Items.Add(_statusLabel);

        Controls.Add(split);
        Controls.Add(outputPanel);
        Controls.Add(top);
        Controls.Add(_status);
    }

    private TextBox _cliPathBox = new();
    private SplitContainer? _split;

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        if (_split != null)
        {
            _split.Panel1MinSize = 160;
            _split.Panel2MinSize = 420;
            _split.SplitterDistance = Math.Min(TreeWidth, _split.Width - _split.Panel2MinSize - 8);
        }
    }

    // ------------------------------------------------------------------
    // Command tree
    // ------------------------------------------------------------------
    private void PopulateTree()
    {
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        var byGroup = CommandRegistry.All.GroupBy(c => c.Group).OrderBy(g => g.Key);
        foreach (var g in byGroup)
        {
            var node = new TreeNode(g.Key);
            foreach (var c in g.OrderBy(c => c.Name))
            {
                _commands[c.Name] = c;
                node.Nodes.Add(new TreeNode(c.Name) { Tag = c });
            }
            _tree.Nodes.Add(node);
            node.Expand();
        }
        _tree.EndUpdate();
    }

    private void SelectFirstCommand()
    {
        if (_tree.Nodes.Count > 0 && _tree.Nodes[0].Nodes.Count > 0)
            _tree.SelectedNode = _tree.Nodes[0].Nodes[0];
    }

    private void SelectCommand(TreeNode node)
    {
        if (node?.Tag is not CommandDef cmd) return;
        _current = cmd;
        _descLabel.Text = $"{cmd.Title} — {cmd.Description}";
        BuildFields(cmd);
        _runButton.Enabled = true;
    }

    // ------------------------------------------------------------------
    // Dynamic fields
    // ------------------------------------------------------------------
    private void BuildFields(CommandDef cmd)
    {
        _fieldsPanel.SuspendLayout();
        _fieldsPanel.Controls.Clear();
        _fieldControls.Clear();

        foreach (var f in cmd.Fields)
        {
            var row = new TableLayoutPanel
            {
                AutoSize = true,
                ColumnCount = 2,
                Width = _fieldsPanel.ClientSize.Width - 24,
                Padding = new Padding(2, 2, 2, 2),
            };
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 190));
            row.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

            var label = new Label { Text = f.Label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 4, 0, 0) };

            Control input;
            switch (f.Kind)
            {
                case FieldKind.File:
                    input = MakeFileRow(f, open: true);
                    break;
                case FieldKind.SaveFile:
                    input = MakeFileRow(f, open: false);
                    break;
                case FieldKind.Folder:
                    input = MakeFolderRow(f);
                    break;
                case FieldKind.Combo:
                    var combo = new ComboBox
                    {
                        DropDownStyle = ComboBoxStyle.DropDownList,
                        Width = 420,
                        Anchor = AnchorStyles.Left | AnchorStyles.Right,
                    };
                    combo.Items.AddRange(f.Choices);
                    if (combo.Items.Count > 0)
                        combo.SelectedIndex = Math.Max(0, Array.IndexOf(f.Choices, f.Default));
                    input = combo;
                    break;
                case FieldKind.Check:
                    input = new CheckBox { Text = "", AutoSize = true, Checked = f.Default == "1" };
                    break;
                default:
                    var box = new TextBox { Width = 420, Anchor = AnchorStyles.Left | AnchorStyles.Right, Text = f.Default };
                    input = box;
                    break;
            }

            _fieldControls[f.Id] = input;
            row.Controls.Add(label, 0, 0);
            row.Controls.Add(input, 1, 0);
            _fieldsPanel.Controls.Add(row);
        }

        _fieldsPanel.ResumeLayout(true);
    }

    private Control MakeFileRow(CommandField f, bool open)
    {
        var panel = new TableLayoutPanel { ColumnCount = 2, Width = 420, AutoSize = true, Margin = new Padding(0) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));

        var box = new TextBox { Dock = DockStyle.Fill, Text = f.Default };
        var btn = new Button { Text = "...", Dock = DockStyle.Fill, Width = 28 };
        btn.Click += (_, _) =>
        {
            if (open)
            {
                using var dlg = new OpenFileDialog { Title = f.Label, Filter = f.Filter.Length > 0 ? f.Filter : "All files (*.*)|*.*" };
                if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.FileName;
            }
            else
            {
                using var dlg = new SaveFileDialog { Title = f.Label, Filter = f.Filter.Length > 0 ? f.Filter : "All files (*.*)|*.*" };
                if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.FileName;
            }
        };
        panel.Controls.Add(box, 0, 0);
        panel.Controls.Add(btn, 1, 0);
        return panel;
    }

    private Control MakeFolderRow(CommandField f)
    {
        var panel = new TableLayoutPanel { ColumnCount = 2, Width = 420, AutoSize = true, Margin = new Padding(0) };
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        panel.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 30));

        var box = new TextBox { Dock = DockStyle.Fill, Text = f.Default };
        var btn = new Button { Text = "...", Dock = DockStyle.Fill, Width = 28 };
        btn.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = f.Label, UseDescriptionForTitle = true };
            if (dlg.ShowDialog(this) == DialogResult.OK) box.Text = dlg.SelectedPath;
        };
        panel.Controls.Add(box, 0, 0);
        panel.Controls.Add(btn, 1, 0);
        return panel;
    }

    // ------------------------------------------------------------------
    // Run
    // ------------------------------------------------------------------
    private async Task RunAsync()
    {
        if (_current == null) return;

        var exe = ResolveCli();
        if (exe == null)
        {
            MessageBox.Show(this,
                "OrbisPkgTool.Cli.exe not found.\nSelect it with the \"...\" button next to CLI path.",
                "CLI not found", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        var values = new Dictionary<string, string>();
        foreach (var kv in _fieldControls)
        {
            values[kv.Key] = kv.Value switch
            {
                TextBox t => t.Text.Trim(),
                ComboBox c => c.SelectedItem?.ToString() ?? "",
                CheckBox ck => ck.Checked ? "1" : "0",
                TableLayoutPanel tp => GetTextBoxOfFileRow(tp)?.Text.Trim() ?? "",
                _ => "",
            };
        }

        string[] args;
        try
        {
            args = _current.BuildArgs(values);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"Cannot build arguments: {ex.Message}", "Argument error",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        _cts = new CancellationTokenSource();
        _runButton.Enabled = false;
        _cancelButton.Enabled = true;
        _statusLabel.Text = $"Running: {_current.CliWord}";
        AppendLine($"> {Path.GetFileName(exe)} {string.Join(' ', args)}");
        AppendLine("");

        try
        {
            var sw = Stopwatch.StartNew();
            int exit = await RunProcessAsync(exe, args, _cts.Token);
            sw.Stop();
            AppendLine("");
            AppendLine(exit == 0
                ? $"=== EXIT 0 in {sw.Elapsed.TotalSeconds:F1}s ==="
                : $"=== EXIT {exit} in {sw.Elapsed.TotalSeconds:F1}s ===");
            _statusLabel.Text = exit == 0 ? $"Done in {sw.Elapsed.TotalSeconds:F1}s" : $"Failed (exit {exit})";
        }
        catch (OperationCanceledException)
        {
            AppendLine("=== CANCELLED ===");
            _statusLabel.Text = "Cancelled";
        }
        catch (Exception ex)
        {
            AppendLine($"=== ERROR: {ex.Message} ===");
            _statusLabel.Text = "Error";
        }
        finally
        {
            _runButton.Enabled = true;
            _cancelButton.Enabled = false;
        }
    }

    private async Task<int> RunProcessAsync(string exe, string[] args, CancellationToken ct)
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

        var sb = new StringBuilder();
        proc.OutputDataReceived += (_, e) => { if (e.Data != null) AppendLine(e.Data); };
        proc.ErrorDataReceived += (_, e) => { if (e.Data != null) AppendLine(e.Data); };

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
    // Settings + helpers
    // ------------------------------------------------------------------
    private string? ResolveCli()
    {
        if (_cliPathBox.Text.Length > 0 && File.Exists(_cliPathBox.Text))
            return _cliPathBox.Text;

        // Auto-detect relative to this assembly: bin/Debug/net10.0-windows/
        var baseDir = AppContext.BaseDirectory;
        for (int i = 0; i < 8; i++)
        {
            var probe = Path.Combine(baseDir, "OrbisPkgTool.Cli.exe");
            if (File.Exists(probe)) { _cliPathBox.Text = probe; return probe; }
            baseDir = Path.GetDirectoryName(baseDir) ?? "";
            if (baseDir.Length == 0) break;
        }

        // Search the solution root (4 projects only — no heavy dirs) for the CLI exe
        var root = FindRepoRoot();
        if (root != null)
        {
            foreach (var dir in Directory.EnumerateDirectories(root, "bin", SearchOption.AllDirectories))
            {
                var probe = Path.Combine(dir, "OrbisPkgTool.Cli.exe");
                if (File.Exists(probe)) { _cliPathBox.Text = probe; return probe; }
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

    private void LoadSettings()
    {
        try
        {
            if (File.Exists(_settingsPath))
            {
                var lines = File.ReadAllLines(_settingsPath);
                foreach (var l in lines)
                {
                    if (l.StartsWith("cli=", StringComparison.OrdinalIgnoreCase))
                        _cliPathBox.Text = l[4..];
                }
            }
        }
        catch { }
    }

    private void SaveSettings()
    {
        try
        {
            File.WriteAllText(_settingsPath, $"cli={_cliPathBox.Text}\n");
        }
        catch { }
    }

    private void AppendLine(string text)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLine(text));
            return;
        }
        _output.AppendText(text + Environment.NewLine);
        _output.SelectionStart = _output.TextLength;
        _output.ScrollToCaret();
    }

    private static TextBox? GetTextBoxOfFileRow(TableLayoutPanel panel)
    {
        if (panel.Controls.Count > 0 && panel.Controls[0] is TextBox tb) return tb;
        return null;
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
        base.OnFormClosing(e);
    }
}
