using HaPersonalAgent.Configuration;
using HaPersonalAgent.HomeAssistant;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using System.ClientModel.Primitives;

namespace HaPersonalAgent.Agent;

/// <summary>
/// Что: исполнитель одного model attempt (без fallback-orchestration).
/// Зачем: разделяет "один вызов агента" и "retry/fallback сценарий", чтобы контролировать сложность и повторное использование в разных execution policy.
/// Как: через AgentMafFactory создает ChatClientAgent, запускает RunAsync/RunStreamingAsync и возвращает AgentResponse.
/// </summary>
public sealed class AgentRunner
{
    private readonly AgentMafFactory _mafFactory;

    public AgentRunner(AgentMafFactory mafFactory)
    {
        _mafFactory = mafFactory ?? throw new ArgumentNullException(nameof(mafFactory));
    }

    public async Task<AgentResponse> RunOnceAsync(
        string userMessage,
        AgentContext context,
        LlmOptions llmOptions,
        string model,
        HomeAssistantMcpAgentToolSet homeAssistantMcpTools,
        LlmExecutionPlan executionPlan,
        ReasoningRunDiagnostics reasoningDiagnostics,
        CompactionRunDiagnostics compactionDiagnostics,
        Func<AgentRuntimeReasoningUpdate, CancellationToken, Task>? onReasoningUpdate,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(llmOptions);
        ArgumentException.ThrowIfNullOrWhiteSpace(model);
        ArgumentNullException.ThrowIfNull(homeAssistantMcpTools);
        ArgumentNullException.ThrowIfNull(executionPlan);
        ArgumentNullException.ThrowIfNull(reasoningDiagnostics);
        ArgumentNullException.ThrowIfNull(compactionDiagnostics);

        var agent = _mafFactory.CreateAgent(
            llmOptions,
            model,
            homeAssistantMcpTools,
            context,
            executionPlan,
            reasoningDiagnostics,
            compactionDiagnostics);
        var runOptions = new ChatClientAgentRunOptions(new ChatOptions())
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["correlation_id"] = context.CorrelationId,
            },
        };
        var messages = AgentMessageFactory.CreateMessages(userMessage, context);

        if (onReasoningUpdate is null)
        {
            return await agent.RunAsync(
                messages,
                session: null,
                options: runOptions,
                cancellationToken);
        }

        // MAF pattern: stream updates, process intermediate deltas, then assemble AgentResponse.
        // Ref: dotnet/samples/02-agents/Agents/Agent_Step02_StructuredOutput/Program.cs (RunStreamingAsync + ToAgentResponseAsync).
        var updates = new List<AgentResponseUpdate>();
        await foreach (var update in agent.RunStreamingAsync(
                           messages,
                           session: null,
                           options: runOptions,
                           cancellationToken).WithCancellation(cancellationToken))
        {
            updates.Add(update);
            var reasoningTextDelta = ExtractReasoningTextDelta(update);
            if (!string.IsNullOrWhiteSpace(reasoningTextDelta))
            {
                await onReasoningUpdate(
                    new AgentRuntimeReasoningUpdate(context.CorrelationId, reasoningTextDelta),
                    cancellationToken);
            }
        }

        return updates.ToAgentResponse();
    }

    private static string ExtractReasoningTextDelta(AgentResponseUpdate update)
    {
        var reasoningText = string.Concat(
            update.Contents
                .OfType<TextReasoningContent>()
                .Select(content => content.Text ?? string.Empty));

        return reasoningText;
    }
}
