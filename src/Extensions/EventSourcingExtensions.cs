using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using VolcanoBlue.EventSourcing.EventStore.Serialization;

namespace VolcanoBlue.EventSourcing.Extensions;

public static class EventSourcingExtensions
{
    public static IServiceCollection AddEventSourcing(this IServiceCollection services)
    {
        EventTypesRegistry.Initialize(Assembly.GetExecutingAssembly());
        
        return services;
    }
}