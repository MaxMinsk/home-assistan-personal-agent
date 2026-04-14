using System.Text.Json;
using HaPersonalAgent.Dialogue;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Logging;

namespace HaPersonalAgent.Confirmation;

/// <summary>
/// Что: generic confirmation policy для любых risky actions.
/// Зачем: агент может готовить управление домом, изменение файлов и другие операции, но фактическое выполнение должно происходить только после явного approval.
/// Как: proposal сохраняет PendingConfirmation в SQLite, а `/approve` атомарно переводит ее в Executing и запускает executor по ActionKind.
/// </summary>
public sealed class ConfirmationService : IConfirmationService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan DefaultPendingLifetime = TimeSpan.FromMinutes(10);

    private readonly IReadOnlyDictionary<string, IConfirmationActionExecutor> _executorsByKind;
    private readonly ILogger<ConfirmationService> _logger;
    private readonly AgentStateRepository _stateRepository;

    public ConfirmationService(
        AgentStateRepository stateRepository,
        IEnumerable<IConfirmationActionExecutor> actionExecutors,
        ILogger<ConfirmationService> logger)
    {
        _stateRepository = stateRepository;
        _executorsByKind = actionExecutors.ToDictionary(
            executor => executor.ActionKind,
            StringComparer.Ordinal);
        _logger = logger;
    }

    public async Task<ConfirmationProposalResult> ProposeAsync(
        ConfirmationProposalRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(request.Context);

        if (string.IsNullOrWhiteSpace(request.Context.ConversationKey)
            || string.IsNullOrWhiteSpace(request.Context.ParticipantId))
        {
            return ConfirmationProposalResult.Rejected("Не могу создать действие: текущий канал не поддерживает confirmation scope.");
        }

        if (string.IsNullOrWhiteSpace(request.ActionKind))
        {
            return ConfirmationProposalResult.Rejected("Не могу создать действие: action kind пустой.");
        }

        if (string.IsNullOrWhiteSpace(request.OperationName))
        {
            return ConfirmationProposalResult.Rejected("Не могу создать действие: operation name пустой.");
        }

        if (!TryNormalizePayloadJson(request.PayloadJson, out var normalizedPayloadJson, out var payloadError))
        {
            return ConfirmationProposalResult.Rejected($"Не могу создать действие: {payloadError}");
        }

        var now = DateTimeOffset.UtcNow;
        var confirmationId = CreateConfirmationId();
        var safeSummary = NormalizeHumanText(request.Summary, request.OperationName.Trim());
        var safeRisk = NormalizeHumanText(request.Risk, "Действие может изменить состояние внешней системы.");
        var pendingConfirmation = new PendingConfirmation(
            confirmationId,
            request.ActionKind.Trim(),
            request.Context.ConversationKey,
            request.Context.ParticipantId,
            request.OperationName.Trim(),
            normalizedPayloadJson,
            safeSummary,
            safeRisk,
            ConfirmationActionStatus.Pending,
            now,
            now.Add(request.ExpiresAfter ?? DefaultPendingLifetime),
            CompletedAtUtc: null,
            request.Context.CorrelationId,
            ResultJson: null,
            Error: null);

        await _stateRepository.SavePendingConfirmationAsync(
            pendingConfirmation,
            cancellationToken);
        await AppendAuditAsync(
            pendingConfirmation,
            "Proposed",
            details: null,
            now,
            cancellationToken);

        var approveCommand = $"/approve {confirmationId}";
        var rejectCommand = $"/reject {confirmationId}";
        var message = string.Join(
            Environment.NewLine,
            $"Нужно подтверждение действия {confirmationId}.",
            $"Тип: {pendingConfirmation.ActionKind}",
            $"Действие: {safeSummary}",
            $"Риск: {safeRisk}",
            $"Подтвердить: {approveCommand}",
            $"Отклонить: {rejectCommand}",
            $"Истекает: {pendingConfirmation.ExpiresAtUtc:yyyy-MM-dd HH:mm:ss} UTC");

        return new ConfirmationProposalResult(
            IsCreated: true,
            message,
            confirmationId,
            approveCommand,
            rejectCommand,
            pendingConfirmation.ExpiresAtUtc);
    }

    public async Task<ConfirmationDecisionResult> ApproveAsync(
        DialogueConversation conversation,
        string confirmationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationId);

        var pendingConfirmation = await GetScopedConfirmationAsync(
            conversation,
            confirmationId,
            cancellationToken);
        if (pendingConfirmation is null)
        {
            return new ConfirmationDecisionResult(
                ConfirmationDecisionOutcome.NotFound,
                IsSuccess: false,
                $"Действие {confirmationId} не найдено для этого чата.",
                confirmationId);
        }

        var now = DateTimeOffset.UtcNow;
        if (pendingConfirmation.Status != ConfirmationActionStatus.Pending)
        {
            return AlreadyHandled(pendingConfirmation);
        }

        if (pendingConfirmation.IsExpired(now))
        {
            await TryCompleteAsync(
                pendingConfirmation,
                ConfirmationActionStatus.Pending,
                ConfirmationActionStatus.Expired,
                now,
                resultJson: null,
                error: "Pending confirmation expired.",
                cancellationToken);
            await AppendAuditAsync(pendingConfirmation, "Expired", null, now, cancellationToken);

            return new ConfirmationDecisionResult(
                ConfirmationDecisionOutcome.Expired,
                IsSuccess: false,
                $"Действие {pendingConfirmation.Id} истекло. Попроси агента подготовить его заново.",
                pendingConfirmation.Id);
        }

        if (!_executorsByKind.TryGetValue(pendingConfirmation.ActionKind, out var executor))
        {
            await TryCompleteAsync(
                pendingConfirmation,
                ConfirmationActionStatus.Pending,
                ConfirmationActionStatus.Failed,
                now,
                resultJson: null,
                error: $"Executor for action kind '{pendingConfirmation.ActionKind}' is not registered.",
                cancellationToken);
            await AppendAuditAsync(pendingConfirmation, "ExecutorMissing", null, now, cancellationToken);

            return new ConfirmationDecisionResult(
                ConfirmationDecisionOutcome.ExecutorMissing,
                IsSuccess: false,
                $"Не удалось выполнить действие {pendingConfirmation.Id}: executor для '{pendingConfirmation.ActionKind}' не зарегистрирован.",
                pendingConfirmation.Id);
        }

        var started = await _stateRepository.TryUpdateConfirmationStatusAsync(
            pendingConfirmation.Id,
            ConfirmationActionStatus.Pending,
            ConfirmationActionStatus.Executing,
            completedAtUtc: null,
            resultJson: null,
            error: null,
            cancellationToken);
        if (!started)
        {
            _logger.LogInformation(
                "Confirmation {ConfirmationId} was not moved to Executing because status changed concurrently.",
                pendingConfirmation.Id);

            return AlreadyHandled(pendingConfirmation);
        }

        await AppendAuditAsync(pendingConfirmation, "Approved", null, now, cancellationToken);

        var executionResult = await executor.ExecuteAsync(
            pendingConfirmation with { Status = ConfirmationActionStatus.Executing },
            cancellationToken);
        var completedAtUtc = DateTimeOffset.UtcNow;

        if (executionResult.IsSuccess)
        {
            await TryCompleteAsync(
                pendingConfirmation,
                ConfirmationActionStatus.Executing,
                ConfirmationActionStatus.Completed,
                completedAtUtc,
                executionResult.ResultJson,
                error: null,
                cancellationToken);
            await AppendAuditAsync(
                pendingConfirmation,
                "Completed",
                Truncate(executionResult.ResultJson, 512),
                completedAtUtc,
                cancellationToken);

            return new ConfirmationDecisionResult(
                ConfirmationDecisionOutcome.Completed,
                IsSuccess: true,
                $"Выполнено действие {pendingConfirmation.Id}: {pendingConfirmation.Summary}",
                pendingConfirmation.Id,
                executionResult.ResultJson);
        }

        await TryCompleteAsync(
            pendingConfirmation,
            ConfirmationActionStatus.Executing,
            ConfirmationActionStatus.Failed,
            completedAtUtc,
            resultJson: null,
            executionResult.Error,
            cancellationToken);
        await AppendAuditAsync(
            pendingConfirmation,
            "Failed",
            executionResult.Error,
            completedAtUtc,
            cancellationToken);

        return new ConfirmationDecisionResult(
            ConfirmationDecisionOutcome.ExecutionFailed,
            IsSuccess: false,
            $"Не удалось выполнить действие {pendingConfirmation.Id}: {executionResult.Error}",
            pendingConfirmation.Id);
    }

    public async Task<ConfirmationDecisionResult> RejectAsync(
        DialogueConversation conversation,
        string confirmationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(conversation);
        ArgumentException.ThrowIfNullOrWhiteSpace(confirmationId);

        var pendingConfirmation = await GetScopedConfirmationAsync(
            conversation,
            confirmationId,
            cancellationToken);
        if (pendingConfirmation is null)
        {
            return new ConfirmationDecisionResult(
                ConfirmationDecisionOutcome.NotFound,
                IsSuccess: false,
                $"Действие {confirmationId} не найдено для этого чата.",
                confirmationId);
        }

        if (pendingConfirmation.Status != ConfirmationActionStatus.Pending)
        {
            return AlreadyHandled(pendingConfirmation);
        }

        var now = DateTimeOffset.UtcNow;
        await TryCompleteAsync(
            pendingConfirmation,
            ConfirmationActionStatus.Pending,
            ConfirmationActionStatus.Rejected,
            now,
            resultJson: null,
            error: "Rejected by user.",
            cancellationToken);
        await AppendAuditAsync(pendingConfirmation, "Rejected", null, now, cancellationToken);

        return new ConfirmationDecisionResult(
            ConfirmationDecisionOutcome.Rejected,
            IsSuccess: true,
            $"Отклонено действие {pendingConfirmation.Id}: {pendingConfirmation.Summary}",
            pendingConfirmation.Id);
    }

    private async Task<PendingConfirmation?> GetScopedConfirmationAsync(
        DialogueConversation conversation,
        string confirmationId,
        CancellationToken cancellationToken)
    {
        var conversationKey = DialogueConversationKey.Create(conversation);

        return await _stateRepository.GetPendingConfirmationAsync(
            conversationKey,
            conversation.ParticipantId,
            confirmationId.Trim(),
            cancellationToken);
    }

    private async Task TryCompleteAsync(
        PendingConfirmation pendingConfirmation,
        ConfirmationActionStatus expectedStatus,
        ConfirmationActionStatus newStatus,
        DateTimeOffset completedAtUtc,
        string? resultJson,
        string? error,
        CancellationToken cancellationToken)
    {
        var updated = await _stateRepository.TryUpdateConfirmationStatusAsync(
            pendingConfirmation.Id,
            expectedStatus,
            newStatus,
            completedAtUtc,
            resultJson,
            error,
            cancellationToken);
        if (!updated)
        {
            _logger.LogInformation(
                "Confirmation {ConfirmationId} was not moved from {ExpectedStatus} to {NewStatus}.",
                pendingConfirmation.Id,
                expectedStatus,
                newStatus);
        }
    }

    private async Task AppendAuditAsync(
        PendingConfirmation confirmation,
        string eventName,
        string? details,
        DateTimeOffset createdAtUtc,
        CancellationToken cancellationToken)
    {
        await _stateRepository.AppendConfirmationAuditAsync(
            new ConfirmationAuditRecord(
                confirmation.Id,
                confirmation.ActionKind,
                confirmation.ConversationKey,
                confirmation.ParticipantId,
                confirmation.OperationName,
                eventName,
                confirmation.Summary,
                Truncate(details, 1024),
                createdAtUtc),
            cancellationToken);
    }

    private static ConfirmationDecisionResult AlreadyHandled(PendingConfirmation pendingConfirmation) =>
        new(
            ConfirmationDecisionOutcome.AlreadyHandled,
            IsSuccess: false,
            $"Действие {pendingConfirmation.Id} уже не ожидает подтверждения. Текущий статус: {pendingConfirmation.Status}.",
            pendingConfirmation.Id);

    private static bool TryNormalizePayloadJson(
        string payloadJson,
        out string normalizedPayloadJson,
        out string error)
    {
        normalizedPayloadJson = "{}";
        error = string.Empty;

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            return true;
        }

        try
        {
            using var document = JsonDocument.Parse(payloadJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                error = "payloadJson должен быть JSON object.";
                return false;
            }

            normalizedPayloadJson = JsonSerializer.Serialize(document.RootElement, JsonOptions);
            return true;
        }
        catch (JsonException)
        {
            error = "payloadJson содержит невалидный JSON.";
            return false;
        }
    }

    private static string NormalizeHumanText(string text, string fallback)
    {
        var normalized = string.IsNullOrWhiteSpace(text) ? fallback : text.Trim();

        return Truncate(normalized, 500) ?? fallback;
    }

    private static string CreateConfirmationId() =>
        Guid.NewGuid().ToString("N")[..8];

    private static string? Truncate(string? value, int maxLength)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return value;
        }

        return value.Length <= maxLength
            ? value
            : value[..maxLength];
    }
}
