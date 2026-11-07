using System.Collections.Immutable;
using VolcanoBlue.EventSourcing.Abstractions;

namespace VolcanoBlue.EventSourcing.Tests;

/// <summary>
/// Minimal event-sourced entity: every <see cref="Rename"/> raises a <see cref="TestEvent"/>
/// whose EventId is the entity version at the time of the change.
/// </summary>
public sealed class TestEntity : EventSourcedEntity
{
    public string StreamId { get; }
    public string? CurrentName { get; private set; }
    public List<string> History { get; } = [];

    public TestEntity(string id) => StreamId = $"Test-{id}";

    public static TestEntity Rehydrate(string id, ImmutableArray<IEvent> stream)
    {
        var entity = new TestEntity(id);

        // FeedEvents always bumps Version, so an empty stream must be left at the initial version.
        if (!stream.IsEmpty)
            entity.Rehydrate(stream);

        return entity;
    }

    public void Rename(string name)
    {
        if (string.IsNullOrWhiteSpace(name))
            throw new ArgumentException("Name is required.", nameof(name));

        if (name == CurrentName)
            return;

        RaiseEvent(new TestEvent(Version, name));
    }

    protected override void ProcessEvent(IEvent @event)
    {
        switch (@event)
        {
            case TestEvent e:
                CurrentName = e.Name;
                History.Add(e.Name);
                break;
            default:
                throw new InvalidOperationException($"Unsupported event {@event.GetType().Name}.");
        }
    }
}
