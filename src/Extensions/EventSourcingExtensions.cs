using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;

namespace VolcanoBlue.EventSourcing.Extensions;

public static class EventSourcingExtensions
{
    /// <summary>
    /// Registers <see cref="EventSourcingOptions"/> (singleton, configured by <paramref name="configure"/>) and the
    /// scoped <see cref="EventStore"/>. The <see cref="EventStoreDbContext"/> is registered by the caller with
    /// <c>AddDbContext</c>.
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
    /// (the calling assembly when none is given).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IServiceCollection AddEventSourcing(this IServiceCollection services,
        params Assembly[] eventAssemblies)
    {
        var assemblies = eventAssemblies.Length > 0 ? eventAssemblies : [Assembly.GetCallingAssembly()];
        return services.AddEventSourcing(options => options.AddEventsFrom(assemblies));
    }
}
