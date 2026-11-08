using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using VolcanoBlue.EventSourcing;
using VolcanoBlue.EventSourcing.Serialization;

namespace VolcanoBlue.EventSourcing.Tests;

/// <summary>
/// Database-first scenario: DatabaseFirst.db already exists with its own table and column names,
/// and both stores are mapped onto it through the model configuration callbacks of EventDbContext and SnapshotDbContext.
/// </summary>
public sealed class DatabaseFirstTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), $"es-db-first-{Guid.NewGuid():N}");
    private readonly string _databasePath;
    private readonly ServiceProvider _provider;

    static DatabaseFirstTests() => EventTypesRegistry.Initialize(typeof(SqliteFixture).Assembly);

    public DatabaseFirstTests()
    {
        Directory.CreateDirectory(_directory);
        _databasePath = Path.Combine(_directory, "DatabaseFirst.db");
        CreateDatabase();

        _provider = new ServiceCollection()
            .AddSingleton<EventModelConfiguration>(MapEventTable)
            .AddSingleton<SnapshotModelConfiguration>(MapSnapshotTable)
            // Models are cached per context type; keep these mappings apart from the default ones used by other tests.
            .AddDbContext<EventDbContext>(options => options
                .UseSqlite($"Data Source={_databasePath}")
                .ReplaceService<IModelCacheKeyFactory, DatabaseFirstCacheKeyFactory>())
            .AddDbContext<SnapshotDbContext>(options => options
                .UseSqlite($"Data Source={_databasePath}")
                .ReplaceService<IModelCacheKeyFactory, DatabaseFirstCacheKeyFactory>())
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        SqliteConnection.ClearAllPools();
        Directory.Delete(_directory, recursive: true);
    }

    // The schema is owned by the database, not by EF: no EnsureCreated and no migrations.
    private void CreateDatabase()
    {
        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE event_log (
                event_id    INTEGER NOT NULL,
                stream_name TEXT    NOT NULL,
                created_utc TEXT    NOT NULL,
                type_name   TEXT    NOT NULL,
                payload     TEXT    NOT NULL,
                PRIMARY KEY (event_id, stream_name)
            );

            CREATE TABLE snapshot_log (
                entity_name    TEXT    NOT NULL,
                entity_version INTEGER NOT NULL,
                created_utc    TEXT    NOT NULL,
                state_json     TEXT    NOT NULL,
                PRIMARY KEY (entity_name, entity_version)
            );
            """;
        command.ExecuteNonQuery();
    }

    private EventDbContext CreateStore() =>
        _provider.CreateScope().ServiceProvider.GetRequiredService<EventDbContext>();

    private SnapshotDbContext CreateSnapshotStore() =>
        _provider.CreateScope().ServiceProvider.GetRequiredService<SnapshotDbContext>();

    [Fact]
    public async Task AppendAndRead_UsesMappedTableAndColumns()
    {
        using (var writer = CreateStore())
        {
            await writer.AppendToStreamAsync("Test-1", [new TestEvent(1, "first"), new TestEvent(2, "second")]);
            await writer.SaveStreamAsync();
        }

        using var reader = CreateStore();
        var result = await reader.ReadStreamAsync("Test-1");

        Assert.Equal(["first", "second"], result.Cast<TestEvent>().Select(e => e.Name));
    }

    [Fact]
    public async Task SavedRows_LandInDatabaseFirstColumns()
    {
        using (var writer = CreateStore())
        {
            await writer.AppendToStreamAsync("Test-7", [new TestEvent(1, "first")]);
            await writer.SaveStreamAsync();
        }

        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT event_id, stream_name, created_utc, type_name, payload FROM event_log";
        using var row = command.ExecuteReader();

        Assert.True(row.Read());
        Assert.Equal(1L, row.GetInt64(0));
        Assert.Equal("Test-7", row.GetString(1));
        Assert.False(string.IsNullOrWhiteSpace(row.GetString(2)));
        Assert.Equal(typeof(TestEvent).FullName, row.GetString(3));
        Assert.Contains("first", row.GetString(4));
        Assert.False(row.Read());
    }

    [Fact]
    public async Task ReadStreamFromEventId_ReturnsOnlyNewerEvents()
    {
        using (var writer = CreateStore())
        {
            await writer.AppendToStreamAsync("Test-1", [new TestEvent(1, "a"), new TestEvent(2, "b"), new TestEvent(3, "c")]);
            await writer.SaveStreamAsync();
        }

        using var reader = CreateStore();
        var result = await reader.ReadStreamFromEventIdAsync("Test-1", 1);

        Assert.Equal(["b", "c"], result.Cast<TestEvent>().Select(e => e.Name));
    }

    [Fact]
    public async Task Snapshot_UpsertAndRead_UsesMappedTableAndColumns()
    {
        using (var writer = CreateSnapshotStore())
        {
            await writer.UpsertAsync("Test-1", new TestSnapshot(3, "state"));
            await writer.SaveChangesAsync();
        }

        using var reader = CreateSnapshotStore();
        var result = await reader.ByIdAsync<TestSnapshot>("Test-1");

        Assert.True((bool)result);
        Assert.Equal(new TestSnapshot(3, "state"), result.Get());
    }

    [Fact]
    public async Task Snapshot_SavedRow_LandsInDatabaseFirstColumns()
    {
        using (var writer = CreateSnapshotStore())
        {
            await writer.UpsertAsync("Test-9", new TestSnapshot(5, "state"));
            await writer.SaveChangesAsync();
        }

        using var connection = new SqliteConnection($"Data Source={_databasePath}");
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = "SELECT entity_name, entity_version, created_utc, state_json FROM snapshot_log";
        using var row = command.ExecuteReader();

        Assert.True(row.Read());
        Assert.Equal("Test-9", row.GetString(0));
        Assert.Equal(5L, row.GetInt64(1));
        Assert.False(string.IsNullOrWhiteSpace(row.GetString(2)));
        Assert.Contains("state", row.GetString(3));
        Assert.False(row.Read());
    }

    [Fact]
    public async Task Snapshot_Upsert_ReplacesPreviousVersion()
    {
        using (var writer = CreateSnapshotStore())
        {
            await writer.UpsertAsync("Test-1", new TestSnapshot(1, "old"));
            await writer.SaveChangesAsync();
        }

        using (var writer = CreateSnapshotStore())
        {
            await writer.UpsertAsync("Test-1", new TestSnapshot(2, "new"));
            await writer.SaveChangesAsync();
        }

        using var reader = CreateSnapshotStore();
        Assert.Equal(new TestSnapshot(2, "new"), (await reader.ByIdAsync<TestSnapshot>("Test-1")).Get());
    }

    private static void MapSnapshotTable(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Snapshot>(entity =>
        {
            entity.ToTable("snapshot_log");
            entity.Property(e => e.EntityId).HasColumnName("entity_name");
            entity.Property(e => e.EntityVersion).HasColumnName("entity_version");
            entity.Property(e => e.Created).HasColumnName("created_utc");
            entity.Property(e => e.Data).HasColumnName("state_json");
        });

    private static void MapEventTable(ModelBuilder modelBuilder) =>
        modelBuilder.Entity<Event>(entity =>
        {
            entity.ToTable("event_log");
            entity.Property(e => e.Id).HasColumnName("event_id");
            entity.Property(e => e.StreamId).HasColumnName("stream_name");
            entity.Property(e => e.OccurredAt).HasColumnName("created_utc");
            entity.Property(e => e.EventType).HasColumnName("type_name");
            entity.Property(e => e.Data).HasColumnName("payload");
        });

    private sealed class DatabaseFirstCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            (typeof(DatabaseFirstCacheKeyFactory), context.GetType(), designTime);
    }
}
