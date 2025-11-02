using Warp.Latinum.Attributes;

namespace Warp.Latinum.Infrastructure;

/// <summary>
/// Startup filter that registers payment webhooks after the application has fully started.
/// This ensures the webhook endpoints are available when LocalStripe tries to validate them.
/// </summary>
public class WebhookRegistrationStartupFilter : IStartupFilter
{
    private readonly List<(PaymentControllerAttribute attr, IConfigurationSection config)> _registrations;
    private readonly IServiceProvider _serviceProvider;

    public WebhookRegistrationStartupFilter(
        List<(PaymentControllerAttribute attr, IConfigurationSection config)> registrations,
        IServiceProvider serviceProvider)
    {
        _registrations = registrations;
        _serviceProvider = serviceProvider;
    }

    public Action<IApplicationBuilder> Configure(Action<IApplicationBuilder> next)
    {
        return app =>
        {
            // Register webhook registration to run after app starts
            var lifetime = app.ApplicationServices.GetRequiredService<IHostApplicationLifetime>();
            var logger = app.ApplicationServices.GetRequiredService<ILogger<WebhookRegistrationStartupFilter>>();
            
            lifetime.ApplicationStarted.Register(async () =>
            {
                logger.LogInformation("Application started, registering {Count} payment webhooks", _registrations.Count);
                
                foreach (var (attr, config) in _registrations)
                {
                    try
                    {
                        await attr.ConfigureAsync(_serviceProvider, config);
                    }
                    catch (Exception ex)
                    {
                        logger.LogError(ex, "Failed to register payment webhook during application startup");
                        // Don't throw here - let the app continue running even if webhook registration fails
                    }
                }
                
                logger.LogInformation("Completed payment webhook registration");
            });
            
            next(app);
        };
    }
}