# Compendium.Adapters.WhatsApp

WhatsApp Cloud API adapter for the [Compendium](https://github.com/sassy-solutions/compendium)
framework. Implements `IMessagingConnector` (from `Compendium.Abstractions.Messaging`).

- **Inbound verification (GET)**: validates the `hub.verify_token` and echoes `hub.challenge`.
- **Inbound messages (POST)**: verifies the `X-Hub-Signature-256` HMAC-SHA256 (app secret) and
  projects the nested `entry/changes/value/messages` payload onto normalized `InboundMessage`s.
- **Outbound**: `POST https://graph.facebook.com/{version}/{phone_number_id}/messages`.
- **Multi-tenant**: verify token, app secret, access token and phone-number id are supplied per
  call via `ChannelCredentials`.

## Credential keys

| Key             | Purpose                                              |
|-----------------|------------------------------------------------------|
| `verifyToken`   | webhook GET verification (`hub.verify_token`)        |
| `appSecret`     | HMAC verification of inbound POSTs                    |
| `accessToken`   | Bearer token for the Graph API (outbound)            |
| `phoneNumberId` | the WhatsApp Business phone-number id (outbound URL) |

## Usage

```csharp
services.AddWhatsAppMessaging();   // or AddWhatsAppMessaging(o => o.ApiVersion = "v21.0")
```

> `Compendium.Abstractions.Messaging` is consumed from a local feed (`../.local-nuget`) until it
> is published to nuget.org.
