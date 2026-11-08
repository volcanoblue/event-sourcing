using System;
using System.Diagnostics.CodeAnalysis;
using System.Text.Json;
using VolcanoBlue.EventSourcing.Abstractions;

namespace VolcanoBlue.EventSourcing.Serialization;

/// <summary>
/// [INFRASTRUCTURE - SERIALIZATION] Turns an entity into its snapshot row and back.
/// With a <see cref="EventSourcingOptions.TypeInfoResolver"/> the entity is serialized by System.Text.Json
/// (source generation, AOT friendly); without one it is serialized by its fields, through reflection.
/// </summary>
internal sealed class SnapshotSerializer(JsonSerializerOptions readOptions, JsonSerializerOptions writeOptions, bool useReflection)
{
    private const string ReflectionJustification =
        "Reached only without a TypeInfoResolver, and then only when the runtime supports reflection and dynamic code.";

    private void EnsureReflectionAvailable()
    {
        if (!System.Runtime.CompilerServices.RuntimeFeature.IsDynamicCodeSupported || !JsonSerializer.IsReflectionEnabledByDefault)
        {
            throw new InvalidOperationException(
                "Snapshots need reflection, which this runtime does not allow (Native AOT or trimming). " +
                "Set EventSourcingOptions.TypeInfoResolver to a source-generated JsonSerializerContext covering the entity.");
        }
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = ReflectionJustification)]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = ReflectionJustification)]
    public Snapshot Serialize(string entityId, ISnapshot snapshot, string created)
    {
        if (snapshot is not EventSourcedEntity entity)
            throw new ArgumentException($"{snapshot.GetType().Name} implements ISnapshot but is not an EventSourcedEntity.", nameof(snapshot));

        string data;
        if (useReflection)
        {
            EnsureReflectionAvailable();
            data = EntityStateSerializer.Serialize(entity);
        }
        else
        {
            data = JsonSerializer.Serialize(entity, writeOptions.GetTypeInfo(entity.GetType()));
        }

        return new Snapshot(entityId, snapshot.EntityVersion, created, data);
    }

    [UnconditionalSuppressMessage("Trimming", "IL2026", Justification = ReflectionJustification)]
    [UnconditionalSuppressMessage("AOT", "IL3050", Justification = ReflectionJustification)]
    public T Deserialize<T>(Snapshot row) where T : EventSourcedEntity
    {
        if (useReflection)
        {
            EnsureReflectionAvailable();
            return EntityStateSerializer.Deserialize<T>(row.Data, row.EntityVersion);
        }

        var entity = (T?)JsonSerializer.Deserialize(row.Data, readOptions.GetTypeInfo(typeof(T)))
                     ?? throw new JsonException($"The snapshot of '{row.EntityId}' is null.");
        entity.RestoreVersion(row.EntityVersion);
        return entity;
    }
}
