# Changelog

## 0.2.21

- Add Telegram command `/showVector [N]` to inspect recent vector-memory entries for the current chat.
- Extend Telegram command hints (`setMyCommands`) with `showvector`.
- Improve reasoning preview UX: create delayed placeholder when reasoning stream is slow and add explicit lifecycle diagnostics logs.
- Extend `/status` with approximate loaded-context token estimate and breakdown (`history/summary/capsules/scaffolding`) using MAF-aligned `UTF8 bytes / 4` heuristic.
- Extend context snapshot model and tests for vector-memory command, reasoning preview diagnostics, and token-estimate reporting.


## 0.2.20

- Add Telegram live reasoning preview (ephemeral): for long requests, show temporary reasoning progress and remove it after final answer.
- Add streaming reasoning callback path in runtime/dialogue transport contracts without persisting preview text to memory layers.
- Extend Telegram adapter with send/edit/delete message operations required for preview lifecycle.
- Add add-on options `reasoning_preview_enabled` and `reasoning_preview_delay_seconds` (+ docs/translations/tests).
- Complete HAAG-051 and move it to Done in backlog.


## 0.2.19

- Publish Telegram command metadata via `setMyCommands`, so Telegram UI shows command hints when user types `/`.
- Register command hints at gateway startup with retry-on-failure behavior and explicit diagnostics logs.
- Keep command names Telegram-compatible (lowercase) while preserving existing case-insensitive command handling.
- Extend Telegram test adapter with command registration support.


## 0.2.18

- Add configurable memory retrieval mode: `before_invoke` (auto-injected vector recall) or `on_demand_tool` (explicit `search_conversation_memory` tool calls by the agent).
- Expose retrieval mode diagnostics in `/status` and runtime/dialogue logs to make memory behavior transparent.
- Add Telegram inline confirmation buttons (`Подтвердить` / `Отклонить`) with callback handling and keyboard cleanup after click.
- Keep `/approve <id>` and `/reject <id>` as text fallback confirmation commands.
- Improve `/approve` result formatting for `project_capsule_upsert`: human-readable summary instead of raw escaped JSON payload.
- Extend configuration/docs/translations/tests for retrieval-mode switch, callback confirmations, and improved confirmation result rendering.


## 0.2.17

- Operational add-on release to republish current `0.2.16` functionality (no code changes).


## 0.2.16

- Add bounded chat history + vector overflow memory layer (`conversation_vector_memory`) with retrieval context injection before each tool-enabled run.
- Add derived project capsules memory layer (`project_capsules` + extraction state) with manual/auto-batched refresh modes and add-on options.
- Add Telegram capsule commands (`/showCapsules`, `/refreshCapsules`) and extend `/status` with capsule storage/extraction diagnostics.
- Expose capsule tools to the agent runtime: `project_capsules_list`, `project_capsule_get`, and `propose_project_capsule_upsert`.
- Add confirmation-gated capsule write executor (`project_capsule_upsert`) so capsule updates are applied only after `/approve`.
- Extend storage/dialogue/confirmation test coverage for capsules, extraction state, and confirmation write path.


## 0.2.15

- Add Telegram typing indicator loop for long LLM operations (`typing...`) without persisting progress artifacts into dialogue memory.
- Introduce append-only `raw_events` SQLite store as source-of-truth event log, separated from trimmed `conversation_messages`.
- Persist user/assistant/system-notification/reset events into `raw_events` and expose raw event counters in `/status`.
- Add Telegram command `/showRawEvents [N]` to inspect recent raw events for the current chat.
- Extend storage/dialogue/telegram tests for raw event persistence, raw event command output, and updated status diagnostics.


## 0.2.14

- Add dedicated `Summarization` LLM execution profile and force `thinking=disabled` for summary-compaction requests when provider supports request-level thinking control.
- Use `Summarization` profile for forced `/refreshSummary` service runs to keep summary rebuild deterministic and cheaper.
- Improve summary continuity by injecting previous persisted summary as explicit baseline in summarization prompt, so relevant long-term facts are retained across topic changes.
- Expand planner/dialogue/telegram tests for summarization profile and forced refresh behavior.

## 0.2.13

- Add Telegram command `/refreshSummary` to force immediate persisted summary rebuild for the current chat.
- Rename summary inspection command from `/showSummarized` to `/showSummary` for consistent command naming with refresh flow.
- Add forced summary refresh path in dialogue/runtime (`PureChat` service run, compaction summarize trigger override) without appending extra dialogue turns.
- Relax and enrich persisted summary prompt so memory keeps more durable context/facts instead of overly short snapshots.
- Add regression tests for forced refresh behavior in dialogue and Telegram handlers.

## 0.2.12

- Fix persisted summary quality: stop paragraph-wise append/merge and store a single canonical summary snapshot per refresh.
- Tighten compaction summarization prompt to a structured memory format (context, facts, open tasks, constraints) without user-facing chatter.
- Update dialogue tests for snapshot-replacement behavior and refresh memory-flow docs accordingly.

## 0.2.11

- Fix persisted summary refresh behavior: merge new summarize output with existing summary instead of replacing it with a single recent chunk.
- Add regression test coverage for summary merge on repeated compaction refresh.
- Ignore local `references/` and `sdks_comparision.md` artifacts in git.

## 0.2.10

- Fix rolling summary refresh strategy: summarize now runs when summary is missing or when enough new messages accumulated after the last summary.
- Add context counters to Telegram `/status` (stored/loaded message counts, summary version/length/source id, messages since summary).
- Add context snapshot API in dialogue/storage layers and expand tests for summary refresh/context diagnostics.

## 0.2.9

- Add Telegram command `/showSummarized` to inspect persisted conversation summary for the current chat.
- Keep `[context-summary]` visible to the user response but stop persisting this marker in `conversation_messages`.
- Add dialogue/Telegram tests for persisted summary command behavior and cleaned assistant turn persistence.
- Update memory flow docs for persisted summary read path and non-persistent summary marker behavior.


## 0.2.8

- Add persisted summary memory in SQLite (`conversation_summary`) separated from regular dialogue turns.
- Inject persisted summary into runtime prompt as a dedicated context layer before recent turns.
- Persist compaction summarization output as a summary candidate for reuse across subsequent runs.
- Extend `/resetContext` flow to clear both recent turns and persisted summary memory.
- Add SQL and dialogue tests for summary upsert/get/clear and summary reuse on the next runtime call.
- Tune compaction thresholds so summarize step does not trigger on every run with the default 24-message history window.


## 0.2.7

- Operational release to publish the current stable code state and Home Assistant add-on image.


## 0.2.6

- Align memory compaction with MAF patterns via `CompactionProvider` + `PipelineCompactionStrategy` (`ToolResult`, `Summarization`, `SlidingWindow`, `Truncation`).
- Add per-run compaction diagnostics and explicit `[context-summary]` notice in assistant response when summarize-step is applied.
- Add per-request LLM diagnostics middleware and run-level reasoning diagnostics aggregation for clearer tool/thinking troubleshooting.
- Add SQL-focused memory regression tests proving compaction notice is persisted as a normal assistant turn and reused from `conversation_messages`.
- Add `memory_flow.md` and refresh reasoning/docs/backlog notes for MAF-first reference alignment.


## 0.2.5

- Add detailed Telegram diagnostics for long polling iterations, update routing, and dialogue request lifecycle.
- Expand `/status` with reasoning diagnostics (`ReasoningActive`, requested/effective reasoning plan, request patch, safety fallback visibility).
- Add request-patch decision logs for forced/safety thinking overrides in chat completions.
- Add reasoning replay middleware diagnostics to show tool-step reasoning capture/replay coverage and failure reasons.
- Add dialogue persistence diagnostics that explain when turns are saved or intentionally skipped.

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
