using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Net;
using System.Net.NetworkInformation;
using System.Windows.Controls;
using System.Security.Cryptography;
using System.Windows.Shapes;
using System.Windows.Threading;
using DualLink.Core;
using MediaBrushes = System.Windows.Media.Brushes;
using MediaColor = System.Windows.Media.Color;
using WpfPoint = System.Windows.Point;

namespace DualLink.App;

public partial class MainWindow : Window
{
    private readonly ObservableCollection<AdapterRow> _rows = [];
    private readonly DualLinkSettings _settings = new();
    private readonly NetworkService _network = new();
    private readonly WireGuardConfigService _wireGuardConfig = new();
    private readonly ServerProvisioner _serverProvisioner = new();
    private readonly CancellationTokenSource _stop = new();
    private FailoverController _controller;
    private string? _selectedPreferenceId;
    private string? _lastAppliedId;
    private string? _wireGuardRoutedId;
    private bool _updatingChoices;
    private System.Net.IPAddress? _wireGuardEndpoint;
    private string? _endpointRouteSignature;
    private BondingEngine? _bonding;
    private CancellationTokenSource? _serverSetupCancellation;
    private IReadOnlyCollection<BondingPathSample> _bondingSamples = [];
    private string? _preferredBondingPathName;
    private Dictionary<string, AdapterByteCounters> _previousAdapterCounters = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _previousAdapterCountersAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastTunnelLatencyProbe = DateTimeOffset.MinValue;
    private double? _bondedInternetLatency;
    private readonly List<ConnectionHistorySample> _historySamples = [];
    private readonly List<ConnectionHistoryEvent> _historyEvents = [];
    private readonly ObservableCollection<HistoryEventRow> _historyEventRows = [];
    private readonly Dictionary<string, string> _historyState = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _outageStarted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _knownConnectionTypes = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastHistorySave = DateTimeOffset.MinValue;
    private readonly SemaphoreSlim _networkChanged = new(0, 1);
    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private readonly StablePathIdAllocator _bondingPathIds = new();
    private readonly PathHealthTracker _healthTracker = new();
    private readonly Dictionary<string, string> _lastAdapterAddresses = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastRecordedActiveId;
    private bool? _lastRecordedWireGuard;
    private bool? _lastRecordedBonding;
    private string _publicIp = "";
    private string _lastRecordedPublicIp = "";
    private DateTimeOffset _lastPublicIpProbe = DateTimeOffset.MinValue;
    private Task<string?>? _publicIpProbeTask;
    private DateTimeOffset _lastWireGuardRouteAudit = DateTimeOffset.MinValue;
    private CancellationTokenSource? _probeRound;
    private CancellationTokenSource? _tunnelVerificationCancellation;
    private Task<bool>? _tunnelVerification;
    private string? _tunnelVerificationIdentity;
    private DateTimeOffset _lastTunnelVerification;
    private readonly Dictionary<string, string> _adapterIdentities = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProbeResult> _latestGoodProbes = new(StringComparer.OrdinalIgnoreCase);
    private string? _lastMonitorMessage;
    private DateTimeOffset _lastUiUpdate;
    private bool _preferenceInitialized;
    private Task<IPAddress?>? _endpointObservation;
    private DateTimeOffset _lastEndpointObservation;
    private readonly TunnelRecoveryPolicy _tunnelRecovery = new();
    private readonly PathProbeScheduler _probeScheduler = new();
    private readonly Dictionary<string, ProbeResult> _stableProbes = new(StringComparer.OrdinalIgnoreCase);

    public MainWindow()
    {
        InitializeComponent();
        StartWithWindowsCheck.IsChecked = StartupService.IsEnabled();
        StartWithWindowsCheck.Checked += StartWithWindowsCheck_Changed;
        StartWithWindowsCheck.Unchecked += StartWithWindowsCheck_Changed;
        AdapterGrid.ItemsSource = _rows;
        HistoryEventGrid.ItemsSource = _historyEventRows;
        LoadConnectionHistory();
        _controller = new FailoverController(_settings);
        _wireGuardEndpoint = _wireGuardConfig.LoadEndpoint();
        if (MonitorSettingsStore.Load() is { } monitorSettings)
        {
            _selectedPreferenceId = monitorSettings.PreferredId;
            _preferenceInitialized = true;
            ResponseProfileCombo.SelectedItem = ResponseProfileCombo.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(x => x.Tag?.ToString() == monitorSettings.Response) ?? ResponseProfileCombo.SelectedItem;
            AutoCheck.IsChecked = monitorSettings.AutoRoutes;
            ProtonModeCheck.IsChecked = monitorSettings.ProtonSafe;
            BondingModeCombo.SelectedItem = BondingModeCombo.Items.Cast<ComboBoxItem>()
                .FirstOrDefault(x => x.Content?.ToString() == monitorSettings.BondingMode) ?? BondingModeCombo.SelectedItem;
        }
        AutoCheck.Checked += MonitorOptionChanged;
        AutoCheck.Unchecked += MonitorOptionChanged;
        ProtonModeCheck.Checked += MonitorOptionChanged;
        ProtonModeCheck.Unchecked += MonitorOptionChanged;
        BondingModeCombo.SelectionChanged += MonitorOptionChanged;
        if (BondingSettingsStore.Load() is { } saved)
        {
            RelayAddressText.Text = saved.RelayAddress;
            RelayKeyBox.Password = Convert.ToBase64String(saved.Key);
            SetServerState(saved.ServerReady ? $"Server: Ready — {saved.RelayAddress}" : $"Server: Configuration saved — {saved.RelayAddress}",
                saved.ServerReady ? MediaColor.FromRgb(74, 222, 128) : MediaColor.FromRgb(253, 230, 138));
        }
        Loaded += async (_, _) => await MonitorLoop();
        NetworkChange.NetworkAddressChanged += NetworkChanged;
        NetworkChange.NetworkAvailabilityChanged += NetworkAvailabilityChanged;
    }

    private void StartWithWindowsCheck_Changed(object sender, RoutedEventArgs e)
    {
        try
        {
            StartupService.SetEnabled(StartWithWindowsCheck.IsChecked == true);
            StatusText.Text = StartWithWindowsCheck.IsChecked == true
                ? "LinkWeaver will start minimized in the system tray when you sign in"
                : "Start with Windows is disabled";
        }
        catch (Exception ex)
        {
            StartWithWindowsCheck.Checked -= StartWithWindowsCheck_Changed;
            StartWithWindowsCheck.Unchecked -= StartWithWindowsCheck_Changed;
            StartWithWindowsCheck.IsChecked = StartupService.IsEnabled();
            StartWithWindowsCheck.Checked += StartWithWindowsCheck_Changed;
            StartWithWindowsCheck.Unchecked += StartWithWindowsCheck_Changed;
            System.Windows.MessageBox.Show(this, ex.Message, "Unable to change startup setting", MessageBoxButton.OK, MessageBoxImage.Error);
            AppLog.Write($"Startup setting failed: {ex}");
        }
    }

    private async void SetupServer_Click(object sender, RoutedEventArgs e)
    {
        if (_serverSetupCancellation is not null)
        {
            ServerSetupProgressText.Text = "Cancelling server setup…";
            ServerSetupButton.IsEnabled = false;
            _serverSetupCancellation.Cancel();
            return;
        }

        try
        {
            if (!IPAddress.TryParse(RelayAddressText.Text.Trim(), out var relay) || relay.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new InvalidOperationException("Enter a valid IPv4 relay address.");
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "AWS private key (*.pem)|*.pem|All files (*.*)|*.*", Title = "Select the AWS EC2 private key" };
            if (dialog.ShowDialog(this) != true) return;

            var key = RandomNumberGenerator.GetBytes(32);
            _serverSetupCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            SetServerState("Server: Setting up…", MediaColor.FromRgb(125, 211, 252));
            ServerSetupButton.Content = "Cancel setup";
            BondingToggleButton.IsEnabled = false;
            RelayAddressText.IsEnabled = false;
            ServerSetupProgressPanel.Visibility = Visibility.Visible;
            ServerSetupProgressText.Text = "Connecting securely to the relay…";
            System.Windows.Input.Mouse.OverrideCursor = System.Windows.Input.Cursors.Wait;
            var progress = new Progress<string>(message =>
            {
                ServerSetupProgressText.Text = message;
                StatusText.Text = message;
            });
            await _serverProvisioner.ProvisionAsync(relay, dialog.FileName, key, progress, _serverSetupCancellation.Token);
            RelayKeyBox.Password = Convert.ToBase64String(key);
            BondingSettingsStore.Save(relay.ToString(), key, serverReady: true);
            SetServerState($"Server: Ready — {relay}", MediaColor.FromRgb(74, 222, 128));
            StatusText.Text = "Relay installed and ready; press Start bonding";
            VpnText.Text = "The bonding key is encrypted for your Windows user with DPAPI.";
            AppLog.Write($"Relay provisioned at {relay}");
        }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested)
        {
            StatusText.Text = "Server setup cancelled. No bonding routes were changed.";
            SetServerState("Server: Setup cancelled", MediaColor.FromRgb(253, 230, 138));
            AppLog.Write("Relay setup cancelled by user.");
        }
        catch (Exception ex)
        {
            SetServerState("Server: Setup error", MediaColor.FromRgb(248, 113, 113));
            System.Windows.MessageBox.Show(this, ex.Message, "Unable to set up relay", MessageBoxButton.OK, MessageBoxImage.Error);
            AppLog.Write($"Relay setup failed: {ex}");
        }
        finally
        {
            _serverSetupCancellation?.Dispose();
            _serverSetupCancellation = null;
            ServerSetupButton.IsEnabled = true;
            ServerSetupButton.Content = "Setup server";
            BondingToggleButton.IsEnabled = true;
            RelayAddressText.IsEnabled = true;
            ServerSetupProgressPanel.Visibility = Visibility.Collapsed;
            System.Windows.Input.Mouse.OverrideCursor = null;
        }
    }

    private async Task MonitorLoop()
    {
        while (!_stop.IsCancellationRequested)
        {
            await RefreshAsync();
            try
            {
                using var interval = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
                interval.CancelAfter(TimeSpan.FromMilliseconds(_settings.ProbeIntervalMilliseconds));
                await _networkChanged.WaitAsync(interval.Token);
            }
            catch (OperationCanceledException) when (_stop.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { }
        }
    }

    private void NetworkChanged(object? sender, EventArgs e)
    {
        try { _probeRound?.Cancel(); } catch (ObjectDisposedException) { }
        WakeNetworkMonitor();
    }

    private void NetworkAvailabilityChanged(object? sender, NetworkAvailabilityEventArgs e) => NetworkChanged(sender, e);

    private void WakeNetworkMonitor()
    {
        try { _networkChanged.Release(); }
        catch (SemaphoreFullException) { }
    }

    private async Task RefreshAsync()
    {
        if (!await _refreshGate.WaitAsync(0)) return;
        try
        {
            using var round = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
            _probeRound = round;
            var adapters = _network.GetInternetAdapters();
            var identities = adapters.ToDictionary(x => x.Id, AdapterIdentity, StringComparer.OrdinalIgnoreCase);
            foreach (var removed in _adapterIdentities.Keys.Where(id => !identities.ContainsKey(id)).ToArray())
            {
                _healthTracker.Forget(removed);
                _tunnelRecovery.Forget(removed);
                _latestGoodProbes.Remove(removed);
                _stableProbes.Remove(removed);
                _adapterIdentities.Remove(removed);
            }
            foreach (var adapter in adapters)
            {
                if (_adapterIdentities.TryGetValue(adapter.Id, out var oldIdentity) && oldIdentity != identities[adapter.Id])
                {
                    _healthTracker.Forget(adapter.Id);
                    _tunnelRecovery.Forget(adapter.Id);
                    _latestGoodProbes.Remove(adapter.Id);
                    _stableProbes.Remove(adapter.Id);
                    if (adapter.Id == _lastAppliedId) _lastAppliedId = null;
                }
                _adapterIdentities[adapter.Id] = identities[adapter.Id];
            }
            if (!adapters.Any(x => x.Id == _lastAppliedId && NetworkService.IsUsable(x))) _lastAppliedId = null;
            var protonActive = ProtonModeCheck.IsChecked == true && _network.IsProtonTunnelActive();
            if (protonActive) ObserveWireGuardEndpoint();
            if (!protonActive)
            {
                _wireGuardRoutedId = null;
                CancelTunnelVerification();
            }
            else if (_wireGuardRoutedId is null && _wireGuardEndpoint is not null)
            {
                var index = await _network.GetPreferredRouteInterfaceAsync(_wireGuardEndpoint);
                _wireGuardRoutedId = adapters.FirstOrDefault(x => x.InterfaceIndex == index && NetworkService.IsUsable(x))?.Id;
            }
            var manage = _serverSetupCancellation is null && _bonding is null && AutoCheck.IsChecked == true;
            var failedCarryingPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (_bonding is not null)
            {
                var livePaths = adapters.Where(IsUsablePhysicalPath)
                    .Select(adapter => new BondingPathConfig(GetBondingPathId(adapter), adapter.Name, adapter.Address!, adapter.InterfaceIndex));
                await _bonding.UpdatePathsAsync(livePaths);
            }
            UpdateConnectionChoices(adapters);
            _preferredBondingPathName = adapters.FirstOrDefault(x => x.Id == _selectedPreferenceId)?.Name;

            // Definitive media loss does not wait behind probes. Reuse only a
            // recent successful standby result with the same address/gateway epoch.
            var carryingId = protonActive ? _wireGuardRoutedId : _lastAppliedId;
            if (manage && carryingId is not null && !adapters.Any(x => x.Id == carryingId && NetworkService.IsUsable(x)))
            {
                var standby = RecentStandby(adapters, carryingId, allowRejected: true);
                if (standby is not null) await SwitchPathAsync(standby, adapters, protonActive);
            }

            await _network.PrepareProbeRoutesAsync(adapters, protonActive);
            var response = SelectedResponseProfile();
            var completedProbes = _probeScheduler.Collect(identities).ToList();
            var probeTimeout = SelectedProbeTimeoutMilliseconds();
            var tasks = adapters.ToDictionary(x => x.Id, x => _probeScheduler.Start(x.Id, identities[x.Id], token =>
                _network.ProbeAsync(x, _settings.ProbeHost, protonActive, token, timeoutMilliseconds: probeTimeout), _stop.Token));
            carryingId = protonActive ? _wireGuardRoutedId : _lastAppliedId;
            // Check the carrying path first. A slow or broken standby must never
            // delay escape from the path carrying the game.
            if (manage && carryingId is not null && tasks.TryGetValue(carryingId, out var carryingTask))
            {
                var sample = await carryingTask.WaitAsync(round.Token);
                if (!sample.Online)
                {
                    failedCarryingPaths.Add(carryingId);
                    _lastAppliedId = null;
                    _latestGoodProbes.Remove(carryingId);
                    var standby = adapters.Where(x => x.Id != carryingId && NetworkService.IsUsable(x))
                        .Where(x => tasks[x.Id].IsCompletedSuccessfully && tasks[x.Id].Result.Online)
                        .OrderByDescending(x => tasks[x.Id].Result.Score).FirstOrDefault()
                        ?? RecentStandby(adapters, carryingId, allowRejected: true);
                    if (standby is not null) await SwitchPathAsync(standby, adapters, protonActive);
                }
            }
            else if (manage && carryingId is null && tasks.Count > 0)
            {
                var pending = tasks.Values.ToList();
                while (pending.Count > 0)
                {
                    var done = await Task.WhenAny(pending).WaitAsync(round.Token);
                    pending.Remove(done);
                    if ((await done).Online) break;
                }
            }
            completedProbes.AddRange(_probeScheduler.Collect(identities));
            var rawProbes = completedProbes.GroupBy(x => x.AdapterId, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.OrderByDescending(x => x.Timestamp).First()).ToArray();
            round.Token.ThrowIfCancellationRequested();
            // Reject results from a topology that changed while its sockets ran.
            var current = _network.GetInternetAdapters();
            if (current.Count != adapters.Count || current.Any(x => !identities.TryGetValue(x.Id, out var identity) || identity != AdapterIdentity(x)))
            {
                WakeNetworkMonitor();
                return;
            }
            foreach (var probe in rawProbes)
            {
                var adapter = adapters.First(x => x.Id == probe.AdapterId);
                if (probe.Online) _latestGoodProbes[probe.AdapterId] = probe;
                else _latestGoodProbes.Remove(probe.AdapterId);
                var failures = failedCarryingPaths.Contains(adapter.Id) || string.Equals(adapter.Id,
                    protonActive ? _wireGuardRoutedId : _lastAppliedId, StringComparison.OrdinalIgnoreCase) ? 1 : response.Failures;
                _stableProbes[probe.AdapterId] = _healthTracker.Update(probe, failures, response.Recoveries,
                    NetworkService.IsUsable(adapter), TimeSpan.FromSeconds(1));
            }
            var probes = adapters.Select(adapter => _stableProbes.TryGetValue(adapter.Id, out var sample) ? sample :
                new ProbeResult(adapter.Id, DateTimeOffset.UtcNow, false, 0, 0, 100, 0,
                    NetworkService.IsUsable(adapter) ? "Checking Internet reachability" : "Physical link unavailable")).ToArray();
            _controller.Synchronize(protonActive ? _wireGuardRoutedId : _lastAppliedId);
            var decision = ChooseConnection(protonActive ? probes.Where(x =>
                _tunnelRecovery.IsEligible(x.AdapterId, DateTimeOffset.UtcNow)).ToArray() : probes);
            if (manage && decision.ActiveAdapterId is not null)
            {
                var requested = adapters.First(x => x.Id == decision.ActiveAdapterId);
                var actual = protonActive ? _wireGuardRoutedId : _lastAppliedId;
                if (!string.Equals(actual, requested.Id, StringComparison.OrdinalIgnoreCase))
                {
                    await SwitchPathAsync(requested, adapters, protonActive);
                    decision = decision with { Reason = $"Routed to {requested.Name}; {(protonActive ? "verifying WireGuard without restarting it" : "Internet path verified")}" };
                }
            }
            if (manage && protonActive)
            {
                await AuditWireGuardEndpointRouteAsync(adapters);
                await PollTunnelVerificationAsync(adapters);
                if (_wireGuardRoutedId is not null && _lastAppliedId != _wireGuardRoutedId)
                    decision = decision with { Reason = $"WireGuard routed to {adapters.FirstOrDefault(x => x.Id == _wireGuardRoutedId)?.Name}; tunnel verification pending" };
            }
            var confirmedActiveId = _lastAppliedId;

            _rows.Clear();
            var traffic = BuildTrafficDisplays(adapters);
            var relayTelemetry = _bonding?.GetPathTelemetry().ToDictionary(x => x.PathId, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, BondingPathSample>(StringComparer.OrdinalIgnoreCase);
            var relayPreferred = relayTelemetry.Values.FirstOrDefault(x => x.Online && x.PathId == _preferredBondingPathName)
                ?? relayTelemetry.Values.Where(x => x.Online).OrderBy(x => x.SmoothedRttMs / 2 + x.JitterMs).FirstOrDefault();
            if (_bonding is not null && DateTimeOffset.UtcNow - _lastTunnelLatencyProbe >= TimeSpan.FromSeconds(2))
            {
                // This is display telemetry; never block route management on ICMP.
                _ = UpdateBondedLatencyAsync();
                _lastTunnelLatencyProbe = DateTimeOffset.UtcNow;
            }
            // Report the path that is actually carrying the WireGuard endpoint.
            // _lastAppliedId deliberately remains the last end-to-end verified path,
            // so using it for the UI marked disconnected Ethernet as active while
            // Wi-Fi was already routed and awaiting a fresh handshake.
            var routedActiveId = protonActive ? _wireGuardRoutedId : confirmedActiveId;
            if (!adapters.Any(x => x.Id == routedActiveId && NetworkService.IsUsable(x))) routedActiveId = null;
            foreach (var adapter in adapters)
            {
                var probe = probes.First(x => x.AdapterId == adapter.Id);
                traffic.TryGetValue(adapter.Id, out var pathTraffic);
                relayTelemetry.TryGetValue(adapter.Name, out var relaySample);
                var configuredPreferred = _selectedPreferenceId is null
                    ? string.Equals((_bonding is null ? routedActiveId : decision.ActiveAdapterId), adapter.Id, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(_selectedPreferenceId, adapter.Id, StringComparison.OrdinalIgnoreCase);
                var active = string.Equals((_bonding is null ? routedActiveId : decision.ActiveAdapterId), adapter.Id,
                    StringComparison.OrdinalIgnoreCase);
                if (_bonding is not null)
                    active = relaySample?.Online == true && (_bonding.Mode != BondingMode.Failover || relaySample.PathId == relayPreferred?.PathId);
                _rows.Add(AdapterRow.From(adapter, probe, configuredPreferred, active,
                    pathTraffic?.Upload ?? "—", pathTraffic?.Download ?? "—",
                    relaySample is { SmoothedRttMs: > 0 } ? $"{relaySample.SmoothedRttMs:0} ms" : "—", relaySample?.Online == true));
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
            UpdateRelayLatencyStatus(relayTelemetry.Values);
            UpdatePublicIpObservation();
            if (DateTimeOffset.UtcNow - _lastUiUpdate >= TimeSpan.FromSeconds(1))
            {
                RecordConnectionHistory(adapters, probes, traffic, routedActiveId, protonActive);
                _lastUiUpdate = DateTimeOffset.UtcNow;
            }
            if (_bonding is not null)
            {
                var verifiedPaths = relayTelemetry.Values.Where(x => x.Online).Select(x => x.PathId).ToArray();
                ActiveText.Text = verifiedPaths.Length == 0 ? "Tunnel: No verified path" : $"Tunnel: {string.Join(" + ", verifiedPaths)}";
            }
            else
            {
                if (routedActiveId is null)
                    ActiveText.Text = "Active: Unverified";
                else
                {
                    var routedName = adapters.FirstOrDefault(x => x.Id == routedActiveId)?.Name;
                    var verified = string.Equals(routedActiveId, _lastAppliedId, StringComparison.OrdinalIgnoreCase);
                    ActiveText.Text = $"Active: {routedName} · {(verified ? "verified" : "routed, verifying")}";
                }
            }
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
            if (_bonding is not null)
            {
                VpnText.Text = _bonding.Mode == BondingMode.Redundant
                    ? "Redundant relay tunnel: packets are copied over available paths with one stable VPS public IP."
                    : "Relay tunnel active with one stable VPS public IP. Redundant mode provides packet duplication for gaming continuity.";
                VpnText.Foreground = new SolidColorBrush(MediaColor.FromRgb(134, 239, 172));
                StatusDot.Fill = new SolidColorBrush(relayTelemetry.Values.Any(x => x.Online)
                    ? MediaColor.FromRgb(34, 197, 94) : MediaColor.FromRgb(239, 68, 68));
            }
            if (protonActive && _bonding is null && _lastAppliedId is null && probes.Any(x => x.Online))
                StatusDot.Fill = new SolidColorBrush(MediaColor.FromRgb(253, 230, 138));
            if (protonActive && probes.Any(x => x.Online))
                StatusText.Text = $"{decision.Reason} — measuring each physical internet path while WireGuard is connected";
            if (_lastMonitorMessage != decision.Reason)
            {
                AppLog.Write(decision.Reason);
                _lastMonitorMessage = decision.Reason;
            }
        }
        catch (OperationCanceledException) when (!_stop.IsCancellationRequested) { WakeNetworkMonitor(); }
        catch (OperationCanceledException) when (_stop.IsCancellationRequested) { }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            StatusDot.Fill = MediaBrushes.Red;
            AppLog.Write(ex.ToString());
        }
        finally
        {
            _probeRound = null;
            _refreshGate.Release();
        }
    }

    private static string AdapterIdentity(AdapterInfo x) => $"{x.Id}:{x.Status}:{x.InterfaceIndex}:{x.Address}:{x.Gateway}";

    private void ObserveWireGuardEndpoint()
    {
        if (_endpointObservation is { IsCompleted: true } completed)
        {
            if (completed.IsCompletedSuccessfully && completed.Result is { } endpoint && !endpoint.Equals(_wireGuardEndpoint))
            {
                AppLog.Write($"Observed active WireGuard endpoint {endpoint}; replacing stale prepared endpoint {_wireGuardEndpoint}.");
                _wireGuardEndpoint = endpoint;
                _wireGuardRoutedId = null;
                _lastAppliedId = null;
                _endpointRouteSignature = null;
                CancelTunnelVerification();
            }
            _endpointObservation = null;
        }
        if (_endpointObservation is null && DateTimeOffset.UtcNow - _lastEndpointObservation >= TimeSpan.FromSeconds(2))
        {
            _lastEndpointObservation = DateTimeOffset.UtcNow;
            _endpointObservation = _network.GetActiveWireGuardEndpointAsync(_stop.Token);
        }
    }

    private AdapterInfo? RecentStandby(IReadOnlyList<AdapterInfo> adapters, string excluded, bool allowRejected = false) =>
        adapters.Where(x => x.Id != excluded && NetworkService.IsUsable(x))
            .Where(x => allowRejected || _tunnelRecovery.IsEligible(x.Id, DateTimeOffset.UtcNow))
            .Where(x => _latestGoodProbes.TryGetValue(x.Id, out var probe) &&
                DateTimeOffset.UtcNow - probe.Timestamp.ToUniversalTime() < TimeSpan.FromSeconds(1))
            .OrderByDescending(x => _latestGoodProbes[x.Id].Score).FirstOrDefault();

    private async Task SwitchPathAsync(AdapterInfo selected, IReadOnlyList<AdapterInfo> adapters, bool wireGuard)
    {
        if (!NetworkService.IsUsable(selected)) return;
        if (wireGuard)
        {
            if (_wireGuardEndpoint is null)
                throw new InvalidOperationException("WireGuard is active but its endpoint is unknown. Prepare and import the Proton configuration first.");
            // Commit only a route Windows actually resolves through the target NIC.
            // Verification is observational and never rolls a route back to a dead NIC.
            if (!await _network.MoveWireGuardEndpointRouteAsync(adapters, _wireGuardEndpoint, selected))
                throw new InvalidOperationException($"Windows could not route the WireGuard endpoint through {selected.Name}.");
            _wireGuardRoutedId = selected.Id;
            _endpointRouteSignature = null;
            _lastAppliedId = null;
            CancelTunnelVerification();
            _lastTunnelVerification = DateTimeOffset.MinValue;
            AppLog.Write($"WireGuard endpoint routed to {selected.Name} (ifIndex {selected.InterfaceIndex}, gateway {selected.Gateway}); tunnel stays running.");
        }
        else
        {
            await _network.ApplyMetricsAsync(adapters, selected.Id, _settings.PreferredMetric, _settings.BackupMetric);
            _lastAppliedId = selected.Id;
        }
        _controller.Synchronize(selected.Id);
    }

    private void CancelTunnelVerification()
    {
        _tunnelVerificationCancellation?.Cancel();
        _tunnelVerificationCancellation?.Dispose();
        _tunnelVerificationCancellation = null;
        _tunnelVerification = null;
        _tunnelVerificationIdentity = null;
    }

    private async Task PollTunnelVerificationAsync(IReadOnlyList<AdapterInfo> adapters)
    {
        var routed = adapters.FirstOrDefault(x => x.Id == _wireGuardRoutedId && NetworkService.IsUsable(x));
        if (routed is null) { CancelTunnelVerification(); _lastAppliedId = null; return; }
        var identity = $"{_wireGuardEndpoint}|{AdapterIdentity(routed)}";
        if (_tunnelVerification is { IsCompleted: true } completed)
        {
            if (_tunnelVerificationIdentity == identity)
            {
                var verified = completed.IsCompletedSuccessfully && completed.Result;
                _lastAppliedId = verified && _latestGoodProbes.ContainsKey(routed.Id) ? routed.Id : null;
                if (_tunnelRecovery.Observe(routed.Id, identity, verified))
                {
                    var standby = RecentStandby(adapters, routed.Id);
                    if (standby is not null)
                    {
                        _tunnelRecovery.Reject(routed.Id, DateTimeOffset.UtcNow);
                        AppLog.Write($"WireGuard failed two independent tunnel verification rounds on {routed.Name}; trying {standby.Name} with a 15-second retry cooldown.");
                        await SwitchPathAsync(standby, adapters, wireGuard: true);
                        return;
                    }
                }
            }
            CancelTunnelVerification();
        }
        if (_tunnelVerification is not null || DateTimeOffset.UtcNow - _lastTunnelVerification < TimeSpan.FromMilliseconds(500)) return;
        _lastTunnelVerification = DateTimeOffset.UtcNow;
        _tunnelVerificationIdentity = identity;
        _tunnelVerificationCancellation = CancellationTokenSource.CreateLinkedTokenSource(_stop.Token);
        _tunnelVerification = _network.VerifyRoutedInternetAsync(_tunnelVerificationCancellation.Token);
    }

    private async Task UpdateBondedLatencyAsync()
    {
        try { _bondedInternetLatency = await _network.MeasureBondedInternetLatencyAsync(_stop.Token); }
        catch (OperationCanceledException) { }
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

            if (_network.IsProtonTunnelActive())
                throw new InvalidOperationException("Deactivate Proton/WireGuard before starting relay bonding. The relay provides its own stable public IP; combining both default tunnels would make route ownership ambiguous. Start the selected tunnel before joining GTA.");

            if (!IPAddress.TryParse(RelayAddressText.Text.Trim(), out var relay) || relay.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                throw new InvalidOperationException("Enter a valid IPv4 relay address.");
            byte[] key;
            try { key = Convert.FromBase64String(RelayKeyBox.Password.Trim()); }
            catch (FormatException) { throw new InvalidOperationException("The relay key is not valid Base64."); }
            if (key.Length < 32) throw new InvalidOperationException("The relay key must contain 32 bytes.");
            var serverReady = BondingSettingsStore.Load()?.ServerReady ?? false;
            BondingSettingsStore.Save(relay.ToString(), key, serverReady);
            SetBondingState("Bonding: Connecting to relay…", MediaColor.FromRgb(125, 211, 252));

            var adapters = _network.GetInternetAdapters().Where(IsUsablePhysicalPath).ToArray();
            if (adapters.Length < 1) throw new InvalidOperationException("Connect at least one Internet adapter: Ethernet, Wi-Fi, or USB tethering.");
            await _network.ApplyBondingEndpointRoutesAsync(adapters, relay);
            var paths = adapters.Select(adapter => new BondingPathConfig(
                GetBondingPathId(adapter), adapter.Name, adapter.Address!, adapter.InterfaceIndex));
            _preferredBondingPathName = adapters.FirstOrDefault(adapter =>
                string.Equals(adapter.Id, _selectedPreferenceId, StringComparison.OrdinalIgnoreCase))?.Name;
            _bonding = new BondingEngine(paths, relay, 443, key, () => _bondingSamples,
                () => _preferredBondingPathName);
            _bonding.Mode = SelectedBondingMode();
            StatusText.Text = "Testing encrypted relay connectivity on every physical path…";
            await _bonding.ConnectAsync(TimeSpan.FromSeconds(6), _stop.Token);
            await _network.ConfigureBondingTunnelAsync();
            _bonding.Start();
            await Task.Delay(750, _stop.Token);
            if (!await _network.VerifyBondedInternetAsync(_stop.Token))
                throw new InvalidOperationException("The relay handshake succeeded, but end-to-end Internet forwarding failed. LinkWeaver restored your normal routes.");
            BondingToggleButton.Content = "Stop bonding";
            SetBondingState($"Bonding: Established — {paths.Count()} path(s) via {relay}", MediaColor.FromRgb(74, 222, 128));
            StatusText.Text = "Bonding connected through the LinkWeaver relay";
            VpnText.Text = $"Public traffic is routed through {relay}; both physical adapters are independently bound.";
            AppLog.Write($"Bonding started through {relay}");
        }
        catch (Exception ex)
        {
            if (_bonding is not null) await StopBondingAsync();
            SetBondingState("Bonding: Error — normal routes restored", MediaColor.FromRgb(248, 113, 113));
            System.Windows.MessageBox.Show(this, ex.Message, "Unable to start bonding", MessageBoxButton.OK, MessageBoxImage.Error);
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
        if (IPAddress.TryParse(RelayAddressText.Text.Trim(), out var relay))
            await _network.RemoveBondingEndpointRoutesAsync(_network.GetInternetAdapters(), relay);
        BondingToggleButton.Content = "Start bonding";
        SetBondingState("Bonding: Stopped", MediaColor.FromRgb(203, 213, 225));
        _bondedInternetLatency = null;
        UpdateRelayLatencyStatus([]);
        StatusText.Text = "Bonding stopped; existing failover monitoring remains active";
        AppLog.Write("Bonding stopped");
    }

    private Dictionary<string, TrafficDisplay> BuildTrafficDisplays(IReadOnlyList<AdapterInfo> adapters)
    {
        var now = DateTimeOffset.UtcNow;
        var elapsed = Math.Max(0.001, (now - _previousAdapterCountersAt).TotalSeconds);
        var current = _network.GetAdapterByteCounters();
        var display = new Dictionary<string, TrafficDisplay>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in adapters)
        {
            if (!current.TryGetValue(adapter.Id, out var counter)) continue;
            _previousAdapterCounters.TryGetValue(adapter.Id, out var previous);
            var uploadRate = Math.Max(0, counter.BytesSent - (previous?.BytesSent ?? counter.BytesSent)) * 8d / elapsed / 1_000_000d;
            var downloadRate = Math.Max(0, counter.BytesReceived - (previous?.BytesReceived ?? counter.BytesReceived)) * 8d / elapsed / 1_000_000d;
            display[adapter.Id] = new(uploadRate, downloadRate,
                $"{uploadRate:0.00} Mbps · {FormatBytes(counter.BytesSent)}",
                $"{downloadRate:0.00} Mbps · {FormatBytes(counter.BytesReceived)}");
        }
        _previousAdapterCounters = current.ToDictionary(x => x.Key, x => x.Value, StringComparer.OrdinalIgnoreCase);
        _previousAdapterCountersAt = now;
        return display;
    }

    private static string FormatBytes(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB", "TB"];
        var value = (double)Math.Max(0, bytes);
        var unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return $"{value:0.##} {units[unit]}";
    }

    private void SetServerState(string text, MediaColor color)
    {
        ServerStateText.Text = text;
        ServerStateText.Foreground = new SolidColorBrush(color);
    }

    private void SetBondingState(string text, MediaColor color)
    {
        BondingStateText.Text = text;
        BondingStateText.Foreground = new SolidColorBrush(color);
    }

    private void UpdateRelayLatencyStatus(IEnumerable<BondingPathSample> samples)
    {
        if (_bonding is null)
        {
            RelayLatencyText.Text = "Relay latency: Not connected";
            RelayLatencyText.Foreground = new SolidColorBrush(MediaColor.FromRgb(203, 213, 225));
            return;
        }
        var measured = samples.Where(x => x.SmoothedRttMs > 0).OrderBy(x => x.SmoothedRttMs).ToArray();
        var pathText = measured.Select(x => $"{x.PathId} {x.SmoothedRttMs:0} ms");
        var internetText = _bondedInternetLatency is { } latency ? $"Internet {latency:0} ms" : "Internet measuring…";
        RelayLatencyText.Text = $"Relay: {string.Join(" | ", pathText.Append(internetText))}";
        var best = measured.Select(x => x.SmoothedRttMs).DefaultIfEmpty(999).Min();
        RelayLatencyText.Foreground = new SolidColorBrush(best < 80 ? MediaColor.FromRgb(74, 222, 128) : best < 140 ? MediaColor.FromRgb(253, 230, 138) : MediaColor.FromRgb(248, 113, 113));
    }

    private sealed record TrafficDisplay(double UploadMbps, double DownloadMbps, string Upload, string Download);

    private static bool IsUsablePhysicalPath(AdapterInfo adapter) =>
        adapter.Status == OperationalStatus.Up && adapter.Address is not null && adapter.Gateway is not null;

    private byte GetBondingPathId(AdapterInfo adapter) => _bondingPathIds.GetOrAdd(adapter.Id);

    private void UpdatePublicIpObservation()
    {
        var now = DateTimeOffset.UtcNow;
        if (_publicIpProbeTask is { IsCompleted: true })
        {
            try
            {
                var observed = _publicIpProbeTask.GetAwaiter().GetResult();
                if (!string.IsNullOrWhiteSpace(observed)) _publicIp = observed;
            }
            catch (OperationCanceledException) { }
            _publicIpProbeTask = null;
        }
        if (_publicIpProbeTask is null && now - _lastPublicIpProbe >= TimeSpan.FromSeconds(5))
        {
            _lastPublicIpProbe = now;
            _publicIpProbeTask = _network.GetPublicIpAsync(_stop.Token);
        }
    }

    private void LoadConnectionHistory()
    {
        var data = ConnectionHistoryStore.Load();
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        _historySamples.AddRange(data.Samples.Where(x => x.Timestamp >= cutoff));
        _historyEvents.AddRange(data.Events.Where(x => x.Timestamp >= cutoff));
        foreach (var sample in _historySamples)
            _knownConnectionTypes[sample.Connection] = sample.Type;
        foreach (var latest in _historySamples.GroupBy(x => x.Connection, StringComparer.OrdinalIgnoreCase)
                     .Select(group => group.OrderByDescending(x => x.Timestamp).First()))
            _historyState[latest.Connection] = string.IsNullOrWhiteSpace(latest.State)
                ? (latest.Online ? "Online" : "Unknown")
                : latest.State;
        RebuildHistoryEventRows();
    }

    private void RecordConnectionHistory(IReadOnlyList<AdapterInfo> adapters, IReadOnlyCollection<ProbeResult> probes,
        IReadOnlyDictionary<string, TrafficDisplay> traffic, string? activeAdapterId, bool wireGuardActive)
    {
        var now = DateTimeOffset.UtcNow;
        var current = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var adapter in adapters)
        {
            current.Add(adapter.Name);
            var type = FriendlyType(adapter);
            _knownConnectionTypes[adapter.Name] = type;
            var probe = probes.First(x => x.AdapterId == adapter.Id);
            traffic.TryGetValue(adapter.Id, out var rates);
            var state = ClassifyConnectivity(adapter, probe);
            AddHistorySample(now, adapter.Name, type, state, probe.Online, probe.Score, probe.LatencyMs,
                rates?.DownloadMbps ?? 0, rates?.UploadMbps ?? 0, adapter, probe,
                string.Equals(adapter.Id, activeAdapterId, StringComparison.OrdinalIgnoreCase), wireGuardActive);
            var address = adapter.Address?.ToString() ?? "";
            if (_lastAdapterAddresses.TryGetValue(adapter.Id, out var previousAddress) && previousAddress != address)
                _historyEvents.Add(new(now, adapter.Name, "Local IPv4 changed", null,
                    $"{(string.IsNullOrWhiteSpace(previousAddress) ? "none" : previousAddress)} → {(string.IsNullOrWhiteSpace(address) ? "none" : address)}"));
            _lastAdapterAddresses[adapter.Id] = address;
        }

        foreach (var known in _knownConnectionTypes.Where(x => !current.Contains(x.Key)).ToArray())
            AddHistorySample(now, known.Key, known.Value, "Adapter unavailable", false, 0, 0, 0, 0,
                null, null, false, wireGuardActive);

        if (_lastRecordedActiveId != activeAdapterId)
        {
            var activeName = adapters.FirstOrDefault(x => x.Id == activeAdapterId)?.Name ?? "none";
            var previousName = adapters.FirstOrDefault(x => x.Id == _lastRecordedActiveId)?.Name ?? _lastRecordedActiveId ?? "none";
            _historyEvents.Add(new(now, "LinkWeaver", "Active path changed", null, $"{previousName} → {activeName}"));
            _lastRecordedActiveId = activeAdapterId;
        }
        if (_lastRecordedWireGuard is not null && _lastRecordedWireGuard != wireGuardActive)
            _historyEvents.Add(new(now, "LinkWeaver", wireGuardActive ? "WireGuard detected" : "WireGuard not detected", null));
        _lastRecordedWireGuard = wireGuardActive;
        var bondingActive = _bonding is not null;
        if (_lastRecordedBonding is not null && _lastRecordedBonding != bondingActive)
            _historyEvents.Add(new(now, "LinkWeaver", bondingActive ? "Bonding active" : "Bonding stopped", null));
        _lastRecordedBonding = bondingActive;
        if (!string.IsNullOrWhiteSpace(_publicIp) && !string.IsNullOrWhiteSpace(_lastRecordedPublicIp) && _publicIp != _lastRecordedPublicIp)
            _historyEvents.Add(new(now, "LinkWeaver", "Public IP changed", null, $"{_lastRecordedPublicIp} → {_publicIp}"));
        if (!string.IsNullOrWhiteSpace(_publicIp)) _lastRecordedPublicIp = _publicIp;

        var cutoff = now.AddHours(-24);
        _historySamples.RemoveAll(x => x.Timestamp < cutoff);
        _historyEvents.RemoveAll(x => x.Timestamp < cutoff);
        RebuildHistoryEventRows();
        DrawHistoryChart();
        if (now - _lastHistorySave >= TimeSpan.FromSeconds(10))
        {
            ConnectionHistoryStore.Save(new(_historySamples, _historyEvents));
            _lastHistorySave = now;
        }
    }

    private void AddHistorySample(DateTimeOffset now, string connection, string type, string state, bool online,
        double quality, double latency, double downloadMbps, double uploadMbps, AdapterInfo? adapter,
        ProbeResult? probe, bool activePath, bool wireGuardActive)
    {
        var linkUp = adapter?.Status == OperationalStatus.Up;
        _historySamples.Add(new(now, connection, type, online, online ? quality : 0, online ? latency : 0,
            downloadMbps, uploadMbps, state, linkUp, adapter?.Address is not null, adapter?.Gateway is not null,
            adapter?.Address?.ToString() ?? "", adapter?.Gateway?.ToString() ?? "", probe?.Error ?? "",
            activePath, wireGuardActive, _bonding is not null, _publicIp));
        if (_historyState.TryGetValue(connection, out var previous) && previous != state && previous != "Unknown")
        {
            if (state == "Online")
            {
                var duration = _outageStarted.Remove(connection, out var started) ? (now - started).TotalSeconds : (double?)null;
                _historyEvents.Add(new(now, connection, $"Recovered: {state}", duration, $"Previous state: {previous}"));
            }
            else
            {
                if (!online && !_outageStarted.ContainsKey(connection)) _outageStarted[connection] = now;
                _historyEvents.Add(new(now, connection, state, null,
                    $"Previous state: {previous}; {probe?.Error ?? "no probe detail"}"));
            }
        }
        _historyState[connection] = state;
    }

    private static string ClassifyConnectivity(AdapterInfo adapter, ProbeResult probe)
    {
        if (adapter.Status != OperationalStatus.Up) return "Physical link disconnected";
        if (adapter.Address is null) return "Link up — no IPv4 address";
        if (adapter.Gateway is null) return "Link up — no gateway";
        if (!probe.Online) return "Link up — Internet unreachable";
        if (probe.PacketLossPercent >= 50 || probe.Score < 40) return "Internet degraded";
        return "Online";
    }

    private void RebuildHistoryEventRows()
    {
        _historyEventRows.Clear();
        foreach (var item in _historyEvents.OrderByDescending(x => x.Timestamp).Take(250))
            _historyEventRows.Add(new(item.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), item.Connection,
                item.Event, item.DurationSeconds is { } seconds ? FormatDuration(seconds) : "—", item.Details));
    }

    private static string FormatDuration(double seconds) => seconds < 60 ? $"{seconds:0} sec" : $"{TimeSpan.FromSeconds(seconds):m\\:ss}";

    private void DrawHistoryChart()
    {
        if (HistoryCanvas is null) return;
        HistoryCanvas.Children.Clear();
        var width = HistoryCanvas.ActualWidth;
        var height = HistoryCanvas.ActualHeight;
        if (width < 80 || height < 80) return;
        var end = DateTimeOffset.UtcNow;
        var rangeMinutes = (HistoryRangeCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "30" => 30,
            "60" => 60,
            _ => 15
        };
        var metric = (HistoryMetricCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Quality";
        var start = end.AddMinutes(-rangeMinutes);
        var samples = _historySamples.Where(x => x.Timestamp >= start).ToArray();
        HistoryEmptyText.Visibility = samples.Length == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (samples.Length == 0) return;

        const double left = 42, top = 18, right = 14, bottom = 30;
        var plotWidth = width - left - right;
        var plotHeight = height - top - bottom;
        Func<ConnectionHistorySample, double> selector = metric switch
        {
            "Ping" => sample => sample.Online ? sample.LatencyMs : 0,
            "Download" => sample => sample.DownloadMbps,
            "Upload" => sample => sample.UploadMbps,
            _ => sample => sample.Quality
        };
        var maximum = metric == "Quality" ? 100d : NiceMaximum(samples.Where(x => x.Online).Select(selector).DefaultIfEmpty(0).Max());
        HistoryChartTitle.Text = metric switch
        {
            "Ping" => "Ping history",
            "Download" => "Download history",
            "Upload" => "Upload history",
            _ => "Connection quality"
        };
        HistoryChartDescription.Text = metric switch
        {
            "Ping" => "Round-trip latency per adapter. Lower is better.",
            "Download" => "Receive traffic per physical adapter, including direct mode.",
            "Upload" => "Transmit traffic per physical adapter, including direct mode.",
            _ => "Health score: 100 is excellent and 0 is unusable; based on ping, jitter, loss, and reliability."
        };
        HistoryAxisText.Text = metric switch { "Ping" => "ms", "Download" or "Upload" => "Mbps", _ => "Score (0–100)" };
        for (var tick = 0; tick <= 4; tick++)
        {
            var value = maximum * tick / 4d;
            var y = top + plotHeight * (1 - value / maximum);
            HistoryCanvas.Children.Add(new Line { X1 = left, X2 = left + plotWidth, Y1 = y, Y2 = y, Stroke = new SolidColorBrush(MediaColor.FromRgb(51, 65, 85)), StrokeThickness = 1 });
            var label = new TextBlock { Text = value.ToString(maximum <= 10 ? "0.0" : "0"), Foreground = new SolidColorBrush(MediaColor.FromRgb(148, 163, 184)), FontSize = 11 };
            Canvas.SetLeft(label, 5); Canvas.SetTop(label, y - 8); HistoryCanvas.Children.Add(label);
        }

        MediaColor[] colors = [MediaColor.FromRgb(56, 189, 248), MediaColor.FromRgb(74, 222, 128), MediaColor.FromRgb(250, 204, 21), MediaColor.FromRgb(192, 132, 252)];
        var colorIndex = 0;
        foreach (var group in samples.GroupBy(x => x.Connection).OrderBy(x => x.Key))
        {
            var color = colors[colorIndex++ % colors.Length];
            var points = new System.Windows.Media.PointCollection();
            foreach (var sample in group.OrderBy(x => x.Timestamp))
                points.Add(new WpfPoint(
                    left + plotWidth * Math.Clamp((sample.Timestamp - start).TotalSeconds / (end - start).TotalSeconds, 0, 1),
                    top + plotHeight * (1 - Math.Clamp(selector(sample), 0, maximum) / maximum)));
            HistoryCanvas.Children.Add(new Polyline { Points = points, Stroke = new SolidColorBrush(color), StrokeThickness = 2 });
            var legend = new TextBlock { Text = group.Key, Foreground = new SolidColorBrush(color), FontWeight = FontWeights.SemiBold, FontSize = 12 };
            Canvas.SetLeft(legend, left + (colorIndex - 1) * 130); Canvas.SetTop(legend, height - 22); HistoryCanvas.Children.Add(legend);
        }

        foreach (var drop in _historyEvents.Where(x => x.Timestamp >= start && x.Event is
                     "Physical link disconnected" or "Link up — no IPv4 address" or
                     "Link up — no gateway" or "Link up — Internet unreachable" or "Adapter unavailable"))
        {
            var x = left + plotWidth * Math.Clamp((drop.Timestamp - start).TotalSeconds / (end - start).TotalSeconds, 0, 1);
            HistoryCanvas.Children.Add(new Line { X1 = x, X2 = x, Y1 = top, Y2 = top + plotHeight, Stroke = MediaBrushes.Red, StrokeThickness = 2, StrokeDashArray = new DoubleCollection([4, 3]), ToolTip = $"{drop.Connection} dropped at {drop.Timestamp.ToLocalTime():HH:mm:ss}" });
        }
    }

    private static double NiceMaximum(double value)
    {
        if (value <= 0) return 1;
        var magnitude = Math.Pow(10, Math.Floor(Math.Log10(value)));
        var normalized = value / magnitude;
        var nice = normalized <= 1 ? 1 : normalized <= 2 ? 2 : normalized <= 5 ? 5 : 10;
        return nice * magnitude;
    }

    private void HistoryCanvas_SizeChanged(object sender, SizeChangedEventArgs e) => DrawHistoryChart();

    private void HistoryChartSelectionChanged(object sender, SelectionChangedEventArgs e) => DrawHistoryChart();

    private void ExportHistory_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "CSV data (*.csv)|*.csv",
            FileName = $"LinkWeaver-history-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            Title = "Export LinkWeaver connection history"
        };
        if (dialog.ShowDialog(this) != true) return;
        ConnectionHistoryStore.ExportCsv(dialog.FileName, _historySamples, _historyEvents);
        StatusText.Text = $"History exported to {System.IO.Path.GetFileName(dialog.FileName)}";
    }

    private void ClearHistory_Click(object sender, RoutedEventArgs e)
    {
        _historySamples.Clear();
        _historyEvents.Clear();
        _historyState.Clear();
        _outageStarted.Clear();
        _knownConnectionTypes.Clear();
        RebuildHistoryEventRows();
        DrawHistoryChart();
        ConnectionHistoryStore.Save(new([], []));
    }

    private FailoverDecision ChooseConnection(IReadOnlyCollection<ProbeResult> probes)
    {
        return _controller.Evaluate(probes, _selectedPreferenceId);
    }

    private void UpdateConnectionChoices(IReadOnlyList<AdapterInfo> adapters)
    {
        if (!_preferenceInitialized && adapters.Count > 0)
        {
            _selectedPreferenceId = adapters.FirstOrDefault(x => x.Type == NetworkInterfaceType.Ethernet)?.Id;
            _preferenceInitialized = true;
        }
        var choices = new List<AdapterChoice> { new(null, "Automatic — best quality") };
        choices.AddRange(adapters.Select(x => new AdapterChoice(x.Id, $"Prefer {x.Name} ({FriendlyType(x)})")));
        if (_selectedPreferenceId is not null && choices.All(x => x.Id != _selectedPreferenceId))
            choices.Add(new(_selectedPreferenceId, "Preferred adapter (currently unavailable)"));
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
        if (identity.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) ||
            identity.Contains("Personal Area Network", StringComparison.OrdinalIgnoreCase) ||
            adapter.Type == NetworkInterfaceType.Ppp) return "Bluetooth/PPP tethering";
        return adapter.Type == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet";
    }

    private void PreferredCombo_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
    {
        if (_updatingChoices || PreferredCombo.SelectedItem is not AdapterChoice choice) return;
        _selectedPreferenceId = choice.Id;
        _controller = new FailoverController(_settings);
        WakeNetworkMonitor();
        AppLog.Write($"Preference changed to {choice.DisplayName}");
        SaveMonitorSettings();
    }

    private (int Failures, int Recoveries) SelectedResponseProfile() =>
        (ResponseProfileCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "Aggressive" => (2, 2),
            "Fast" => (2, 3),
            "Stable" => (3, 5),
            _ => (2, 4)
        };

    private int SelectedProbeTimeoutMilliseconds() =>
        (ResponseProfileCombo?.SelectedItem as ComboBoxItem)?.Tag?.ToString() switch
        {
            "Aggressive" => 250,
            "Fast" => 300,
            "Stable" => 500,
            _ => 350
        };

    private async Task AuditWireGuardEndpointRouteAsync(IReadOnlyList<AdapterInfo> adapters)
    {
        if (_wireGuardEndpoint is null || _wireGuardRoutedId is null ||
            DateTimeOffset.UtcNow - _lastWireGuardRouteAudit < TimeSpan.FromSeconds(2))
            return;

        _lastWireGuardRouteAudit = DateTimeOffset.UtcNow;
        var expected = adapters.FirstOrDefault(x => string.Equals(
            x.Id, _wireGuardRoutedId, StringComparison.OrdinalIgnoreCase));
        if (expected is null || !NetworkService.IsUsable(expected)) return;
        if (await _network.GetPreferredRouteInterfaceAsync(_wireGuardEndpoint) == expected.InterfaceIndex)
            return;

        AppLog.Write($"WireGuard endpoint route drift detected; restoring {expected.Name} without restarting the tunnel.");
        if (await _network.MoveWireGuardEndpointRouteAsync(adapters, _wireGuardEndpoint, expected))
        {
            _endpointRouteSignature = null;
            _lastAppliedId = null;
            CancelTunnelVerification();
        }
    }

    private void ResponseProfile_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        _healthTracker.Reset();
        WakeNetworkMonitor();
        if (!IsLoaded) return;
        var profile = (ResponseProfileCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Balanced";
        StatusText.Text = $"Response profile changed to {profile}";
        AppLog.Write($"Response profile changed to {profile}");
        SaveMonitorSettings();
    }

    private void MonitorOptionChanged(object sender, RoutedEventArgs e)
    {
        if (!IsLoaded) return;
        if (_bonding is not null) _bonding.Mode = SelectedBondingMode();
        SaveMonitorSettings();
        WakeNetworkMonitor();
    }

    private void SaveMonitorSettings() => MonitorSettingsStore.Save(new(_selectedPreferenceId,
        (ResponseProfileCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "Balanced",
        AutoCheck.IsChecked == true, ProtonModeCheck.IsChecked == true,
        (BondingModeCombo.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "Bonding"));

    private async void ProbeNow_Click(object sender, RoutedEventArgs e) => await RefreshAsync();
    private async void PrepareWireGuard_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_network.IsProtonTunnelActive())
                throw new InvalidOperationException("Deactivate the active WireGuard tunnel before preparing a configuration, then import the generated file before joining GTA.");
            var dialog = new Microsoft.Win32.OpenFileDialog { Filter = "WireGuard configuration (*.conf)|*.conf", Title = "Select a newly downloaded Proton WireGuard configuration" };
            if (dialog.ShowDialog(this) != true) return;
            var prepared = await _wireGuardConfig.PrepareAsync(dialog.FileName);
            _wireGuardEndpoint = prepared.Endpoint;
            var adapters = _network.GetInternetAdapters();
            var preferred = _selectedPreferenceId ?? adapters.FirstOrDefault(x => x.Type == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet)?.Id;
            await EnsureEndpointRoutesAsync(adapters, preferred, force: true);
            System.Windows.MessageBox.Show(this, $"Prepared safely for LinkWeaver:\n\n{prepared.Path}\n\nImport this generated file into WireGuard. Do not import the original file. Keep both Ethernet and Wi-Fi connected before activating it.", "LinkWeaver Proton configuration", MessageBoxButton.OK, MessageBoxImage.Information);
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
        var signature = $"{_wireGuardEndpoint}|{preferredId}|{string.Join(',', adapters.Select(AdapterIdentity).Order())}";
        if (!force && signature == _endpointRouteSignature) return;
        await _network.ApplyWireGuardEndpointRoutesAsync(adapters, _wireGuardEndpoint, preferredId);
        var index = await _network.GetPreferredRouteInterfaceAsync(_wireGuardEndpoint);
        _wireGuardRoutedId = adapters.FirstOrDefault(x => x.InterfaceIndex == index && NetworkService.IsUsable(x))?.Id;
        _endpointRouteSignature = signature;
    }
    private async void Restore_Click(object sender, RoutedEventArgs e)
    {
        AutoCheck.IsChecked = false;
        await _network.RestoreManagedRoutesAsync(_network.GetInternetAdapters(), _wireGuardEndpoint);
        _endpointRouteSignature = null;
        _wireGuardRoutedId = null;
        _controller = new FailoverController(_settings);
        StatusText.Text = "Windows automatic metrics restored";
    }

    protected override async void OnClosed(EventArgs e)
    {
        NetworkChange.NetworkAddressChanged -= NetworkChanged;
        NetworkChange.NetworkAvailabilityChanged -= NetworkAvailabilityChanged;
        _stop.Cancel();
        _probeScheduler.Dispose();
        SaveMonitorSettings();
        CancelTunnelVerification();
        ConnectionHistoryStore.Save(new(_historySamples, _historyEvents));
        if (_bonding is not null) await StopBondingAsync();
        base.OnClosed(e);
    }
}

public sealed record AdapterRow(string Name, string Type, string Address, string Latency, string RelayLatency, string Jitter, string Loss, string Score, string Upload, string Download, string Role)
{
    public static AdapterRow From(AdapterInfo adapter, ProbeResult probe, bool preferred, bool active,
        string upload = "—", string download = "—", string relayLatency = "—", bool relayVerified = false) => new(
        adapter.Name, FriendlyType(adapter), adapter.Address?.ToString() ?? "—",
        probe.Online ? $"{probe.LatencyMs:0} ms" : relayVerified ? "Relay verified" : probe.Error?.StartsWith("Internet recovery confirmation") == true ? "Confirming recovery" : OfflineLabel(adapter),
        relayLatency,
        probe.Online ? $"{probe.JitterMs:0} ms" : "—",
        $"{probe.PacketLossPercent:0}%", $"{probe.Score:0}", upload, download,
        preferred ? active ? "Preferred · active" : "Preferred" : active ? "Backup · active" : "Backup");

    private static string OfflineLabel(AdapterInfo adapter)
    {
        if (adapter.Status != OperationalStatus.Up) return "Link disconnected";
        if (adapter.Address is null) return "No IPv4";
        if (adapter.Gateway is null) return "No gateway";
        return "No Internet";
    }

    private static string FriendlyType(AdapterInfo adapter)
    {
        var identity = $"{adapter.Name} {adapter.Description}";
        if (identity.Contains("USB", StringComparison.OrdinalIgnoreCase) || identity.Contains("RNDIS", StringComparison.OrdinalIgnoreCase) || identity.Contains("iPhone", StringComparison.OrdinalIgnoreCase)) return "USB tethering";
        if (identity.Contains("Bluetooth", StringComparison.OrdinalIgnoreCase) || identity.Contains("Personal Area Network", StringComparison.OrdinalIgnoreCase) || adapter.Type == NetworkInterfaceType.Ppp) return "Bluetooth/PPP tethering";
        return adapter.Type == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet";
    }
}

public sealed record AdapterChoice(string? Id, string DisplayName);
public sealed record HistoryEventRow(string Time, string Connection, string Event, string Duration, string Details);
