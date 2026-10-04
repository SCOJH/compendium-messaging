// -----------------------------------------------------------------------
// <copyright file="DiscordMessagingConnectorTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
// </copyright>
// -----------------------------------------------------------------------

using System.Net;
using System.Text;
using Compendium.Abstractions.Messaging;
using Compendium.Abstractions.Messaging.Models;
using Compendium.Adapters.Discord;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using NSec.Cryptography;
using Xunit;

namespace Compendium.Adapters.Discord.Tests;

public sealed class DiscordMessagingConnectorTests
{
    // Ed25519 keypair generated once for the whole suite so we can sign request bodies.
    private static readonly Key SigningKey = Key.Create(
        SignatureAlgorithm.Ed25519,
        new KeyCreationParameters { ExportPolicy = KeyExportPolicies.AllowPlaintextExport });

    private static readonly string PublicKeyHex =
        Convert.ToHexString(SigningKey.PublicKey.Export(KeyBlobFormat.RawPublicKey)).ToLowerInvariant();

    private const string Timestamp = "1700000000";

    // The suite's requests are signed at Timestamp; the connector's clock reads 30 seconds later.
    private static readonly DateTimeOffset Now = DateTimeOffset.FromUnixTimeSeconds(1700000030);

    private static DiscordMessagingConnector Build(StubHandler? handler = null, DateTimeOffset? now = null) =>
        new(new StubHttpClientFactory(handler ?? new StubHandler(HttpStatusCode.OK, "{}")),
            Options.Create(new DiscordOptions()),
            NullLogger<DiscordMessagingConnector>.Instance,
            new FixedClock(now ?? Now));

    private static ChannelCredentials Creds() => new(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["publicKey"] = PublicKeyHex,
        ["botToken"] = "bot-token-xyz",
    });

    private static string Sign(string body, string timestamp = Timestamp)
    {
        var message = Encoding.UTF8.GetBytes(timestamp + body);
        var signature = SignatureAlgorithm.Ed25519.Sign(SigningKey, message);
        return Convert.ToHexString(signature).ToLowerInvariant();
    }

    private static InboundRequest SignedRequest(string body, string timestamp = Timestamp) =>
        new(
            new Dictionary<string, string>
            {
                ["X-Signature-Ed25519"] = Sign(body, timestamp),
                ["X-Signature-Timestamp"] = timestamp,
            },
            body);

    /// <summary>
    /// A correctly signed interaction whose timestamp is more than the tolerance away from now, in the
    /// past (captured and replayed) or in the future, is refused.
    /// </summary>
    [Theory]
    [InlineData(-301)]
    [InlineData(301)]
    [InlineData(-86_400)]
    public void ParseInbound_SignedTimestampOutsideTheTolerance_IsRefused(int secondsFromNow)
    {
        var connector = Build(now: Now);
        var stamp = (Now.ToUnixTimeSeconds() + secondsFromNow).ToString(System.Globalization.CultureInfo.InvariantCulture);

        var result = connector.ParseInbound(SignedRequest("""{"type":1}""", stamp), Creds());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Messaging.InvalidSignature");
    }

    [Theory]
    [InlineData(-299)]
    [InlineData(299)]
    public void ParseInbound_SignedTimestampWithinTheTolerance_IsAccepted(int secondsFromNow)
    {
        var connector = Build(now: Now);
        var stamp = (Now.ToUnixTimeSeconds() + secondsFromNow).ToString(System.Globalization.CultureInfo.InvariantCulture);

        var result = connector.ParseInbound(SignedRequest("""{"type":1}""", stamp), Creds());

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : null);
    }

    [Theory]
    [InlineData("")]
    [InlineData("soon")]
    [InlineData("9223372036854775807")]
    public void ParseInbound_UnreadableTimestamp_IsRefused(string stamp)
    {
        var result = Build().ParseInbound(SignedRequest("""{"type":1}""", stamp), Creds());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Messaging.InvalidSignature");
    }

    [Fact]
    public void ParseInbound_Ping_ReturnsPongAck()
    {
        var connector = Build();
        var body = "{\"type\":1}";

        var result = connector.ParseInbound(SignedRequest(body), Creds());

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : null);
        result.Value.Messages.Should().BeEmpty();
        result.Value.Acknowledgement.Should().NotBeNull();
        result.Value.Acknowledgement!.StatusCode.Should().Be(200);
        result.Value.Acknowledgement.Body.Should().Be("{\"type\":1}");
        result.Value.Acknowledgement.ContentType.Should().Be("application/json");
    }

    [Fact]
    public void ParseInbound_ApplicationCommand_WithStringOption_ParsesMessage()
    {
        var connector = Build();
        var body =
            """
            {"id":"interaction-1","type":2,"channel_id":"chan-99",
             "member":{"user":{"id":"user-7","username":"sacha"}},
             "data":{"name":"order","options":[{"name":"item","type":3,"value":"one pizza please"}]}}
            """;

        var result = connector.ParseInbound(SignedRequest(body), Creds());

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : null);
        var message = result.Value.Messages.Should().ContainSingle().Subject;
        message.Platform.Should().Be(MessagingPlatforms.Discord);
        message.ConversationId.Should().Be("chan-99");
        message.Text.Should().Be("one pizza please");
        message.SenderId.Should().Be("user-7");
        message.SenderDisplayName.Should().Be("sacha");
        message.MessageId.Should().Be("interaction-1");
    }

    [Fact]
    public void ParseInbound_ApplicationCommand_WithoutOptions_FallsBackToCommandName()
    {
        var connector = Build();
        var body =
            """
            {"id":"interaction-2","type":2,"channel_id":"chan-1",
             "user":{"id":"user-3","username":"dm-user"},
             "data":{"name":"help"}}
            """;

        var result = connector.ParseInbound(SignedRequest(body), Creds());

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : null);
        var message = result.Value.Messages.Should().ContainSingle().Subject;
        message.ConversationId.Should().Be("chan-1");
        message.Text.Should().Be("help");
        message.SenderId.Should().Be("user-3");
        message.MessageId.Should().Be("interaction-2");
    }

    [Fact]
    public void ParseInbound_BadSignature_Fails()
    {
        var connector = Build();
        var body = "{\"type\":2,\"channel_id\":\"chan-1\",\"data\":{\"name\":\"help\"}}";
        var request = new InboundRequest(
            new Dictionary<string, string>
            {
                ["X-Signature-Ed25519"] = new string('0', 128),
                ["X-Signature-Timestamp"] = Timestamp,
            },
            body);

        var result = connector.ParseInbound(request, Creds());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Messaging.InvalidSignature");
    }

    [Fact]
    public void ParseInbound_MissingPublicKey_Fails()
    {
        var connector = Build();
        var creds = new ChannelCredentials(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["botToken"] = "bot-token-xyz",
        });

        var result = connector.ParseInbound(SignedRequest("{\"type\":1}"), creds);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Messaging.MissingCredential");
    }

    [Fact]
    public async Task SendAsync_PostsToChannelsApi_AndReturnsReceipt()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"id\":\"msg-out-1\"}");
        var connector = Build(handler);

        var result = await connector.SendAsync(
            new OutboundMessage { ConversationId = "chan-99", Text = "Your order is ready" },
            Creds(),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : null);
        result.Value.MessageId.Should().Be("msg-out-1");
        handler.LastRequestUri.Should().Contain("/api/v10/channels/chan-99/messages");
        handler.LastAuthorization.Should().Be("Bot bot-token-xyz");
        handler.LastBody.Should().Contain("\"content\":\"Your order is ready\"");
    }

    [Fact]
    public async Task SendAsync_MissingBotToken_Fails()
    {
        var connector = Build();
        var creds = new ChannelCredentials(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["publicKey"] = PublicKeyHex,
        });

        var result = await connector.SendAsync(
            new OutboundMessage { ConversationId = "chan-99", Text = "hi" },
            creds,
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Messaging.MissingCredential");
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly HttpStatusCode _status;
        private readonly string _body;

        public StubHandler(HttpStatusCode status, string body)
        {
            _status = status;
            _body = body;
        }

        public string? LastRequestUri { get; private set; }

        public string? LastAuthorization { get; private set; }

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
            LastAuthorization = request.Headers.Authorization?.ToString();
            if (request.Content is not null)
            {
                LastBody = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            return new HttpResponseMessage(_status)
            {
                Content = new StringContent(_body, Encoding.UTF8, "application/json"),
            };
        }
    }

    private sealed class FixedClock(DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
