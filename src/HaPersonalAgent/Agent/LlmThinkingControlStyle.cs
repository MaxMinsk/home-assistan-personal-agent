namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: known request-body schema для явного управления provider thinking режимом.
/// Зачем: разные OpenAI-compatible providers могут не поддерживать одинаковые extension поля, поэтому patch policy должна знать capability, а не имя модели.
/// Как: None не меняет request body, OpenAiCompatibleThinkingObject добавляет `thinking: { "type": "..." }`.
/// </summary>
public enum LlmThinkingControlStyle
{
    None,
    OpenAiCompatibleThinkingObject,
}
