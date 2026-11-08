using System.Text.Json.Serialization;
using Microsoft.EntityFrameworkCore;
using VolcanoBlue.EventSourcing.Abstractions;

namespace VolcanoBlue.EventSourcing.Tests;

/// <summary>
/// The Native AOT path: no reflection over the entity, only System.Text.Json source-generated metadata.
/// The generator cannot see private members, so persisted state is `internal` + [JsonInclude] and the constructor
/// is an `internal` [JsonConstructor]; the entity and its context live in the same assembly.
/// </summary>
public sealed class SourceGenEntity : EventSourcedEntity, ISnapshot
{
    [JsonInclude] internal List<string> _names = [];
    [JsonInclude] internal int _renames;

    [JsonInclude] public string Last { get; internal set; } = "";

    [JsonIgnore] public IReadOnlyList<string> Names => _names;
    [JsonIgnore] public int Renames => _renames;
    [JsonIgnore] public long EntityVersion => Version;

    [JsonConstructor]
    internal SourceGenEntity() { }

    public static SourceGenEntity Create() => new();

    public void Rename(string name) => RaiseEvent(new TestEvent(NextEventId, name));

    public bool ShouldTakeSnapshot() => true;

    protected override void ProcessEvent(IEvent @event)
    {
        var name = ((TestEvent)@event).Name;
        _names.Add(name);
        _renames++;
        Last = name;
    }
}

[JsonSourceGenerationOptions(IncludeFields = true)]
[JsonSerializable(typeof(SourceGenEntity))]
[JsonSerializable(typeof(TestEvent))]
internal sealed partial class TestJsonContext : JsonSerializerContext;

public sealed class SourceGenTests : IDisposable
{
    private readonly DatabaseFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private static EventSourcingOptions NewOptions() => new EventSourcingOptions
    {
        TypeInfoResolver = TestJsonContext.Default
    }.AddEvent<TestEvent>();

    [Fact]
    public async Task SaveAndLoad_WithSourceGeneratedMetadata_RoundTripsPrivateState()
    {
        var options = NewOptions();
        using var db = _fixture.CreateContext();
        var store = _fixture.CreateStore(db, options);

        var entity = SourceGenEntity.Create();
        entity.Rename("a");
        entity.Rename("b");
        await store.SaveAsync("Gen-1", entity);

        var loaded = (await store.LoadAsync<SourceGenEntity>("Gen-1"))!;

        Assert.Equal(["a", "b"], loaded.Names);
        Assert.Equal(2, loaded.Renames);
        Assert.Equal("b", loaded.Last);
        Assert.Equal(2, loaded.Version);

        loaded.Rename("c");
        await store.SaveAsync("Gen-1", loaded);
        Assert.Equal(["a", "b", "c"], (await store.LoadAsync<SourceGenEntity>("Gen-1"))!.Names);
    }

    [Fact]
    public async Task Snapshot_IsWrittenByTheSerializerContext_NotByFieldReflection()
    {
        var options = NewOptions();
        using var db = _fixture.CreateContext();
        var store = _fixture.CreateStore(db, options);

        var entity = SourceGenEntity.Create();
        entity.Rename("a");
        await store.SaveAsync("Gen-2", entity);

        // Field reflection would write the backing field name of Last; the generated metadata writes the property.
        var data = db.Snapshots.Single(s => s.EntityId == "Gen-2").Data;
        Assert.Contains("\"Last\":\"a\"", data);
        Assert.Contains("\"_names\":[\"a\"]", data);
        Assert.DoesNotContain("Version", data);
        Assert.DoesNotContain("UncommittedEvents", data);
    }

    [Fact]
    public async Task LoadAsync_WithAnUnreadableSnapshot_ReplaysTheStream()
    {
        var options = NewOptions();
        using var db = _fixture.CreateContext();
        var store = _fixture.CreateStore(db, options);

        var entity = SourceGenEntity.Create();
        entity.Rename("a");
        await store.SaveAsync("Gen-3", entity);

        await db.Database.ExecuteSqlRawAsync("UPDATE \"Snapshots\" SET \"Data\" = 'not json'");
        var loaded = (await store.LoadAsync<SourceGenEntity>("Gen-3"))!;

        Assert.Equal(["a"], loaded.Names);
    }
}
