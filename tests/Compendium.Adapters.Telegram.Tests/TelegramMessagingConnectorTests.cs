// -----------------------------------------------------------------------
// <copyright file="TelegramMessagingConnectorTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
// </copyright>
// -----------------------------------------------------------------------

using System.Net;
using System.Text;
using Compendium.Abstractions.Messaging;
using Compendium.Abstractions.Messaging.Models;
using Compendium.Adapters.Telegram;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Compendium.Adapters.Telegram.Tests;

public sealed class TelegramMessagingConnectorTests
{
    private static TelegramMessagingConnector Build(StubHandler? handler = null)
    {
        var factory = new StubHttpClientFactory(handler ?? new StubHandler(HttpStatusCode.OK, "{\"ok\":true}"));
        return new TelegramMessagingConnector(
            factory,
            Options.Create(new TelegramOptions()),
            NullLogger<TelegramMessagingConnector>.Instance);
    }

    private static ChannelCredentials Creds(string? botToken = "123:ABC", string? secret = null)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        if (botToken is not null)
        {
            values["botToken"] = botToken;
        }

        if (secret is not null)
        {
            values["secretToken"] = secret;
        }

        return new ChannelCredentials(values);
    }

    private const string ValidUpdate =
        """
        {"update_id":1,"message":{"message_id":42,"date":1700000000,
         "chat":{"id":555,"type":"private"},
         "from":{"id":777,"username":"alice","first_name":"Alice"},
         "text":"bonjour"}}
        """;

    [Fact]
    public void ParseInbound_ValidTextMessage_NormalizesFields()
    {
        var connector = Build();
        var request = new InboundRequest(new Dictionary<string, string>(), ValidUpdate);

        var result = connector.ParseInbound(request, Creds());

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : null);
        var messages = result.Value.Messages;
        messages.Should().ContainSingle();
        var message = messages[0];
        message.Platform.Should().Be(MessagingPlatforms.Telegram);
        message.ConversationId.Should().Be("555");
        message.Text.Should().Be("bonjour");
        message.SenderId.Should().Be("777");
        message.SenderDisplayName.Should().Be("alice");
        message.MessageId.Should().Be("42");
        message.Timestamp.Should().Be(DateTimeOffset.FromUnixTimeSeconds(1700000000));
    }

    [Fact]
    public void ParseInbound_WithMatchingSecret_Succeeds()
    {
        var connector = Build();
        var headers = new Dictionary<string, string> { ["X-Telegram-Bot-Api-Secret-Token"] = "s3cr3t" };
        var request = new InboundRequest(headers, ValidUpdate);

        var result = connector.ParseInbound(request, Creds(secret: "s3cr3t"));

        result.IsSuccess.Should().BeTrue();
        result.Value.Messages.Should().ContainSingle();
    }

    [Fact]
    public void ParseInbound_WithWrongSecret_FailsWithInvalidSignature()
    {
        var connector = Build();
        var headers = new Dictionary<string, string> { ["X-Telegram-Bot-Api-Secret-Token"] = "wrong" };
        var request = new InboundRequest(headers, ValidUpdate);

        var result = connector.ParseInbound(request, Creds(secret: "s3cr3t"));

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Messaging.InvalidSignature");
    }

    [Fact]
    public void ParseInbound_NonMessageUpdate_ReturnsEmptyEnvelope()
    {
        var connector = Build();
        var request = new InboundRequest(
            new Dictionary<string, string>(),
            "{\"update_id\":2,\"my_chat_member\":{\"date\":1700000000}}");

        var result = connector.ParseInbound(request, Creds());

        result.IsSuccess.Should().BeTrue();
        result.Value.Messages.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_Success_ReturnsReceiptWithMessageId()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"ok\":true,\"result\":{\"message_id\":99}}");
        var connector = Build(handler);

        var result = await connector.SendAsync(
            new OutboundMessage { ConversationId = "555", Text = "salut" },
            Creds(),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : null);
        result.Value.MessageId.Should().Be("99");
        handler.LastRequestUri.Should().Contain("/bot123:ABC/sendMessage");
        handler.LastBody.Should().Contain("\"chat_id\":\"555\"").And.Contain("\"text\":\"salut\"");
    }

    [Fact]
    public async Task SendAsync_MissingBotToken_FailsWithMissingCredential()
    {
        var connector = Build();

        var result = await connector.SendAsync(
            new OutboundMessage { ConversationId = "555", Text = "x" },
            Creds(botToken: null),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Messaging.MissingCredential");
    }

    [Fact]
    public async Task SendAsync_Non2xx_FailsWithDeliveryFailed()
    {
        var handler = new StubHandler(HttpStatusCode.BadRequest, "{\"ok\":false,\"description\":\"bad\"}");
        var connector = Build(handler);

        var result = await connector.SendAsync(
            new OutboundMessage { ConversationId = "555", Text = "x" },
            Creds(),
            CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Messaging.DeliveryFailed");
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

        public string? LastBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequestUri = request.RequestUri?.ToString();
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
