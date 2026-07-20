namespace HaPersonalAgent.Web;

/// <summary>
/// Что: границы доступа агента к инструментам в виде web-DTO.
/// Зачем: UI редактирует их галочками, не зная про внутренний record домена.
/// Как: плоская проекция AutonomousAgentToolScope; управление устройствами отсутствует намеренно (research-only, HPA-024).
/// </summary>
public sealed record AgentToolScopeDto(
    bool AllowHomeAssistantRead,
    bool AllowWebSearch,
    bool AllowMemoryRead,
    bool AllowMemoryWrite,
    int MaxDurableFactsPerRun);

/// <summary>
/// Что: строка списка агентов для левого ростера.
/// Зачем: списку нужен минимум — имя, состояние и срок, без миссии и настроек.
/// Как: включает вычисляемые признаки (идёт ли запуск, сколько ответов ждёт), чтобы UI не делал доп. запросов.
/// </summary>
public sealed record AgentSummaryResponse(
    string Id,
    string Name,
    string Status,
    string ScheduleKind,
    string? NextRunUtc,
    string? LastRunUtc,
    bool HasRunningRun,
    int PendingReplyCount,
    int OpenQuestionCount);

/// <summary>
/// Что: полная карточка агента для детальной панели.
/// Зачем: вкладки Обзор/Настройки/Память читают всё из одного ответа.
/// Как: определение + границы инструментов + состояние непрерывности (фокус, открытые вопросы, ключ капсулы).
/// </summary>
public sealed record AgentDetailResponse(
    string Id,
    string Name,
    string Mission,
    string Status,
    string ScheduleKind,
    string? ScheduleExpression,
    long? DeliveryTelegramChatId,
    AgentToolScopeDto ToolScope,
    string? NextRunUtc,
    string? LastRunUtc,
    string CreatedUtc,
    string UpdatedUtc,
    bool HasRunningRun,
    int PendingReplyCount,
    string? Focus,
    string? OpenQuestions,
    string? CapsuleNoteKey,
    string? CapsuleUpdatedUtc);

/// <summary>
/// Что: запись истории запусков агента.
/// Зачем: вкладка "Запуски" показывает ленту сводок и провалов.
/// Как: вопросы отдаются уже разобранным массивом, чтобы UI не парсил JSON сам.
/// </summary>
public sealed record AgentRunResponse(
    string Id,
    string Status,
    string StartedUtc,
    string? FinishedUtc,
    string? Summary,
    IReadOnlyList<string> Questions,
    string? Error,
    int ToolCallCount);

/// <summary>
/// Что: тело создания/редактирования агента.
/// Зачем: форма UI присылает один и тот же набор полей и при создании, и при правке.
/// Как: ScheduleKind — строка enum (manual/hourly/daily/weekly/cron); ScheduleExpression обязателен только для cron.
/// </summary>
public sealed record AgentUpsertRequest(
    string? Name,
    string? Mission,
    string? ScheduleKind,
    string? ScheduleExpression,
    long? DeliveryTelegramChatId,
    AgentToolScopeDto? ToolScope);

/// <summary>
/// Что: тело ответа пользователя агенту из Web UI.
/// Зачем: ответ из панели должен попадать в ту же очередь, что и reply в Telegram.
/// Как: текст кладётся в inbox и потребляется следующим плановым запуском, запуск не инициируется.
/// </summary>
public sealed record AgentReplyRequest(string? Text);

/// <summary>
/// Что: тело смены статуса агента (пауза/возобновление).
/// Зачем: отдельная операция, чтобы пауза не требовала присылать всю форму настроек.
/// Как: Status — строка enum (active/paused).
/// </summary>
public sealed record AgentStatusRequest(string? Status);
