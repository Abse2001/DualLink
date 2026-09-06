using System.Windows;

namespace DualLink.App;

public partial class App : System.Windows.Application
{
    private System.Windows.Forms.NotifyIcon? _tray;
    private System.Drawing.Icon? _trayIcon;
    private MainWindow? _window;
    private bool _exitRequested;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (e.Args.Contains("--wintun-smoke", StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                using var device = new WintunDevice("DualLink CI Smoke");
                Shutdown(0);
            }
            catch (Exception error)
            {
                AppLog.Write($"Wintun smoke test failed: {error}");
                Shutdown(1);
            }
            return;
        }
        _window = new MainWindow();
        var startMinimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
        _trayIcon = Environment.ProcessPath is { } processPath
            ? System.Drawing.Icon.ExtractAssociatedIcon(processPath)
            : null;
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = _trayIcon ?? System.Drawing.SystemIcons.Application,
            Text = "DualLink — network bonding",
            Visible = true,
            ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip()
        };
        _tray.ContextMenuStrip.Items.Add("Open DualLink", null, (_, _) => Dispatcher.Invoke(RestoreWindow));
        _tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(() =>
        {
            _exitRequested = true;
            Shutdown();
        }));
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreWindow);
        _window.StateChanged += (_, _) =>
        {
            if (_window.WindowState == WindowState.Minimized)
                _window.Hide();
        };
        _window.Closing += (_, args) =>
        {
            if (_exitRequested) return;
            args.Cancel = true;
            _window.Hide();
        };

        if (!startMinimized)
            _window.Show();
    }

    private void RestoreWindow()
    {
        if (_window is null) return;
        _window.Show();
        _window.WindowState = WindowState.Normal;
        _window.Activate();
        _window.Topmost = true;
        _window.Topmost = false;
        _window.Focus();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        _trayIcon?.Dispose();
        base.OnExit(e);
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _exitRequested = true;
        base.OnSessionEnding(e);
    }
}
