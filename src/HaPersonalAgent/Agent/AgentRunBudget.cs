namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: бюджет вызовов инструментов в рамках одного run и счётчик фактически потраченного.
/// Зачем: фоновый агент с веб-поиском может уйти в длинный дорогой цикл, а рядом никого нет, чтобы его остановить;
/// плюс до сих пор счётчик tool-call'ов в журнале запусков был нулём, то есть просто врал.
/// Как: потокобезопасный счётчик; TryConsume разрешает вызов, пока лимит не исчерпан, и после исчерпания честно возвращает false.
/// </summary>
public sealed class AgentRunBudget
{
    private int _usedToolCalls;

    public AgentRunBudget(int maxToolCalls)
    {
        MaxToolCalls = Math.Max(1, maxToolCalls);
    }

    public int MaxToolCalls { get; }

    public int UsedToolCalls => Volatile.Read(ref _usedToolCalls);

    public bool IsExhausted => UsedToolCalls >= MaxToolCalls;

    /// <summary>Резервирует один вызов инструмента. Возвращает false, если бюджет уже исчерпан.</summary>
    public bool TryConsumeToolCall()
    {
        // Increment всегда, чтобы попытки сверх лимита были видны в логах как переполнение,
        // но разрешаем только те, что уложились в бюджет.
        var used = Interlocked.Increment(ref _usedToolCalls);
        return used <= MaxToolCalls;
    }
}
