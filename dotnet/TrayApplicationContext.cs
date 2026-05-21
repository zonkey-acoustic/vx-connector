namespace VxProxy;

public class TrayApplicationContext : ApplicationContext
{
    private readonly NotifyIcon _tray;
    private readonly ProxyEngine _engine;
    private readonly MainForm _form;
    private ConnectionStatus _currentStatus;

    public TrayApplicationContext(bool directMode = false)
    {
        _engine = new ProxyEngine { DirectMode = directMode };
        _form = new MainForm(_engine);

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
    }

    private void OnStatusChanged(ConnectionStatus status)
    {
        if (status == _currentStatus) return;
        _currentStatus = status;

        var (color, text) = status switch
        {
            ConnectionStatus.Connected =>
                (Color.FromArgb(76, 175, 80), "VX Proxy — All connected"),
            ConnectionStatus.ProTeeOnly =>
                (Color.FromArgb(255, 193, 7), "VX Proxy — Infinite Tees disconnected"),
            ConnectionStatus.Listening =>
                (Color.FromArgb(255, 152, 0), "VX Proxy — Waiting for ProTee Labs"),
            ConnectionStatus.Direct =>
                (Color.FromArgb(33, 150, 243), "VX Proxy — Direct mode (passthrough)"),
            _ =>
                (Color.FromArgb(244, 67, 54), "VX Proxy — Stopped"),
        };

        // Marshal to UI thread for tray updates
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

    /// <summary>
    /// Generate a 32x32 icon: dark circle background, colored inner circle, "VX" text.
    /// </summary>
    private static Icon CreateIcon(Color fill)
    {
        using var bmp = new Bitmap(32, 32);
        using var g = Graphics.FromImage(bmp);
        g.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
        g.Clear(Color.Transparent);

        // Dark outer circle
        using var darkBrush = new SolidBrush(Color.FromArgb(40, 40, 40));
        g.FillEllipse(darkBrush, 1, 1, 30, 30);

        // Colored inner circle
        using var fillBrush = new SolidBrush(fill);
        g.FillEllipse(fillBrush, 3, 3, 26, 26);

        // "VX" text
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

        var startItem = menu.Items.Add("Start Proxy", null, (_, _) => _engine.Start());
        var stopItem = menu.Items.Add("Stop Proxy", null, (_, _) => _engine.Stop());

        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Quit", null, (_, _) => Quit());

        // Show/hide start/stop based on engine state
        menu.Opening += (_, _) =>
        {
            startItem.Visible = !_engine.IsRunning;
            stopItem.Visible = _engine.IsRunning;
        };

        return menu;
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
