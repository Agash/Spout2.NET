using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Spout2.NET;

// Service registration lives in the namespace of IServiceCollection, as the framework's own does, so it
// is found without a using directive.
#pragma warning disable IDE0130
namespace Microsoft.Extensions.DependencyInjection;

#pragma warning restore IDE0130

/// <summary>Registers Spout2.NET with a service collection.</summary>
public static class SpoutServiceCollectionExtensions
{
    /// <summary>
    /// Adds a <see cref="SpoutDevice"/> on a GPU, made with the application's logger factory and
    /// disposed with the container, and a <see cref="SpoutFactory"/> on it with the application's
    /// <see cref="TimeProvider"/>. An application that already has a Direct3D device registers its own
    /// <see cref="SpoutDevice"/> (<see cref="SpoutDevice.FromD3D11Device"/>) before calling this.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="adapterLuid">The GPU's LUID, or null for the system's default GPU.</param>
    /// <returns>The service collection.</returns>
    public static IServiceCollection AddSpout(
        this IServiceCollection services,
        long? adapterLuid = null
    )
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddSingleton(provider =>
            SpoutDevice.Create(adapterLuid, provider.GetService<ILoggerFactory>())
        );
        services.TryAddSingleton(provider => new SpoutFactory(
            provider.GetRequiredService<SpoutDevice>(),
            provider.GetService<TimeProvider>()
        ));
        return services;
    }
}
