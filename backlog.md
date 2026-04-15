# Backlog

## Текущий фокус

Цель проекта остается учебной: в первую очередь понять Microsoft Agent Framework на реальном C#/.NET приложении, во вторую - вырастить полезного домашнего ассистента для Home Assistant.

Ближайший фокус: не просто "чат в Telegram", а агентная система с инструментами, памятью, guardrails, workflows, observability и понятной границей между диалогами, системными событиями и действиями в доме.

Release/version bump/image build делаем только когда это явно запрошено.

## Done

- `HAAG-001`: .NET solution skeleton на `net8.0`.
- `HAAG-002`: централизованные NuGet dependencies, включая `Microsoft.Agents.AI`, `Microsoft.Agents.AI.OpenAI`, `ModelContextProtocol`, Telegram и SQLite.
- `HAAG-003`: configuration слой для local dev и Home Assistant add-on options.
- `HAAG-004`: SQLite state store для Telegram offset и истории диалога.
- `HAAG-005`: первый MAF runtime spike с Moonshot/OpenAI-compatible backend и safe `status` tool.
- `HAAG-006`: Telegram dialogue MVP с allowlist, `/status`, `/resetContext` и краткосрочным контекстом.
- `HAAG-007`: минимальный Docker/Home Assistant add-on skeleton.
- `HAAG-008`: Home Assistant add-on UI options для Telegram, LLM, HA MCP и workspace/state paths.
- `HAAG-009`: CI, GHCR image build и manual version bump workflow.
- `HAAG-010`: memory strategy decision: MVP = последние N turns + rolling summary, vector storage позже.
- `HAAG-012`: transport-agnostic dialogue layer, чтобы Telegram был adapter, а не core dialogue runtime.
- `HAAG-013`: lazy Home Assistant MCP discovery/status через Streamable HTTP без выполнения tools.
- `HAAG-015`: универсальная confirmation policy для risky actions через pending SQLite confirmation, `/approve`/`/reject`, audit log и Home Assistant MCP executor.
- `HAAG-020`: read-only Home Assistant MCP tools доступны MAF agent runtime с консервативной safety policy.
- `HAAG-033`: post-approval UX возвращает sanitized/truncated result preview без записи raw result в dialogue memory.

## Ready

### HAAG-014: MAF Workflow spike для дневного анализа Frigate alerts

Цель: применить MAF Workflow на не-диалоговой задаче: собрать тревоги Frigate NVR за день, обогатить metadata snapshot/clip и сформировать краткий отчет.

Учебная ценность MAF:

- Проверить workflow как directed graph из executors и edges.
- Сравнить deterministic executors и LLM/agent executor внутри одного workflow.
- Понять streaming workflow events и как их можно показывать в Telegram/Web UI.

Домашняя ценность:

- Получить ежедневный отчет по тревогам камер.
- Отделить workflow artifacts/system notifications от обычной dialogue memory.

Минимальный graph:

1. `BuildFrigateDailyWindowExecutor`: дата, timezone, список камер.
2. `FetchFrigateEventsExecutor`: read-only запрос к Frigate API `/api/events`.
3. `FilterAndGroupEventsExecutor`: фильтр low-confidence и группировка по camera/label/zone/time bucket.
4. `EnrichEventMediaExecutor`: metadata и ссылки на snapshot/clip без тяжелого video analysis.
5. `AnalyzeDailyAlertsAgentExecutor`: агент анализирует сгруппированные события.
6. `BuildDailySecurityReportExecutor`: формирует отчет.
7. `NotifyUserExecutor`: отправляет краткое системное уведомление без записи в `conversation_messages`.

Acceptance criteria:

- Есть design note с workflow graph, executors, contracts и error handling.
- Есть минимальный `WorkflowBuilder` spike с fake Frigate client и deterministic тестом порядка executors.
- Workflow gracefully handles недоступный Frigate, пустой день и слишком большое число events.
- Workflow output сохраняется как artifact/event, а не как обычная dialogue history.
- Зафиксированы future config options: Frigate base URL, auth strategy, camera filters, timezone.

### HAAG-016: MAF middleware spike для guardrails и runtime policy

Цель: вынести cross-cutting правила агента в middleware/pipeline, а не размазывать их по Telegram handler и tools.

Учебная ценность MAF:

- Проверить agent run middleware, function calling middleware и `IChatClient` middleware.
- Понять порядок выполнения middleware и где лучше делать validation, logging, redaction и budget checks.

Домашняя ценность:

- Единое место для запрета опасных запросов, ограничения tool calls, redaction секретов и token budget.

Acceptance criteria:

- Есть middleware spike, который логирует correlation id и sanitized tool call metadata.
- Function middleware блокирует control tool без confirmation policy.
- Chat/client middleware считает примерный prompt size и оставляет 20-30% окна на ответ.
- Sensitive data не пишется в production logs/traces.
- Покрыто unit-тестами на blocking, pass-through и redaction.

### HAAG-017: OpenTelemetry/Aspire observability для agents, tools и workflows

Цель: сделать поведение агента наблюдаемым: какие tool calls были предложены, сколько занял runtime, где workflow остановился и почему.

Учебная ценность MAF:

- Подключить MAF/OpenTelemetry instrumentation.
- Посмотреть spans для agent invocation, model call, tool execution и workflow executors.
- Понять, что включать в dev traces и что запрещено включать в production.

Домашняя ценность:

- Быстрее разбирать проблемы add-on на сервере без просмотра сырых prompt/secret values.

Acceptance criteria:

- Есть options для включения observability и OTLP endpoint.
- Локально можно отправить traces/metrics в Aspire Dashboard или OTLP collector.
- По умолчанию sensitive data disabled.
- `/status` показывает observability enabled/disabled и endpoint configured/not configured без секретов.
- Есть test или smoke check, что instrumentation registration не ломает старт без collector.

### HAAG-018: Streaming replies и progress events через dialogue abstraction

Цель: перейти от одного финального ответа к streaming/progress модели, не привязанной к Telegram.

Учебная ценность MAF:

- Изучить streaming agent run.
- Разделить final assistant answer, partial tokens, tool call progress и workflow progress events.

Домашняя ценность:

- Telegram/Web UI смогут показывать "думаю", "проверяю Home Assistant", "собираю события Frigate" и не выглядеть зависшими.

Acceptance criteria:

- Добавлен transport-agnostic `DialogueEvent`/`DialogueStream` контракт.
- Telegram adapter умеет отправить progress без записи progress messages в dialogue memory.
- Финальный assistant response хранится в memory один раз.
- Есть fake streaming runtime tests.

### HAAG-019: Rolling summary memory MVP

Цель: реализовать выбранную стратегию памяти до vector storage: последние N turns + summary для старых сообщений.

Учебная ценность MAF:

- Сделать отдельный summarizer agent/tool и проверить, как агентные шаги используются для обслуживания памяти, а не только для ответа пользователю.
- Проверить prompt assembly с фиксированным system prompt, recent turns и summary.

Домашняя ценность:

- Ассистент лучше помнит долгий разговор, но не тащит всю историю в каждый запрос.

Acceptance criteria:

- SQLite хранит per-conversation summary и timestamps.
- Summary обновляется при достижении 70% budget или 10-15 turns без summary.
- `/resetContext` очищает recent turns и summary.
- Summary не включает system notifications, secrets и raw tool results.
- Prompt assembly тесты проверяют порядок: System + summary + recent turns + current user message.

## Next

### HAAG-021: Post-MVP vector memory spike

Цель: ввести vector storage и более качественную организацию памяти после rolling summary MVP.

Учебная ценность MAF:

- Сравнить RAG/retrieval как отдельный deterministic слой и как tool для агента.
- Понять, какие memory records должны попадать в relevant history.

Домашняя ценность:

- Агент сможет вспоминать релевантные домашние предпочтения, события и прошлые решения без раздувания prompt.

Acceptance criteria:

- Есть decision note по SQLite vector extension, Qdrant и pgvector для Home Assistant add-on.
- Есть минимальный `IMemoryStore` и `IEmbeddingGenerator` с тестами без реального embedding provider.
- Есть redaction policy до embeddings.
- Retrieval учитывает semantic score, recency, importance и source-type.
- `/resetContext` удаляет vector records текущего conversation scope.

### HAAG-022: File workspace tool с sandbox policy

Цель: дать агенту безопасную работу с файлами внутри `/data/workspace`.

Учебная ценность MAF:

- Реализовать набор function tools с typed inputs/outputs.
- Проверить tool error handling и result summarization.

Домашняя ценность:

- Агент сможет вести заметки, сохранять отчеты, читать локальные markdown/json файлы и готовить artifacts.

Acceptance criteria:

- Path traversal заблокирован, работа только внутри configured workspace.
- Есть read/list/write/delete с max file size и allowed extensions policy.
- Tool results больших файлов суммаризируются и не пишутся целиком в dialogue memory.
- Есть audit log для write/delete.
- Тесты покрывают sandbox boundary и лимиты.

### HAAG-023: Multi-agent composition spike

Цель: разделить обязанности между specialist agents и coordinator agent.

Учебная ценность MAF:

- Проверить agent-as-tool composition и multi-agent orchestration.
- Сравнить coordinator-agent подход с workflow подходом на одинаковом сценарии.

Домашняя ценность:

- Отдельные специалисты: `HomeStateAgent`, `SecurityAgent`, `MemoryAgent`, `FileAgent`.

Acceptance criteria:

- Есть coordinator agent, который может вызвать fake specialist agent как function tool.
- Описаны границы ответственности specialist agents.
- Есть deterministic тест, что coordinator не вызывает `SecurityAgent` для обычного вопроса о памяти.
- Нет доступа specialist agents к секретам или unrelated tools без явной регистрации.

### HAAG-024: Web UI dialogue adapter

Цель: добавить второй transport для диалога, чтобы проверить transport-agnostic архитектуру на практике.

Учебная ценность MAF:

- Развести backend dialogue events и UI rendering.
- Подготовить UI к streaming/progress events и tool approval cards.

Домашняя ценность:

- Можно общаться с агентом из браузера Home Assistant/local network, а не только Telegram.

Acceptance criteria:

- Web UI использует тот же `DialogueService`, что Telegram.
- Есть отдельная conversation identity strategy для web sessions.
- `/resetContext` или UI-кнопка reset работает через общий dialogue contract.
- System notifications не попадают в обычную history.

## Later

### HAAG-025: Event/notification store

Цель: хранить системные события отдельно от диалоговой памяти.

- Camera alerts, daily reports, workflow artifacts и proactive notifications идут в `event_notifications`, не в `conversation_messages`.
- Есть TTL, severity, source, correlation id и optional links to artifacts.
- Будущий RAG может искать по event scope, но prompt assembly явно отличает events от user-assistant turns.

### HAAG-026: Live Frigate alert pipeline

Цель: перейти от дневного отчета к near-real-time анализу тревог.

- Поддержать Frigate MQTT или API polling adapter.
- Дедуплицировать события и группировать burst alerts.
- Для первого шага использовать metadata/snapshots, heavy video analysis оставить отдельно.
- Уведомления отправлять как system notification, не как dialogue turn.

### HAAG-027: Scheduled routines workflow

Цель: добавить домашние routines как workflow: утренний статус, вечерняя проверка, отчет "дом пустой/все закрыто".

- Workflow запускается по расписанию или вручную.
- Read-only стадия отделена от write/control стадий.
- Write/control требует confirmation policy.

### HAAG-028: Prompt/version/evaluation harness

Цель: не ломать поведение агента при изменениях prompts/tools/memory.

- Версионировать system prompt и tool descriptions.
- Добавить golden scenarios: "сбросить контекст", "узнать температуру", "предложить выключить свет, но не выполнить без approval".
- Добавить fake LLM/tool runner для deterministic tests.
- Сохранять eval report как artifact.

### HAAG-029: Provider strategy: Moonshot/OpenAI/optional local model

Цель: сравнить provider behavior без переписывания application code.

- Оставить Moonshot как primary local key path.
- Добавить OpenAI API fallback как опцию.
- Исследовать local model/Ollama только для read-only/low-risk сценариев.
- Зафиксировать capability matrix: tools, streaming, approvals, context window, cost, latency.
- Учесть `HAAG-032`: разные provider capabilities для thinking/reasoning mode и tool calling.

### HAAG-030: Agent Skills spike

Цель: понять, как MAF Agent Skills могут помочь с переиспользуемыми навыками ассистента, и не смешивать skills с обычными tools/workflows без необходимости.

- Сравнить `tools`, `skills` и `workflows` на 2-3 домашних сценариях.
- Проверить approval model для script/code skills.
- Сделать fake/local skill, который читает безопасный markdown playbook из workspace.
- Зафиксировать, когда skills полезнее обычных typed C# tools.
- Не выполнять shell/scripts без отдельного approval и sandbox policy.

### HAAG-031: Add-on runtime hardening

Цель: улучшить эксплуатацию на Home Assistant сервере.

- Health/readiness endpoints или status command extensions.
- Backup/restore notes для `/data/state.sqlite` и `/data/workspace`.
- Options validation с понятными ошибками.
- Graceful shutdown для polling/workflows.
- Логи без heartbeat noise и без секретов.

### HAAG-032: Adaptive thinking/reasoning mode для LLM provider

Цель: перестать держать Moonshot/Kimi `thinking` в одном глобальном режиме и выбирать стратегию reasoning по типу agent run: tool-enabled, pure chat или deep reasoning.

Контекст:

- У Kimi K2.5 `thinking` может быть включен по умолчанию.
- При multi-step tool calling Moonshot требует, чтобы assistant tool-call history содержала provider-specific `reasoning_content`.
- Текущая цепочка `Microsoft Agent Framework -> Microsoft.Extensions.AI -> OpenAI SDK -> Moonshot` не сохраняет `reasoning_content` как first-class поле в истории сообщений.
- Поэтому workaround `thinking: { "type": "disabled" }` нужен для стабильных tool calls, но он не должен навсегда запрещать thinking для обычного диалога без tools.

Что такое `reasoning_content`:

- Это Moonshot/Kimi-specific поле в assistant response, которое появляется при включенном thinking/reasoning mode.
- Оно находится рядом с обычным `content`, но не является финальным ответом пользователю: `reasoning_content` содержит reasoning trace модели, а `content` содержит user-facing ответ.
- В streaming mode reasoning обычно приходит раньше обычного `content`; когда начинает приходить `content`, reasoning phase для этого ответа можно считать завершенной.
- Токены `reasoning_content` учитываются в общем output budget вместе с `content`, поэтому deep thinking может съесть заметную часть `max_tokens`.
- Это не часть OpenAI Chat Completions canonical typed model в .NET SDK: у `ChoiceDelta`/`ChatCompletionMessage` нет обычного strongly typed свойства `.ReasoningContent`, поэтому поле может жить только как raw/unknown provider extension.

Кто его заполняет и кто должен сохранять:

- Заполняет `reasoning_content` сама модель/Moonshot API в assistant message, если thinking включен.
- Приложение не должно придумывать или синтезировать `reasoning_content`: его можно только сохранить и вернуть обратно ровно в том виде, в котором provider прислал его в текущем assistant tool-call turn.
- При multi-step tool calling цикл выглядит так:
  1. Мы отправляем user/system messages и tools.
  2. Модель возвращает assistant message с `reasoning_content` и `tool_calls`.
  3. Runtime выполняет tool calls.
  4. Следующий request должен включить assistant message с исходными `tool_calls` и тем же `reasoning_content`, затем tool result messages.
  5. Модель продолжает reasoning с учетом tool results и может вернуть новые tool calls или финальный `content`.
- Если на шаге 4 потерять `reasoning_content`, Moonshot/Kimi считает историю неполной и может вернуть HTTP 400 вида `thinking is enabled but reasoning_content is missing in assistant tool call message`.

Почему это важно для MAF:

- MAF и `Microsoft.Extensions.AI` нормализуют provider messages в общие abstractions, что полезно для portability, но provider-specific поля могут потеряться.
- Для Moonshot thinking + tools нужен round-trip provider metadata между internal tool-call шагами одного agent run.
- Нужно исследовать, можно ли сохранить `reasoning_content` через raw response/request hooks, `AdditionalProperties`, custom `IChatClient` middleware или отдельный Moonshot adapter.
- Даже если round-trip будет реализован, reasoning trace не должен попадать в persistent dialogue memory, Telegram/Web UI messages, обычные logs или long-term vector memory. Максимум - ephemeral per-run state и sanitized diagnostics: наличие поля, размер, выбранный thinking mode.

Учебная ценность MAF:

- Понять, где лучше принимать runtime-level решения: в `AgentRuntime`, provider adapter, `IChatClient` middleware или agent run options.
- Сравнить tool-enabled agent run и pure LLM run как разные execution profiles.
- Проверить, как provider-specific capabilities вписываются в transport-agnostic dialogue layer.
- Разобрать, как MAF хранит tool-call history и можно ли корректно прокинуть reasoning metadata между шагами.

Домашняя ценность:

- Стабильное управление домом через tools без HTTP 400 на Moonshot.
- Возможность использовать более глубокие reasoning ответы для обычных вопросов, где tools не нужны.
- Явное поведение в `/status` и логах: почему thinking включен или отключен в конкретном run.

Предлагаемый дизайн:

1. Ввести `LlmProviderCapabilities` или аналогичный provider profile:
   - supports tools;
   - supports streaming;
   - supports reasoning/thinking;
   - requires reasoning content round-trip for tool call history;
   - supports raw provider extensions.
2. Добавить настройки:
   - `llm_thinking_mode`: `auto | disabled | enabled`;
   - default для Moonshot: `auto`;
   - Home Assistant add-on UI option для этой настройки.
3. В `auto` режиме выбирать execution profile:
   - tool-enabled run: disable thinking для Moonshot, пока нет корректного `reasoning_content` round-trip;
   - pure chat/no tools: allow thinking, если provider это поддерживает;
   - deep reasoning mode: отдельный no-tools profile, где агент явно не вызывает Home Assistant/file/control tools.
4. Добавить явный deep reasoning entrypoint:
   - возможно команда/префикс `/think` или transport-agnostic `DialogueMode.DeepReasoning`;
   - deep reasoning не должен выполнять control actions и не должен иметь risky tools;
   - если в deep reasoning пользователь просит состояние дома, агент должен объяснить, что для этого нужен tool-enabled режим.
5. Логировать без секретов:
   - selected provider;
   - selected execution profile;
   - thinking mode requested/effective;
   - reason why thinking was disabled.
6. Отразить effective mode в `/status` или diagnostic tool, не раскрывая prompt и raw provider payload.
7. Для thinking-enabled tool runs исследовать полноценный `reasoning_content` round-trip:
   - capture `reasoning_content` из raw assistant response;
   - связать его с конкретным assistant tool-call message;
   - replay в следующем provider request только внутри текущего agent run;
   - не сохранять reasoning trace в SQLite dialogue memory;
   - добавить fallback на `thinking: disabled`, если provider adapter не может гарантировать round-trip.

Acceptance criteria:

- `LlmOptions` содержит настройку thinking mode и она настраивается через Home Assistant add-on UI.
- Для Moonshot `auto + tools` добавляет `thinking: {"type":"disabled"}` и не ломает tool calling.
- Для Moonshot `auto + pure chat` не добавляет forced disabled thinking.
- Есть technical note или кодовый spike, который показывает, можно ли round-trip-ить `reasoning_content` через текущий MAF/OpenAI SDK stack.
- Есть тесты на выбор execution profile: tool-enabled, pure chat, deep reasoning.
- Есть тесты, что Telegram/Web UI не знают о provider-specific thinking policy.
- Provider error fallback остается: HTTP 400/429 не роняет Telegram polling и не пишет failed turn в dialogue memory.
- В логах виден effective thinking mode, но нет API keys, tokens, prompts и raw tool payloads.
- В `agent_memory_analysis.md` или отдельной decision note описано, как reasoning output отличается от dialogue memory и почему reasoning trace не хранится как обычный контекст.
