using System.Collections.Generic;
using System.Collections.Immutable;
using System.Text.Json.Serialization;

namespace VolcanoBlue.EventSourcing.Abstractions
{
    /// <summary>
    /// [INFRASTRUCTURE - EVENT SOURCING BASE] Base class for entities using Event Sourcing.
    /// Architectural Role: Provides infrastructure for applying events and managing entity version.
    /// Maintains collection of uncommitted events that need to be persisted to the event store.
    /// Implements the event application pattern through abstract ProcessEvent method.
    /// Derived classes must implement ProcessEvent to handle their specific domain events.
    /// </summary>
    public abstract class EventSourcedEntity
    {
        [JsonIgnore]
        public long Version { get; protected set; }
        
        protected long NextEventId => Version + 1;

        private readonly List<IEvent> _uncommittedEvents = [];

        protected EventSourcedEntity() =>
            UncommittedEvents = _uncommittedEvents.AsReadOnly();

        [JsonIgnore]
        public IReadOnlyList<IEvent> UncommittedEvents { get; }

        protected void RaiseEvent(IEvent @event)
        {
            Replay(@event);
            _uncommittedEvents.Add(@event);
        }

        public void Rehydrate(ImmutableArray<IEvent> stream)
        {
            for (var i = 0; i < stream.Length; i++)
                Replay(stream[i]);
        }

        internal void Replay(IEvent @event)
        {
            Version = @event.EventId;
            ProcessEvent(@event);
        }

        internal void RestoreVersion(long version) => Version = version;

        protected abstract void ProcessEvent(IEvent @event);

        public void CommitEvents() =>
            _uncommittedEvents.Clear();
    }
}
