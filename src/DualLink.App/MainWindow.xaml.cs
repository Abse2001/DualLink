using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Net;
using System.Windows.Controls;
using DualLink.Core;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;

namespace DualLink.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<AdapterRow> _rows = [];
    private readonly DualLinkSettings _settings = new();
    private readonly NetworkService _network = new();
    private readonly WireGuardConfigService _wireGuardConfig = new();
    private readonly CancellationTokenSource _stop = new();
    private FailoverController _controller;
    private string? _selectedPreferenceId;
    private string? _lastAppliedId;
    private int _preferredRecoveryWins;
    private bool _updatingChoices;
    private System.Net.IPAddress? _wireGuardEndpoint;
    private string? _endpointRouteSignature;
    private BondingEngine? _bonding;
    private IReadOnlyCollection<BondingPathSample> _bondingSamples = [];

    public MainWindow()
    {
        InitializeComponent();
        AdapterGrid.ItemsSource = _rows;
        _controller = new FailoverController(_settings);
        _wireGuardEndpoint = _wireGuardConfig.LoadEndpoint();
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
            var routePreference = _lastAppliedId ?? _selectedPreferenceId ?? adapters.FirstOrDefault(x => x.Type == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet)?.Id;
            await EnsureEndpointRoutesAsync(adapters, routePreference);
            var probes = await Task.WhenAll(adapters.Select(x => _network.ProbeAsync(x, _settings.ProbeHost, protonActive, _stop.Token)));
            var decision = ChooseConnection(probes);
            if (_bonding is null && AutoCheck.IsChecked == true && decision.ActiveAdapterId is not null && decision.Changed)
            {
                await _network.ApplyMetricsAsync(adapters, decision.ActiveAdapterId, _settings.PreferredMetric, _settings.BackupMetric);
                await EnsureEndpointRoutesAsync(adapters, decision.ActiveAdapterId, force: true);
                _lastAppliedId = decision.ActiveAdapterId;
            }

            _rows.Clear();
            foreach (var adapter in adapters)
            {
                var probe = probes.First(x => x.AdapterId == adapter.Id);
                _rows.Add(AdapterRow.From(adapter, probe, decision.ActiveAdapterId == adapter.Id));
            }
            _bondingSamples = adapters.Select(adapter =>
            {
                var probe = probes.First(x => x.AdapterId == adapter.Id);
                return new BondingPathSample(
                    adapter.Name,
                    probe.Online,
                    probe.LatencyMs,
                    probe.JitterMs,
                    probe.PacketLossPercent,
                    adapter.Type == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet ? 20 : 30,
                    0,
                    Math.Clamp(1 - probe.PacketLossPercent / 100d, 0, 1));
            }).ToArray();
            ActiveText.Text = decision.ActiveAdapterId is null ? "" : $"Active: {adapters.FirstOrDefault(x => x.Id == decision.ActiveAdapterId)?.Name}";
            StatusText.Text = decision.Reason;
            VpnText.Text = ProtonModeCheck.IsChecked == true
                ? protonActive
                    ? _wireGuardEndpoint is not null
                        ? "WireGuard tunnel detected. Dual-path endpoint routing and protected internet probes are active."
                        : "WireGuard detected, but no prepared Proton config is registered. Deactivate it and use Prepare Proton config."
                    : "Proton-safe mode is enabled, but no active Proton/WireGuard tunnel was detected. Connect Proton before opening GTA."
                : "Proton-safe mode is off. Switching between router and hotspot will change GTA's public IP.";
            VpnText.Foreground = new SolidColorBrush(protonActive ? MediaColor.FromRgb(134, 239, 172) : MediaColor.FromRgb(253, 230, 138));
            StatusDot.Fill = new SolidColorBrush(probes.Any(x => x.Online) ? MediaColor.FromRgb(34, 197, 94) : MediaColor.FromRgb(239, 68, 68));
            if (protonActive && probes.Any(x => x.Online))
                StatusText.Text = $"{decision.Reason} — measuring each physical internet path while WireGuard is connected";
            AppLog.Write(decision.Reason);
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            StatusDot.Fill = MediaBrushes.Red;
            AppLog.Write(ex.ToString());
        }
    }

    private async void BondingToggle_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_bonding is not null)
            {
                await StopBondingAsync();
                return;
            }

            if (!IPAddress.TryParse(RelayAddressText.Text.Trim(), out var relay) || relay.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new InvalidOperationException("Enter a valid IPv4 relay address.");
            byte[] key;
            try { key = Convert.FromBase64String(RelayKeyBox.Password.Trim()); }
            catch (FormatException) { throw new InvalidOperationException("The relay key is not valid Base64."); }
            if (key.Length < 32) throw new InvalidOperationException("The relay key must contain 32 bytes.");

            var adapters = _network.GetInternetAdapters().Where(x => x.Address is not null && x.Gateway is not null).ToArray();
            if (adapters.Length < 2) throw new InvalidOperationException("Connect at least two Internet adapters: Ethernet, Wi-Fi hotspot, or USB tethering.");
            await _network.ApplyBondingEndpointRoutesAsync(adapters, relay);
            var paths = adapters.Select((adapter, index) => new BondingPathConfig(
                (byte)(index + 1), adapter.Name, adapter.Address!, adapter.InterfaceIndex));
            _bonding = new BondingEngine(paths, relay, 443, key, () => _bondingSamples);
            await _network.ConfigureBondingTunnelAsync();
            _bonding.Mode = SelectedBondingMode();
            _bonding.Start();
            BondingToggleButton.Content = "Stop bonding";
            StatusText.Text = "Bonding connected through the DualLink relay";
            VpnText.Text = $"Public traffic is routed through {relay}; both physical adapters are independently bound.";
            AppLog.Write($"Bonding started through {relay}");
        }
        catch (Exception ex)
        {
            if (_bonding is not null) await StopBondingAsync();
            MessageBox.Show(this, ex.Message, "Unable to start bonding", MessageBoxButton.OK, MessageBoxImage.Error);
            AppLog.Write($"Bonding start failed: {ex}");
        }
    }

    private BondingMode SelectedBondingMode() =>
        (BondingModeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() switch
        {
            "Failover" => BondingMode.Failover,
            "Redundant" => BondingMode.Redundant,
            _ => BondingMode.Bonding
        };

    private async Task StopBondingAsync()
    {
        await _network.RemoveBondingRoutesAsync();
        if (_bonding is not null) await _bonding.DisposeAsync();
        _bonding = null;
        BondingToggleButton.Content = "Start bonding";
        StatusText.Text = "Bonding stopped; existing failover monitoring remains active";
        AppLog.Write("Bonding stopped");
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
        choices.AddRange(adapters.Select(x => new AdapterChoice(x.Id, $"Prefer {x.Name} ({FriendlyType(x)})")));
        var existingIds = PreferredCombo.Items.Cast<AdapterChoice>().Select(x => x.Id).ToList();
        if (existingIds.SequenceEqual(choices.Select(x => x.Id))) return;
        _updatingChoices = true;
        PreferredCombo.ItemsSource = choices;
        PreferredCombo.SelectedItem = choices.FirstOrDefault(x => x.Id == _selectedPreferenceId) ?? choices[0];
        _updatingChoices = false;
    }

    private static string FriendlyType(AdapterInfo adapter)
    {
        var identity = $"{adapter.Name} {adapter.Description}";
        if (identity.Contains("USB", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("RNDIS", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("iPhone", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("Apple Mobile", StringComparison.OrdinalIgnoreCase)) return "USB tethering";
        return adapter.Type == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet";
    }

    private void PreferredCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingChoices || PreferredCombo.SelectedItem is not AdapterChoice choice) return;
        _selectedPreferenceId = choice.Id;
        _preferredRecoveryWins = 0;
        _controller = new FailoverController(_settings);
        AppLog.Write($"Preference changed to {choice.DisplayName}");
    }

    private async void ProbeNow_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void PrepareWireGuard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "WireGuard configuration (*.conf)|*.conf", Title = "Select a newly downloaded Proton WireGuard configuration" };
            if (dialog.ShowDialog(this) != true) return;
            var prepared = await _wireGuardConfig.PrepareAsync(dialog.FileName);
            _wireGuardEndpoint = prepared.Endpoint;
            var adapters = _network.GetInternetAdapters();
            var preferred = _selectedPreferenceId ?? adapters.FirstOrDefault(x => x.Type == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet)?.Id;
            await EnsureEndpointRoutesAsync(adapters, preferred, force: true);
            System.Windows.MessageBox.Show(this, $"Prepared safely for DualLink:\n\n{prepared.Path}\n\nImport this generated file into WireGuard. Do not import the original file. Keep both Ethernet and Wi-Fi connected before activating it.", "DualLink Proton configuration", MessageBoxButton.OK, MessageBoxImage.Information);
            StatusText.Text = "Proton endpoint routes prepared; import the generated -DualLink.conf file into WireGuard";
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(this, ex.Message, "Unable to prepare WireGuard configuration", MessageBoxButton.OK, MessageBoxImage.Error);
            AppLog.Write($"WireGuard preparation failed: {ex.Message}");
        }
    }

    private async Task EnsureEndpointRoutesAsync(IReadOnlyList<AdapterInfo> adapters, string? preferredId, bool force = false)
    {
        if (_wireGuardEndpoint is null) return;
        var signature = $"{_wireGuardEndpoint}|{preferredId}|{string.Join(',', adapters.Select(x => x.Id).Order())}";
        if (!force && signature == _endpointRouteSignature) return;
        await _network.ApplyWireGuardEndpointRoutesAsync(adapters, _wireGuardEndpoint, preferredId);
        _endpointRouteSignature = signature;
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        AutoCheck.IsChecked = false;
        await _network.RestoreManagedRoutesAsync(_network.GetInternetAdapters(), _wireGuardEndpoint);
        _endpointRouteSignature = null;
        _controller = new FailoverController(_settings);
        StatusText.Text = "Windows automatic metrics restored";
    }

    protected override void OnClosed(EventArgs e)
    {
        _stop.Cancel();
        if (_bonding is not null) StopBondingAsync().GetAwaiter().GetResult();
        base.OnClosed(e);
    }
}

public sealed record AdapterRow(string Name, string Type, string Address, string Latency, string Jitter, string Loss, string Score, string Role)
{
    public static AdapterRow From(AdapterInfo adapter, ProbeResult probe, bool active) => new(
        adapter.Name, FriendlyType(adapter), adapter.Address?.ToString() ?? "—",
        probe.Online ? $"{probe.LatencyMs:0} ms" : "Offline",
        probe.Online ? $"{probe.JitterMs:0} ms" : "—",
        $"{probe.PacketLossPercent:0}%", $"{probe.Score:0}", active ? "Preferred" : "Backup");

    private static string FriendlyType(AdapterInfo adapter)
    {
        var identity = $"{adapter.Name} {adapter.Description}";
        if (identity.Contains("USB", StringComparison.OrdinalIgnoreCase) || identity.Contains("RNDIS", StringComparison.OrdinalIgnoreCase) || identity.Contains("iPhone", StringComparison.OrdinalIgnoreCase)) return "USB tethering";
        return adapter.Type == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet";
    }
}

public sealed record AdapterChoice(string? Id, string DisplayName);
