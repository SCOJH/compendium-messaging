// -----------------------------------------------------------------------
// <copyright file="SlackMessagingConnectorTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
// </copyright>
// -----------------------------------------------------------------------

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Compendium.Abstractions.Messaging;
using Compendium.Abstractions.Messaging.Models;
using Compendium.Adapters.Slack;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Compendium.Adapters.Slack.Tests;

public sealed class SlackMessagingConnectorTests
{
    private const string SigningSecret = "signing-secret-123";
    private const string Timestamp = "1700000000";

    private static SlackMessagingConnector Build(StubHandler? handler = null) =>
        new(new StubHttpClientFactory(handler ?? new StubHandler(HttpStatusCode.OK, "{}")),
            Options.Create(new SlackOptions()),
            NullLogger<SlackMessagingConnector>.Instance);

    private static ChannelCredentials Creds() => new(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["signingSecret"] = SigningSecret,
        ["botToken"] = "xoxb-token",
    });

    private const string MessageBody =
        """
        {"type":"event_callback","event":{
          "type":"message","channel":"C123","user":"U999",
          "ts":"1700000000.0002","text":"hello there"
        }}
        """;

    private static string Sign(string body, string timestamp = Timestamp)
    {
        var baseString = $"v0:{timestamp}:{body}";
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(SigningSecret));
        return "v0=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(baseString))).ToLowerInvariant();
    }

    [Fact]
    public void ParseInbound_UrlVerification_EchoesChallenge()
    {
        var connector = Build();
        var request = new InboundRequest(
            new Dictionary<string, string>(),
            """{"type":"url_verification","challenge":"abc123challenge"}""");

        var result = connector.ParseInbound(request, Creds());

        result.IsSuccess.Should().BeTrue();
        result.Value.Messages.Should().BeEmpty();
        result.Value.Acknowledgement.Should().NotBeNull();
        result.Value.Acknowledgement!.StatusCode.Should().Be(200);
        result.Value.Acknowledgement.Body.Should().Be("abc123challenge");
        result.Value.Acknowledgement.ContentType.Should().Be("text/plain");
    }

    [Fact]
    public void ParseInbound_PostWithValidSignature_ParsesMessage()
    {
        var connector = Build();
        var headers = new Dictionary<string, string>
        {
            ["X-Slack-Request-Timestamp"] = Timestamp,
            ["X-Slack-Signature"] = Sign(MessageBody),
        };
        var request = new InboundRequest(headers, MessageBody);

        var result = connector.ParseInbound(request, Creds());

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : null);
        var message = result.Value.Messages.Should().ContainSingle().Subject;
        message.Platform.Should().Be(MessagingPlatforms.Slack);
        message.ConversationId.Should().Be("C123");
        message.Text.Should().Be("hello there");
        message.SenderId.Should().Be("U999");
        message.MessageId.Should().Be("1700000000.0002");
        message.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000000));
    }

    [Fact]
    public void ParseInbound_PostWithInvalidSignature_Fails()
    {
        var connector = Build();
        var headers = new Dictionary<string, string>
        {
            ["X-Slack-Request-Timestamp"] = Timestamp,
            ["X-Slack-Signature"] = "v0=deadbeef",
        };
        var request = new InboundRequest(headers, MessageBody);

        var result = connector.ParseInbound(request, Creds());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Messaging.InvalidSignature");
    }

    [Fact]
    public void ParseInbound_PostWithBotMessage_YieldsEmptyEnvelope()
    {
        var connector = Build();
        const string botBody =
            """
            {"type":"event_callback","event":{
              "type":"message","channel":"C123","bot_id":"B1","ts":"1700000000.0002","text":"hi"
            }}
            """;
        var headers = new Dictionary<string, string>
        {
            ["X-Slack-Request-Timestamp"] = Timestamp,
            ["X-Slack-Signature"] = Sign(botBody),
        };
        var request = new InboundRequest(headers, botBody);

        var result = connector.ParseInbound(request, Creds());

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : null);
        result.Value.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_PostsToChatPostMessage_AndReturnsReceipt()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"ok\":true,\"ts\":\"1700000005.0007\"}");
        var connector = Build(handler);

        var result = await connector.SendAsync(
            new OutboundMessage { ConversationId = "C123", Text = "your route is ready" },
            Creds(),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : null);
        result.Value.MessageId.Should().Be("1700000005.0007");
        handler.LastRequestUri.Should().Contain("/api/chat.postMessage");
        handler.LastAuthorization.Should().Be("Bearer xoxb-token");
        handler.LastBody.Should().Contain("\"channel\":\"C123\"").And.Contain("your route is ready");
    }

    [Fact]
    public async Task SendAsync_WhenOkFalse_FailsWithDeliveryFailed()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"ok\":false,\"error\":\"channel_not_found\"}");
        var connector = Build(handler);

        var result = await connector.SendAsync(
            new OutboundMessage { ConversationId = "C123", Text = "hi" },
            Creds(),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Messaging.DeliveryFailed");
        result.Error.Message.Should().Contain("channel_not_found");
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

    private sealed class StubHttpClientFactory : IHttpClientFactory
    {
        private readonly HttpMessageHandler _handler;

        public StubHttpClientFactory(HttpMessageHandler handler) => _handler = handler;

        public HttpClient CreateClient(string name) => new(_handler, disposeHandler: false);
    }
}
