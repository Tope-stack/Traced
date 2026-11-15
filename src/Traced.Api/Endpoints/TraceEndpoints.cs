using Traced.Api.Contracts;
using Traced.Api.Mapping;
using Traced.Api.Validation;
using Traced.Domain;

namespace Traced.Api.Endpoints;

public static class TraceEndpoints
{
    public static IEndpointRouteBuilder MapTraceEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/traces").WithTags("Traces");

        group.MapGet("", ListTraces);
        group.MapGet("/{traceId}", GetTrace);

        return app;
    }

    private static IResult ListTraces(HttpRequest request, ITraceStore store)
    {
        var query = TraceQueryParser.Parse(request.Query);
        if (!query.IsSuccess)
        {
            return Errors.BadRequest(query.Error);
        }

        var response = store.Query(query.Value).ToResponse();
        return TypedResults.Json(response, TracedJsonContext.Default.TraceListResponse);
    }

    private static IResult GetTrace(string traceId, ITraceStore store)
    {
        var id = TraceIdParser.Parse(traceId);
        if (!id.IsSuccess)
        {
            return Errors.BadRequest(id.Error);
        }

        if (!store.TryGetTrace(id.Value, out var spans))
        {
            return Errors.NotFound("trace not found");
        }

        return TypedResults.Json(ContractMapping.ToDetailResponse(id.Value, spans), TracedJsonContext.Default.TraceDetailResponse);
    }
}
