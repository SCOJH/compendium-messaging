// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Microsoft.Extensions.DependencyInjection;

namespace Compendium.Adapters.Slack.DependencyInjection;

/// <summary>DI extensions for registering the Slack messaging connector.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="SlackMessagingConnector"/> as an <see cref="IMessagingConnector"/>,
    /// wiring the named outbound HttpClient and (optionally) host-wide <see cref="SlackOptions"/>.
    /// Per-tenant secrets (signing secret, bot token) are supplied per call, not here.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options configurator.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddSlackMessaging(
        this IServiceCollection services,
        Action<SlackOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(SlackMessagingConnector.HttpClientName);

        var optionsBuilder = services.AddOptions<SlackOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.AddSingleton<IMessagingConnector, SlackMessagingConnector>();
        return services;
    }
}
