// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Microsoft.Extensions.DependencyInjection;

namespace Compendium.Adapters.WhatsApp.DependencyInjection;

/// <summary>DI extensions for registering the WhatsApp messaging connector.</summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="WhatsAppMessagingConnector"/> as an <see cref="IMessagingConnector"/>,
    /// wiring the named outbound HttpClient and (optionally) host-wide <see cref="WhatsAppOptions"/>.
    /// Per-tenant secrets (verify token, app secret, access token, phone-number id) are supplied
    /// per call, not here.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options configurator.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddWhatsAppMessaging(
        this IServiceCollection services,
        Action<WhatsAppOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(WhatsAppMessagingConnector.HttpClientName);

        var optionsBuilder = services.AddOptions<WhatsAppOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.AddSingleton<IMessagingConnector, WhatsAppMessagingConnector>();
        return services;
    }
}
