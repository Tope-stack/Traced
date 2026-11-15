namespace Traced.Domain;

public sealed class WindowOptions
{
    private const long NanosecondsPerMinute = 60L * 1_000_000_000L;

    public int WindowMinutes { get; init; } = 30;

    public TimeSpan EvictionInterval { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>Must be a power of two.</summary>
    public int ShardCount { get; init; } = 32;

    public long WindowNs => WindowMinutes * NanosecondsPerMinute;

    /// <summary>Oldest root start time still inside the window. A span starting exactly at the cutoff is kept.</summary>
    public long CutoffNs(long nowNs) => nowNs - WindowNs;
}
