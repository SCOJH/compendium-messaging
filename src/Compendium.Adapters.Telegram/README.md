# Compendium.Adapters.Telegram

Telegram Bot API adapter for the [Compendium](https://github.com/sassy-solutions/compendium)
framework. Implements `IMessagingConnector` (from `Compendium.Abstractions.Messaging`) so an
agent or service can receive and reply to Telegram messages with a normalized, multi-tenant API.

- **Inbound**: verifies the `X-Telegram-Bot-Api-Secret-Token` header (constant-time comparison,
  when a `secretToken` is configured) and projects a Telegram
  `Update` onto a normalized `InboundMessage` (chat id, text, sender, attachments).
- **Outbound**: sends replies via `POST /bot{token}/sendMessage`.
- **Multi-tenant**: the bot token + webhook secret are supplied per call via `ChannelCredentials`,
  so a single registered connector serves many tenants.

## Usage

```csharp
services.AddTelegramMessaging();                 // or AddTelegramMessaging(o => o.BaseUrl = "...")

// inbound (in your webhook endpoint):
var creds = new ChannelCredentials(new Dictionary<string,string>
{
    ["botToken"]    = tenantBotToken,
    ["secretToken"] = tenantWebhookSecret,
});
var parsed = connector.ParseInbound(new InboundRequest(headers, body), creds);

// outbound:
await connector.SendAsync(new OutboundMessage { ConversationId = chatId, Text = reply }, creds, ct);
```

## Credential keys

| Key           | Required | Purpose                                           |
|---------------|----------|---------------------------------------------------|
| `botToken`    | to send  | Bot API token (`123456:ABC...`)                   |
| `secretToken` | optional | Verifies the inbound webhook `X-Telegram-Bot-Api-Secret-Token` header |

## Build

```bash
dotnet build && dotnet test
```

> Note: `Compendium.Abstractions.Messaging` is currently consumed from a local feed
> (`../.local-nuget`) until it is published to nuget.org. Once published, bump the version in
> `Directory.Packages.props` and remove the `local` source from `nuget.config`.
