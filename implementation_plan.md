# Implementation Plan: Home Assistant Personal Agent

## Цель

Главная цель проекта: изучить Microsoft Agent Framework на практическом C#/.NET проекте, пройти через реальные задачи orchestration, tool calling, policy/confirmation layer, memory/state, observability, MCP integration и контейнерный deployment.

Вторая цель: сделать личного ассистента для Home Assistant, который потом можно будет использовать на домашнем сервере как Docker-based Home Assistant add-on/app. Ассистент должен вести диалог через Telegram bot, работать с файлами, вызывать инструменты Home Assistant через MCP и помогать разбирать тревоги с камер видеонаблюдения.

Ориентир по UX и архитектуре: OpenClaw-подобная модель, где есть постоянный agent runtime, изолированные инструменты, файловое рабочее пространство, наблюдаемые действия и явные подтверждения для рискованных операций. Это не клон OpenClaw, а адаптация идеи под Home Assistant.

## Базовые решения

- Runtime: C# на .NET 10 LTS.
- Application model: .NET Generic Host, `BackgroundService`, dependency injection, typed options, `HttpClientFactory`, structured logging.
- Agent layer: Microsoft Agent Framework для .NET.
- Приоритет проектных решений: сначала учебная ценность для Microsoft Agent Framework, затем production-практичность домашнего ассистента. Если есть выбор между быстрым shortcut и вариантом, который лучше раскрывает MAF concepts, выбирать второй, пока это не ломает будущий запуск на Home Assistant сервере.
- Agent Framework NuGet: базовый пакет `Microsoft.Agents.AI`, provider package `Microsoft.Agents.AI.OpenAI` для OpenAI-compatible endpoints, включая Moonshot/Kimi и OpenAI API. Azure OpenAI оставить как optional backend, если он понадобится позже.
- MCP client: официальный C# SDK `ModelContextProtocol`; transport к Home Assistant изолировать отдельным adapter, потому что HA MCP endpoint работает по HTTP `/api/mcp` с bearer token.
- LLM provider: настраиваемый через переменные окружения и Home Assistant options. На старте использовать Moonshot/Kimi через OpenAI-compatible endpoint; OpenAI API оставить как второй backend при наличии отдельного OpenAI API key.
- Канал общения: Telegram Bot API через C# client library `Telegram.Bot` или тонкий typed `HttpClient` wrapper.
- Home Assistant integration: MCP server integration как основной путь для чтения/управления сущностями, плюс Home Assistant Supervisor/Core API для add-on окружения и health checks.
- Файлы: отдельная рабочая директория внутри контейнера, примонтированная в `/data/workspace`.
- Камеры: adapter-based слой. MVP работает с Home Assistant events/entities и снимками, следующий шаг добавляет Frigate MQTT/events, если Frigate есть в инсталляции.
- Persistence: SQLite через `Microsoft.Data.Sqlite` и небольшой repository layer.
- Deployment: Home Assistant add-on/app package с Dockerfile, `config.yaml`, entrypoint и persisted `/data`.

## Важные внешние факты

- Microsoft Agent Framework опубликован как open-source проект с поддержкой .NET/C#.
- Для .NET базовый пакет Agent Framework называется `Microsoft.Agents.AI`; для OpenAI и Azure OpenAI в quickstart используется `Microsoft.Agents.AI.OpenAI`.
- На момент проверки NuGet пакеты `Microsoft.Agents.AI` и `Microsoft.Agents.AI.OpenAI` доступны как stable `1.1.0`; версии все равно нужно фиксировать в `Directory.Packages.props`, а обновления делать осознанно.
- .NET 10 является текущим LTS релизом с поддержкой до 14 ноября 2028 года. Это хороший default для нового C# проекта в 2026 году.
- Официальный C# SDK для Model Context Protocol публикуется как `ModelContextProtocol`; на момент проверки NuGet доступна stable версия `1.2.0`.
- Moonshot/Kimi API поддерживает OpenAI-compatible chat completions endpoint `https://api.moonshot.ai/v1/chat/completions`, bearer token auth через `MOONSHOT_API_KEY`, tool use/function calling и multimodal content для изображений/видео.
- ChatGPT subscription и OpenAI API billing являются отдельными продуктами. Подписка ChatGPT Plus/Pro сама по себе не дает backend API для контейнера; для OpenAI API нужен отдельный API key и API billing на `platform.openai.com`.
- Home Assistant MCP Server integration предоставляет MCP endpoint на `/api/mcp` и требует long-lived access token.
- Home Assistant add-on/app контейнеры могут читать конфигурацию из `/data/options.json`, а доступ к API Home Assistant выполняется через Supervisor API и `SUPERVISOR_TOKEN`.
- Telegram Bot API поддерживает `getUpdates` для long polling и webhook mode. Для add-on в домашней сети MVP проще начать с long polling, чтобы не требовать публичного HTTPS endpoint.
- Если используется Frigate, события и alert/review данные удобнее получать через MQTT, а снапшоты и клипы брать через Frigate API или Home Assistant media/camera сущности.

## Архитектура

```text
Telegram User
    |
    v
Telegram Bot Gateway
    |
    v
Agent Runtime
    |---- Policy & Confirmation Layer
    |---- Memory / Conversation Store
    |---- File Workspace Tool
    |---- Home Assistant MCP Tool Client
    |---- Camera Alert Analyzer
    |         |---- HA event/entity adapter
    |         |---- Snapshot/video frame extractor
    |         |---- Vision model adapter
    |         |---- Frigate adapter, optional
    |
    v
Home Assistant / MCP / Files / Camera Sources
```

## Компоненты

### 1. Telegram Bot Gateway

- Long polling для MVP.
- Реализовать как `BackgroundService`, который читает updates, нормализует их в internal messages и передает в agent runtime.
- Allowlist Telegram user IDs и chat IDs.
- Команды:
  - `/start`: проверка доступа и краткий статус.
  - `/status`: состояние агента, HA connection, MCP connection, cameras adapter.
  - `/help`: доступные действия.
  - `/confirm <id>` и `/reject <id>`: подтверждение рискованных действий.
- Поддержка текста, фото и файлов.
- Rate limiting на пользователя и дедупликация update IDs.

### 2. Agent Runtime

- Microsoft Agent Framework для .NET как слой orchestration.
- Обернуть Agent Framework в сервис `IAgentRuntime`, чтобы Telegram gateway не зависел от конкретных provider packages.
- Разделить system prompt, tool registry и policy.
- Регистрировать инструменты как C# functions/services с явными input/output моделями.
- Для каждого запроса сохранять trace: user input, выбранные инструменты, действия, результат, ошибки.
- Для долгих задач возвращать промежуточные статусы в Telegram.
- Сделать единый `AgentContext`: user, chat, HA connection, workspace path, correlation id, `CancellationToken`.

### 3. Policy & Confirmation Layer

Операции разделить по уровням риска:

- Safe read: чтение состояний HA, чтение файлов в workspace, анализ снимков.
- Controlled write: создание заметок/файлов, отправка сообщений, неопасные HA сервисы.
- Risky write: открытие замков, выключение сигнализации, управление дверями/воротами, отключение камер, массовые действия.

Risky write всегда требует явного `/confirm <id>` от allowlisted пользователя. В MVP лучше вообще заблокировать high-risk домены и включать их только через конфиг.

### 4. Home Assistant MCP Client

- Подключаться к `/api/mcp` через long-lived access token.
- Для MCP использовать `ModelContextProtocol` и отдельный `IHomeAssistantMcpClient`, который скрывает transport/auth детали.
- Инкапсулировать MCP tools за внутренним интерфейсом, чтобы Telegram/Agent runtime не зависели от деталей Home Assistant API.
- Добавить health check: доступность HA, валидность токена, список доступных инструментов.
- Кэшировать список сущностей и обновлять его по TTL.
- Для write actions логировать service call, параметры и результат.

### 5. File Workspace

- Ограничить файловые операции директорией `/data/workspace`.
- Запретить path traversal и symlink escape.
- Поддержать операции:
  - list/read/write/delete внутри workspace;
  - сохранение входящих Telegram файлов;
  - экспорт отчетов по тревогам;
  - attachment response в Telegram.
- Ввести лимиты размера файлов и типов MIME.

### 6. Camera Alert Analyzer

MVP:

- Слушать/получать события от Home Assistant по выбранным entity IDs или automation-triggered Telegram command.
- По событию брать snapshot или последний кадр из camera entity.
- Отправлять кадр в vision-capable model.
- Возвращать короткое заключение: что видно, вероятность ложной тревоги, что стоит проверить, ссылки/пути к сохраненным кадрам.

Расширение:

- Frigate adapter через MQTT events/reviews.
- Поддержка видеофрагментов: извлечение key frames, анализ по нескольким кадрам, итоговый summary.
- Deduplication incident IDs, чтобы не спамить одинаковыми тревогами.
- Правила маршрутизации: какие камеры анализировать автоматически, какие только по запросу.

### 7. Persistence

- `/data/options.json`: конфиг add-on.
- `/data/state.sqlite`: состояние агента, Telegram offset, pending confirmations, incident records.
- `/data/workspace`: файлы пользователя и отчеты.
- `/data/logs`: структурированные JSON logs, если не хватает stdout.
- В коде использовать `Microsoft.Data.Sqlite` и явные migrations/init scripts, без тяжелого ORM на MVP.

## Структура проекта

```text
home-assistant-personal-agent/
  implementation_plan.md
  README.md
  HomeAssistantPersonalAgent.sln
  Directory.Build.props
  addon/
    config.yaml
    Dockerfile
    run.sh
  src/
    HaPersonalAgent/
      HaPersonalAgent.csproj
      Program.cs
      appsettings.json
      Configuration/
        AgentOptions.cs
        CameraAnalysisOptions.cs
        HomeAssistantOptions.cs
        TelegramOptions.cs
      Telegram/
        TelegramBotGateway.cs
        TelegramUpdateRouter.cs
      Agent/
        AgentRuntime.cs
        AgentContext.cs
        AgentToolRegistry.cs
      Policy/
        PolicyEngine.cs
        ConfirmationService.cs
      Tools/
        HomeAssistantMcpTool.cs
        FileWorkspaceTool.cs
        CameraAlertTool.cs
      Adapters/
        HomeAssistantMcpClient.cs
        HomeAssistantCoreClient.cs
        FrigateMqttClient.cs
        VisionModelClient.cs
      Storage/
        SqliteConnectionFactory.cs
        AgentStateRepository.cs
        IncidentRepository.cs
      Observability/
        LoggingSetup.cs
  tests/
    HaPersonalAgent.Tests/
      HaPersonalAgent.Tests.csproj
      PolicyEngineTests.cs
      FileWorkspaceToolTests.cs
      ConfigurationTests.cs
```

## Конфиг add-on

Минимальные options:

```yaml
telegram_bot_token: ""
allowed_telegram_user_ids: ""
ha_url: "http://supervisor/core"
ha_long_lived_access_token: ""
mcp_endpoint: "/api/mcp"
llm_provider: "moonshot"
llm_base_url: "https://api.moonshot.ai/v1"
llm_model: "kimi-k2.5"
llm_api_key: ""
workspace_max_mb: 512
camera_analysis:
  enabled: true
  mode: "manual"
  camera_entity_ids: []
  frigate:
    enabled: false
    mqtt_host: ""
    mqtt_username: ""
    mqtt_password: ""
high_risk_actions:
  enabled: false
```

На production лучше хранить секреты через Home Assistant add-on secrets, а не в git.

## Фазы реализации

### Phase 0: Skeleton

- Создать `.sln`, `src/HaPersonalAgent/HaPersonalAgent.csproj` и `tests/HaPersonalAgent.Tests/HaPersonalAgent.Tests.csproj`.
- Target framework: `net10.0`; если Home Assistant add-on base image или Agent Framework package потребует более консервативную цель, fallback на `net8.0` без изменения архитектуры.
- Добавить NuGet packages:
  - `Microsoft.Agents.AI`;
  - `Microsoft.Agents.AI.OpenAI`;
  - `ModelContextProtocol`;
  - `Telegram.Bot`;
  - `Microsoft.Data.Sqlite`;
  - `MQTTnet`;
  - `Microsoft.Extensions.Hosting`;
  - `Microsoft.Extensions.Options.ConfigurationExtensions`;
  - `Microsoft.Extensions.Http`.
- Зафиксировать версии NuGet пакетов в `Directory.Packages.props`.
- Ввести `LlmOptions`: `Provider`, `BaseUrl`, `Model`, `ApiKey`; для Moonshot default `Provider=moonshot`, `BaseUrl=https://api.moonshot.ai/v1`, `Model=kimi-k2.5`.
- Добавить Dockerfile и add-on `config.yaml`.
- Реализовать чтение `/data/options.json`, `appsettings.json` для local dev и env overrides.
- Добавить structured logging, `/status` command и basic health service.
- Включить `dotnet format`, nullable reference types и analyzers.

Definition of done:

- Контейнер стартует локально.
- `dotnet test` проходит.
- `/status` возвращает версию, uptime и состояние конфигурации без раскрытия секретов.

### Phase 1: Telegram + базовый agent loop

- Реализовать long polling Telegram bot как `BackgroundService`.
- Добавить allowlist user IDs.
- Подключить Microsoft Agent Framework для .NET с одним простым tool: echo/status.
- Сохранять Telegram offset в SQLite.

Definition of done:

- Бот отвечает только разрешенным пользователям.
- После рестарта не обрабатывает старые сообщения повторно.

### Phase 2: File Workspace Tool

- Реализовать безопасные file tools внутри `/data/workspace` на `System.IO`.
- Проверять путь через `Path.GetFullPath`, запретить выход из workspace и symlink escape.
- Поддержать загрузку Telegram файлов в workspace.
- Добавить тесты на path traversal, лимиты размера и MIME allowlist.

Definition of done:

- Агент может создать, прочитать и отправить файл из workspace.
- Попытки выйти за пределы workspace блокируются.

### Phase 3: Home Assistant MCP

- Подключить C# MCP client к `/api/mcp`.
- Сделать tool registry для HA read actions.
- Добавить controlled write actions с confirmation layer.
- Реализовать `/confirm` и `/reject`.

Definition of done:

- Агент умеет отвечать на вопросы о состояниях выбранных сущностей.
- Любое write action создает pending confirmation и не выполняется без подтверждения.

### Phase 4: Camera Alert MVP

- Добавить HA camera/entity adapter.
- Реализовать получение snapshot по событию или команде.
- Подключить vision model adapter через выбранный OpenAI-compatible provider. Для Moonshot использовать multimodal Kimi model; если качество анализа камер окажется слабым, сделать fallback на OpenAI vision model.
- Сохранять отчет и кадр в workspace.

Definition of done:

- По команде "проанализируй камеру X" агент возвращает краткий анализ и сохраняет артефакты.
- Повторные тревоги по одному incident id не создают спам.

### Phase 5: Frigate / расширенные тревоги

- Добавить Frigate MQTT adapter на `MQTTnet`.
- Реализовать key-frame extraction из клипов.
- Добавить правила автоматического анализа по камерам и зонам.
- Добавить routing: тихие уведомления, срочные уведомления, digest.

Definition of done:

- Frigate alert автоматически создает incident record.
- Агент присылает Telegram summary с кадрами и ссылкой на сохраненный отчет.

### Phase 6: Hardening

- Проверить secret handling.
- Добавить retry/backoff для Telegram, HA, LLM и MQTT.
- Добавить интеграционные тесты на mocked HA/MCP/Telegram.
- Подготовить README с установкой add-on repository.
- Добавить backup/restore notes для `/data`.

Definition of done:

- Add-on можно собрать, установить и обновить без потери состояния.
- Есть минимальная документация для токенов, прав и troubleshooting.

## Риски и решения

- MCP endpoint и конкретный набор tools зависят от версии Home Assistant. Решение: discovery tools на старте и graceful degradation.
- Microsoft Agent Framework и C# MCP SDK могут менять API между minor версиями. Решение: pin versions, отдельные adapters вокруг Agent Framework/MCP и короткий upgrade checklist.
- Видеоанализ может быть дорогим и медленным. Решение: анализировать snapshots/key frames, а не весь видеопоток.
- Telegram bot token и HA token дают высокий уровень доступа. Решение: allowlist, scoped HA token, secret masking, no token logs.
- Agent hallucination опасна при управлении домом. Решение: tool schema validation, allowlist сервисов, confirmations, audit log.
- Docker add-on окружение отличается от обычного локального запуска. Решение: отдельный local dev mode с env/config file и отдельный add-on mode.

## Открытые решения

- Первый LLM backend: Moonshot/Kimi через существующий API key. OpenAI API оставить опциональным fallback, если появится отдельный OpenAI API key. ChatGPT-подписку не считать API backend.
- Нужен ли автоматический анализ тревог в MVP или только ручной анализ по Telegram-команде.
- Есть ли в целевой Home Assistant инсталляции Frigate, MQTT broker и доступ к camera snapshots.
- Какие HA домены разрешить на запись: lights/switches/scripts/scenes или только read-only на первом этапе.

## Источники

- Microsoft Agent Framework: https://github.com/microsoft/agent-framework
- Microsoft Agent Framework docs: https://learn.microsoft.com/en-us/agent-framework/
- Agent Framework .NET package docs: https://learn.microsoft.com/en-us/dotnet/api/microsoft.agents.ai
- Model Context Protocol C# SDK: https://github.com/modelcontextprotocol/csharp-sdk
- .NET support policy: https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core
- Kimi K2.5 API docs: https://platform.kimi.ai/docs/guide/kimi-k2-5-quickstart
- Kimi model list: https://platform.kimi.ai/docs/models
- OpenAI ChatGPT vs API billing: https://help.openai.com/en/articles/9039756-billing-settings-in-chatgpt-vs-platform
- Home Assistant MCP Server integration: https://www.home-assistant.io/integrations/mcp_server/
- Home Assistant add-on/app documentation: https://developers.home-assistant.io/docs/add-ons/
- Home Assistant REST API: https://developers.home-assistant.io/docs/api/rest/
- Telegram Bot API: https://core.telegram.org/bots/api
- Frigate MQTT documentation: https://docs.frigate.video/integrations/mqtt/
- OpenClaw reference: https://github.com/openclaw/openclaw
