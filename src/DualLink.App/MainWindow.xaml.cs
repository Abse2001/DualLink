using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using DualLink.Core;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;

namespace DualLink.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<AdapterRow> _rows = [];
    private readonly DualLinkSettings _settings = new();
    private readonly NetworkService _network = new();
    private readonly CancellationTokenSource _stop = new();
    private FailoverController _controller;
    private string? _selectedPreferenceId;
    private string? _lastAppliedId;
    private int _preferredRecoveryWins;
    private bool _updatingChoices;

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
            UpdateConnectionChoices(adapters);
            var protonActive = ProtonModeCheck.IsChecked == true && _network.IsProtonTunnelActive();
            var probes = await Task.WhenAll(adapters.Select(x => _network.ProbeAsync(x, _settings.ProbeHost, protonActive, _stop.Token)));
            var decision = ChooseConnection(probes);
            if (AutoCheck.IsChecked == true && decision.ActiveAdapterId is not null && decision.Changed)
            {
                await _network.ApplyMetricsAsync(adapters, decision.ActiveAdapterId, _settings.PreferredMetric, _settings.BackupMetric);
                _lastAppliedId = decision.ActiveAdapterId;
            }

            _rows.Clear();
            foreach (var adapter in adapters)
            {
                var probe = probes.First(x => x.AdapterId == adapter.Id);
                _rows.Add(AdapterRow.From(adapter, probe, decision.ActiveAdapterId == adapter.Id));
            }
            ActiveText.Text = decision.ActiveAdapterId is null ? "" : $"Active: {adapters.FirstOrDefault(x => x.Id == decision.ActiveAdapterId)?.Name}";
            StatusText.Text = decision.Reason;
            VpnText.Text = ProtonModeCheck.IsChecked == true
                ? protonActive
                    ? "Proton tunnel detected. Protected gateway monitoring is active; GTA traffic stays inside Proton."
                    : "Proton-safe mode is enabled, but no active Proton/WireGuard tunnel was detected. Connect Proton before opening GTA."
                : "Proton-safe mode is off. Switching between router and hotspot will change GTA's public IP.";
            VpnText.Foreground = new SolidColorBrush(protonActive ? MediaColor.FromRgb(134, 239, 172) : MediaColor.FromRgb(253, 230, 138));
            StatusDot.Fill = new SolidColorBrush(probes.Any(x => x.Online) ? MediaColor.FromRgb(34, 197, 94) : MediaColor.FromRgb(239, 68, 68));
            if (protonActive && probes.Any(x => x.Online))
                StatusText.Text = $"{decision.Reason} — measuring gateway latency while Proton is connected";
            AppLog.Write(decision.Reason);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            StatusDot.Fill = MediaBrushes.Red;
            AppLog.Write(ex.ToString());
        }
    }

    private FailoverDecision ChooseConnection(IReadOnlyCollection<ProbeResult> probes)
    {
        if (_selectedPreferenceId is null) return _controller.Evaluate(probes);

        var preferred = probes.FirstOrDefault(x => x.AdapterId == _selectedPreferenceId);
        var current = probes.FirstOrDefault(x => x.AdapterId == _lastAppliedId);
        if (preferred?.Online == true)
        {
            if (_lastAppliedId is null || _lastAppliedId == preferred.AdapterId || current?.Online != true)
            {
                _preferredRecoveryWins = 0;
                return new(preferred.AdapterId, _lastAppliedId != preferred.AdapterId, "Using your preferred connection");
            }

            _preferredRecoveryWins++;
            if (_preferredRecoveryWins < 10)
                return new(_lastAppliedId, false, $"Preferred connection recovered; waiting for stability ({_preferredRecoveryWins}/10)");

            _preferredRecoveryWins = 0;
            return new(preferred.AdapterId, true, "Preferred connection is stable again");
        }

        _preferredRecoveryWins = 0;
        var backup = probes.Where(x => x.Online).OrderByDescending(x => x.Score).FirstOrDefault();
        if (backup is null) return new(_lastAppliedId, false, "Preferred connection is offline; no working backup found");
        return new(backup.AdapterId, _lastAppliedId != backup.AdapterId, "Preferred connection failed; using the healthiest backup");
    }

    private void UpdateConnectionChoices(IReadOnlyList<AdapterInfo> adapters)
    {
        var choices = new List<AdapterChoice> { new(null, "Automatic — best quality") };
        choices.AddRange(adapters.Select(x => new AdapterChoice(x.Id, $"Prefer {x.Name} ({FriendlyType(x.Type)})")));
        var existingIds = PreferredCombo.Items.Cast<AdapterChoice>().Select(x => x.Id).ToList();
        if (existingIds.SequenceEqual(choices.Select(x => x.Id))) return;
        _updatingChoices = true;
        PreferredCombo.ItemsSource = choices;
        PreferredCombo.SelectedItem = choices.FirstOrDefault(x => x.Id == _selectedPreferenceId) ?? choices[0];
        _updatingChoices = false;
    }

    private static string FriendlyType(System.Net.NetworkInformation.NetworkInterfaceType type) =>
        type == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet";

    private void PreferredCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingChoices || PreferredCombo.SelectedItem is not AdapterChoice choice) return;
        _selectedPreferenceId = choice.Id;
        _preferredRecoveryWins = 0;
        _controller = new FailoverController(_settings);
        AppLog.Write($"Preference changed to {choice.DisplayName}");
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

public sealed record AdapterChoice(string? Id, string DisplayName);
