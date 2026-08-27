# Interview Flow (.NET)

Native .NET 10 + Avalonia port of [Interview Flow](../interview-flow) — a local AI interview-prep coach. Runs on **Windows and macOS**, shares its **data formats** with the original app, and matches its on-screen markdown rendering.

Design docs live in [`docs/`](docs/README.md) — start there. Architecture decisions are in [`docs/adr/`](docs/adr/).

## Build & run

```
run.cmd                              # build + launch from the repo root
dotnet test InterviewFlow.slnx
```

## Settings

Copy `.env.example` to `.env` **in this folder** and fill in an API key. The app
reads `./.env` first (the repo root when launched via `run.cmd`, matching the
original's `Path(".env")`), then one beside the executable, then the per-user
config folder. Workflows are stored in `./data` when that folder exists,
otherwise under the per-user data folder — `INTERVIEW_DATA_DIR` overrides both.
The format is shared with the original Python app, so both can use one file.

Currently the app opens the **M0 markdown spike harness**: golden-corpus markdown source on the left, the native `MarkdownView` renderer on the right, live re-render on edit.

## Layout

```
src/InterviewFlow.App    Avalonia UI (net10.0, cross-platform)
src/InterviewFlow.Core   domain: models, state store, agents, providers, markdown pipeline
tests/InterviewFlow.Tests xunit; includes the ADR-001b mermaid spike harness
tools/get-mermaid.ps1    fetches mermaid 11.4.0 for the Jint spike (not committed)
```

⚠️ Never commit a `.env` here — it holds API keys (see `docs/adr/ADR-002-config-storage.md`).
