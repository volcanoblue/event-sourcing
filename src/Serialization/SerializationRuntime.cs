using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace VolcanoBlue.EventSourcing.Serialization;

/// <summary>
/// Everything <see cref="EventStore"/> needs at run time, built once from <see cref="EventSourcingOptions"/>:
/// the serializers (and their JSON metadata caches, which are per options instance) and the clock.
/// </summary>
internal sealed class SerializationRuntime
{
    public SerializationRuntime(IEnumerable<Type> eventTypes, IJsonTypeInfoResolver? resolver, TimeProvider timeProvider)
    {
        TimeProvider = timeProvider;

        var read = new JsonSerializerOptions { IncludeFields = true, PropertyNameCaseInsensitive = true };
        // Separate from read: PrivateConstructorConverter.Write re-enters the serializer with the same options.
        var write = new JsonSerializerOptions { IncludeFields = true };

        if (resolver is not null)
        {
            read.TypeInfoResolver = resolver;
            write.TypeInfoResolver = resolver;
        }
        else
        {
            UseReflection(read, privateConstructors: true);
            UseReflection(write, privateConstructors: false);
        }

        Events = new EventSerializer(new EventTypeRegistry(eventTypes), read, write);
        Snapshots = new SnapshotSerializer(read, write, useReflection: resolver is null);
    }

    public TimeProvider TimeProvider { get; }

    public EventSerializer Events { get; }

    public SnapshotSerializer Snapshots { get; }

    /// <summary>UTC timestamp with sub-second precision; still sorts as text and mixes with rows stored without the fraction.</summary>
    public string Timestamp() =>
        TimeProvider.GetUtcNow().UtcDateTime.ToString("yyyy-MM-dd HH:mm:ss.fffffff", CultureInfo.InvariantCulture);

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = "Only with reflection enabled, which is the default resolver's requirement anyway.")]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = "Skipped when dynamic code is not supported.")]
    private static void UseReflection(JsonSerializerOptions options, bool privateConstructors)
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported || !JsonSerializer.IsReflectionEnabledByDefault)
            return; // GetTypeInfo then reports that no metadata was provided

        options.TypeInfoResolver = new DefaultJsonTypeInfoResolver();
        if (privateConstructors)
            options.Converters.Add(new PrivateConstructorConverter());
    }
}
