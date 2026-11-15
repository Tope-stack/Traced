using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Traced.Domain;

namespace Traced.Storage;

/// <summary>
/// Every <see cref="WindowOptions.EvictionInterval"/>, evicts traces older than <c>now - window</c>.
/// The timer runs on the injected <see cref="TimeProvider"/>, so tests drive ticks with <c>FakeTimeProvider.Advance</c>.
/// A failing tick is logged and the loop carries on: a dead eviction loop would leak memory silently.
/// </summary>
public sealed partial class EvictionService(
    ITraceStore store,
    TimeProvider time,
    WindowOptions options,
    ILogger<EvictionService> logger) : BackgroundService
{
    private PeriodicTimer? _timer;

    public override Task StartAsync(CancellationToken cancellationToken)
    {
        // Created here, synchronously, not in ExecuteAsync: since .NET 10 the host runs ExecuteAsync on the
        // thread pool, so a timer created there could miss time that passes before it exists.
        // PeriodicTimer latches a tick that fires before WaitForNextTickAsync is awaited.
        _timer = new PeriodicTimer(options.EvictionInterval, time);
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var timer = _timer ?? throw new InvalidOperationException("StartAsync must run before ExecuteAsync.");
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                EvictOnce();
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // Host shutdown.
        }
    }

    public override void Dispose()
    {
        _timer?.Dispose();
        base.Dispose();
    }

    private void EvictOnce()
    {
        try
        {
            var cutoff = options.CutoffNs(time.GetUtcNowUnixNanoseconds());
            var removed = store.EvictOlderThan(cutoff);
            if (removed > 0)
            {
                LogEvicted(removed, cutoff);
            }
        }
#pragma warning disable CA1031 // Deliberately broad: any failure must not end the loop.
        catch (Exception ex)
#pragma warning restore CA1031
        {
            LogTickFailed(ex);
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Evicted {Removed} traces older than {CutoffNs}")]
    private partial void LogEvicted(int removed, long cutoffNs);

    [LoggerMessage(EventId = 2, Level = LogLevel.Error, Message = "Eviction tick failed; will retry on the next tick")]
    private partial void LogTickFailed(Exception exception);
}
