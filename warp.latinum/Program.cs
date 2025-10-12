using Microsoft.Extensions.Configuration;
using OpenTelemetry.Resources;
using OpenTelemetry.Trace;
using System.Diagnostics;
using Warp.Core.Data;
using Warp.Core.Helper;
using Warp.Latinum.Middleware.Stripe;

var builder = WebApplication.CreateBuilder(args);
var assemblyName = "warp.latinum";

// Load configuration from warp.latinum.yml using Warp config system
var configBuilder = new ConfigurationBuilder().AddWarpConfiguration("warp.latinum",
    baseDirectory: Environment.GetEnvironmentVariable("WARP_CONFIG_BASE_DIR") ?? "./config");
var warpConfig = configBuilder.Build();

// Configure data context
IDataContext? dataContext = null;
var dataContextSection = warpConfig.GetSection("DataContext");
if (dataContextSection.Exists())
{
    dataContext = dataContextSection.CreateFromConfiguration();
    builder.Services.AddSingleton(dataContext);
}

// Configure Stripe middleware options using the Warp config
builder.Services.Configure<StripeSubscriptionOptions>(
    warpConfig.GetSection("Stripe:Subscription"));
builder.Services.Configure<StripePaymentOptions>(
    warpConfig.GetSection("Stripe:Payment"));

// Register middleware
builder.Services.AddScoped<StripeSubscriptionMiddleware>();
builder.Services.AddScoped<StripePaymentMiddleware>();

// Add controllers
builder.Services.AddControllers();

// Add OpenTelemetry tracing using the Warp config
var otelSection = warpConfig.GetSection("OpenTelemetry");
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