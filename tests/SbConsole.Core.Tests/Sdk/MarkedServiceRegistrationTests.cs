using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using SbConsole.Sdk;

namespace SbConsole.Core.Tests.Sdk;

public class MarkedServiceRegistrationTests
{
    public interface IGreeter;
    public interface IFarewell;

    public sealed class ScopedHandler : IScopedService;
    public sealed class TransientHandler : ITransientService;
    public sealed class SingletonGreeter : IGreeter, ISingletonService, IDisposable
    {
        public void Dispose() { }
    }
    public sealed class ScopedGreeterAndFarewell : IGreeter, IFarewell, IScopedService;
    public abstract class AbstractScoped : IScopedService;
    public sealed class Unmarked : IGreeter;
    public sealed class Ambiguous : IScopedService, ISingletonService;
    public sealed class OpenGeneric<T> : IScopedService;

    private static IServiceCollection Register(params Type[] types)
    {
        var services = new ServiceCollection();
        services.AddMarkedServices(types);
        return services;
    }

    [Fact]
    public void Class_without_service_interfaces_is_registered_as_itself_with_its_marker_lifetime()
    {
        var services = Register(typeof(ScopedHandler), typeof(TransientHandler));

        services.Should().ContainSingle(d => d.ServiceType == typeof(ScopedHandler))
            .Which.Should().Match<ServiceDescriptor>(d => d.Lifetime == ServiceLifetime.Scoped && d.ImplementationType == typeof(ScopedHandler));
        services.Should().ContainSingle(d => d.ServiceType == typeof(TransientHandler))
            .Which.Lifetime.Should().Be(ServiceLifetime.Transient);
    }

    [Fact]
    public void Class_with_one_service_interface_is_registered_under_that_interface_only()
    {
        var services = Register(typeof(SingletonGreeter));

        services.Should().ContainSingle()
            .Which.Should().Match<ServiceDescriptor>(d =>
                d.ServiceType == typeof(IGreeter) && d.ImplementationType == typeof(SingletonGreeter) && d.Lifetime == ServiceLifetime.Singleton);
    }

    [Fact]
    public void Class_with_several_service_interfaces_shares_one_instance_per_scope_across_them()
    {
        using var provider = Register(typeof(ScopedGreeterAndFarewell)).BuildServiceProvider();
        using var scope = provider.CreateScope();

        var greeter = scope.ServiceProvider.GetRequiredService<IGreeter>();
        var farewell = scope.ServiceProvider.GetRequiredService<IFarewell>();

        greeter.Should().BeSameAs(farewell).And.BeSameAs(scope.ServiceProvider.GetRequiredService<ScopedGreeterAndFarewell>());
    }

    [Fact]
    public void Unmarked_abstract_and_open_generic_classes_are_ignored()
    {
        Register(typeof(Unmarked), typeof(AbstractScoped), typeof(OpenGeneric<>)).Should().BeEmpty();
    }

    [Fact]
    public void Class_with_more_than_one_lifetime_marker_is_rejected()
    {
        var act = () => Register(typeof(Ambiguous));

        act.Should().Throw<InvalidOperationException>().WithMessage($"*{nameof(Ambiguous)}*");
    }

    [Fact]
    public void Assembly_overload_scans_every_type_in_the_assembly()
    {
        var services = new ServiceCollection();
        services.Invoking(s => s.AddMarkedServices(typeof(MarkedServiceRegistrationTests).Assembly))
            .Should().Throw<InvalidOperationException>("this test assembly deliberately contains the Ambiguous class");
    }
}
