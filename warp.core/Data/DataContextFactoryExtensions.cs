using Microsoft.Extensions.Configuration;
using System.Reflection;

using Warp.Core.Helper;

namespace Warp.Core.Data;

public static class DataContextFactoryExtensions
{
    public static IDataContext CreateFromConfiguration(this IConfigurationSection dataContextSection)
    {
        if (!dataContextSection.Exists())
            throw new Exception("DataContext configuration section does not exist.");

        var dataContextTypeName = dataContextSection.GetValue<string>("Type") ?? throw new Exception("DataContext type not specified in configuration.");
        var dataContextType = dataContextTypeName.ResolveType() ?? throw new Exception($"Could not resolve DataContext type: {dataContextTypeName}");
        var dataContextArgsSection = dataContextSection.GetSection("args");
        object? dataContextInstance;

        if (dataContextArgsSection.Exists())
        {
            var ctorParams = dataContextType.GetConstructors().OrderByDescending(c => c.GetParameters().Length).FirstOrDefault()?.GetParameters();
            if (ctorParams != null && ctorParams.Length > 0)
            {
                var argsList = new List<object?>();
                foreach (var param in ctorParams)
                {
                    var val = param.Name != null
                        ? dataContextArgsSection.GetValue(param.ParameterType, param.Name)
                        : throw new Exception("Parameter name is null.");
                    if (val == null && param.HasDefaultValue)
                        val = param.DefaultValue;
                    if (val == null)
                        throw new Exception($"Missing required DataContext constructor argument: {param.Name}");
                    argsList.Add(val);
                }
                dataContextInstance = Activator.CreateInstance(dataContextType, argsList.ToArray());
            }
            else
                dataContextInstance = Activator.CreateInstance(dataContextType);
        }
        else
            dataContextInstance = Activator.CreateInstance(dataContextType);

        if (dataContextInstance is not IDataContext context)
            throw new Exception($"DataContext instance does not implement IDataContext: {dataContextType}");

        return context;
    }
}
