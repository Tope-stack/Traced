using Microsoft.AspNetCore.Diagnostics;
using Traced.Api.Contracts;

namespace Traced.Api.Endpoints;

/// <summary>Every non-2xx body is the spec's <c>{"error": "..."}</c>.</summary>
public static class Errors
{
    public static IResult BadRequest(string message) => Json(StatusCodes.Status400BadRequest, message);

    public static IResult NotFound(string message) => Json(StatusCodes.Status404NotFound, message);

    /// <summary>
    /// Catches anything the endpoints didn't turn into a result. A <see cref="BadHttpRequestException"/> (e.g. a body
    /// over the size limit) keeps its status code; everything else is a 500 with no exception details.
    /// </summary>
    public static IApplicationBuilder UseJsonErrors(this IApplicationBuilder app) =>
        app.UseExceptionHandler(errorApp => errorApp.Run(async context =>
        {
            var error = context.Features.Get<IExceptionHandlerFeature>()?.Error;
            var (status, message) = error switch
            {
                BadHttpRequestException { StatusCode: StatusCodes.Status413PayloadTooLarge } =>
                    (StatusCodes.Status413PayloadTooLarge, "request body too large"),
                BadHttpRequestException bad => (bad.StatusCode, "invalid request"),
                _ => (StatusCodes.Status500InternalServerError, "internal server error"),
            };

            context.Response.StatusCode = status;
            await context.Response.WriteAsJsonAsync(
                new ErrorResponse(message),
                TracedJsonContext.Default.ErrorResponse,
                contentType: null,
                context.RequestAborted).ConfigureAwait(false);
        }));

    private static IResult Json(int statusCode, string message) =>
        TypedResults.Json(new ErrorResponse(message), TracedJsonContext.Default.ErrorResponse, statusCode: statusCode);
}
