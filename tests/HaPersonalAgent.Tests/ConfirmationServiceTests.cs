using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Confirmation;
using HaPersonalAgent.Dialogue;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты generic confirmation policy.
/// Зачем: Home Assistant, файловые операции и будущие risky actions должны выполняться только после `/approve` и не попадать в обычную память диалога.
/// Как: использует временную SQLite базу и fake executor вместо реальных внешних систем.
/// </summary>
public class ConfirmationServiceTests
{
    private const string FakeActionKind = "fake_action";

    [Fact]
    public async Task Propose_then_approve_executes_action_once_and_marks_completed()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var executor = new FakeActionExecutor(ConfirmationActionExecutionResult.Success("{\"ok\":true}"));
            var service = CreateService(repository, executor);
            var conversation = DialogueConversation.Create("telegram", "200", "100");

            var proposal = await service.ProposeAsync(
                CreateRequest(conversation, payloadJson: "{\"path\":\"notes.md\"}"),
                CancellationToken.None);
            var approved = await service.ApproveAsync(
                conversation,
                proposal.ConfirmationId!,
                CancellationToken.None);
            var secondApprove = await service.ApproveAsync(
                conversation,
                proposal.ConfirmationId!,
                CancellationToken.None);
            var stored = await repository.GetPendingConfirmationAsync(
                DialogueConversationKey.Create(conversation),
                conversation.ParticipantId,
                proposal.ConfirmationId!,
                CancellationToken.None);

            Assert.True(proposal.IsCreated);
            Assert.Contains("/approve", proposal.Message, StringComparison.Ordinal);
            Assert.True(approved.IsSuccess);
            Assert.Equal(ConfirmationDecisionOutcome.Completed, approved.Outcome);
            Assert.Contains("Результат:", approved.Message, StringComparison.Ordinal);
            Assert.Contains("\"ok\":true", approved.Message, StringComparison.Ordinal);
            Assert.Single(executor.ExecutedConfirmations);
            Assert.Equal(ConfirmationActionStatus.Completed, stored?.Status);
            Assert.Equal(ConfirmationDecisionOutcome.AlreadyHandled, secondApprove.Outcome);
            Assert.Empty(await repository.GetConversationMessagesAsync(
                DialogueConversationKey.Create(conversation),
                limit: 10,
                CancellationToken.None));
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Approve_uses_participant_fallback_when_conversation_key_differs()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var executor = new FakeActionExecutor(ConfirmationActionExecutionResult.Success("{\"ok\":true}"));
            var service = CreateService(repository, executor);
            var sourceConversation = DialogueConversation.Create("telegram", "200", "100");
            var fallbackConversation = DialogueConversation.Create("telegram", "201", "100");

            var proposal = await service.ProposeAsync(
                CreateRequest(sourceConversation, payloadJson: "{}"),
                CancellationToken.None);
            var approved = await service.ApproveAsync(
                fallbackConversation,
                proposal.ConfirmationId!,
                CancellationToken.None);

            Assert.True(proposal.IsCreated);
            Assert.True(approved.IsSuccess);
            Assert.Equal(ConfirmationDecisionOutcome.Completed, approved.Outcome);
            Assert.Single(executor.ExecutedConfirmations);
            Assert.Equal("100", executor.ExecutedConfirmations[0].ParticipantId);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Approve_with_different_participant_remains_not_found()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var executor = new FakeActionExecutor(ConfirmationActionExecutionResult.Success("{\"ok\":true}"));
            var service = CreateService(repository, executor);
            var sourceConversation = DialogueConversation.Create("telegram", "200", "100");
            var differentParticipantConversation = DialogueConversation.Create("telegram", "200", "999");

            var proposal = await service.ProposeAsync(
                CreateRequest(sourceConversation, payloadJson: "{}"),
                CancellationToken.None);
            var approved = await service.ApproveAsync(
                differentParticipantConversation,
                proposal.ConfirmationId!,
                CancellationToken.None);

            Assert.False(approved.IsSuccess);
            Assert.Equal(ConfirmationDecisionOutcome.NotFound, approved.Outcome);
            Assert.Empty(executor.ExecutedConfirmations);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Get_latest_pending_confirmation_id_returns_id_for_same_correlation_scope()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var service = CreateService(repository);
            var conversation = DialogueConversation.Create("telegram", "200", "100");

            var proposal = await service.ProposeAsync(
                CreateRequest(conversation, payloadJson: "{}"),
                CancellationToken.None);
            var latestId = await service.GetLatestPendingConfirmationIdAsync(
                conversation,
                "test-correlation",
                CancellationToken.None);

            Assert.True(proposal.IsCreated);
            Assert.Equal(proposal.ConfirmationId, latestId);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Approve_returns_sanitized_truncated_result_preview()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var resultJson = $$"""
                {
                  "ok": true,
                  "access_token": "secret-token",
                  "note": "authorization: Bearer embedded-secret",
                  "message": "{{new string('x', 2000)}}"
                }
                """;
            var executor = new FakeActionExecutor(ConfirmationActionExecutionResult.Success(resultJson));
            var service = CreateService(repository, executor);
            var conversation = DialogueConversation.Create("telegram", "200", "100");
            var proposal = await service.ProposeAsync(
                CreateRequest(conversation, payloadJson: "{}"),
                CancellationToken.None);

            var approved = await service.ApproveAsync(
                conversation,
                proposal.ConfirmationId!,
                CancellationToken.None);

            Assert.True(approved.IsSuccess);
            Assert.Contains("[redacted]", approved.Message, StringComparison.Ordinal);
            Assert.Contains("[truncated]", approved.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("secret-token", approved.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("embedded-secret", approved.Message, StringComparison.Ordinal);
            Assert.True(approved.Message.Length < 1800);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Reject_marks_confirmation_rejected_without_executing()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var executor = new FakeActionExecutor(ConfirmationActionExecutionResult.Success("{\"ok\":true}"));
            var service = CreateService(repository, executor);
            var conversation = DialogueConversation.Create("telegram", "200", "100");
            var proposal = await service.ProposeAsync(
                CreateRequest(conversation, payloadJson: "{}"),
                CancellationToken.None);

            var rejected = await service.RejectAsync(
                conversation,
                proposal.ConfirmationId!,
                CancellationToken.None);
            var stored = await repository.GetPendingConfirmationAsync(
                DialogueConversationKey.Create(conversation),
                conversation.ParticipantId,
                proposal.ConfirmationId!,
                CancellationToken.None);

            Assert.True(rejected.IsSuccess);
            Assert.Equal(ConfirmationDecisionOutcome.Rejected, rejected.Outcome);
            Assert.Empty(executor.ExecutedConfirmations);
            Assert.Equal(ConfirmationActionStatus.Rejected, stored?.Status);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Approve_expired_confirmation_marks_expired_without_executing()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var executor = new FakeActionExecutor(ConfirmationActionExecutionResult.Success("{\"ok\":true}"));
            var service = CreateService(repository, executor);
            var conversation = DialogueConversation.Create("telegram", "200", "100");
            var confirmation = new PendingConfirmation(
                "expired1",
                FakeActionKind,
                DialogueConversationKey.Create(conversation),
                conversation.ParticipantId,
                "fake_operation",
                "{}",
                "Run expired operation",
                "Changes external state.",
                ConfirmationActionStatus.Pending,
                DateTimeOffset.UtcNow.AddMinutes(-20),
                DateTimeOffset.UtcNow.AddMinutes(-10),
                CompletedAtUtc: null,
                "test-correlation",
                ResultJson: null,
                Error: null);
            await repository.SavePendingConfirmationAsync(confirmation, CancellationToken.None);

            var approved = await service.ApproveAsync(
                conversation,
                confirmation.Id,
                CancellationToken.None);
            var stored = await repository.GetPendingConfirmationAsync(
                DialogueConversationKey.Create(conversation),
                conversation.ParticipantId,
                confirmation.Id,
                CancellationToken.None);

            Assert.False(approved.IsSuccess);
            Assert.Equal(ConfirmationDecisionOutcome.Expired, approved.Outcome);
            Assert.Empty(executor.ExecutedConfirmations);
            Assert.Equal(ConfirmationActionStatus.Expired, stored?.Status);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Proposal_rejects_invalid_payload_object()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var service = CreateService(
                CreateRepository(databasePath),
                new FakeActionExecutor(ConfirmationActionExecutionResult.Success("{\"ok\":true}")));
            var conversation = DialogueConversation.Create("telegram", "200", "100");

            var invalidPayload = await service.ProposeAsync(
                CreateRequest(conversation, payloadJson: "[]"),
                CancellationToken.None);

            Assert.False(invalidPayload.IsCreated);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Approve_without_registered_executor_marks_failed()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var service = CreateService(repository);
            var conversation = DialogueConversation.Create("telegram", "200", "100");
            var proposal = await service.ProposeAsync(
                CreateRequest(conversation, payloadJson: "{}"),
                CancellationToken.None);

            var approved = await service.ApproveAsync(
                conversation,
                proposal.ConfirmationId!,
                CancellationToken.None);
            var stored = await repository.GetPendingConfirmationAsync(
                DialogueConversationKey.Create(conversation),
                conversation.ParticipantId,
                proposal.ConfirmationId!,
                CancellationToken.None);

            Assert.False(approved.IsSuccess);
            Assert.Equal(ConfirmationDecisionOutcome.ExecutorMissing, approved.Outcome);
            Assert.Equal(ConfirmationActionStatus.Failed, stored?.Status);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Participant_scoped_list_and_approve_drive_an_autonomous_agents_proposal_to_execution()
    {
        // HPA-035: фоновый прогон создаёт предложение с participant == agentId; владелец одобряет его сервер-сайд.
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var repository = CreateRepository(databasePath);
            var executor = new FakeActionExecutor(ConfirmationActionExecutionResult.Success("{\"ok\":true}"));
            var service = CreateService(repository, executor);
            var agentConversation = DialogueConversation.Create("autonomous", "agent-42", "agent-42");

            var proposal = await service.ProposeAsync(
                CreateRequest(agentConversation, payloadJson: "{}"),
                CancellationToken.None);

            var listed = await service.ListPendingForParticipantAsync("agent-42", correlationId: null, CancellationToken.None);
            Assert.Single(listed);
            Assert.Equal(proposal.ConfirmationId, listed[0].Id);

            var approved = await service.ApproveForParticipantAsync("agent-42", proposal.ConfirmationId!, CancellationToken.None);

            Assert.True(approved.IsSuccess);
            Assert.Equal(ConfirmationDecisionOutcome.Completed, approved.Outcome);
            Assert.Single(executor.ExecutedConfirmations);
            // После исполнения предложение больше не «ожидающее».
            Assert.Empty(await service.ListPendingForParticipantAsync("agent-42", null, CancellationToken.None));
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Approve_by_confirmation_id_resolves_the_owner_participant_from_the_confirmation()
    {
        // Telegram-кнопка знает только id действия — участник резолвится из самого подтверждения.
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var repository = CreateRepository(databasePath);
            var executor = new FakeActionExecutor(ConfirmationActionExecutionResult.Success("{\"ok\":true}"));
            var service = CreateService(repository, executor);
            var agentConversation = DialogueConversation.Create("autonomous", "agent-7", "agent-7");

            var proposal = await service.ProposeAsync(
                CreateRequest(agentConversation, payloadJson: "{}"),
                CancellationToken.None);

            var approved = await service.ApproveByConfirmationIdAsync(proposal.ConfirmationId!, CancellationToken.None);

            Assert.True(approved.IsSuccess);
            Assert.Equal(ConfirmationDecisionOutcome.Completed, approved.Outcome);
            Assert.Single(executor.ExecutedConfirmations);
            Assert.Equal("agent-7", executor.ExecutedConfirmations[0].ParticipantId);

            // Неизвестный id — NotFound, ничего не исполняется.
            var missing = await service.ApproveByConfirmationIdAsync("does-not-exist", CancellationToken.None);
            Assert.Equal(ConfirmationDecisionOutcome.NotFound, missing.Outcome);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Reject_for_participant_marks_rejected_and_never_executes()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var repository = CreateRepository(databasePath);
            var executor = new FakeActionExecutor(ConfirmationActionExecutionResult.Success("{}"));
            var service = CreateService(repository, executor);
            var agentConversation = DialogueConversation.Create("autonomous", "agent-9", "agent-9");

            var proposal = await service.ProposeAsync(
                CreateRequest(agentConversation, payloadJson: "{}"),
                CancellationToken.None);
            var rejected = await service.RejectForParticipantAsync("agent-9", proposal.ConfirmationId!, CancellationToken.None);

            Assert.Equal(ConfirmationDecisionOutcome.Rejected, rejected.Outcome);
            Assert.Empty(executor.ExecutedConfirmations);
            Assert.Empty(await service.ListPendingForParticipantAsync("agent-9", null, CancellationToken.None));
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Participant_scoped_list_filters_out_expired_proposals()
    {
        var databasePath = CreateTemporaryDatabasePath();
        try
        {
            var repository = CreateRepository(databasePath);
            var service = CreateService(repository);
            var agentConversation = DialogueConversation.Create("autonomous", "agent-x", "agent-x");

            // Предложение с истёкшим сроком не должно попасть в список ожидающих.
            var expiredRequest = new ConfirmationProposalRequest(
                AgentContext.Create(
                    "corr",
                    conversationKey: DialogueConversationKey.Create(agentConversation),
                    participantId: agentConversation.ParticipantId),
                FakeActionKind,
                "fake_operation",
                "{}",
                "summary",
                "risk",
                ExpiresAfter: TimeSpan.FromSeconds(-1));
            await service.ProposeAsync(expiredRequest, CancellationToken.None);

            Assert.Empty(await service.ListPendingForParticipantAsync("agent-x", null, CancellationToken.None));
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    private static ConfirmationProposalRequest CreateRequest(
        DialogueConversation conversation,
        string payloadJson,
        string actionKind = FakeActionKind,
        string operationName = "fake_operation",
        string summary = "Run fake operation",
        string risk = "Changes external state.")
    {
        return new ConfirmationProposalRequest(
            AgentContext.Create(
                "test-correlation",
                conversationKey: DialogueConversationKey.Create(conversation),
                participantId: conversation.ParticipantId),
            actionKind,
            operationName,
            payloadJson,
            summary,
            risk);
    }

    private static ConfirmationService CreateService(
        AgentStateRepository repository,
        params IConfirmationActionExecutor[] executors)
    {
        var loggerFactory = LoggerFactory.Create(_ => { });

        return new ConfirmationService(
            repository,
            executors,
            new ConfirmationResultFormatter(),
            loggerFactory.CreateLogger<ConfirmationService>());
    }

    private static AgentStateRepository CreateRepository(string databasePath)
    {
        var options = Options.Create(new AgentOptions
        {
            StateDatabasePath = databasePath,
        });
        var connectionFactory = new SqliteConnectionFactory(options);

        return new AgentStateRepository(connectionFactory);
    }

    private static string CreateTemporaryDatabasePath()
    {
        return Path.Combine(
            Path.GetTempPath(),
            "ha-personal-agent-tests",
            Guid.NewGuid().ToString("N"),
            "state.sqlite");
    }

    private static void DeleteTemporaryDatabaseDirectory(string databasePath)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory) && Directory.Exists(directory))
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    /// <summary>
    /// Что: fake executor для generic confirmation tests.
    /// Зачем: approve path должен проверяться без реальных внешних систем.
    /// Как: записывает полученные confirmations и возвращает заранее заданный результат.
    /// </summary>
    private sealed class FakeActionExecutor : IConfirmationActionExecutor
    {
        private readonly string _actionKind;
        private readonly ConfirmationActionExecutionResult _result;

        public FakeActionExecutor(
            ConfirmationActionExecutionResult result,
            string actionKind = FakeActionKind)
        {
            _result = result;
            _actionKind = actionKind;
        }

        public string ActionKind => _actionKind;

        public List<PendingConfirmation> ExecutedConfirmations { get; } = new();

        public Task<ConfirmationActionExecutionResult> ExecuteAsync(
            PendingConfirmation confirmation,
            CancellationToken cancellationToken)
        {
            ExecutedConfirmations.Add(confirmation);

            return Task.FromResult(_result);
        }
    }
}
