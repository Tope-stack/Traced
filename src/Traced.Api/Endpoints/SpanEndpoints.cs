using Traced.Api.Contracts;
using Traced.Api.Validation;
using Traced.Domain;

namespace Traced.Api.Endpoints;

public static class SpanEndpoints
{
    /// <summary>Kestrel request body limit (PLAN §4): 500 spans with worst-case tags is ~30 MB.</summary>
    public const long MaxRequestBodyBytes = 32L * 1024 * 1024;

    public static IEndpointRouteBuilder MapSpanEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/spans").WithTags("Ingest");

        group.MapPost("", IngestAsync);

        return app;
    }

    // The body is read by hand, not bound by the framework, so malformed JSON and wrong types come back
    // as our {"error"} 400 instead of the framework's empty 400 / ProblemDetails.
    private static async Task<IResult> IngestAsync(HttpRequest request, ITraceStore store, CancellationToken cancellationToken)
    {
        if (!request.HasJsonContentType())
        {
            return Errors.BadRequest("invalid request body: Content-Type must be application/json");
        }

        using var body = new MemoryStream();
        await request.Body.CopyToAsync(body, cancellationToken).ConfigureAwait(false);

        var result = IngestRequestValidator.Parse(body.GetBuffer().AsSpan(0, (int)body.Length));
        if (!result.IsSuccess)
        {
            return Errors.BadRequest(result.Error);
        }

        store.Ingest(result.Value);
        return TypedResults.Json(StatusResponse.Ok, TracedJsonContext.Default.StatusResponse, statusCode: StatusCodes.Status202Accepted);
    }
}
