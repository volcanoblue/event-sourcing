using System.Collections.Immutable;
using System.Text.Json.Serialization;
using VolcanoBlue.EventSourcing.Abstractions;

namespace VolcanoBlue.EventSourcing.Tests;

/// <summary>
/// Entity that is its own snapshot (<see cref="ISnapshot"/>), so SnapshotDbContext can deserialize it directly.
/// <see cref="EntityVersion"/> is derived from <see cref="EventSourcedEntity.Version"/> (the last applied event).
/// </summary>
public sealed class SnapshotEntity : EventSourcedEntity, ISnapshot
{
    public int EntityVersion
    {
        get => (int)Version - 1;
        set => Version = value + 1;
    }

    public string State { get; set; } = "";

    [JsonIgnore]
    public int ProcessedEvents { get; private set; }

    [JsonIgnore]
    public bool AutoSnapshot { get; set; }

    public void Rename(string name) => RaiseEvent(new TestEvent(Version, name));

    public bool ShouldTakeSnapshot() => AutoSnapshot;

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

/// <summary>Takes snapshots with an off-by-one EntityVersion (Version instead of Version - 1).</summary>
public sealed class WrongVersionSnapshotEntity : EventSourcedEntity, ISnapshot
{
    public int EntityVersion => (int)Version;

    public void Rename(string name) => RaiseEvent(new TestEvent(Version, name));

    public bool ShouldTakeSnapshot() => true;

    protected override void ProcessEvent(IEvent @event) { }
}
