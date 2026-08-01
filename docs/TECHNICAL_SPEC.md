# AI Usage Monitor — Complete Technical Specification

**Purpose of this document:** a complete, implementation-independent description of what this application does and how, precise enough to reimplement it in a different programming language / technology stack (e.g. C#/.NET, Electron/TypeScript, Python+Qt, Swift, etc.) without access to the original Rust source.

This document describes the application **as of source snapshot with `Cargo.toml` version `1.7.0`**, including several uncommitted local changes on top of the last published release (v1.7): independent per-service polling, Codex window-classification fix, per-limit reset notifications via ntfy.sh, and a Codex "reset credits available" badge. Where something is "not yet released," it is called out explicitly.

Written in English intentionally, for portability into other tools/environments regardless of conversation language. The authoritative source of exact truth for any point below is always the current source tree; this document is a snapshot description of it.

---

## 1. Overview

**AI Usage Monitor** is a small, native Windows desktop utility: a borderless, always-visible "floating widget" plus a system tray icon, that shows how much of a user's Claude (Anthropic) and/or ChatGPT/Codex (OpenAI) rate-limit usage windows have been consumed, without the user needing to open a terminal, IDE, or provider website.

It works by reading OAuth credentials that other official CLIs (`claude` for Claude Code, `codex` for OpenAI Codex CLI) already store locally on disk, and calling the same/similar usage endpoints those CLIs use. It does not implement its own authentication flow — it is a read-only viewer riding on credentials created by those tools.

### 1.1 What it shows

For each of up to two "services" (Claude, ChatGPT/Codex), independently:
- A short-window usage bar ("5h" for Claude; for Codex this window may or may not exist depending on the account — see §7.5).
- A long-window usage bar ("7d" for both).
- Each bar shows: percentage used (0–100%), a colored fill (green→amber→red gradient by percentage), and a human countdown to reset ("H:MM" for the short window, "D:HH:MM" for the long window).
- A small checkbox next to each bar to arm a one-shot push notification for when that specific limit resets (see §12).
- (Codex only) a small badge showing the count of banked "rate-limit reset credits" available on the account, when > 0 (see §7.6).

### 1.2 Non-functional requirements to preserve in a rewrite

- **Single native executable**, no bundled runtime/interpreter, no embedded browser/webview.
- **Small footprint**: current release binary is ~840 KB (Rust release profile: `opt-level = "z"`, LTO, symbol stripping, `panic = "abort"`, single codegen unit).
- **No bundled fonts**: text uses a font that ships with the OS (`Bahnschrift SemiCondensed` on Windows) — see §9.4. A rewrite on another OS should pick an equivalent OS-bundled condensed sans-serif rather than embedding a font file, to keep the binary/install small — but this is a soft constraint, not a hard one, if the target platform doesn't have anything reasonable pre-installed.
- **No backend / no telemetry / no analytics** — the app talks directly, from the user's machine, to: Anthropic's API, OpenAI's ChatGPT backend, GitHub's REST API (release checks only), and the user-configured ntfy.sh topic (notifications only, opt-in). Nothing else.
- **Does not write to the CLIs' credential files.** It only reads them. When a token looks expired, it asks the *official CLI* to refresh it (by shelling out), never mutates the credential file itself.
- **Single-instance**: a second launch silently exits if one instance is already running.
- **Autostart is optional** and implemented via the OS's native mechanism (Windows: `Run` registry key), not a scheduled task or service.

---

## 2. Technology stack (current implementation, for reference — not a requirement to replicate)

- **Language:** Rust, edition 2021.
- **Windowing/UI:** raw Win32 API via the `windows` crate (0.58) — no GUI framework (no WinForms/WPF/Win32 wrapper/Electron/Qt). All UI is a single owner-drawn window painted with GDI, plus one native Win32 modal dialog (text input) and one owner-drawn tooltip popup.
- **HTTP:** `ureq` (blocking, synchronous) + `native-tls`.
- **JSON:** `serde` / `serde_json`.
- **No async runtime.** Networking happens on ad-hoc spawned OS threads (`std::thread::spawn`), never on the UI thread.
- **Build:** `build.rs` embeds the `.ico` icon, PE version info, and an application manifest (opts into Common Controls v6, does **not** declare DPI awareness in the manifest — DPI awareness is instead set at runtime via `SetProcessDpiAwarenessContext`).

A rewrite is free to choose a completely different stack (e.g. a cross-platform toolkit) — the sections below describe *behavior*, not implementation mechanics, except where Win32-specific quirks materially shaped the design (called out explicitly).

---

## 3. High-level architecture

Single-process, single-window application with:

1. **One global mutable application state** (`AppState`), guarded by a single mutex, holding: current displayed percentages/text per limit, layout/theme/language preferences, window position, polling state, notification-arm flags, and the last successfully polled data blob.
2. **A native message loop** (the classic Win32 `GetMessage`/`DispatchMessage` loop) drives everything: painting, timers, mouse/keyboard input, and custom "app messages" posted from background threads back to the UI thread.
3. **Background worker threads** (spawned ad hoc, not a pool) perform:
   - Network polling of Claude/Codex usage.
   - Self-update download/apply.
   - ntfy.sh notification delivery.
   - Shelling out to `claude`/`codex`/`wsl.exe` CLIs to force a token refresh or probe credential state.
   None of these threads ever touch UI directly; they mutate the shared state under the mutex and then post a message to the UI thread, which repaints.
4. **Four OS timers** drive periodic behavior (IDs are logical, not literal):
   - **Poll timer** — triggers a background poll at the user-configured interval (1 min / 5 min / 15 min / 1 hour; default 15 min).
   - **Countdown timer** — ticks roughly once a display digit is expected to change (see §7.4), to keep the "H:MM" countdown live without a full re-poll.
   - **Reset-poll timer** — a fast 5-second timer that only runs while at least one limit's `resets_at` has already passed but the last poll hasn't picked up the fresh (reset) values yet; stops itself once fresh data arrives.
   - **Update-check timer** — fires once every 24h (persisted across restarts) to check GitHub for a new release.
5. **Persistence**: a single JSON settings file on disk (§13). No database, no registry storage of app data (registry is used only for the OS-native "run at startup" mechanism, §16).

---

## 4. Data model

### 4.1 Core usage types

```
UsageSection {
    percentage: f64          // 0.0..100.0 (not clamped at parse time; clamped only when drawing the bar fill)
    resets_at: Option<Timestamp>   // absolute UTC time this window resets; None if unknown
    available: bool          // true only if the provider actually reported this window at all
}

UsageData {
    session: UsageSection            // "short" window (labelled "5h")
    weekly: UsageSection             // "long" window (labelled "7d")
    reset_credits_available: Option<u32>   // Codex-only; number of banked manual-reset credits; None = not applicable/unknown
}

AppUsageData {
    claude_code: Option<UsageData>   // None if Claude is disabled or the last poll for it failed
    codex: Option<UsageData>         // None if Codex is disabled or the last poll for it failed
}
```

**Critical semantic**: `available` on `UsageSection` distinguishes *"this window does not exist for this account"* from *"this window exists and is at 0% used"*. This flag was added specifically because OpenAI changed their API in June 2026 to sometimes report only one window (see §7.5) — a naive implementation that defaults percentage to 0 cannot tell these two states apart, and would misleadingly show "0% used" for a window the account doesn't even have. **A rewrite must preserve this distinction** and render it differently (see §9.6: unavailable → em/en dash, not "0%").

### 4.2 Limits enumeration

There are exactly **4 addressable "limits"** in the whole app, used for the reset-notification feature:

| Limit | Service | Window |
|---|---|---|
| `ClaudeSession` | Claude | short (5h) |
| `ClaudeWeekly` | Claude | long (7d) |
| `CodexSession` | ChatGPT/Codex | short (5h, if present) |
| `CodexWeekly` | ChatGPT/Codex | long (7d) |

Each has: an index (0–3, used for a fixed-size `[bool; 4]` array of "armed" flags), a way to look up its `UsageSection` inside an `AppUsageData`, and a notification message string (currently hard-coded English, not localized — see §12.4).

---

## 5. Credential discovery & reading

The app never asks the user to log in. It reads credentials that `claude`/`codex` CLIs already wrote to disk.

### 5.1 Claude credentials

**Primary (Windows) source**: `%USERPROFILE%\.claude\.credentials.json`

```json
{
  "claudeAiOauth": {
    "accessToken": "...",
    "refreshToken": "...",
    "expiresAt": 1780000000000,   // unix millis
    "scopes": [...],
    "subscriptionType": "pro",
    "rateLimitTier": "default_claude_ai"
  }
}
```
Only `accessToken` and `expiresAt` are consumed. A missing/unparseable file, missing `claudeAiOauth` key, or missing `accessToken` string ⇒ treated as "no credentials." An empty-string `accessToken` is technically read successfully but will fail auth against the API (this is the real-world "logged out locally" state — see §7.3 for how it's handled).

**Secondary sources (WSL)**: for every installed WSL distro (enumerated via `wsl.exe -l -q`), the same relative path `~/.claude/.credentials.json` inside that distro is probed by running `wsl.exe -d <distro> -- sh -lc "cat ~/.claude/.credentials.json"` with a 5-second timeout, and parsed the same way. Distro output may be UTF-16LE (Windows console codepage weirdness) or UTF-8; both are auto-detected and decoded (heuristic: look for a UTF-16LE BOM, or check whether every other byte is `0x00` in a sample).

**Selection order**: Windows source is tried first; if absent/unreadable, WSL distros are tried in the order `wsl.exe -l -q` lists them, first successful parse wins. This defines a `CredentialSource` (`Windows(path)` or `Wsl{distro}`) that is remembered so subsequent refresh attempts target the same source.

**Token freshness check**: `now_millis >= expiresAt` ⇒ considered expired (no timezone math needed; `expiresAt` is already unix millis UTC). If `expiresAt` is absent, the token is treated as never-expiring for this check (some accounts/tiers may omit it).

### 5.2 Codex (ChatGPT) credentials

**Source**: `$CODEX_HOME/auth.json` if `CODEX_HOME` env var is set, else `%USERPROFILE%\.codex\auth.json`.

```json
{
  "auth_mode": "...",
  "OPENAI_API_KEY": null,
  "tokens": {
    "id_token": "...",
    "access_token": "...",
    "refresh_token": "...",
    "account_id": "..."
  },
  "last_refresh": "..."
}
```
Only `tokens.access_token` and `tokens.account_id` are consumed. Empty-string `access_token` ⇒ treated as "no credentials." No WSL fallback for Codex (Windows-only lookup) — this is an intentional current limitation, not a hard requirement.

### 5.3 Token refresh (forcing the official CLI to refresh)

The app **never** implements OAuth refresh itself. Instead, when it detects an expired/rejected token, it shells out to the official CLI with a no-op command that causes that CLI to refresh its own stored token as a side effect, then re-reads the credential file:

- **Claude on Windows**: resolve the `claude` executable (`claude.cmd` or `claude` on PATH — try running `--version`, then fall back to `where.exe`; default to `claude.cmd` if nothing is found), then run `claude -p .` (a minimal non-interactive prompt) with the environment variables `CLAUDECODE` and `CLAUDE_CODE_ENTRYPOINT` explicitly removed from the child's environment (to avoid the CLI thinking it's being invoked recursively from inside itself), no visible console window, stdio redirected to null, waited on for up to **30 seconds** (then killed if still running).
- **Claude in WSL**: `wsl.exe -d <distro> -- bash -lic "if command -v claude ...; then claude -p .; elif [ -x \"$HOME/.local/bin/claude\" ]; then ...; else exit 127; fi"` — same 30s timeout/kill pattern.
- **Codex on Windows**: resolve `codex.cmd`/`codex.ps1`/`codex.exe`/`codex` similarly, then run `codex exec .` (if resolved to a `.ps1`, invoke via `powershell.exe -NoProfile -ExecutionPolicy Bypass -File <path> exec .`). Same 30s timeout pattern.

**Important UX consequence**: this refresh attempt runs **synchronously on the polling background thread**, blocking that specific poll (up to 30s worst case) before falling through to try the next credential source or give up. This was identified during development as a real latency issue (Codex data can visibly lag behind Claude data on first launch because Claude's failed refresh blocks the whole `poll()` call before Codex is even attempted — see §7.1). A rewrite should seriously consider polling services **in parallel** rather than sequentially to avoid this.

**Refresh retry chain (Claude only)**: if refreshing the current source doesn't produce a valid (non-expired) token, the app advances to the *next* known credential source (Windows → first WSL distro → next WSL distro → …) and retries the whole refresh dance there, until sources are exhausted, at which point it reports `TokenExpired`.

**Codex refresh**: single attempt only (no fallback chain) — on `AuthRequired` (HTTP 401/403 from the usage endpoint), shell out to `codex exec .` once, re-read `auth.json`, retry the usage call once. Any further failure is final for that poll cycle.

---

## 6. Remote API contracts

### 6.1 Anthropic — dedicated usage endpoint (primary)

```
GET https://api.anthropic.com/api/oauth/usage
Authorization: Bearer <access_token>
anthropic-beta: oauth-2025-04-20
```

Expected 200 JSON body:
```json
{
  "five_hour": { "utilization": 34.2, "resets_at": "2026-06-30T20:00:00.123456+00:00" },
  "seven_day": { "utilization": 7.1, "resets_at": "2026-07-06T20:00:00+00:00" }
}
```
Either top-level key may be absent (⇒ that `UsageSection.available` stays `false`). `resets_at` is an ISO-8601 timestamp with either a `+HH:MM` offset or a trailing `Z`, with or without fractional seconds. **A minimal hand-rolled ISO-8601-ish parser is used (no timezone library) that strips the offset/`Z` suffix and computes days-since-epoch manually** (correct for `YYYY-MM-DDTHH:MM:SS[.frac]` assumed to already be UTC, which the API guarantees by construction — always emits `+00:00`/`Z`). A rewrite in a language with a real datetime library should obviously just use that instead; this hand-rolled parser exists purely because the original avoided pulling in a chrono-equivalent dependency to keep the binary small.

HTTP 401/403 ⇒ `AuthRequired` (triggers refresh flow, §5.3). Any other error (including non-JSON body, network failure, or a 5xx) ⇒ treated as "endpoint unavailable, fall back to §6.2" — **not** a hard failure.

### 6.2 Anthropic — Messages API rate-limit-header fallback (secondary)

Used when 6.1 doesn't return usable data (non-401/403 error, or JSON missing both windows, or reset timestamps missing from 6.1's response and being backfilled from here).

```
POST https://api.anthropic.com/v1/messages
Authorization: Bearer <access_token>
anthropic-version: 2023-06-01
anthropic-beta: oauth-2025-04-20
Body: {"model": "<model>", "max_tokens": 1, "messages": [{"role":"user","content":"."}]}
```

This deliberately sends the **cheapest possible real inference request** (1 output token) purely to read the rate-limit headers Anthropic attaches to every Messages API response — the response body content is discarded entirely. Model fallback chain, tried in order until one call succeeds enough to read headers: `claude-3-haiku-20240307`, then `claude-haiku-4-5-20251001` (kept intentionally cheap/small models — this is a real (billed, if the account has metered billing beyond a subscription) API call, so cost matters).

Relevant response headers read:
```
anthropic-ratelimit-unified-5h-utilization      (0.0–1.0 fraction, multiply by 100 for %)
anthropic-ratelimit-unified-5h-reset            (unix seconds)
anthropic-ratelimit-unified-7d-utilization
anthropic-ratelimit-unified-7d-reset
anthropic-ratelimit-unified-status              ("rejected" if the request itself got rate-limited)
anthropic-ratelimit-unified-representative-claim ("five_hour" | "seven_day" — which window caused the rejection)
anthropic-ratelimit-unified-reset               (overall reset, used as a session-reset fallback only)
```
`available` per section is set from whether the *utilization* header was present at all for that window. Special case: if both utilizations parsed as exactly 0.0 **and** `status == "rejected"`, the representative-claim header is used to force that specific window's percentage to 100% (because a rejection with 0%-reported utilization but a rejected status is contradictory — the account is actually saturated, the utilization number just hadn't caught up).

If a call returns HTTP 401/403 ⇒ `AuthRequired`. If a call returns some other error status, its headers are still inspected (a non-2xx response can still carry the rate-limit headers). If **none** of the header names are present in that response, the next model in the fallback chain is tried. If the whole chain is exhausted without any usable headers ⇒ `RequestFailed`.

### 6.3 OpenAI/Codex — usage endpoint

```
GET https://chatgpt.com/backend-api/wham/usage
Authorization: Bearer <access_token>
User-Agent: codex-cli
ChatGPT-Account-Id: <account_id>     (omitted if account_id is empty/absent)
```

Expected 200 JSON body (real captured example, June 2026):
```json
{
  "user_id": "user-...",
  "account_id": "user-...",
  "email": "...",
  "plan_type": "plus",
  "rate_limit": {
    "allowed": true,
    "limit_reached": false,
    "primary_window": {
      "used_percent": 0,
      "limit_window_seconds": 604800,
      "reset_after_seconds": 604800,
      "reset_at": 1786205599
    },
    "secondary_window": null
  },
  "rate_limit_reset_credits": {
    "available_count": 1,
    "applicable_available_count": 0
  }
}
```
Only these fields are consumed: `rate_limit.primary_window` and `rate_limit.secondary_window` (each optionally present/null, each with `used_percent: f64`, `reset_at: i64` unix seconds, and optionally `limit_window_seconds: i64`), and `rate_limit_reset_credits.available_count: u32` (top-level sibling of `rate_limit`, **not** nested inside it).

**⚠ Critical, non-obvious rule (see §7.5 for the bug this fixes):** do **not** assume `primary_window` is always the short/session window and `secondary_window` is always the long/weekly window. As of June 2026, OpenAI began returning accounts with **only** `primary_window` populated and `secondary_window: null`, where that lone `primary_window` can itself be a **weekly** window (`limit_window_seconds: 604800`). Classify each present window by its *actual* `limit_window_seconds` duration:
- `limit_window_seconds <= 86400` (24h) ⇒ this is the **session/short** window.
- `limit_window_seconds > 86400` ⇒ this is the **weekly/long** window.
- If `limit_window_seconds` is absent entirely on a window object ⇒ fall back to positional convention (`primary_window` → session, `secondary_window` → weekly) as a last resort.
- If, after classification, a slot (session or weekly) already has data assigned and another window also classifies into the same slot, the **first** one processed wins (don't overwrite).

401/403 ⇒ `AuthRequired` (triggers the single-attempt refresh in §5.3). Any other transport/parse error ⇒ `RequestFailed`.

### 6.4 GitHub — release check (self-update)

```
GET https://api.github.com/repos/<owner>/<repo>/releases/latest
Accept: application/vnd.github+json
User-Agent: ai-usage-monitor/<current_version>
X-GitHub-Api-Version: 2022-11-28
```
`<owner>/<repo>` is parsed at compile time from the Cargo package's `repository` URL metadata (currently `jpribil/AI-Usage-Monitor`). Response: `{ "tag_name": "v1.7", "assets": [{"name": "ai-usage-monitor.exe", "browser_download_url": "..."}] }`. Version comparison: strip a leading `v` from `tag_name`, parse as `(major, minor, patch)` tuple (non-numeric/missing parts default to 0, ignoring any `-suffix`), and compare as a tuple — newer only if strictly greater than the running `CARGO_PKG_VERSION` parsed the same way. Asset selection: exact case-insensitive match on `ai-usage-monitor.exe`, else first asset whose name ends in `.exe`, else the whole check fails with an explicit error ("no Windows executable asset found").

### 6.5 ntfy.sh — reset notifications

```
POST https://ntfy.sh/<user-configured-topic>
Title: AI Usage Monitor
Content-Type: text/plain; charset=utf-8
Body: <plain-text message, currently hard-coded English>
```
Best-effort, fire-and-forget: any failure is logged (diagnostics only, §17) and otherwise silently ignored — it must never disrupt the rest of the app. An empty/whitespace-only topic short-circuits before making any request.

---

## 7. Polling logic & state machine

### 7.1 Top-level poll

`poll(claude_enabled, codex_enabled) -> Result<AppUsageData, PollError>`:

- Polls Claude (if enabled) and Codex (if enabled) **sequentially, independently**. A failure polling one service **does not** prevent the other from being polled or displayed — this was a real bug fixed during development (previously, an error propagated with `?` from the Claude branch aborted the whole function before Codex was even attempted, so one broken/logged-out service blanked out a perfectly working other service with a generic "!" error on **both** cards).
- If **both** fail (or the one enabled service fails), the whole call returns `Err`; Claude's error is preferred as the surfaced one if both are `Err` (arbitrary but stable tie-break, matching pre-fix behavior for the single-service-failing case).
- If **at least one** succeeds, returns `Ok` with whichever succeeded populated and the other left as `None` (rendered as an error indicator only for the failed one — see §9.6).

`PollError` variants: `AuthRequired` (server rejected the token, needs re-login/refresh), `NoCredentials` (no local credential file/tokens found at all), `TokenExpired` (local token expired and the refresh chain — §5.3 — didn't recover a valid one), `RequestFailed` (generic transient network/parse failure).

### 7.2 Poll scheduling & backoff

- Base interval: user-selectable, one of **1 min / 5 min / 15 min (default) / 1 hour**, persisted.
- On **success**: the normal timer/interval is (re)armed; if the app had been in a retry/backoff state, that state is cleared and the timer is reset back to the normal interval.
- On a **transient failure** (`RequestFailed`, or `NoCredentials` when not already being "auth-watched" — see §7.3): exponential backoff starting at **30 seconds**, doubling each consecutive failure (`30s, 60s, 120s, 240s, …`), **capped at the user's configured normal interval** (never waits longer than the user asked for). Displayed values become `"..."` (a literal three-dot placeholder, not localized) for all 4 lines while in this state.
- On an **auth failure** (`AuthRequired`, `TokenExpired`, or `NoCredentials` — all three enter the same "auth-watch" mode, see §7.3): the poll timer is reset to the **normal user interval** (not backed off further — retrying rapidly against a known-bad auth state is pointless), but polling is additionally **paused** in favor of a cheaper "did credentials change" watch (§7.3). Displayed values become `"!"` (literal exclamation mark, not localized) for the affected service's two lines.

### 7.3 Auth-error "credential watch" mode — avoiding repeated failed polls

When entering an auth-error state, the app does **not** just blindly keep polling the API on a timer (which would keep failing until the user manually re-authenticates). Instead:

1. It takes a **lightweight fingerprint ("watch snapshot")** of the relevant local credential file(s) — not their contents, just: existence + file size + modification time (`"win:<path>|present|<size>|<mtime>"` or `"...|missing"`), one string per known source. For a `TokenExpired`/`AuthRequired` failure it watches only the *currently active* credential source; for a `NoCredentials` failure (nothing found anywhere) it watches **all known sources** (Windows path + every WSL distro's path), since the user might log in via any of them.
2. On each subsequent poll-timer tick, instead of immediately re-polling the network, it cheaply recomputes the same fingerprint and compares it to the stored snapshot.
3. Only if the fingerprint **changed** (implying the user re-ran `claude`/`codex login` in the meantime, rewriting the credential file) does it actually attempt a real network poll again.
4. A manual "Refresh" menu action (§11) sets a `force_notify_auth_error` flag so that the *next* failure (even if the watch snapshot hasn't changed) will re-show the balloon notification rather than silently no-op — this lets the user "acknowledge and retry" without waiting for a real file change.

This avoids hammering the API (or spawning CLI refresh subprocesses) every 15 minutes indefinitely while the user is simply not logged in, while still reacting promptly once they do log in — checking a file's mtime is essentially free compared to a network round-trip + possible 30s CLI subprocess spawn.

### 7.4 Countdown timer — cheap UI ticking without re-polling

Between actual polls, the displayed "H:MM" / "D:HH:MM" countdown text needs to visibly tick down. Rather than repainting every second, a **variable-delay timer** is computed: for each of the (up to) 4 `resets_at` timestamps currently known, compute how many seconds remain until the *displayed minute digit* would next change (i.e. `((remaining_seconds - 1) % 60) + 1`, clamped so a already-past deadline still fires in 1 second) — then arm a one-shot timer for the **minimum** of those 4 values (default 60s if none are known). When it fires: re-render the text from already-known data (no network call), then immediately recompute and re-arm the next such timer. This is a self-rescheduling single timer, not an interval.

### 7.5 Fast "just reset" polling

If **any** known `resets_at` timestamp is now in the past (server-side window presumably just rolled over) but the locally cached percentage hasn't been refreshed yet, a separate **5-second interval timer** is armed to aggressively re-poll until fresh (post-reset) data arrives, at which point that timer is cancelled. This exists because there's inherent latency between "the reset time we were told about passes" and "the provider's backend has actually rolled the counter and will return fresh numbers" — blindly trusting the old `resets_at` the instant it passes would show stale (100%-ish) data for a few seconds otherwise.

### 7.6 Codex reset-credits badge (informational only, no action)

`reset_credits_available` (from §6.3) is stored and, if `> 0`, rendered as a small non-interactive badge on the Codex card (§9.6). **There is deliberately no UI to spend/redeem a credit** — this was an explicit product decision (spending a banked credit is a real, semi-irreversible action against the user's OpenAI account quota, and the user only wanted visibility, not a trigger). If a future version *does* want to add redemption, the relevant (reverse-engineered, unofficial) endpoint is:
```
POST https://chatgpt.com/backend-api/wham/rate-limit-reset-credits/consume
Authorization: Bearer <access_token>
ChatGPT-Account-Id: <account_id>
Body: {"credit_id": "...", "redeem_request_id": "..."}
```
(discovered via a third-party OSS tool, not officially documented — treat with caution, and definitely gate behind an explicit confirmation if ever implemented).

---

## 8. Window / UI behavior

- **Window kind**: borderless popup (`WS_POPUP`), layered (per-pixel alpha capable but currently always fully opaque — `SetLayeredWindowAttributes(..., 255, LWA_ALPHA)`), tool-window style (`WS_EX_TOOLWINDOW` — excluded from the taskbar and Alt-Tab). It is **not** embedded in the taskbar; the tray icon is the only persistent OS-chrome presence besides the floating window itself.
- **Custom title bar**: drawn by the app itself (no OS-native title bar/border), containing: a small live-drawn gauge icon (§9.5), the app name + version, and a custom "×" close button.
- **Close button behavior**: clicking it does **not** quit the process — it **hides the widget** (sets `widget_visible = false`, persisted) while the tray icon and background polling keep running. The only way to actually exit the process is the tray/context-menu "Exit" item.
- **Dragging**: click-and-drag anywhere in the window body (that isn't the close button or a checkbox) moves the window; position is clamped to the virtual screen bounds (`GetSystemMetrics(SM_XVIRTUALSCREEN/…)`, i.e. spans all monitors) so it can never be dragged fully off-screen, and is persisted on mouse-up.
- **Default position** (first run, or "Reset Position" menu action): bottom-right-ish of the virtual screen, offset inward by a small margin (16px scaled) horizontally and a larger margin (64px scaled) vertically from the bottom.
- **Show/hide**: toggled from the tray icon (double-click) or the context-menu "Show Widget" checkbox item; persisted.
- **Always on top**: toggled from the context menu; implemented via `SetWindowPos` with `HWND_TOPMOST`/`HWND_NOTOPMOST`; persisted.
- **DPI awareness**: Per-Monitor-V2 (`SetProcessDpiAwarenessContext(DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2)`), set once at process start. A live DPI value is re-queried per-window (`GetDpiForWindow`, not the process-wide/cached `GetDpiForSystem`) whenever it might have changed (`WM_DPICHANGED`, display change, before repositioning/painting), so the widget stays crisp when dragged between monitors with different scale factors.
- **Global UI scale**: on top of OS DPI scaling, there is a single hard-coded multiplier applied to every single pixel constant in the layout (`UI_SCALE = 1.2`, i.e. the whole widget renders ~20% larger than its "designed" 96-DPI pixel values). This exists purely as a one-line design knob discovered to be necessary during development (the initial 1.0 scale felt too small on screen) — a rewrite can pick any base size it likes; the important thing to preserve is that *one single scalar* feeds every dimension (bar height, card size, font sizes, paddings, checkbox size, everything), so resizing the whole UI is a one-constant change.
- **Layout modes**: "side-by-side" (both service cards horizontally adjacent, single row) vs. "stacked" (cards vertically stacked). When only one service is enabled, layout choice is irrelevant (always renders as a single wide card) — the window auto-sizes to fit exactly 1 or 2 cards in the chosen arrangement; there is no scrolling, no resizable window (no OS resize border/grip at all).
- **Language auto-detection**: unless the user pinned a language, on `WM_SETTINGCHANGE` (fired when OS locale/settings change) the app re-detects the system UI language and live-switches without a restart. Detection order: Windows' "preferred UI languages" list (first entry that maps to a supported language wins) → `GetUserDefaultUILanguage`'s locale name → `GetUserDefaultLocaleName` → hard fallback to English.
- **Theme auto-detection**: unless the user pinned Light/Dark, the OS dark-mode registry value (`HKCU\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize\SystemUsesLightTheme`, DWORD; `1` = light, anything else/missing/error = **dark**, i.e. dark is the safe default on any read failure) is polled on `WM_SETTINGCHANGE` and on startup; live-switches without a restart.

---

## 9. Visual design specification

### 9.1 Color palette

Two complete palettes, selected by effective theme (`is_dark: bool`). All colors are plain RGB hex, no alpha/transparency in the palette itself (the window as a whole is fully opaque).

**Dark** (default):
| Role | Hex |
|---|---|
| Window background | `#0B0D10` |
| Title bar background | `#13161B` |
| Border (title bar bottom rule) | `#23272F` |
| Card background | `#15181E` |
| Card border | `#262B34` |
| Primary text | `#F4F6FA` |
| Muted/secondary text | `#8B94A1` |
| Progress-bar track (empty portion) | `#242A33` |
| Close-button background | `#1E232B` |
| Close-button glyph | `#C8CED7` |

**Light**:
| Role | Hex |
|---|---|
| Window background | `#F2F4F8` |
| Title bar background | `#FFFFFF` |
| Border | `#E1E5EC` |
| Card background | `#FFFFFF` |
| Card border | `#E4E8EF` |
| Primary text | `#11151B` |
| Muted/secondary text | `#5B6573` |
| Progress-bar track | `#E7EBF1` |
| Close-button background | `#EBEEF3` |
| Close-button glyph | `#454D58` |

**Usage-percentage color ramp** (same in both themes — used for progress-bar fill, the titlebar gauge icon's three arcs, and the "armed" checkbox fill color at 0%):
- `0%` → `#22C55E` (green)
- `50%` → `#F59E0B` (amber)
- `100%` → `#EF4444` (red)
- Linear RGB interpolation (lerp) between green→amber for 0–50%, and amber→red for 50–100%. (Simple per-channel linear blend, not perceptual/HSL interpolation.)

### 9.2 Layout geometry (base/96-DPI pixel values, before the `UI_SCALE` and OS-DPI multipliers described in §8)

| Constant | Value (px) | Meaning |
|---|---|---|
| Title bar height | 34 | |
| Content padding | 14 | outer margin around cards |
| Service card width (2-up / side-by-side) | 214 | |
| Service card width (1-up / single service) | 274 | |
| Service card height | 126 | fixed, regardless of content |
| Gap between cards | 12 | |
| Progress bar height | 10 | |
| Progress bar corner radius | 5 | |
| Row label column width | 28 | space for "5h"/"7d" label before the value |
| Close button size | 22×22 | square |
| Notification checkbox size | 14×14 | square |
| Checkbox column reserved width | 14 + 8 = 22 | checkbox size + gap, subtracted from the bar's available width |

**Card internal layout** (all offsets relative to the card's own top-left):
- Title text: left 12, top 8, right = (card width − 12), height 22 (ends at top 30). Font: 15pt-equivalent, semibold. Ellipsized if too long.
- If the Codex reset-credit badge is shown (§9.6), it occupies the top-right corner of this same title row (top 9, height 16, right-aligned to card width − 12, auto-width around its text + 6px horizontal padding each side), and the title's available right-edge shrinks to make room (title text ellipsizes further if needed — badge always wins the space).
- Session ("5h") row: starts at top 43.
- Weekly ("7d") row: starts at top 82.
- Each row: label (muted color, left-aligned) + value text (primary color, right-aligned, ellipsized) on one 18px-tall line, then a 10px-tall progress bar 3px below that (i.e. bar top = row top + 21).
- The checkbox for each row is vertically centered on that row's progress bar, right-aligned to the row's content width (i.e. the bar itself is drawn 22px narrower than the full row width to leave room for it, not overlapping).

**Window sizing** (derived, not fixed): `width = 2×content_padding + (N cards' widths + (N−1) gaps if side-by-side, else just one card's width)`; `height = title_bar_height + 2×content_padding + (card_height × rows + gap × (rows−1))` where `rows` = 1 if side-by-side with >1 active service, else = number of active services (stacked). All of this recomputes and the window physically resizes whenever the set of active services or the layout mode changes.

### 9.3 Rounded rectangles

All card/bar/button/badge shapes use uniform-radius rounded rectangles, implemented as: create a round-rect GDI region, fill it with a solid brush. The progress-bar *fill* portion additionally clips to a round-rect region so the colored fill's own right edge (which may land anywhere along the bar, not just at the far end) still respects the track's rounded corners rather than producing a hard square edge poking out.

### 9.4 Typography

- **Font family**: `Bahnschrift SemiCondensed` — a condensed, technical-looking sans-serif (DIN-style) that ships built into Windows 10 (1709+) and Windows 11. Chosen specifically because: (a) it needs no bundling/download, (b) it's genuinely narrow, giving the small widget a "denser, more digital" look than a default UI font. **Verified via direct GDI font-resolution testing** that this exact family name resolves correctly (as opposed to silently falling back to Arial, which happened with an earlier, incorrect font-family name choice during development — always verify the resolved font name if porting to a new environment, don't trust a name string alone).
- `DEFAULT_CHARSET` is used (not an explicit Latin/ANSI charset) specifically so GDI's automatic font-linking kicks in for the CJK/Cyrillic strings (Japanese, Korean, Traditional Chinese, Ukrainian localizations) — Bahnschrift itself only covers Latin glyphs, so those languages render via the OS's automatic fallback font for the missing glyphs, while Latin-script languages still render in Bahnschrift.
- Font sizes used (in the `sc()`-scaled pixel scale, i.e. before scaling these are negative "point-ish" GDI height values): title bar text −14, card title −15 (semibold), card body/value text −12 (medium weight), close-button glyph −15 (medium), checkbox-badge text −10 (medium).
- No italic, no underline anywhere in the UI.

### 9.5 Titlebar brand mark & application icon — the "gauge" design

Both the in-titlebar mark (live GDI-drawn every paint) and the actual `.ico` application icon (embedded at build time, used for taskbar/Alt-Tab/tray) are **the same design**, drawn from the same geometric formula so they visually match:

A **speedometer/gauge glyph**: three colored arcs forming a ¾ circle (135° start, 90° sweep each, i.e. covering 135°→225°→315°→405°/45°) in green→amber→red (same 3 colors as §9.1's ramp, at fixed thirds rather than the continuous gradient), plus a needle (a straight line from center to a point along the 252° radial direction, i.e. pointing into roughly the "amber/low-red" zone — a fixed decorative angle, not tied to real usage data) with a small solid pivot dot at the center, both in the red accent color.

Geometry (relative to a bounding square of side `size`): center `cx,cy` where `cy` is offset down from vertical-center by `size × 0.60` (i.e. the arc sits slightly below the icon's visual center to leave headroom for the needle), radius `r = size × 0.34`, stroke thickness `size × 0.135` (minimum 2px), needle length `r × 0.82`, needle stroke thickness `stroke × 0.8`, pivot dot radius `stroke × 0.62`.

- **Titlebar rendering**: drawn live via GDI (`ExtCreatePen` with round caps/joins, `Polyline` sampling each arc into ~28 line segments, a straight `LineTo` for the needle, a filled `Ellipse` for the pivot). Explicitly **not** a bitmap blit of the `.ico` — this was a deliberate fix: an earlier version blitted the actual app icon (which had a dark rounded background plate baked into the image) into the titlebar, which looked broken/boxy on the light theme. Live-drawing with no background plate lets it sit cleanly on either theme's titlebar color.
- **Application `.ico`**: generated by a standalone tool (`tools/gen_icon.ps1`, PowerShell + GDI+/System.Drawing) implementing the *exact same geometric formula* at multiple fixed pixel sizes (16, 20, 24, 32, 40, 48, 64, 128, 256), assembled into a single multi-resolution `.ico` container. **Small sizes (≤64px) are encoded as uncompressed 32bpp BMP/DIB frames, not PNG** — this is a load-bearing detail: Win32's `ExtractIconExW` (used to pull the icon back out of the running .exe for both the window titlebar-icon fallback path and the tray icon, §10) was found to unreliably decode PNG-compressed small icon frames, occasionally yielding a NULL handle. DIB frames are always reliable. Sizes >64px (128, 256) use PNG (much smaller file size, and those large sizes are never round-tripped through `ExtractIconExW` in practice — they're for Explorer/shell display, not extracted back out by this app).

### 9.6 Notification checkboxes & the Codex reset-credits badge

- **Checkbox** (one per limit row, 4 total when both services shown): a small rounded-square. **Unarmed**: hollow appearance — outer rounded square filled with the theme's "muted" color, then a smaller inset rounded square (inset = `size/7`, min 1px) filled with the card's own background color on top, producing a thin colored-ring/outline look. **Armed**: fully filled with the usage-ramp's 0%-color (green, `#22C55E` — chosen deliberately to echo "the start of the progress bar," per explicit product direction), with a white 3-point check-mark polyline drawn on top (`(0.26,0.52) → (0.44,0.70) → (0.74,0.30)` as fractions of the box).
- **Click behavior**: if notifications are already configured (a non-empty ntfy topic is set) ⇒ clicking a checkbox directly toggles that limit's armed flag and persists it immediately, no confirmation. If **no** topic is configured yet ⇒ clicking **any** checkbox instead opens the channel-configuration dialog (§12.2); the clicked checkbox is armed **only if** the user actually enters and confirms a non-empty topic in that dialog (Cancel, or an empty string, leaves everything unarmed and un-configured). **The checkbox is never visually disabled/greyed** regardless of whether a channel is configured — this was an explicit product decision (originally implemented as disabled-with-tooltip, then explicitly changed to "always clickable, prompts inline instead").
- **Hover tooltip**: a small custom-drawn popup (see §9.7) always appears on hovering any checkbox (regardless of armed state), showing one of exactly two possible strings depending on whether a channel is configured: the "no channel yet, click to set one up" hint, or the "will notify on reset" confirmation hint. (There is no per-checkbox distinct tooltip text — same two strings apply to whichever checkbox is hovered.)
- **Codex reset-credits badge**: a small rounded-rect pill using the theme's "track" background color and "muted" text color (i.e. visually low-emphasis, not an alert), positioned top-right of the Codex card's title row, showing the localized compact label with the live count substituted in (e.g. English `"Resets: 1"`). **Only rendered when the count is > 0** — otherwise entirely absent (no empty/zero state shown). Purely informational: not clickable, no tooltip, no action.

### 9.7 Custom tooltip popup (technical note — why it's hand-rolled)

The standard Win32 Common Controls tooltip (`tooltips_class32`, the `TTM_*` message family) was tried first and found **unreliable in this specific window setup** (a topmost, layered, owner-drawn popup window) — the tooltip window would be created successfully but `TTM_TRACKACTIVATE` would not reliably make it visible, even after adding a Common-Controls-v6 application manifest (§2). Rather than keep fighting the platform control, it was replaced with a **fully custom owner-drawn popup window**: a separate tiny always-on-top, non-activating (`WS_EX_NOACTIVATE`, so it never steals focus/appears in Alt-Tab) child popup window that paints its own background (using the OS's system "info tip" colors, `COLOR_INFOBK`/`COLOR_INFOTEXT`, so it still matches the OS tooltip look-and-feel) and a single line of text, resized to exactly fit the measured text each time it's shown, and clamped to stay fully within the current monitor's work area (so it never gets clipped off-screen near a screen edge — relevant here because the widget itself commonly sits near a screen corner). Shown/hidden reactively on `WM_MOUSEMOVE` (arming a `TrackMouseEvent`/`WM_MOUSELEAVE` pair each time, since Win32 doesn't otherwise notify you when the mouse leaves a window) — **a rewrite on a platform with a working native tooltip primitive should just use that instead**; this custom implementation is a Win32-specific workaround, not a deliberate design choice worth preserving for its own sake.

---

## 10. Tray icon behavior

- **Single, generic tray icon** (not per-service, not showing live percentage in the icon itself — just the same static gauge glyph as the app icon, §9.5).
- **Tooltip**: the localized app title (e.g. "AI Usage Monitor").
- **Double-click**: toggles the floating widget's visibility (same as the in-window close button, but reversible from the tray).
- **Right-click**: opens the same context menu as right-clicking the floating widget itself (§11) — the tray icon and the widget window share one identical menu.
- **Balloon/toast notification**: shown on the *first* auth failure after a success (not repeated on every subsequent failed poll — see §7.2/7.3's "watch mode" — to avoid spamming), or immediately again after a manual "Refresh" if still failing. Content is localized, service-specific (different title/body depending on whether the failing service is Claude or Codex — if both are somehow simultaneously in this state, Claude's message wins).
- **Icon caching**: the icon bitmap is extracted from the running executable's own embedded resources **once** (lazily, on first need) and the handle is cached for the process's whole lifetime — *not* re-extracted on every tray-icon refresh. This was a deliberate fix for a real bug: extracting fresh on every update meant a single transient extraction failure produced a NULL icon handle that got pushed into the tray as a **blank icon** (visually broken, looked like the icon had "disappeared"). Caching means extraction is attempted again only if it previously failed (nothing valid was ever cached), and a NULL handle is never advertised to the shell — the icon-presence flag on the tray-icon update call is only set when a genuinely valid handle is held.
- **Surviving Explorer restarts**: Windows Explorer (`explorer.exe`) can restart (crash/recover, or the user restarts it manually) without the whole OS rebooting; when it does, **all tray icons silently vanish** and must be re-added by their owning apps. The shell announces this by broadcasting a registered window message named literally `"TaskbarCreated"` to every top-level window. This app registers that message name once (`RegisterWindowMessageW`) and, on receiving it, immediately re-adds its tray icon. This was a second deliberate fix for a real bug (previously the icon would just permanently vanish until the app was manually restarted).
- **Add-vs-modify semantics**: on every tray-icon "sync" call (which happens after every poll, language change, etc. — see §11's trigger points), the app first attempts an *add* (`NIM_ADD`); if that fails (because the icon already exists — the normal/common case), it falls back to *modify* (`NIM_MODIFY`) to refresh the tooltip text. This single code path correctly handles both "icon doesn't exist yet" (first launch, or just re-added after an Explorer restart) and "icon already exists, just update it" without needing to track that state separately.

---

## 11. Context menu structure

One single flat-ish menu (with a handful of submenus), identical whether opened from the tray icon or from right-clicking the floating widget. **Item order and grouping is a deliberate, explicitly-requested design** (previously had different groupings under "Models"/"Settings" submenus; was explicitly restructured per user feedback into this flatter, more logically-grouped form) — preserve this exact order in a rewrite unless asked to change it again:

```
[✓/ ] Claude Code                    ← toggles whether the Claude card is shown
[✓/ ] ChatGPT                        ← toggles whether the Codex card is shown
──────────────
Refresh                              ← forces an immediate poll (id=1)
Update Frequency            ▸        ← submenu: 1 Minute / 5 Minutes / 15 Minutes (default,✓) / 1 Hour
──────────────
Appearance                  ▸        ← submenu: System Default(✓ if unpinned) / Light / Dark
Layout                      ▸        ← submenu: Side by Side / Stacked (one is always ✓)
Language                    ▸        ← submenu: System Default(✓ if unpinned), then all 13 languages
[✓/ ] Show Widget                    ← toggles floating-window visibility (id = tray module's IDM_TOGGLE_WIDGET)
[✓/ ] Always on Top
Reset Position                       ← re-centers window to the default corner position
──────────────
[✓/ ] Start with Windows             ← autostart toggle, see §16
Notification channel…                ← opens the ntfy.sh topic input dialog, see §12.2
v<version> - Check for Updates       ← label changes with update state; see below
──────────────
Exit                                 ← id=2, the only real quit path
```

**Service toggle constraint**: at least one of Claude/Codex must remain enabled at all times — clicking to disable the currently-only-enabled one is a no-op (the click is silently ignored rather than allowing zero services). Toggling either triggers: text reset to a loading placeholder (`"..."`) for all 4 lines, a window resize/reposition (card count changed), a tray-icon resync, and an immediate background re-poll.

**"v<version> - Check for Updates" item** — this single item's label and click-behavior change based on live update-check state:
- Idle → `"v1.7 - Check for Updates"`; click starts an interactive check.
- Checking → `"v1.7 - Checking for Updates..."`, greyed out (non-clickable while checking).
- Applying → `"v1.7 - Applying update..."`, greyed out.
- Up to date (after a completed check) → `"v1.7 - Up to date"`; click re-checks.
- Update available → `"v1.7 - Update to vX.Y"`; click immediately begins downloading & applying that already-known release (skips re-checking).

---

## 12. Reset-notification feature (checkboxes + ntfy.sh)

### 12.1 What "armed" means & the detection algorithm

Each of the 4 limits (§4.2) has an independent boolean "armed" flag, persisted. When armed **and** a channel is configured (§12.2), the app watches for that specific limit's window to reset:

On every successful poll, **before** overwriting the previously-cached `AppUsageData` with the new one: for each armed limit, compare its **old** `resets_at` (from the previous poll's cached data) to its **new** `resets_at` (from the poll that just completed). If both are known and the new timestamp is **at least 60 seconds later** than the old one, that's interpreted as "this window just rolled over to a new reset cycle" (i.e. the server started a fresh countdown) ⇒ fire a notification for that limit and **immediately disarm it** (one-shot; the user must re-check the box to be notified again next cycle). The 60-second threshold exists to avoid false triggers from minor clock/precision jitter between polls where the reset timestamp might shift by a few seconds without a real reset having occurred.

This check only runs at all if `notifications_enabled()` (non-empty topic) — if the user has armed checkboxes but hasn't configured a channel yet, the armed flags are preserved (not silently cleared) but no detection/notification logic runs until a channel is set.

### 12.2 Channel configuration

- A single free-text **topic name** (ntfy.sh's term for a channel — just becomes the path segment in `https://ntfy.sh/<topic>`), no other configuration (no self-hosted-server support, no auth/token for the topic, no per-limit distinct topics).
- Configured via: the "Notification channel…" menu item (§11), **or** implicitly by clicking any unarmed checkbox while no channel is set (§9.6).
- UI: a small native modal dialog (see §12.3) with one text field (pre-filled with the current topic if any), OK/Cancel. Entered text is `.trim()`-ed before being stored; an empty/whitespace-only result after trimming is equivalent to "no channel configured" (checkboxes revert to prompting again on next click).
- **The topic is never hard-coded or committed to source control** — it lives only in the local per-user settings file (§13). This was an explicit privacy/security requirement during development (an earlier debugging session had accidentally hard-coded a real personal topic name into the source; it was subsequently made fully user-configurable and the hard-coded value removed).

### 12.3 The input dialog (technical note)

Implemented as a genuine native Win32 modal dialog (not a custom-drawn popup like the tooltip) — a small owner-window-relative popup with a caption bar, containing a `STATIC` prompt label, an `EDIT` text field (auto-horizontal-scroll style, pre-selected-all text on open for easy overwrite), and `BUTTON`-class OK (default button) / Cancel controls, laid out at fixed DPI-scaled coordinates. Runs its own nested message loop (`IsDialogMessageW` for Tab/Enter/Esc keyboard navigation) until OK/Cancel/close, disabling the owner window for the duration (classic modal pattern) and restoring focus to it afterward. Returns `Some(text)` on OK, `None` on Cancel or window close.

### 12.4 Notification message content (currently English-only, not localized)

| Limit | Message |
|---|---|
| Claude session | `"Claude 5h limit reset"` |
| Claude weekly | `"Claude 7d limit reset"` |
| Codex session | `"ChatGPT 5h limit reset"` |
| Codex weekly | `"ChatGPT 7d limit reset"` |

Sent with the fixed title `"AI Usage Monitor"` (§6.5). **A rewrite that wants localized notification bodies would need to add this** — it's a known, accepted gap in the current implementation, not an oversight to silently "fix" without being asked, but worth flagging.

---

## 13. Settings persistence

**Location**: `%APPDATA%\AIUsageMonitor\settings.json` (created, including parent directories, on first write if absent).

**Format**: pretty-printed JSON, written wholesale (full overwrite, not a merge/patch) every time *any* persisted field changes — there is no partial-write/transaction concept, it's simple and safe enough given the tiny file size and infrequent writes (user-menu-interaction frequency, not per-poll).

**Full current schema**, with defaults applied when a key is absent (supporting seamless upgrade from older settings files that predate newer fields):

```json
{
  "window_x": 4733,                        // absent on very first run → computed default position used instead
  "window_y": -1065,
  "poll_interval_ms": 900000,              // default: 900000 (15 min)
  "language": "cs",                        // absent = follow system language; else a language code (see §14)
  "last_update_check_unix": 1785517370,    // absent = check due immediately on next launch
  "widget_visible": true,                  // default: true
  "show_claude_code": true,                // default: true
  "show_codex": false,                     // default: false — NOTE: if both this and show_claude_code
                                            //   are false on load, show_claude_code is force-corrected to true
                                            //   (never allow zero services enabled, even from a hand-edited file)
  "layout_horizontal": true,               // default: true (side-by-side)
  "always_on_top": false,                  // default: false
  "theme": "dark",                         // absent = follow system theme; else "light" or "dark"
  "notify_claude_session": false,          // default: false (all 4 notify_* flags)
  "notify_claude_weekly": false,
  "notify_codex_session": false,
  "notify_codex_weekly": false,
  "ntfy_topic": ""                         // default: "" (empty = notifications not configured)
}
```

Fields not present in the file at all when it's absent-or-corrupt entirely (unparseable JSON) ⇒ the whole file is treated as "use all defaults" (never a hard crash/error to the user).

---

## 14. Localization

**13 supported languages**, each a complete, structurally-identical translation of the same fixed set of string keys (currently ~44 keys — see the canonical English list below). Language selection: `None` (follow system) or an explicit pinned language code.

| Code | Language | Native name shown in the language submenu |
|---|---|---|
| `en` | English | English |
| `cs` | Czech | Čeština |
| `nl` | Dutch | Nederlands |
| `es` | Spanish | Español |
| `fr` | French | Français |
| `de` | German | Deutsch |
| `it` | Italian | Italiano |
| `pl` | Polish | Polski |
| `pt` | Portuguese | Português |
| `uk` | Ukrainian | Українська |
| `ja` | Japanese | 日本語 |
| `ko` | Korean | 한국어 |
| `zh-TW` | Traditional Chinese | 繁體中文 |

**Explicitly, deliberately excluded: Russian.** This was a direct, explicit user instruction during development (not an oversight) — do not add a Russian translation to this app without a fresh explicit request.

**System-language auto-detection** (used whenever no language is pinned): try, in order, until one maps to a supported language —
1. Each entry in the OS's ordered "preferred UI languages" list (`GetUserPreferredUILanguages(MUI_LANGUAGE_NAME, ...)`), first match wins.
2. The OS UI language's locale name (`GetUserDefaultUILanguage` → `LCIDToLocaleName`).
3. The OS default locale name (`GetUserDefaultLocaleName`).
4. Hard fallback: English.

**Code-matching rule** (`from_code`): normalize (trim, `_`→`-`, lowercase), then match on the **primary subtag only** (text before the first `-`) against the 2-letter codes above — e.g. `en-US`, `en-GB`, `EN_us` all resolve to English. Special case for Chinese: only maps to Traditional Chinese, and only if the full normalized string contains `tw`, `hk`, or `hant` (i.e. `zh-CN`/`zh-Hans`/bare `zh` do **not** match — there is currently no Simplified Chinese translation, so unmatched Chinese variants fall through to the next detection step / ultimately English).

### 14.1 Canonical string catalog (keys + English reference text)

For **exact translated text** in the other 12 languages, the source-of-truth is the corresponding `src/localization/<lang>.rs` file in the original project — this table gives every *key* (semantic identifier) and its English value as the canonical reference for what each key means, which a rewrite must reproduce with equivalent meaning in every supported language.

| Key | English value | Notes |
|---|---|---|
| `window_title` | `AI Usage Monitor` | Base app name; version number is appended separately at render time (`app_title()` = `"{window_title} {version}"`), version itself is never translated. |
| `refresh` | `Refresh` | |
| `update_frequency` | `Update Frequency` | submenu label |
| `one_minute` | `1 Minute` | |
| `five_minutes` | `5 Minutes` | |
| `fifteen_minutes` | `15 Minutes` | |
| `one_hour` | `1 Hour` | |
| `models` | `Models` | **Unused in current UI** (kept only because the string translations already existed after a menu restructure removed the "Models" submenu it used to label) |
| `claude_code_model` | `Claude Code` | top-level service toggle label |
| `codex_model` | `ChatGPT` | top-level service toggle label |
| `layout` | `Layout` | submenu label |
| `layout_side_by_side` | `Side by Side` | |
| `layout_stacked` | `Stacked` | |
| `settings` | `Settings` | **Unused in current UI** (same reason as `models`) |
| `start_with_windows` | `Start with Windows` | |
| `reset_position` | `Reset Position` | |
| `always_on_top` | `Always on Top` | |
| `language` | `Language` | submenu label |
| `system_default` | `System Default` | used both in the Language and the Appearance submenus |
| `appearance` | `Appearance` | submenu label |
| `theme_light` | `Light` | |
| `theme_dark` | `Dark` | |
| `notify_channel` | `Notification channel…` | menu item that opens the dialog |
| `notify_channel_prompt` | `Enter your ntfy.sh channel (topic) name:` | dialog body label |
| `notify_channel_hint` | `Set an ntfy.sh channel name first to enable notifications` | tooltip shown hovering a checkbox when no channel is set |
| `notify_on_reset` | `Send an ntfy.sh notification after reset` | tooltip shown hovering a checkbox when a channel *is* set |
| `codex_resets_available` | `Resets: {count}` | badge text; `{count}` is a literal placeholder replaced at render time with the integer |
| `check_for_updates` | `Check for Updates` | |
| `checking_for_updates` | `Checking for Updates...` | |
| `updates` | `Updates` | dialog-box title used for update-related message boxes |
| `update_in_progress` | `An update check is already in progress.` | |
| `up_to_date` | `You already have the latest version.` | |
| `up_to_date_short` | `Up to date` | compact form for the menu item label |
| `update_failed` | `Unable to update automatically` | |
| `applying_update` | `Applying update...` | |
| `update_to` | `Update to` | used as `"Update to v{version}"` |
| `update_available` | `Update available` | message-box title |
| `update_prompt_now` | `Version {version} is available. Do you want to update now?` | `{version}` placeholder |
| `exit` | `Exit` | |
| `show_widget` | `Show Widget` | |
| `session_window` | `5h` | the short-window row label |
| `weekly_window` | `7d` | the long-window row label |
| `now` | `now` | shown instead of a countdown when `resets_at` has already passed but fresh data hasn't arrived yet |
| `token_expired_title` | `Claude Code Auth Error` | tray balloon title |
| `token_expired_body` | `Run 'claude' in a terminal, then use '/login' and follow the prompts. After that, refresh or restart this app.` | tray balloon body — **contains literal CLI command names, do not translate those tokens** |
| `codex_token_expired_title` | `Codex Auth Error` | |
| `codex_token_expired_body` | `Run 'codex' in a terminal and follow the sign-in prompts. After that, refresh or restart this app.` | same caveat as above |

**Placeholder convention**: exactly two keys use a `{placeholder}` — `update_prompt_now` (`{version}`) and `codex_resets_available` (`{count}`). Substitution is a plain literal-string `.replace("{token}", value)` at render time, no formatting library/ICU pluralization. This means, e.g., `codex_resets_available` reads slightly awkwardly for `count == 1` in some languages ("Resets: 1") — this was an accepted simplification (explicitly chosen over building real pluralization support) given the badge is meant to be a minimal, low-emphasis indicator, not prose.

---

## 15. Self-update mechanism

Full flow (see §6.4 for the network contract):

1. **Check** (automatic every 24h, or manual via menu): compares latest GitHub release tag to the running version.
2. If interactive (user-triggered) and an update is available: show a Yes/No message box (`"Version {version} is available. Do you want to update now?"`); if declined, nothing further happens (state remains "Available", the menu item now reads `"Update to vX.Y"` for next time).
3. If accepted (or triggered directly by clicking an already-known "Update to vX.Y" menu item, or an automatic/non-interactive background check that found an update — note: **non-interactive checks do not prompt or auto-apply**, they only update the stored "available" state silently for the user to act on later via the menu):
   - Verify the **current executable's own directory is writable** (a lightweight probe: create-then-delete a hidden marker file there) — fail fast with a clear "move the app out of Program Files" error if not, *before* downloading anything.
   - Download the new `.exe` from the release asset URL to a per-user local-data staging directory (`%LOCALAPPDATA%\AIUsageMonitor\updates\`, falling back to a temp-dir path if that can't be resolved), first to a `.part` file, then atomically renamed to its final staged name on success (never leaves a half-downloaded file at the "real" staged name).
   - Copy the **currently running** executable itself to a "helper" copy in that same staging directory (this is the "updater helper" — literally a copy of the old version of the app, reused as the update-applier process, so no separate helper binary needs to be built/shipped).
   - Launch that helper copy with special hidden CLI arguments: `--apply-update <target_path> <source_path> <current_pid>` (this is a distinct code path checked at the very top of `main()`, before any UI/window creation — `handle_cli_mode()` — so the helper process does its job headlessly and exits without ever showing a window).
   - The **original** (still-running) process then simply posts itself a `WM_CLOSE` and exits normally, releasing its file lock on its own executable.
4. **Helper process** (`--apply-update` mode): waits (up to 30s, via `WaitForSingleObject` on the original process's PID) for the original process to actually exit and release its file lock; then, in a retry loop (up to **60 attempts, 500ms apart** — i.e. up to ~30s total, tolerating lingering AV-scan/file-lock delays after process exit): rename the current target exe aside to a `.old` backup, copy the downloaded new exe into place at the target path, and on success delete the `.old` backup (on any failure at any step, roll back — delete whatever partial target file exists and rename the `.old` backup back into place — so the install location is never left in a broken/missing state). Once the swap succeeds: relaunch the (now-updated) target executable as a normal process (no special args), delete the downloaded staging file, and exit.
5. End state: user is now running the new version, launched fresh, with no manual action beyond the original Yes/No prompt (or menu click).

**Failure surfacing**: if the initial "is the location writable" check or the download step fails *before* the original process has committed to closing itself, an error message box is shown and the app continues running normally (no state corruption, nothing was touched). If the helper's file-swap ultimately fails after all retries, it shows its own error message box (since by then the original process is already gone) — the user is left with the *old* version still intact (rollback succeeded) but not auto-relaunched; they'd need to start it manually.

---

## 16. Autostart ("Start with Windows")

- **Mechanism**: a value named `AIUsageMonitor` under the registry key `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`, whose data is the **quoted** full path to the current executable (quoting matters for paths containing spaces).
- **Menu checkbox state** is computed from **existence** of that value at all (any non-trivial value present ⇒ shown as checked) — **deliberately not** an exact-path string match against the currently-running executable's own path. This was a real bug fix: the app can legitimately be launched from more than one location during development/testing (a release build output folder vs. a distributed copy, say), and an exact-path check made the menu checkbox show as *unchecked* (implying autostart was off) whenever the running copy's path didn't happen to be byte-identical to whatever path was last written — confusing, since the feature was in fact working from whichever path *was* registered.
- **Self-healing on every launch**: at startup, if a `Run` entry already exists at all (regardless of which path it currently points to), the app **rewrites** it to point at the *current* running executable's path. This means: if the app is moved, or replaced by a self-update to a location the OS install actually resolves to, or the user runs a different copy once, the registered autostart path silently heals itself to match — autostart never "goes stale" pointing at a since-deleted file. (If no entry exists yet, this self-heal does nothing — it only refreshes an existing entry, it does not silently *enable* the feature for a user who never turned it on.)
- **Toggling off** simply deletes the registry value entirely (`RegDeleteValueW`) — no other state, no "disabled but remembered" flag.

---

## 17. Diagnostics

- **Activation**: pass `--diagnose` as a command-line argument. Off by default (zero logging overhead/disk writes in normal operation).
- **Log file**: `%TEMP%\ai-usage-monitor.log` — truncated (fresh start) on every launch with the flag, not appended across runs.
- **Format**: one line per event, `[<unix-seconds>] <message>`. Every write is immediately flushed to disk (no buffering delay) so the log is useful even if the process later crashes/hangs.
- **What gets logged** (representative, not exhaustive — logging calls are sprinkled at meaningful state transitions throughout the poll/refresh/auth/update/tray code paths): process startup args and resolved log path, window creation, single-instance-mutex outcome, tray-icon registration, every poll attempt's outcome (success/which-error), every CLI-refresh-subprocess attempt and its outcome (including "still expired after refresh attempt" — this is the exact signal used to diagnose a genuinely-logged-out CLI, as opposed to an app bug, during development), WSL distro credential-probe failures, floating-window repositioning, update-check results, self-update apply failures, and TaskbarCreated re-registration events.
- **No PII/secrets are ever logged** — file paths and error messages are logged, but token values themselves are never written to the log.

---

## 18. Build & packaging notes

- **Icon source of truth**: `tools/gen_icon.ps1` (PowerShell + `System.Drawing`/GDI+, run manually/offline — not part of the normal build) regenerates `src/icons/icon.ico` plus loose `16x16.png`/`32x32.png`/`48x48.png`/`256x256.png` reference files, from a single parameterized drawing routine (see §9.5 for the geometry it implements). **Must be run via classic Windows PowerShell (`powershell.exe`), not PowerShell 7 (`pwsh`)** — `System.Drawing.Common`'s COM-interop-style type resolution was found to behave unreliably under `pwsh` during development; if porting the *generator itself* to a new environment, just use whatever native 2D drawing API is natural there (this Win32/GDI+-specific gotcha is not meaningful outside a Windows+PowerShell context).
- **Version source of truth**: `Cargo.toml`'s `[package] version`. The window title, tray tooltip, update-check comparison baseline, and the User-Agent sent to GitHub all derive from `env!("CARGO_PKG_VERSION")` at compile time — there is exactly one place to bump for a release.
- **Release tagging convention**: git tags of the form `v<major>.<minor>` (patch-less, e.g. `v1.6`, `v1.7` — not `v1.7.0`), matching what `Cargo.toml`'s three-part version collapses to for *display* purposes (`display_version()` strips a trailing literal `".0"` suffix, so `1.7.0` displays as `"1.7"` everywhere in the UI, but the underlying Cargo/PE version metadata does remain the full three-part `1.7.0`).
- **Release binary asset name**: always exactly `ai-usage-monitor.exe`, published as the sole release asset on GitHub (see §6.4/§15 — the update mechanism looks for exactly this filename first).
- **Reference-only note on the CI/CD setup** (not part of the app's own runtime behavior, but relevant if reimplementing the whole project including its release pipeline): the GitHub Actions workflow builds on `windows-latest`, triggered by either a `v*` tag push or a manual `workflow_dispatch` (with a `tag` input) — the manual trigger was added after discovering that tag-pushes alone were, for unclear reasons specific to this repository's history, not reliably triggering a run; manual dispatch was confirmed to work reliably and is the currently-recommended way to actually cut a release (`gh workflow run release.yml -f tag=vX.Y`).

---

## 19. Consolidated list of non-obvious business rules / edge cases

A rewrite that misses any of these will diverge from real, previously-observed behavior of the original app. Each entry below references the section with full detail.

1. A failure in one service's poll must never blank out the other service's already-working display (§7.1).
2. `available: false` on a usage window must render distinctly from `0%` — the two mean different things (§4.1, §9.6 — currently an em/en dash `–`).
3. Codex's two rate-limit windows must be classified by their *actual reported duration*, not by which JSON field (`primary`/`secondary`) they arrived in — OpenAI stopped guaranteeing the old positional convention (§6.3, §7.5).
4. A "reset" (for notification purposes) is detected as the *new* `resets_at` being ≥60s later than the *previous* poll's `resets_at` for that same window — not simply "the old resets_at is now in the past" (§12.1).
5. Reset-notification arm flags are preserved (not force-cleared) while no ntfy channel is configured — only the *detection/firing* logic is gated on having a channel, not the persisted arm state itself (§12.1).
6. Clicking an unarmed checkbox with no channel configured opens the configuration dialog instead of toggling; the checkbox only becomes armed if the dialog is completed with a non-empty result (§9.6, §12.2).
7. At least one of the two services must always remain enabled — the UI must refuse (silently no-op, not error) an attempt to disable the last remaining one (§11, and enforced again defensively when loading settings from disk, §13).
8. Autostart's "is it enabled" check is existence-based, not exact-path-based, and the registered path self-heals to the current executable's path on every launch (§16).
9. The tray icon handle is cached once and never re-extracted from the exe on a NULL/failed extraction — a transient extraction failure must not blank the tray icon (§10).
10. The app must listen for the shell's `"TaskbarCreated"` broadcast and re-add its tray icon in response, or the icon permanently vanishes after any Explorer restart (§10).
11. Auth-failure recovery must not busy-poll the network — it should watch a cheap local credential-file fingerprint and only re-attempt the real network call once that fingerprint changes (§7.3).
12. Countdown timers should be self-rescheduling based on "seconds until the displayed digit would next change," not a flat 1-second interval — for battery/CPU friendliness in a long-running background widget (§7.4).
13. Window position must always be clamped to the current virtual-screen bounds (spanning all monitors), both on load and after any drag, so the widget can never become permanently off-screen/unreachable (§8).
14. The self-update helper process must verify install-location writability *before* downloading anything, and must roll back cleanly (restore the pre-update binary) if the final file-swap fails after all retries (§15).
15. Notification message bodies sent to ntfy.sh are currently English-only/unlocalized — a known, accepted gap, not a bug to silently work around by guessing a localization scheme without being asked (§12.4).
16. Do not add a Russian localization — explicit standing instruction (§14).
17. Never hard-code a real ntfy.sh topic name into source — it is strictly a per-user local setting (§12.2).

---

## 20. Explicitly out of scope (things this app deliberately does *not* do)

- No account creation, login UI, or credential storage of its own — 100% dependent on the official `claude`/`codex` CLIs already being installed and logged in.
- No backend service, no analytics/telemetry, no crash reporting service.
- No redemption/spending of Codex's banked reset credits (informational display only, §7.6).
- No per-service distinct tray icons or live-updating tray icon badges (single static icon, §10).
- No window resizing by the user (fixed, content-driven size only).
- No multi-window support, no settings/preferences dialog beyond the single context menu + the one small text-input dialog.
- No packaged installer/MSI — distributed as a single portable `.exe` (self-update handles version upgrades in place, §15).
