using Traced.Api.Validation;
using Traced.Domain;

namespace Traced.Api.Tests.Validation;

public sealed class TraceQueryParserTests
{
    private static TraceQuery Ok(string? limit, string? after, string? before)
    {
        var result = TraceQueryParser.Parse(limit, after, before);
        Assert.True(result.IsSuccess, result.Error);
        return result.Value;
    }

    private static string Fails(string? limit, string? after, string? before)
    {
        var result = TraceQueryParser.Parse(limit, after, before);
        Assert.False(result.IsSuccess);
        Assert.StartsWith("invalid query parameter: ", result.Error);
        return result.Error;
    }

    [Fact]
    public void No_parameters_gives_default_limit_and_no_bounds()
    {
        var query = Ok(null, null, null);

        Assert.Equal(20, query.Limit);
        Assert.Null(query.AfterNs);
        Assert.Null(query.BeforeNs);
    }

    [Theory]
    [InlineData("1", 1)]
    [InlineData("20", 20)]
    [InlineData("1000", 1000)]
    public void Limit_in_range_is_accepted(string raw, int expected)
    {
        Assert.Equal(expected, Ok(raw, null, null).Limit);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1001")]
    [InlineData("-1")]
    [InlineData("99999999999")]
    public void Limit_out_of_range_is_rejected(string raw)
    {
        Assert.Contains("limit", Fails(raw, null, null));
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.5")]
    [InlineData(" 5")]
    [InlineData("0x10")]
    public void Limit_not_an_integer_is_rejected(string raw)
    {
        Assert.Contains("limit", Fails(raw, null, null));
    }

    [Theory]
    [InlineData("0")]
    [InlineData("1713484800000000000")]
    [InlineData("9223372036854775807")]
    public void After_and_before_accept_non_negative_int64(string raw)
    {
        Assert.Equal(long.Parse(raw, System.Globalization.CultureInfo.InvariantCulture), Ok(null, raw, null).AfterNs);
        Assert.Equal(long.Parse(raw, System.Globalization.CultureInfo.InvariantCulture), Ok(null, null, raw).BeforeNs);
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.0")]
    [InlineData("9223372036854775808")]
    public void After_invalid_is_rejected(string raw)
    {
        Assert.Contains("after", Fails(null, raw, null));
    }

    [Theory]
    [InlineData("-1")]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("1.0")]
    [InlineData("9223372036854775808")]
    public void Before_invalid_is_rejected(string raw)
    {
        Assert.Contains("before", Fails(null, null, raw));
    }

    [Theory]
    [InlineData("100", "100")]
    [InlineData("101", "100")]
    public void After_not_less_than_before_is_rejected(string after, string before)
    {
        var error = Fails(null, after, before);

        Assert.Contains("after", error);
        Assert.Contains("before", error);
    }

    [Fact]
    public void After_less_than_before_is_accepted()
    {
        var query = Ok("5", "99", "100");

        Assert.Equal(5, query.Limit);
        Assert.Equal(99L, query.AfterNs);
        Assert.Equal(100L, query.BeforeNs);
    }

    private static Microsoft.AspNetCore.Http.QueryCollection Query(params (string Key, string[] Values)[] pairs) =>
        new(pairs.ToDictionary(p => p.Key, p => new Microsoft.Extensions.Primitives.StringValues(p.Values)));

    [Theory]
    [InlineData("limit")]
    [InlineData("after")]
    [InlineData("before")]
    public void Repeated_key_is_rejected_and_named(string key)
    {
        var result = TraceQueryParser.Parse(Query((key, ["1", "2"])));

        Assert.False(result.IsSuccess);
        Assert.Equal($"invalid query parameter: {key} must not be repeated", result.Error);
    }

    [Fact]
    public void Query_collection_overload_applies_the_same_rules()
    {
        var result = TraceQueryParser.Parse(Query(("limit", ["5"]), ("after", ["99"]), ("before", ["100"]), ("other", ["x", "y"])));

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(new TraceQuery(5, 99, 100), result.Value);
    }

    [Fact]
    public void Empty_query_collection_gives_defaults()
    {
        var result = TraceQueryParser.Parse(Query());

        Assert.True(result.IsSuccess, result.Error);
        Assert.Equal(new TraceQuery(), result.Value);
    }

    [Fact]
    public void Present_but_empty_value_is_rejected()
    {
        Assert.False(TraceQueryParser.Parse(Query(("limit", [""]))).IsSuccess);
    }
}
