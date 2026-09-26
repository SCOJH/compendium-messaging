// -----------------------------------------------------------------------
// <copyright file="TeamsMessagingConnectorTests.cs" company="Sassy Solutions">
//     Copyright (c) 2026 Sassy Solutions. Licensed under the MIT License.
// </copyright>
// -----------------------------------------------------------------------

using System.Net;
using System.Text;
using Compendium.Abstractions.Messaging;
using Compendium.Abstractions.Messaging.Models;
using Compendium.Adapters.Teams;
using AwesomeAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace Compendium.Adapters.Teams.Tests;

public sealed class TeamsMessagingConnectorTests
{
    private const string ServiceUrl = "https://smba.trafficmanager.net/teams";

    private static TeamsMessagingConnector Build(StubHandler? handler = null, TeamsOptions? options = null) =>
        new(new StubHttpClientFactory(handler ?? new StubHandler()),
            Options.Create(options ?? new TeamsOptions()),
            NullLogger<TeamsMessagingConnector>.Instance);

    private static ChannelCredentials Creds() => new(new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["appId"] = "app-id-123",
        ["appPassword"] = "app-password-456",
    });

    // A Bot Framework "message" Activity.
    private const string MessageActivity =
        """
        {
          "type": "message",
          "id": "1700000000001",
          "timestamp": "2026-06-20T12:00:00.000Z",
          "serviceUrl": "https://smba.trafficmanager.net/teams",
          "conversation": { "id": "19:meeting_abc@thread.v2" },
          "from": { "id": "29:user-aad-id", "name": "Sacha Hennaut" },
          "text": "une pizza svp"
        }
        """;

    // A non-message Activity (e.g. members added to the conversation).
    private const string ConversationUpdateActivity =
        """
        { "type": "conversationUpdate", "id": "abc", "conversation": { "id": "19:x@thread.v2" } }
        """;

    [Fact]
    public void ParseInbound_MessageActivity_ParsesNormalizedMessage()
    {
        var connector = Build();
        var request = new InboundRequest(new Dictionary<string, string>(), MessageActivity);

        var result = connector.ParseInbound(request, Creds());

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : null);
        var message = result.Value.Messages.Should().ContainSingle().Subject;
        message.Platform.Should().Be(MessagingPlatforms.Teams);
        message.ConversationId.Should().Be("19:meeting_abc@thread.v2");
        message.Text.Should().Be("une pizza svp");
        message.SenderId.Should().Be("29:user-aad-id");
        message.SenderDisplayName.Should().Be("Sacha Hennaut");
        message.MessageId.Should().Be("1700000000001");
        message.Metadata.Should().ContainKey("serviceUrl")
            .WhoseValue.Should().Be(ServiceUrl);
    }

    [Fact]
    public void ParseInbound_NonMessageActivity_ReturnsEmptyEnvelope()
    {
        var connector = Build();
        var request = new InboundRequest(new Dictionary<string, string>(), ConversationUpdateActivity);

        var result = connector.ParseInbound(request, Creds());

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : null);
        result.Value.Messages.Should().BeEmpty();
    }

    [Fact]
    public void ParseInbound_MalformedJson_Fails()
    {
        var connector = Build();
        var request = new InboundRequest(new Dictionary<string, string>(), "{ not json");

        var result = connector.ParseInbound(request, Creds());

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Messaging.MalformedPayload");
    }

    [Fact]
    public async Task SendAsync_AcquiresToken_ThenPostsActivity_AndReturnsReceipt()
    {
        var handler = new StubHandler(
            new StubResponse(HttpStatusCode.OK, "{\"access_token\":\"aad-token-xyz\",\"expires_in\":3600}"),
            new StubResponse(HttpStatusCode.OK, "{\"id\":\"activity-out-1\"}"));
        var connector = Build(handler);

        var outbound = new OutboundMessage
        {
            ConversationId = "19:meeting_abc@thread.v2",
            Text = "Your route is ready",
            Options = new Dictionary<string, string>(StringComparer.Ordinal) { ["serviceUrl"] = ServiceUrl },
        };

        var result = await connector.SendAsync(outbound, Creds(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : null);
        result.Value.MessageId.Should().Be("activity-out-1");

        // Two HTTP calls were made: the token request first, then the activity post.
        handler.Requests.Should().HaveCount(2);

        var tokenCall = handler.Requests[0];
        tokenCall.Uri.Should().Be("https://login.microsoftonline.com/botframework.com/oauth2/v2.0/token");
        tokenCall.Body.Should().Contain("grant_type=client_credentials");
        tokenCall.Body.Should().Contain("client_id=app-id-123");
        tokenCall.Body.Should().Contain("client_secret=app-password-456");
        tokenCall.Body.Should().Contain("scope=https%3A%2F%2Fapi.botframework.com%2F.default");

        var activityCall = handler.Requests[1];
        activityCall.Uri.Should().Be(
            "https://smba.trafficmanager.net/teams/v3/conversations/19:meeting_abc@thread.v2/activities");
        activityCall.Authorization.Should().Be("Bearer aad-token-xyz");
        activityCall.Body.Should().Contain("\"type\":\"message\"");
        activityCall.Body.Should().Contain("Your route is ready");
    }

    [Fact]
    public async Task SendAsync_UsesOptionsBaseUrl_WhenConfigured()
    {
        var handler = new StubHandler(
            new StubResponse(HttpStatusCode.OK, "{\"access_token\":\"aad-token-xyz\"}"),
            new StubResponse(HttpStatusCode.OK, "{\"id\":\"activity-out-2\"}"));
        var connector = Build(handler, new TeamsOptions { BaseUrl = "https://fixed.example.com" });

        var outbound = new OutboundMessage
        {
            ConversationId = "19:conv@thread.v2",
            Text = "hello",
        };

        var result = await connector.SendAsync(outbound, Creds(), CancellationToken.None);

        result.IsSuccess.Should().BeTrue(result.IsFailure ? result.Error.Message : null);
        handler.Requests[1].Uri.Should().Be(
            "https://fixed.example.com/v3/conversations/19:conv@thread.v2/activities");
    }

    [Fact]
    public async Task SendAsync_MissingServiceUrl_ReturnsMissingCredential()
    {
        var handler = new StubHandler();
        var connector = Build(handler);

        var outbound = new OutboundMessage
        {
            ConversationId = "19:conv@thread.v2",
            Text = "hello",
            // No Options serviceUrl and no TeamsOptions.BaseUrl.
        };

        var result = await connector.SendAsync(outbound, Creds(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Messaging.MissingCredential");
        // No HTTP call should have been attempted.
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_MissingAppId_ReturnsMissingCredential()
    {
        var handler = new StubHandler();
        var connector = Build(handler);
        var creds = new ChannelCredentials(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["appPassword"] = "app-password-456",
        });

        var outbound = new OutboundMessage
        {
            ConversationId = "19:conv@thread.v2",
            Text = "hello",
            Options = new Dictionary<string, string>(StringComparer.Ordinal) { ["serviceUrl"] = ServiceUrl },
        };

        var result = await connector.SendAsync(outbound, creds, CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Messaging.MissingCredential");
        handler.Requests.Should().BeEmpty();
    }

    [Fact]
    public async Task SendAsync_TokenEndpointFails_ReturnsDeliveryFailed()
    {
        var handler = new StubHandler(
            new StubResponse(HttpStatusCode.Unauthorized, "{\"error\":\"invalid_client\"}"));
        var connector = Build(handler);

        var outbound = new OutboundMessage
        {
            ConversationId = "19:conv@thread.v2",
            Text = "hello",
            Options = new Dictionary<string, string>(StringComparer.Ordinal) { ["serviceUrl"] = ServiceUrl },
        };

        var result = await connector.SendAsync(outbound, Creds(), CancellationToken.None);

        result.IsFailure.Should().BeTrue();
        result.Error.Code.Should().Be("Messaging.DeliveryFailed");
        // Only the token call happened; the activity call was never reached.
        handler.Requests.Should().HaveCount(1);
    }

    private sealed record CapturedRequest(string? Uri, string? Authorization, string? Body);

    private sealed record StubResponse(HttpStatusCode Status, string Body);

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Queue<StubResponse> _responses;

        public StubHandler(params StubResponse[] responses)
        {
            _responses = new Queue<StubResponse>(
                responses.Length > 0 ? responses : new[] { new StubResponse(HttpStatusCode.OK, "{}") });
        }

        public List<CapturedRequest> Requests { get; } = new();

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            string? body = null;
            if (request.Content is not null)
            {
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            Requests.Add(new CapturedRequest(
                request.RequestUri?.ToString(),
                request.Headers.Authorization?.ToString(),
                body));

            var response = _responses.Count > 1 ? _responses.Dequeue() : _responses.Peek();
            return new HttpResponseMessage(response.Status)
            {
                Content = new StringContent(response.Body, Encoding.UTF8, "application/json"),
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
