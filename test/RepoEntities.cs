using System.Collections.Immutable;
using System.Text.Json.Serialization;
using VolcanoBlue.EventSourcing.Abstractions;

namespace VolcanoBlue.EventSourcing.Tests;

/// <summary>
/// Entity that is its own snapshot (<see cref="ISnapshot"/>): EventStore restores it from the snapshot row.
/// <see cref="EntityVersion"/> is derived from <see cref="EventSourcedEntity.Version"/> (the last applied event).
/// </summary>
public sealed class SnapshotEntity : EventSourcedEntity, ISnapshot
{
    public long EntityVersion
    {
        get => Version;
        set => Version = value;
    }

    public string State { get; set; } = "";

    [JsonIgnore]
    public int ProcessedEvents { get; private set; }

    [JsonIgnore]
    public bool AutoSnapshot { get; set; }

    public void Rename(string name) => RaiseEvent(new TestEvent(NextEventId, name));

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

    public void Rename(string name) => RaiseEvent(new TestEvent(NextEventId, name));

    protected override void ProcessEvent(IEvent @event) => State = ((TestEvent)@event).Name;
}

public sealed class FieldEvent : IEvent
{
    public long EventId { get; set; }
    public string Note = "";
}

/// <summary>Takes snapshots with an off-by-one EntityVersion (Version - 1 instead of Version).</summary>
public sealed class WrongVersionSnapshotEntity : EventSourcedEntity, ISnapshot
{
    public long EntityVersion => Version - 1;

    public void Rename(string name) => RaiseEvent(new TestEvent(NextEventId, name));

    public bool ShouldTakeSnapshot() => true;

    protected override void ProcessEvent(IEvent @event) { }
}

/// <summary>No public constructor, no setters, readonly and private fields: state is only changed through events.</summary>
public sealed class EncapsulatedEntity : EventSourcedEntity, ISnapshot
{
    private readonly List<string> _names = [];
    private int _renames;

    public string Last { get; private set; } = "";

    [JsonIgnore]
    public int Skipped { get; private set; } = 7;

    public IReadOnlyList<string> Names => _names;
    public int Renames => _renames;
    public long EntityVersion => Version;

    private EncapsulatedEntity() { }

    public static EncapsulatedEntity Create() => new();

    public void Rename(string name) => RaiseEvent(new TestEvent(NextEventId, name));

    public bool ShouldTakeSnapshot() => true;

    protected override void ProcessEvent(IEvent @event)
    {
        var name = ((TestEvent)@event).Name;
        _names.Add(name);
        _renames++;
        Last = name;
        Skipped = 0;
    }
}

public sealed class EventIdEntity : EventSourcedEntity
{
    public void RaiseWith(long eventId) => RaiseEvent(new TestEvent(eventId, "x"));

    protected override void ProcessEvent(IEvent @event) { }
}

[EventName("tests.named-event")]
public sealed record NamedEvent(long EventId, string Name) : IEvent;

/// <summary>Closed forms of this generic share one [EventName], which makes them collide.</summary>
[EventName("tests.dup")]
public sealed record DupEvent<T>(long EventId, T Value) : IEvent;

/// <summary>Snapshot state that cannot be serialized.</summary>
public sealed class UnserializableSnapshotEntity : EventSourcedEntity, ISnapshot
{
    private Action _callback = () => { };

    public long EntityVersion => Version;

    public void Rename(string name) => RaiseEvent(new TestEvent(NextEventId, name));

    public bool ShouldTakeSnapshot() => true;

    protected override void ProcessEvent(IEvent @event) => _callback();
}
