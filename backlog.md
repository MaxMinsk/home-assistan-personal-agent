# Backlog

## Текущий фокус

Первая порция задач нужна не для полноценного домашнего ассистента, а для учебного vertical slice по Microsoft Agent Framework: поднять C#/.NET skeleton, подключить Moonshot/Kimi через OpenAI-compatible provider, сделать первый tool и доставить ответ через Telegram.

Пока не берем: анализ камер, Frigate, полноценные Home Assistant write actions, сложную memory, UI, автоматический деплой на домашний сервер.

## Done

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

### HAAG-006: Telegram long polling skeleton

Цель: доставить первый agent response через Telegram, но без сложной логики диалога.

- Создать `TelegramBotGateway` как `BackgroundService`.
- Добавить allowlist `allowed_telegram_user_ids`.
- Реализовать обработку `/start` и `/status`.
- Сохранять последний обработанный update offset в SQLite.
- Игнорировать неразрешенных пользователей без раскрытия деталей.

Acceptance criteria:

- Бот отвечает только allowlisted пользователям.
- После рестарта старые updates не обрабатываются повторно.
- `/status` использует тот же status tool/сервис, что и MAF spike.

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

## Backlog Later

- Home Assistant MCP discovery и read-only tools.
- File workspace tool.
- Confirmation layer для controlled/risky actions.
- Camera alert MVP.
- Frigate MQTT adapter.
- OpenAI API fallback backend.
