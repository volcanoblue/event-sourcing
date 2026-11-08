using System.Reflection;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.DependencyInjection;
using VolcanoBlue.EventSourcing;
using VolcanoBlue.EventSourcing.Serialization;

namespace VolcanoBlue.EventSourcing.Extensions;

public static class EventSourcingExtensions
{
    /// <summary>
    /// Registers <see cref="EventStore"/> and the event types found in <paramref name="eventAssemblies"/>
    /// (the calling assembly when none is given).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    public static IServiceCollection AddEventSourcing(this IServiceCollection services,
        params Assembly[] eventAssemblies)
    {
        EventTypesRegistry.Initialize(eventAssemblies.Length > 0 ? eventAssemblies : [Assembly.GetCallingAssembly()]);
        services.AddScoped<EventStore>();
        
        return services;
    }
}