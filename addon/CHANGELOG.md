# Changelog

## 0.2.4

- Fix Moonshot/Kimi first tool-enabled turn failure (`HTTP 400 reasoning_content is missing`) by enabling request policy in `auto` tool profile and applying request-level safety fallback.
- Add robust `reasoning_content` detection in request patching for multiple JSON shapes.
- Add regression tests for tool-call history with/without `reasoning_content` to guarantee non-breaking behavior.

## 0.2.3

- Add provider-agnostic LLM capability profiles and adaptive execution planner for `tool-enabled`, `pure chat`, and `deep reasoning` runs.
- Add `llm_thinking_mode` (`auto|disabled|enabled`) to Home Assistant add-on UI/options mapping and status diagnostics.
- Add Telegram `/think` command for no-tools deep reasoning mode with transport-agnostic execution profile wiring.
- Replace Moonshot-only thinking patch with generic chat completion request policy.
- Add per-run `ReasoningContentReplayChatClient` middleware to capture/replay reasoning content across tool-call steps.
- Keep Moonshot tool-enabled `auto` mode on provider-default thinking when reasoning replay is available.
- Add tests for planner selection, request policy behavior, deep reasoning command, and reasoning replay middleware.

## 0.2.2

- Treat Home Assistant `GetLiveContext` as read-only so state questions can run without approval.
- Return a sanitized and truncated result preview after `/approve` completes an action.
- Log Home Assistant MCP tool policy categories by tool name for easier diagnosis.
- Suppress normal info-level MCP client stream shutdown noise.
- Expand backlog notes for Moonshot/Kimi `reasoning_content` and move confirmation result UX to Done.

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
