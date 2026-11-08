using Microsoft.EntityFrameworkCore;
using Npgsql;
using Testcontainers.PostgreSql;
using VolcanoBlue.EventSourcing.Abstractions;

namespace VolcanoBlue.EventSourcing.Tests;

public sealed record TestEvent(long EventId, string Name) : IEvent;

/// <summary>Fixed clock, to assert the stored timestamps.</summary>
public sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

/// <summary>
/// Each fixture owns a private database on one PostgreSQL container shared by the whole test run (Docker is required).
/// The container is started on first use and removed when the test process exits.
/// </summary>
public sealed class DatabaseFixture : IDisposable
{
    private static readonly Lazy<PostgreSqlContainer> Container = new(() =>
    {
        var container = new PostgreSqlBuilder("postgres:17-alpine").Build();
        container.StartAsync().GetAwaiter().GetResult();
        return container;
    });

    private readonly string _name = $"es_{Guid.NewGuid():N}";

    // Events are registered one by one, the way a trimmed or AOT application has to.
    public static readonly EventSourcingOptions Options = new EventSourcingOptions()
        .AddEvent<TestEvent>()
        .AddEvent<FieldEvent>()
        .AddEvent<NamedEvent>();

    public DatabaseFixture(bool createSchema = true)
    {
        Execute($"CREATE DATABASE \"{_name}\"");
        ConnectionString = new NpgsqlConnectionStringBuilder(Container.Value.GetConnectionString())
        {
            Database = _name,
            Pooling = false,
        }.ConnectionString;

        if (createSchema)
        {
            using var db = CreateContext();
            db.Database.EnsureCreated();
        }
    }

    public string ConnectionString { get; }

    public EventStoreDbContext CreateContext() =>
        new(new DbContextOptionsBuilder<EventStoreDbContext>().UseNpgsql(ConnectionString).Options);

    public EventStore CreateStore(EventStoreDbContext db, EventSourcingOptions? options = null) =>
        new(db, options ?? Options);

    public void Dispose() => Execute($"DROP DATABASE IF EXISTS \"{_name}\" WITH (FORCE)");

    private static void Execute(string sql)
    {
        using var connection = new NpgsqlConnection(
            new NpgsqlConnectionStringBuilder(Container.Value.GetConnectionString()) { Pooling = false }.ConnectionString);
        connection.Open();
        using var command = new NpgsqlCommand(sql, connection);
        command.ExecuteNonQuery();
    }
}
