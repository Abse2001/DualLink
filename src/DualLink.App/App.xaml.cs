using System.Windows;

namespace DualLink.App;

public partial class App : Application
{
    private System.Windows.Forms.NotifyIcon? _tray;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        _window = new MainWindow();
        _window.Show();
        _tray = new System.Windows.Forms.NotifyIcon
        {
            Icon = System.Drawing.SystemIcons.Shield,
            Text = "DualLink network optimizer",
            Visible = true,
            ContextMenuStrip = new System.Windows.Forms.ContextMenuStrip()
        };
        _tray.ContextMenuStrip.Items.Add("Open", null, (_, _) => Dispatcher.Invoke(() => { _window.Show(); _window.Activate(); }));
        _tray.ContextMenuStrip.Items.Add("Exit", null, (_, _) => Dispatcher.Invoke(Shutdown));
        _tray.DoubleClick += (_, _) => Dispatcher.Invoke(() => { _window.Show(); _window.Activate(); });
        _window.Closing += (_, args) => { args.Cancel = true; _window.Hide(); };
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _tray?.Dispose();
        base.OnExit(e);
    }
}
