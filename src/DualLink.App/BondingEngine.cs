using System.Net;
using DualLink.Core;

namespace DualLink.App;

internal sealed class BondingEngine : IAsyncDisposable
{
    private readonly WintunDevice _tun;
    private readonly DualPathBondingClient _client;
    private readonly Func<IReadOnlyCollection<BondingPathSample>> _samples;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _captureTask;
    private Task? _probeTask;

    public BondingMode Mode { get; set; } = BondingMode.Bonding;

    public BondingEngine(
        IEnumerable<BondingPathConfig> paths,
        IPAddress relayAddress,
        int relayPort,
        ReadOnlySpan<byte> key,
        Func<IReadOnlyCollection<BondingPathSample>> samples)
    {
        _samples = samples;
        _tun = new WintunDevice();
        _client = new DualPathBondingClient(paths, new IPEndPoint(relayAddress, relayPort), key);
        _client.PacketReceived += packet =>
        {
            _tun.Send(packet.Span);
            return ValueTask.CompletedTask;
        };
    }

    public void Start()
    {
        if (_captureTask is not null) return;
        _client.Start();
        _captureTask = Task.Run(CaptureLoopAsync);
        _probeTask = Task.Run(ProbeLoopAsync);
    }

    public async Task ConnectAsync(TimeSpan timeout, CancellationToken token)
    {
        _client.Start();
        await _client.ProbeAllAsync(token);
        await _client.WaitForRelayAsync(timeout, token);
    }

    private async Task CaptureLoopAsync()
    {
        while (!_shutdown.IsCancellationRequested)
        {
            var packet = await _tun.ReceiveAsync(_shutdown.Token);
            await _client.SendPacketAsync(packet, _client.GetAdaptiveSamples(_samples()), Mode, _shutdown.Token);
        }
    }

    public Task UpdatePathsAsync(IEnumerable<BondingPathConfig> paths) => _client.UpdatePathsAsync(paths);
    public IReadOnlyCollection<BondingPathTraffic> GetTraffic() => _client.GetTraffic();

    private async Task ProbeLoopAsync()
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(2));
        while (await timer.WaitForNextTickAsync(_shutdown.Token))
            await _client.ProbeAllAsync(_shutdown.Token);
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        var tasks = new[] { _captureTask, _probeTask }.Where(task => task is not null).Cast<Task>();
        try { await Task.WhenAll(tasks); } catch (OperationCanceledException) { }
        await _client.DisposeAsync();
        _tun.Dispose();
        _shutdown.Dispose();
    }
}
