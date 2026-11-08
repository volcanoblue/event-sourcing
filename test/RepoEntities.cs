using System.Collections.Immutable;
using System.Text.Json.Serialization;
using VolcanoBlue.EventSourcing.Abstractions;

namespace VolcanoBlue.EventSourcing.Tests;

/// <summary>
/// Entity that is its own snapshot (<see cref="ISnapshot"/>), so SnapshotDbContext can deserialize it directly.
/// <see cref="EntityVersion"/> is the event version captured by <see cref="MarkSnapshot"/>.
/// </summary>
public sealed class SnapshotEntity : EventSourcedEntity, ISnapshot, ISnapshotable<SnapshotEntity>
{
    public int EntityVersion { get; set; }
    public string State { get; set; } = "";

    [JsonIgnore]
    public int ProcessedEvents { get; private set; }

    public void Rename(string name) => RaiseEvent(new TestEvent(Version, name));

    public SnapshotEntity MarkSnapshot()
    {
        EntityVersion = (int)Version - 1;
        return this;
    }

    public bool ShouldTakeSnapshot() => true;

    public SnapshotEntity TakeSnapshot() => MarkSnapshot();

    protected override void ProcessEvent(IEvent @event)
    {
        var name = ((TestEvent)@event).Name;
        State = State.Length == 0 ? name : $"{State},{name}";
        ProcessedEvents++;
    }
}

public sealed class PrivateCtorEntity : EventSourcedEntity
{
    public string State { get; private set; } = "";

    private PrivateCtorEntity() { }

    public static PrivateCtorEntity Create() => new();

    public void Rename(string name) => RaiseEvent(new TestEvent(Version, name));

    protected override void ProcessEvent(IEvent @event) => State = ((TestEvent)@event).Name;
}

public sealed class FieldEvent : IEvent
{
    public long EventId { get; set; }
    public string Note = "";
}
