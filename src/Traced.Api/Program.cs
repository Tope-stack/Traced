using Traced.Api.Contracts;
using Traced.Api.Endpoints;

var builder = WebApplication.CreateBuilder(args);

// Default bind for the Docker emitter; ASPNETCORE_URLS / --urls still override.
if (string.IsNullOrEmpty(builder.Configuration["urls"]))
{
    builder.WebHost.UseUrls("http://0.0.0.0:8080");
}

builder.Services.ConfigureHttpJsonOptions(options =>
    options.SerializerOptions.TypeInfoResolverChain.Insert(0, TracedJsonContext.Default));

var app = builder.Build();

app.MapHealthEndpoints();

app.Run();

public partial class Program;
