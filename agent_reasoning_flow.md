# Agent Reasoning Flow

## Контекст

Этот документ фиксирует текущий runtime flow для диалога с агентом в проекте **Home Assistant Personal Agent** (MAF + Home Assistant MCP + Telegram).

Цель: явно показать, как сообщение проходит путь от Telegram до LLM и обратно, где включаются tools, где применяется confirmation policy, как работает `reasoning_content`, и почему после `v0.2.4` больше не должен повторяться `HTTP 400 reasoning_content is missing`.

## Ключевые компоненты

- `TelegramBotGateway` / `TelegramUpdateHandler`: транспортный вход, команды `/status`, `/resetContext`, `/think`, `/approve`, `/reject`.
- `DialogueService`: transport-agnostic orchestration (history load -> runtime call -> history append).
- `AgentRuntime`: сборка execution plan, инициализация MAF agent, запуск `RunAsync`.
- `LlmExecutionPlanner`: выбор effective thinking режима на run.
- `LlmChatCompletionRequestPolicy`: request-level patch JSON перед `/chat/completions`.
- `ReasoningContentReplayChatClient`: per-run capture+replay reasoning metadata между tool-шагами.
- `HomeAssistantMcpAgentToolProvider`: загрузка MCP tools, разделение read-only vs requires-confirmation.
- `ConfirmationService`: lifecycle pending actions (`propose -> approve/reject -> execute`).

## Основной поток (обычное сообщение в Telegram)

1. Пользователь отправляет текст в Telegram.
2. `TelegramUpdateHandler`:
   - проверяет allowlist;
   - определяет execution profile:
     - обычный текст -> `ToolEnabled`;
     - `/think ...` -> `DeepReasoning`;
   - вызывает `DialogueService.SendUserMessageAsync(...)`.
3. `DialogueService`:
   - строит `conversationKey`;
   - загружает последние `N` turns из SQLite;
   - вызывает `AgentRuntime.SendAsync(...)`.
4. `AgentRuntime`:
   - валидирует LLM config (api key, base url, model, thinking mode);
   - строит `LlmExecutionPlan`;
   - загружает MCP tools (если profile допускает tools);
   - создает `ChatClientAgent` (MAF) и запускает `RunAsync`.
5. MAF/LLM формирует ответ (и при необходимости tool calls).
6. `AgentRuntime` возвращает текстовый ответ в `DialogueService`.
7. `DialogueService` сохраняет в память только:
   - user message;
   - assistant final text.
8. `TelegramUpdateHandler` отправляет ответ пользователю.

## Что попадает в память, а что нет

В SQLite `conversation_messages` сохраняются только пользовательские и assistant текстовые turns.

Не сохраняются:

- системные уведомления (`DialogueService.RecordSystemNotificationAsync`);
- неуспешные запросы к провайдеру LLM (`response.IsConfigured == false`);
- pending confirmation payloads (они идут в отдельные таблицы confirmation);
- reasoning traces (`reasoning_content`) как часть постоянной диалоговой памяти.

## MCP tools и confirmation policy

### Read-only path

Если MCP доступен, `HomeAssistantMcpAgentToolProvider`:

- открывает MCP session;
- классифицирует tools policy-классом;
- в agent напрямую передает только read-only tools.

### Control path

Для risky tools агент НЕ вызывает tool напрямую. Вместо этого он вызывает `propose_home_assistant_mcp_action`.

Дальше:

1. `ConfirmationService.ProposeAsync` создает pending action в SQLite.
2. Пользователь получает `/approve <id>` или `/reject <id>`.
3. На `/approve`:
   - статус действия атомарно переводится в `Executing`;
   - вызывается executor по `ActionKind` (для HA: `HomeAssistantMcpActionExecutor`);
   - результат форматируется и возвращается пользователю.

## Thinking / reasoning flow

### 1) Планирование режима

`LlmExecutionPlanner` на каждый run учитывает:

- `llm_thinking_mode` (`auto|disabled|enabled`);
- execution profile (`ToolEnabled|PureChat|DeepReasoning`);
- provider capabilities (включая round-trip требования для tool calls).

Итог: `LlmExecutionPlan` с `EffectiveThinkingMode` и причиной (для логов).

### 2) Request patch policy

`LlmChatCompletionRequestPolicy` может изменить исходный JSON request перед отправкой:

- выставить `thinking.type=disabled` или `enabled` (если поддерживается);
- применить safety fallback для `auto` + tools, когда в history есть assistant `tool_calls` без `reasoning_content`.

### 3) Per-run reasoning replay

`ReasoningContentReplayChatClient` в рамках одного run:

- после ответа модели пытается захватить `TextReasoningContent` из assistant tool-call сообщения;
- перед следующим tool-step пробует подставить его обратно в соответствующее assistant history message.

Важно: это **ephemeral** state run-а, не persistence.

## Почему был `HTTP 400` и как исправлено в `v0.2.4`

Проблема: Moonshot/Kimi в tool-chain может требовать `reasoning_content` в assistant tool-call history. Если поле пропадает, провайдер возвращает:

`thinking is enabled but reasoning_content is missing in assistant tool call message`.

Исправление:

1. Включено применение request policy и в `auto + ToolEnabled` runs (раньше policy могла не включаться).
2. Добавлен fallback:
   - если есть assistant `tool_calls` без `reasoning_content`, текущий request отправляется с `thinking.type=disabled`.
3. Добавлены регрессионные тесты:
   - кейс без `reasoning_content` -> patch обязателен;
   - кейс с `reasoning_content` -> patch не нужен.

Итог: вместо падения всего диалога запрос должен завершаться стабильно.

## Ошибки и поведение пользователя

- `401` от HA MCP: MCP неавторизован (проверка токена/endpoint), но runtime остается жив.
- `429` от LLM: перегрузка провайдера, runtime возвращает fallback-ответ и не пишет turn в память.
- `400 reasoning_content missing`: после `v0.2.4` должен быть смягчен safety fallback-ом.

## Быстрый трассировочный чек-лист

1. Проверить стартовый лог run:
   - provider/model/profile;
   - requested/effective thinking;
   - MCP status/tool counts.
2. При ошибке LLM проверить:
   - код статуса (`400/429/...`);
   - сохранился ли turn в `conversation_messages` (не должен при провале).
3. При проблемах с контролем устройств:
   - создан ли pending action;
   - есть ли `/approve`/`/reject` команды;
   - какой финальный статус confirmation (`Completed`, `Failed`, `Expired`).

## Что улучшать дальше

- Capture/replay `reasoning_content` на более низком уровне адаптера, чтобы меньше зависеть от конкретной формы `TextReasoningContent`.
- После успешного `/approve` добавить continuation run с результатом действия (не только форматированный preview в Telegram).
- Подготовить тот же dialogue/runtime flow для будущего Web UI без привязки к Telegram.
