using System;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace VolcanoBlue.EventSourcing.Extensions;

public static class EventSourcingExtensions
{
    /// <summary>
    /// Registers <see cref="EventSourcingOptions"/> (singleton, configured by <paramref name="configure"/>) and the
    /// scoped <see cref="EventStore"/>. The <see cref="EventStoreDbContext"/> is registered by the caller with
    /// <c>AddDbContext</c>. Prefer this overload for trimming and Native AOT: register events with
    /// <c>AddEvent&lt;T&gt;()</c> and set <c>TypeInfoResolver</c>.
    /// </summary>
    public static IServiceCollection AddEventSourcing(this IServiceCollection services,
        Action<EventSourcingOptions> configure)
    {
        var options = new EventSourcingOptions();
        configure(options);

        services.AddSingleton(options);
        services.AddScoped<EventStore>();

        return services;
    }

    /// <summary>
    /// Registers <see cref="EventStore"/> and the events found in <paramref name="eventAssemblies"/>
    /// (the calling assembly when none is given). Scans assemblies, so it is not trimming friendly.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    [RequiresUnreferencedCode("Scans assemblies for event types; use the overload with AddEvent<T>() when trimming.")]
    public static IServiceCollection AddEventSourcing(this IServiceCollection services,
        params Assembly[] eventAssemblies)
    {
        var assemblies = eventAssemblies.Length > 0 ? eventAssemblies : [Assembly.GetCallingAssembly()];
        return services.AddEventSourcing(options => options.AddEventsFrom(assemblies));
    }
}
