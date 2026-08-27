# ADR-002 — Configuration & Secrets Storage

**Status:** Accepted — **Option A (env-file compatible, as-is)**, decided by the user 2026-08-26.
**Drives:** [08-configuration.md](../08-configuration.md), Core/Config.

## Context

The original stores all config — including plaintext API keys — in a `.env` file that it reads at startup and **rewrites in place** (preserving comments) when the user saves Configuration. Ground rule 1 (same data formats) argues for compatibility; good practice argues against plaintext keys. Must work on Windows and macOS.

## Options

### A. Env-file compatible, as-is (original behavior)

Port the exact read/rewrite-in-place codec; keys stay plaintext in `.env`.

- **+** Full compatibility — both apps can share one config during transition; zero migration.
- **−** Perpetuates plaintext secrets on disk.

### B. Env-file format + OS-protected secrets

`.env` remains the format for non-secret keys; API keys move to DPAPI (Windows) / Keychain (macOS), with one-time import from an existing `.env` (and optionally blanking the key lines).

- **+** Secrets protected; non-secret compat retained.
- **−** Breaks two-way sharing with the original app (it can't read the keychain); import/export UX needed.

### C. Native settings file (TOML per openlogi-net) + `.env` import

- **+** Cleanest .NET-side story.
- **−** Violates ground rule 1 for config; migration-only compatibility.

## Sub-decisions

1. **Config file location resolution order** (portable vs installed): env-var override → beside executable if present → per-user config dir (`%APPDATA%\InterviewFlow` / `~/Library/Application Support/InterviewFlow`). *Confirm.*
2. **Default data dir** when `INTERVIEW_DATA_DIR` unset: `<exedir>/data` (original frozen behavior) vs per-user data dir. Must not orphan existing users' data.
3. **Langfuse keys**: carried through even if tracing is dropped (don't destroy user config lines).

## Decision

**Option A.** Compatibility is the port's stated purpose; the original's security posture is unchanged and the plaintext-keys risk is documented in 00-overview. Option B (OS-protected secrets) remains a candidate fast-follow once side-by-side use with the original ends. The codec must round-trip: comments, unknown keys, and newline style preserved.

The sub-decisions above (config file location resolution order, default data dir) are still to be confirmed during M1 — they are implementation details within Option A, not format questions.

## Consequences

- Core/Config gets `EnvFile` (parse/update-in-place) + `AppConfig` typed accessor.
- Round-trip tests: original-written file → port save → original loads unchanged semantics.
- ⚠️ Repo hygiene: `.env` in `.gitignore` from day one; never commit the original repo's `.env` (contains live keys).
