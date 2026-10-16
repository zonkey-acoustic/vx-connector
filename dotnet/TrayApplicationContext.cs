namespace VxProxy;

public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ProxyEngine _engine;
    private readonly MainForm _form;
    private readonly SimTarget _detectedSim;
    private readonly UpdateChecker _updates;
    private readonly SynchronizationContext? _ui;
    private ConnectionStatus _currentStatus;
    private ToolStripMenuItem _updateItem = null!;
    private ToolStripMenuItem _autoUpdateItem = null!;

    public TrayApplicationContext(
        EngineMode startupMode = EngineMode.ProxyInfiniteTees,
        SimTarget detectedSim = SimTarget.None)
    {
        _detectedSim = detectedSim;
        _engine = new ProxyEngine { Mode = startupMode };
        _form = new MainForm(_engine, detectedSim);

        // Constructing a Control installs the WinForms sync context, so this is a valid
        // marshalling target from here on. Used instead of _form.Invoke for update
        // notifications: the log window may never have been shown, and a Form with no
        // handle isn't something you can BeginInvoke onto.
        _ui = SynchronizationContext.Current;

        // Carries "Start with Windows" across a move — most importantly the move from a
        // loose downloaded exe into the installed location.
        StartupRegistration.RepairIfStale(_engine.RaiseLog);

        _updates = new UpdateChecker(_engine.RaiseLog)
        {
            AutoCheckEnabled = UserSettings.LoadUpdateChecksEnabled(),
        };
        _updates.UpdateFound += OnUpdateFound;

        _tray = new NotifyIcon
        {
            Icon = CreateIcon(Color.FromArgb(255, 152, 0)),
            Text = "VX Connector — Starting",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        _tray.DoubleClick += (_, _) => ShowForm();

        _engine.StatusChanged += OnStatusChanged;

        // Either half of the relay changing state has to repaint the icon. The
        // mode-level ConnectionStatus doesn't change with it, so force the refresh
        // past the early-out in ApplyStatus.
        _engine.ConnectionStateChanged += _ => ApplyStatus(_engine.Status, forceRefresh: true);

        _engine.Start();

        // Defensive: ensure the tray reflects the actual engine status, even if
        // the StatusChanged event fired before we could subscribe (race).
        ApplyStatus(_engine.Status, forceRefresh: true);

        _updates.Start();
    }

    private void OnStatusChanged(ConnectionStatus status) => ApplyStatus(status, forceRefresh: false);

    private void ApplyStatus(ConnectionStatus status, bool forceRefresh)
    {
        if (!forceRefresh && status == _currentStatus) return;
        _currentStatus = status;

        var (color, text) = status switch
        {
            ConnectionStatus.Direct =>
                (Color.FromArgb(33, 150, 243), DirectStatusText()),
            // Green only when the relay is actually complete end to end: ProTee Labs
            // on our listener AND the sim on the far side. Purple means the proxy is
            // up but one half is still missing, so nothing would flow yet.
            ConnectionStatus.Proxy =>
                (_engine.IsUpstreamConnected && _engine.IsConnected
                    ? Color.FromArgb(76, 175, 80)
                    : Color.FromArgb(156, 39, 176), ProxyStatusText()),
            ConnectionStatus.FolderWatcher =>
                (Color.FromArgb(0, 150, 136), ForwardingStatusText("Legacy")),
            _ =>
                (Color.FromArgb(244, 67, 54), "VX Connector — Stopped"),
        };

        if (_form.InvokeRequired)
            _form.BeginInvoke(() => UpdateTray(color, text));
        else
            UpdateTray(color, text);
    }

    private void UpdateTray(Color color, string text)
    {
        // Deliberately not folded into the icon colour: those five colours already encode
        // mode and connection health, and overloading them with update state would muddy a
        // documented signal. The tooltip carries it instead.
        if (_updates.Available is { } update) text += $" — v{update.Version} available";

        var oldIcon = _tray.Icon;
        _tray.Icon = CreateIcon(color);
        // NotifyIcon.Text throws past 127 chars, and the longest "waiting for both halves"
        // string plus the update suffix gets close enough to be worth clamping.
        _tray.Text = text.Length > 127 ? text[..126] + "…" : text;
        oldIcon?.Dispose();
    }

    private string DirectStatusText()
    {
        var label = FormatSims(_detectedSim) ?? "passthrough";
        return $"VX Connector — Direct mode ({label})";
    }

    internal static string? FormatSims(SimTarget sims)
    {
        if (sims == SimTarget.None) return null;
        var parts = new List<string>();
        if (sims.HasFlag(SimTarget.Drills)) parts.Add(SimTarget.Drills.DisplayName());
        if (sims.HasFlag(SimTarget.InfiniteTees)) parts.Add(SimTarget.InfiniteTees.DisplayName());
        return string.Join("/", parts);
    }

    private string ForwardingStatusText(string modeLabel)
    {
        var (sim, port) = _engine.ResolveForwardTarget();
        return $"VX Connector — {modeLabel} → {sim} :{port}";
    }

    /// <summary>
    /// Proxy tooltip. The icon colour says "complete or not"; the tooltip has to say
    /// *which* half is missing, since "running but nothing arrives" is the state
    /// people actually need to debug.
    /// </summary>
    private string ProxyStatusText()
    {
        var (sim, port) = _engine.ResolveForwardTarget();
        var baseText = $"VX Connector — Proxy → {sim} :{port}";

        // Only two states below, not four: with PTL away we close the sim link on
        // purpose, so "waiting for the sim" would be reporting our own doing as
        // something the user could go and fix.
        return (_engine.IsUpstreamConnected, _engine.IsConnected) switch
        {
            (true, true) => $"{baseText} (connected)",
            (true, false) => $"{baseText} (waiting for {sim})",
            _ => $"{baseText} (waiting for ProTee Labs)",
        };
    }

    /// <summary>
    /// Generate a 32x32 icon: dark circle background, colored inner circle, "VX" text.
    /// </summary>
    private static Icon CreateIcon(Color fill)
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        using var darkBrush = new SolidBrush(Color.FromArgb(40, 40, 40));
        g.FillEllipse(darkBrush, 1, 1, 30, 30);

        using var fillBrush = new SolidBrush(fill);
        g.FillEllipse(fillBrush, 3, 3, 26, 26);

        using var font = new Font("Arial", 10, FontStyle.Bold);
        var sf = new StringFormat
        {
            Alignment = StringAlignment.Center,
            LineAlignment = StringAlignment.Center,
        };
        g.DrawString("VX", font, Brushes.White, new RectangleF(0, 0, 32, 32), sf);

        return Icon.FromHandle(bmp.GetHicon());
    }

    private ContextMenuStrip BuildMenu()
    {
        var menu = new ContextMenuStrip();
        // Inert header — the tray menu is the only UI a user sees when the log
        // window is closed, so the version needs to be readable from here too.
        menu.Items.Add(new ToolStripMenuItem($"VX Connector {AppVersion.Display}") { Enabled = false });

        // Sits directly under the version it supersedes. Hidden until a check finds
        // something, so the menu looks exactly as it does today when we're current.
        _updateItem = new ToolStripMenuItem("Update available") { Visible = false };
        _updateItem.Click += (_, _) => InstallAvailableUpdate();
        menu.Items.Add(_updateItem);

        menu.Items.Add(new ToolStripSeparator());

        menu.Items.Add("Show Log", null, (_, _) => ShowForm());
        menu.Items.Add("Check for Updates…", null, (_, _) => CheckForUpdatesNow());
        menu.Items.Add(new ToolStripSeparator());

        var toDrillsItem = menu.Items.Add(
            "Switch to OpenAPI Compatible",
            null,
            (_, _) => SwitchEngineMode(EngineMode.ProxyDrills));
        var toIteesItem = menu.Items.Add(
            "Switch to Infinite Tees",
            null,
            (_, _) => SwitchEngineMode(EngineMode.ProxyInfiniteTees));

        menu.Items.Add(new ToolStripSeparator());
        var startWithWindowsItem = new ToolStripMenuItem("Start with Windows")
        {
            CheckOnClick = false,   // we set the check from the registry, not from the click
        };
        startWithWindowsItem.Click += (_, _) => ToggleStartWithWindows(startWithWindowsItem);
        menu.Items.Add(startWithWindowsItem);

        // The update check is the app's only outbound internet traffic, so it gets an
        // explicit opt-out. Unlike the startup entry this one IS ours to persist, so the
        // checked state comes from settings.json rather than being re-read from elsewhere.
        _autoUpdateItem = new ToolStripMenuItem("Automatically Check for Updates")
        {
            Checked = _updates.AutoCheckEnabled,
            CheckOnClick = true,
        };
        _autoUpdateItem.CheckedChanged += (_, _) => ToggleAutoUpdate();
        menu.Items.Add(_autoUpdateItem);

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        menu.Opening += (_, _) =>
        {
            // Read from the registry each time: the user can also remove the entry via
            // Task Manager's Startup tab, and the menu must reflect that.
            startWithWindowsItem.Checked = StartupRegistration.IsEnabled();

            toDrillsItem.Visible = _engine.Mode != EngineMode.ProxyDrills;
            toIteesItem.Visible = _engine.Mode != EngineMode.ProxyInfiniteTees;
        };

        return menu;
    }

    /// <summary>
    /// Raised from the background poll. Marshal before touching the tray or the menu.
    /// </summary>
    private void OnUpdateFound(AvailableUpdate update)
    {
        if (_ui is not null && _ui != SynchronizationContext.Current)
        {
            _ui.Post(_ => ShowUpdateAvailable(update, notify: true), null);
            return;
        }
        ShowUpdateAvailable(update, notify: true);
    }

    private void ShowUpdateAvailable(AvailableUpdate update, bool notify)
    {
        _updateItem.Text = $"Update to v{update.Version}…";
        _updateItem.Visible = true;
        _updateItem.Enabled = true;

        // Repaint so the tooltip picks up the suffix; the status itself hasn't changed.
        ApplyStatus(_engine.Status, forceRefresh: true);

        if (notify)
        {
            _tray.ShowBalloonTip(
                10_000,
                "VX Connector update available",
                $"Version {update.Version} is ready to install ({update.SizeLabel}). " +
                "Right-click the tray icon to install it.",
                ToolTipIcon.Info);
        }
    }

    private async void CheckForUpdatesNow()
    {
        var result = await _updates.CheckNowAsync();

        switch (result.Outcome)
        {
            case CheckOutcome.UpdateAvailable when result.Update is { } update:
                // The user asked, so answer in a dialog rather than only a balloon — and
                // offer to act on it while they're already here.
                ShowUpdateAvailable(update, notify: false);
                var install = MessageBox.Show(
                    $"Version {update.Version} is available (you're on {AppVersion.Display}).\n\n" +
                    "Install it now?",
                    "VX Connector", MessageBoxButtons.YesNo, MessageBoxIcon.Information);
                if (install == DialogResult.Yes) InstallAvailableUpdate();
                break;

            case CheckOutcome.UpToDate:
                MessageBox.Show(
                    $"VX Connector {AppVersion.Display} is up to date.",
                    "VX Connector", MessageBoxButtons.OK, MessageBoxIcon.Information);
                break;

            default:
                MessageBox.Show(
                    "Could not reach GitHub to check for updates. See the log window for details.",
                    "VX Connector", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                break;
        }
    }

    private async void InstallAvailableUpdate()
    {
        if (_updates.Available is not { } update) return;
        if (!_updateItem.Enabled) return;   // already installing

        var confirm = MessageBox.Show(
            $"Install VX Connector v{update.Version}?\n\n" +
            $"That's a {update.SizeLabel} download. VX Connector restarts once it's ready, " +
            "so shot forwarding pauses for a few seconds — don't do this mid-shot.",
            "VX Connector", MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
        if (confirm != DialogResult.OK) return;

        _updateItem.Enabled = false;
        _updateItem.Text = $"Installing v{update.Version}…";

        bool installed;
        try
        {
            installed = await UpdateApplier.ApplyAsync(update, _engine.RaiseLog);
        }
        catch (Exception ex)
        {
            _engine.RaiseLog($"Update Error: {ex.Message}");
            installed = false;
        }

        if (!installed)
        {
            _updateItem.Enabled = true;
            _updateItem.Text = $"Update to v{update.Version}…";
            MessageBox.Show(
                "The update was not installed. Your current version is untouched — " +
                "see the log window for details.",
                "VX Connector", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            return;
        }

        // Free :12321 and the sim socket before the replacement tries to bind them. The
        // new instance's single-instance kill would also take us out, but releasing the
        // listener first means its bind can't lose a race with our teardown.
        _engine.Stop();
        UpdateApplier.RestartInto(_engine.Mode, _engine.RaiseLog);

        _tray.Visible = false;
        Application.Exit();
    }

    private void ToggleAutoUpdate()
    {
        var enabled = _autoUpdateItem.Checked;
        _updates.AutoCheckEnabled = enabled;
        UserSettings.SaveUpdateChecksEnabled(enabled);
        _engine.RaiseLog(enabled
            ? "Automatic update checks are on."
            : "Automatic update checks are off.");
    }

    private void ToggleStartWithWindows(ToolStripMenuItem item)
    {
        var desired = !StartupRegistration.IsEnabled();
        if (StartupRegistration.SetEnabled(desired, _engine.RaiseLog))
        {
            item.Checked = desired;
        }
        else
        {
            // Registry write failed (policy, permissions) — reflect reality, and the
            // reason is already in the log window.
            item.Checked = StartupRegistration.IsEnabled();
            MessageBox.Show(
                "Could not change the Windows startup setting. See the log window for details.",
                "VX Connector", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void SwitchEngineMode(EngineMode mode)
    {
        _engine.RestartIn(mode);
        UserSettings.SaveLastMode(mode);
        ApplyStatus(_engine.Status, forceRefresh: true);
    }

    private void ShowForm()
    {
        _form.Show();
        _form.WindowState = FormWindowState.Normal;
        _form.BringToFront();
        _form.Activate();
    }

    private void Quit()
    {
        _engine.Stop();
        _tray.Visible = false;
        _tray.Dispose();
        _engine.Dispose();
        Application.Exit();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _updates.Dispose();
            _tray.Dispose();
            _engine.Dispose();
            _form.Dispose();
        }
        base.Dispose(disposing);
    }
}
