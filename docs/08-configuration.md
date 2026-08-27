# 08 — Configuration, Secrets, Paths & Migration

## 8.1 Config keys

Full key list in [02-data-formats.md](02-data-formats.md) §2.3: provider selection, per-provider API key + model, Ollama base URL / model / num_ctx, `RESUME_NAME`, `RESUME_CONTACT`, `INTERVIEW_DATA_DIR`, Langfuse keys (unused if tracing is dropped).

## 8.2 Storage format (ADR-002)

The original reads **and rewrites** a `.env` file beside the app, preserving comments and unrelated lines. **ADR-002 (decided): Option A — env-file compatible, as-is.** Concretely:

- **Keep an env-file-compatible codec** (`Core/Config/EnvFile.cs`): parse `KEY=value` lines, comments preserved; save = update matching `KEY=` lines in place, append new keys, preserve the file's newline style. This keeps a shared `.env` usable by both apps during transition.
- **Location** (implemented in `AppConfig.ResolveEnvPath`) — an **existing** file wins, searched in the original's own precedence:
  1. **the working directory** — `./.env`, which is the repo root under `run.cmd` and mirrors the original's `Path(".env")`;
  2. beside the executable (packaged / portable installs);
  3. the per-user config dir — `%APPDATA%\InterviewFlow\.env` / `~/Library/Application Support/InterviewFlow/.env`.

  When no file exists anywhere it is **created in the working directory**, falling back to the per-user dir only if that isn't writable (an installed app can start in a read-only location). `.env.example` at the repo root documents every key.

- **Import** (`AppConfig.ImportFrom`, "Import .env…" in Configuration): copies a user-chosen file over the active settings file, keeping the replaced one as `<name>.bak`. It refuses a file with no `KEY=value` lines (wrong file picked) and refuses importing the active file onto itself, in both cases changing nothing. After a successful import it reloads the file **and re-syncs the process environment** for every key in `AppConfig.KnownKeys` — necessary because process variables win over the file (§ below), so values written by earlier in-session edits would otherwise shadow the import. An imported `INTERVIEW_DATA_DIR` re-points the stores immediately.
- **Reads** follow python-dotenv precedence: a real process environment variable beats the file. The Configuration screen sets both when saving, so edits take effect without a restart.
- **Secrets**: plaintext API keys in `.env`, matching the original (accepted risk per ADR-002; OS keychain protection is a deferred fast-follow).

⚠️ Never copy `c:\dev\interview-flow\.env` into this repo — it currently holds live API keys (flagged in 00-overview).

## 8.3 Data directory

- `INTERVIEW_DATA_DIR` when set; otherwise an **existing** `data` folder is used, searched like the env file:
  1. `./data` in the working directory (the repo root under `run.cmd` — the original's `<repo>/data` default);
  2. `<exedir>/data` (frozen / portable installs).

  Requiring the folder to already exist keeps a stray working directory from hijacking where data lives. With neither present it falls back to the per-user data dir (`%LOCALAPPDATA%\InterviewFlow\data` / `~/Library/Application Support/InterviewFlow/data`).

  **Gotcha:** the `./data` rule only fires when the working directory *is* the repo root — true under `run.cmd`, but not when the app is launched from an IDE or by running the exe in `bin/…`, which start in the output folder and therefore land on the empty per-user folder ("0 previous applications"). Setting `INTERVIEW_DATA_DIR` explicitly makes the location launch-independent.

- **Choosing another folder** (Configuration → Data Storage → Save) branches on what's already there:
  - target **already contains data files** → the app *adopts* it: the location is saved and the stores re-point, with nothing copied, moved, or deleted. This is the "use my existing workflows" path, and it is what prevents the migration from overwriting that data with the current folder's.
  - target **empty** → the 5-phase move wizard runs (§8.5).
  - current folder empty → nothing to move, so it just re-points.
- Contents: `interview-flow-data.json`, `custom-actions.json`, `resume-template.docx` (case-insensitive), `README.md`.

## 8.4 Configuration screen behaviors

See 03 §3.11 for full UI. Functional notes:

- Saving provider/key/model updates the config file immediately (openlogi-net's apply-on-change pattern fits: `partial void On<Prop>Changed` → save).
- Gemini model list fetched live from the API; Ollama models via `GET /api/tags` with tool-capability probe via `POST /api/show`.
- The ⚙ sidebar button pulses amber while no provider has a key configured.
- Provider label chip on Setup shows `"{Provider} - {model}"`.

## 8.5 Data-folder migration wizard (port exactly)

Changing the Data Storage path launches a 5-phase modal wizard (original `MigrationModal`):

1. **Confirm** — show source → destination, file list with sizes.
2. **Copy** — copy all data files to the new folder.
3. **Verify** — byte-for-byte comparison of every copied file.
4. **Save config** — write `INTERVIEW_DATA_DIR` to config.
5. **Delete originals** — remove source files.

Each phase has distinct error states with retry paths; failure before phase 4 leaves config pointing at the old folder (no data loss). Port as a dialog `Window` with a small state machine in its VM; file ops in Core so they're unit-testable.

## 8.6 First-run / compatibility checklist

- Existing original-app data folder must open unchanged (schema `version: 1`).
- A `.env` written by the original must load; a `.env` written by the port must load in the original (round-trip test on the codec).
- Newer store `version` than known → refuse to load with a clear error (don't silently wipe — openlogi-net convention).
