using System;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;
using System.Runtime.CompilerServices;
using VolcanoBlue.EventSourcing.Abstractions;

namespace VolcanoBlue.EventSourcing;

/// <summary>
/// Per-type factory: the constructor is resolved once (static initializer is thread-safe).
/// With a JIT it is compiled, so each instantiation is a single delegate call; without dynamic code (Native AOT)
/// it falls back to invoking the constructor. Works with non-public parameterless constructors.
/// </summary>
internal static class EntityFactory<[DynamicallyAccessedMembers(
    DynamicallyAccessedMemberTypes.PublicConstructors | DynamicallyAccessedMemberTypes.NonPublicConstructors)] T>
    where T : EventSourcedEntity
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

        if (RuntimeFeature.IsDynamicCodeSupported)
            return Expression.Lambda<Func<T>>(Expression.New(ctor)).Compile();

        return () => (T)ctor.Invoke(null);
    }
}
