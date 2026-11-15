namespace Traced.Domain;

/// <summary>
/// A validated <c>GET /traces</c> query. <see cref="AfterNs"/> is inclusive, <see cref="BeforeNs"/> exclusive.
/// </summary>
public sealed record TraceQuery(int Limit = TraceQuery.DefaultLimit, long? AfterNs = null, long? BeforeNs = null)
{
    public const int DefaultLimit = 20;
    public const int MaxLimit = 1000;
}
