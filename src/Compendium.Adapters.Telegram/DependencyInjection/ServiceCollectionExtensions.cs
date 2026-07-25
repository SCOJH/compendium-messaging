// -----------------------------------------------------------------------
// <copyright file="ServiceCollectionExtensions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using Microsoft.Extensions.DependencyInjection;

namespace Compendium.Adapters.Telegram.DependencyInjection;

/// <summary>
/// DI extensions for registering the Telegram messaging connector.
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers <see cref="TelegramMessagingConnector"/> as an <see cref="IMessagingConnector"/>,
    /// wiring the named outbound HttpClient and (optionally) host-wide <see cref="TelegramOptions"/>.
    /// Per-tenant secrets (bot token, webhook secret) are supplied per call, not here.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Optional options configurator (for example a custom base URL).</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddTelegramMessaging(
        this IServiceCollection services,
        Action<TelegramOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddHttpClient(TelegramMessagingConnector.HttpClientName);

        var optionsBuilder = services.AddOptions<TelegramOptions>();
        if (configure is not null)
        {
            optionsBuilder.Configure(configure);
        }

        services.AddSingleton<IMessagingConnector, TelegramMessagingConnector>();
        return services;
    }
}
