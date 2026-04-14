using HaPersonalAgent.Agent;

namespace HaPersonalAgent.Confirmation;

/// <summary>
/// Что: запрос на создание pending confirmation.
/// Зачем: producer конкретного risky action должен передать generic service только action kind, operation, payload и human-readable summary/risk.
/// Как: MAF tool или другой adapter формирует request, а ConfirmationService валидирует scope и сохраняет PendingConfirmation.
/// </summary>
public sealed record ConfirmationProposalRequest(
    AgentContext Context,
    string ActionKind,
    string OperationName,
    string PayloadJson,
    string Summary,
    string Risk,
    TimeSpan? ExpiresAfter = null);
