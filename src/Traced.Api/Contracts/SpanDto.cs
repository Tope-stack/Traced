namespace Traced.Api.Contracts;

/// <summary>Outbound spec <c>Span</c>. Null <see cref="ParentSpanId"/> and <see cref="Tags"/> are omitted.</summary>
public sealed record SpanDto(
    string TraceId,
    string SpanId,
    string? ParentSpanId,
    string Service,
    string Operation,
    long StartTime,
    long EndTime,
    string Status,
    IReadOnlyDictionary<string, string>? Tags);
