using Traced.Api.Contracts;

namespace Traced.Api.Endpoints;

public static class HealthEndpoints
{
    public static IEndpointRouteBuilder MapHealthEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("/health").WithTags("Health");

        group.MapGet("", () => TypedResults.Ok(StatusResponse.Ok));

        return app;
    }
}
