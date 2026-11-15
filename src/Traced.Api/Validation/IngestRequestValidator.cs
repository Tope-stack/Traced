using System.Text.Json;
using Traced.Api.Contracts;
using Traced.Domain;

namespace Traced.Api.Validation;

/// <summary>
/// Turns a <c>POST /spans</c> body into domain spans, or the first error found. All-or-nothing:
/// one invalid span fails the whole batch. Inbound mapping happens here so each field is parsed once.
/// </summary>
public static class IngestRequestValidator
{
    public const int MaxSpans = 500;
    public const int MaxNameLength = 256;
    public const int MaxTags = 50;
    public const int MaxTagValueLength = 1024;

    private const string Prefix = "invalid request body: ";

    public static Result<IReadOnlyList<Span>> Parse(ReadOnlySpan<byte> utf8Json)
    {
        IngestRequest? request;
        try
        {
            request = JsonSerializer.Deserialize(utf8Json, TracedJsonContext.Default.IngestRequest);
        }
        catch (JsonException ex)
        {
            // The one exception-to-Result conversion: System.Text.Json reports malformed input by throwing.
            return Fail(ex.Path is { Length: > 1 } path ? $"malformed JSON at {path}" : "malformed JSON");
        }

        return Validate(request);
    }

    public static Result<IReadOnlyList<Span>> Validate(IngestRequest? request)
    {
        if (request is null)
        {
            return Fail("body must be a JSON object");
        }

        if (request.Spans is not { } items)
        {
            return Fail("missing required field spans");
        }

        if (items.Count == 0)
        {
            return Fail("spans must contain at least 1 span");
        }

        if (items.Count > MaxSpans)
        {
            return Fail($"spans must contain at most {MaxSpans} spans");
        }

        var spans = new Span[items.Count];
        for (var i = 0; i < items.Count; i++)
        {
            var error = TryMapSpan(items[i], out var span);
            if (error is not null)
            {
                return Fail($"spans[{i}]: {error}");
            }

            spans[i] = span!;
        }

        return Result<IReadOnlyList<Span>>.Success(spans);
    }

    /// <returns>null on success (and <paramref name="span"/> set), otherwise the error for this span.</returns>
    private static string? TryMapSpan(IngestSpan? dto, out Span? span)
    {
        span = null;
        if (dto is null)
        {
            return "must be an object";
        }

        if (dto.TraceId is not { } rawTraceId) { return Missing("trace_id"); }
        if (dto.SpanId is not { } rawSpanId) { return Missing("span_id"); }
        if (dto.Service is not { } service) { return Missing("service"); }
        if (dto.Operation is not { } operation) { return Missing("operation"); }
        if (dto.StartTime is not { } startNs) { return Missing("start_time"); }
        if (dto.EndTime is not { } endNs) { return Missing("end_time"); }
        if (dto.Status is not { } rawStatus) { return Missing("status"); }

        if (!TraceIdParser.TryParseUuid(rawTraceId, out var traceId)) { return InvalidUuid("trace_id"); }
        if (!TraceIdParser.TryParseUuid(rawSpanId, out var spanId)) { return InvalidUuid("span_id"); }

        Guid? parentSpanId = null;
        if (dto.ParentSpanId is { } rawParent)
        {
            if (!TraceIdParser.TryParseUuid(rawParent, out var parent)) { return InvalidUuid("parent_span_id"); }
            parentSpanId = parent;
        }

        if (service.Length > MaxNameLength) { return TooLong("service", MaxNameLength); }
        if (operation.Length > MaxNameLength) { return TooLong("operation", MaxNameLength); }

        if (endNs < startNs)
        {
            return "end_time must be >= start_time";
        }

        SpanStatus status;
        switch (rawStatus)
        {
            case "ok": status = SpanStatus.Ok; break;
            case "error": status = SpanStatus.Error; break;
            default: return "status must be one of: ok, error";
        }

        var tagsError = TryMapTags(dto.Tags, out var tags);
        if (tagsError is not null)
        {
            return tagsError;
        }

        span = new Span(traceId, spanId, parentSpanId, service, operation, startNs, endNs, status, tags);
        return null;
    }

    private static string? TryMapTags(IReadOnlyDictionary<string, string?>? raw, out IReadOnlyDictionary<string, string> tags)
    {
        tags = EmptyTags;
        if (raw is null || raw.Count == 0)
        {
            return null;
        }

        if (raw.Count > MaxTags)
        {
            return $"tags must have at most {MaxTags} entries";
        }

        var copy = new Dictionary<string, string>(raw.Count, StringComparer.Ordinal);
        foreach (var (key, value) in raw)
        {
            if (value is null) { return $"tags.{key} must be a string"; }
            if (value.Length > MaxTagValueLength) { return TooLong($"tags.{key}", MaxTagValueLength); }
            copy[key] = value;
        }

        tags = copy;
        return null;
    }

    private static readonly IReadOnlyDictionary<string, string> EmptyTags =
        System.Collections.ObjectModel.ReadOnlyDictionary<string, string>.Empty;

    private static string Missing(string field) => $"missing required field {field}";

    private static string InvalidUuid(string field) => $"{field} must be a valid UUID";

    private static string TooLong(string field, int max) => $"{field} must be at most {max} characters";

    private static Result<IReadOnlyList<Span>> Fail(string message) =>
        Result<IReadOnlyList<Span>>.Failure(Prefix + message);
}
