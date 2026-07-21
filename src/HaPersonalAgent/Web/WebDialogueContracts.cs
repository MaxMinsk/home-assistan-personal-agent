namespace HaPersonalAgent.Web;

/// <summary>
/// Что: тело запроса одного хода диалога через веб-API (turn/stream).
/// Зачем: SPA и другие HTTP-клиенты должны передавать conversation-идентичность и текст, не зная про внутренние типы DialogueService.
/// Как: conversationId стабилен на клиента/сессию; participantId опционален (по умолчанию = conversationId); profile выбирает режим (tool/deep/chat).
/// </summary>
public sealed record DialogueTurnRequest(
    string? ConversationId,
    string? ParticipantId,
    string? Text,
    string? Profile);

/// <summary>
/// Что: ответ на ход диалога через веб-API.
/// Зачем: клиенту нужен текст ответа, correlation id для трассировки и флаг сконфигурированности runtime, без provider-specific типов.
/// Как: проекция AgentRuntimeResponse в безопасный JSON без секретов и health-деталей.
/// </summary>
public sealed record DialogueTurnResponse(
    string Text,
    string CorrelationId,
    bool IsConfigured);

/// <summary>
/// Что: тело запроса на сброс контекста веб-диалога.
/// Зачем: UI-кнопка "очистить контекст" должна работать через общий dialogue-контракт так же, как Telegram /resetContext.
/// Как: адресует диалог по conversationId (+ опциональный participantId), остальное делает DialogueService.ResetAsync.
/// </summary>
public sealed record DialogueResetRequest(
    string? ConversationId,
    string? ParticipantId);

/// <summary>
/// Что: результат операции сброса контекста веб-диалога.
/// Зачем: клиенту достаточно подтверждения, что сброс выполнен.
/// Как: простой флаг ok=true при успешном ResetAsync.
/// </summary>
public sealed record DialogueResetResponse(bool Ok);

/// <summary>
/// Что: одно сообщение истории диалога для восстановления ленты чата в Web UI.
/// Зачем: чтобы после перезагрузки страницы (в т.ч. после апдейта add-on) чат показывал прошлый разговор, а не выглядел «забытым».
/// Как: role = user|assistant, text и время создания; проекция AgentConversationMessage.
/// </summary>
public sealed record DialogueHistoryMessage(
    string Role,
    string Text,
    string CreatedUtc);

/// <summary>
/// Что: persisted summary диалога для вкладки "Память" в Web UI.
/// Зачем: UI показывает свёрнутую долговременную память чата (текст + версию), не раскрывая внутренние типы хранилища.
/// Как: проекция ConversationSummaryMemory; при отсутствии summary present=false, остальные поля пустые.
/// </summary>
public sealed record DialogueSummaryResponse(
    bool Present,
    string? Summary,
    int Version,
    string? UpdatedUtc,
    long SourceLastMessageId);
