namespace Traced.Domain;

/// <summary><paramref name="Total"/> counts every match before the limit; <paramref name="Traces"/> is the page.</summary>
public sealed record TraceQueryResult(int Total, IReadOnlyList<TraceSummary> Traces);
