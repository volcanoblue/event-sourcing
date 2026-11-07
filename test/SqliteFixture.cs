using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Storage;
using VolcanoBlue.EventSourcing.Abstractions;
using VolcanoBlue.EventSourcing.EventStore;
using VolcanoBlue.EventSourcing.EventStore.Serialization;

namespace VolcanoBlue.EventSourcing.Tests;

public sealed record TestEvent(long EventId, string Name) : IEvent;

public sealed record TestSnapshot(int EntityVersion, string State) : ISnapshot;

/// <summary>
/// Each fixture owns a private in-memory SQLite database that lives as long as its open connection.
/// </summary>
public sealed class SqliteFixture : IDisposable
{
    private readonly SqliteConnection _connection;

    static SqliteFixture() => EventTypesRegistry.Initialize(typeof(SqliteFixture).Assembly);

    public SqliteFixture()
    {
        _connection = new SqliteConnection("DataSource=:memory:");
        _connection.Open();

        using var events = CreateEventStore();
        events.Database.EnsureCreated();

        using var snapshots = CreateSnapshotStore();
        snapshots.GetService<IRelationalDatabaseCreator>().CreateTables();
    }

    public EventDbContext CreateEventStore() =>
        new(new DbContextOptionsBuilder<EventDbContext>().UseSqlite(_connection).Options);

    public SnapshotDbContext CreateSnapshotStore() =>
        new(new DbContextOptionsBuilder<SnapshotDbContext>().UseSqlite(_connection).Options);

    public void Dispose() => _connection.Dispose();
}
