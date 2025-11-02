using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;
using System.Reflection;
using Warp.Core.Data;
using Warp.Core.Helper;
using Warp.Latinum.Extensions;

var builder = WebApplication.CreateBuilder(args);
var assemblyName = "warp.latinum";

// Load configuration from warp.latinum.yml using Warp config system
var configBuilder = new ConfigurationBuilder().AddWarpConfiguration("warp.latinum",
    baseDirectory: Environment.GetEnvironmentVariable("WARP_CONFIG_BASE_DIR") ?? "./config");
var config = configBuilder.Build();

// Configure Kestrel URLs from the loaded configuration
var kestrelSection = config.GetSection("Kestrel");
if (kestrelSection.Exists())
{
    builder.WebHost.UseConfiguration(config);
}

// Configure data context
IDataContext? dataContext = null;
var dataContextSection = config.GetSection("DataContext");
if (dataContextSection.Exists())
{
    dataContext = dataContextSection.CreateFromConfiguration();
    builder.Services.AddSingleton(dataContext);
}

// Add HTTP client for webhook registration  
builder.Services.AddHttpClient();

// Add basic MVC controllers
builder.Services.AddControllers();

// Configure custom controllers from configuration using Warp's pattern
var controllersSection = config.GetSection("Controllers");
builder.Services.AddControllersFromConfig(controllersSection);

// Add OpenTelemetry tracing using the Warp config
var otelSection = config.GetSection("OpenTelemetry");
var sourceNames = otelSection.GetSection("SourceNames").Get<string[]>() ?? new[] { "Warp" };
var otelEndpoint = otelSection.GetValue<string>("Endpoint") ?? "http://localhost:4317";
var serviceName = otelSection.GetValue<string>("ServiceName") ?? assemblyName;

builder.Services.AddOpenTelemetry().WithTracing(tracer =>
{
    tracer
        .SetResourceBuilder(ResourceBuilder.CreateDefault().AddService(serviceName))
        .AddAspNetCoreInstrumentation(options =>
            options.EnrichWithHttpResponse = (activity, response) =>
                activity.SetStatus(response?.StatusCode.IsErrorStatus() == true
                    ? ActivityStatusCode.Error
                    : ActivityStatusCode.Ok,
                    response != null
                        ? $"HTTP {response.StatusCode.GetStatusDescription()}"
                        : string.Empty))
        .AddSource(sourceNames)

        .AddOtlpExporter(otlp => otlp.Endpoint = new Uri(otelEndpoint))
        .AddHttpClientInstrumentation();
});

var app = builder.Build();

app.MapControllers();
app.MapGet("/health", () => Results.Ok(new { status = "healthy", service = assemblyName }));

app.Run();