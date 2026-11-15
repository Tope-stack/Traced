using System.Diagnostics.CodeAnalysis;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Traced.Domain;
using static Traced.Storage.Tests.TestSpans;

namespace Traced.Storage.Tests;

public sealed class EvictionServiceTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    private sealed class RecordingStore : ITraceStore
    {
        public readonly Channel<long> Cutoffs = Channel.CreateUnbounded<long>();
        public int ThrowOnCall { get; init; } = -1;
        private int _calls;

        public int EvictOlderThan(long cutoffNs)
        {
            var call = _calls++;
            Cutoffs.Writer.TryWrite(cutoffNs);
            if (call == ThrowOnCall)
            {
                throw new InvalidOperationException("boom");
            }

            return 0;
        }

        public void Ingest(IReadOnlyList<Span> spans) => throw new NotSupportedException();

        public TraceQueryResult Query(TraceQuery query) => throw new NotSupportedException();

        public bool TryGetTrace(Guid traceId, [NotNullWhen(true)] out IReadOnlyList<Span>? spans) => throw new NotSupportedException();
    }

    private sealed class ListLogger : ILogger<EvictionService>
    {
        public readonly List<(LogLevel Level, Exception? Exception)> Entries = [];

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        {
            lock (Entries)
            {
                Entries.Add((logLevel, exception));
            }
        }
    }

    private static (FakeTimeProvider Time, RecordingStore Store, ListLogger Logger, EvictionService Service) Create(int throwOnCall = -1)
    {
        var time = new FakeTimeProvider(Epoch);
        var store = new RecordingStore { ThrowOnCall = throwOnCall };
        var logger = new ListLogger();
        var options = new WindowOptions { WindowMinutes = 30, EvictionInterval = TimeSpan.FromSeconds(1) };
        return (time, store, logger, new EvictionService(store, time, options, logger));
    }

    private static async Task<long> NextCutoff(RecordingStore store)
    {
        using var timeout = new CancellationTokenSource(Wait);
        return await store.Cutoffs.Reader.ReadAsync(timeout.Token);
    }

    [Fact]
    public async Task Each_interval_tick_evicts_with_now_minus_window()
    {
        var (time, store, _, service) = Create();
        await service.StartAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(NowNs + 1_000_000_000L - (30 * Minute), await NextCutoff(store));

        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(NowNs + 2_000_000_000L - (30 * Minute), await NextCutoff(store));

        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task No_tick_before_the_interval_elapses()
    {
        var (time, store, _, service) = Create();
        await service.StartAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromMilliseconds(999));
        await Task.Delay(100);

        Assert.False(store.Cutoffs.Reader.TryRead(out _));
        await service.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task A_throwing_tick_is_logged_and_the_next_tick_still_runs()
    {
        var (time, store, logger, service) = Create(throwOnCall: 0);
        await service.StartAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(1));
        await NextCutoff(store); // throws inside the service
        time.Advance(TimeSpan.FromSeconds(1));
        Assert.Equal(NowNs + 2_000_000_000L - (30 * Minute), await NextCutoff(store));

        await service.StopAsync(CancellationToken.None);
        lock (logger.Entries)
        {
            var error = Assert.Single(logger.Entries, e => e.Level == LogLevel.Error);
            Assert.IsType<InvalidOperationException>(error.Exception);
        }

        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task Stopping_ends_the_loop_cleanly()
    {
        var (time, store, _, service) = Create();
        await service.StartAsync(CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(1));
        await NextCutoff(store);

        await service.StopAsync(CancellationToken.None);

        Assert.True(service.ExecuteTask!.IsCompletedSuccessfully);
        time.Advance(TimeSpan.FromSeconds(5));
        await Task.Delay(100);
        Assert.False(store.Cutoffs.Reader.TryRead(out _));
    }

    [Fact]
    public async Task Drives_a_real_store_so_evicted_traces_disappear()
    {
        // Store clamps with a 30 min window; the service evicts with a 1 min window. After 1 min + 1 s the
        // trace is still inside the store's own window, so only an actual eviction can hide it.
        var (time, store) = NewStore(windowMinutes: 30);
        var serviceOptions = new WindowOptions { WindowMinutes = 1, EvictionInterval = TimeSpan.FromSeconds(1) };
        using var service = new EvictionService(store, time, serviceOptions, new ListLogger());
        var root = Root(Guid.NewGuid(), startNs: NowNs);
        store.Ingest([root, Child(root)]);
        await service.StartAsync(CancellationToken.None);

        time.Advance(TimeSpan.FromSeconds(61));

        using var timeout = new CancellationTokenSource(Wait);
        while (store.TryGetTrace(root.TraceId, out _))
        {
            await Task.Delay(10, timeout.Token);
        }

        Assert.Equal(0, store.Query(new TraceQuery()).Total);
        await service.StopAsync(CancellationToken.None);
    }
}
