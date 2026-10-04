# Compendium.Adapters.Discord

Discord Interactions adapter for the [Compendium](https://github.com/sassy-solutions/compendium)
framework. Implements `IMessagingConnector` (from `Compendium.Abstractions.Messaging`).

- **Inbound verification (POST)**: validates the `X-Signature-Ed25519` / `X-Signature-Timestamp`
  headers against the application's Ed25519 public key (per Discord's security model), and refuses a
  timestamp further than `DiscordOptions.TimestampTolerance` (5 minutes by default, either way) from
  the current time: a captured interaction cannot be replayed later.
- **PING handling**: replies to `type: 1` (PING) interactions with the `type: 1` (PONG) ack.
- **Inbound commands (POST)**: projects `type: 2` (APPLICATION_COMMAND) interactions onto a
  normalized `InboundMessage`.
- **Outbound**: `POST https://discord.com/api/{version}/channels/{channel_id}/messages` with a
  `Bot {token}` authorization header.
- **Multi-tenant**: the Ed25519 public key and bot token are supplied per call via
  `ChannelCredentials`.

## Credential keys

| Key         | Purpose                                                       |
|-------------|--------------------------------------------------------------|
| `publicKey` | hex-encoded Ed25519 application public key (inbound verify)  |
| `botToken`  | bot token for the Discord REST API (outbound `Bot` auth)     |

## Usage

```csharp
services.AddDiscordMessaging();   // or AddDiscordMessaging(o => o.ApiVersion = "v10")
```

> `Compendium.Abstractions.Messaging` is consumed from a local feed (`../.local-nuget`) until it
> is published to nuget.org.
