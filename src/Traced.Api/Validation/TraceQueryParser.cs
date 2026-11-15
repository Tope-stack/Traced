using System.Globalization;
using Traced.Domain;

namespace Traced.Api.Validation;

/// <summary>Parses raw <c>GET /traces</c> query values (null = absent) into a <see cref="TraceQuery"/>.</summary>
public static class TraceQueryParser
{
    private const string Prefix = "invalid query parameter: ";

    public static Result<TraceQuery> Parse(string? limit, string? after, string? before)
    {
        var parsedLimit = TraceQuery.DefaultLimit;
        if (limit is not null)
        {
            if (!TryParseInt64(limit, out var value) || value < 1 || value > TraceQuery.MaxLimit)
            {
                return Fail($"limit must be an integer between 1 and {TraceQuery.MaxLimit}");
            }

            parsedLimit = (int)value;
        }

        if (!TryParseBound(after, out var afterNs))
        {
            return Fail("after must be a non-negative integer (Unix nanoseconds)");
        }

        if (!TryParseBound(before, out var beforeNs))
        {
            return Fail("before must be a non-negative integer (Unix nanoseconds)");
        }

        if (afterNs is { } a && beforeNs is { } b && a >= b)
        {
            return Fail("after must be less than before");
        }

        return Result<TraceQuery>.Success(new TraceQuery(parsedLimit, afterNs, beforeNs));
    }

    private static bool TryParseBound(string? raw, out long? value)
    {
        value = null;
        if (raw is null)
        {
            return true;
        }

        if (!TryParseInt64(raw, out var parsed) || parsed < 0)
        {
            return false;
        }

        value = parsed;
        return true;
    }

    // Digits with an optional sign only: no whitespace, decimals, exponents or hex.
    private static bool TryParseInt64(string raw, out long value) =>
        long.TryParse(raw, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out value);

    private static Result<TraceQuery> Fail(string message) => Result<TraceQuery>.Failure(Prefix + message);
}
