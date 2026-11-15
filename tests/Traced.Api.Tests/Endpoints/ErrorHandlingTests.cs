using System.Diagnostics.CodeAnalysis;
using System.Net;
using Microsoft.Extensions.DependencyInjection;
using Traced.Domain;

namespace Traced.Api.Tests.Endpoints;

public sealed class ErrorHandlingTests() : ApiTestBase(services => services.AddSingleton<ITraceStore, ThrowingStore>())
{
    private sealed class ThrowingStore : ITraceStore
    {
        public void Ingest(IReadOnlyList<Span> spans) => throw new InvalidOperationException("store exploded");

        public TraceQueryResult Query(TraceQuery query) => throw new InvalidOperationException("store exploded");

        public bool TryGetTrace(Guid traceId, [NotNullWhen(true)] out IReadOnlyList<Span>? spans) => throw new InvalidOperationException("store exploded");

        public int EvictOlderThan(long cutoffNs) => 0;
    }

    [Fact]
    public async Task Unhandled_exception_is_a_json_500_without_details()
    {
        using var response = await PostSpans(SpecIngestExample);

        await AssertJson(response, HttpStatusCode.InternalServerError, """{"error":"internal server error"}""");
    }

    [Fact]
    public async Task Unhandled_exception_on_get_is_a_json_500()
    {
        using var response = await Get("/traces");

        await AssertJson(response, HttpStatusCode.InternalServerError, """{"error":"internal server error"}""");
    }
}
