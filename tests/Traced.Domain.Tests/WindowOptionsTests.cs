namespace Traced.Domain.Tests;

public sealed class WindowOptionsTests
{
    [Fact]
    public void Defaults_match_plan()
    {
        var options = new WindowOptions();

        Assert.Equal(30, options.WindowMinutes);
        Assert.Equal(TimeSpan.FromSeconds(1), options.EvictionInterval);
        Assert.Equal(32, options.ShardCount);
    }

    [Fact]
    public void Cutoff_is_now_minus_window_in_nanoseconds()
    {
        var options = new WindowOptions { WindowMinutes = 30 };
        const long now = 10_000_000_000_000L;

        Assert.Equal(now - (30L * 60 * 1_000_000_000), options.CutoffNs(now));
    }
}
