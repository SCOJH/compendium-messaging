// -----------------------------------------------------------------------
// <copyright file="SlackMessagingConnector.cs" company="Sassy Solutions">
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

namespace Compendium.Adapters.Slack;

/// <summary>
/// <see cref="IMessagingConnector"/> for the Slack Events API + Web API. Handles the
/// <c>url_verification</c> challenge handshake, verifies inbound POSTs with the
/// <c>X-Slack-Signature</c> (v0) HMAC-SHA256, and sends replies through <c>chat.postMessage</c>.
/// All per-tenant secrets are supplied per call.
/// </summary>
public sealed class SlackMessagingConnector : IMessagingConnector
{
    /// <summary>The named <see cref="HttpClient"/> used for outbound Web API calls.</summary>
    public const string HttpClientName = "compendium-slack";

    /// <summary>Credential key: signing secret used for inbound HMAC verification.</summary>
    public const string SigningSecretKey = "signingSecret";

    /// <summary>Credential key: bot token (outbound bearer for the Web API).</summary>
    public const string BotTokenKey = "botToken";

    private const string SignatureHeader = "X-Slack-Signature";
    private const string TimestampHeader = "X-Slack-Request-Timestamp";
    private const string SignatureVersion = "v0";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SlackOptions _options;
    private readonly ILogger<SlackMessagingConnector> _logger;
    private readonly TimeProvider _time;

    /// <summary>Initialises a new instance, on the system clock.</summary>
    public SlackMessagingConnector(
        IHttpClientFactory httpClientFactory,
        IOptions<SlackOptions> options,
        ILogger<SlackMessagingConnector> logger)
        : this(httpClientFactory, options, logger, TimeProvider.System)
    {
    }

    /// <summary>Initialises a new instance; <paramref name="time"/> is the clock request timestamps are checked against.</summary>
    public SlackMessagingConnector(
        IHttpClientFactory httpClientFactory,
        IOptions<SlackOptions> options,
        ILogger<SlackMessagingConnector> logger,
        TimeProvider time)
    {
        ArgumentNullException.ThrowIfNull(httpClientFactory);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(time);
        _httpClientFactory = httpClientFactory;
        _options = options.Value;
        _logger = logger;
        _time = time;
    }

    /// <inheritdoc />
    public string Platform => MessagingPlatforms.Slack;

    /// <inheritdoc />
    public Result<InboundEnvelope> ParseInbound(InboundRequest request, ChannelCredentials credentials)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(credentials);

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
            var root = document.RootElement;

            // 1) Verify the v0 signature over "v0:{timestamp}:{rawBody}", and that the signed timestamp
            //    is current. Slack signs every request, url_verification included, so nothing is
            //    answered before this: echoing the challenge first told any caller whether a signing
            //    secret was configured (an echo when it was, a refusal when it was not).
            var signingSecret = credentials.Get(SigningSecretKey);
            var timestamp = GetHeader(request.Headers, TimestampHeader);
            if (string.IsNullOrEmpty(signingSecret)
                || !IsCurrent(timestamp)
                || !VerifySignature(
                    request.Body,
                    signingSecret,
                    timestamp,
                    GetHeader(request.Headers, SignatureHeader)))
            {
                return Result.Failure<InboundEnvelope>(MessagingErrors.InvalidSignature(Platform));
            }

            // 2) url_verification challenge — echo it back.
            if (root.TryGetProperty("type", out var topType)
                && topType.ValueKind == JsonValueKind.String
                && topType.GetString() == "url_verification")
            {
                var challenge = root.TryGetProperty("challenge", out var ch) ? ch.GetString() ?? string.Empty : string.Empty;
                return Result.Success(new InboundEnvelope(
                    Array.Empty<InboundMessage>(),
                    new ChannelAck(200, challenge, "text/plain")));
            }

            // 3) Parse the event — a user "message" event with no bot_id yields one InboundMessage.
            if (root.TryGetProperty("event", out var evt) && evt.ValueKind == JsonValueKind.Object)
            {
                var eventType = evt.TryGetProperty("type", out var et) ? et.GetString() : null;
                var isBot = evt.TryGetProperty("bot_id", out var botId)
                    && botId.ValueKind != JsonValueKind.Null;

                if (eventType == "message" && !isBot)
                {
                    return Result.Success(new InboundEnvelope(new[] { MapMessage(evt) }));
                }
            }

            return Result.Success(InboundEnvelope.Empty);
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

        var token = credentials.Require(BotTokenKey);
        if (token.IsFailure)
        {
            return Result.Failure<OutboundReceipt>(token.Error);
        }

        var url = $"{_options.BaseUrl.TrimEnd('/')}/api/chat.postMessage";
        var payload = new
        {
            channel = message.ConversationId,
            text = message.Text,
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
            _logger.LogWarning(ex, "Slack send transport failure");
            return Result.Failure<OutboundReceipt>(MessagingErrors.DeliveryFailed(Platform, "transport failure"));
        }

        using (response)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.LogWarning("Slack send returned {StatusCode}", (int)response.StatusCode);
                return Result.Failure<OutboundReceipt>(
                    MessagingErrors.DeliveryFailed(Platform, $"HTTP {(int)response.StatusCode}"));
            }

            try
            {
                using var document = JsonDocument.Parse(body);
                var root = document.RootElement;
                var ok = root.TryGetProperty("ok", out var okElement)
                    && okElement.ValueKind == JsonValueKind.True;

                if (!ok)
                {
                    var error = root.TryGetProperty("error", out var err) ? err.GetString() : null;
                    return Result.Failure<OutboundReceipt>(
                        MessagingErrors.DeliveryFailed(Platform, error ?? "unknown error"));
                }

                var ts = root.TryGetProperty("ts", out var tsElement) ? tsElement.GetString() : null;
                return Result.Success(new OutboundReceipt(ts, DateTimeOffset.UtcNow));
            }
            catch (JsonException)
            {
                return Result.Failure<OutboundReceipt>(MessagingErrors.DeliveryFailed(Platform, "malformed response"));
            }
        }
    }

    private static InboundMessage MapMessage(JsonElement evt)
    {
        var channel = evt.TryGetProperty("channel", out var c) ? c.GetString() ?? string.Empty : string.Empty;
        var text = evt.TryGetProperty("text", out var t) ? t.GetString() ?? string.Empty : string.Empty;
        var user = evt.TryGetProperty("user", out var u) ? u.GetString() : null;
        var ts = evt.TryGetProperty("ts", out var tsElement) ? tsElement.GetString() : null;

        var timestamp = DateTimeOffset.UtcNow;
        if (!string.IsNullOrEmpty(ts))
        {
            // Slack ts is "{seconds}.{counter}" — take the integer (seconds) part.
            var dot = ts.IndexOf('.', StringComparison.Ordinal);
            var secondsPart = dot >= 0 ? ts[..dot] : ts;
            if (long.TryParse(secondsPart, NumberStyles.Integer, CultureInfo.InvariantCulture, out var unix))
            {
                timestamp = DateTimeOffset.FromUnixTimeSeconds(unix);
            }
        }

        return new InboundMessage
        {
            Platform = MessagingPlatforms.Slack,
            ConversationId = channel,
            Text = text,
            SenderId = user,
            MessageId = ts,
            Timestamp = timestamp,
        };
    }

    /// <summary>
    /// Whether <paramref name="timestamp"/> (Unix seconds) is within <see cref="SlackOptions.TimestampTolerance"/>
    /// of now. The signature proves who wrote a request, not when: without this a captured request could
    /// be replayed forever.
    /// </summary>
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

    private static bool VerifySignature(string body, string signingSecret, string? timestamp, string? signature)
    {
        if (string.IsNullOrEmpty(timestamp) || string.IsNullOrEmpty(signature))
        {
            return false;
        }

        var baseString = $"{SignatureVersion}:{timestamp}:{body}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(signingSecret));
        var computed = SignatureVersion + "=" +
            Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(baseString))).ToLowerInvariant();

        return CryptographicOperations.FixedTimeEquals(
            Encoding.UTF8.GetBytes(computed),
            Encoding.UTF8.GetBytes(signature.Trim()));
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
