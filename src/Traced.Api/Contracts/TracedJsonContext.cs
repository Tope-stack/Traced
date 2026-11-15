using System.Text.Json.Serialization;

namespace Traced.Api.Contracts;

[JsonSourceGenerationOptions(
    PropertyNamingPolicy = JsonKnownNamingPolicy.SnakeCaseLower,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull)]
[JsonSerializable(typeof(StatusResponse))]
[JsonSerializable(typeof(ErrorResponse))]
[JsonSerializable(typeof(IngestRequest))]
[JsonSerializable(typeof(TraceListResponse))]
[JsonSerializable(typeof(TraceDetailResponse))]
public sealed partial class TracedJsonContext : JsonSerializerContext;
