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
            Assert.Single(executor.ExecutedConfirmations);
            Assert.Equal(ConfirmationActionStatus.Completed, stored?.Status);
            Assert.Equal(ConfirmationDecisionOutcome.AlreadyHandled, secondApprove.Outcome);
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

    private static ConfirmationProposalRequest CreateRequest(
        DialogueConversation conversation,
        string payloadJson)
    {
        return new ConfirmationProposalRequest(
            AgentContext.Create(
                "test-correlation",
                conversationKey: DialogueConversationKey.Create(conversation),
                participantId: conversation.ParticipantId),
            FakeActionKind,
            "fake_operation",
            payloadJson,
            "Run fake operation",
            "Changes external state.");
    }

    private static ConfirmationService CreateService(
        AgentStateRepository repository,
        params IConfirmationActionExecutor[] executors)
    {
        var loggerFactory = LoggerFactory.Create(_ => { });

        return new ConfirmationService(
            repository,
            executors,
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
        private readonly ConfirmationActionExecutionResult _result;

        public FakeActionExecutor(ConfirmationActionExecutionResult result)
        {
            _result = result;
        }

        public string ActionKind => FakeActionKind;

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
