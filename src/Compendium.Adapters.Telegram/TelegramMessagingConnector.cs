// -----------------------------------------------------------------------
// <copyright file="TelegramMessagingConnector.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
//     See LICENSE in the project root for license information.
// </copyright>
// -----------------------------------------------------------------------

using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Compendium.Adapters.Telegram;

/// <summary>
/// <see cref="IMessagingConnector"/> for the Telegram Bot API. Inbound webhooks are authenticated
/// with the <c>X-Telegram-Bot-Api-Secret-Token</c> header (set when the webhook is registered);
/// outbound replies use <c>POST /bot{token}/sendMessage</c>. The bot token is supplied per call.
/// </summary>
public sealed class TelegramMessagingConnector : IMessagingConnector
{
    /// <summary>The named <see cref="HttpClient"/> used for outbound Bot API calls.</summary>
    public const string HttpClientName = "compendium-telegram";

    /// <summary>Credential key for the bot token (required to send).</summary>
    public const string BotTokenKey = "botToken";

    /// <summary>Credential key for the inbound webhook secret token (optional but recommended).</summary>
    public const string SecretTokenKey = "secretToken";

    private const string SecretHeader = "X-Telegram-Bot-Api-Secret-Token";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly TelegramOptions _options;
    private readonly ILogger<TelegramMessagingConnector> _logger;

    /// <summary>Initialises a new instance.</summary>
    public TelegramMessagingConnector(
        IHttpClientFactory httpClientFactory,
        IOptions<TelegramOptions> options,
        ILogger<TelegramMessagingConnector> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Platform => MessagingPlatforms.Telegram;

    /// <inheritdoc />
    public Result<InboundEnvelope> ParseInbound(InboundRequest request, ChannelCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credentials);

        // Verify the secret token when the tenant configured one.
        var expectedSecret = credentials.Get(SecretTokenKey);
        if (!string.IsNullOrEmpty(expectedSecret))
        {
            var provided = GetHeader(request.Headers, SecretHeader);
            if (!string.Equals(provided, expectedSecret, StringComparison.Ordinal))
            {
                return Result.Failure<InboundEnvelope>(MessagingErrors.InvalidSignature(Platform));
            }
        }

        JsonDocument document;
        try
        {
            document = JsonDocument.Parse(request.Body);
        }
        catch (JsonException)
        {
            return Result.Failure<InboundEnvelope>(MessagingErrors.MalformedPayload(Platform));
        }

        using (document)
        {
            if (!TryGetMessage(document.RootElement, out var message)
                || !message.TryGetProperty("chat", out var chat)
                || !chat.TryGetProperty("id", out var chatId))
            {
                // A non-message update (delivery receipt, my_chat_member, ...) — acknowledge with 200.
                return Result.Success(InboundEnvelope.Empty);
            }

            var inbound = new InboundMessage
            {
                Platform = Platform,
                ConversationId = ScalarToString(chatId),
                Text = message.TryGetProperty("text", out var text) ? text.GetString() ?? string.Empty : string.Empty,
                SenderId = TryGetSender(message, out var senderId, out var displayName) ? senderId : null,
                SenderDisplayName = displayName,
                MessageId = message.TryGetProperty("message_id", out var mid) ? ScalarToString(mid) : null,
                Timestamp = message.TryGetProperty("date", out var date) && date.TryGetInt64(out var unix)
                    ? DateTimeOffset.FromUnixTimeSeconds(unix)
                    : DateTimeOffset.UtcNow,
            };

            return Result.Success(new InboundEnvelope(new[] { inbound }));
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

        var tokenResult = credentials.Require(BotTokenKey);
        if (tokenResult.IsFailure)
        {
            return Result.Failure<OutboundReceipt>(tokenResult.Error);
        }

        var payload = new Dictionary<string, object?>(StringComparer.Ordinal)
        {
            ["chat_id"] = message.ConversationId,
            ["text"] = message.Text,
        };

        if (!string.IsNullOrEmpty(message.ReplyToMessageId)
            && long.TryParse(message.ReplyToMessageId, out var replyTo))
        {
            payload["reply_to_message_id"] = replyTo;
        }

        if (message.Options is not null
            && message.Options.TryGetValue("parse_mode", out var parseMode)
            && !string.IsNullOrEmpty(parseMode))
        {
            payload["parse_mode"] = parseMode;
        }

        var client = _httpClientFactory.CreateClient(HttpClientName);
        var url = $"{_options.BaseUrl.TrimEnd('/')}/bot{tokenResult.Value}/sendMessage";

        HttpResponseMessage response;
        try
        {
            response = await client.PostAsJsonAsync(url, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "Telegram sendMessage transport failure");
            return Result.Failure<OutboundReceipt>(
                MessagingErrors.DeliveryFailed(Platform, "transport failure"));
        }

        using (response)
        {
            if (response.StatusCode == HttpStatusCode.TooManyRequests)
            {
                return Result.Failure<OutboundReceipt>(
                    MessagingErrors.Throttled(Platform, response.Headers.RetryAfter?.Delta));
            }

            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);

            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning(
                    "Telegram sendMessage returned {StatusCode}: {Body}", (int)response.StatusCode, Truncate(body));
                return Result.Failure<OutboundReceipt>(
                    MessagingErrors.DeliveryFailed(Platform, $"HTTP {(int)response.StatusCode}"));
            }

            string? messageId = null;
            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                if (root.TryGetProperty("ok", out var ok) && ok.ValueKind == JsonValueKind.False)
                {
                    return Result.Failure<OutboundReceipt>(
                        MessagingErrors.DeliveryFailed(Platform, "Telegram returned ok=false."));
                }

                if (root.TryGetProperty("result", out var result)
                    && result.TryGetProperty("message_id", out var mid))
                {
                    messageId = ScalarToString(mid);
                }
            }
            catch (JsonException)
            {
                // Delivery succeeded at the HTTP layer; we just couldn't read the id.
            }

            return Result.Success(new OutboundReceipt(messageId, DateTimeOffset.UtcNow));
        }
    }

    private static bool TryGetMessage(JsonElement root, out JsonElement message)
    {
        foreach (var key in (ReadOnlySpan<string>)["message", "edited_message", "channel_post", "edited_channel_post"])
        {
            if (root.TryGetProperty(key, out message) && message.ValueKind == JsonValueKind.Object)
            {
                return true;
            }
        }

        message = default;
        return false;
    }

    private static bool TryGetSender(JsonElement message, out string? senderId, out string? displayName)
    {
        senderId = null;
        displayName = null;
        if (!message.TryGetProperty("from", out var from) || from.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        if (from.TryGetProperty("id", out var id))
        {
            senderId = ScalarToString(id);
        }

        if (from.TryGetProperty("username", out var username))
        {
            displayName = username.GetString();
        }
        else if (from.TryGetProperty("first_name", out var firstName))
        {
            displayName = firstName.GetString();
        }

        return senderId is not null;
    }

    private static string ScalarToString(JsonElement element) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? string.Empty : element.GetRawText();

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

    private static string Truncate(string value) =>
        value.Length <= 256 ? value : value[..256];
}
