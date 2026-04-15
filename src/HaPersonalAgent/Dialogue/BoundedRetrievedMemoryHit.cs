using HaPersonalAgent.Agent;

namespace HaPersonalAgent.Dialogue;

/// <summary>
/// Что: найденный релевантный memory hit из vector overflow для bounded history retrieval.
/// Зачем: один и тот же тип нужен и для auto-retrieval (`before_invoke`), и для on-demand tool поиска памяти.
/// Как: хранит source id исходного turn, роль сообщения, текст и similarity score для ранжирования.
/// </summary>
public sealed record BoundedRetrievedMemoryHit(
    long SourceMessageId,
    AgentConversationRole Role,
    string Text,
    float Score);
