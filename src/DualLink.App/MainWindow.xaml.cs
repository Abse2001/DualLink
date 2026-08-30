using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using DualLink.Core;

namespace DualLink.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<AdapterRow> _rows = [];
    private readonly DualLinkSettings _settings = new();
    private readonly NetworkService _network = new();
    private readonly CancellationTokenSource _stop = new();
    private FailoverController _controller;

    public MainWindow()
    {
        InitializeComponent();
        AdapterGrid.ItemsSource = _rows;
        _controller = new FailoverController(_settings);
        Loaded += async (_, _) => await MonitorLoop();
    }

    private async Task MonitorLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            await RefreshAsync();
            try { await Task.Delay(TimeSpan.FromSeconds(_settings.ProbeIntervalSeconds), _stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var adapters = _network.GetInternetAdapters();
            var probes = await Task.WhenAll(adapters.Select(x => _network.ProbeAsync(x, _settings.ProbeHost, _stop.Token)));
            var decision = _controller.Evaluate(probes);
            if (AutoCheck.IsChecked == true && decision.ActiveAdapterId is not null && decision.Changed)
                await _network.ApplyMetricsAsync(adapters, decision.ActiveAdapterId, _settings.PreferredMetric, _settings.BackupMetric);

            _rows.Clear();
            foreach (var adapter in adapters)
            {
                var probe = probes.First(x => x.AdapterId == adapter.Id);
                _rows.Add(AdapterRow.From(adapter, probe, decision.ActiveAdapterId == adapter.Id));
            }
            ActiveText.Text = decision.ActiveAdapterId is null ? "" : $"Active: {adapters.FirstOrDefault(x => x.Id == decision.ActiveAdapterId)?.Name}";
            StatusText.Text = decision.Reason;
            StatusDot.Fill = new SolidColorBrush(probes.Any(x => x.Online) ? Color.FromRgb(34, 197, 94) : Color.FromRgb(239, 68, 68));
            AppLog.Write(decision.Reason);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            StatusDot.Fill = Brushes.Red;
            AppLog.Write(ex.ToString());
        }
    }

    private async void ProbeNow_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        AutoCheck.IsChecked = false;
        await _network.RestoreAutomaticMetricsAsync();
        _controller = new FailoverController(_settings);
        StatusText.Text = "Windows automatic metrics restored";
    }

    protected override void OnClosed(EventArgs e) { _stop.Cancel(); base.OnClosed(e); }
}

public sealed record AdapterRow(string Name, string Type, string Address, string Latency, string Jitter, string Loss, string Score, string Role)
{
    public static AdapterRow From(AdapterInfo adapter, ProbeResult probe, bool active) => new(
        adapter.Name, adapter.Type.ToString(), adapter.Address?.ToString() ?? "—",
        probe.Online ? $"{probe.LatencyMs:0} ms" : "Offline",
        probe.Online ? $"{probe.JitterMs:0} ms" : "—",
        $"{probe.PacketLossPercent:0}%", $"{probe.Score:0}", active ? "Preferred" : "Backup");
}
