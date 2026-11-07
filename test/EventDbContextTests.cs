using Microsoft.EntityFrameworkCore;

namespace VolcanoBlue.EventSourcing.Tests;

public class EventDbContextTests : IDisposable
{
    private readonly SqliteFixture _fixture = new();

    public void Dispose() => _fixture.Dispose();

    [Fact]
    public async Task ReadStream_UnknownStream_ReturnsEmpty()
    {
        using var store = _fixture.CreateEventStore();

        var result = await store.ReadStreamAsync("Missing-1");

        Assert.Empty(result);
    }

    [Fact]
    public async Task AppendAndRead_RoundTripsEventsInOrder()
    {
        using (var writer = _fixture.CreateEventStore())
        {
            await writer.AppendToStreamAsync("Test-1", [new TestEvent(2, "second"), new TestEvent(1, "first")]);
            Assert.Equal(2, await writer.SaveStreamAsync());
        }

        using var reader = _fixture.CreateEventStore();
        var result = await reader.ReadStreamAsync("Test-1");

        var events = result.Cast<TestEvent>().ToArray();
        Assert.Equal(["first", "second"], events.Select(e => e.Name));
    }

    [Fact]
    public async Task ReadStream_IsolatesStreams()
    {
        using (var writer = _fixture.CreateEventStore())
        {
            await writer.AppendToStreamAsync("Test-A", [new TestEvent(1, "a")]);
            await writer.AppendToStreamAsync("Test-B", [new TestEvent(1, "b")]);
            await writer.SaveStreamAsync();
        }

        using var reader = _fixture.CreateEventStore();
        var result = await reader.ReadStreamAsync("Test-B");

        var single = Assert.Single(result);
        Assert.Equal("b", ((TestEvent)single).Name);
    }

    [Fact]
    public async Task ReadStreamFromEventId_ReturnsOnlyNewerEvents()
    {
        using (var writer = _fixture.CreateEventStore())
        {
            await writer.AppendToStreamAsync("Test-1", [new TestEvent(1, "a"), new TestEvent(2, "b"), new TestEvent(3, "c")]);
            await writer.SaveStreamAsync();
        }

        using var reader = _fixture.CreateEventStore();
        var result = await reader.ReadStreamFromEventIdAsync("Test-1", 1);

        Assert.Equal([2L, 3L], result.Select(e => e.EventId));
    }

    [Fact]
    public async Task Save_DuplicateEventId_Throws()
    {
        using (var writer = _fixture.CreateEventStore())
        {
            await writer.AppendToStreamAsync("Test-1", [new TestEvent(1, "a")]);
            await writer.SaveStreamAsync();
        }

        using var second = _fixture.CreateEventStore();
        await second.AppendToStreamAsync("Test-1", [new TestEvent(1, "dup")]);

        await Assert.ThrowsAsync<DbUpdateException>(() => second.SaveStreamAsync());
    }
}
