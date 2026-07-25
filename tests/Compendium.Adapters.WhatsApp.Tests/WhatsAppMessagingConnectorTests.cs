// -----------------------------------------------------------------------
// <copyright file="WhatsAppMessagingConnectorTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
// </copyright>
// -----------------------------------------------------------------------

using System.Net;
using System.Security.Cryptography;
using System.Text;
using Compendium.Abstractions.Messaging;
using Compendium.Abstractions.Messaging.Models;
using Compendium.Adapters.WhatsApp;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Compendium.Adapters.WhatsApp.Tests;

public sealed class WhatsAppMessagingConnectorTests
{
    private const string AppSecret = "app-secret-123";

    private static WhatsAppMessagingConnector Build(StubHandler? handler = null) =>
        new(new StubHttpClientFactory(handler ?? new StubHandler(HttpStatusCode.OK, "{}")),
            Options.Create(new WhatsAppOptions()),
            NullLogger<WhatsAppMessagingConnector>.Instance);

    private static ChannelCredentials Creds() => new(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["verifyToken"] = "verify-me",
        ["appSecret"] = AppSecret,
        ["accessToken"] = "EAAG-token",
        ["phoneNumberId"] = "123456",
    });

    private const string MessageBody =
        """
        {"object":"whatsapp_business_account","entry":[{"changes":[{"value":{
          "contacts":[{"profile":{"name":"Sacha"},"wa_id":"32470000000"}],
          "messages":[{"from":"32470000000","id":"wamid.ABC","timestamp":"1700000000",
                       "type":"text","text":{"body":"une pizza svp"}}]
        }}]}]}
        """;

    private static string Sign(string body)
    {
        using var hmac = new HMACSHA256(Encoding.UTF8.GetBytes(AppSecret));
        return "sha256=" + Convert.ToHexString(hmac.ComputeHash(Encoding.UTF8.GetBytes(body))).ToLowerInvariant();
    }

    [Fact]
    public void ParseInbound_GetVerification_WithCorrectToken_EchoesChallenge()
    {
        var connector = Build();
        var request = new InboundRequest(
            new Dictionary<string, string>(),
            string.Empty,
            "?hub.mode=subscribe&hub.verify_token=verify-me&hub.challenge=42");

        var result = connector.ParseInbound(request, Creds());

        result.IsSuccess.Should().BeTrue();
        result.Value.Acknowledgement.Should().NotBeNull();
        result.Value.Acknowledgement!.StatusCode.Should().Be(200);
        result.Value.Acknowledgement.Body.Should().Be("42");
    }

    [Fact]
    public void ParseInbound_GetVerification_WithWrongToken_Fails()
    {
        var connector = Build();
        var request = new InboundRequest(
            new Dictionary<string, string>(), string.Empty,
            "?hub.mode=subscribe&hub.verify_token=WRONG&hub.challenge=42");

        var result = connector.ParseInbound(request, Creds());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Messaging.InvalidSignature");
    }

    [Fact]
    public void ParseInbound_PostWithValidSignature_ParsesMessage()
    {
        var connector = Build();
        var headers = new Dictionary<string, string> { ["X-Hub-Signature-256"] = Sign(MessageBody) };
        var request = new InboundRequest(headers, MessageBody);

        var result = connector.ParseInbound(request, Creds());

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : null);
        var message = result.Value.Messages.Should().ContainSingle().Subject;
        message.Platform.Should().Be(MessagingPlatforms.WhatsApp);
        message.ConversationId.Should().Be("32470000000");
        message.Text.Should().Be("une pizza svp");
        message.SenderDisplayName.Should().Be("Sacha");
        message.MessageId.Should().Be("wamid.ABC");
    }

    [Fact]
    public void ParseInbound_PostWithInvalidSignature_Fails()
    {
        var connector = Build();
        var headers = new Dictionary<string, string> { ["X-Hub-Signature-256"] = "sha256=deadbeef" };
        var request = new InboundRequest(headers, MessageBody);

        var result = connector.ParseInbound(request, Creds());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Messaging.InvalidSignature");
    }

    [Fact]
    public async Task SendAsync_PostsToGraphApi_AndReturnsReceipt()
    {
        var handler = new StubHandler(HttpStatusCode.OK, "{\"messages\":[{\"id\":\"wamid.OUT\"}]}");
        var connector = Build(handler);

        var result = await connector.SendAsync(
            new OutboundMessage { ConversationId = "32470000000", Text = "Your route is ready" },
            Creds(),
            CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : null);
        result.Value.MessageId.Should().Be("wamid.OUT");
        handler.LastRequestUri.Should().Contain("/v21.0/123456/messages");
        handler.LastAuthorization.Should().Be("Bearer EAAG-token");
        handler.LastBody.Should().Contain("\"to\":\"32470000000\"").And.Contain("Your route is ready");
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
