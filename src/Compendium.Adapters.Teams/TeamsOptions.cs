// -----------------------------------------------------------------------
// <copyright file="TeamsOptions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.Teams;

/// <summary>
/// Host-wide options for the Microsoft Teams connector. Per-tenant secrets (Bot Framework
/// <c>appId</c> / <c>appPassword</c>) are supplied per call via <see cref="ChannelCredentials"/>.
/// </summary>
public sealed class TeamsOptions
{
    /// <summary>The configuration section name (<c>Teams</c>).</summary>
    public const string SectionName = "Teams";

    /// <summary>
    /// The Azure AD login authority used to acquire the outbound bot token. The OAuth token
    /// endpoint is <c>{LoginUrl}/oauth2/v2.0/token</c>. Overridable for testing.
    /// </summary>
    public string LoginUrl { get; set; } = "https://login.microsoftonline.com/botframework.com";

    /// <summary>
    /// Optional fixed Bot Connector service base URL. When set, it overrides the per-message
    /// <c>serviceUrl</c> option for outbound sends; otherwise the <c>serviceUrl</c> captured from
    /// the inbound activity (and round-tripped via <see cref="OutboundMessage.Options"/>) is used.
    /// </summary>
    public string? BaseUrl { get; set; }

    /// <summary>
    /// When <see langword="true"/>, inbound Bot Framework JWTs would be validated before parsing.
    /// In v1 this is <see langword="false"/> and inbound token validation is NOT performed — see
    /// the README "Known simplification" note. Setting this to <see langword="true"/> currently has
    /// no effect (validation is not yet implemented).
    /// </summary>
    public bool ValidateInboundToken { get; set; }
}
