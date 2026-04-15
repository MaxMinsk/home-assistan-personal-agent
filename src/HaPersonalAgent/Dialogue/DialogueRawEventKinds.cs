namespace HaPersonalAgent.Dialogue;

/// <summary>
/// Что: стандартные типы raw events для transport-agnostic dialogue слоя.
/// Зачем: единые имена событий упрощают дальнейшую аналитику памяти и исключают строковые опечатки между сервисами и тестами.
/// Как: значения используются при append в `raw_events`; список можно расширять без миграции старых записей.
/// </summary>
public static class DialogueRawEventKinds
{
    public const string UserMessage = "dialogue.user_message";
    public const string AssistantMessage = "dialogue.assistant_message";
    public const string SystemNotification = "dialogue.system_notification";
    public const string ContextReset = "dialogue.context_reset";
}
