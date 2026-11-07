namespace VolcanoBlue.EventSourcing.Tests;

public class SnapshotDbContextTests : IDisposable
{
    private readonly SqliteFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ById_Missing_ReturnsNone()
    {
        using var store = _fixture.CreateSnapshotStore();

        var result = await store.ByIdAsync<TestSnapshot>("nope");

        Assert.False((bool)result);
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

        Assert.True((bool)result);
        Assert.Equal(new TestSnapshot(3, "state"), result.Get());
    }
}
