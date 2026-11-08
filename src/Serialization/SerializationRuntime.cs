using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.Json;

namespace VolcanoBlue.EventSourcing.Serialization;

/// <summary>
/// Everything <see cref="EventStore"/> needs at run time, built once from <see cref="EventSourcingOptions"/>:
/// the serializers (and their JSON metadata caches, which are per options instance) and the clock.
/// </summary>
internal sealed class SerializationRuntime
{
    public SerializationRuntime(IEnumerable<Type> eventTypes, TimeProvider timeProvider)
    {
        TimeProvider = timeProvider;

        var read = new JsonSerializerOptions
        {
            IncludeFields = true,
            PropertyNameCaseInsensitive = true,
            Converters = { new PrivateConstructorConverter() }
        };
        // Separate from read: PrivateConstructorConverter.Write re-enters the serializer with the same options.
        var write = new JsonSerializerOptions { IncludeFields = true };

        Events = new EventSerializer(new EventTypeRegistry(eventTypes), read, write);
        Snapshots = new SnapshotSerializer();
    }

    public TimeProvider TimeProvider { get; }

    public EventSerializer Events { get; }

    public SnapshotSerializer Snapshots { get; }

    /// <summary>UTC timestamp with sub-second precision; still sorts as text and mixes with rows stored without the fraction.</summary>
    public string Timestamp() =>
        TimeProvider.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);
}
