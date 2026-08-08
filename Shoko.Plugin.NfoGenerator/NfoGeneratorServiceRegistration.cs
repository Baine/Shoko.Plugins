using Microsoft.Extensions.DependencyInjection;
using Shoko.Abstractions.Config.Services;
using Shoko.Abstractions.Plugin;
using Shoko.Plugin.NfoGenerator.Config;

namespace Shoko.Plugin.NfoGenerator;

/// <summary>
/// Registers the NFO generation service, its config provider, and exposes the
/// hosted listener so on-demand triggers can reuse the same instance.
/// </summary>
public sealed class NfoGeneratorServiceRegistration : IPluginServiceRegistration
{
    public static void RegisterServices(IServiceCollection serviceCollection, IApplicationPaths applicationPaths)
    {
        serviceCollection.AddSingleton(provider =>
            provider.GetRequiredService<IConfigurationService>().CreateProvider<NfoGeneratorSettings>());

        serviceCollection.AddSingleton<NfoGeneratorService>();
        serviceCollection.AddHostedService(sp => sp.GetRequiredService<NfoGeneratorService>());
    }
}
