namespace SbConsole.Sdk;

/// <summary>
/// Lifetime markers picked up by <see cref="MarkedServiceCollectionExtensions.AddMarkedServices(Microsoft.Extensions.DependencyInjection.IServiceCollection, System.Reflection.Assembly)"/>.
/// Implement exactly one on a concrete class and it is registered automatically -- no hand-written
/// AddScoped/AddSingleton line. See that method for which service types the class is exposed as.
/// </summary>
public interface IScopedService;

/// <inheritdoc cref="IScopedService"/>
public interface ISingletonService;

/// <inheritdoc cref="IScopedService"/>
public interface ITransientService;
