using Warp.Core.Data;
using Warp.Core.Data.Contexts;

namespace Warp.Integration.Tests.Infrastructure;

/// <summary>Json backend: a single process-wide-locked context over a temp file.</summary>
public sealed class JsonBackend : IDataContextBackend
{
    private string _file = string.Empty;

    public string Name => "Json";
    public int Parallelism => 200;
    public IDataContext Context { get; private set; } = null!;

    public Task<T> ExecuteAsync<T>(Func<Task<T>> operation) => operation();

    public Task InitializeAsync()
    {
        _file = Path.Combine(Path.GetTempPath(), $"warp-json-{Guid.NewGuid():N}.json");
        Context = new JsonDataContext(_file);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { if (File.Exists(_file)) File.Delete(_file); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }
}

/// <summary>Sqlite backend: BEGIN IMMEDIATE transactions over a temp database file.</summary>
public sealed class SqliteBackend : IDataContextBackend
{
    private string _file = string.Empty;

    public string Name => "Sqlite";
    public int Parallelism => 200;
    public IDataContext Context { get; private set; } = null!;

    public Task<T> ExecuteAsync<T>(Func<Task<T>> operation) => operation();

    public Task InitializeAsync()
    {
        _file = Path.Combine(Path.GetTempPath(), $"warp-sqlite-{Guid.NewGuid():N}.db");
        Context = new SqliteDataContext(_file);
        return Task.CompletedTask;
    }

    public Task DisposeAsync()
    {
        try { if (File.Exists(_file)) File.Delete(_file); } catch { /* best-effort cleanup */ }
        return Task.CompletedTask;
    }
}
