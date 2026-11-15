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

builder.WebHost.ConfigureKestrel(kestrel => kestrel.Limits.MaxRequestBodySize = SpanEndpoints.MaxRequestBodyBytes);

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, TracedJsonContext.Default));

// The dashboard (:8081) calls the API (:8080) from the browser.
builder.Services.AddCors(cors => cors.AddDefaultPolicy(policy => policy
    .AllowAnyOrigin()
    .WithMethods("GET", "POST")
    .WithHeaders("Content-Type")));

// WINDOW_MINUTES comes from the environment (or any other configuration source); default 30.
var window = builder.Configuration.GetValue<int?>("WINDOW_MINUTES") is { } minutes
    ? new WindowOptions { WindowMinutes = minutes }
    : new WindowOptions();

builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddSingleton(window);
builder.Services.AddSingleton<ITraceStore, ShardedTraceStore>();
builder.Services.AddHostedService<EvictionService>();

var app = builder.Build();

app.UseJsonErrors();
app.UseCors();

app.MapDocsEndpoints();
app.MapHealthEndpoints();
app.MapSpanEndpoints();
app.MapTraceEndpoints();

app.Run();

public partial class Program;
