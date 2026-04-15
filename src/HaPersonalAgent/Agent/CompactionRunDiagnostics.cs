using System.Threading;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: per-run диагностика MAF compaction pipeline.
/// Зачем: для HAAG-034 нужно понимать, запускался ли summarization step и сигнализировать об этом пользователю в диалоге.
/// Как: обертка summarizer chat client инкрементирует счетчики, а AgentRuntime читает snapshot после run.
/// Ссылки:
/// - https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/Agents/Agent_Step18_CompactionPipeline/Program.cs
/// - https://github.com/microsoft/agent-framework/blob/main/docs/decisions/0019-python-context-compaction-strategy.md
/// </summary>
public sealed class CompactionRunDiagnostics
{
    private int _summarizationRequests;
    private int _summarizationResponses;

    public void RecordSummarizationRequest() =>
        Interlocked.Increment(ref _summarizationRequests);

    public void RecordSummarizationResponse() =>
        Interlocked.Increment(ref _summarizationResponses);

    public CompactionRunDiagnosticsSnapshot Snapshot() =>
        new(
            SummarizationRequests: Volatile.Read(ref _summarizationRequests),
            SummarizationResponses: Volatile.Read(ref _summarizationResponses));
}

/// <summary>
/// Что: immutable snapshot compaction-диагностики за run.
/// Зачем: runtime должен логировать стабильную картину и принимать решение о явном сообщении в диалоге.
/// Как: создается через <see cref="CompactionRunDiagnostics.Snapshot"/>.
/// </summary>
public sealed record CompactionRunDiagnosticsSnapshot(
    int SummarizationRequests,
    int SummarizationResponses)
{
    public bool SummarizationTriggered => SummarizationRequests > 0;
}
