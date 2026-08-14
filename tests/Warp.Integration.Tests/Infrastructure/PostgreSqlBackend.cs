using Testcontainers.PostgreSql;
using Warp.Core.Data;
using Warp.Core.Data.Contexts;

namespace Warp.Integration.Tests.Infrastructure;

/// <summary>
/// PostgreSql backend backed by a real Postgres container. Exercises the SQL transaction /
/// row-lock atomicity path (conditional UPDATE for quota, SELECT ... FOR UPDATE for rate limit).
/// </summary>
public sealed class PostgreSqlBackend : IDataContextBackend
{
    private readonly PostgreSqlContainer _container = new PostgreSqlBuilder()
        .WithImage("postgres:16-alpine")
        .Build();

    public string Name => "PostgreSql";
    public int Parallelism => 200;
    public IDataContext Context { get; private set; } = null!;

    public Task<T> ExecuteAsync<T>(Func<Task<T>> operation) => operation();

    public async Task InitializeAsync()
    {
        await _container.StartAsync();
        Context = new PostgreSqlDataContext(_container.GetConnectionString());
    }

    public async Task DisposeAsync() => await _container.DisposeAsync();
}
