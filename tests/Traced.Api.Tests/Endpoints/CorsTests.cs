using System.Net;

namespace Traced.Api.Tests.Endpoints;

public sealed class CorsTests : ApiTestBase
{
    private const string DashboardOrigin = "http://localhost:8081";

    private async Task<HttpResponseMessage> Preflight(string path, string method, string? headers = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Options, new Uri(path, UriKind.Relative));
        request.Headers.Add("Origin", DashboardOrigin);
        request.Headers.Add("Access-Control-Request-Method", method);
        if (headers is not null)
        {
            request.Headers.Add("Access-Control-Request-Headers", headers);
        }

        return await Client.SendAsync(request);
    }

    private static string? Header(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values) ? string.Join(",", values) : null;

    [Fact]
    public async Task Preflight_for_post_spans_with_content_type_is_allowed()
    {
        using var response = await Preflight("/spans", "POST", "content-type");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("*", Header(response, "Access-Control-Allow-Origin"));
        Assert.Contains("POST", Header(response, "Access-Control-Allow-Methods") ?? "", StringComparison.Ordinal);
        Assert.Contains("content-type", Header(response, "Access-Control-Allow-Headers") ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Preflight_for_get_traces_is_allowed()
    {
        using var response = await Preflight("/traces", "GET");

        Assert.Equal(HttpStatusCode.NoContent, response.StatusCode);
        Assert.Equal("*", Header(response, "Access-Control-Allow-Origin"));
        Assert.Contains("GET", Header(response, "Access-Control-Allow-Methods") ?? "", StringComparison.Ordinal);
    }

    [Fact]
    public async Task Preflight_for_a_disallowed_method_does_not_allow_it()
    {
        // ASP.NET still echoes Allow-Origin here; the browser blocks because DELETE isn't in Allow-Methods.
        using var response = await Preflight("/traces", "DELETE");

        Assert.DoesNotContain("DELETE", Header(response, "Access-Control-Allow-Methods") ?? "", StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Simple_get_from_the_dashboard_origin_carries_allow_origin()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/traces?limit=1000", UriKind.Relative));
        request.Headers.Add("Origin", DashboardOrigin);

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("*", Header(response, "Access-Control-Allow-Origin"));
    }

    [Fact]
    public async Task Error_responses_also_carry_allow_origin()
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri("/traces?limit=0", UriKind.Relative));
        request.Headers.Add("Origin", DashboardOrigin);

        using var response = await Client.SendAsync(request);

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("*", Header(response, "Access-Control-Allow-Origin"));
    }
}
