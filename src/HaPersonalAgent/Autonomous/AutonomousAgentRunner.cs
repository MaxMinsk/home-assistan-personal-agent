using System.Text.Json;
using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: исполнитель одного фонового запуска автономного агента (research-run).
/// Зачем: это точка, где переиспользуется общий IAgentRuntime — автономный агент не форкает ядро, а лишь приносит свой контекст и политику инструментов.
/// Как: заводит запись запуска, собирает вход из миссии + непрерывности + накопленных ответов, зовёт runtime с read-only политикой,
/// разбирает структурированный вывод, фиксирует запуск, помечает ответы потреблёнными и переносит фокус в непрерывность.
/// </summary>
public sealed class AutonomousAgentRunner : IAutonomousAgentRunner
{
    /// <summary>Имя транспорта для автономных запусков — изолирует их ключи от telegram/web диалогов.</summary>
    public const string TransportName = "autonomous";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly IAutonomousAgentRepository _repository;
    private readonly IAgentRuntime _agentRuntime;
    private readonly AutonomousAgentCapsuleWriter _capsuleWriter;
    private readonly ILogger<AutonomousAgentRunner> _logger;
    private readonly IAutonomousAgentNotifier? _notifier;
    private readonly IOptions<AutonomousAgentOptions>? _options;

    public AutonomousAgentRunner(
        IAutonomousAgentRepository repository,
        IAgentRuntime agentRuntime,
        AutonomousAgentCapsuleWriter capsuleWriter,
        ILogger<AutonomousAgentRunner> logger,
        IAutonomousAgentNotifier? notifier = null,
        IOptions<AutonomousAgentOptions>? options = null)
    {
        _repository = repository;
        _agentRuntime = agentRuntime;
        _capsuleWriter = capsuleWriter;
        _logger = logger;
        _notifier = notifier;
        _options = options;
    }

    public async Task RunAsync(AutonomousAgentDefinition definition, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(definition);

        var run = AutonomousAgentRun.Start(definition.Id);
        await _repository.AppendRunAsync(run, cancellationToken);

        try
        {
            var continuity = await _repository.GetContinuityAsync(definition.Id, cancellationToken);
            var pendingReplies = await _repository.GetPendingRepliesAsync(definition.Id, cancellationToken);
            var previousSummary = await GetPreviousSummaryAsync(definition.Id, run.Id, cancellationToken);

            var input = AutonomousAgentPromptBuilder.BuildRunInput(
                definition,
                continuity,
                pendingReplies,
                previousSummary);

            _logger.LogInformation(
                "Autonomous run {CorrelationId} for agent {AgentId} starting: {PendingReplyCount} queued user replies, previous summary present {HasPreviousSummary}.",
                run.CorrelationId,
                definition.Id,
                pendingReplies.Count,
                previousSummary is not null);

            // Бюджет вызовов инструментов на этот запуск: и потолок, и честный счётчик для журнала.
            var runBudget = new AgentRunBudget(_options?.Value.MaxToolCallsPerRun ?? 20);

            var response = await _agentRuntime.SendAsync(
                input,
                BuildContext(definition, run, runBudget),
                onReasoningUpdate: null,
                cancellationToken);

            if (!response.IsConfigured)
            {
                await FailRunAsync(run, $"Agent runtime is not configured: {response.Text}");
                return;
            }

            var output = AutonomousRunOutputParser.Parse(
                response.Text,
                definition.ToolScope.MaxDurableFactsPerRun);

            var completed = run.Complete(
                summary: ComposeRunSummary(output, response.Text),
                questionsJson: output.Questions.Count > 0
                    ? JsonSerializer.Serialize(output.Questions, JsonOptions)
                    : null,
                diagnostics: JsonSerializer.Serialize(
                    new
                    {
                        model = response.Health.Model,
                        provider = response.Health.Provider,
                        consumedReplies = pendingReplies.Count,
                        durableFactCandidates = output.DurableFacts.Count,
                        toolBudget = runBudget.MaxToolCalls,
                        toolBudgetExhausted = runBudget.IsExhausted,
                    },
                    JsonOptions),
                toolCallCount: Math.Min(runBudget.UsedToolCalls, runBudget.MaxToolCalls));
            await _repository.UpdateRunAsync(completed, cancellationToken);

            // HPA-032: доставляем бриф и запоминаем id сообщения — это якорь, по которому reply
            // пользователя будет сопоставлен с этим агентом.
            if (_notifier is not null)
            {
                var deliveredMessageId = await _notifier.DeliverAsync(definition, completed, output, cancellationToken);
                if (!string.IsNullOrWhiteSpace(deliveredMessageId))
                {
                    completed = completed with { DeliveredMessageId = deliveredMessageId };
                    await _repository.UpdateRunAsync(completed, cancellationToken);
                }
            }

            // Ответы считаются потреблёнными только после успешного запуска — иначе при падении
            // пользовательские правки потерялись бы, так и не попав в контекст.
            if (pendingReplies.Count > 0)
            {
                await _repository.MarkRepliesConsumedAsync(
                    pendingReplies.Select(entry => entry.Id),
                    run.Id,
                    DateTimeOffset.UtcNow,
                    cancellationToken);
            }

            // HPA-031: в общую память уходит ровно одна идемпотентная капсула на агента
            // плюс ограниченное число durable-фактов; журнал запусков остаётся локальным.
            var capsuleNoteKey = await _capsuleWriter.PublishAsync(definition, output, cancellationToken);

            await UpdateContinuityAsync(definition.Id, continuity, output, capsuleNoteKey, cancellationToken);

            _logger.LogInformation(
                "Autonomous run {CorrelationId} for agent {AgentId} completed: summary {SummaryLength} chars, {QuestionCount} questions, {FactCount} durable-fact candidates.",
                run.CorrelationId,
                definition.Id,
                completed.Summary?.Length ?? 0,
                output.Questions.Count,
                output.DurableFacts.Count);
        }
        catch (OperationCanceledException)
        {
            // Токен уже отменён (таймаут или остановка хоста), поэтому фиксируем провал вне его области действия.
            await FailRunAsync(run, "Run was cancelled (timeout or host shutdown).");
            throw;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                exception,
                "Autonomous run {CorrelationId} for agent {AgentId} failed.",
                run.CorrelationId,
                definition.Id);
            await FailRunAsync(run, $"{exception.GetType().Name}: {exception.Message}");
        }
    }

    /// <summary>
    /// Сводка для журнала запусков и UI: суть теперь в findings, поэтому склеиваем рамку + тезисы,
    /// чтобы вкладка «Запуски» показывала то же содержание, что и бриф в Telegram.
    /// </summary>
    private static string ComposeRunSummary(AutonomousRunOutput output, string fallbackText)
    {
        var builder = new System.Text.StringBuilder();
        if (!string.IsNullOrWhiteSpace(output.Summary))
        {
            builder.AppendLine(output.Summary.Trim());
        }

        foreach (var finding in output.Findings)
        {
            builder.AppendLine($"• {finding}");
        }

        var composed = builder.ToString().Trim();
        return composed.Length > 0 ? composed : fallbackText;
    }

    private AgentContext BuildContext(
        AutonomousAgentDefinition definition,
        AutonomousAgentRun run,
        AgentRunBudget runBudget) =>
        AgentContext.Create(
            correlationId: run.CorrelationId,
            conversationKey: $"{TransportName}:{definition.Id}",
            transport: TransportName,
            conversationId: definition.Id,
            participantId: definition.Id,
            executionProfile: LlmExecutionProfile.ToolEnabled,
            // Автономный агент сам решает, что вспоминать: авто-инъекция памяти здесь только жгла бы контекст.
            memoryRetrievalMode: AgentOptions.MemoryRetrievalModeOnDemandTool,
            // Фоновый запуск не ведёт диалоговую историю, поэтому rolling summary ему не пересобирают.
            shouldRefreshPersistedSummary: false,
            // Границы берём из настроек конкретного агента — галочки в UI должны реально что-то значить.
            toolPolicy: AgentToolPolicy.ReadOnlyResearch(
                allowWebSearch: definition.ToolScope.AllowWebSearch,
                allowHomeAssistantRead: definition.ToolScope.AllowHomeAssistantRead,
                allowMemoryRead: definition.ToolScope.AllowMemoryRead),
            runBudget: runBudget);

    private async Task<string?> GetPreviousSummaryAsync(
        string agentId,
        string currentRunId,
        CancellationToken cancellationToken)
    {
        var recentRuns = await _repository.ListRunsAsync(agentId, 5, cancellationToken);
        return recentRuns
            .FirstOrDefault(candidate =>
                candidate.Id != currentRunId
                && candidate.Status == AutonomousAgentRunStatus.Completed
                && !string.IsNullOrWhiteSpace(candidate.Summary))
            ?.Summary;
    }

    private async Task UpdateContinuityAsync(
        string agentId,
        AutonomousAgentContinuity? continuity,
        AutonomousRunOutput output,
        string? capsuleNoteKey,
        CancellationToken cancellationToken)
    {
        var current = continuity ?? AutonomousAgentContinuity.Empty(agentId);
        var updated = current with
        {
            Focus = string.IsNullOrWhiteSpace(output.NextFocus) ? current.Focus : output.NextFocus,
            OpenQuestions = output.Questions.Count > 0
                ? string.Join(Environment.NewLine, output.Questions)
                : null,
            CapsuleNoteKey = capsuleNoteKey ?? current.CapsuleNoteKey,
            CapsuleUpdatedUtc = capsuleNoteKey is not null ? DateTimeOffset.UtcNow : current.CapsuleUpdatedUtc,
            UpdatedUtc = DateTimeOffset.UtcNow,
        };

        await _repository.UpsertContinuityAsync(updated, cancellationToken);
    }

    private async Task FailRunAsync(AutonomousAgentRun run, string error)
    {
        try
        {
            // CancellationToken.None намеренно: запись о провале должна пройти даже когда запуск отменён по таймауту.
            await _repository.UpdateRunAsync(run.Fail(error), CancellationToken.None);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Failed to record the failure of autonomous run {RunId}.", run.Id);
        }
    }
}
