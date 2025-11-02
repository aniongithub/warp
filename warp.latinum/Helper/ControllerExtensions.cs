using System.Reflection;
using Warp.Core.Helper;
using Warp.Latinum.Attributes;
using Warp.Latinum.Infrastructure;

namespace Warp.Latinum.Extensions;

public static class ControllerExtensions
{
    /// <summary>
    /// Adds controllers to the service collection based on configuration.
    /// Uses the same pattern as middleware registration in Warp.
    /// Also processes PaymentControllerAttributes for webhook registration.
    /// </summary>
    /// <param name="services">The service collection</param>
    /// <param name="controllersSection">Configuration section containing controller definitions</param>
    /// <returns>A list of controller type names that were registered</returns>
    public static List<string> AddControllersFromConfig(
        this IServiceCollection services,
        IConfigurationSection controllersSection)
    {
        var registeredControllers = new List<string>();
        
        if (!controllersSection.Exists())
        {
            return registeredControllers;
        }

        // Collect payment attributes for webhook registration after app startup
        var webhookRegistrations = new List<(PaymentControllerAttribute attr, IConfigurationSection config)>();

        foreach (var controllerConfig in controllersSection.GetChildren())
        {
            var typeName = controllerConfig.GetValue<string>("Type");

            if (string.IsNullOrEmpty(typeName))
            {
                continue;
            }

            try
            {
                // Resolve controller type using Warp's type resolution
                var controllerType = typeName.ResolveType();
                if (controllerType == null)
                {
                    throw new Exception($"Could not resolve controller type: {typeName}");
                }

                // Configure options if they exist
                var optionsSection = controllerConfig.GetSection("Options");
                if (optionsSection.Exists())
                {
                    // Find the corresponding options type (e.g., StripeWebhookController -> StripeWebhookOptions)
                    var optionsTypeName = controllerType.Name.Replace("Controller", "Options");
                    var optionsType = controllerType.Assembly.GetType($"{controllerType.Namespace}.{optionsTypeName}");

                    if (optionsType != null)
                    {
                        // Use the same configuration pattern as middlewares - directly configure with IConfiguration
                        var configureMethod = typeof(OptionsConfigurationServiceCollectionExtensions)
                            .GetMethods()
                            .Where(m => m.Name == "Configure" && m.IsGenericMethodDefinition &&
                                       m.GetParameters().Length == 2 &&
                                       m.GetParameters()[1].ParameterType == typeof(IConfiguration))
                            .FirstOrDefault();

                        if (configureMethod != null)
                        {
                            var genericConfigureMethod = configureMethod.MakeGenericMethod(optionsType);
                            genericConfigureMethod.Invoke(null, new object[] { services, optionsSection });
                        }
                    }
                }

                // Collect PaymentControllerAttributes for webhook registration after app startup
                var paymentAttributes = controllerType.GetCustomAttributes<PaymentControllerAttribute>();
                foreach (var attr in paymentAttributes)
                {
                    webhookRegistrations.Add((attr, optionsSection));
                }

                // Register controller - let ASP.NET Core handle dependency injection
                services.AddScoped(controllerType);
                registeredControllers.Add(controllerType.Name);
            }
            catch (Exception ex)
            {
                throw new Exception($"Error registering controller '{typeName}': {ex.Message}", ex);
            }
        }
        
        // Register startup filter to handle webhook registration after app starts
        if (webhookRegistrations.Count > 0)
        {
            services.AddSingleton<IStartupFilter>(serviceProvider => 
                new WebhookRegistrationStartupFilter(webhookRegistrations, serviceProvider));
        }
        
        return registeredControllers;
    }
}