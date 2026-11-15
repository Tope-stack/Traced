using System.Net;

namespace Traced.Api.Tests.Endpoints;

public sealed class DocsEndpointTests : ApiTestBase
{
    [Fact]
    public async Task Openapi_yaml_serves_the_contract_unchanged()
    {
        using var response = await Client.GetAsync(new Uri("/openapi.yaml", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("application/yaml", response.Content.Headers.ContentType?.MediaType);

        var expected = await File.ReadAllTextAsync(SpecPath());
        Assert.Equal(expected, await response.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task Docs_page_is_html_that_loads_the_contract()
    {
        using var response = await Client.GetAsync(new Uri("/docs", UriKind.Relative));

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("text/html", response.Content.Headers.ContentType?.MediaType);

        var html = await response.Content.ReadAsStringAsync();
        Assert.Contains("data-url=\"/openapi.yaml\"", html, StringComparison.Ordinal);
        Assert.Contains("@scalar/api-reference@", html, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Root_redirects_to_docs()
    {
        using var client = Factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        using var response = await client.GetAsync(new Uri("/", UriKind.Relative));

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        Assert.Equal("/docs", response.Headers.Location?.OriginalString);
    }

    // tests/Traced.Api.Tests/bin/<config>/net10.0 -> repository root -> spec/openapi.yaml
    private static string SpecPath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "spec", "openapi.yaml")))
        {
            dir = dir.Parent;
        }

        return Path.Combine(dir?.FullName ?? throw new InvalidOperationException("spec/openapi.yaml not found"), "spec", "openapi.yaml");
    }
}
