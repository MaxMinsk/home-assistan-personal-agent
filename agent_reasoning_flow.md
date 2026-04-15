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

### Кто что делает (по классам)

- `LlmProviderCapabilitiesResolver`:
  определяет профиль провайдера (`SupportsReasoning`, `RequiresReasoningContentRoundTripForToolCalls`, `ThinkingControlStyle`).
- `LlmExecutionPlanner`:
  по `llm_thinking_mode + profile + capabilities` вычисляет `LlmExecutionPlan`.
- `LlmExecutionPlan`:
  хранит итог (`RequestedThinkingMode`, `EffectiveThinkingMode`) и решает, нужен ли request patch (`ShouldPatchChatCompletionRequest`).
- `LlmChatCompletionRequestPolicy`:
  последний guard перед HTTP-запросом в `/chat/completions`; может проставить `thinking` или safety-fallback.
- `ReasoningContentReplayChatClient`:
  middleware над `IChatClient`, который в рамках одного run делает capture/replay `TextReasoningContent` между tool-step вызовами.
- `AgentRuntime`:
  связывает все вместе: создает plan, подключает policy, оборачивает chat client replay middleware, запускает MAF agent.

### Точный порядок вызова (ToolEnabled run)

1. `AgentRuntime.SendAsync` строит `executionPlan` через `LlmExecutionPlanner`.
2. `AgentRuntime.CreateOpenAIClientOptions` добавляет `LlmChatCompletionRequestPolicy`, если `executionPlan.ShouldPatchChatCompletionRequest == true`.
3. `AgentRuntime.CreateAgent` оборачивает chat client в `ReasoningContentReplayChatClient`, если profile с tools и провайдер требует reasoning round-trip для tool-calls.
4. MAF (`ChatClientAgent.RunAsync`) делает один или несколько вызовов `IChatClient.GetResponseAsync`.
5. На каждом таком вызове:
   - `ReasoningContentReplayChatClient` получает message history и пытается подставить сохраненный reasoning в assistant tool-call сообщения (replay);
   - перед фактической отправкой HTTP срабатывает `LlmChatCompletionRequestPolicy`:
     - forced режим: ставит `thinking.type=disabled|enabled`, если plan так требует;
     - safety режим: в `auto + tools` может поставить `thinking.type=disabled`, если видит assistant `tool_calls` без `reasoning_content`;
   - после ответа провайдера `ReasoningContentReplayChatClient` пытается извлечь `TextReasoningContent` из assistant tool-call сообщения и сохранить в in-memory cache (capture) для следующего шага.
6. После завершения run возвращается только final text; reasoning metadata в persistence не пишется.

### Что значит `/status` по reasoning

`/status` сейчас показывает:

- `ReasoningActive(tool-enabled)`:
  true, если effective mode не `disabled` (то есть `provider-default` или `enabled`).
- `ReasoningPlan(tool-enabled)`:
  requested/effective + включен ли request patch.
- `ReasoningSafetyFallback(tool-enabled)`:
  может ли этот профиль в принципе применить safety fallback (capability-level), а не факт, что fallback уже применился в конкретном запросе.
- аналогичный блок для `deep` profile.

### Как читать новые логи reasoning

- `Agent run ... thinking requested/effective/reason ...`:
  решение planner на старте run.
- `LLM request patch forced thinking ...`:
  policy принудительно проставил режим thinking.
- `LLM request patch applied safety fallback ...`:
  policy отключил thinking для конкретного tool-step запроса из-за missing `reasoning_content`.
- `Reasoning replay middleware ... request diagnostics`:
  сколько assistant tool-call сообщений пришло на вход шага и сколько из них без reasoning.
- `Reasoning replay middleware ... response diagnostics`:
  смогли ли мы захватить reasoning из ответа провайдера.
- `Agent run ... reasoning diagnostics: ...`:
  итоговый сводный лог по run (policy/replay counters + признаки `provider reasoning observed`, `safety fallback applied`, `replay needed`).

### Важный нюанс про `auto`

`llm_thinking_mode=auto` не означает «всегда видимый reasoning-трейс в каждом ответе».

Это означает:

- planner обычно оставляет `provider-default`;
- на tool-step safety policy может временно выключить thinking для стабильности;
- провайдер может сам решать, в каких ответах отдавать reasoning metadata.

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
