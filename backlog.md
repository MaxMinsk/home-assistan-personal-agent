# Backlog

## Текущий фокус

Первая порция задач нужна не для полноценного домашнего ассистента, а для учебного vertical slice по Microsoft Agent Framework: поднять C#/.NET skeleton, подключить Moonshot/Kimi через OpenAI-compatible provider, сделать первый tool и довести контекстный диалог через Telegram.

Пока не берем: анализ камер, Frigate, полноценные Home Assistant write actions, vector memory, UI, автоматический деплой на домашний сервер.

## Done

### HAAG-010: Memory strategy spike

Цель: выбрать следующую форму памяти после Telegram MVP, чтобы агент не таскал весь контекст в каждый запрос.

Decision:

- Для MVP используем последние N turns + rolling summary для старых сообщений.
- Храним это локально в SQLite внутри `/data/state.sqlite`.
- `/resetContext` должен очищать и последние сообщения, и summary текущего Telegram chat/user.
- Vector storage не включаем в MVP; вводим отдельной post-MVP задачей.

Acceptance criteria:

- Зафиксировать decision note с выбранной стратегией для следующего этапа.
- Определить, что хранится локально в `/data`, как это сбрасывается и что нельзя отправлять в embeddings.
- Выбрать минимальное хранилище для Home Assistant add-on: SQLite-only для MVP, vector storage отдельным post-MVP spike.

Status: done. Decision captured in `agent_memory_analysis.md`: MVP memory is last N turns plus rolling summary; vector storage moves to `HAAG-011`.

### HAAG-006: Telegram dialogue MVP с краткосрочным контекстом

Цель: вести диалог с агентом из Telegram, сохраняя краткосрочный контекст беседы по chat/user и давая пользователю команду сброса.

- Создать `TelegramBotGateway` как `BackgroundService`.
- Добавить allowlist `allowed_telegram_user_ids`.
- Реализовать обработку `/start`, `/status` и `/resetContext`.
- Передавать обычные текстовые сообщения в `IAgentRuntime`.
- Хранить историю диалога по Telegram chat/user в SQLite.
- Ограничивать краткосрочный контекст последними N turns, чтобы не отправлять в модель всю историю.
- Сохранять последний обработанный update offset в SQLite.
- Игнорировать неразрешенных пользователей без раскрытия деталей.

Acceptance criteria:

- Бот отвечает только allowlisted пользователям.
- Обычное текстовое сообщение получает ответ агента через настроенный Moonshot/OpenAI-compatible provider.
- Follow-up сообщение может опираться на предыдущую реплику в том же Telegram chat.
- Контекст беседы переживает рестарт приложения.
- `/resetContext` очищает контекст только текущего Telegram chat/user, и следующий запрос не видит старую историю.
- После рестарта старые updates не обрабатываются повторно.
- `/status` использует тот же status tool/сервис, что и MAF spike.
- Логи и ответы не раскрывают Telegram token, LLM API key и Home Assistant token.

Status: done. Added Telegram long polling gateway, allowlist-based update handler, `/start`, `/status`, `/resetContext`, SQLite conversation history, bounded context window, and tests for handler/storage behavior.

### HAAG-005: Первый Microsoft Agent Framework spike

Цель: изучить минимальный рабочий путь MAF в нашем проекте.

- Создать `IAgentRuntime` и реализацию `AgentRuntime`.
- Подключить `Microsoft.Agents.AI.OpenAI` к Moonshot/Kimi через OpenAI-compatible base URL.
- Добавить первый безопасный tool: `status`.
- Сделать простой request/response метод без Telegram.
- Добавить cancellation token и correlation id в `AgentContext`.

Acceptance criteria:

- При наличии `MOONSHOT_API_KEY` локальная команда/тестовый runner получает ответ от модели.
- Без `MOONSHOT_API_KEY` приложение стартует, но MAF health помечается как not configured.
- Tool call `status` возвращает версию, uptime и режим конфигурации без секретов.

Status: done. Added `IAgentRuntime`, `AgentRuntime`, `AgentContext`, safe `status` tool, OpenAI-compatible Moonshot wiring through Microsoft Agent Framework, and a local `ask` CLI runner. External Moonshot call is implemented but not executed automatically to avoid spending API quota.

### HAAG-004: Добавить SQLite state store

Цель: заложить минимальное состояние для Telegram offset и будущих confirmations.

- Создать `SqliteConnectionFactory`.
- Создать init/migration script для `/data/state.sqlite` или local dev path.
- Создать `AgentStateRepository`.
- Поддержать чтение/запись Telegram update offset.

Acceptance criteria:

- Тесты проверяют создание схемы на пустой базе.
- Offset сохраняется и читается после пересоздания repository.
- Путь к базе задается конфигом.

Status: done. Added SQLite connection factory, idempotent schema initialization, and Telegram update offset persistence through `AgentStateRepository`.

### HAAG-009: Настроить CI, image build и version bump workflow

Цель: автоматизировать проверку .NET проекта, сборку Home Assistant app image и обновление версии add-on.

- Добавить CI workflow для restore, format, build, test и vulnerability check.
- Добавить workflow сборки Home Assistant app/add-on image через Docker BuildKit и актуальные `home-assistant/builder/actions/*`.
- Публиковать image в GHCR на push в `main`.
- На pull request собирать image без публикации.
- Добавить ручной workflow для bump версии `addon/config.yaml`, обновления changelog и создания git tag.

Acceptance criteria:

- CI workflow запускается на изменениях `.NET` проекта.
- Build workflow отслеживает изменения `addon/**`, `src/**`, solution и workflow-файлов.
- Версия add-on хранится в `addon/config.yaml`.
- Ручной bump версии создает commit и tag `vX.Y.Z`.

Status: done. Docker build is verified by GitHub Actions because Docker is not available in the local environment.

### HAAG-008: Настроить Home Assistant add-on UI options

Цель: секреты и базовые параметры должны задаваться через UI Home Assistant add-on, а env aliases использовать только как удобство для local dev/CI.

- Описать options schema в `addon/config.yaml`.
- Добавить UI-configurable поля для Telegram token, allowlisted Telegram users, Moonshot/OpenAI-compatible LLM settings, Home Assistant MCP endpoint и workspace/state paths.
- Проверить, что значения из add-on UI попадают в `/data/options.json` и корректно мапятся в `TelegramOptions`, `LlmOptions`, `HomeAssistantOptions` и `AgentOptions`.
- Для секретов использовать Home Assistant add-on secrets, где это возможно.
- Сохранить env aliases `MOONSHOT_API_KEY`, `TELEGRAM_BOT_TOKEN`, `HOME_ASSISTANT_LONG_LIVED_ACCESS_TOKEN` только как override/fallback для локального запуска и CI.

Acceptance criteria:

- Основной production path настройки работает через Home Assistant add-on UI.
- Пользователь может настроить Moonshot API key и Telegram bot token без редактирования файлов внутри контейнера.
- `/status` показывает только boolean-флаги configured/not configured и не раскрывает значения из UI.

Status: done. Add-on UI options and schema are in `addon/config.yaml`.

### HAAG-007: Минимальный Docker/add-on skeleton

Цель: проверить, что выбранный .NET подход совместим с контейнерной упаковкой для Home Assistant.

- Добавить `addon/Dockerfile`.
- Добавить `addon/config.yaml` с минимальными options.
- Добавить `addon/run.sh`.
- Собрать контейнер локально без реального Home Assistant окружения.

Acceptance criteria:

- Docker image собирается.
- Контейнер стартует с local test options.
- `/data` используется как persisted root для state/workspace paths.

Status: done with local limitation. Add-on skeleton is present, .NET self-contained publish for `linux-musl-x64` passes locally, and Docker image build is delegated to GitHub Actions because Docker is not installed in the local environment.

### HAAG-003: Реализовать конфигурацию приложения

Цель: поддержать local dev и будущий Home Assistant add-on mode.

- Добавить options records/classes: `TelegramOptions`, `LlmOptions`, `HomeAssistantOptions`, `AgentOptions`.
- Читать `appsettings.json` для local dev.
- Добавить чтение `/data/options.json`, если файл существует.
- Добавить env overrides для секретов.
- Для Moonshot default: `Provider=moonshot`, `BaseUrl=https://api.moonshot.ai/v1`, `Model=kimi-k2.5`.
- Замаскировать секреты в logs/status output.

Acceptance criteria:

- Тесты покрывают default values и env overrides.
- При отсутствующем `/data/options.json` локальный запуск не падает.
- `llm_api_key` и `telegram_bot_token` нигде не печатаются целиком.

Status: done. App reads normal .NET sections, optional Home Assistant add-on `/data/options.json`, `HA_PERSONAL_AGENT_*` env overrides and common secret aliases such as `MOONSHOT_API_KEY` and `TELEGRAM_BOT_TOKEN`.

### HAAG-002: Зафиксировать NuGet dependencies

Цель: сделать воспроизводимую основу для Agent Framework и интеграций.

- Добавить `Directory.Packages.props`.
- Зафиксировать `Microsoft.Agents.AI`.
- Зафиксировать `Microsoft.Agents.AI.OpenAI`.
- Зафиксировать `ModelContextProtocol`.
- Добавить `Telegram.Bot`, `Microsoft.Data.Sqlite` и нужные `Microsoft.Extensions.*` пакеты.
- Не добавлять Azure-specific packages на первом шаге.

Acceptance criteria:

- Все версии управляются централизованно.
- `dotnet restore` проходит.
- В проекте нет Azure OpenAI как обязательной зависимости.

Status: done. Versions are centralized in `Directory.Packages.props`; no Azure-specific package references were added.

### HAAG-001: Создать .NET solution skeleton

Цель: получить минимальный компилируемый C# проект под будущий Home Assistant add-on.

- Создать `HomeAssistantPersonalAgent.sln`.
- Создать `src/HaPersonalAgent/HaPersonalAgent.csproj`.
- Создать `tests/HaPersonalAgent.Tests/HaPersonalAgent.Tests.csproj`.
- Target framework: `net10.0`, с fallback decision note для `net8.0`, если понадобится.
- Включить nullable reference types и analyzers.
- Добавить базовый `Program.cs` на .NET Generic Host.

Acceptance criteria:

- `dotnet build` проходит.
- `dotnet test` запускается и проходит хотя бы с одним smoke test.
- Приложение стартует локально и корректно завершается по Ctrl+C.

Status: done. Skeleton uses `net8.0` because local SDKs are `8.0.303` and `9.0.100`; see `docs/decisions/0001-target-framework.md`.

## Ready

### HAAG-011: Post-MVP vector memory spike

Цель: ввести vector storage и более качественную организацию памяти после проверки Telegram MVP.

- Спроектировать `IMemoryStore` и `IEmbeddingGenerator` как порты приложения.
- Описать `MemoryRecord`: scope, kind, text, source, conversation key, metadata, importance, ttl и embedding.
- Сравнить local-first SQLite vector extension, Qdrant и pgvector для Home Assistant add-on.
- Добавить policy удаления: `/resetContext` должен удалять vector records текущего chat/user.
- Добавить redaction policy до embeddings: не embedding-овать токены и другие секреты.
- Сделать retrieval pipeline: semantic score + recency + importance + source-type weighting.

Acceptance criteria:

- Есть decision note по выбранному vector store.
- Есть минимальная реализация `IMemoryStore` с тестами без реального LLM/embedding provider.
- Есть стратегия multi-arch packaging для Home Assistant add-on, если выбран native SQLite extension.
- Reset и delete сценарии покрыты тестами.

## Backlog Later

Остальные later items:

- Home Assistant MCP discovery и read-only tools.
- File workspace tool.
- Confirmation layer для controlled/risky actions.
- Camera alert MVP.
- Frigate MQTT adapter.
- OpenAI API fallback backend.
