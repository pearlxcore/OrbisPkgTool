namespace OrbisPkgTool.Gui;

/// <summary>
/// Bottom-of-window output console: one tab per run, live-streaming,
/// ✓/✗ exit markers, per-tab close, and a cap of 10 tabs (oldest
/// finished run is dropped first).
/// </summary>
public sealed class OutputConsole : UserControl
{
    private const int MaxTabs = 10;

    private readonly TabControl _tabs = new();
    private readonly List<TabPage> _pages = [];
    private readonly Button _closeTabButton = new();
    private readonly Button _clearAllButton = new();

    /// <summary>Set of pages whose run has not finished yet (never auto-dropped).</summary>
    private readonly HashSet<TabPage> _running = new();

    public event EventHandler? AllRunsCleared;

    public OutputConsole()
    {
        Dock = DockStyle.Fill;
        BuildLayout();
    }

    // ------------------------------------------------------------------
    // Layout
    // ------------------------------------------------------------------
    private void BuildLayout()
    {
        var toolbar = new Panel { Dock = DockStyle.Top, Height = 30, Padding = new Padding(6, 3, 6, 3) };
        _closeTabButton.Text = "Close Tab";
        _closeTabButton.Dock = DockStyle.Left;
        _closeTabButton.Width = 90;
        _closeTabButton.Click += (_, _) => ClosePage(_tabs.SelectedTab);
        _clearAllButton.Text = "Clear All";
        _clearAllButton.Dock = DockStyle.Left;
        _clearAllButton.Width = 90;
        _clearAllButton.Click += (_, _) => ClearAll();
        toolbar.Controls.Add(_clearAllButton);
        toolbar.Controls.Add(_closeTabButton);

        _tabs.Dock = DockStyle.Fill;
        _tabs.Appearance = TabAppearance.Normal;

        // Right-click a tab → close it
        var ctx = new ContextMenuStrip();
        var closeItem = new ToolStripMenuItem("Close tab");
        closeItem.Click += (_, _) => ClosePage(_tabs.SelectedTab);
        ctx.Items.Add(closeItem);
        _tabs.ContextMenuStrip = ctx;

        // Welcome tab so the console isn't an empty void on first launch
        var welcome = new TabPage("welcome")
        {
            Padding = new Padding(4),
        };
        var wbox = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Vertical,
            WordWrap = true,
            Font = new Font("Cascadia Mono", 9f),
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(220, 220, 220),
            BorderStyle = BorderStyle.None,
            Text =
                "OrbisPkgTool GUI" + Environment.NewLine +
                Environment.NewLine +
                "1.  Select a .pkg file in the package bar (top)." + Environment.NewLine +
                "    Metadata loads instantly via in-process PkgReader.GetInfo()." + Environment.NewLine +
                Environment.NewLine +
                "2.  Pick an operation from the Package tab (left tree) or switch" + Environment.NewLine +
                "    to the Build & Tools tab for non-PKG tools." + Environment.NewLine +
                Environment.NewLine +
                "3.  Press Run (or F5). Each run opens a new tab here." + Environment.NewLine +
                Environment.NewLine +
                "Shortcuts: F5 = Run · Esc = Cancel · Double-click op = Run" + Environment.NewLine,
        };
        welcome.Controls.Add(wbox);
        _tabs.TabPages.Add(welcome);

        Controls.Add(_tabs);
        Controls.Add(toolbar);
    }

    // ------------------------------------------------------------------
    // Public API
    // ------------------------------------------------------------------
    /// <summary>Creates a new tab for a run (titled "op · HH:mm"), selects it, returns it.</summary>
    public TabPage StartRun(string opName)
    {
        // Cap: drop the oldest non-running tab when we're at the limit.
        if (_pages.Count >= MaxTabs)
        {
            var victim = _pages.FirstOrDefault(p => !_running.Contains(p));
            if (victim != null) RemovePage(victim);
        }

        string stamp = DateTime.Now.ToString("HH:mm");
        var page = new TabPage($"{opName} · {stamp}")
        {
            ToolTipText = opName,
            Padding = new Padding(4),
        };

        var box = new TextBox
        {
            Dock = DockStyle.Fill,
            Multiline = true,
            ReadOnly = true,
            ScrollBars = ScrollBars.Both,
            WordWrap = false,
            Font = new Font("Cascadia Mono", 9f),
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(220, 220, 220),
            BorderStyle = BorderStyle.None,
        };
        page.Controls.Add(box);

        _pages.Add(page);
        _running.Add(page);
        _tabs.TabPages.Add(page);
        _tabs.SelectedTab = page;
        return page;
    }

    /// <summary>Streams one line into the given run's page (thread-safe).</summary>
    public void AppendLine(TabPage page, string text)
    {
        if (!IsHandleCreated) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => AppendLine(page, text));
            return;
        }
        var box = GetBox(page);
        if (box == null) return;
        box.AppendText(text + Environment.NewLine);
        box.SelectionStart = box.TextLength;
        box.ScrollToCaret();
    }

    /// <summary>Marks the run finished: ✓/✗ in the tab title + exit line.</summary>
    public void FinishRun(TabPage page, bool success, double seconds)
    {
        if (!IsHandleCreated) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => FinishRun(page, success, seconds));
            return;
        }
        _running.Remove(page);
        string marker = success ? "✓" : "✗";
        // title is "op · HH:mm" — prefix the marker
        page.Text = page.Text.StartsWith("✓ ") || page.Text.StartsWith("✗ ")
            ? marker + page.Text[1..]
            : marker + " " + page.Text;
        AppendLine(page, "");
        AppendLine(page, success
            ? $"=== EXIT 0 in {seconds:F1}s ==="
            : $"=== FAILED (exit != 0) in {seconds:F1}s ===");
    }

    /// <summary>Marks the run cancelled.</summary>
    public void CancelRun(TabPage page)
    {
        if (!IsHandleCreated) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => CancelRun(page));
            return;
        }
        _running.Remove(page);
        page.Text = page.Text.StartsWith("✓ ") || page.Text.StartsWith("✗ ")
            ? "✗" + page.Text[1..]
            : "✗ " + page.Text;
        AppendLine(page, "");
        AppendLine(page, "=== CANCELLED ===");
    }

    /// <summary>Reports an infrastructure error (process failed to start, etc.).</summary>
    public void ErrorRun(TabPage page, string message)
    {
        if (!IsHandleCreated) return;
        if (InvokeRequired)
        {
            BeginInvoke(() => ErrorRun(page, message));
            return;
        }
        _running.Remove(page);
        page.Text = page.Text.StartsWith("✓ ") || page.Text.StartsWith("✗ ")
            ? "✗" + page.Text[1..]
            : "✗ " + page.Text;
        AppendLine(page, "");
        AppendLine(page, $"=== ERROR: {message} ===");
    }

    public void ClearAll()
    {
        foreach (var p in _pages.ToList())
            RemovePage(p);
        _running.Clear();
        AllRunsCleared?.Invoke(this, EventArgs.Empty);
    }

    public bool IsRunning(TabPage page) => _running.Contains(page);

    public bool AnyRunning => _running.Count > 0;

    // ------------------------------------------------------------------
    // Internals
    // ------------------------------------------------------------------
    private void ClosePage(TabPage? page)
    {
        if (page == null) return;
        if (_running.Contains(page))
        {
            var r = MessageBox.Show(FindForm(), "This run is still in progress. Close its tab anyway?",
                "Run in progress", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return;
        }
        RemovePage(page);
    }

    private void RemovePage(TabPage page)
    {
        _tabs.TabPages.Remove(page);
        _pages.Remove(page);
        _running.Remove(page);
        page.Dispose();
    }

    private static TextBox? GetBox(TabPage page) =>
        page.Controls.Count > 0 ? page.Controls[0] as TextBox : null;
}
