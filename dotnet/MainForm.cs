namespace VxProxy;

public class MainForm : Form
{
    private readonly ProxyEngine _engine;
    private readonly SimTarget _detectedSim;
    private readonly RichTextBox _logBox;
    private readonly Label _statusDot;
    private readonly Label _statusLabel;
    private readonly Label _shotLabel;
    private readonly Button _toggleBtn;
    private const int MaxLogLines = 500;
    private const int TrimBatchSize = 100;

    public MainForm(ProxyEngine engine, SimTarget detectedSim = SimTarget.None)
    {
        _engine = engine;
        _detectedSim = detectedSim;

        Text = $"VX Connector {AppVersion.Display}";
        Icon = LoadAppIcon();
        Size = new Size(750, 500);
        MinimumSize = new Size(500, 300);
        BackColor = Color.FromArgb(30, 30, 30);
        StartPosition = FormStartPosition.CenterScreen;

        // --- Status bar ---
        var statusPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 40,
            BackColor = Color.FromArgb(45, 45, 45),
            Padding = new Padding(10, 8, 10, 8),
        };

        _statusDot = new Label
        {
            Text = "\u25cf",
            Font = new Font("Arial", 14),
            ForeColor = Color.FromArgb(255, 152, 0),
            AutoSize = true,
            Location = new Point(10, 6),
        };

        _statusLabel = new Label
        {
            Text = "Starting...",
            Font = new Font("Segoe UI", 10),
            ForeColor = Color.FromArgb(204, 204, 204),
            AutoSize = true,
            Location = new Point(36, 9),
        };

        _shotLabel = new Label
        {
            Text = "",
            Font = new Font("Segoe UI", 10),
            ForeColor = Color.FromArgb(136, 136, 136),
            AutoSize = true,
            Anchor = AnchorStyles.Top | AnchorStyles.Right,
        };

        statusPanel.Controls.AddRange([_statusDot, _statusLabel, _shotLabel]);

        // Position shot label on the right
        statusPanel.Resize += (_, _) =>
        {
            _shotLabel.Location = new Point(
                statusPanel.ClientSize.Width - _shotLabel.Width - 10, 9);
        };

        // --- Button bar ---
        var btnPanel = new Panel
        {
            Dock = DockStyle.Top,
            Height = 36,
            BackColor = Color.FromArgb(30, 30, 30),
            Padding = new Padding(10, 4, 10, 4),
        };

        _toggleBtn = MakeButton("Stop", 10);
        _toggleBtn.Click += (_, _) =>
        {
            if (_engine.IsRunning) _engine.Stop(); else _engine.Start();
        };

        var clearBtn = MakeButton("Clear Log", 88);
        clearBtn.Click += (_, _) => _logBox!.Clear();

        btnPanel.Controls.AddRange([_toggleBtn, clearBtn]);

        // --- Log area ---
        _logBox = new RichTextBox
        {
            Dock = DockStyle.Fill,
            ReadOnly = true,
            BackColor = Color.FromArgb(30, 30, 30),
            ForeColor = Color.FromArgb(212, 212, 212),
            Font = new Font("Consolas", 9),
            BorderStyle = BorderStyle.None,
            WordWrap = true,
            Padding = new Padding(8),
        };

        // Add controls — fill first, then top panels stack in reverse order
        Controls.Add(_logBox);
        Controls.Add(btnPanel);
        Controls.Add(statusPanel);

        // --- Wire up engine events (marshal to UI thread) ---
        _engine.Log += line =>
        {
            if (InvokeRequired)
                BeginInvoke(() => AppendLog(line));
            else
                AppendLog(line);
        };

        _engine.StatusChanged += status =>
        {
            if (InvokeRequired)
                BeginInvoke(() => RefreshStatus(status));
            else
                RefreshStatus(status);
        };

        _engine.ConnectionStateChanged += _ =>
        {
            // Fires for either half of the relay (IsConnected / IsUpstreamConnected);
            // the visuals read both, so just re-render with the current Status.
            if (InvokeRequired)
                BeginInvoke(() => RefreshStatus(_engine.Status));
            else
                RefreshStatus(_engine.Status);
        };

        _engine.ShotReceived += shot =>
        {
            void Update() => _shotLabel.Text =
                $"SHOT #{shot.ShotNumber}  Speed={shot.BallSpeed:F1}  Spin={shot.TotalSpin:F0}";

            if (InvokeRequired)
                BeginInvoke(Update);
            else
                Update();
        };
    }

    private static Button MakeButton(string text, int x)
    {
        var btn = new Button
        {
            Text = text,
            FlatStyle = FlatStyle.Flat,
            BackColor = Color.FromArgb(51, 51, 51),
            ForeColor = Color.White,
            Size = new Size(text.Length * 9 + 20, 28),
            Location = new Point(x, 4),
            Cursor = Cursors.Hand,
        };
        btn.FlatAppearance.BorderSize = 0;
        return btn;
    }

    private void AppendLog(string line)
    {
        // Trim old lines when buffer gets large
        if (_logBox.Lines.Length > MaxLogLines)
        {
            _logBox.SelectionStart = 0;
            _logBox.SelectionLength = _logBox.GetFirstCharIndexFromLine(TrimBatchSize);
            _logBox.SelectedText = "";
        }

        Color color = GetLineColor(line);

        _logBox.SelectionStart = _logBox.TextLength;
        _logBox.SelectionLength = 0;
        _logBox.SelectionColor = color;
        _logBox.AppendText(line + "\n");
        _logBox.ScrollToCaret();
    }

    private static Color GetLineColor(string line)
    {
        if (line.Contains("SHOT"))
            return Color.FromArgb(78, 201, 176);   // teal
        if (line.Contains("Connected") || line.Contains("listening"))
            return Color.FromArgb(106, 153, 85);    // green
        if (line.Contains("Cannot") || line.Contains("Reconnecting"))
            return Color.FromArgb(206, 145, 120);   // orange
        if (line.Contains("Error") || line.Contains("error"))
            return Color.FromArgb(244, 71, 71);     // red
        if (line.Contains("Status") || line.Contains("Ready") || line.Contains("Update available"))
            return Color.FromArgb(156, 220, 254);   // blue
        return Color.FromArgb(212, 212, 212);        // default gray
    }

    private void RefreshStatus(ConnectionStatus status)
    {
        (_statusDot.ForeColor, _statusLabel.Text) = status switch
        {
            ConnectionStatus.Direct =>
                (Color.FromArgb(33, 150, 243), DirectStatusText()),
            ConnectionStatus.Proxy or ConnectionStatus.FolderWatcher =>
                ForwardingStatusVisuals(),
            _ =>
                (Color.FromArgb(244, 67, 54), "Stopped"),
        };

        _toggleBtn.Text = _engine.IsRunning ? "Stop" : "Start";
    }

    private string DirectStatusText()
    {
        var label = TrayApplicationContext.FormatSims(_detectedSim) ?? "the target sim";
        return $"Direct mode — ProTee Labs talks straight to {label}";
    }

    /// <summary>
    /// Status for both forwarding modes.
    ///
    /// Proxy mode needs both halves of the relay before anything can flow — ProTee Labs
    /// on our :12321 listener and the sim on the far side — so the dot goes green only
    /// when both are up, matching the tray icon. Folder Watcher has no upstream socket,
    /// so there the dot tracks the sim alone; that mode is called out by name so it's
    /// obvious when you're on the path that no longer works with the PTL beta.
    /// </summary>
    private (Color color, string text) ForwardingStatusVisuals()
    {
        var (sim, port) = _engine.ResolveForwardTarget();
        var green = Color.FromArgb(76, 175, 80);
        var amber = Color.FromArgb(255, 152, 0);

        if (_engine.FolderWatcherMode)
        {
            const string suffix = " (legacy folder watcher)";
            return _engine.IsConnected
                ? (green, $"Connected to {sim} on port {port}{suffix}")
                : (amber, $"Disconnected from {sim} on port {port}{suffix}");
        }

        // With ProTee Labs away the sim link is closed on purpose, so there is no
        // "connected to the sim but waiting for PTL" state left to report.
        return (_engine.IsUpstreamConnected, _engine.IsConnected) switch
        {
            (true, true) => (green, $"Connected — ProTee Labs and {sim} on port {port}"),
            (true, false) => (amber, $"Waiting for {sim} on port {port}"),
            _ => (amber, $"Waiting for ProTee Labs — {sim} link closed until it connects"),
        };
    }

    protected override void OnFormClosing(FormClosingEventArgs e)
    {
        if (e.CloseReason == CloseReason.UserClosing)
        {
            // Don't exit — hide to tray instead
            e.Cancel = true;
            Hide();
        }
    }

    /// <summary>
    /// The window icon is the same vx-connector.ico the exe carries as its shell icon,
    /// so the title bar, alt-tab and Explorer all show one thing. Read from the managed
    /// resource rather than drawn at runtime: the .ico carries per-size artwork (the
    /// 16px frame drops most of the dimples, which is the only way it reads at that
    /// scale), and a single 48px bitmap scaled down cannot.
    /// </summary>
    private static Icon LoadAppIcon()
    {
        var stream = typeof(MainForm).Assembly.GetManifestResourceStream("vx-connector.ico");
        // Falling back rather than throwing: a missing icon is cosmetic, and the build
        // would have to have dropped the EmbeddedResource for it to happen at all.
        if (stream is null) return SystemIcons.Application;

        using (stream)
            return new Icon(stream);
    }
}
