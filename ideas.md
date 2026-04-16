# Ideas

Набор идей для следующих итераций: одновременно повышают полезность домашнего ассистента и прокачивают понимание MAF на практических сценариях.

## 1) Улучшение ассистента + обучение MAF

| Идея | Польза для ассистента | Что изучаем в MAF |
|---|---|---|
| Ежедневный `Home Briefing` workflow (погода, камеры, todo, события HA) | Утренний сводный отчет в Telegram/Web UI | Workflows, executors, conditional edges, scheduled runs |
| `Incident workflow` для тревог Frigate (тревога -> контекст дома -> рекомендация) | Быстрая реакция на инциденты и меньше ложных действий | Workflow orchestration + tool steps + HITL |
| User-scoped memory + cross-transport identity (HA UI <-> Telegram) | Один общий контекст пользователя во всех каналах | Memory scopes, session/state strategy, isolation |
| Adaptive persona per user (управляемая эволюция стиля) | Ответы подстраиваются под владельца, но не ломают safety | Additional AI context + memory extraction + guardrails |
| Capsule artifacts (MD/PDF + вложения) | Отчеты по проектам/дому в виде файлов | Agent tools + file pipeline + post-processing |

## 2) Архитектурные спайки (MAF-first)

1. Unified LLM routing layer (`model + thinking`) с режимами `shadow/enforced`.
2. AI-context hygiene filters: не сохранять retrieval/context сообщения в обычную историю.
3. Checkpoint/recovery в function loop и workflow run (устойчивость к рестартам).
4. Agent-as-tool composition: coordinator + specialist agents с явными границами.
5. OpenTelemetry end-to-end: user turn -> tool call -> provider request.

## 3) Память и персональные знания

1. Fact extraction pipeline из `raw_events` в структурированные long-term факты.
2. Temporal memory: факты с валидностью/сроком действия (например, временные настройки дома).
3. Contradiction detector: если новые факты конфликтуют со старыми, спрашивать подтверждение.
4. Memory quality metrics: recall hit-rate, stale fact ratio, correction rate.
5. Ручные команды контроля памяти: `/showMemoryFact`, `/forgetFact`, `/pinFact`.

## 4) UX идеи

1. Full long-response chunking в Telegram без обрезания.
2. Единый progress протокол (`thinking`, `tool running`, `tool done`) для Telegram/Web UI.
3. Smart confirmation cards: краткий diff "что изменится" до approve.
4. Команда `/explainDecision` для разъяснения, почему агент выбрал конкретный tool/маршрут.
5. Личный dashboard статуса памяти и качества ответов.

## 5) Надежность и безопасность

1. Ролевые policy профили на risky actions (`read-only`, `home-ops`, `admin`).
2. TTL/retention policies для разных слоев памяти.
3. Chaos/failure injection тесты (429/401/timeouts/tool errors) с проверкой fallback UX.
4. Audit trail с удобным просмотром "кто/когда/что подтвердил".
5. Safe-by-default режим: при сомнении — confirmation вместо действия.

## Top 5 кандидатов на ближайшие задачи

1. User-scoped memory + identity mapping.
2. Adaptive persona per user.
3. Unified LLM routing layer (shadow -> enforced).
4. Telegram long-response chunking.
5. Capsule artifacts (MD/PDF + отправка вложением).
