using Microsoft.Extensions.Configuration;
using Warp.Core.Helper;

namespace Warp.Core.Journal;

public static class JournalStoreFactoryExtensions
{
    public static IJournalStore CreateJournalStore(this IConfigurationSection journalSection)
    {
        if (!journalSection.Exists())
            throw new InvalidOperationException("Journal configuration section does not exist.");

        var typeName = journalSection.GetValue<string>("Type")
            ?? throw new InvalidOperationException("Journal store type is not specified.");
        var type = typeName.ResolveType()
            ?? throw new InvalidOperationException($"Could not resolve journal store type: {typeName}");
        if (!typeof(IJournalStore).IsAssignableFrom(type))
            throw new InvalidOperationException($"Configured type does not implement {nameof(IJournalStore)}: {type}");

        var arguments = journalSection.GetSection("Args");
        var constructor = type.GetConstructors()
            .OrderByDescending(candidate => candidate.GetParameters().Length)
            .FirstOrDefault()
            ?? throw new InvalidOperationException($"Journal store type has no public constructor: {type}");
        var values = new List<object?>();
        foreach (var parameter in constructor.GetParameters())
        {
            var value = parameter.Name is null
                ? null
                : arguments.GetValue(parameter.ParameterType, parameter.Name);
            if (value is null && parameter.HasDefaultValue)
                value = parameter.DefaultValue;
            if (value is null)
                throw new InvalidOperationException(
                    $"Missing required journal store constructor argument: {parameter.Name}");
            values.Add(value);
        }

        return (IJournalStore)(Activator.CreateInstance(type, values.ToArray())
            ?? throw new InvalidOperationException($"Could not create journal store: {type}"));
    }
}
