using System.Reflection;

namespace Traced.Api.Endpoints;

/// <summary>
/// Human-facing extras outside the OpenAPI contract: the contract itself at <c>/openapi.yaml</c>, an API reference page
/// at <c>/docs</c>, and <c>/</c> redirecting to it. Both files are embedded resources, read once at startup.
/// </summary>
public static class DocsEndpoints
{
    private static readonly byte[] OpenApiYaml = ReadResource("openapi.yaml");
    private static readonly byte[] DocsHtml = ReadResource("docs.html");

    public static IEndpointRouteBuilder MapDocsEndpoints(this IEndpointRouteBuilder app)
    {
        var group = app.MapGroup("").WithTags("Docs").ExcludeFromDescription();

        group.MapGet("/", () => TypedResults.Redirect("/docs"));
        group.MapGet("/docs", () => TypedResults.Bytes(DocsHtml, "text/html; charset=utf-8"));
        group.MapGet("/openapi.yaml", () => TypedResults.Bytes(OpenApiYaml, "application/yaml; charset=utf-8"));

        return app;
    }

    private static byte[] ReadResource(string name)
    {
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream(name)
            ?? throw new InvalidOperationException($"Embedded resource '{name}' is missing.");
        using var copy = new MemoryStream();
        stream.CopyTo(copy);
        return copy.ToArray();
    }
}
