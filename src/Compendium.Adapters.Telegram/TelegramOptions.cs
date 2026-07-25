// -----------------------------------------------------------------------
// <copyright file="TelegramOptions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.Telegram;

/// <summary>
/// Host-wide options for the Telegram connector. Per-tenant secrets (bot token, webhook
/// secret) are NOT here — they are supplied per call via <see cref="ChannelCredentials"/>.
/// </summary>
public sealed class TelegramOptions
{
    /// <summary>The configuration section name (<c>Telegram</c>).</summary>
    public const string SectionName = "Telegram";

    /// <summary>The Bot API base URL. Overridable for testing / self-hosted Bot API servers.</summary>
    public string BaseUrl { get; set; } = "https://api.telegram.org";
}
