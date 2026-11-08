using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VolcanoBlue.EventSourcing.Abstractions;
using VolcanoBlue.EventSourcing.Serialization;

namespace VolcanoBlue.EventSourcing;

/// <summary>
/// Configuration shared by every <see cref="EventStore"/>: which event types exist and the
/// clock. Build it once at startup (<c>AddEventSourcing</c> registers it as a singleton). After the first
/// <see cref="EventStore"/> uses it, it is frozen and further changes throw.
/// </summary>
public sealed class EventSourcingOptions
{
    private readonly object _lock = new();
    private readonly List<Type> _eventTypes = [];
    private TimeProvider _timeProvider = TimeProvider.System;
    private SerializationRuntime? _runtime;

    /// <summary>Clock used for <c>OccurredAt</c> and <c>Created</c>; <see cref="TimeProvider.System"/> by default.</summary>
    public TimeProvider TimeProvider
    {
        get => _timeProvider;
        set => Configure(() => _timeProvider = value ?? throw new ArgumentNullException(nameof(value)));
    }

    /// <summary>Registers an event type.</summary>
    public EventSourcingOptions AddEvent<TEvent>() where TEvent : IEvent => AddEvent(typeof(TEvent));

    /// <summary>Registers an event type, which must implement <see cref="IEvent"/>.</summary>
    public EventSourcingOptions AddEvent(Type eventType)
    {
        ArgumentNullException.ThrowIfNull(eventType);
        if (!typeof(IEvent).IsAssignableFrom(eventType) || eventType.IsInterface || eventType.IsAbstract)
            throw new ArgumentException($"{eventType.FullName} is not a concrete {nameof(IEvent)}.", nameof(eventType));

        Configure(() => _eventTypes.Add(eventType));
        return this;
    }

    /// <summary>Registers every concrete <see cref="IEvent"/> found in <paramref name="assemblies"/>.</summary>
    public EventSourcingOptions AddEventsFrom(params Assembly[] assemblies)
    {
        foreach (var assembly in assemblies)
        {
            foreach (var type in LoadableTypes(assembly))
            {
                // Open generic definitions cannot be instantiated; their closed forms are registered with AddEvent.
                if (typeof(IEvent).IsAssignableFrom(type) && !type.IsInterface && !type.IsAbstract && !type.ContainsGenericParameters)
                    AddEvent(type);
            }
        }

        return this;
    }

    internal SerializationRuntime Runtime
    {
        get
        {
            lock (_lock)
                return _runtime ??= new SerializationRuntime(_eventTypes, _timeProvider);
        }
    }

    private void Configure(Action change)
    {
        lock (_lock)
        {
            if (_runtime is not null)
                throw new InvalidOperationException("EventSourcingOptions cannot change after an EventStore has used it.");

            change();
        }
    }

    private static Type[] LoadableTypes(Assembly assembly)
    {
        try
        {
            return assembly.GetTypes();
        }
        catch (ReflectionTypeLoadException ex)
        {
            return ex.Types.Where(t => t is not null).ToArray()!;
        }
    }
}
