using VolcanoBlue.EventSourcing.EventStore;

namespace VolcanoBlue.EventSourcing.Tests;

public class SnapshotDbContextTests : IDisposable
{
    private readonly SqliteFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ById_Missing_ReturnsSnapshotNotFound()
    {
        using var store = _fixture.CreateSnapshotStore();

        var result = await store.ByIdAsync<TestSnapshot>("nope");

        Assert.True(result.IsError);
        Assert.Same(SnapshotNotFoundError.Instance, result.ErrorValue);
    }

    [Fact]
    public async Task Upsert_ThenById_RoundTrips()
    {
        using (var writer = _fixture.CreateSnapshotStore())
        {
            await writer.UpsertAsync("e1", new TestSnapshot(3, "state"));
            await writer.SaveChangesAsync();
        }

        using var reader = _fixture.CreateSnapshotStore();
        var result = await reader.ByIdAsync<TestSnapshot>("e1");

        Assert.False(result.IsError);
        Assert.Equal(new TestSnapshot(3, "state"), result.ResultValue);
    }
}
