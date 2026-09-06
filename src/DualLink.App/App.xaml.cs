using System.Windows;
using System.Windows.Threading;

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
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        AppLog.Write($"LinkWeaver starting. Arguments: {string.Join(' ', e.Args)}");
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
        try
        {
            var startMinimized = e.Args.Contains("--minimized", StringComparer.OrdinalIgnoreCase);
            _trayIcon = Environment.ProcessPath is { } processPath
                ? System.Drawing.Icon.ExtractAssociatedIcon(processPath)
                : null;
            _tray = new System.Windows.Forms.NotifyIcon
            {
                Icon = _trayIcon ?? System.Drawing.SystemIcons.Application,
                Text = "LinkWeaver — network bonding",
                Visible = true,
                ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip()
            };
            _tray.ContextMenuStrip.Items.Add("Open LinkWeaver", null, (_, _) => Dispatcher.Invoke(RestoreWindow));
            _tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(() =>
            {
                _exitRequested = true;
                Shutdown();
            }));
            _tray.DoubleClick += (_, _) => Dispatcher.Invoke(RestoreWindow);

            _window = new MainWindow();
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

            _window.Show();
            if (startMinimized)
            {
                _window.Hide();
                _tray.BalloonTipTitle = "LinkWeaver is running";
                _tray.BalloonTipText = "Monitoring continues in the system tray. Double-click the icon to open it.";
                _tray.ShowBalloonTip(4000);
            }
            else RestoreWindow();
        }
        catch (Exception error)
        {
            AppLog.Write($"LinkWeaver startup failed: {error}");
            System.Windows.MessageBox.Show($"LinkWeaver could not start.\n\n{error.Message}\n\nDetails were saved to:\n%LOCALAPPDATA%\\DualLink\\duallink.log",
                "LinkWeaver startup error", MessageBoxButton.OK, MessageBoxImage.Error);
            _exitRequested = true;
            Shutdown(1);
        }
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

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        AppLog.Write($"Unhandled LinkWeaver UI error: {e.Exception}");
        System.Windows.MessageBox.Show($"LinkWeaver encountered an error.\n\n{e.Exception.Message}\n\nDetails were saved to:\n%LOCALAPPDATA%\\DualLink\\duallink.log",
            "LinkWeaver error", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        _exitRequested = true;
        base.OnSessionEnding(e);
    }
}
