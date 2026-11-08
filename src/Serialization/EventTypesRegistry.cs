using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using VolcanoBlue.EventSourcing.Abstractions;

namespace VolcanoBlue.EventSourcing.Serialization
{
    /// <summary>
    /// [INFRASTRUCTURE - TYPE REGISTRY] Cached registry for event type mappings.
    /// Architectural Role: Scans assemblies once at startup to build type cache.
    /// Provides fast O(1) type lookup during event deserialization.
    /// </summary>
    public static class EventTypesRegistry
    {
        private static readonly ConcurrentDictionary<string, Type> _types = [];
        private static readonly object InitLock = new();
        private static readonly HashSet<Assembly> Scanned = [];

        /// <summary>Scans each assembly once; calling it again with new assemblies adds their events.</summary>
        public static void Initialize(params Assembly[] assemblies)
        {
            lock (InitLock)
            {
                foreach (var assembly in assemblies)
                {
                    if (!Scanned.Add(assembly))
                        continue;

                    var eventTypes = LoadableTypes(assembly)
                        .Where(t => typeof(IEvent).IsAssignableFrom(t) && !t.IsInterface && !t.IsAbstract);

                    foreach (var type in eventTypes)
                        _types.TryAdd(type.FullName!, type);
                }
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

        public static Type GetType(string typeName)
        {
            if (_types.TryGetValue(typeName, out var type))
                return type;

            throw new InvalidOperationException(
                $"Event type '{typeName}' not found. Ensure EventTypesRegistry.Initialize() is called with correct assemblies.");
        }
    }
}