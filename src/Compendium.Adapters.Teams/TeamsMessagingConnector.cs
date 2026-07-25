// -----------------------------------------------------------------------
// <copyright file="TeamsMessagingConnector.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Compendium.Adapters.Teams;

/// <summary>
/// <see cref="IMessagingConnector"/> for Microsoft Teams via the Bot Framework. Inbound POSTs carry
/// a Bot Framework Activity JSON which is projected onto a normalized <see cref="InboundMessage"/>.
/// Outbound replies acquire an AAD client-credentials token and are posted to the Bot Connector
/// service URL. All per-tenant secrets are supplied per call.
/// </summary>
/// <remarks>
/// <para><b>Known simplification (v1):</b> inbound Bot Framework JWT validation is NOT performed.
/// The <c>Authorization</c> bearer on inbound requests is ignored and the activity is trusted as
/// received. This is gated behind <see cref="TeamsOptions.ValidateInboundToken"/> (default
/// <see langword="false"/>); enabling it currently has no effect. Hosts that require end-to-end
/// authenticity should terminate and validate the JWT (issuer, audience = the bot <c>appId</c>,
/// and the Bot Framework OpenID metadata signing keys) in front of this connector until validation
/// is implemented here.</para>
/// </remarks>
public sealed class TeamsMessagingConnector : IMessagingConnector
{
    /// <summary>The named <see cref="HttpClient"/> used for outbound token + activity calls.</summary>
    public const string HttpClientName = "compendium-teams";

    /// <summary>Credential key: Bot Framework application (client) id.</summary>
    public const string AppIdKey = "appId";

    /// <summary>Credential key: Bot Framework application password (client secret).</summary>
    public const string AppPasswordKey = "appPassword";

    /// <summary>
    /// Option key (on inbound metadata and outbound options) carrying the Bot Connector service URL.
    /// </summary>
    public const string ServiceUrlKey = "serviceUrl";

    /// <summary>The OAuth scope requested for the Bot Connector token.</summary>
    public const string BotScope = "https://api.botframework.com/.default";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TeamsOptions _options;
    private readonly ILogger<TeamsMessagingConnector> _logger;

    /// <summary>Initialises a new instance.</summary>
    public TeamsMessagingConnector(
        IHttpClientFactory httpClientFactory,
        IOptions<TeamsOptions> options,
        ILogger<TeamsMessagingConnector> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Platform => MessagingPlatforms.Teams;

    /// <inheritdoc />
    public Result<InboundEnvelope> ParseInbound(InboundRequest request, ChannelCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credentials);

        // NOTE (v1 simplification): inbound Bot Framework JWT validation is intentionally skipped.
        // See the class-level <remarks> and the README. ValidateInboundToken is reserved for a
        // future implementation and currently has no effect.

        try
        {
            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;

            var type = root.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, "message", StringComparison.Ordinal))
            {
                // Non-message activities (conversationUpdate, typing, etc.) are acknowledged with
                // no normalized messages to dispatch.
                return Result.Success(InboundEnvelope.Empty);
            }

            var conversationId = root.TryGetProperty("conversation", out var conv)
                && conv.TryGetProperty("id", out var convId)
                ? convId.GetString() ?? string.Empty
                : string.Empty;

            var text = root.TryGetProperty("text", out var txt) ? txt.GetString() ?? string.Empty : string.Empty;

            string senderId = string.Empty;
            string? senderName = null;
            if (root.TryGetProperty("from", out var from))
            {
                senderId = from.TryGetProperty("id", out var fid) ? fid.GetString() ?? string.Empty : string.Empty;
                senderName = from.TryGetProperty("name", out var fname) ? fname.GetString() : null;
            }

            var messageId = root.TryGetProperty("id", out var mid) ? mid.GetString() : null;

            var timestamp = DateTimeOffset.UtcNow;
            if (root.TryGetProperty("timestamp", out var ts)
                && ts.ValueKind == JsonValueKind.String
                && DateTimeOffset.TryParse(ts.GetString(), out var parsed))
            {
                timestamp = parsed;
            }

            var metadata = new Dictionary<string, string>(StringComparer.Ordinal);
            if (root.TryGetProperty("serviceUrl", out var svc) && svc.ValueKind == JsonValueKind.String)
            {
                var serviceUrl = svc.GetString();
                if (!string.IsNullOrEmpty(serviceUrl))
                {
                    metadata[ServiceUrlKey] = serviceUrl;
                }
            }

            var message = new InboundMessage
            {
                Platform = MessagingPlatforms.Teams,
                ConversationId = conversationId,
                Text = text,
                SenderId = senderId,
                SenderDisplayName = senderName,
                MessageId = messageId,
                Timestamp = timestamp,
                Metadata = metadata,
            };

            return Result.Success(new InboundEnvelope(new[] { message }));
        }
        catch (JsonException)
        {
            return Result.Failure<InboundEnvelope>(MessagingErrors.MalformedPayload(Platform));
        }
    }

    /// <inheritdoc />
    public async Task<Result<OutboundReceipt>> SendAsync(
        OutboundMessage message,
        ChannelCredentials credentials,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        ArgumentNullException.ThrowIfNull(credentials);

        var appId = credentials.Require(AppIdKey);
        if (appId.IsFailure)
        {
            return Result.Failure<OutboundReceipt>(appId.Error);
        }

        var appPassword = credentials.Require(AppPasswordKey);
        if (appPassword.IsFailure)
        {
            return Result.Failure<OutboundReceipt>(appPassword.Error);
        }

        var serviceUrl = ResolveServiceUrl(message);
        if (string.IsNullOrEmpty(serviceUrl))
        {
            return Result.Failure<OutboundReceipt>(MessagingErrors.MissingCredential(ServiceUrlKey));
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);

        // (a) Acquire an AAD client-credentials token for the Bot Connector.
        var tokenResult = await AcquireTokenAsync(client, appId.Value, appPassword.Value, cancellationToken)
            .ConfigureAwait(false);
        if (tokenResult.IsFailure)
        {
            return Result.Failure<OutboundReceipt>(tokenResult.Error);
        }

        // (b) Post the activity to the conversation.
        return await PostActivityAsync(client, serviceUrl, message, tokenResult.Value, cancellationToken)
            .ConfigureAwait(false);
    }

    private string? ResolveServiceUrl(OutboundMessage message)
    {
        if (!string.IsNullOrEmpty(_options.BaseUrl))
        {
            return _options.BaseUrl;
        }

        if (message.Options is not null
            && message.Options.TryGetValue(ServiceUrlKey, out var url)
            && !string.IsNullOrEmpty(url))
        {
            return url;
        }

        return null;
    }

    private async Task<Result<string>> AcquireTokenAsync(
        HttpClient client,
        string appId,
        string appPassword,
        CancellationToken cancellationToken)
    {
        var tokenUrl = $"{_options.LoginUrl.TrimEnd('/')}/oauth2/v2.0/token";
        var form = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = appId,
            ["client_secret"] = appPassword,
            ["scope"] = BotScope,
        };

        using var tokenRequest = new HttpRequestMessage(HttpMethod.Post, tokenUrl)
        {
            Content = new FormUrlEncodedContent(form),
        };

        HttpResponseMessage tokenResponse;
        try
        {
            tokenResponse = await client.SendAsync(tokenRequest, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Teams token transport failure");
            return Result.Failure<string>(MessagingErrors.DeliveryFailed(Platform, "token transport failure"));
        }

        using (tokenResponse)
        {
            var body = await tokenResponse.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!tokenResponse.IsSuccessStatusCode)
            {
                _logger.LogWarning("Teams token endpoint returned {StatusCode}", (int)tokenResponse.StatusCode);
                return Result.Failure<string>(
                    MessagingErrors.DeliveryFailed(Platform, $"token HTTP {(int)tokenResponse.StatusCode}"));
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("access_token", out var token)
                    && token.ValueKind == JsonValueKind.String)
                {
                    var value = token.GetString();
                    if (!string.IsNullOrEmpty(value))
                    {
                        return Result.Success(value);
                    }
                }
            }
            catch (JsonException)
            {
                // Fall through to the failure below.
            }

            return Result.Failure<string>(MessagingErrors.DeliveryFailed(Platform, "no access_token in response"));
        }
    }

    private async Task<Result<OutboundReceipt>> PostActivityAsync(
        HttpClient client,
        string serviceUrl,
        OutboundMessage message,
        string accessToken,
        CancellationToken cancellationToken)
    {
        var url = $"{serviceUrl.TrimEnd('/')}/v3/conversations/{message.ConversationId}/activities";
        var payload = new
        {
            type = "message",
            text = message.Text,
        };

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(requestMessage, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Teams send transport failure");
            return Result.Failure<OutboundReceipt>(MessagingErrors.DeliveryFailed(Platform, "transport failure"));
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Teams send returned {StatusCode}", (int)response.StatusCode);
                return Result.Failure<OutboundReceipt>(
                    MessagingErrors.DeliveryFailed(Platform, $"HTTP {(int)response.StatusCode}"));
            }

            string? messageId = null;
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String)
                {
                    messageId = id.GetString();
                }
            }
            catch (JsonException)
            {
                // Delivered at the HTTP layer; id unavailable.
            }

            return Result.Success(new OutboundReceipt(messageId, DateTimeOffset.UtcNow));
        }
    }
}
