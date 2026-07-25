# Migration record — messaging domain repo

This repository assembles the Compendium **messaging** domain (repo-per-domain
topology, ADR-0007): the `Compendium.Abstractions.Messaging` port plus the five
platform adapters that implement it. Package IDs are unchanged — consumers keep
the same `PackageReference` names and only move to the `1.1.x` version train.

## Component provenance

| Component | Source repo | Source HEAD SHA | Notes |
|---|---|---|---|
| `src/Compendium.Abstractions.Messaging` | `sassy-solutions/compendium` | `5b8d420d31b47b643813db31f23ef85b903993f5` (see notes) | Reconstructed — see below. |
| `src/Compendium.Adapters.Discord` + tests | `compendium-adapter-discord` (local clone, `main`) | `5becc9ca9a33fded20b2b7122d1063df8737c7ec` | Verbatim; csproj: Messaging PackageReference → ProjectReference. |
| `src/Compendium.Adapters.Slack` + tests | `compendium-adapter-slack` (local clone, `main`) | `a98dd5bd876aa39d9a7d4a840b65c8332187fdb1` | Verbatim; same csproj change. |
| `src/Compendium.Adapters.Teams` + tests | `compendium-adapter-teams` (local clone, `main`) | `f2aab9dce11cad0d1cb1127f126e4592d886eed6` | Verbatim; same csproj change. |
| `src/Compendium.Adapters.Telegram` + tests | `compendium-adapter-telegram` (local clone, `main`) | `94018a98eba1183b198a889cdca5fec84ad8ee3d` | Verbatim; same csproj change. |
| `src/Compendium.Adapters.WhatsApp` + tests | `compendium-adapter-whatsapp` (local clone, `main`) | `49053968235004484e3c217bf65f8388a9cc6839` | Verbatim; same csproj change. |
| Scaffold (props, workflows, global.json, tools manifest, .gitignore) | `compendium-adapter-supabase` (local clone) | — | Adapted: repo URLs, 6-nupkg release assert, 80% coverage gate. |

## Abstraction provenance (important)

`Compendium.Abstractions.Messaging` is **not present on the framework repo's
`origin/main`** (`792dd626496f69a4f7c86ce79f48795e6aba7e34` at assembly time,
2026-07-25). It only exists on an in-flight framework branch that is actively
being evolved by another workstream and was therefore off-limits as a source.

The source here was **reconstructed against the frozen `1.0.4` package**
(`Compendium.Abstractions.Messaging.1.0.4.nupkg`, local feed) that all five
adapters were built and pinned against. That package's nuspec records framework
commit `5b8d420d31b47b643813db31f23ef85b903993f5`
("feat(messaging): add Compendium.Abstractions.Messaging") as its build source.
Reconstruction inputs:

- the package's XML documentation file (full public API + doc comments),
- reflection + behavior probes against the 1.0.4 assembly (constants, error
  codes/messages/types, record shapes, required/init members, defaults,
  `ChannelCredentials` semantics),
- framework `origin/main` sibling abstractions (file layout + style conventions).

Equivalence evidence: all five adapters compile against the in-repo abstraction
with `TreatWarningsAsErrors` and their full unit suites pass (33/33), covering
signature verification, payload parsing and error paths end-to-end.

When the framework branch lands, diff its
`src/Abstractions/Compendium.Abstractions.Messaging` against this repo and
fold in any intentional evolution; removal of the abstraction from the
framework repo is a separate later step.

## Deliberate changes vs sources

- Adapters reference the abstraction via `ProjectReference` (the atomic-PR
  benefit of the domain repo) instead of the local-feed `PackageReference`;
  the `Compendium.Abstractions.Messaging` pin and the `../.local-nuget` feed
  are gone.
- `Compendium.Core` / `Compendium.Abstractions` pins raised 1.0.4 →
  `1.0.5-preview.1` (nuget.org). No API drift: clean build, all tests green.
- `Directory.Packages.props` is the union of the source repos' pins; version
  conflicts resolved by taking the highest (e.g. NSubstitute 5.3.0).
- `TreatWarningsAsErrors=true` repo-wide (sources had `false`); the assembled
  code builds clean under it.
- Per-adapter repo README.md files moved into each project directory so every
  package keeps its own README (`PackageReadmeFile`).
- Version train starts at `v1.1.0-preview.1` — above the framework's `1.0.x` —
  so domain-repo packages win NuGet resolution over any framework-era ones.
