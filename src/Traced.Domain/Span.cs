namespace Traced.Domain;

/// <summary>
/// One validated span. Timestamps are Unix nanoseconds. <see cref="ParentSpanId"/> is null on roots.
/// <see cref="Tags"/> is never null; a span sent without tags has an empty map.
/// </summary>
public sealed record Span(
    Guid TraceId,
    Guid SpanId,
    Guid? ParentSpanId,
    string Service,
    string Operation,
    long StartNs,
    long EndNs,
    SpanStatus Status,
    IReadOnlyDictionary<string, string> Tags)
{
    public bool IsRoot => ParentSpanId is null;
}
