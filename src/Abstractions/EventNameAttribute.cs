using System;

namespace VolcanoBlue.EventSourcing.Abstractions;

/// <summary>
/// Stable name under which an event type is stored, instead of its full CLR name. Renaming or moving the type then
/// does not break reading existing events. Events stored under the full name remain readable.
/// </summary>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Struct, AllowMultiple = false, Inherited = false)]
public sealed class EventNameAttribute(string name) : Attribute
{
    public string Name { get; } = name;
}
