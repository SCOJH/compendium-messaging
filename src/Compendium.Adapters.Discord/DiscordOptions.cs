// -----------------------------------------------------------------------
// <copyright file="DiscordOptions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.Discord;

/// <summary>
/// Host-wide options for the Discord connector. Per-tenant secrets (Ed25519 public key, bot token)
/// are supplied per call via <see cref="ChannelCredentials"/>.
/// </summary>
public sealed class DiscordOptions
{
    /// <summary>The configuration section name (<c>Discord</c>).</summary>
    public const string SectionName = "Discord";

    /// <summary>The Discord REST API base URL. Overridable for testing.</summary>
    public string BaseUrl { get; set; } = "https://discord.com";

    /// <summary>The Discord REST API version segment used in outbound URLs.</summary>
    public string ApiVersion { get; set; } = "v10";
}
