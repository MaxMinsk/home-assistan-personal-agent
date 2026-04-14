# Agent Memory Analysis

Дата: 2026-04-14

Контекст проекта: Home Assistant Personal Agent - learning-first C#/.NET проект для изучения Microsoft Agent Framework, который запускается как Home Assistant add-on и общается с пользователем через Telegram.

## Решение

Для MVP используем гибридную краткосрочную память:

- Последние N turns храним и передаем в агент явно.
- Все более старые сообщения сворачиваем в rolling summary по Telegram conversation key.
- Vector storage не входит в MVP. Его добавляем следующей post-MVP задачей, когда Telegram диалог, reset и базовая SQLite-память будут проверены в Home Assistant.

Причина: такой подход дает устойчивый token budget, переживает рестарт add-on, работает локально в `/data/state.sqlite`, не требует embedding provider и не добавляет отдельный сервис в домашний сервер раньше времени.

## Требования

- Диалог должен быть привязан к Telegram `chatId` + `userId`, чтобы разные чаты не смешивали контекст.
- `/resetContext` должен очищать последние сообщения и summary только текущего chat/user.
- Контекст должен переживать рестарт add-on.
- В LLM нельзя отправлять секреты: Telegram token, LLM API key, Home Assistant token, внутренние access tokens.
- Контекст должен быть ограничен по размеру: рост истории не должен линейно увеличивать стоимость каждого запроса.
- Нужно оставлять 20-30% context window на ответ модели, а не забивать окно памятью полностью.
- Для обучения MAF реализация должна оставаться понятной: сначала простой контролируемый memory pipeline, потом vector retrieval.

## MVP Architecture

SQLite остается единственным persistent store.

Текущая таблица `conversation_messages` хранит последние и еще не свернутые сообщения. Для summary нужно добавить отдельное состояние:

- `conversation_key`: например `telegram:200:100`
- `summary`: текстовый summary старой части беседы
- `summarized_until_message_id`: последний message id, который уже учтен в summary
- `updated_utc`
- `summary_version`: версия prompt/алгоритма summary, чтобы можно было пересобрать summary при изменении формата
- `model`: модель, которой был сделан summary, только для диагностики

Сборка prompt для MVP:

1. System instructions из `AgentRuntime`.
2. Последние N turns из `conversation_messages`.
3. Conversation summary старой части диалога, если он есть.
4. Текущее сообщение пользователя.

Сборка prompt после добавления vector memory:

1. System instructions из `AgentRuntime`.
2. Последние N turns из `conversation_messages`.
3. Relevant history из RAG/search, ограниченный token budget и релевантностью.
4. Текущее сообщение пользователя.

Token budget rules:

- Оставлять 20-30% context window на ответ модели.
- Начинать summary/cleanup, когда prompt приближается к 70% лимита токенов.
- Не держать больше 10-15 шагов диалога как точную историю без summary.
- Использовать смену темы как сигнал для summary/cleanup, а не только счетчик сообщений.
- Если есть конфликт между recent turns и relevant history, приоритет у recent turns; RAG не должен вытеснять локальный диалог.

Обновление summary:

- После успешного ответа агента сохраняем user/assistant turn.
- Если количество несвернутых старых сообщений превышает 10-15 шагов, пользователь явно сменил тему или prompt приближается к 70% лимита токенов, берем сообщения старше последних N turns и просим модель обновить summary.
- После успешного summary обновляем `summarized_until_message_id`.
- Последние N turns не сворачиваем, потому что они лучше сохраняют точные формулировки и локальный контекст.

Поведение `/resetContext`:

- Удалить `conversation_messages` для текущего conversation key.
- Удалить `conversation_summary` для текущего conversation key.
- В post-MVP vector storage также удалить vector memory records с тем же conversation key.

## Dialogue Memory vs System Notifications

Диалог с моделью не должен быть привязан к Telegram. Telegram - это только transport adapter; будущий Web UI должен использовать тот же dialogue service, agent runtime и memory store.

Обычная dialogue memory хранит только user/assistant turns. Outbound system notifications, например будущая тревога на камере, не должны попадать в `conversation_messages`, потому что они не являются репликой пользователя или ответом assistant.

Для системных уведомлений нужен отдельный event/notification scope:

- `camera_event`
- `home_assistant_event`
- `automation_notification`

Такой event scope позже можно использовать в RAG/retrieval, если пользователь спросит "что было по камере?", но он не должен автоматически засорять prompt history текущего разговора.

## Варианты

### Sliding Window Only

Суть: хранить и отправлять только последние N turns.

Плюсы:

- Минимальная сложность.
- Прозрачное поведение.
- Легко тестировать.
- Уже почти реализовано текущим Telegram MVP.

Минусы:

- Агент быстро забывает старые детали.
- Нельзя удерживать предпочтения пользователя и долгие планы.
- При уменьшении N поведение становится хрупким.

Вывод: хорошая база, но недостаточно для нормального ассистента.

### Rolling Summary

Суть: старые сообщения сворачиваются в краткое summary, последние N turns остаются как точный контекст.

Плюсы:

- Стабильный token budget.
- Не требует embeddings и отдельного vector store.
- Хорошо подходит для MVP после Telegram диалога.
- `/resetContext` остается простым и надежным.

Минусы:

- Summary может потерять нюансы или закрепить ошибку.
- Нужен аккуратный prompt для summary.
- Нужно хранить границу `summarized_until_message_id`, иначе легко повторно суммировать одно и то же.

Вывод: выбираем для MVP.

### Vector Storage / RAG

Суть: сохранять memory records с embeddings и доставать релевантные записи по semantic similarity.

Плюсы:

- Лучше масштабируется на долгую историю.
- Может находить старые релевантные факты без отправки всего summary.
- Подходит для памяти по дому, файлам, тревогам камер и Home Assistant events.

Минусы:

- Нужен embedding provider и политика стоимости.
- Нужна схема удаления и reindex.
- Нужны threshold, reranking, deduplication и тесты качества retrieval.
- Риск: агент может получить похожий, но нерелевантный фрагмент и начать уверенно ошибаться.

Вывод: это правильный post-MVP шаг, но не первая память для Telegram MVP.

### Hybrid Memory

Суть: последние N turns + rolling summary + vector facts.

Плюсы:

- Лучший общий баланс.
- Последние turns дают точность, summary дает непрерывность, vectors дают долгую релевантную память.
- Позволяет разделить память на типы: conversation, user preference, home fact, file fact, camera event.

Минусы:

- Больше moving parts.
- Нужен memory orchestration: что извлекать, что забывать, что показывать модели.
- Сложнее тестировать и объяснять.

Вывод: целевая архитектура после MVP и vector storage spike.

### Structured Profile / Preferences

Суть: явно хранить устойчивые факты в key-value/JSON: имя пользователя, язык, предпочтения, важные устройства дома.

Плюсы:

- Прозрачно.
- Легко редактировать и сбрасывать.
- Хорошо для Home Assistant ассистента: комнаты, устройства, привычные команды.

Минусы:

- Не заменяет свободную память диалога.
- Нужен extractor фактов и confidence policy.
- Ошибочно извлеченный факт может долго вредить.

Вывод: полезно позже как отдельный слой поверх summary/vector memory.

## Post-MVP Vector Storage

Следующая задача после MVP: ввести vector storage и более качественную организацию памяти.

Минимальная архитектура:

- `IMemoryStore`: append/search/delete по conversation/user/source.
- `IEmbeddingGenerator`: отдельный порт для embeddings, чтобы не привязываться к конкретному provider.
- `MemoryRecord`: `id`, `scope`, `kind`, `text`, `source`, `conversation_key`, `created_utc`, `importance`, `expires_utc`, `embedding`, `metadata_json`.
- Retrieval pipeline: semantic score + recency + importance + source-type weighting.
- Prompt structure: system + last N turns + relevant history, with 20-30% context window reserved for response.
- Safety policy: redaction секретов до embeddings, clear/delete по `/resetContext`, запрет сохранять одноразовые токены.

Storage candidates:

- SQLite vector extension family: лучший local-first fit для Home Assistant add-on, но нужно выбрать конкретную реализацию и проверить упаковку native extension под `amd64` и `aarch64`.
- Qdrant: хороший dedicated vector DB с официальным .NET client, но это дополнительный сервис или отдельный container.
- pgvector: сильный вариант, если в домашней инфраструктуре уже есть PostgreSQL, но для add-on MVP это лишняя операционная нагрузка.
- Microsoft.Extensions.VectorData abstraction: хороший слой поверх разных vector stores в .NET, стоит рассмотреть для provider-agnostic интерфейса.

Предварительный выбор для post-MVP spike: начать с `IMemoryStore` abstraction и local-first реализации-кандидата на SQLite vector extension family. Qdrant оставить как fallback, если native SQLite extension плохо упакуется для Home Assistant multi-arch add-on.

## Quality Plan

- Сначала тестируем deterministic сценарии: имя пользователя, предпочтение, follow-up после summary.
- Отдельно тестируем `/resetContext`: исчезают messages, summary и future vector records.
- Вводим memory scopes: `conversation`, `user_profile`, `home_fact`, `file_fact`, `camera_event`.
- Для vector retrieval добавляем минимальные метрики: precision в synthetic dialogues, отсутствие retrieval после reset, отсутствие секретов в memory records.
- Для prompt assembly добавляем тесты на token budget: 20-30% окна остается на ответ, cleanup запускается около 70% лимита, recent turns не вытесняются RAG.
- Для summary добавляем versioning: при изменении summary prompt можно пересобрать summary.
- Для спорных фактов используем confidence и provenance, а не безусловное сохранение.

## Источники

- Microsoft .NET Blog: Microsoft.Extensions.VectorData поддерживает provider-agnostic vector data abstractions и набор connectors, включая Qdrant, PostgreSQL и SQLite: https://devblogs.microsoft.com/dotnet/vector-data-in-dotnet-building-blocks-for-ai-part-2/
- SQLite Vec1: SQLite vector extension для ANN vector search через virtual table interface, portable C, без внешних зависимостей: https://sqlite.org/vec1
- Qdrant docs: Qdrant поддерживает официальный .NET client `Qdrant.Client`: https://qdrant.tech/documentation/interfaces/
- pgvector README: pgvector добавляет vector similarity search в PostgreSQL и поддерживает exact/approximate nearest neighbor search: https://github.com/pgvector/pgvector
