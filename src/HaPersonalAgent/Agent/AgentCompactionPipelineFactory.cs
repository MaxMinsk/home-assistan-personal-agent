using HaPersonalAgent.Configuration;
using Microsoft.Agents.AI.Compaction;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;
using OpenAI.Chat;

#pragma warning disable MAAI001 // Microsoft.Agents.AI.Compaction is preview in current package.

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: фабрика MAF compaction pipeline для runtime-вызовов.
/// Зачем: сборка стратегий compaction/summarization не должна жить в orchestration-классе и мешать пониманию основного run flow.
/// Как: создает цепочку ToolResult -> (optional Summarization) -> SlidingWindow -> Truncation и настраивает summarization prompt.
/// </summary>
public sealed class AgentCompactionPipelineFactory
{
    private readonly LlmExecutionPlanner _executionPlanner;
    private readonly ILoggerFactory _loggerFactory;
    private readonly PersistedSummaryPromptBuilder _summaryPromptBuilder;

    public AgentCompactionPipelineFactory(
        LlmExecutionPlanner executionPlanner,
        ILoggerFactory loggerFactory,
        PersistedSummaryPromptBuilder? summaryPromptBuilder = null)
    {
        _executionPlanner = executionPlanner ?? throw new ArgumentNullException(nameof(executionPlanner));
        _loggerFactory = loggerFactory ?? throw new ArgumentNullException(nameof(loggerFactory));
        _summaryPromptBuilder = summaryPromptBuilder ?? new PersistedSummaryPromptBuilder();
    }

    /// <summary>
    /// Что: собирает compaction pipeline по MAF-паттерну для входящего контекста диалога.
    /// Зачем: HAAG-034 требует перейти от ad-hoc trimming к стандартным стратегиям MAF с atomic grouping tool-call/result.
    /// Как: применяет стратегии от мягкой к агрессивной: ToolResult - Summarization - SlidingWindow - Truncation.
    /// Ссылки:
    /// - https://github.com/microsoft/agent-framework/blob/main/dotnet/samples/02-agents/Agents/Agent_Step18_CompactionPipeline/Program.cs
    /// - https://github.com/microsoft/agent-framework/blob/main/docs/decisions/0019-python-context-compaction-strategy.md
    /// </summary>
    public CompactionStrategy CreatePipeline(
        ChatClient primaryChatClient,
        LlmOptions llmOptions,
        AgentContext context,
        CompactionRunDiagnostics compactionDiagnostics)
    {
        ArgumentNullException.ThrowIfNull(primaryChatClient);
        ArgumentNullException.ThrowIfNull(llmOptions);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(compactionDiagnostics);

        var summarizationExecutionPlan = _executionPlanner.CreatePlan(
            llmOptions,
            LlmExecutionProfile.Summarization);
        IChatClient summarizationChatClient = primaryChatClient.AsIChatClient();
        summarizationChatClient = new LlmRequestLoggingChatClient(
            summarizationChatClient,
            context.CorrelationId,
            summarizationExecutionPlan,
            _loggerFactory.CreateLogger<LlmRequestLoggingChatClient>());
        summarizationChatClient = new CompactionSummarizationChatClient(
            summarizationChatClient,
            context.CorrelationId,
            compactionDiagnostics,
            _loggerFactory.CreateLogger<CompactionSummarizationChatClient>());
        var strategies = new List<CompactionStrategy>
        {
            // Tool result compaction остается первым, чтобы не разрывать связки вызов/результат.
            new ToolResultCompactionStrategy(CompactionTriggers.MessagesExceed(28)),
        };

        // Summarization запускается, когда DialogueService запрашивает refresh rolling summary:
        // либо summary отсутствует, либо накопился новый "хвост" сообщений после последнего summary.
        if (context.ShouldRefreshPersistedSummary)
        {
            var summarizationTrigger = context.ForcePersistedSummaryRefresh
                ? CompactionTriggers.MessagesExceed(2)
                : CompactionTriggers.MessagesExceed(24);
            var summarizationTarget = context.ForcePersistedSummaryRefresh
                ? CompactionTriggers.MessagesExceed(2)
                : CompactionTriggers.MessagesExceed(24);
            var minimumPreservedGroups = context.ForcePersistedSummaryRefresh
                ? 2
                : 10;
            strategies.Add(new SummarizationCompactionStrategy(
                summarizationChatClient,
                summarizationTrigger,
                minimumPreservedGroups: minimumPreservedGroups,
                summarizationPrompt: _summaryPromptBuilder.Build(
                    context.PersistedSummary,
                    context.PersistedSummaryRefreshReason,
                    context.MessagesSincePersistedSummary),
                target: summarizationTarget));
        }

        strategies.AddRange(
        [
            new SlidingWindowCompactionStrategy(
                CompactionTriggers.TurnsExceed(16),
                minimumPreservedTurns: 8,
                target: CompactionTriggers.TurnsExceed(12)),
            new TruncationCompactionStrategy(
                CompactionTriggers.MessagesExceed(48),
                minimumPreservedGroups: 12,
                target: CompactionTriggers.MessagesExceed(34)),
        ]);

        return new PipelineCompactionStrategy(strategies);
    }
}

#pragma warning restore MAAI001
