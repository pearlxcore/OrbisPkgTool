namespace OrbisPkgTool.Gui;

using System.Collections.Concurrent;
using System.Text;

/// <summary>
/// Bottom-of-window output console: one tab per run, live-streaming,
/// ✓/✗ exit markers, per-tab close, and a cap of 10 tabs (oldest
/// finished run is dropped first).
///
/// Output lines are BATCHED: worker threads enqueue into a concurrent
/// queue and a 100ms UI timer flushes them in one append+scroll. The old
/// per-line BeginInvoke flooded the message pump during progress-heavy
/// stages (PFSC emits ~17k lines for a 1 GB game), freezing the form.
/// </summary>
public sealed class OutputConsole : UserControl
{
    private const int MaxTabs = 10;

    /// <summary>UI text buffer cap — beyond this the head is trimmed so
    /// AppendText stays O(1)-ish on long runs.</summary>
    private const int MaxTextChars = 50_000;

    /// <summary>Characters kept when the head is trimmed.</summary>
    private const int TrimKeepChars = 30_000;

    /// <summary>Flush interval for the batched output queue.</summary>
    private const int FlushIntervalMs = 100;

    private readonly TabControl _tabs = new();
    private readonly List<TabPage> _pages = [];
    private readonly Button _closeTabButton = new();
    private readonly Button _clearAllButton = new();

    /// <summary>Set of pages whose run has not finished yet (never auto-dropped).</summary>
    private readonly HashSet<TabPage> _running = new();

    /// <summary>Batched output: page → lines waiting for the timer flush.</summary>
    private readonly ConcurrentQueue<(TabPage Page, string Text)> _pending = new();

    /// <summary>Drains <see cref="_pending"/> on the UI thread every 100ms.</summary>
    private readonly System.Windows.Forms.Timer _flushTimer = new();

    public event EventHandler? AllRunsCleared;

    public OutputConsole()
    {
        Dock = DockStyle.Fill;
        BuildLayout();

        _flushTimer.Interval = FlushIntervalMs;
        _flushTimer.Tick += (_, _) => FlushPending();
        _flushTimer.Start();
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

    /// <summary>Streams one line into the given run's page (thread-safe, batched).</summary>
    /// <remarks>Lines are enqueued lock-free and flushed on a 100ms UI timer,
    /// so heavy progress output (PFSC: ~17k lines for a 1 GB game) cannot
    /// starve the message pump.</remarks>
    public void AppendLine(TabPage page, string text)
    {
        _pending.Enqueue((page, text));
    }

    /// <summary>Drains the batched output queue on the UI thread.</summary>
    private void FlushPending()
    {
        if (_pending.IsEmpty) return;
        if (!IsHandleCreated) return;

        // Group lines by page so each TextBox gets one append + one scroll.
        var byPage = new Dictionary<TabPage, StringBuilder>();
        while (_pending.TryDequeue(out var item))
        {
            if (!byPage.TryGetValue(item.Page, out var sb))
            {
                sb = new StringBuilder();
                byPage[item.Page] = sb;
            }
            sb.AppendLine(item.Text);
        }

        foreach (var (page, sb) in byPage)
        {
            var box = GetBox(page);
            if (box == null) continue;

            // Trim the head when the buffer is too large, keeping recent output.
            if (box.TextLength > MaxTextChars)
            {
                int keepFrom = box.TextLength - TrimKeepChars;
                // Advance to the next newline so we don't split a line.
                int nl = box.Text.IndexOf('\n', keepFrom);
                if (nl >= 0) keepFrom = nl + 1;
                box.Text = box.Text[keepFrom..];
            }

            box.AppendText(sb.ToString());
            box.SelectionStart = box.TextLength;
            box.ScrollToCaret();
        }
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
        FlushPending(); // immediate flush so the final status is visible
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
        FlushPending();
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
        FlushPending();
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
