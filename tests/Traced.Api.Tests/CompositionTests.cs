using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Traced.Domain;
using Traced.Storage;

namespace Traced.Api.Tests;

public sealed class CompositionTests(WebApplicationFactory<Program> factory)
    : IClassFixture<WebApplicationFactory<Program>>
{
    [Fact]
    public void Store_is_a_single_sharded_instance()
    {
        var services = factory.Services;

        var first = services.GetRequiredService<ITraceStore>();
        var second = services.GetRequiredService<ITraceStore>();

        Assert.IsType<ShardedTraceStore>(first);
        Assert.Same(first, second);
    }

    [Fact]
    public void Eviction_service_is_registered_as_a_hosted_service()
    {
        Assert.Contains(factory.Services.GetServices<IHostedService>(), s => s is EvictionService);
    }

    [Fact]
    public void System_time_provider_and_default_window_are_registered()
    {
        Assert.Same(TimeProvider.System, factory.Services.GetRequiredService<TimeProvider>());
        Assert.Equal(30, factory.Services.GetRequiredService<WindowOptions>().WindowMinutes);
    }

    [Fact]
    public void Window_minutes_setting_is_honoured()
    {
        using var custom = factory.WithWebHostBuilder(b => b.UseSetting("WINDOW_MINUTES", "5"));

        Assert.Equal(5, custom.Services.GetRequiredService<WindowOptions>().WindowMinutes);
    }
}
