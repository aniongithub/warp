using System.Text.Json;

namespace Warp.Core.Journal;

internal static class JournalJson
{
    internal static readonly JsonSerializerOptions SerializerOptions = new(JsonSerializerDefaults.Web);

    internal static JsonElement EmptyObject() => JsonSerializer.SerializeToElement(new Dictionary<string, object?>());
}
