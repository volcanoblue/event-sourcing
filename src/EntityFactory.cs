using System;
using System.Linq.Expressions;
using System.Reflection;
using VolcanoBlue.EventSourcing.Abstractions;

namespace VolcanoBlue.EventSourcing;

/// <summary>
/// Per-type factory: the constructor is resolved and compiled once (static initializer is thread-safe),
/// so each instantiation is a single delegate call, without reflection or per-call allocations.
/// Works with non-public parameterless constructors.
/// </summary>
internal static class EntityFactory<T> where T : EventSourcedEntity
{
    public static readonly Func<T> Create = Build();

    private static Func<T> Build()
    {
        var ctor = typeof(T).GetConstructor(
            BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, Type.EmptyTypes, null);

        if (ctor is null)
        {
            return static () => throw new InvalidOperationException(
                $"{typeof(T).Name} needs a parameterless constructor to be loaded from events.");
        }

        return Expression.Lambda<Func<T>>(Expression.New(ctor)).Compile();
    }
}