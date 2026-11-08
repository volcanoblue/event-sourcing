using System;
using System.Collections.Concurrent;
using System.Buffers;
using System.Collections.Generic;
using System.Linq.Expressions;
using System.Reflection;
using System.Reflection.Emit;
using System.Text.Json;
using System.Text.Json.Serialization;
using VolcanoBlue.EventSourcing.Abstractions;

namespace VolcanoBlue.EventSourcing.Serialization;

/// <summary>
/// Serializes an entity by its instance fields (including private ones and auto-property backing fields),
/// so the entity needs no public setters nor a public constructor. Fields declared by
/// <see cref="EventSourcedEntity"/> are skipped: <c>Version</c> is restored from the snapshot row.
/// Honors <see cref="JsonIgnoreAttribute"/> on a property (for its backing field) and <see cref="NonSerializedAttribute"/>.
/// Auto-property backing fields are written under the property name.
/// </summary>
internal static class EntityStateSerializer
{
    private static readonly ConcurrentDictionary<Type, TypeState> Cache = [];

    public static bool Handles(Type type) => typeof(EventSourcedEntity).IsAssignableFrom(type);

    public static string Serialize(object entity)
    {
        var state = Cache.GetOrAdd(entity.GetType(), static t => new TypeState(t));
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach (var field in state.Fields)
            {
                writer.WritePropertyName(field.Name);
                JsonSerializer.Serialize(writer, field.Get(entity), field.Type);
            }
            writer.WriteEndObject();
        }

        return System.Text.Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    public static T Deserialize<T>(string json, long entityVersion)
    {
        var state = Cache.GetOrAdd(typeof(T), static t => new TypeState(t));
        var entity = state.Create();

        using var doc = JsonDocument.Parse(json);
        foreach (var field in state.Fields)
        {
            if (doc.RootElement.TryGetProperty(field.Name, out var value))
                field.Set(entity, value.Deserialize(field.Type));
        }

        ((EventSourcedEntity)entity).RestoreVersion(entityVersion + 1);
        return (T)entity;
    }

    // Accessors are emitted once per field; unlike FieldInfo.SetValue they cost a plain delegate call,
    // and unlike expression trees they can assign readonly fields.
    private sealed record StateField(string Name, Type Type, Func<object, object?> Get, Action<object, object?> Set)
    {
        public static StateField Create(string name, FieldInfo info)
        {
            var owner = info.DeclaringType!;

            var getter = new DynamicMethod("get_" + info.Name, typeof(object), [typeof(object)], owner, skipVisibility: true);
            var il = getter.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, owner);
            il.Emit(OpCodes.Ldfld, info);
            if (info.FieldType.IsValueType)
                il.Emit(OpCodes.Box, info.FieldType);
            il.Emit(OpCodes.Ret);

            var setter = new DynamicMethod("set_" + info.Name, typeof(void), [typeof(object), typeof(object)], owner, skipVisibility: true);
            il = setter.GetILGenerator();
            il.Emit(OpCodes.Ldarg_0);
            il.Emit(OpCodes.Castclass, owner);
            il.Emit(OpCodes.Ldarg_1);
            il.Emit(info.FieldType.IsValueType ? OpCodes.Unbox_Any : OpCodes.Castclass, info.FieldType);
            il.Emit(OpCodes.Stfld, info);
            il.Emit(OpCodes.Ret);

            return new StateField(name, info.FieldType,
                (Func<object, object?>)getter.CreateDelegate(typeof(Func<object, object?>)),
                (Action<object, object?>)setter.CreateDelegate(typeof(Action<object, object?>)));
        }
    }

    private sealed class TypeState
    {
        public readonly StateField[] Fields;
        public readonly Func<object> Create;

        public TypeState(Type type)
        {
            var ctor = type.GetConstructor(
                BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null)
                ?? throw new InvalidOperationException($"{type.Name} needs a parameterless constructor to be restored from a snapshot.");
            Create = Expression.Lambda<Func<object>>(Expression.Convert(Expression.New(ctor), typeof(object))).Compile();

            var fields = new List<StateField>();
            for (var t = type; t is not null && t != typeof(EventSourcedEntity); t = t.BaseType)
            {
                foreach (var info in t.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly))
                {
                    if (info.GetCustomAttribute<NonSerializedAttribute>() is not null)
                        continue;

                    var name = info.Name;
                    if (name.Length > 0 && name[0] == '<' && name.EndsWith(">k__BackingField", StringComparison.Ordinal))
                    {
                        name = name[1..name.IndexOf('>')];
                        var property = t.GetProperty(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly);
                        if (property?.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
                            continue;
                    }
                    else if (info.GetCustomAttribute<JsonIgnoreAttribute>() is not null)
                        continue;

                    fields.Add(StateField.Create(name, info));
                }
            }

            Fields = [.. fields];
        }
    }
}
