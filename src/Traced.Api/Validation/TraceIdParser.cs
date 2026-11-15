namespace Traced.Api.Validation;

public static class TraceIdParser
{
    /// <summary>Accepts only the canonical 8-4-4-4-12 hex form (either case), per the spec's <c>format: uuid</c>.</summary>
    public static Result<Guid> Parse(string? raw) =>
        Guid.TryParseExact(raw, "D", out var id)
            ? Result<Guid>.Success(id)
            : Result<Guid>.Failure("invalid trace_id: must be a valid UUID");

    internal static bool TryParseUuid(string raw, out Guid id) => Guid.TryParseExact(raw, "D", out id);
}
