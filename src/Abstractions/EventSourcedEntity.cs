using System.Collections.Generic;
using System.Collections.Immutable;

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
        public long Version { get; protected set; } = 1;
        private readonly List<IEvent> _uncommittedEvents = [];

        protected EventSourcedEntity() =>
            UncommittedEvents = _uncommittedEvents.AsReadOnly();

        public IReadOnlyList<IEvent> UncommittedEvents { get; }

        protected void RaiseEvent<TEvent>(TEvent @event) where TEvent : IEvent
        {
            Version++;
            _uncommittedEvents.Add(@event);
            try
            {
                ProcessEvent(@event);
            }
            catch
            {
                // A rejected event must not stay queued nor consume a version.
                _uncommittedEvents.RemoveAt(_uncommittedEvents.Count - 1);
                Version--;
                throw;
            }
        }

        public void Rehydrate(ImmutableArray<IEvent> stream)
        {
            for (var i = 0; i < stream.Length; i++)
            {
                var @event = stream[i];
                Version = @event.EventId;
                ProcessEvent(@event);
            }

            Version++;
        }

        /// <summary>
        /// Continues from a snapshot: sets <c>Version</c> to the snapshot version and replays the later events.
        /// Without later events <c>Version</c> ends at <paramref name="snapshotVersion"/> + 1.
        /// </summary>
        internal void RehydrateFromSnapshot(long snapshotVersion, ImmutableArray<IEvent> stream)
        {
            Version = snapshotVersion;
            Rehydrate(stream);
        }

        internal void RestoreVersion(long version) => Version = version;

        protected abstract void ProcessEvent(IEvent @event);

        public void CommitEvents() =>
            _uncommittedEvents.Clear();
    }
}
