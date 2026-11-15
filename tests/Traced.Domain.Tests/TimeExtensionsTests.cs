using Microsoft.Extensions.Time.Testing;

namespace Traced.Domain.Tests;

public sealed class TimeExtensionsTests
{
    [Fact]
    public void Unix_epoch_is_zero_nanoseconds()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);

        Assert.Equal(0L, time.GetUtcNowUnixNanoseconds());
    }

    [Fact]
    public void Converts_spec_example_instant_to_nanoseconds()
    {
        // 1713484800000000000 ns is the spec's example root start_time (2024-04-19T00:00:00Z).
        var time = new FakeTimeProvider(new DateTimeOffset(2024, 4, 19, 0, 0, 0, TimeSpan.Zero));

        Assert.Equal(1_713_484_800_000_000_000L, time.GetUtcNowUnixNanoseconds());
    }

    [Fact]
    public void Advancing_fake_time_advances_nanoseconds_at_tick_resolution()
    {
        var time = new FakeTimeProvider(DateTimeOffset.UnixEpoch);

        time.Advance(TimeSpan.FromTicks(1));

        Assert.Equal(100L, time.GetUtcNowUnixNanoseconds());
    }
}
