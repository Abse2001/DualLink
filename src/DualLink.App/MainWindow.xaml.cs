using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Media;
using System.Net;
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
    private int _preferredRecoveryWins;
    private bool _updatingChoices;
    private System.Net.IPAddress? _wireGuardEndpoint;
    private string? _endpointRouteSignature;
    private BondingEngine? _bonding;
    private CancellationTokenSource? _serverSetupCancellation;
    private IReadOnlyCollection<BondingPathSample> _bondingSamples = [];
    private Dictionary<string, AdapterByteCounters> _previousAdapterCounters = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _previousAdapterCountersAt = DateTimeOffset.UtcNow;
    private DateTimeOffset _lastTunnelLatencyProbe = DateTimeOffset.MinValue;
    private double? _bondedInternetLatency;
    private readonly List<ConnectionHistorySample> _historySamples = [];
    private readonly List<ConnectionHistoryEvent> _historyEvents = [];
    private readonly ObservableCollection<HistoryEventRow> _historyEventRows = [];
    private readonly Dictionary<string, bool> _historyState = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> _outageStarted = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> _knownConnectionTypes = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _lastHistorySave = DateTimeOffset.MinValue;

    public MainWindow()
    {
        InitializeComponent();
        AdapterGrid.ItemsSource = _rows;
        HistoryEventGrid.ItemsSource = _historyEventRows;
        LoadConnectionHistory();
        _controller = new FailoverController(_settings);
        _wireGuardEndpoint = _wireGuardConfig.LoadEndpoint();
        if (BondingSettingsStore.Load() is { } saved)
        {
            RelayAddressText.Text = saved.RelayAddress;
            RelayKeyBox.Password = Convert.ToBase64String(saved.Key);
            SetServerState(saved.ServerReady ? $"Server: Ready — {saved.RelayAddress}" : $"Server: Configuration saved — {saved.RelayAddress}",
                saved.ServerReady ? MediaColor.FromRgb(74, 222, 128) : MediaColor.FromRgb(253, 230, 138));
        }
        Loaded += async (_, _) => await MonitorLoop();
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
            try { await Task.Delay(TimeSpan.FromSeconds(_settings.ProbeIntervalSeconds), _stop.Token); }
            catch (OperationCanceledException) { break; }
        }
    }

    private async Task RefreshAsync()
    {
        try
        {
            var adapters = _network.GetInternetAdapters();
            if (_bonding is not null)
            {
                var livePaths = adapters.Where(adapter => adapter.Address is not null && adapter.Gateway is not null)
                    .Select((adapter, index) => new BondingPathConfig((byte)(index + 1), adapter.Name, adapter.Address!, adapter.InterfaceIndex));
                await _bonding.UpdatePathsAsync(livePaths);
            }
            UpdateConnectionChoices(adapters);
            var protonActive = ProtonModeCheck.IsChecked == true && _network.IsProtonTunnelActive();
            var routePreference = _lastAppliedId ?? _selectedPreferenceId ?? adapters.FirstOrDefault(x => x.Type == System.Net.NetworkInformation.NetworkInterfaceType.Ethernet)?.Id;
            if (_serverSetupCancellation is null)
                await EnsureEndpointRoutesAsync(adapters, routePreference);
            var probes = await Task.WhenAll(adapters.Select(x => _network.ProbeAsync(x, _settings.ProbeHost, protonActive, _stop.Token)));
            var decision = ChooseConnection(probes);
            if (_serverSetupCancellation is null && _bonding is null && AutoCheck.IsChecked == true && decision.ActiveAdapterId is not null && decision.Changed)
            {
                await _network.ApplyMetricsAsync(adapters, decision.ActiveAdapterId, _settings.PreferredMetric, _settings.BackupMetric);
                await EnsureEndpointRoutesAsync(adapters, decision.ActiveAdapterId, force: true);
                _lastAppliedId = decision.ActiveAdapterId;
            }

            _rows.Clear();
            var traffic = BuildTrafficDisplays(adapters);
            var relayTelemetry = _bonding?.GetPathTelemetry().ToDictionary(x => x.PathId, StringComparer.OrdinalIgnoreCase)
                ?? new Dictionary<string, BondingPathSample>(StringComparer.OrdinalIgnoreCase);
            if (_bonding is not null && DateTimeOffset.UtcNow - _lastTunnelLatencyProbe >= TimeSpan.FromSeconds(2))
            {
                _bondedInternetLatency = await _network.MeasureBondedInternetLatencyAsync(_stop.Token);
                _lastTunnelLatencyProbe = DateTimeOffset.UtcNow;
            }
            foreach (var adapter in adapters)
            {
                var probe = probes.First(x => x.AdapterId == adapter.Id);
                traffic.TryGetValue(adapter.Id, out var pathTraffic);
                relayTelemetry.TryGetValue(adapter.Name, out var relaySample);
                _rows.Add(AdapterRow.From(adapter, probe, decision.ActiveAdapterId == adapter.Id,
                    pathTraffic?.Upload ?? "—", pathTraffic?.Download ?? "—",
                    relaySample is { SmoothedRttMs: > 0 } ? $"{relaySample.SmoothedRttMs:0} ms" : "—"));
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
            RecordConnectionHistory(adapters, probes, traffic);
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
            var serverReady = BondingSettingsStore.Load()?.ServerReady ?? false;
            BondingSettingsStore.Save(relay.ToString(), key, serverReady);
            SetBondingState("Bonding: Connecting to relay…", MediaColor.FromRgb(125, 211, 252));

            var adapters = _network.GetInternetAdapters().Where(x => x.Address is not null && x.Gateway is not null).ToArray();
            if (adapters.Length < 1) throw new InvalidOperationException("Connect at least one Internet adapter: Ethernet, Wi-Fi, or USB tethering.");
            await _network.ApplyBondingEndpointRoutesAsync(adapters, relay);
            var paths = adapters.Select((adapter, index) => new BondingPathConfig(
                (byte)(index + 1), adapter.Name, adapter.Address!, adapter.InterfaceIndex));
            _bonding = new BondingEngine(paths, relay, 443, key, () => _bondingSamples);
            _bonding.Mode = SelectedBondingMode();
            StatusText.Text = "Testing encrypted relay connectivity on every physical path…";
            await _bonding.ConnectAsync(TimeSpan.FromSeconds(6), _stop.Token);
            await _network.ConfigureBondingTunnelAsync();
            _bonding.Start();
            await Task.Delay(750, _stop.Token);
            if (!await _network.VerifyBondedInternetAsync(_stop.Token))
                throw new InvalidOperationException("The relay handshake succeeded, but end-to-end Internet forwarding failed. DualLink restored your normal routes.");
            BondingToggleButton.Content = "Stop bonding";
            SetBondingState($"Bonding: Established — {paths.Count()} path(s) via {relay}", MediaColor.FromRgb(74, 222, 128));
            StatusText.Text = "Bonding connected through the DualLink relay";
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

    private void LoadConnectionHistory()
    {
        var data = ConnectionHistoryStore.Load();
        var cutoff = DateTimeOffset.UtcNow.AddHours(-24);
        _historySamples.AddRange(data.Samples.Where(x => x.Timestamp >= cutoff));
        _historyEvents.AddRange(data.Events.Where(x => x.Timestamp >= cutoff));
        foreach (var sample in _historySamples)
            _knownConnectionTypes[sample.Connection] = sample.Type;
        RebuildHistoryEventRows();
    }

    private void RecordConnectionHistory(IReadOnlyList<AdapterInfo> adapters, IReadOnlyCollection<ProbeResult> probes,
        IReadOnlyDictionary<string, TrafficDisplay> traffic)
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
            AddHistorySample(now, adapter.Name, type, probe.Online, probe.Score, probe.LatencyMs,
                rates?.DownloadMbps ?? 0, rates?.UploadMbps ?? 0);
        }

        foreach (var known in _knownConnectionTypes.Where(x => !current.Contains(x.Key)).ToArray())
            AddHistorySample(now, known.Key, known.Value, false, 0, 0, 0, 0);

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

    private void AddHistorySample(DateTimeOffset now, string connection, string type, bool online, double quality,
        double latency, double downloadMbps, double uploadMbps)
    {
        _historySamples.Add(new(now, connection, type, online, online ? quality : 0, online ? latency : 0,
            online ? downloadMbps : 0, online ? uploadMbps : 0));
        if (_historyState.TryGetValue(connection, out var previous) && previous != online)
        {
            if (!online)
            {
                _outageStarted[connection] = now;
                _historyEvents.Add(new(now, connection, "Connection dropped", null));
            }
            else
            {
                var duration = _outageStarted.Remove(connection, out var started) ? (now - started).TotalSeconds : (double?)null;
                _historyEvents.Add(new(now, connection, "Connection recovered", duration));
            }
        }
        _historyState[connection] = online;
    }

    private void RebuildHistoryEventRows()
    {
        _historyEventRows.Clear();
        foreach (var item in _historyEvents.OrderByDescending(x => x.Timestamp).Take(250))
            _historyEventRows.Add(new(item.Timestamp.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss"), item.Connection,
                item.Event, item.DurationSeconds is { } seconds ? FormatDuration(seconds) : "—"));
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

        foreach (var drop in _historyEvents.Where(x => x.Timestamp >= start && x.Event == "Connection dropped"))
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
            FileName = $"DualLink-history-{DateTime.Now:yyyyMMdd-HHmm}.csv",
            Title = "Export DualLink connection history"
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
        ConnectionHistoryStore.Save(new(_historySamples, _historyEvents));
        if (_bonding is not null) StopBondingAsync().GetAwaiter().GetResult();
        base.OnClosed(e);
    }
}

public sealed record AdapterRow(string Name, string Type, string Address, string Latency, string RelayLatency, string Jitter, string Loss, string Score, string Upload, string Download, string Role)
{
    public static AdapterRow From(AdapterInfo adapter, ProbeResult probe, bool active, string upload = "—", string download = "—", string relayLatency = "—") => new(
        adapter.Name, FriendlyType(adapter), adapter.Address?.ToString() ?? "—",
        probe.Online ? $"{probe.LatencyMs:0} ms" : "Offline",
        relayLatency,
        probe.Online ? $"{probe.JitterMs:0} ms" : "—",
        $"{probe.PacketLossPercent:0}%", $"{probe.Score:0}", upload, download, active ? "Preferred" : "Backup");

    private static string FriendlyType(AdapterInfo adapter)
    {
        var identity = $"{adapter.Name} {adapter.Description}";
        if (identity.Contains("USB", StringComparison.OrdinalIgnoreCase) || identity.Contains("RNDIS", StringComparison.OrdinalIgnoreCase) || identity.Contains("iPhone", StringComparison.OrdinalIgnoreCase)) return "USB tethering";
        return adapter.Type == System.Net.NetworkInformation.NetworkInterfaceType.Wireless80211 ? "Wi-Fi" : "Ethernet";
    }
}

public sealed record AdapterChoice(string? Id, string DisplayName);
public sealed record HistoryEventRow(string Time, string Connection, string Event, string Duration);
