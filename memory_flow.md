# Memory Flow

## Контекст

Этот документ фиксирует текущий memory flow в проекте **Home Assistant Personal Agent** после внедрения HAAG-034/HAAG-039/HAAG-043/HAAG-041 (MAF compaction pipeline + persisted summary + bounded history/vector overflow + project capsules).

Цель документа:

- показать, как сообщение проходит путь `transport -> runtime -> LLM -> persistence`;
- явно зафиксировать, что и где хранится в SQLite;
- объяснить, как compaction влияет на prompt и почему это не ломает SQL-память.

## Что уже умеем (текущий baseline)

На текущем этапе memory pipeline работает как четырехслойная схема:

1. Короткая оперативная память:
   - `conversation_messages` хранит только recent window (`ConversationContextMaxTurns * 2`).
2. Persisted summary:
   - `conversation_summary` хранит долгоживущий сжатый контекст по разговору.
3. Overflow retrieval memory:
   - вытесненные сообщения архивируются в `conversation_vector_memory`;
   - перед каждым run выполняется retrieval и релевантный memory-context подмешивается в prompt.
4. Derived project memory:
   - `project_capsules` хранит карточки проектов, извлеченные из `raw_events`;
   - extraction работает в режимах `manual` или `auto-batched`;
   - в prompt добавляется отдельный блок капсул;
   - агенту доступны tools `project_capsules_list`/`project_capsule_get` (read) и `propose_project_capsule_upsert` (write через confirmation).

Дополнительно:

- `raw_events` ведется как append-only source of truth для диагностики и будущих derived-memory пайплайнов.
- `/resetContext` очищает все слои conversation memory (`messages`, `summary`, `vector_memory`, `project_capsules` + extraction state) для текущего scope.
- `/status` показывает метрики по слоям: `Context(stored)`, `RawEvents(stored)`, `VectorMemory(stored)`, `ProjectCapsules(...)`, `PersistedSummary(...)`.

## Ограничения текущего этапа

- Векторизация локальная и легковесная (hash-based embedding), без внешнего embedding provider.
- Retrieval сейчас fixed в режиме `before_invoke` (подмешивание перед каждым run), без `on_demand` режима через tool call.
- Капсулы пока conversation-scoped и не покрывают полноценный cross-conversation user/project graph.
- Качество extraction зависит от batched LLM refresh и будет улучшаться отдельным facts-layer/quality pass.

## Что получим дальше (план)

Следующие memory-этапы:

1. `HAAG-044`: switch retrieval mode (`before_invoke` vs `on_demand_tool`) и прозрачная диагностика выбранного режима.
2. `HAAG-045`: memory hygiene filters + source attribution, чтобы retrieval/context сообщения не загрязняли обычную историю.
3. `HAAG-042`: artifacts по капсулам (MD/PDF) и повторная выдача через Telegram вложения.
4. `HAAG-046`: переход от локальной векторизации к production-grade backend/embeddings.

## Источники паттерна (MAF)

- `Agent_Step18_CompactionPipeline`:
  - <https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/Agents/Agent_Step18_CompactionPipeline/Program.cs>
- `AgentWithMemory_Step05_BoundedChatHistory`:
  - <https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/AgentWithMemory/AgentWithMemory_Step05_BoundedChatHistory/BoundedChatHistoryProvider.cs>
- `AgentWithMemory_Step01_ChatHistoryMemory`:
  - <https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/AgentWithMemory/AgentWithMemory_Step01_ChatHistoryMemory/Program.cs>
- `AgentWithMemory_Step02_MemoryUsingMem0`:
  - <https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/AgentWithMemory/AgentWithMemory_Step02_MemoryUsingMem0/Program.cs>
- ADR про atomic group compaction:
  - <https://github.com/microsoft/agent-framework/blob/main/docs/decisions/0019-python-context-compaction-strategy.md>

## Где хранится состояние

### SQLite (`state.sqlite`)

Ключевая таблица диалоговой памяти:

- `conversation_messages`
  - `id` (AUTOINCREMENT)
  - `conversation_key`
  - `role` (`User`/`Assistant`)
  - `content`
  - `created_utc`

- `conversation_summary`
  - `conversation_key` (PK)
  - `summary`
  - `updated_utc`
  - `source_last_message_id`
  - `summary_version`

- `conversation_vector_memory`
  - `id` (AUTOINCREMENT)
  - `conversation_key`
  - `source_message_id` (id исходного сообщения из `conversation_messages`)
  - `role` (`User`/`Assistant`)
  - `content`
  - `embedding` (локальный нормализованный вектор, сериализованный как CSV)
  - `created_utc`

- `raw_events`
  - `id` (AUTOINCREMENT)
  - `conversation_key`
  - `transport` / `conversation_id` / `participant_id`
  - `event_kind`
  - `payload`
  - `source_id` / `correlation_id`
  - `created_utc`

- `project_capsules`
  - `id` (AUTOINCREMENT)
  - `conversation_key`
  - `capsule_key`
  - `title`
  - `content_markdown`
  - `scope`
  - `confidence`
  - `source_event_id`
  - `updated_utc`
  - `version`

- `project_capsule_extraction_state`
  - `conversation_key` (PK)
  - `last_raw_event_id`
  - `updated_utc`
  - `runs_count`

Отдельно (не memory turns):

- `pending_confirmations`
- `confirmation_audit`
- `agent_state` (например Telegram offset)

### Временное состояние run (не в SQL)

- `ReasoningContentReplayChatClient` cache (`reasoning` по tool-signature) живет только в памяти процесса и только в рамках текущего run.
- `CompactionRunDiagnostics` и `ReasoningRunDiagnostics` живут только в памяти текущего run.
- `CompactionProvider` state живет в `AgentSession.StateBag`; в текущей реализации session создается на run (`session: null`), поэтому состояние compaction не пишется в SQLite.

## Поток сообщения

1. `TelegramUpdateHandler` (или другой transport) отправляет запрос в `DialogueService`.
2. `DialogueService`:
   - считает `conversationKey`;
   - читает persisted summary из `conversation_summary`;
   - через `BoundedChatHistoryProvider` читает последние `N*2` сообщений из `conversation_messages`;
   - через тот же provider делает retrieval по `conversation_vector_memory` и собирает компактный memory-context блок;
   - подмешивает `project_capsules` как отдельный long-term context блок;
   - формирует `AgentContext` и вызывает `AgentRuntime.SendAsync`.
3. `AgentRuntime`:
   - создает execution plan;
   - собирает MAF agent;
   - подключает tools для капсул (`project_capsules_list`, `project_capsule_get`, `propose_project_capsule_upsert`);
   - подключает `CompactionProvider` с `PipelineCompactionStrategy`:
     - `ToolResultCompactionStrategy`
     - `SummarizationCompactionStrategy`
     - `SlidingWindowCompactionStrategy`
     - `TruncationCompactionStrategy`
4. Если `SummarizationCompactionStrategy` срабатывает:
   - summarizer делает отдельный LLM вызов через `CompactionSummarizationChatClient`;
   - в `CompactionRunDiagnostics` фиксируется факт summarize-step.
5. Финальный assistant response возвращается из runtime.
6. Если summarize-step был, runtime добавляет в начало ответа явный маркер:
   - `[context-summary] ...`
7. `DialogueService` сохраняет в SQLite только 2 сообщения:
   - `User` (входной текст)
   - `Assistant` (очищенный текст без технического префикса `[context-summary]`)
8. Параллельно `DialogueService` пишет append-only `raw_events`:
   - `dialogue.user_message`
   - `dialogue.assistant_message`
   - `dialogue.context_reset` (на `/resetContext`)
   - `dialogue.system_notification` (через `RecordSystemNotificationAsync`)
9. Если runtime вернул summary candidate, `DialogueService` обновляет `conversation_summary` полным snapshot-перезаписыванием (upsert, новая версия, `source_last_message_id`), без склейки старых и новых абзацев.
10. После сохранения `DialogueService` запускает `BoundedChatHistoryProvider.ArchiveOverflowAndTrimAsync`:
    - определяет overflow сообщения за пределами окна (`ConversationContextMaxTurns * 2`);
    - архивирует их в `conversation_vector_memory`;
    - подрезает `conversation_messages` до recent window.
11. Если включен режим `capsule_extraction_mode=auto-batched` и накоплен порог новых raw events:
    - вызывается `ProjectCapsuleService.RefreshAsync`;
    - обновляются `project_capsules` и `project_capsule_extraction_state`.
12. Если агент предложил `propose_project_capsule_upsert` и пользователь выполнил `/approve <id>`:
    - `ProjectCapsuleUpsertActionExecutor` выполняет подтвержденный upsert капсулы;
    - запись проходит через `pending_confirmations`/`confirmation_audit`, а затем в `project_capsules`.

## Что важно для SQL-корректности после compaction

После HAAG-039 SQL-модель разделена на два слоя:

- `conversation_messages` остается журналом фактических turns;
- `conversation_summary` хранит сжатую память отдельно;
- `conversation_vector_memory` хранит overflow turns как долговременную retrieval-память;
- `project_capsules` хранит derived project memory с source attribution;
- `project_capsule_extraction_state` фиксирует границу обработки raw events;
- `raw_events` хранит полный append-only event trail (включая reset/system notifications), который не подрезается `TrimConversationMessagesAsync`;
- write-обновления капсул через agent tool проходят только через confirmation-слой, а не напрямую;
- нет скрытых служебных сообщений compaction в `conversation_messages`;
- internal tool-call/result и internal summary message groups не пишутся в SQL как отдельные turns.

Единственное видимое изменение памяти:

- если summarize-step сработал, пользователь видит префикс `[context-summary]` в ответе transport-слоя, но этот префикс не сохраняется в `conversation_messages`.
- если runtime отдал summary candidate, обновляется `conversation_summary`.

Это намеренно: пользователь явно видит, что ранняя часть контекста была сжата.

## Что не сохраняется в `conversation_messages`

- системные notifications (`RecordSystemNotificationAsync`);
- неуспешные LLM-вызовы (`IsConfigured == false`);
- `reasoning_content`/`TextReasoningContent`;
- внутренние tool-call/result payloads и internal compaction groups;
- raw confirmation payload/result (они в `pending_confirmations`/`confirmation_audit`).

Примечание: system notifications и reset события теперь попадают в `raw_events`, но не в `conversation_messages`.
Примечание: `/resetContext` очищает `conversation_messages`, `conversation_summary`, `conversation_vector_memory`, `project_capsules` и `project_capsule_extraction_state` для текущего conversation scope.

## Как summary попадает в prompt

Порядок сборки контекста в runtime:

1. persisted summary как отдельное `System` сообщение;
2. project capsules (если есть) как отдельное `System` сообщение;
3. retrieved memory-context (если есть релевантный recall из vector overflow) как отдельное `System` сообщение;
4. recent turns из `conversation_messages`;
5. текущий `User` message.

Это оставляет `conversation_messages` чистым журналом, а summary работает как отдельный memory слой.

## Тестовая верификация

Покрытие после HAAG-041 включает SQL-проверки:
- `StorageTests.Conversation_messages_preserve_multiline_compaction_notice_content`
  - проверяет корректное сохранение multiline `Assistant.content` с `[context-summary]`.
- `DialogueServiceTests.Compaction_notice_is_visible_to_user_but_not_persisted_in_sql_history`
  - проверяет, что пользователь видит `[context-summary]`, но в SQL сохраняется только чистый assistant текст.
- `DialogueServiceTests.Bounded_history_archives_overflow_and_recalls_relevant_vector_memory`
  - проверяет overflow в `conversation_vector_memory` и recall в `AgentContext.RetrievedMemoryContext`.
- `StorageTests.Conversation_summary_upsert_get_and_clear_roundtrip`
  - проверяет таблицу `conversation_summary`: upsert/get/update/clear.
- `StorageTests.Conversation_vector_memory_upsert_get_count_and_clear_roundtrip`
  - проверяет upsert/get/count/clear таблицы `conversation_vector_memory`.
- `StorageTests.Project_capsules_and_extraction_state_roundtrip`
  - проверяет upsert/get/count/clear для `project_capsules` и `project_capsule_extraction_state`.
- `ProjectCapsuleServiceTests.Refresh_is_idempotent_when_no_new_raw_events_and_tracks_source_event_id`
  - проверяет идемпотентность batched extraction и трассировку `capsule -> source_event_id`.
- `ProjectCapsuleServiceTests.Auto_batched_mode_uses_raw_event_threshold`
  - проверяет порог автозапуска extraction в `auto-batched` режиме.
- `ProjectCapsuleUpsertActionExecutorTests.ExecuteAsync_creates_capsule_from_confirmation_payload`
  - проверяет write-path капсулы через confirmation executor.
- `ProjectCapsuleUpsertActionExecutorTests.ExecuteAsync_keeps_version_for_same_payload_and_increments_for_changed_payload`
  - проверяет идемпотентную версионность upsert-операций.
- `DialogueServiceTests.Project_capsules_are_injected_into_runtime_memory_context`
  - проверяет, что капсулы подмешиваются в prompt context текущего run.
- `DialogueServiceTests.Persisted_summary_candidate_is_saved_and_reused_in_next_runtime_context`
  - проверяет, что summary сохраняется после run и используется в следующем run как `AgentContext.PersistedSummary`.

Итог: память теперь четырехслойная: recent turns (`conversation_messages`), persisted summary (`conversation_summary`), overflow retrieval (`conversation_vector_memory`) и derived project capsules (`project_capsules`) поверх append-only `raw_events`.
