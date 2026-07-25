# Compendium.Adapters.Teams

Microsoft Teams (Bot Framework) adapter for the [Compendium](https://github.com/sassy-solutions/compendium)
framework. Implements `IMessagingConnector` (from `Compendium.Abstractions.Messaging`).

- **Inbound (POST)**: parses a Bot Framework [Activity](https://learn.microsoft.com/azure/bot-service/rest-api/bot-framework-rest-connector-activities)
  JSON. When `activity.type == "message"` it projects `conversation.id`, `text`, `from.id`,
  `from.name`, `id`, and `serviceUrl` (kept in `Metadata`) onto a normalized `InboundMessage`.
  Any other activity type yields `InboundEnvelope.Empty`.
- **Outbound**: a two-step send — (a) acquire an AAD client-credentials token from
  `{LoginUrl}/oauth2/v2.0/token`, then (b) `POST {serviceUrl}/v3/conversations/{conversationId}/activities`
  with `Authorization: Bearer {token}` and `{"type":"message","text":...}`.
- **Multi-tenant**: `appId` and `appPassword` are supplied per call via `ChannelCredentials`.

## Credential keys

| Key           | Purpose                                                      |
|---------------|-------------------------------------------------------------|
| `appId`       | Bot Framework application (client) id — token `client_id`   |
| `appPassword` | Bot Framework application password — token `client_secret`  |

## Options

| Option                 | Default                                                  | Purpose                                                                 |
|------------------------|----------------------------------------------------------|-------------------------------------------------------------------------|
| `LoginUrl`             | `https://login.microsoftonline.com/botframework.com`     | Azure AD authority; token endpoint is `{LoginUrl}/oauth2/v2.0/token`    |
| `BaseUrl`              | _(unset)_                                                | Optional fixed Bot Connector service URL; overrides per-message value   |
| `ValidateInboundToken` | `false`                                                  | Reserved; see "Known simplification" below (currently has no effect)    |

## serviceUrl

The Bot Connector `serviceUrl` is a per-conversation value that Teams sends on each inbound
activity. This adapter captures it into `InboundMessage.Metadata["serviceUrl"]`. To reply, pass it
back via `OutboundMessage.Options["serviceUrl"]` (or set `TeamsOptions.BaseUrl` to a fixed value).
If neither is present, `SendAsync` returns `MessagingErrors.MissingCredential("serviceUrl")`.

## Usage

```csharp
services.AddTeamsMessaging();   // or AddTeamsMessaging(o => o.LoginUrl = "https://login.microsoftonline.com/botframework.com")
```

## Known simplification (v1): inbound JWT is not validated

For v1, inbound Bot Framework JWTs are **not** validated. The `Authorization` bearer on inbound
POSTs is ignored and the Activity is parsed as received. This is intentionally gated behind
`TeamsOptions.ValidateInboundToken` (default `false`); setting it to `true` currently has **no
effect** because validation is not yet implemented.

Production hosts that require end-to-end authenticity should validate the JWT in front of this
connector — checking the issuer, the audience (the bot `appId`), and the Bot Framework OpenID
metadata signing keys — until token validation is implemented here.

> `Compendium.Abstractions.Messaging` is consumed from a local feed (`../.local-nuget`) until it
> is published to nuget.org.
