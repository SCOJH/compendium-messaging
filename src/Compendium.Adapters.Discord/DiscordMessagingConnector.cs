// -----------------------------------------------------------------------
// <copyright file="DiscordMessagingConnector.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Globalization;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSec.Cryptography;

namespace Compendium.Adapters.Discord;

/// <summary>
/// <see cref="IMessagingConnector"/> for the Discord Interactions API. Verifies inbound POSTs with
/// the Ed25519 request signature, answers the PING handshake with a PONG, projects
/// APPLICATION_COMMAND interactions onto <see cref="InboundMessage"/>, and sends replies through the
/// Discord REST API. All per-tenant secrets are supplied per call.
/// </summary>
public sealed class DiscordMessagingConnector : IMessagingConnector
{
    /// <summary>The named <see cref="HttpClient"/> used for outbound REST API calls.</summary>
    public const string HttpClientName = "compendium-discord";

    /// <summary>Credential key: hex-encoded Ed25519 application public key (inbound verify).</summary>
    public const string PublicKeyKey = "publicKey";

    /// <summary>Credential key: bot token for the Discord REST API (outbound <c>Bot</c> auth).</summary>
    public const string BotTokenKey = "botToken";

    private const string SignatureHeader = "X-Signature-Ed25519";
    private const string TimestampHeader = "X-Signature-Timestamp";

    // Discord interaction type ids.
    private const int InteractionTypePing = 1;
    private const int InteractionTypeApplicationCommand = 2;

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly DiscordOptions _options;
    private readonly ILogger<DiscordMessagingConnector> _logger;
    private readonly TimeProvider _time;

    /// <summary>Initialises a new instance, on the system clock.</summary>
    public DiscordMessagingConnector(
        IHttpClientFactory httpClientFactory,
        IOptions<DiscordOptions> options,
        ILogger<DiscordMessagingConnector> logger)
        : this(httpClientFactory, options, logger, TimeProvider.System)
    {
    }

    /// <summary>Initialises a new instance; <paramref name="time"/> is the clock request timestamps are checked against.</summary>
    public DiscordMessagingConnector(
        IHttpClientFactory httpClientFactory,
        IOptions<DiscordOptions> options,
        ILogger<DiscordMessagingConnector> logger,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(time);
        _time = time;
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Platform => MessagingPlatforms.Discord;

    /// <inheritdoc />
    public Result<InboundEnvelope> ParseInbound(InboundRequest request, ChannelCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credentials);

        // 1) Verify the Ed25519 request signature.
        var publicKey = credentials.Require(PublicKeyKey);
        if (publicKey.IsFailure)
        {
            return Result.Failure<InboundEnvelope>(publicKey.Error);
        }

        // The signed timestamp must also be current: the signature proves who wrote the request, not
        // when, and a captured interaction would otherwise be accepted every time it is sent again.
        var signature = GetHeader(request.Headers, SignatureHeader);
        var timestamp = GetHeader(request.Headers, TimestampHeader);
        if (!IsCurrent(timestamp) || !VerifySignature(publicKey.Value, timestamp, request.Body, signature))
        {
            return Result.Failure<InboundEnvelope>(MessagingErrors.InvalidSignature(Platform));
        }

        // 2) Parse the interaction.
        try
        {
            using var document = JsonDocument.Parse(request.Body);
            var root = document.RootElement;

            var type = root.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number
                ? t.GetInt32()
                : 0;

            // PING -> PONG handshake.
            if (type == InteractionTypePing)
            {
                return Result.Success(new InboundEnvelope(
                    Array.Empty<InboundMessage>(),
                    new ChannelAck(200, "{\"type\":1}", "application/json")));
            }

            // APPLICATION_COMMAND -> normalized inbound message.
            if (type == InteractionTypeApplicationCommand)
            {
                return Result.Success(new InboundEnvelope(new[] { MapCommand(root) }));
            }

            // Any other interaction type is acknowledged but produces no message.
            return Result.Success(InboundEnvelope.Empty);
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

        var botToken = credentials.Require(BotTokenKey);
        if (botToken.IsFailure)
        {
            return Result.Failure<OutboundReceipt>(botToken.Error);
        }

        var url = $"{_options.BaseUrl.TrimEnd('/')}/api/{_options.ApiVersion}/channels/{message.ConversationId}/messages";
        var payload = new { content = message.Text };

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bot", botToken.Value);

        var client = _httpClientFactory.CreateClient(HttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(requestMessage, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Discord send transport failure");
            return Result.Failure<OutboundReceipt>(MessagingErrors.DeliveryFailed(Platform, "transport failure"));
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Discord send returned {StatusCode}", (int)response.StatusCode);
                return Result.Failure<OutboundReceipt>(
                    MessagingErrors.DeliveryFailed(Platform, $"HTTP {(int)response.StatusCode}"));
            }

            string? messageId = null;
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("id", out var id))
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

    private static InboundMessage MapCommand(JsonElement root)
    {
        var conversationId = root.TryGetProperty("channel_id", out var ch) ? ch.GetString() ?? string.Empty : string.Empty;
        var messageId = root.TryGetProperty("id", out var id) ? id.GetString() : null;

        // Text: first string option value if present, otherwise the command name.
        string text = string.Empty;
        if (root.TryGetProperty("data", out var data))
        {
            var optionValue = TryGetFirstStringOptionValue(data);
            if (optionValue is not null)
            {
                text = optionValue;
            }
            else if (data.TryGetProperty("name", out var name))
            {
                text = name.GetString() ?? string.Empty;
            }
        }

        // SenderId: member.user.id (guild interactions) ?? user.id (DM interactions).
        string? senderId = null;
        string? senderName = null;
        if (root.TryGetProperty("member", out var member) && member.TryGetProperty("user", out var memberUser))
        {
            senderId = memberUser.TryGetProperty("id", out var mid) ? mid.GetString() : null;
            senderName = memberUser.TryGetProperty("username", out var mun) ? mun.GetString() : null;
        }
        else if (root.TryGetProperty("user", out var user))
        {
            senderId = user.TryGetProperty("id", out var uid) ? uid.GetString() : null;
            senderName = user.TryGetProperty("username", out var uun) ? uun.GetString() : null;
        }

        return new InboundMessage
        {
            Platform = MessagingPlatforms.Discord,
            ConversationId = conversationId,
            Text = text,
            SenderId = senderId ?? string.Empty,
            SenderDisplayName = senderName,
            MessageId = messageId,
            Timestamp = DateTimeOffset.UtcNow,
        };
    }

    private static string? TryGetFirstStringOptionValue(JsonElement data)
    {
        if (!data.TryGetProperty("options", out var options) || options.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        foreach (var option in options.EnumerateArray())
        {
            if (option.TryGetProperty("value", out var value) && value.ValueKind == JsonValueKind.String)
            {
                return value.GetString();
            }
        }

        return null;
    }

    /// <summary>Whether <paramref name="timestamp"/> (Unix seconds) is within <see cref="DiscordOptions.TimestampTolerance"/> of now.</summary>
    private bool IsCurrent(string? timestamp)
    {
        if (!long.TryParse(timestamp, NumberStyles.None, CultureInfo.InvariantCulture, out var seconds)
            || seconds > DateTimeOffset.MaxValue.ToUnixTimeSeconds())
        {
            return false;
        }

        var age = _time.GetUtcNow() - DateTimeOffset.FromUnixTimeSeconds(seconds);
        return age.Duration() <= _options.TimestampTolerance;
    }

    private static bool VerifySignature(string publicKeyHex, string? timestamp, string body, string? signatureHex)
    {
        if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signatureHex) || string.IsNullOrEmpty(publicKeyHex))
        {
            return false;
        }

        byte[] publicKeyBytes;
        byte[] signatureBytes;
        try
        {
            publicKeyBytes = Convert.FromHexString(publicKeyHex);
            signatureBytes = Convert.FromHexString(signatureHex);
        }
        catch (FormatException)
        {
            return false;
        }

        var message = Encoding.UTF8.GetBytes(timestamp + body);

        try
        {
            var algorithm = SignatureAlgorithm.Ed25519;
            var key = PublicKey.Import(algorithm, publicKeyBytes, KeyBlobFormat.RawPublicKey);
            return algorithm.Verify(key, message, signatureBytes);
        }
        catch (FormatException)
        {
            // Malformed public key blob.
            return false;
        }
        catch (CryptographicException)
        {
            return false;
        }
    }

    private static string? GetHeader(IReadOnlyDictionary<string, string> headers, string name)
    {
        foreach (var header in headers)
        {
            if (string.Equals(header.Key, name, StringComparison.OrdinalIgnoreCase))
            {
                return header.Value;
            }
        }

        return null;
    }
}
