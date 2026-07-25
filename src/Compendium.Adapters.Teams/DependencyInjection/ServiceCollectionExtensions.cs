// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Microsoft.Extensions.DependencyInjection;

namespace Compendium.Adapters.Teams.DependencyInjection;

/// <summary>DI extensions for registering the Microsoft Teams messaging connector.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TeamsMessagingConnector"/> as an <see cref="IMessagingConnector"/>,
    /// wiring the named outbound HttpClient and (optionally) host-wide <see cref="TeamsOptions"/>.
    /// Per-tenant secrets (<c>appId</c>, <c>appPassword</c>) are supplied per call, not here.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options configurator.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTeamsMessaging(
        this IServiceCollection services,
        Action<TeamsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(TeamsMessagingConnector.HttpClientName);

        var optionsBuilder = services.AddOptions<TeamsOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.AddSingleton<IMessagingConnector, TeamsMessagingConnector>();
        return services;
    }
}
