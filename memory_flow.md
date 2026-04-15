# Memory Flow

## Контекст

Этот документ фиксирует текущий memory flow в проекте **Home Assistant Personal Agent** после внедрения HAAG-034 (MAF compaction pipeline).

Цель документа:

- показать, как сообщение проходит путь `transport -> runtime -> LLM -> persistence`;
- явно зафиксировать, что и где хранится в SQLite;
- объяснить, как compaction влияет на prompt и почему это не ломает SQL-память.

## Источники паттерна (MAF)

- `Agent_Step18_CompactionPipeline`:
  - <https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/Agents/Agent_Step18_CompactionPipeline/Program.cs>
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
   - читает последние `N*2` сообщений из `conversation_messages`;
   - формирует `AgentContext` и вызывает `AgentRuntime.SendAsync`.
3. `AgentRuntime`:
   - создает execution plan;
   - собирает MAF agent;
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
   - `Assistant` (финальный текст, включая `[context-summary]`, если был)
8. После сохранения `DialogueService` применяет `TrimConversationMessagesAsync` по лимиту (`ConversationContextMaxTurns * 2`).

## Что важно для SQL-корректности после compaction

После HAAG-034 SQL-модель не изменилась:

- нет новых таблиц под summary;
- нет скрытых служебных сообщений compaction в `conversation_messages`;
- сохраняется прежний контракт: один user turn + один assistant turn на успешный запрос;
- internal tool-call/result и internal summary message groups не пишутся в SQL как отдельные turns.

Единственное видимое изменение памяти:

- если summarize-step сработал, в `Assistant.content` появляется пользовательский префикс `[context-summary]`.

Это намеренно: пользователь явно видит, что ранняя часть контекста была сжата.

## Что не сохраняется в `conversation_messages`

- системные notifications (`RecordSystemNotificationAsync`);
- неуспешные LLM-вызовы (`IsConfigured == false`);
- `reasoning_content`/`TextReasoningContent`;
- внутренние tool-call/result payloads и internal compaction groups;
- raw confirmation payload/result (они в `pending_confirmations`/`confirmation_audit`).

## Тестовая верификация

Покрытие после HAAG-034 включает SQL-проверки:

- `DialogueServiceTests.Compaction_notice_is_persisted_as_regular_assistant_turn_and_reused_from_sql_history`
  - проверяет, что `[context-summary]` хранится как обычный `Assistant` turn;
  - проверяет, что следующий run поднимает из SQL ровно эти turns.
- `StorageTests.Conversation_messages_preserve_multiline_compaction_notice_content`
  - проверяет корректное сохранение multiline `Assistant.content` с `[context-summary]`.

Итог: compaction влияет на prompt assembly и пользовательский финальный ответ, но не ломает SQL storage contract памяти.
