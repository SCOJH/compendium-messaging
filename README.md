# Compendium — Messaging domain

Conversational messaging for the [Compendium framework](https://github.com/sassy-solutions/compendium):
one provider-agnostic port, five platform adapters, one repo (repo-per-domain
topology, ADR-0007). The abstraction and its adapters version and ship together,
so a cross-cutting change is a single atomic PR.

## The port

`IMessagingConnector` (in `Compendium.Abstractions.Messaging`) is a two-way
connector to a chat platform:

- **`ParseInbound`** — verify a raw platform webhook (HMAC / Ed25519 / secret
  token) and normalize it into `InboundMessage` values, plus an optional
  handshake `ChannelAck` to echo back (Slack `url_verification`, WhatsApp
  `hub.challenge`, Discord PING).
- **`SendAsync`** — deliver a normalized `OutboundMessage` reply via the
  platform's API.

Credentials (`ChannelCredentials`) are supplied **per call**, never baked into
the connector — a single registered connector serves many tenants. All
operations return the Compendium `Result` pattern (`MessagingErrors.*`), never
exceptions for control flow.

## Packages

| Package | Description |
|---|---|
| `Compendium.Abstractions.Messaging` | The `IMessagingConnector` port, normalized models, `MessagingErrors`, `MessagingPlatforms`. |
| `Compendium.Adapters.Telegram` | Telegram Bot API — `X-Telegram-Bot-Api-Secret-Token` verification, `sendMessage` replies. |
| `Compendium.Adapters.Slack` | Slack Events API — v0 HMAC-SHA256 signature, `url_verification` challenge, `chat.postMessage`. |
| `Compendium.Adapters.Discord` | Discord Interactions — Ed25519 verification, PING/PONG handshake, REST sends. |
| `Compendium.Adapters.Teams` | Microsoft Teams (Bot Framework) — Activity parsing, AAD client-credentials token, Bot Connector replies. |
| `Compendium.Adapters.WhatsApp` | WhatsApp Cloud API — `hub.challenge` GET verification, `X-Hub-Signature-256` HMAC, Graph API sends. |

All packages target `net9.0` and are versioned together from git tags
(MinVer, tag prefix `v`, `1.1.x` train).

## Build

```bash
dotnet build -c Release
dotnet test  -c Release
```

CI enforces an 80% line-coverage gate (`.github/workflows/ci.yml`). Releases are
tag-driven (`.github/workflows/release.yml`): push a `v*` tag → pack all six
packages → publish to GitHub Packages (and nuget.org when `NUGET_API_KEY` is
available).

## Layout

```
src/Compendium.Abstractions.Messaging/   # the port + models
src/Compendium.Adapters.<Platform>/      # one adapter per platform
tests/Compendium.Adapters.<Platform>.Tests/
```

Adapters reference the abstraction by `ProjectReference`; consumers get it as a
normal NuGet dependency. Provenance of every component is recorded in
[MIGRATION.md](MIGRATION.md).
