using System.Net;
using System.Net.Http.Headers;
using System.Text;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Time.Testing;

namespace Traced.Api.Tests.Endpoints;

/// <summary>A fresh app (and so a fresh store) per test, on a fake clock just after the spec example's root start.</summary>
public sealed class ApiFactory : WebApplicationFactory<Program>
{
    // 2024-04-19T00:00:00.2Z: the spec example's spans (start 1713484800000000000) are 200 ms old.
    public static readonly DateTimeOffset Now = new(2024, 4, 19, 0, 0, 0, 200, TimeSpan.Zero);

    public FakeTimeProvider Time { get; } = new(Now);

    public Action<IServiceCollection>? ConfigureServices { get; init; }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        builder.ConfigureTestServices(services =>
        {
            services.AddSingleton<TimeProvider>(Time);
            ConfigureServices?.Invoke(services);
        });
    }
}

public abstract class ApiTestBase : IDisposable
{
    protected const string SpecTraceId = "550e8400-e29b-41d4-a716-446655440000";

    // Request example from spec/openapi.yaml, POST /spans.
    protected const string SpecIngestExample = """
        {"spans":[
          {"trace_id":"550e8400-e29b-41d4-a716-446655440000","span_id":"6ba7b810-9dad-11d1-80b4-00c04fd430c8","service":"checkout","operation":"place_order","start_time":1713484800000000000,"end_time":1713484800150000000,"status":"ok","tags":{"order_id":"ord_123"}},
          {"trace_id":"550e8400-e29b-41d4-a716-446655440000","span_id":"6ba7b811-9dad-11d1-80b4-00c04fd430c8","parent_span_id":"6ba7b810-9dad-11d1-80b4-00c04fd430c8","service":"inventory","operation":"reserve_stock","start_time":1713484800020000000,"end_time":1713484800100000000,"status":"ok"}
        ]}
        """;

    protected ApiTestBase(Action<Microsoft.Extensions.DependencyInjection.IServiceCollection>? configureServices = null)
    {
        Factory = new ApiFactory { ConfigureServices = configureServices };
        Client = Factory.CreateClient();
    }

    protected ApiFactory Factory { get; }

    protected HttpClient Client { get; }

    protected static long NowNs => 1_713_484_800_200_000_000L;

    protected Task<HttpResponseMessage> PostSpans(string json, string contentType = "application/json") =>
        Client.PostAsync(new Uri("/spans", UriKind.Relative), new StringContent(json, Encoding.UTF8, contentType));

    protected Task<HttpResponseMessage> Get(string path) => Client.GetAsync(new Uri(path, UriKind.Relative));

    protected static async Task AssertJson(HttpResponseMessage response, HttpStatusCode status, string expectedBody)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(status == response.StatusCode, $"expected {(int)status}, got {(int)response.StatusCode}: {body}");
        Assert.Equal("application/json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expectedBody, body);
    }

    protected static string Span(
        string traceId,
        string spanId,
        long start,
        long durationNs = 1_000_000,
        string? parent = null,
        string status = "ok",
        string service = "svc",
        string operation = "op")
    {
        var parentJson = parent is null ? "" : $",\"parent_span_id\":\"{parent}\"";
        return $"{{\"trace_id\":\"{traceId}\",\"span_id\":\"{spanId}\"{parentJson},\"service\":\"{service}\",\"operation\":\"{operation}\",\"start_time\":{start},\"end_time\":{start + durationNs},\"status\":\"{status}\"}}";
    }

    protected static string Batch(params string[] spans) => $"{{\"spans\":[{string.Join(',', spans)}]}}";

    protected static string NewId() => Guid.NewGuid().ToString();

    protected async Task<string> IngestRoot(long start, string status = "ok")
    {
        var traceId = NewId();
        using var response = await PostSpans(Batch(Span(traceId, NewId(), start, status: status)));
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        return traceId;
    }

    public void Dispose()
    {
        Client.Dispose();
        Factory.Dispose();
        GC.SuppressFinalize(this);
    }

    protected static MediaTypeHeaderValue Json => new("application/json");
}
