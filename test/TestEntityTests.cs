using Microsoft.EntityFrameworkCore;

namespace VolcanoBlue.EventSourcing.Tests;

public class TestEntityTests : IDisposable
{
    private readonly SqliteFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    private async Task<TestEntity> LoadAsync(string id)
    {
        using var store = _fixture.CreateEventStore();
        var result = await store.ReadStreamAsync($"Test-{id}");
        return TestEntity.Rehydrate(id, result);
    }

    private async Task SaveAsync(TestEntity entity)
    {
        using var store = _fixture.CreateEventStore();
        await store.AppendToStreamAsync(entity.StreamId, entity.UncommittedEvents);
        await store.SaveStreamAsync();
        entity.CommitEvents();
    }

    [Fact]
    public void Rename_RaisesEventsWithSequentialIds()
    {
        var entity = new TestEntity("1");

        entity.Rename("a");
        entity.Rename("b");

        Assert.Equal([1L, 2L], entity.UncommittedEvents.Select(e => e.EventId));
        Assert.Equal(3, entity.Version);
        Assert.Equal("b", entity.CurrentName);
    }

    [Fact]
    public void Rename_SameName_RaisesNothing()
    {
        var entity = new TestEntity("1");
        entity.Rename("a");

        entity.Rename("a");

        Assert.Single(entity.UncommittedEvents);
    }

    [Fact]
    public async Task SaveAndLoad_RebuildsStateAndVersion()
    {
        var entity = new TestEntity("1");
        entity.Rename("first");
        entity.Rename("second");
        await SaveAsync(entity);

        var loaded = await LoadAsync("1");

        Assert.Equal("second", loaded.CurrentName);
        Assert.Equal(["first", "second"], loaded.History);
        Assert.Equal(entity.Version, loaded.Version);
        Assert.Empty(loaded.UncommittedEvents);
    }

    [Fact]
    public async Task Load_UnknownStream_ReturnsFreshEntity()
    {
        var loaded = await LoadAsync("missing");

        Assert.Null(loaded.CurrentName);
        Assert.Equal(1, loaded.Version);
    }

    [Fact]
    public async Task LoadModifySave_AppendsOnlyNewEvents()
    {
        var entity = new TestEntity("1");
        entity.Rename("a");
        await SaveAsync(entity);

        var loaded = await LoadAsync("1");
        loaded.Rename("b");
        Assert.Equal([2L], loaded.UncommittedEvents.Select(e => e.EventId));
        await SaveAsync(loaded);

        var final = await LoadAsync("1");
        Assert.Equal(["a", "b"], final.History);
        Assert.Equal(3, final.Version);
    }

    [Fact]
    public async Task ConcurrentWriters_SecondSaveFails()
    {
        var seed = new TestEntity("1");
        seed.Rename("a");
        await SaveAsync(seed);

        var first = await LoadAsync("1");
        var second = await LoadAsync("1");
        first.Rename("from-first");
        second.Rename("from-second");
        await SaveAsync(first);

        using var store = _fixture.CreateEventStore();
        await store.AppendToStreamAsync(second.StreamId, second.UncommittedEvents);

        await Assert.ThrowsAsync<DbUpdateException>(() => store.SaveStreamAsync());
        Assert.Equal(["a", "from-first"], (await LoadAsync("1")).History);
    }
}
