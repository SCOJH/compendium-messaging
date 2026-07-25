// -----------------------------------------------------------------------
// <copyright file="WhatsAppMessagingConnector.cs" company="Sassy Solutions">
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

namespace Compendium.Adapters.WhatsApp;

/// <summary>
/// <see cref="IMessagingConnector"/> for the WhatsApp Cloud API. Handles the Meta webhook GET
/// verification handshake, verifies inbound POSTs with the <c>X-Hub-Signature-256</c> HMAC, and
/// sends replies through the Graph API. All per-tenant secrets are supplied per call.
/// </summary>
public sealed class WhatsAppMessagingConnector : IMessagingConnector
{
    /// <summary>The named <see cref="HttpClient"/> used for outbound Graph API calls.</summary>
    public const string HttpClientName = "compendium-whatsapp";

    /// <summary>Credential key: webhook verify token (<c>hub.verify_token</c>).</summary>
    public const string VerifyTokenKey = "verifyToken";

    /// <summary>Credential key: app secret for HMAC verification.</summary>
    public const string AppSecretKey = "appSecret";

    /// <summary>Credential key: Graph API access token (outbound bearer).</summary>
    public const string AccessTokenKey = "accessToken";

    /// <summary>Credential key: WhatsApp Business phone-number id (outbound URL).</summary>
    public const string PhoneNumberIdKey = "phoneNumberId";

    private const string SignatureHeader = "X-Hub-Signature-256";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly WhatsAppOptions _options;
    private readonly ILogger<WhatsAppMessagingConnector> _logger;

    /// <summary>Initialises a new instance.</summary>
    public WhatsAppMessagingConnector(
        IHttpClientFactory httpClientFactory,
        IOptions<WhatsAppOptions> options,
        ILogger<WhatsAppMessagingConnector> logger)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
    }

    /// <inheritdoc />
    public string Platform => MessagingPlatforms.WhatsApp;

    /// <inheritdoc />
    public Result<InboundEnvelope> ParseInbound(InboundRequest request, ChannelCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credentials);

        // 1) GET verification handshake (?hub.mode=subscribe&hub.verify_token=...&hub.challenge=...)
        var query = ParseQuery(request.QueryString);
        if (query.TryGetValue("hub.mode", out var mode) && mode == "subscribe")
        {
            var expected = credentials.Get(VerifyTokenKey);
            var provided = query.GetValueOrDefault("hub.verify_token");
            if (!string.IsNullOrEmpty(expected) && string.Equals(provided, expected, StringComparison.Ordinal))
            {
                var challenge = query.GetValueOrDefault("hub.challenge") ?? string.Empty;
                return Result.Success(new InboundEnvelope(
                    Array.Empty<InboundMessage>(),
                    new ChannelAck(200, challenge, "text/plain")));
            }

            return Result.Failure<InboundEnvelope>(MessagingErrors.InvalidSignature(Platform));
        }

        // 2) POST messages — verify the HMAC signature when an app secret is configured.
        var appSecret = credentials.Get(AppSecretKey);
        if (!string.IsNullOrEmpty(appSecret)
            && !VerifySignature(request.Body, appSecret, GetHeader(request.Headers, SignatureHeader)))
        {
            return Result.Failure<InboundEnvelope>(MessagingErrors.InvalidSignature(Platform));
        }

        try
        {
            using var document = JsonDocument.Parse(request.Body);
            var messages = new List<InboundMessage>();

            if (document.RootElement.TryGetProperty("entry", out var entries)
                && entries.ValueKind == JsonValueKind.Array)
            {
                foreach (var entry in entries.EnumerateArray())
                {
                    if (!entry.TryGetProperty("changes", out var changes) || changes.ValueKind != JsonValueKind.Array)
                    {
                        continue;
                    }

                    foreach (var change in changes.EnumerateArray())
                    {
                        if (!change.TryGetProperty("value", out var value))
                        {
                            continue;
                        }

                        var senderName = TryGetContactName(value);
                        if (!value.TryGetProperty("messages", out var msgs) || msgs.ValueKind != JsonValueKind.Array)
                        {
                            continue;
                        }

                        foreach (var message in msgs.EnumerateArray())
                        {
                            messages.Add(MapMessage(message, senderName));
                        }
                    }
                }
            }

            return Result.Success(new InboundEnvelope(messages));
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

        var token = credentials.Require(AccessTokenKey);
        if (token.IsFailure)
        {
            return Result.Failure<OutboundReceipt>(token.Error);
        }

        var phoneNumberId = credentials.Require(PhoneNumberIdKey);
        if (phoneNumberId.IsFailure)
        {
            return Result.Failure<OutboundReceipt>(phoneNumberId.Error);
        }

        var url = $"{_options.BaseUrl.TrimEnd('/')}/{_options.ApiVersion}/{phoneNumberId.Value}/messages";
        var payload = new
        {
            messaging_product = "whatsapp",
            to = message.ConversationId,
            type = "text",
            text = new { body = message.Text },
        };

        using var requestMessage = new HttpRequestMessage(HttpMethod.Post, url)
        {
            Content = JsonContent.Create(payload),
        };
        requestMessage.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Value);

        var client = _httpClientFactory.CreateClient(HttpClientName);

        HttpResponseMessage response;
        try
        {
            response = await client.SendAsync(requestMessage, cancellationToken).ConfigureAwait(false);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "WhatsApp send transport failure");
            return Result.Failure<OutboundReceipt>(MessagingErrors.DeliveryFailed(Platform, "transport failure"));
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("WhatsApp send returned {StatusCode}", (int)response.StatusCode);
                return Result.Failure<OutboundReceipt>(
                    MessagingErrors.DeliveryFailed(Platform, $"HTTP {(int)response.StatusCode}"));
            }

            string? messageId = null;
            try
            {
                using var document = JsonDocument.Parse(body);
                if (document.RootElement.TryGetProperty("messages", out var sent)
                    && sent.ValueKind == JsonValueKind.Array
                    && sent.GetArrayLength() > 0
                    && sent[0].TryGetProperty("id", out var id))
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

    private static InboundMessage MapMessage(JsonElement message, string? senderName)
    {
        var from = message.TryGetProperty("from", out var f) ? f.GetString() ?? string.Empty : string.Empty;
        var text = message.TryGetProperty("text", out var t) && t.TryGetProperty("body", out var b)
            ? b.GetString() ?? string.Empty
            : string.Empty;
        var messageId = message.TryGetProperty("id", out var id) ? id.GetString() : null;

        var timestamp = DateTimeOffset.UtcNow;
        if (message.TryGetProperty("timestamp", out var ts)
            && long.TryParse(ts.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
        {
            timestamp = DateTimeOffset.FromUnixTimeSeconds(unix);
        }

        return new InboundMessage
        {
            Platform = MessagingPlatforms.WhatsApp,
            ConversationId = from,
            Text = text,
            SenderId = from,
            SenderDisplayName = senderName,
            MessageId = messageId,
            Timestamp = timestamp,
        };
    }

    private static string? TryGetContactName(JsonElement value)
    {
        if (value.TryGetProperty("contacts", out var contacts)
            && contacts.ValueKind == JsonValueKind.Array
            && contacts.GetArrayLength() > 0
            && contacts[0].TryGetProperty("profile", out var profile)
            && profile.TryGetProperty("name", out var name))
        {
            return name.GetString();
        }

        return null;
    }

    private static bool VerifySignature(string body, string appSecret, string? header)
    {
        if (string.IsNullOrEmpty(header))
        {
            return false;
        }

        var provided = header.StartsWith("sha256=", StringComparison.OrdinalIgnoreCase)
            ? header["sha256=".Length..]
            : header;

        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(appSecret));
        var computed = Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(provided.Trim().ToLowerInvariant()));
    }

    private static IReadOnlyDictionary<string, string> ParseQuery(string? queryString)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(queryString))
        {
            return result;
        }

        var trimmed = queryString.StartsWith('?') ? queryString[1..] : queryString;
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var index = pair.IndexOf('=', StringComparison.Ordinal);
            if (index < 0)
            {
                result[Uri.UnescapeDataString(pair)] = string.Empty;
            }
            else
            {
                var key = Uri.UnescapeDataString(pair[..index]);
                var value = Uri.UnescapeDataString(pair[(index + 1)..]);
                result[key] = value;
            }
        }

        return result;
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
