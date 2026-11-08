using System;
using System.Collections.Generic;
using System.Reflection;
using VolcanoBlue.EventSourcing.Abstractions;

namespace VolcanoBlue.EventSourcing.Serialization;

/// <summary>
/// [INFRASTRUCTURE - TYPE REGISTRY] Maps the name stored with each event to its CLR type and back.
/// Built once from <see cref="EventSourcingOptions"/> and immutable afterwards, so lookups need no locking.
/// </summary>
internal sealed class EventTypeRegistry
{
    private readonly Dictionary<string, Type> _types = [];
    private readonly Dictionary<Type, string> _names = [];

    public EventTypeRegistry(IEnumerable<Type> eventTypes)
    {
        foreach (var type in eventTypes)
        {
            if (_names.ContainsKey(type))
                continue;

            // The stable name first; the full name stays readable for events stored before the attribute.
            var name = type.GetCustomAttribute<EventNameAttribute>()?.Name ?? type.FullName!;
            Register(name, type);
            _names[type] = name;
            if (name != type.FullName)
                _types.TryAdd(type.FullName!, type);
        }
    }

    private void Register(string name, Type type)
    {
        if (!_types.TryAdd(name, type) && _types[name] != type)
        {
            throw new InvalidOperationException(
                $"Event type name '{name}' is used by '{_types[name].FullName}' and '{type.FullName}'. " +
                "Stored events identify their type by name, so names must be unique.");
        }
    }

    /// <summary>Name an event type is stored under: its <see cref="EventNameAttribute"/>, else its full name.</summary>
    public string NameOf(Type type) =>
        _names.TryGetValue(type, out var name)
            ? name
            : throw new InvalidOperationException(
                $"Event type '{type.FullName}' is not registered. Call AddEvent<{type.Name}>() or AddEventsFrom(assembly) on EventSourcingOptions.");

    public Type GetType(string typeName) =>
        _types.TryGetValue(typeName, out var type)
            ? type
            : throw new InvalidOperationException(
                $"Event type '{typeName}' not found. Register it on EventSourcingOptions (AddEvent<T>() or AddEventsFrom(assembly)).");
}
