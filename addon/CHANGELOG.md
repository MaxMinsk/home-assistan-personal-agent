# Changelog

## 0.2.1

- Use `SUPERVISOR_TOKEN` for the default `http://supervisor/core` Home Assistant MCP endpoint and keep long-lived tokens for direct Core URLs.
- Add a transport-agnostic Home Assistant MCP status tool for the MAF agent instead of Telegram-specific natural-language shortcuts.
- Add Moonshot/Kimi request policy that disables thinking for tool-compatible chat completions to avoid missing `reasoning_content` failures.
- Return user-facing fallback messages for LLM provider errors without saving failed turns to dialogue memory.
- Add safer runtime logs for agent runs, MCP discovery/tool loading, auth source selection, and confirmation execution without logging secrets or raw payloads.
- Add backlog task for adaptive thinking/reasoning mode across tool-enabled, pure chat, and deep reasoning runs.

## 0.2.0

- Add transport-agnostic dialogue layer so Telegram is an adapter over shared dialogue/runtime/memory contracts.
- Add Home Assistant MCP discovery/status and expose read-only MCP tools to the MAF agent runtime.
- Add generic confirmation policy for risky actions with SQLite pending confirmations, audit log, and Telegram `/approve`/`/reject`.
- Add Home Assistant MCP control executor behind the generic confirmation flow.
- Add memory analysis/backlog updates for rolling summary and future vector storage.

## 0.1.3

- Add Telegram long polling dialogue MVP with `/start`, `/status`, and `/resetContext`.
- Persist per-chat Telegram conversation context in SQLite and pass recent turns into the agent runtime.
- Add memory strategy analysis: MVP uses last N turns plus rolling summary; vector memory is planned post-MVP.

## 0.1.2

- Add the first Microsoft Agent Framework runtime spike with Moonshot/OpenAI-compatible wiring and a safe status tool.
- Add SQLite state storage for Telegram update offsets.
- Remove the noisy worker heartbeat log.

## 0.1.1

- Fix Telegram allowlist option so Home Assistant accepts a single user ID or a comma-separated list from the add-on UI.

## 0.1.0

- Initial Home Assistant app/add-on packaging skeleton.
