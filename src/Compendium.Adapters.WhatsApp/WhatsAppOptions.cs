// -----------------------------------------------------------------------
// <copyright file="WhatsAppOptions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.WhatsApp;

/// <summary>
/// Host-wide options for the WhatsApp connector. Per-tenant secrets (verify token, app secret,
/// access token, phone-number id) are supplied per call via <see cref="ChannelCredentials"/>.
/// </summary>
public sealed class WhatsAppOptions
{
    /// <summary>The configuration section name (<c>WhatsApp</c>).</summary>
    public const string SectionName = "WhatsApp";

    /// <summary>The Graph API base URL. Overridable for testing.</summary>
    public string BaseUrl { get; set; } = "https://graph.facebook.com";

    /// <summary>The Graph API version segment used in outbound URLs.</summary>
    public string ApiVersion { get; set; } = "v21.0";
}
