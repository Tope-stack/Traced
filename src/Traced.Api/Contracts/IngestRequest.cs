namespace Traced.Api.Contracts;

/// <summary>
/// Inbound <c>POST /spans</c> body exactly as received. Every member is nullable so that a missing
/// required field reaches the validator as <c>null</c> instead of failing inside the deserializer.
/// </summary>
public sealed class IngestRequest
{
    public IReadOnlyList<IngestSpan?>? Spans { get; init; }
}

public sealed class IngestSpan
{
    public string? TraceId { get; init; }

    public string? SpanId { get; init; }

    public string? ParentSpanId { get; init; }

    public string? Service { get; init; }

    public string? Operation { get; init; }

    public long? StartTime { get; init; }

    public long? EndTime { get; init; }

    public string? Status { get; init; }

    public IReadOnlyDictionary<string, string?>? Tags { get; init; }
}
