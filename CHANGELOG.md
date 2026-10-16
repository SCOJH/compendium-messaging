# Changelog

All notable changes to the Compendium messaging packages (`Compendium.Abstractions.Messaging` and the
`Compendium.Adapters.{Discord,Slack,Teams,Telegram,WhatsApp}` adapters) are recorded here. The format follows
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow the `v*` tags. Earlier versions are
described in the generated notes of the [GitHub Releases](https://github.com/SCOJH/compendium-messaging/releases).

## [Unreleased]

### Changed

- **Every package now targets `net9.0` and `net10.0`.** .NET 9 (STS) leaves support on 2026-11-10; .NET 10 is
  the LTS consumers are moving to, and `net9.0` stays for the transition only. `global.json` moves to the .NET 10
  SDK (10.0.100 or any later 10.0 feature band), which builds both targets; the CI and release workflows install
  the .NET 9 SDK beside it for its runtime, so every test runs once per target. On `net10.0` the
  `Microsoft.Extensions.*` family resolves to 10.0.13, which packages built for .NET 10 require; `net9.0` keeps
  the 9.0.16 versions it already shipped with. The `System.Text.Json` reference of the five adapters is removed:
  the shared framework provides the assembly on both targets, so the id no longer appears as a nuspec dependency
  and a `net9.0` consumer's graph loses that node with no effect at compile or run time. `Compendium.Core` and
  `Compendium.Abstractions` stay at 1.0.5-preview.1 (their `net9.0` assets serve both targets). (NET10-01,
  agent-spot/backlog#363)
