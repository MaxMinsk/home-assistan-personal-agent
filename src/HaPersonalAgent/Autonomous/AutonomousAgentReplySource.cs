namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: канал, из которого пришёл ответ пользователя автономному агенту.
/// Зачем: ответ можно дать и реплаем в Telegram, и полем в Web UI — источник нужен для диагностики и отображения.
/// Как: Telegram — reply на сообщение со сводкой; Web — форма ответа в панели.
/// </summary>
public enum AutonomousAgentReplySource
{
    Telegram,
    Web,

    /// <summary>Проактивный контекст, который основной conversation-агент подметил в чате и переслал агенту (HPA-043).</summary>
    Conversation,
}
