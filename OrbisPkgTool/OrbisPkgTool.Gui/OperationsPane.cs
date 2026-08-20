namespace OrbisPkgTool.Gui;

/// <summary>
/// Reusable operations pane: a grouped TreeView of operations on the left
/// and a dynamic options panel (per selected operation) on the right, with
/// Run/Cancel buttons at the bottom. Raises <see cref="RunRequested"/>
/// when the user triggers a run (F5, double-click, or Run button).
/// </summary>
public sealed class OperationsPane : UserControl
{
    private readonly TreeView _tree = new();
    private readonly Label _descLabel = new();
    private readonly Panel _fieldsPanel = new();
    private readonly Button _runButton = new();
    private readonly Button _cancelButton = new();
    private readonly SplitContainer _split = new();

    private readonly Dictionary<string, CommandDef> _commands = new();
    private readonly Dictionary<string, Control> _fieldControls = new();
    private CommandDef? _current;

    /// <summary>Raised when the user wants to run the selected op.</summary>
    /// <remarks>Args: (CommandDef, IReadOnlyDictionary&lt;string,string&gt; values).</remarks>
    public event Action<CommandDef, IReadOnlyDictionary<string, string>>? RunRequested;

    /// <summary>Raised when the user clicks Cancel.</summary>
    public event Action? CancelRequested;

    /// <summary>Raised when the selected operation changes (for status bar updates).</summary>
    public event Action<CommandDef?>? SelectionChanged;

    public OperationsPane()
    {
        Dock = DockStyle.Fill;
        BuildLayout();
    }

    public bool CanRun => _current != null;

    public void SetCommands(IReadOnlyList<CommandDef> commands)
    {
        PopulateTree(commands);
        SelectFirstCommand();
    }

    public void SetRunning(bool running)
    {
        _runButton.Enabled = !running && _current != null;
        _cancelButton.Enabled = running;
    }

    // ------------------------------------------------------------------
    // Layout
    // ------------------------------------------------------------------
    private void BuildLayout()
    {
        var bottomBar = new Panel { Dock = DockStyle.Bottom, Height = 34, Padding = new Padding(6, 2, 6, 2) };
        _runButton.Text = "Run";
        _runButton.Dock = DockStyle.Left;
        _runButton.Width = 90;
        _runButton.Click += (_, _) => RaiseRun();
        _cancelButton.Text = "Cancel";
        _cancelButton.Dock = DockStyle.Left;
        _cancelButton.Width = 90;
        _cancelButton.Enabled = false;
        _cancelButton.Click += (_, _) => CancelRequested?.Invoke();
        bottomBar.Controls.Add(_cancelButton);
        bottomBar.Controls.Add(_runButton);

        _descLabel.Dock = DockStyle.Top;
        _descLabel.AutoEllipsis = true;
        _descLabel.Padding = new Padding(6, 4, 6, 4);
        _descLabel.BackColor = Color.FromArgb(240, 240, 245);
        _descLabel.Height = 44;

        _fieldsPanel.Dock = DockStyle.Fill;
        _fieldsPanel.AutoScroll = true;
        _fieldsPanel.Padding = new Padding(4, 4, 4, 4);

        var right = new Panel { Dock = DockStyle.Fill, Padding = new Padding(6) };
        right.Controls.Add(_fieldsPanel);
        right.Controls.Add(_descLabel);

        _tree.Dock = DockStyle.Fill;
        _tree.HideSelection = false;
        _tree.AfterSelect += (_, e) => SelectCommand(e.Node);
        _tree.DoubleClick += (_, _) =>
        {
            if (_current != null) RaiseRun();
        };

        _split.Dock = DockStyle.Fill;
        _split.Panel1.Controls.Add(_tree);
        _split.Panel2.Controls.Add(right);

        // Empty state
        _descLabel.Text = "Select an operation on the left.";
        _runButton.Enabled = false;

        // Key handling — F5 = Run, Esc = Cancel
        _tree.PreviewKeyDown += (_, e) =>
        {
            if (e.KeyCode == Keys.F5) RaiseRun();
            else if (e.KeyCode == Keys.Escape) CancelRequested?.Invoke();
        };

        Controls.Add(_split);
        Controls.Add(bottomBar);
    }

    protected override void OnLoad(EventArgs e)
    {
        base.OnLoad(e);
        _split.Panel1MinSize = 160;
        _split.Panel2MinSize = 360;
        if (_split.Width > 420)
            _split.SplitterDistance = Math.Min(220, _split.Width - _split.Panel2MinSize - 8);
    }

    // ------------------------------------------------------------------
    // Command tree
    // ------------------------------------------------------------------
    private void PopulateTree(IReadOnlyList<CommandDef> commands)
    {
        _commands.Clear();
        _tree.BeginUpdate();
        _tree.Nodes.Clear();
        var byGroup = commands.GroupBy(c => c.Group).OrderBy(g => g.Key);
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

    private void SelectCommand(TreeNode? node)
    {
        if (node?.Tag is not CommandDef cmd) return;
        _current = cmd;
        _descLabel.Text = $"{cmd.Title} — {cmd.Description}";
        BuildFields(cmd);
        _runButton.Enabled = true;
        SelectionChanged?.Invoke(cmd);
    }

    // ------------------------------------------------------------------
    // Dynamic fields
    // ------------------------------------------------------------------
    private void BuildFields(CommandDef cmd)
    {
        _fieldsPanel.SuspendLayout();
        _fieldsPanel.Controls.Clear();
        _fieldControls.Clear();

        // Add rows in reverse order so Dock=Top stacks them top→bottom.
        var rows = new List<Control>();

        if (cmd.Fields.Length == 0)
        {
            // Show a hint so the panel isn't an empty void
            var hint = new Label
            {
                Text = "This operation has no extra options.\n" +
                       (cmd.Group == "Inspect" || cmd.Group == "Diagnose" || cmd.Group == "Extract"
                           ? "It uses the PKG and passcode from the package bar."
                           : "Click Run to execute."),
                AutoSize = true,
                Padding = new Padding(4, 8, 4, 4),
                ForeColor = Color.Gray,
                Dock = DockStyle.Top,
            };
            rows.Add(hint);
        }

        foreach (var f in cmd.Fields)
        {
            // One row panel, Dock=Top, fixed height, label left + input fill.
            var row = new Panel
            {
                Dock = DockStyle.Top,
                Height = 30,
                Padding = new Padding(2, 2, 2, 2),
                Margin = new Padding(4, 2, 4, 2),
            };

            var label = new Label
            {
                Text = f.Label,
                AutoSize = false,
                Width = 190,
                Dock = DockStyle.Left,
                TextAlign = ContentAlignment.MiddleLeft,
                Padding = new Padding(0, 0, 8, 0),
            };

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
                    var box = new TextBox { Width = 420, Text = f.Default };
                    input = box;
                    break;
            }

            input.Dock = DockStyle.None;  // restore — file/folder rows are TLPs that manage their own layout
            _fieldControls[f.Id] = input;

            row.Controls.Add(input);
            row.Controls.Add(label);
            rows.Add(row);
        }

        // Stack: add in reverse so the first field ends up at the top.
        for (int i = rows.Count - 1; i >= 0; i--)
            _fieldsPanel.Controls.Add(rows[i]);

        _fieldsPanel.ResumeLayout(true);
    }

    private Control MakeFileRow(CommandField f, bool open)
    {
        // Panel: [textbox fill] [browse btn]
        var panel = new Panel
        {
            Width = 420,
            Height = 23,
            Margin = new Padding(0),
        };

        var box = new TextBox { Text = f.Default, Dock = DockStyle.Fill };
        var btn = new Button { Text = "...", Dock = DockStyle.Right, Width = 28 };
        btn.Click += (_, _) =>
        {
            if (open)
            {
                using var dlg = new OpenFileDialog { Title = f.Label, Filter = f.Filter.Length > 0 ? f.Filter : "All files (*.*)|*.*" };
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK) box.Text = dlg.FileName;
            }
            else
            {
                using var dlg = new SaveFileDialog { Title = f.Label, Filter = f.Filter.Length > 0 ? f.Filter : "All files (*.*)|*.*" };
                if (dlg.ShowDialog(FindForm()) == DialogResult.OK) box.Text = dlg.FileName;
            }
        };
        panel.Controls.Add(box);
        panel.Controls.Add(btn);
        // z-order: btn added second → docks right first, box fills rest
        return panel;
    }

    private Control MakeFolderRow(CommandField f)
    {
        var panel = new Panel
        {
            Width = 420,
            Height = 23,
            Margin = new Padding(0),
        };

        var box = new TextBox { Text = f.Default, Dock = DockStyle.Fill };
        var btn = new Button { Text = "...", Dock = DockStyle.Right, Width = 28 };
        btn.Click += (_, _) =>
        {
            using var dlg = new FolderBrowserDialog { Description = f.Label, UseDescriptionForTitle = true };
            if (dlg.ShowDialog(FindForm()) == DialogResult.OK) box.Text = dlg.SelectedPath;
        };
        panel.Controls.Add(box);
        panel.Controls.Add(btn);
        return panel;
    }

    // ------------------------------------------------------------------
    // Run
    // ------------------------------------------------------------------
    private void RaiseRun()
    {
        if (_current == null) return;

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

        RunRequested?.Invoke(_current, values);
    }

    private static TextBox? GetTextBoxOfFileRow(TableLayoutPanel panel)
    {
        if (panel.Controls.Count > 0 && panel.Controls[0] is TextBox tb) return tb;
        return null;
    }

    /// <summary>Exposed so MainForm can set the splitter distance from settings.</summary>
    public SplitContainer Split => _split;
}
