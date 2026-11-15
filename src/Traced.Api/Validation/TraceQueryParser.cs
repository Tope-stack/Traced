using System.Globalization;
using Microsoft.AspNetCore.Http;
using Traced.Domain;

namespace Traced.Api.Validation;

/// <summary>Parses raw <c>GET /traces</c> query values (null = absent) into a <see cref="TraceQuery"/>.</summary>
public static class TraceQueryParser
{
    private const string Prefix = "invalid query parameter: ";

    /// <summary>
    /// Reads <c>limit</c>, <c>after</c> and <c>before</c> from a request's query string. A repeated key is a 400
    /// (Tope's decision, DECISIONS.md) rather than first- or last-wins. Other keys are ignored.
    /// </summary>
    public static Result<TraceQuery> Parse(IQueryCollection query)
    {
        ArgumentNullException.ThrowIfNull(query);

        if (Single(query, "limit", out var limit) is { } limitError) { return limitError; }
        if (Single(query, "after", out var after) is { } afterError) { return afterError; }
        if (Single(query, "before", out var before) is { } beforeError) { return beforeError; }

        return Parse(limit, after, before);
    }

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

    private static Result<TraceQuery>? Single(IQueryCollection query, string key, out string? value)
    {
        value = null;
        if (!query.TryGetValue(key, out var values) || values.Count == 0)
        {
            return null;
        }

        if (values.Count > 1)
        {
            return Fail($"{key} must not be repeated");
        }

        value = values[0] ?? string.Empty;
        return null;
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
