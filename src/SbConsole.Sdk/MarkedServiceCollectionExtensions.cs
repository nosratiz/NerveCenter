using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace SbConsole.Sdk;

public static class MarkedServiceCollectionExtensions
{
    private static readonly Dictionary<Type, ServiceLifetime> Markers = new()
    {
        [typeof(IScopedService)] = ServiceLifetime.Scoped,
        [typeof(ISingletonService)] = ServiceLifetime.Singleton,
        [typeof(ITransientService)] = ServiceLifetime.Transient,
    };

    /// <summary>
    /// Registers every concrete class in <paramref name="assembly"/> that implements a lifetime
    /// marker (<see cref="IScopedService"/>, <see cref="ISingletonService"/>,
    /// <see cref="ITransientService"/>). Hosts call it for Core and Web; each plugin calls it for its
    /// own assembly from <see cref="IPlugin.ConfigureServices"/>.
    /// </summary>
    public static IServiceCollection AddMarkedServices(this IServiceCollection services, Assembly assembly) =>
        services.AddMarkedServices(assembly.GetTypes());

    /// <summary>
    /// Registers each marked class in <paramref name="types"/>. The service types it is exposed as are
    /// its "service interfaces" -- every interface it implements except the markers themselves and
    /// framework (System.* / Microsoft.*) interfaces such as IDisposable:
    /// <list type="bullet">
    /// <item>none (e.g. a query/command handler) -- registered as the class itself;</item>
    /// <item>exactly one (e.g. EfAuditWriter : IAuditWriter) -- registered as that interface only;</item>
    /// <item>several -- the class is registered once and each interface forwards to it, so a
    /// singleton or scoped instance is shared rather than duplicated per interface.</item>
    /// </list>
    /// </summary>
    public static IServiceCollection AddMarkedServices(this IServiceCollection services, IEnumerable<Type> types)
    {
        foreach (var type in types)
        {
            if (!type.IsClass || type.IsAbstract || type.IsGenericTypeDefinition)
            {
                continue;
            }

            var interfaces = type.GetInterfaces();
            var markers = interfaces.Where(Markers.ContainsKey).ToList();
            if (markers.Count == 0)
            {
                continue;
            }

            if (markers.Count > 1)
            {
                throw new InvalidOperationException(
                    $"{type.FullName} implements more than one lifetime marker ({string.Join(", ", markers.Select(m => m.Name))}); pick one.");
            }

            var lifetime = Markers[markers[0]];
            var serviceInterfaces = interfaces.Where(IsServiceInterface).ToList();

            switch (serviceInterfaces.Count)
            {
                case 0:
                    services.Add(new ServiceDescriptor(type, type, lifetime));
                    break;
                case 1:
                    services.Add(new ServiceDescriptor(serviceInterfaces[0], type, lifetime));
                    break;
                default:
                    services.Add(new ServiceDescriptor(type, type, lifetime));
                    foreach (var serviceInterface in serviceInterfaces)
                    {
                        services.Add(new ServiceDescriptor(serviceInterface, sp => sp.GetRequiredService(type), lifetime));
                    }
                    break;
            }
        }

        return services;
    }

    private static bool IsServiceInterface(Type type) =>
        !Markers.ContainsKey(type)
        && type.Namespace is { } ns
        && !ns.StartsWith("System", StringComparison.Ordinal)
        && !ns.StartsWith("Microsoft", StringComparison.Ordinal);
}
