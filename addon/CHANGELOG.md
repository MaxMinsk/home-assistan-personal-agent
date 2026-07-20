# Changelog

## 0.11.0

- **Autonomous agents.** You can now create background agents from the panel: give one a mission ("research what business to start in Minsk after I quit"), a cadence, and what it may use — and it wakes up on its own, researches, and sends you a short brief. It never interrupts you between runs. (Sprint HPA-S4 of the autonomous-agents epic.)
- **The reply loop.** A brief arrives in Telegram with up to three clarifying questions. Reply to that message and your answer is queued — it is folded into the context of the *next* scheduled run rather than starting one immediately. You can answer from the panel instead; both go to the same queue. Each agent carries its focus and still-open questions across runs, so it continues rather than restarting.
- **Managing agents in the panel.** The left roster now lists your agents with a live status (running, waiting for your answer, paused) and the next run time. Each agent has tabs: Обзор (run now, pause, delete), Запуски (feed of past briefs), Вопросы (answer box), Настройки (mission, schedule, Telegram target, permissions), Память (what it is focused on and its memory note).
- **Memory stays clean.** Each agent keeps exactly ONE note in long-term memory — a research capsule rewritten on every run, so notes never pile up. Beyond that it may save at most a few durable facts per run (hard cap of 5), and everything else — run history, briefs, your answers — stays local to the add-on.
- **Safe by default.** A background run is read-only: it can read Home Assistant and long-term memory, but cannot control devices, because you are not there to approve anything. Runs have a timeout, never overlap for the same agent, survive restarts, and a slot missed while the add-on was off is either run once or skipped (configurable).
- **Thinking mode now has tools.** Previously the deepest reasoning mode was the only one that could not consult memory or Home Assistant, which pushed it toward guessing. Deep reasoning now has the same tool access as a normal reply; the Web UI switch was relabelled `быстрый | глубокий` to stop implying you must choose between tools and thinking. (HPA-037.)
- New options: `autonomous_agents_enabled`, `autonomous_agent_run_timeout_minutes`, `autonomous_agent_max_concurrent_runs`, `autonomous_agent_catch_up_policy`.
- **Known gap:** web search is not implemented yet, so research currently draws on Home Assistant state, your long-term memory and the model's own knowledge. Web-dependent missions (market/legal/listings research) will be noticeably stronger once it lands (HPA-034).
- Internal: new `Autonomous` subsystem (domain + local SQLite + scheduler + runner + capsule writer), a neutral `AgentToolPolicy` on `AgentContext` that lets a run withhold risky tools, `Cronos` for 5-field cron, and `/api/agents` endpoints behind the same ingress/token gate. Tickets HPA-028…HPA-033 + HPA-037.


## 0.10.0

- The "Personal Agent" sidebar panel is now a real interface instead of a placeholder. It opens as a two-pane console: a left roster with the interactive Conversation agent (pinned on top, with a live status indicator) and a section where background agents will appear once they exist, plus a detail pane with three tabs. (HPA-027.)
- **Chat** — talk to the agent directly from Home Assistant. Reasoning streams in live while the agent thinks, a `tools`/`deep` switch picks the run profile, and "Очистить контекст" clears the conversation. The web chat runs through the same agent runtime and long-term memory as Telegram while keeping its own chat history, so the two channels never mix threads.
- **Context** — live counters for the dialogue window: stored messages, loaded messages vs. the window size, raw events, estimated token usage, the memory-retrieval mode and persisted-summary state.
- **Memory** — shows the chat's rolling persisted summary with its version and last update, backed by a new `GET /api/dialogue/summary` endpoint.
- The panel follows your light/dark system preference and has a manual theme toggle. It is fully self-contained (no external CDNs or fonts), which HA Ingress requires.
- Internal: the SPA is TypeScript + Preact built with Vite (`src/HaPersonalAgent/ClientApp` → `wwwroot`). The built bundle is committed, so the .NET build, CI and the add-on image need no Node toolchain — after changing the UI, run `npm run build` in `ClientApp` and commit the regenerated `wwwroot`. Completes sprint HPA-S3 (HPA-025, HPA-026, HPA-027) of the autonomous-agents epic (HPA-024).


## 0.9.0

- Add an embedded Web UI host behind Home Assistant Ingress. The add-on now serves a web panel (sidebar entry "Personal Agent") on ingress port 8099 in the same process as the worker and the Telegram bot. This release lays the foundation — a health endpoint and a placeholder page; the full management interface (conversation chat + autonomous agents) follows. New options: `web_ui_enabled` (default on) and an optional `web_api_token` that protects direct (non-ingress) access — requests that arrive through HA Ingress are trusted, so the panel needs no token. (HPA-025.)
- Add a JSON dialogue API for the conversation agent, served by the same web host and reusing the transport-agnostic dialogue core, so Telegram and the Web UI share one runtime, history and memory: `POST /api/dialogue/turn`, `POST /api/dialogue/stream` (Server-Sent Events with live reasoning), `GET /api/dialogue/context` and `POST /api/dialogue/reset`. Web conversations use a separate `web` identity that never collides with Telegram keys. (HPA-026.)
- Internal: the host moved from the plain generic host to ASP.NET Core `WebApplication` (Kestrel) with a `Microsoft.AspNetCore.App` framework reference; the `ask` CLI path stays server-less. Ingress path handling uses the `X-Ingress-Path` header, and the static SPA is served from `wwwroot`. First slice of the autonomous-agents epic (HPA-024), sprint HPA-S3.


## 0.8.0

- Retire the local project-capsule subsystem (memory redesign, Phase A). Structured long-term memory now lives only in Memory MCP — read via `memory_recall`/`memory_tags`, written via `propose_memory_save` — removing the divergent local copy that caused stale and confabulated answers (the agent wrote capsules to local SQLite, mirrored them to MCP, but always read from local, so MCP fixes were invisible). Removed: capsule auto-extraction from raw events, the local `project_capsules`/extraction-state tables, the `project_capsules_list`/`project_capsule_get`/`propose_project_capsule_upsert` tools, the capsule→MCP mirror, the `/showCapsules`, `/refreshCapsules` and `/clearlocalcapsules` commands, and the `capsule_extraction_mode`/`capsule_auto_batch_raw_event_threshold` options.
- Conversation history and the rolling summary stay local for hot-path resilience for now; a later phase will make Memory MCP their store of record with a local cache so an MCP outage never breaks a turn.


## 0.7.2

- Add a `/clearlocalcapsules` Telegram command that deletes the local project capsules for the current chat (and resets the capsule-extraction watermark so they are not re-derived from old raw events). Long-term Memory MCP notes are untouched. Use `/resetContext` for a full reset (history + summary + capsules).


## 0.7.1

- Route long-term-memory questions to a tools-enabled path. "поищи в памяти / вспомни / what do you remember"-style questions were classified as simple chat and sent to the cost-optimized profile, which strips every tool (including `memory_recall`/`memory_tags`), so the assistant said it had no memory access and confabulated. Memory-intent keywords now keep tools available for those turns.
- `memory_recall`: when `tags` or `type` are provided, ignore the free-text query. Memory MCP ANDs query tokens with the filter, so a noisy natural-language query alongside an exact filter (e.g. query "количество сортов перцев" + `tags=crop:pepper`) AND-matched to nothing and returned 0. The structured filter now wins, and the instructions tell the assistant to leave the query empty when filtering by tag/type and to narrow with additional facet tags.


## 0.7.0

- Structured memory search (Phase 1 of the memory redesign). `memory_recall` now accepts optional `tags` (comma-separated facets, e.g. `crop:pepper`) and `type` (e.g. `seed_variety`) combined with the query, so "how many / which / list X" questions are answered by exact, morphology-proof structure instead of brittle full-text matching. A new `memory_tags` tool lists facet tags (optionally by prefix) so the assistant can discover the right facet, and the instructions teach it to prefer structured queries and read `total` for counts. (HPA-022.)


## 0.6.3

- Fix memory recall returning nothing for natural-language questions. Memory MCP's search matches query tokens with AND, so passing the raw user message ("сколько у меня перцев?") required function words that never appear in notes and returned 0 hits. The recall query is now reduced to content tokens with prefix matching (drop stop-words/punctuation, append `*`), so real questions surface the relevant notes. Applied to both auto-recall and the `memory_recall` tool (`MemoryRecallQueryBuilder`).


## 0.6.2

- Add diagnostic logging for every Memory MCP tool call: the add-on log now records the exact tool, endpoint, token fingerprint (last 4 chars only), verbatim arguments (domain/query/limit), and the raw server response (length + preview incl. `total`/error). This lets an empty or unexpected `memory_recall` result be diagnosed from the add-on log directly, instead of relying on the assistant's own narration.
- `memory_recall`: support pagination (`offset`) and a larger page size, and surface `total`/`hasMore` so the assistant answers "how many" from `total` (not the visible snippet count) and can page through to list everything (HPA-020).


## 0.6.1

- Fix long-term memory recall returning nothing: both recall paths required a `ha-personal-agent` marker tag that the user's imported notes never carry, so `memory_recall` and the auto-injected context found almost nothing and the assistant confabulated answers. Recall now scopes by domain only and finds all of the user's durable notes.
- Stop dropping long-term memory on the cost-optimized "simple" route: short personal-fact questions ("how many pepper varieties do I have?") looked like simple chat and were answered by the small model with memory stripped. Memory now survives the cost route, and memory-heavy turns fall back to the default model.
- Add a grounding rule to the assistant's instructions: if memory search returns nothing, say so and ask — never invent facts/lists/counts, and never claim a save happened without tool confirmation.
- Clarify the `memory_recall` tool: full-text relevance search over the user's memory; pass a natural-language query, not tag syntax.
- Raise the default model from `kimi-k2.5` to `kimi-k2.6` (newer general-purpose Moonshot model). Existing installs keep their configured value — change it in the add-on options to adopt the new default.


## 0.6.0

- Fix age/date reasoning: every run now includes the real current date/time, so the assistant no longer computes ages, "tomorrow"/"in a week", or memory timestamps from a stale remembered date.
- Add a `memory_mcp_status` tool and list the long-term-memory tools in the assistant's instructions when Memory MCP is configured, so it can see its memory capabilities instead of guessing.
- Make the active model/routing profile explicit to the assistant, and state that tools withheld on the cost-optimized profile are intentional (not an outage).
- Clarify when to use project capsules (structured topic cards) vs. saving an ad-hoc durable fact.


## 0.5.0

- Retire the local vector-memory store: long-term recall now comes from Memory MCP (when configured) instead of the local hash-vector index, and the `/showVector` command is removed. Without Memory MCP configured, the rolling conversation summary remains the long-term context.
- Mirror project capsules to Memory MCP as `project_capsule` notes in the `home` domain when `memory_store_type` is `memory_mcp`.
- Back-fill existing local conversation summaries and project capsules into Memory MCP on first start when `memory_store_type` is `memory_mcp` (one-time and idempotent).
- Internal: remove the `conversation_vector_memory` table, types and hash-embedding code.


## 0.4.0

- Add agent-facing long-term memory tools (active only when Memory MCP is configured): `memory_recall` to look up durable facts, and `propose_memory_save` to save a durable fact through the existing approval flow (`/approve`).
- Mirror durable conversation summaries to Memory MCP when `memory_store_type` is `memory_mcp`: summaries are written as lossless `conversation_summary` notes in the `home` domain. The short-term window stays local and the hot path is never blocked on remote calls; if Memory MCP is unreachable the mirror is skipped without breaking the conversation.
- Internal: dedicated Memory MCP note types for durable memory, a documented memory-model mapping, and best-effort resilient mirroring that never breaks a dialogue turn.


## 0.3.0

- Add a Memory MCP integration foundation: the add-on can now connect to a shared Memory MCP server over streamable HTTP. New optional add-on options `memory_mcp_endpoint`, `memory_mcp_token`, `memory_mcp_domain`, `memory_mcp_project` and `memory_store_type` (existing installations are unaffected).
- Log a Memory MCP health check at startup (status, server version, tool count) without exposing the token.
- Internal: introduce the `IConversationMemoryStore` seam over the SQLite conversation memory (no behavior change) as the precondition for durable memory on Memory MCP.
- Internal: migrate the project backlog and docs into Memory MCP; `backlog.md` is now reference-only.


## 0.2.26

- Enable enforced cost-aware routing by default: simple chat uses `moonshot-v1-8k` without tools/reasoning, while simple tool-heavy requests keep tools but disable expensive reasoning.
- Add explicit MAF function-loop safety limits to prevent excessive repeated LLM round trips.
- Guard oversized Home Assistant MCP tool results before the next LLM tool step and return compact project capsule previews from list calls.
- Stabilize MCP tool ordering and add detailed per-request diagnostics for prompt/tool/result sizes, estimated input tokens, cached input tokens, cache-hit ratio, and prefix hashes.
- Fix repeated persisted-summary generation when summarization returns unchanged text by advancing the processed-message watermark without incrementing summary version.

## 0.2.25

- Add Telegram command `/routerProbe <text>` to inspect routing decision without LLM call (intent, context profile, blocker reason, model target, reasoning mode, bucket).
- Add dialogue API to build real current-chat runtime context for routing probe, so probe diagnostics match actual bounded-history/summary retrieval inputs.
- Register `routerprobe` in Telegram bot command hints and update `/start` help text.
- Fix Home Assistant add-on options backward compatibility: make `llm_router_simple_max_input_chars`, `llm_router_simple_max_history_messages`, and `llm_router_simple_allow_tools` optional in schema for existing installations.
- Expand Telegram handler tests for `/routerProbe` (success path and missing-argument path).


## 0.2.24

- Complete HAAG-056 routing overhaul for `enforced` mode: deterministic intent classes (`simple_chat`, `complex_analysis`, `tool_heavy`, `deep_reasoning`) with explicit `simple_packed` vs `default_full` context profiles.
- Add `LlmRoutingContextProfileBuilder` and apply packed context for simple-chat route (bounded recent tail, trimmed summary, retrieval-memory drop) so small-path can work under long raw history.
- Add new router configuration options for simple-path budgets and behavior (`llm_router_simple_max_input_chars`, `llm_router_simple_max_history_messages`, `llm_router_simple_allow_tools`) in app settings and Home Assistant add-on UI mapping.
- Extend runtime and `/status` diagnostics with router intent/profile/blocker visibility plus richer decision reason traces for debugging route selection.
- Expand test coverage for routing matrix and packing behavior (simple under large context, tool-heavy/default guardrail, prompt-shape blocker, resolver effective profile, context profile builder, config binding/status).


## 0.2.23

- Refactor `AgentRuntime` into orchestration facade + dedicated components (`Preflight`, `ExecutionResolver`, `MafFactory`, `Runner`, `ToolCatalog`, `CompactionPipelineFactory`, `FallbackExecutor`, diagnostics/result/toolset resolvers) and add focused unit tests for these components.
- Upgrade persisted summary pipeline (HAAG-055): strict delta-merge summarization prompt (`new_summary = merge(old_summary, summary(new_tail))`), anti-drift rules, importance scoring, and source attribution section contract.
- Add explicit persisted summary refresh reasons (`missing|threshold|topic-shift|manual`) in dialogue/runtime logs and summarize prompt diagnostics.
- Extend `/status` diagnostics for summary quality/freshness: refresh suggestion + reason/threshold, structured-contract flag, facts/conflicts counters, and history/summary compression ratio.
- Add summary quality harness tests (`PersistedSummaryPromptBuilder`, `PersistedSummaryRefreshPolicy`, `PersistedSummaryQualityAnalyzer`) and update memory/backlog docs.


## 0.2.22

- Add adaptive LLM routing layer (`off|shadow|enforced`) with deterministic `model + reasoning` decision and per-run diagnostics.
- Add retry safety for routed small-model path: one fallback attempt from small model to default model on retryable provider/model errors.
- Extend add-on/UI and runtime configuration with router options (`llm_router_mode`, `llm_router_small_model`, `llm_router_max_input_chars_for_small`, `llm_router_max_history_messages_for_small`, `llm_router_deep_keywords`).
- Extend Telegram `/status` with router telemetry, last routing decision details, and fallback visibility.
- Improve confirmation proposal reliability in Telegram: resolve pending confirmation id directly from runtime scope and normalize approve/reject commands in outbound message text.
- Add repository/service support methods for scoped confirmation lookup by `(conversation, participant, correlation)` and by `confirmationId`, plus additional diagnostics logs.
- Update reasoning flow docs and backlog state (HAAG-048 moved to Done) and expand tests for routing decision matrix, fallback policy, and configuration validation.

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
