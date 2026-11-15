using Traced.Api.Contracts;
using Traced.Api.Endpoints;
using Traced.Domain;
using Traced.Storage;

var builder = WebApplication.CreateBuilder(args);

// Default bind for the Docker emitter; ASPNETCORE_URLS / --urls still override.
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://0.0.0.0:8080");
}

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, TracedJsonContext.Default));

// WINDOW_MINUTES comes from the environment (or any other configuration source); default 30.
var window = builder.Configuration.GetValue<int?>("WINDOW_MINUTES") is { } minutes
    ? new WindowOptions { WindowMinutes = minutes }
    : new WindowOptions();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(window);
builder.Services.AddSingleton<ITraceStore, ShardedTraceStore>();
builder.Services.AddHostedService<EvictionService>();

var app = builder.Build();

app.MapHealthEndpoints();

app.Run();

public partial class Program;
