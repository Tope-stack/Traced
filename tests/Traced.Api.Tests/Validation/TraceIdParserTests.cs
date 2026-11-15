using Traced.Api.Validation;

namespace Traced.Api.Tests.Validation;

public sealed class TraceIdParserTests
{
    [Theory]
    [InlineData("550e8400-e29b-41d4-a716-446655440000")]
    [InlineData("550E8400-E29B-41D4-A716-446655440000")]
    public void Valid_uuid_is_accepted(string raw)
    {
        var result = TraceIdParser.Parse(raw);

        Assert.True(result.IsSuccess);
        Assert.Equal(Guid.Parse(raw), result.Value);
    }

    [Theory]
    [InlineData("")]
    [InlineData("abc")]
    [InlineData("550e8400e29b41d4a716446655440000")]
    [InlineData("{550e8400-e29b-41d4-a716-446655440000}")]
    public void Invalid_uuid_is_rejected(string raw)
    {
        var result = TraceIdParser.Parse(raw);

        Assert.False(result.IsSuccess);
        Assert.Equal("invalid trace_id: must be a valid UUID", result.Error);
    }
}
