namespace VxProxy;

public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ProxyEngine _engine;
    private readonly MainForm _form;
    private readonly SimTarget _detectedSim;
    private ConnectionStatus _currentStatus;

    public TrayApplicationContext(EngineMode startupMode = EngineMode.FolderWatcherInfiniteTees, SimTarget detectedSim = SimTarget.None)
    {
        _detectedSim = detectedSim;
        _engine = new ProxyEngine { Mode = startupMode };
        _form = new MainForm(_engine, detectedSim);

        _tray = new NotifyIcon
        {
            Icon = CreateIcon(Color.FromArgb(255, 152, 0)),
            Text = "VX Proxy — Starting",
            Visible = true,
            ContextMenuStrip = BuildMenu(),
        };

        _tray.DoubleClick += (_, _) => ShowForm();

        _engine.StatusChanged += OnStatusChanged;
        _engine.Start();

        // Defensive: ensure the tray reflects the actual engine status, even if
        // the StatusChanged event fired before we could subscribe (race).
        ApplyStatus(_engine.Status, forceRefresh: true);
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
            ConnectionStatus.FolderWatcher =>
                (Color.FromArgb(156, 39, 176), FolderWatcherStatusText()),
            _ =>
                (Color.FromArgb(244, 67, 54), "VX Proxy — Stopped"),
        };

        if (_form.InvokeRequired)
            _form.BeginInvoke(() => UpdateTray(color, text));
        else
            UpdateTray(color, text);
    }

    private void UpdateTray(Color color, string text)
    {
        var oldIcon = _tray.Icon;
        _tray.Icon = CreateIcon(color);
        _tray.Text = text;
        oldIcon?.Dispose();
    }

    private string DirectStatusText()
    {
        var label = FormatSims(_detectedSim) ?? "passthrough";
        return $"VX Proxy — Direct mode ({label})";
    }

    internal static string? FormatSims(SimTarget sims)
    {
        if (sims == SimTarget.None) return null;
        var parts = new List<string>();
        if (sims.HasFlag(SimTarget.Drills)) parts.Add("Drills");
        if (sims.HasFlag(SimTarget.InfiniteTees)) parts.Add("Infinite Tees");
        return string.Join("/", parts);
    }

    private string FolderWatcherStatusText()
    {
        var (sim, port) = _engine.ResolveForwardTarget();
        return $"VX Proxy — Folder watcher → {sim} :{port}";
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
        menu.Items.Add("Show Log", null, (_, _) => ShowForm());
        menu.Items.Add(new ToolStripSeparator());

        var toDirectItem = menu.Items.Add(
            "Switch to Direct mode",
            null,
            (_, _) => SwitchEngineMode(EngineMode.Direct));
        var toFolderWatcherDrillsItem = menu.Items.Add(
            "Switch to Folder Watcher → Drills",
            null,
            (_, _) => SwitchEngineMode(EngineMode.FolderWatcherDrills));
        var toFolderWatcherIteesItem = menu.Items.Add(
            "Switch to Folder Watcher → Infinite Tees",
            null,
            (_, _) => SwitchEngineMode(EngineMode.FolderWatcherInfiniteTees));

        var iteesSeparator = new ToolStripSeparator();
        menu.Items.Add(iteesSeparator);
        var iteesToDirectItem = menu.Items.Add(
            "Switch Infinite Tees to Direct mode (port 921)",
            null,
            (_, _) => SwitchInfiniteTeesToDirect());

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        menu.Opening += (_, _) =>
        {
            toDirectItem.Visible = _engine.Mode != EngineMode.Direct;
            toFolderWatcherDrillsItem.Visible = _engine.Mode != EngineMode.FolderWatcherDrills;
            toFolderWatcherIteesItem.Visible = _engine.Mode != EngineMode.FolderWatcherInfiniteTees;

            var iteesPort = SimConfig.GetInfiniteTeesPort();
            iteesToDirectItem.Visible = iteesPort == 999;
            iteesSeparator.Visible = iteesPort == 999;
        };

        return menu;
    }

    private void SwitchEngineMode(EngineMode mode)
    {
        _engine.RestartIn(mode);
        ApplyStatus(_engine.Status, forceRefresh: true);
    }

    private void SwitchInfiniteTeesToDirect()
    {
        const int newPort = 921;
        const int oldPort = 999;

        var prompt = $"Change the Infinite Tees listening port from {oldPort} to {newPort}?\n\n" +
                     "This puts Infinite Tees on the port ProTee Labs sends to, so Direct mode works without VX Proxy in the data path.\n" +
                     "You'll need to restart Infinite Tees for the change to take effect.";

        var result = MessageBox.Show(
            prompt,
            "Switch Infinite Tees to Direct mode",
            MessageBoxButtons.OKCancel,
            MessageBoxIcon.Question);

        if (result != DialogResult.OK) return;

        if (!SimConfig.SetInfiniteTeesPort(newPort))
        {
            MessageBox.Show(
                $"Could not update {SimConfig.InfiniteTeesIniPath}.\nFile may be missing or read-only.",
                "VX Proxy",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            return;
        }

        MessageBox.Show(
            $"Infinite Tees port set to {newPort}.\n\nRestart Infinite Tees so the new port takes effect.",
            "VX Proxy",
            MessageBoxButtons.OK,
            MessageBoxIcon.Information);
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
            _tray.Dispose();
            _engine.Dispose();
            _form.Dispose();
        }
        base.Dispose(disposing);
    }
}
