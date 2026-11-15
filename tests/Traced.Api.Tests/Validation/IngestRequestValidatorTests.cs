using System.Text;
using System.Text.Json.Nodes;
using Traced.Api.Validation;
using Traced.Domain;

namespace Traced.Api.Tests.Validation;

public sealed class IngestRequestValidatorTests
{
    private const string TraceId = "550e8400-e29b-41d4-a716-446655440000";
    private const string RootSpanId = "6ba7b810-9dad-11d1-80b4-00c04fd430c8";
    private const string ChildSpanId = "6ba7b811-9dad-11d1-80b4-00c04fd430c8";

    // The spec's POST /spans request example.
    private static JsonObject RootSpan() => new()
    {
        ["trace_id"] = TraceId,
        ["span_id"] = RootSpanId,
        ["service"] = "checkout",
        ["operation"] = "place_order",
        ["start_time"] = 1713484800000000000L,
        ["end_time"] = 1713484800150000000L,
        ["status"] = "ok",
        ["tags"] = new JsonObject { ["order_id"] = "ord_123" },
    };

    private static JsonObject ChildSpan() => new()
    {
        ["trace_id"] = TraceId,
        ["span_id"] = ChildSpanId,
        ["parent_span_id"] = RootSpanId,
        ["service"] = "inventory",
        ["operation"] = "reserve_stock",
        ["start_time"] = 1713484800020000000L,
        ["end_time"] = 1713484800100000000L,
        ["status"] = "ok",
    };

    private static string Body(params JsonNode?[] spans) =>
        new JsonObject { ["spans"] = new JsonArray(spans) }.ToJsonString();

    private static Result<IReadOnlyList<Span>> Parse(string json) =>
        IngestRequestValidator.Parse(Encoding.UTF8.GetBytes(json));

    private static IReadOnlyList<Span> Ok(string json)
    {
        var result = Parse(json);
        Assert.True(result.IsSuccess, result.Error);
        return result.Value;
    }

    private static string Fails(string json)
    {
        var result = Parse(json);
        Assert.False(result.IsSuccess);
        Assert.StartsWith("invalid request body: ", result.Error);
        return result.Error;
    }

    private static string FailsWithSpan(Action<JsonObject> mutate)
    {
        var span = RootSpan();
        mutate(span);
        return Fails(Body(span));
    }

    // ---- happy path / mapping ----

    [Fact]
    public void Spec_example_batch_maps_to_domain_spans()
    {
        var spans = Ok(Body(RootSpan(), ChildSpan()));

        Assert.Equal(2, spans.Count);
        var root = spans[0];
        Assert.Equal(Guid.Parse(TraceId), root.TraceId);
        Assert.Equal(Guid.Parse(RootSpanId), root.SpanId);
        Assert.Null(root.ParentSpanId);
        Assert.Equal("checkout", root.Service);
        Assert.Equal("place_order", root.Operation);
        Assert.Equal(1713484800000000000L, root.StartNs);
        Assert.Equal(1713484800150000000L, root.EndNs);
        Assert.Equal(SpanStatus.Ok, root.Status);
        Assert.Equal("ord_123", root.Tags["order_id"]);

        var child = spans[1];
        Assert.Equal(Guid.Parse(RootSpanId), child.ParentSpanId);
        Assert.Empty(child.Tags);
    }

    [Fact]
    public void Status_error_maps_to_error()
    {
        var span = RootSpan();
        span["status"] = "error";

        Assert.Equal(SpanStatus.Error, Ok(Body(span))[0].Status);
    }

    [Fact]
    public void Uppercase_uuid_is_accepted()
    {
        var span = RootSpan();
        span["trace_id"] = TraceId.ToUpperInvariant();

        Assert.Equal(Guid.Parse(TraceId), Ok(Body(span))[0].TraceId);
    }

    [Fact]
    public void Explicit_null_parent_span_id_is_a_root()
    {
        var span = RootSpan();
        span["parent_span_id"] = null;

        Assert.Null(Ok(Body(span))[0].ParentSpanId);
    }

    [Fact]
    public void Unknown_fields_are_ignored()
    {
        var span = RootSpan();
        span["extra"] = "x";

        Assert.Single(Ok(Body(span)));
    }

    [Fact]
    public void Child_outside_parent_time_bounds_is_accepted()
    {
        // PLAN §1: child end may exceed root end; don't validate child-within-parent bounds.
        var child = ChildSpan();
        child["start_time"] = 1713484700000000000L;
        child["end_time"] = 1713484900000000000L;

        Assert.Equal(2, Ok(Body(RootSpan(), child)).Count);
    }

    [Fact]
    public void Empty_tags_object_is_accepted()
    {
        var span = RootSpan();
        span["tags"] = new JsonObject();

        Assert.Empty(Ok(Body(span))[0].Tags);
    }

    // ---- body-level rules ----

    [Theory]
    [InlineData("")]
    [InlineData("{")]
    [InlineData("not json")]
    [InlineData("{\"spans\": [] } trailing")]
    public void Malformed_json_is_rejected(string json)
    {
        Fails(json);
    }

    [Theory]
    [InlineData("null")]
    [InlineData("[]")]
    [InlineData("\"spans\"")]
    [InlineData("42")]
    public void Body_that_is_not_an_object_is_rejected(string json)
    {
        Fails(json);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"spans\": null}")]
    public void Missing_spans_is_rejected(string json)
    {
        Assert.Contains("missing required field spans", Fails(json));
    }

    [Fact]
    public void Spans_not_an_array_is_rejected()
    {
        Fails("{\"spans\": {}}");
    }

    [Fact]
    public void Empty_spans_array_is_rejected()
    {
        Assert.Contains("at least 1", Fails(Body()));
    }

    [Fact]
    public void Batch_of_500_spans_is_accepted()
    {
        var spans = Enumerable.Range(0, 500).Select(_ => (JsonNode?)ChildWithNewId()).ToArray();

        Assert.Equal(500, Ok(Body(spans)).Count);
    }

    [Fact]
    public void Batch_of_501_spans_is_rejected()
    {
        var spans = Enumerable.Range(0, 501).Select(_ => (JsonNode?)ChildWithNewId()).ToArray();

        Assert.Contains("at most 500", Fails(Body(spans)));
    }

    [Fact]
    public void Null_span_element_is_rejected()
    {
        Assert.Contains("spans[1]", Fails(Body(RootSpan(), null)));
    }

    [Fact]
    public void One_invalid_span_rejects_the_whole_batch_and_names_its_index()
    {
        var bad = ChildSpan();
        bad["status"] = "nope";

        var error = Fails(Body(RootSpan(), ChildSpan(), bad));

        Assert.Contains("spans[2]", error);
    }

    // ---- per-span rules ----

    [Theory]
    [InlineData("trace_id")]
    [InlineData("span_id")]
    [InlineData("service")]
    [InlineData("operation")]
    [InlineData("start_time")]
    [InlineData("end_time")]
    [InlineData("status")]
    public void Missing_required_field_is_rejected(string field)
    {
        var error = FailsWithSpan(s => s.Remove(field));

        Assert.Equal($"invalid request body: spans[0]: missing required field {field}", error);
    }

    [Theory]
    [InlineData("trace_id")]
    [InlineData("span_id")]
    [InlineData("service")]
    [InlineData("operation")]
    [InlineData("start_time")]
    [InlineData("end_time")]
    [InlineData("status")]
    public void Null_required_field_is_rejected_as_missing(string field)
    {
        var error = FailsWithSpan(s => s[field] = null);

        Assert.Contains($"missing required field {field}", error);
    }

    [Theory]
    [InlineData("trace_id", "not-a-uuid")]
    [InlineData("trace_id", "")]
    [InlineData("trace_id", "550e8400e29b41d4a716446655440000")]
    [InlineData("trace_id", "{550e8400-e29b-41d4-a716-446655440000}")]
    [InlineData("trace_id", "550e8400-e29b-41d4-a716-44665544000g")]
    [InlineData("span_id", "not-a-uuid")]
    [InlineData("span_id", "550e8400e29b41d4a716446655440000")]
    [InlineData("parent_span_id", "not-a-uuid")]
    [InlineData("parent_span_id", "")]
    [InlineData("parent_span_id", "(550e8400-e29b-41d4-a716-446655440000)")]
    public void Invalid_uuid_is_rejected(string field, string value)
    {
        var error = FailsWithSpan(s => s[field] = value);

        Assert.Equal($"invalid request body: spans[0]: {field} must be a valid UUID", error);
    }

    [Theory]
    [InlineData("trace_id")]
    [InlineData("span_id")]
    [InlineData("parent_span_id")]
    [InlineData("service")]
    [InlineData("operation")]
    [InlineData("status")]
    public void Non_string_value_for_string_field_is_rejected(string field)
    {
        FailsWithSpan(s => s[field] = 123);
    }

    [Theory]
    [InlineData("service")]
    [InlineData("operation")]
    public void Name_of_256_chars_is_accepted(string field)
    {
        var span = RootSpan();
        span[field] = new string('a', 256);

        Assert.Single(Ok(Body(span)));
    }

    [Theory]
    [InlineData("service")]
    [InlineData("operation")]
    public void Name_of_257_chars_is_rejected(string field)
    {
        var error = FailsWithSpan(s => s[field] = new string('a', 257));

        Assert.Equal($"invalid request body: spans[0]: {field} must be at most 256 characters", error);
    }

    [Theory]
    [InlineData("service")]
    [InlineData("operation")]
    public void Empty_name_is_accepted_because_spec_has_no_min_length(string field)
    {
        var span = RootSpan();
        span[field] = "";

        Assert.Equal("", field == "service" ? Ok(Body(span))[0].Service : Ok(Body(span))[0].Operation);
    }

    [Fact]
    public void End_before_start_is_rejected()
    {
        var error = FailsWithSpan(s => s["end_time"] = 1713484799999999999L);

        Assert.Equal("invalid request body: spans[0]: end_time must not be before start_time", error);
    }

    [Fact]
    public void End_equal_to_start_is_accepted()
    {
        var span = RootSpan();
        span["end_time"] = 1713484800000000000L;

        Assert.Single(Ok(Body(span)));
    }

    [Theory]
    [InlineData("start_time", "\"1713484800000000000\"")]
    [InlineData("start_time", "1.5")]
    [InlineData("end_time", "\"x\"")]
    [InlineData("end_time", "99999999999999999999")]
    public void Non_int64_timestamp_is_rejected(string field, string rawJson)
    {
        FailsWithSpan(s => s[field] = JsonNode.Parse(rawJson));
    }

    [Theory]
    [InlineData("OK")]
    [InlineData("Error")]
    [InlineData("unset")]
    [InlineData("")]
    public void Status_outside_enum_is_rejected(string status)
    {
        var error = FailsWithSpan(s => s["status"] = status);

        Assert.Equal("invalid request body: spans[0]: status must be one of: ok, error", error);
    }

    [Fact]
    public void Fifty_tags_are_accepted()
    {
        var span = RootSpan();
        span["tags"] = Tags(50, valueLength: 1);

        Assert.Equal(50, Ok(Body(span))[0].Tags.Count);
    }

    [Fact]
    public void Fifty_one_tags_are_rejected()
    {
        var error = FailsWithSpan(s => s["tags"] = Tags(51, valueLength: 1));

        Assert.Equal("invalid request body: spans[0]: tags must have at most 50 entries", error);
    }

    [Fact]
    public void Tag_value_of_1024_chars_is_accepted()
    {
        var span = RootSpan();
        span["tags"] = Tags(1, valueLength: 1024);

        Assert.Single(Ok(Body(span)));
    }

    [Fact]
    public void Tag_value_of_1025_chars_is_rejected()
    {
        var error = FailsWithSpan(s => s["tags"] = Tags(1, valueLength: 1025));

        Assert.Equal("invalid request body: spans[0]: tags.k0 must be at most 1024 characters", error);
    }

    [Fact]
    public void Null_tag_value_is_rejected()
    {
        var error = FailsWithSpan(s => s["tags"] = new JsonObject { ["k"] = null });

        Assert.Equal("invalid request body: spans[0]: tags.k must be a string", error);
    }

    [Theory]
    [InlineData("{\"k\": 1}")]
    [InlineData("{\"k\": true}")]
    [InlineData("{\"k\": {}}")]
    [InlineData("[]")]
    [InlineData("\"tags\"")]
    public void Non_string_tag_values_or_non_object_tags_are_rejected(string rawJson)
    {
        FailsWithSpan(s => s["tags"] = JsonNode.Parse(rawJson));
    }

    private static JsonObject ChildWithNewId()
    {
        var span = ChildSpan();
        span["span_id"] = Guid.NewGuid().ToString();
        return span;
    }

    private static JsonObject Tags(int count, int valueLength)
    {
        var tags = new JsonObject();
        for (var i = 0; i < count; i++)
        {
            tags[$"k{i}"] = new string('v', valueLength);
        }

        return tags;
    }
}
