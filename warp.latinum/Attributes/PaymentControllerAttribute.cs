using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Warp.Latinum.Attributes;

/// <summary>
/// Base attribute for payment provider controllers.
/// Handles webhook registration and provider-specific configuration during app startup.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = true)]
public abstract class PaymentControllerAttribute : Attribute
{
    /// <summary>
    /// Configure the payment provider (register webhooks, etc.) during controller registration.
    /// This method is called once during app startup with access to the service provider and configuration.
    /// If this method throws an exception, app startup will fail.
    /// </summary>
    /// <param name="serviceProvider">Service provider for accessing dependencies</param>
    /// <param name="optionsSection">Configuration section for this controller's options</param>
    /// <returns>Task that completes when configuration is done</returns>
    public abstract Task ConfigureAsync(IServiceProvider serviceProvider, IConfigurationSection optionsSection);
}