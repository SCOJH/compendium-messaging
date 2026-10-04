// -----------------------------------------------------------------------
// <copyright file="SlackOptions.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

namespace Compendium.Adapters.Slack;

/// <summary>
/// Host-wide options for the Slack connector. Per-tenant secrets (signing secret, bot token)
/// are supplied per call via <see cref="ChannelCredentials"/>.
/// </summary>
public sealed class SlackOptions
{
    /// <summary>The configuration section name (<c>Slack</c>).</summary>
    public const string SectionName = "Slack";

    /// <summary>The Slack Web API base URL. Overridable for testing.</summary>
    public string BaseUrl { get; set; } = "https://slack.com";

    /// <summary>
    /// How far <c>X-Slack-Request-Timestamp</c> may be from the current time, either way, for an inbound
    /// request to be accepted. The signature covers the timestamp, so it cannot be moved without the
    /// signing secret; without this bound a request captured once stays valid forever and can be
    /// replayed at will. Five minutes is Slack's own recommendation.
    /// </summary>
    public TimeSpan TimestampTolerance { get; set; } = TimeSpan.FromMinutes(5);
}
