# Compendium.Adapters.Slack

Slack adapter for the [Compendium](https://github.com/sassy-solutions/compendium)
framework. Implements `IMessagingConnector` (from `Compendium.Abstractions.Messaging`).

- **Every inbound request** — `url_verification` included, since Slack signs it too — must carry a
  valid `X-Slack-Signature` (v0) HMAC-SHA256 over `v0:{X-Slack-Request-Timestamp}:{rawBody}`
  (signing secret), and a timestamp within `SlackOptions.TimestampTolerance` (5 minutes by default,
  either way) of the current time: a captured request cannot be replayed later.
- **Inbound verification (`url_verification`)**: once verified, echoes the `challenge` field back as
  `text/plain`, completing the Events API handshake.
- **Inbound messages (POST)**: projects a `message` event (with no `bot_id`) onto a normalized
  `InboundMessage`.
- **Outbound**: `POST https://slack.com/api/chat.postMessage` with `Authorization: Bearer {botToken}`.
- **Multi-tenant**: signing secret and bot token are supplied per call via `ChannelCredentials`.

## Credential keys

| Key             | Purpose                                              |
|-----------------|------------------------------------------------------|
| `signingSecret` | HMAC verification of inbound POSTs (`X-Slack-Signature`) |
| `botToken`      | Bearer token for the Web API (`chat.postMessage`)    |

## Usage

```csharp
services.AddSlackMessaging();   // or AddSlackMessaging(o => o.BaseUrl = "https://slack.com")
```

> `Compendium.Abstractions.Messaging` is consumed from a local feed (`../.local-nuget`) until it
> is published to nuget.org.
