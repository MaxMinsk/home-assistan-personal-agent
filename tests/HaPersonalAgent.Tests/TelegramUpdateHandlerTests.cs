using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Confirmation;
using HaPersonalAgent.Dialogue;
using HaPersonalAgent.HomeAssistant;
using HaPersonalAgent.Storage;
using HaPersonalAgent.Telegram;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Telegram.Bot.Types;
using Telegram.Bot.Types.Enums;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты Telegram update handler без реального Telegram API.
/// Зачем: MVP должен проверять allowlist, /resetContext и передачу SQLite-истории в agent runtime до запуска на домашнем сервере.
/// Как: использует fake Telegram adapter и fake IAgentRuntime, а состояние хранит во временной SQLite базе.
/// </summary>
public class TelegramUpdateHandlerTests
{
    [Fact]
    public async Task Text_message_invokes_agent_with_persisted_context_and_saves_reply()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var runtime = new FakeAgentRuntime("Запомнил.");
            var handler = CreateHandler(repository, runtime);
            var adapter = new FakeTelegramBotClientAdapter();
            var options = new TelegramOptions
            {
                AllowedUserIds = new long[] { 100 },
            };

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 10, chatId: 200, userId: 100, text: "Меня зовут Максим."),
                options,
                CancellationToken.None);

            runtime.NextResponseText = "Тебя зовут Максим.";
            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 11, chatId: 200, userId: 100, text: "Как меня зовут?"),
                options,
                CancellationToken.None);

            Assert.Equal(2, runtime.Calls.Count);
            Assert.Equal("Как меня зовут?", runtime.Calls[1].Message);
            Assert.Collection(
                runtime.Calls[1].Context.ConversationMessages,
                message =>
                {
                    Assert.Equal(AgentConversationRole.User, message.Role);
                    Assert.Equal("Меня зовут Максим.", message.Text);
                },
                message =>
                {
                    Assert.Equal(AgentConversationRole.Assistant, message.Role);
                    Assert.Equal("Запомнил.", message.Text);
                });

            var stored = await repository.GetConversationMessagesAsync("telegram:200:100", 10, CancellationToken.None);
            Assert.Equal(
                new[] { "Меня зовут Максим.", "Запомнил.", "Как меня зовут?", "Тебя зовут Максим." },
                stored.Select(message => message.Text));
            Assert.Equal(new[] { "Запомнил.", "Тебя зовут Максим." }, adapter.SentMessages.Select(message => message.Text));
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Reset_context_clears_only_current_chat_user_context()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            await repository.AppendConversationMessagesAsync(
                "telegram:200:100",
                new[] { new AgentConversationMessage(AgentConversationRole.User, "clear me", DateTimeOffset.UtcNow) },
                CancellationToken.None);
            await repository.AppendConversationMessagesAsync(
                "telegram:201:100",
                new[] { new AgentConversationMessage(AgentConversationRole.User, "keep me", DateTimeOffset.UtcNow) },
                CancellationToken.None);

            var handler = CreateHandler(repository, new FakeAgentRuntime("unused"));
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 12, chatId: 200, userId: 100, text: "/resetContext"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            var cleared = await repository.GetConversationMessagesAsync("telegram:200:100", 10, CancellationToken.None);
            var kept = await repository.GetConversationMessagesAsync("telegram:201:100", 10, CancellationToken.None);

            Assert.Empty(cleared);
            Assert.Single(kept);
            Assert.Contains("очищен", adapter.SentMessages.Single().Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Show_summarized_returns_persisted_summary_for_current_conversation()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            await repository.AppendConversationMessagesAsync(
                "telegram:200:100",
                new[] { new AgentConversationMessage(AgentConversationRole.User, "seed", DateTimeOffset.UtcNow) },
                CancellationToken.None);
            var latestMessageId = await repository.GetLatestConversationMessageIdAsync(
                "telegram:200:100",
                CancellationToken.None);
            Assert.True(latestMessageId.HasValue);
            await repository.UpsertConversationSummaryAsync(
                new ConversationSummaryMemory(
                    "telegram:200:100",
                    "Краткое summary по диалогу.",
                    DateTimeOffset.UtcNow,
                    latestMessageId!.Value,
                    SummaryVersion: 3),
                CancellationToken.None);

            var runtime = new FakeAgentRuntime("unused");
            var handler = CreateHandler(repository, runtime);
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 19, chatId: 200, userId: 100, text: "/showSummary"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            Assert.Empty(runtime.Calls);
            Assert.Single(adapter.SentMessages);
            Assert.Contains("Summary version: 3", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains("Краткое summary по диалогу.", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Show_summarized_returns_empty_message_when_summary_is_missing()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var runtime = new FakeAgentRuntime("unused");
            var handler = CreateHandler(repository, runtime);
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 20, chatId: 200, userId: 100, text: "/showSummary"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            Assert.Empty(runtime.Calls);
            Assert.Single(adapter.SentMessages);
            Assert.Contains("persisted summary", adapter.SentMessages.Single().Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Refresh_summary_forces_rebuild_and_returns_updated_snapshot()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var now = DateTimeOffset.UtcNow;
            await repository.AppendConversationMessagesAsync(
                "telegram:200:100",
                new[]
                {
                    new AgentConversationMessage(AgentConversationRole.User, "u1", now),
                    new AgentConversationMessage(AgentConversationRole.Assistant, "a1", now),
                },
                CancellationToken.None);
            var latestMessageId = await repository.GetLatestConversationMessageIdAsync(
                "telegram:200:100",
                CancellationToken.None);
            Assert.True(latestMessageId.HasValue);
            await repository.UpsertConversationSummaryAsync(
                new ConversationSummaryMemory(
                    "telegram:200:100",
                    "старый summary",
                    now,
                    latestMessageId!.Value,
                    SummaryVersion: 1),
                CancellationToken.None);

            var runtime = new FakeAgentRuntime("service response")
            {
                NextSummaryCandidate = "новый summary",
            };
            var handler = CreateHandler(repository, runtime);
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 21, chatId: 200, userId: 100, text: "/refreshSummary"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            var stored = await repository.GetConversationMessagesAsync("telegram:200:100", 10, CancellationToken.None);
            var refreshedSummary = await repository.GetConversationSummaryAsync("telegram:200:100", CancellationToken.None);

            Assert.Single(runtime.Calls);
            Assert.Equal(LlmExecutionProfile.Summarization, runtime.Calls.Single().Context.ExecutionProfile);
            Assert.True(runtime.Calls.Single().Context.ForcePersistedSummaryRefresh);
            Assert.Equal(2, stored.Count);
            Assert.NotNull(refreshedSummary);
            Assert.Equal(2, refreshedSummary.SummaryVersion);
            Assert.Equal("новый summary", refreshedSummary.Summary);
            Assert.Contains("пересобран", adapter.SentMessages.Single().Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Summary version: 2", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Refresh_summary_returns_noop_when_history_is_empty()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var runtime = new FakeAgentRuntime("unused");
            var handler = CreateHandler(repository, runtime);
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 22, chatId: 200, userId: 100, text: "/refreshSummary"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            Assert.Empty(runtime.Calls);
            Assert.Contains("нет истории", adapter.SentMessages.Single().Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Non_allowlisted_user_is_ignored_without_response()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var handler = CreateHandler(
                CreateRepository(databasePath),
                new FakeAgentRuntime("unused"));
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 13, chatId: 200, userId: 999, text: "hello"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            Assert.Empty(adapter.SentMessages);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Status_command_includes_home_assistant_mcp_health()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var handler = CreateHandler(
                CreateRepository(databasePath),
                new FakeAgentRuntime("unused"),
                new FakeHomeAssistantMcpClient(
                    HomeAssistantMcpDiscoveryResult.Reachable(
                        new Uri("http://supervisor/core/api/mcp"),
                        new HomeAssistantMcpDiscovery(
                            new[] { new HomeAssistantMcpItemInfo("get_state", "Get state", "Reads HA state") },
                            new[] { new HomeAssistantMcpItemInfo("assist", "Assist", "Uses Assist API") }))));
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 14, chatId: 200, userId: 100, text: "/status"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            Assert.Contains("HA MCP: reachable (1 tools, 1 prompts)", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains("thinking auto", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains("ReasoningActive(tool-enabled): True", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains("ReasoningPlan(tool-enabled): requested auto, effective provider-default", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains("Context(stored): 0 messages", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains("Context(loaded): 0 / 24 messages", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains("PersistedSummary: present False", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Think_command_invokes_deep_reasoning_profile_without_command_prefix()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var runtime = new FakeAgentRuntime("deep answer");
            var handler = CreateHandler(CreateRepository(databasePath), runtime);
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 18, chatId: 200, userId: 100, text: "/think сравни два подхода"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            Assert.Single(runtime.Calls);
            Assert.Equal("сравни два подхода", runtime.Calls.Single().Message);
            Assert.Equal(LlmExecutionProfile.DeepReasoning, runtime.Calls.Single().Context.ExecutionProfile);
            Assert.Equal("deep answer", adapter.SentMessages.Single().Text);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Natural_language_mcp_status_question_goes_through_dialogue_agent()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var runtime = new FakeAgentRuntime("agent answer");
            var handler = CreateHandler(
                CreateRepository(databasePath),
                runtime,
                new FakeHomeAssistantMcpClient(HomeAssistantMcpDiscoveryResult.NotConfigured(
                    new Uri("http://supervisor/core/api/mcp"),
                    "HomeAssistant:LongLivedAccessToken is empty.")));
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 15, chatId: 200, userId: 100, text: "доступен mcp?"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            Assert.Single(runtime.Calls);
            Assert.Equal("доступен mcp?", runtime.Calls.Single().Message);
            Assert.Equal("agent answer", adapter.SentMessages.Single().Text);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Approve_command_uses_confirmation_service_without_invoking_dialogue_agent()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var runtime = new FakeAgentRuntime("unused");
            var confirmationService = new FakeConfirmationService(
                new ConfirmationDecisionResult(
                    ConfirmationDecisionOutcome.Completed,
                    IsSuccess: true,
                    "Выполнено действие abc12345.",
                    "abc12345"));
            var handler = CreateHandler(
                CreateRepository(databasePath),
                runtime,
                confirmationService: confirmationService);
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 16, chatId: 200, userId: 100, text: "/approve abc12345"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            Assert.Empty(runtime.Calls);
            Assert.Single(confirmationService.ApprovedConfirmations);
            Assert.Equal("abc12345", confirmationService.ApprovedConfirmations.Single().ConfirmationId);
            Assert.Contains("Выполнено", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Agent_runtime_exception_returns_user_facing_message_without_saving_context()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var runtime = new FakeAgentRuntime("unused")
            {
                ExceptionToThrow = new InvalidOperationException("provider failed"),
            };
            var handler = CreateHandler(repository, runtime);
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 17, chatId: 200, userId: 100, text: "hello"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            var stored = await repository.GetConversationMessagesAsync("telegram:200:100", 10, CancellationToken.None);

            Assert.Contains("Не смог обработать", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Empty(stored);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    private static TelegramUpdateHandler CreateHandler(
        AgentStateRepository repository,
        FakeAgentRuntime runtime,
        IHomeAssistantMcpClient? homeAssistantMcpClient = null,
        IConfirmationService? confirmationService = null)
    {
        var statusProvider = new ConfigurationStatusProvider(
            Options.Create(new AgentOptions()),
            Options.Create(new TelegramOptions { AllowedUserIds = new long[] { 100 } }),
            Options.Create(new LlmOptions { ApiKey = "configured" }),
            Options.Create(new HomeAssistantOptions()));
        var llmOptions = Options.Create(new LlmOptions
        {
            ApiKey = "configured",
        });
        var loggerFactory = LoggerFactory.Create(_ => { });
        var dialogueService = new DialogueService(
            runtime,
            Options.Create(new AgentOptions()),
            repository,
            loggerFactory.CreateLogger<DialogueService>());

        return new TelegramUpdateHandler(
            dialogueService,
            homeAssistantMcpClient ?? new FakeHomeAssistantMcpClient(HomeAssistantMcpDiscoveryResult.NotConfigured(
                new Uri("http://supervisor/core/api/mcp"),
                "HomeAssistant:LongLivedAccessToken is empty.")),
            new AgentStatusTool(statusProvider),
            runtime,
            new LlmExecutionPlanner(new LlmProviderCapabilitiesResolver()),
            llmOptions,
            loggerFactory.CreateLogger<TelegramUpdateHandler>(),
            confirmationService);
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

    private static Update CreateTextUpdate(int updateId, long chatId, long userId, string text) =>
        new()
        {
            Id = updateId,
            Message = new Message
            {
                Id = 1,
                Date = DateTime.UtcNow,
                Chat = new Chat
                {
                    Id = chatId,
                    Type = ChatType.Private,
                },
                From = new User
                {
                    Id = userId,
                    IsBot = false,
                    FirstName = "Test",
                },
                Text = text,
            },
        };

    /// <summary>
    /// Что: fake agent runtime для тестов Telegram handler.
    /// Зачем: тесты не должны обращаться к Moonshot/OpenAI-compatible endpoint и тратить API quota.
    /// Как: записывает вызовы SendAsync и возвращает заранее заданный текст ответа.
    /// </summary>
    private sealed class FakeAgentRuntime : IAgentRuntime
    {
        public FakeAgentRuntime(string nextResponseText)
        {
            NextResponseText = nextResponseText;
        }

        public string NextResponseText { get; set; }

        public string? NextSummaryCandidate { get; set; }

        public Exception? ExceptionToThrow { get; set; }

        public List<(string Message, AgentContext Context)> Calls { get; } = new();

        public AgentRuntimeHealth GetHealth() =>
            AgentRuntimeHealth.Configured(new LlmOptions());

        public Task<AgentRuntimeResponse> SendAsync(
            string message,
            AgentContext context,
            CancellationToken cancellationToken)
        {
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            Calls.Add((message, context));

            return Task.FromResult(new AgentRuntimeResponse(
                context.CorrelationId,
                IsConfigured: true,
                NextResponseText,
                GetHealth(),
                NextSummaryCandidate));
        }
    }

    /// <summary>
    /// Что: fake Home Assistant MCP client для тестов Telegram handler.
    /// Зачем: `/status` должен проверяться без сетевого вызова к Home Assistant `/api/mcp`.
    /// Как: возвращает заранее заданный discovery result.
    /// </summary>
    private sealed class FakeHomeAssistantMcpClient : IHomeAssistantMcpClient
    {
        private readonly HomeAssistantMcpDiscoveryResult _result;

        public FakeHomeAssistantMcpClient(HomeAssistantMcpDiscoveryResult result)
        {
            _result = result;
        }

        public Task<HomeAssistantMcpDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) =>
            Task.FromResult(_result);
    }

    /// <summary>
    /// Что: fake confirmation service для Telegram command tests.
    /// Зачем: handler должен проверяться без реального MCP executor и SQLite confirmation orchestration.
    /// Как: записывает approve/reject calls и возвращает заранее заданный результат.
    /// </summary>
    private sealed class FakeConfirmationService : IConfirmationService
    {
        private readonly ConfirmationDecisionResult _result;

        public FakeConfirmationService(ConfirmationDecisionResult result)
        {
            _result = result;
        }

        public List<(DialogueConversation Conversation, string ConfirmationId)> ApprovedConfirmations { get; } = new();

        public List<(DialogueConversation Conversation, string ConfirmationId)> RejectedConfirmations { get; } = new();

        public Task<ConfirmationProposalResult> ProposeAsync(
            ConfirmationProposalRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new ConfirmationProposalResult(
                IsCreated: false,
                "unused",
                ConfirmationId: null,
                ApproveCommand: null,
                RejectCommand: null,
                ExpiresAtUtc: null));

        public Task<ConfirmationDecisionResult> ApproveAsync(
            DialogueConversation conversation,
            string confirmationId,
            CancellationToken cancellationToken)
        {
            ApprovedConfirmations.Add((conversation, confirmationId));

            return Task.FromResult(_result);
        }

        public Task<ConfirmationDecisionResult> RejectAsync(
            DialogueConversation conversation,
            string confirmationId,
            CancellationToken cancellationToken)
        {
            RejectedConfirmations.Add((conversation, confirmationId));

            return Task.FromResult(_result);
        }
    }

    /// <summary>
    /// Что: fake Telegram adapter для unit-тестов.
    /// Зачем: handler должен проверяться без настоящего bot token, long polling и отправки сообщений в Telegram.
    /// Как: сохраняет отправленные сообщения в список, а polling методы возвращают пустые значения.
    /// </summary>
    private sealed class FakeTelegramBotClientAdapter : ITelegramBotClientAdapter
    {
        public List<(long ChatId, string Text)> SentMessages { get; } = new();

        public Task DeleteWebhookAsync(bool dropPendingUpdates, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<Update>> GetUpdatesAsync(
            int? offset,
            int limit,
            int timeoutSeconds,
            IReadOnlyList<UpdateType> allowedUpdates,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Update>>(Array.Empty<Update>());

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            SentMessages.Add((chatId, text));
            return Task.CompletedTask;
        }
    }
}
