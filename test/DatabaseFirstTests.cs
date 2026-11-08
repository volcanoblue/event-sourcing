using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;
using VolcanoBlue.EventSourcing.Extensions;

namespace VolcanoBlue.EventSourcing.Tests;

/// <summary>
/// Database-first scenario: the database already exists with its own table and column names,
/// and the single EventStoreDbContext is mapped onto it through the model configuration callback.
/// </summary>
public sealed class DatabaseFirstTests : IDisposable
{
    private readonly DatabaseFixture _database = new(createSchema: false);
    private readonly ServiceProvider _provider;

    public DatabaseFirstTests()
    {
        CreateDatabase();

        _provider = new ServiceCollection()
            .AddSingleton<EventStoreModelConfiguration>(MapTables)
            // Models are cached per context type; keep these mappings apart from the default ones used by other tests.
            .AddDbContext<EventStoreDbContext>(options => options
                .UseNpgsql(_database.ConnectionString)
                .ReplaceService<IModelCacheKeyFactory, DatabaseFirstCacheKeyFactory>())
            .AddEventSourcing(options => options.AddEvent<TestEvent>())
            .BuildServiceProvider();
    }

    public void Dispose()
    {
        _provider.Dispose();
        _database.Dispose();
    }

    // The schema is owned by the database, not by EF: no EnsureCreated and no migrations.
    private void CreateDatabase()
    {
        using var connection = new NpgsqlConnection(_database.ConnectionString);
        connection.Open();

        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE event_log (
                event_id    BIGINT  NOT NULL,
                stream_name TEXT    NOT NULL,
                created_utc TEXT    NOT NULL,
                type_name   TEXT    NOT NULL,
                payload     TEXT    NOT NULL,
                PRIMARY KEY (event_id, stream_name)
            );

            CREATE TABLE snapshot_log (
                entity_name    TEXT    NOT NULL,
                entity_version BIGINT  NOT NULL,
                created_utc    TEXT    NOT NULL,
                state_json     TEXT    NOT NULL,
                PRIMARY KEY (entity_name, entity_version)
            );
            """;
        command.ExecuteNonQuery();
    }

    private EventStore CreateStore() =>
        _provider.CreateScope().ServiceProvider.GetRequiredService<EventStore>();

    private NpgsqlDataReader Query(string sql)
    {
        var connection = new NpgsqlConnection(_database.ConnectionString);
        connection.Open();

        var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteReader(System.Data.CommandBehavior.CloseConnection);
    }

    private static SnapshotEntity Renamed(int count, bool snapshot)
    {
        var entity = new SnapshotEntity { AutoSnapshot = snapshot };
        for (var i = 1; i <= count; i++)
            entity.Rename($"e{i}");
        return entity;
    }

    [Fact]
    public async Task SaveAndLoad_UseMappedTablesAndColumns()
    {
        await CreateStore().SaveAsync("Test-1", Renamed(2, snapshot: false));

        var loaded = (await CreateStore().LoadAsync<SnapshotEntity>("Test-1"))!;

        Assert.Equal("e1,e2", loaded.State);
        Assert.Equal(2, loaded.Version);
    }

    [Fact]
    public async Task SavedEvents_LandInDatabaseFirstColumns()
    {
        await CreateStore().SaveAsync("Test-7", Renamed(1, snapshot: false));

        using var row = Query("SELECT event_id, stream_name, created_utc, type_name, payload FROM event_log");

        Assert.True(row.Read());
        Assert.Equal(1L, row.GetInt64(0));
        Assert.Equal("Test-7", row.GetString(1));
        Assert.False(string.IsNullOrWhiteSpace(row.GetString(2)));
        Assert.Equal(typeof(TestEvent).FullName, row.GetString(3));
        Assert.Contains("e1", row.GetString(4));
        Assert.False(row.Read());
    }

    [Fact]
    public async Task SavedSnapshot_LandsInDatabaseFirstColumns_AndLoadsFromIt()
    {
        await CreateStore().SaveAsync("Test-9", Renamed(5, snapshot: true));

        using (var row = Query("SELECT entity_name, entity_version, created_utc, state_json FROM snapshot_log"))
        {
            Assert.True(row.Read());
            Assert.Equal("Test-9", row.GetString(0));
            Assert.Equal(5L, row.GetInt64(1));
            Assert.False(string.IsNullOrWhiteSpace(row.GetString(2)));
            Assert.Contains("e1,e2,e3,e4,e5", row.GetString(3));
            Assert.False(row.Read());
        }

        var loaded = (await CreateStore().LoadAsync<SnapshotEntity>("Test-9"))!;
        Assert.Equal(0, loaded.ProcessedEvents); // restored from the snapshot, nothing replayed
    }

    [Fact]
    public async Task NewerSnapshot_ReplacesThePreviousOne()
    {
        var entity = Renamed(1, snapshot: true);
        await CreateStore().SaveAsync("Test-1", entity);
        entity.Rename("e2");
        await CreateStore().SaveAsync("Test-1", entity);

        using var rows = Query("SELECT entity_version FROM snapshot_log");

        Assert.True(rows.Read());
        Assert.Equal(2L, rows.GetInt64(0));
        Assert.False(rows.Read());
    }

    private static void MapTables(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Snapshot>(entity =>
        {
            entity.ToTable("snapshot_log");
            entity.Property(e => e.EntityId).HasColumnName("entity_name");
            entity.Property(e => e.EntityVersion).HasColumnName("entity_version");
            entity.Property(e => e.Created).HasColumnName("created_utc");
            entity.Property(e => e.Data).HasColumnName("state_json");
        });

        modelBuilder.Entity<Event>(entity =>
        {
            entity.ToTable("event_log");
            entity.Property(e => e.Id).HasColumnName("event_id");
            entity.Property(e => e.StreamId).HasColumnName("stream_name");
            entity.Property(e => e.OccurredAt).HasColumnName("created_utc");
            entity.Property(e => e.EventType).HasColumnName("type_name");
            entity.Property(e => e.Data).HasColumnName("payload");
        });
    }

    private sealed class DatabaseFirstCacheKeyFactory : IModelCacheKeyFactory
    {
        public object Create(DbContext context, bool designTime) =>
            (typeof(DatabaseFirstCacheKeyFactory), context.GetType(), designTime);
    }
}
