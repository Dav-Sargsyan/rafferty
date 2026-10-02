using System.Net.NetworkInformation;
using System.Threading.Channels;
using Rafferty.Core;
using Microsoft.Extensions.Hosting;

namespace Rafferty.Service;

public sealed class RecoveryWorker : BackgroundService
{
    private readonly CommandDispatcher _dispatcher;
    private readonly IBypassEngine _engine;
    private readonly Channel<RecoveryEvent> _events = Channel.CreateBounded<RecoveryEvent>(new BoundedChannelOptions(8) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true });

    public RecoveryWorker(CommandDispatcher dispatcher, IBypassEngine engine)
    {
        _dispatcher = dispatcher;
        _engine = engine;
    }

    public override async Task StartAsync(CancellationToken cancellationToken)
    {
        _engine.UnexpectedExit += OnUnexpectedExit;
        NetworkChange.NetworkAddressChanged += OnNetworkChanged;
        await base.StartAsync(cancellationToken).ConfigureAwait(false);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await _dispatcher.RestoreAsync(stoppingToken).ConfigureAwait(false);
        }
        catch
        {
            // Startup restore failure is recorded by the controller and must not stop IPC.
        }

        await foreach (var recoveryEvent in _events.Reader.ReadAllAsync(stoppingToken).ConfigureAwait(false))
        {
            try
            {
                if (recoveryEvent == RecoveryEvent.EngineExited)
                    await _dispatcher.RecoverAsync(stoppingToken).ConfigureAwait(false);
                else
                    await _dispatcher.NetworkChangedAsync(stoppingToken).ConfigureAwait(false);
            }
            catch when (!stoppingToken.IsCancellationRequested)
            {
                // The next explicit command or event can recover; never busy-loop.
            }
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _engine.UnexpectedExit -= OnUnexpectedExit;
        NetworkChange.NetworkAddressChanged -= OnNetworkChanged;
        await base.StopAsync(cancellationToken).ConfigureAwait(false);
    }

    private void OnUnexpectedExit(object? sender, EventArgs args) => _events.Writer.TryWrite(RecoveryEvent.EngineExited);
    private void OnNetworkChanged(object? sender, EventArgs args) => _events.Writer.TryWrite(RecoveryEvent.NetworkChanged);
    private enum RecoveryEvent { EngineExited, NetworkChanged }
}
