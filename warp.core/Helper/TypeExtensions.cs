using System;
using System.Text.RegularExpressions;

namespace Warp.Core.Helper;

public static class TypeStringConverter
{
    /// <summary>
    /// Converts a user-friendly type string (e.g. "Namespace.Type<Arg>, Assembly" or "Namespace.Type, Assembly") to a System.Type.
    /// </summary>
    public static Type? ResolveType(this string typeString)
    {
        // Pattern: Namespace.Type<Arg>, Assembly
        var genericMatch = Regex.Match(typeString, @"^(?<type>[^<,]+)<(?<arg>[^>]+)>\\s*,\\s*(?<assembly>.+)$");
        if (genericMatch.Success)
        {
            var genericTypeName = genericMatch.Groups["type"].Value.Trim();
            var argTypeName = genericMatch.Groups["arg"].Value.Trim();
            var assemblyName = genericMatch.Groups["assembly"].Value.Trim();

            var argType = Type.GetType(argTypeName) ??
                Type.GetType($"{argTypeName}, {assemblyName}") ??
                AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType(argTypeName))
                    .FirstOrDefault(t => t != null);

            if (argType == null)
                throw new Exception($"Could not resolve generic argument type: {argTypeName}");

            var genericType = Type.GetType($"{genericTypeName}`1, {assemblyName}") ??
                AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType($"{genericTypeName}`1"))
                    .FirstOrDefault(t => t != null);

            if (genericType == null)
                throw new Exception($"Could not resolve generic type: {genericTypeName}`1, {assemblyName}");

            return genericType.MakeGenericType(argType);
        }

        // Pattern: Namespace.Type, Assembly (non-generic)
        var nongenericMatch = Regex.Match(typeString, @"^(?<type>[^,]+),\\s*(?<assembly>.+)$");
        if (nongenericMatch.Success)
        {
            var typeName = nongenericMatch.Groups["type"].Value.Trim();
            var assemblyName = nongenericMatch.Groups["assembly"].Value.Trim();
            var type = Type.GetType($"{typeName}, {assemblyName}") ??
                AppDomain.CurrentDomain.GetAssemblies()
                    .Select(a => a.GetType(typeName))
                    .FirstOrDefault(t => t != null);
            if (type == null)
                throw new Exception($"Could not resolve type: {typeName}, {assemblyName}");
            return type;
        }

        // Fallback: try to resolve directly (no assembly specified)
        return Type.GetType(typeString);
    }
}