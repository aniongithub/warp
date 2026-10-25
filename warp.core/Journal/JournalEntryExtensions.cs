using System.Text.Json;
using Warp.Core.Data;

namespace Warp.Core.Journal;

public static class JournalEntryExtensions
{
    public static JournalEntry CreateEntry(this IJournalEntry root, string kind) =>
        CreateRootEntry(root, new JournalEntry(), kind);

    public static JournalEntry<TSubject> CreateEntry<TSubject>(
        this IJournalEntry root,
        string kind,
        TSubject subject)
        where TSubject : IEntity =>
        CreateRootEntry(root, new JournalEntry<TSubject> { Subject = subject }, kind);

    public static JournalEntry<TSubject, TObject> CreateEntry<TSubject, TObject>(
        this IJournalEntry root,
        string kind,
        TSubject subject,
        TObject @object)
        where TSubject : IEntity
        where TObject : IEntity =>
        CreateRootEntry(
            root,
            new JournalEntry<TSubject, TObject> { Subject = subject, Object = @object },
            kind);

    public static JournalEntry CreateChild(this IJournalEntry parent, string kind) =>
        CreateChildEntry(parent, new JournalEntry(), kind);

    public static JournalEntry<TSubject> CreateChild<TSubject>(
        this IJournalEntry parent,
        string kind,
        TSubject subject)
        where TSubject : IEntity =>
        CreateChildEntry(parent, new JournalEntry<TSubject> { Subject = subject }, kind);

    public static JournalEntry<TSubject, TObject> CreateChild<TSubject, TObject>(
        this IJournalEntry parent,
        string kind,
        TSubject subject,
        TObject @object)
        where TSubject : IEntity
        where TObject : IEntity =>
        CreateChildEntry(
            parent,
            new JournalEntry<TSubject, TObject> { Subject = subject, Object = @object },
            kind);

    public static TEntry WithData<TEntry>(this TEntry entry, JsonElement data)
        where TEntry : IJournalEntry
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.Data = data.ValueKind == JsonValueKind.Undefined
            ? JournalJson.EmptyObject()
            : data.Clone();
        return entry;
    }

    public static TEntry WithData<TEntry, TData>(
        this TEntry entry,
        TData data,
        JsonSerializerOptions? options = null)
        where TEntry : IJournalEntry
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.Data = JsonSerializer.SerializeToElement(data, options);
        return entry;
    }

    public static TEntry WithCause<TEntry>(this TEntry entry, IJournalEntry cause)
        where TEntry : IJournalEntry
    {
        ArgumentNullException.ThrowIfNull(entry);
        ArgumentNullException.ThrowIfNull(cause);
        if (entry.Root is null || cause.Root is null)
            throw new ArgumentException("Causal entries must reference a journal root.", nameof(cause));
        if (!StringComparer.Ordinal.Equals(entry.Root.Id, cause.Root.Id))
            throw new ArgumentException("Causal entries must belong to the same journal root.", nameof(cause));

        entry.CausedBy = cause;
        return entry;
    }

    public static TEntry WithSchemaVersion<TEntry>(this TEntry entry, int schemaVersion)
        where TEntry : IJournalEntry
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (schemaVersion <= 0)
            throw new ArgumentOutOfRangeException(nameof(schemaVersion), "Schema version must be positive.");

        entry.SchemaVersion = schemaVersion;
        return entry;
    }

    public static TEntry At<TEntry>(this TEntry entry, DateTimeOffset timestamp)
        where TEntry : IJournalEntry
    {
        ArgumentNullException.ThrowIfNull(entry);
        entry.Timestamp = timestamp;
        return entry;
    }

    private static TEntry CreateRootEntry<TEntry>(IJournalEntry root, TEntry entry, string kind)
        where TEntry : IJournalEntry
    {
        ArgumentNullException.ThrowIfNull(root);
        if (root.Root is null || !StringComparer.Ordinal.Equals(root.Id, root.Root.Id))
            throw new ArgumentException("CreateEntry must be called on a journal root entry.", nameof(root));

        return Initialize(entry, root, root, kind);
    }

    private static TEntry CreateChildEntry<TEntry>(IJournalEntry parent, TEntry entry, string kind)
        where TEntry : IJournalEntry
    {
        ArgumentNullException.ThrowIfNull(parent);
        if (parent.Root is null)
            throw new ArgumentException("Parent entry must reference a journal root.", nameof(parent));

        return Initialize(entry, parent.Root, parent, kind);
    }

    private static TEntry Initialize<TEntry>(
        TEntry entry,
        IJournalEntry root,
        IJournalEntry parent,
        string kind)
        where TEntry : IJournalEntry
    {
        if (string.IsNullOrWhiteSpace(kind))
            throw new ArgumentException("Journal entry kind cannot be empty.", nameof(kind));

        entry.Root = root;
        entry.Parent = parent;
        entry.Kind = kind;
        entry.Phase = JournalPhase.Fact;
        entry.Component = string.IsNullOrWhiteSpace(parent.Component) ? root.Component : parent.Component;
        entry.Request = parent.Request ?? root.Request;
        entry.User = parent.User ?? root.User;
        entry.ApiKey = parent.ApiKey ?? root.ApiKey;
        return entry;
    }
}
