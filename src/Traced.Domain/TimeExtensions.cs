namespace Traced.Domain;

public static class TimeExtensions
{
    private const long NanosecondsPerTick = 100;

    /// <summary>Current UTC time as Unix nanoseconds (100 ns resolution, the <see cref="DateTimeOffset"/> tick).</summary>
    public static long GetUtcNowUnixNanoseconds(this TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        return (timeProvider.GetUtcNow() - DateTimeOffset.UnixEpoch).Ticks * NanosecondsPerTick;
    }
}
