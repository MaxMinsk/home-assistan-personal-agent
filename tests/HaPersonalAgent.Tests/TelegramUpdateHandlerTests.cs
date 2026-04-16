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
            Assert.True(adapter.SentTypingActions.Count >= 2);
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
    public async Task Show_capsules_returns_project_capsules_for_current_conversation()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            await repository.UpsertProjectCapsulesAsync(
                new[]
                {
                    new ProjectCapsuleMemory(
                        "telegram:200:100",
                        "dog",
                        "Щенок",
                        "## Факты\n- Собака живет дома.",
                        "conversation",
                        0.86d,
                        SourceEventId: 15,
                        DateTimeOffset.UtcNow,
                        Version: 2),
                },
                CancellationToken.None);
            var runtime = new FakeAgentRuntime("unused");
            var handler = CreateHandler(repository, runtime);
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 29, chatId: 200, userId: 100, text: "/showCapsules"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            Assert.Empty(runtime.Calls);
            Assert.Single(adapter.SentMessages);
            Assert.Contains("Project capsules:", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains("[dog] Щенок", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains("sourceEventId=15", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Show_vector_returns_vector_memory_for_current_conversation()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            await repository.UpsertConversationVectorMemoryAsync(
                new[]
                {
                    new ConversationVectorMemoryEntry(
                        "telegram:200:100",
                        SourceMessageId: 12,
                        AgentConversationRole.User,
                        "Кодовое слово KESTREL-917.",
                        "0.1,0.2,0.3,0.4",
                        DateTimeOffset.UtcNow),
                },
                CancellationToken.None);
            var runtime = new FakeAgentRuntime("unused");
            var handler = CreateHandler(repository, runtime);
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 290, chatId: 200, userId: 100, text: "/showVector"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            Assert.Empty(runtime.Calls);
            Assert.Single(adapter.SentMessages);
            Assert.Contains("Vector memory:", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains("sourceMessageId=12", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains("embeddingDims=4", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains("KESTREL-917", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Show_vector_returns_empty_message_when_vector_memory_is_missing()
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
                CreateTextUpdate(updateId: 291, chatId: 200, userId: 100, text: "/showVector 5"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            Assert.Empty(runtime.Calls);
            Assert.Single(adapter.SentMessages);
            Assert.Contains("Vector memory", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains("отсутствует", adapter.SentMessages.Single().Text, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Refresh_capsules_extracts_capsules_from_raw_events_without_regular_dialogue_turn()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            await repository.AppendRawEventsAsync(
                new[]
                {
                    RawEventEntry.Create(
                        "telegram:200:100",
                        "telegram",
                        "200",
                        "100",
                        DialogueRawEventKinds.UserMessage,
                        "Мы строим сарай и выбираем доски."),
                },
                CancellationToken.None);
            var runtime = new FakeAgentRuntime(
                """
                {
                  "capsules": [
                    {
                      "key": "construction",
                      "title": "Стройка",
                      "contentMarkdown": "## Статус\n- Идет выбор досок.",
                      "scope": "conversation",
                      "confidence": 0.91,
                      "sourceEventId": 1
                    }
                  ]
                }
                """);
            var handler = CreateHandler(repository, runtime);
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 30, chatId: 200, userId: 100, text: "/refreshCapsules"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            Assert.Single(runtime.Calls);
            Assert.Equal(LlmExecutionProfile.Summarization, runtime.Calls[0].Context.ExecutionProfile);
            Assert.Empty(runtime.Calls[0].Context.ConversationMessages);
            Assert.Contains("refresh project capsules", runtime.Calls[0].Message, StringComparison.OrdinalIgnoreCase);
            Assert.Single(adapter.SentMessages);
            Assert.Contains("Capsules total: 1", adapter.SentMessages.Single().Text, StringComparison.Ordinal);

            var capsules = await repository.GetProjectCapsulesAsync(
                "telegram:200:100",
                limit: 10,
                CancellationToken.None);
            Assert.Single(capsules);
            Assert.Equal("construction", capsules[0].CapsuleKey);
            Assert.Equal(1, capsules[0].SourceEventId);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Show_raw_events_returns_recent_events_for_current_conversation()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            await repository.AppendRawEventsAsync(
                new[]
                {
                    RawEventEntry.Create(
                        "telegram:200:100",
                        "telegram",
                        "200",
                        "100",
                        DialogueRawEventKinds.UserMessage,
                        "Первый запрос",
                        correlationId: "c-1"),
                    RawEventEntry.Create(
                        "telegram:200:100",
                        "telegram",
                        "200",
                        "100",
                        DialogueRawEventKinds.AssistantMessage,
                        "Второй ответ",
                        correlationId: "c-1"),
                    RawEventEntry.Create(
                        "telegram:201:100",
                        "telegram",
                        "201",
                        "100",
                        DialogueRawEventKinds.UserMessage,
                        "чужой чат"),
                },
                CancellationToken.None);
            var runtime = new FakeAgentRuntime("unused");
            var handler = CreateHandler(repository, runtime);
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 23, chatId: 200, userId: 100, text: "/showRawEvents 2"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            Assert.Empty(runtime.Calls);
            Assert.Single(adapter.SentMessages);
            Assert.Contains("Raw events: последние 2", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains(DialogueRawEventKinds.UserMessage, adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains(DialogueRawEventKinds.AssistantMessage, adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.DoesNotContain("чужой чат", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Show_raw_events_returns_empty_message_when_no_events_exist()
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
                CreateTextUpdate(updateId: 24, chatId: 200, userId: 100, text: "/showRawEvents"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            Assert.Empty(runtime.Calls);
            Assert.Single(adapter.SentMessages);
            Assert.Contains("Raw events", adapter.SentMessages.Single().Text, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("отсутствуют", adapter.SentMessages.Single().Text, StringComparison.OrdinalIgnoreCase);
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
            Assert.True(adapter.SentTypingActions.Count >= 1);
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
            Assert.Contains("RawEvents(stored): 0 events", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains("VectorMemory(stored): 0 entries", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains("MemoryRetrieval: mode before_invoke, before-invoke True, on-demand-tool False", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains("ProjectCapsules(stored): 0 entries", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains("Context(loaded): 0 / 24 messages", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains("Context(tokens~): 0 (history 0, summary 0, capsules 0, scaffolding 0; heuristic UTF8 bytes/4)", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Contains("PersistedSummary: present False", adapter.SentMessages.Single().Text, StringComparison.Ordinal);
            Assert.Empty(adapter.SentTypingActions);
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
    public async Task Reasoning_preview_is_sent_and_deleted_for_long_running_reasoning_stream()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var runtime = new FakeAgentRuntime("финальный ответ")
            {
                ReasoningUpdateDelayMs = 1100,
            };
            runtime.ReasoningUpdatesToEmit.Add("Проверяю входные данные.");
            runtime.ReasoningUpdatesToEmit.Add("Собираю итог.");
            var handler = CreateHandler(CreateRepository(databasePath), runtime);
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 41, chatId: 200, userId: 100, text: "долго думай"),
                new TelegramOptions
                {
                    AllowedUserIds = new long[] { 100 },
                    ReasoningPreviewEnabled = true,
                    ReasoningPreviewDelaySeconds = 1,
                },
                CancellationToken.None);

            Assert.NotEmpty(adapter.SentMessagesWithIds);
            var previewMessage = adapter.SentMessagesWithIds[0];
            Assert.True(
                previewMessage.Text.Contains("Промежуточные рассуждения", StringComparison.Ordinal)
                || previewMessage.Text.Contains("Думаю над ответом", StringComparison.Ordinal));
            Assert.Contains(
                adapter.DeletedMessages,
                item => item.ChatId == previewMessage.ChatId && item.MessageId == previewMessage.MessageId);
            Assert.Equal("финальный ответ", adapter.SentMessages.Last().Text);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Reasoning_preview_is_not_sent_when_option_is_disabled()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var runtime = new FakeAgentRuntime("финальный ответ")
            {
                ReasoningUpdateDelayMs = 0,
            };
            runtime.ReasoningUpdatesToEmit.Add("Внутренние рассуждения.");
            var handler = CreateHandler(CreateRepository(databasePath), runtime);
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 42, chatId: 200, userId: 100, text: "ответь"),
                new TelegramOptions
                {
                    AllowedUserIds = new long[] { 100 },
                    ReasoningPreviewEnabled = false,
                    ReasoningPreviewDelaySeconds = 1,
                },
                CancellationToken.None);

            Assert.Empty(adapter.SentMessagesWithIds);
            Assert.Empty(adapter.DeletedMessages);
            Assert.Equal("финальный ответ", adapter.SentMessages.Single().Text);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Reasoning_preview_fallback_is_sent_for_long_response_without_reasoning_updates()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var runtime = new FakeAgentRuntime("финальный ответ")
            {
                ResponseDelayMs = 1300,
            };
            var handler = CreateHandler(CreateRepository(databasePath), runtime);
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 420, chatId: 200, userId: 100, text: "долго отвечай"),
                new TelegramOptions
                {
                    AllowedUserIds = new long[] { 100 },
                    ReasoningPreviewEnabled = true,
                    ReasoningPreviewDelaySeconds = 1,
                },
                CancellationToken.None);

            Assert.NotEmpty(adapter.SentMessagesWithIds);
            var previewMessage = adapter.SentMessagesWithIds[0];
            Assert.Contains("Думаю над ответом", previewMessage.Text, StringComparison.Ordinal);
            Assert.Contains(
                adapter.DeletedMessages,
                item => item.ChatId == previewMessage.ChatId && item.MessageId == previewMessage.MessageId);
            Assert.Equal("финальный ответ", adapter.SentMessages.Last().Text);
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
    public async Task Confirmation_prompt_from_agent_is_sent_with_inline_buttons()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var runtime = new FakeAgentRuntime(
                """
                Нужно подтверждение действия abc12345.
                Тип: home_assistant_mcp
                Действие: Тест
                Риск: Тестовый риск
                Подтвердить: /approve abc12345
                Отклонить: /reject abc12345
                Истекает: 2026-04-15 14:00:00 UTC
                """);
            var handler = CreateHandler(CreateRepository(databasePath), runtime);
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 31, chatId: 200, userId: 100, text: "включи свет"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            Assert.Single(runtime.Calls);
            Assert.Single(adapter.SentConfirmationMessages);
            Assert.Equal(200, adapter.SentConfirmationMessages[0].ChatId);
            Assert.Equal("abc12345", adapter.SentConfirmationMessages[0].ConfirmationId);
            Assert.Contains("/approve abc12345", adapter.SentConfirmationMessages[0].Text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Confirmation_prompt_with_only_approve_command_is_sent_with_inline_buttons()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var runtime = new FakeAgentRuntime(
                """
                Капсула обновлена и ждёт подтверждения.
                Подтверди: /approve a54434b8
                """);
            var handler = CreateHandler(CreateRepository(databasePath), runtime);
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 310, chatId: 200, userId: 100, text: "исправь капсулу"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            Assert.Single(runtime.Calls);
            Assert.Single(adapter.SentConfirmationMessages);
            Assert.Equal(200, adapter.SentConfirmationMessages[0].ChatId);
            Assert.Equal("a54434b8", adapter.SentConfirmationMessages[0].ConfirmationId);
            Assert.Contains("/approve a54434b8", adapter.SentConfirmationMessages[0].Text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Runtime_confirmation_id_overrides_model_confirmation_id_in_telegram_prompt()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var runtime = new FakeAgentRuntime(
                """
                Капсула обновлена и ждёт подтверждения.
                Подтверди: /approve wrong1234
                """);
            var confirmationService = new FakeConfirmationService(
                new ConfirmationDecisionResult(
                    ConfirmationDecisionOutcome.Completed,
                    IsSuccess: true,
                    "unused",
                    "unused"))
            {
                LatestPendingConfirmationId = "real5678",
            };
            var handler = CreateHandler(
                CreateRepository(databasePath),
                runtime,
                confirmationService: confirmationService);
            var adapter = new FakeTelegramBotClientAdapter();

            await handler.HandleAsync(
                adapter,
                CreateTextUpdate(updateId: 311, chatId: 200, userId: 100, text: "обнови капсулу"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            Assert.Single(confirmationService.LatestPendingLookupRequests);
            Assert.Equal("telegram-311", confirmationService.LatestPendingLookupRequests[0].CorrelationId);
            Assert.Single(adapter.SentConfirmationMessages);
            Assert.Equal("real5678", adapter.SentConfirmationMessages[0].ConfirmationId);
            Assert.Contains("/approve real5678", adapter.SentConfirmationMessages[0].Text, StringComparison.Ordinal);
            Assert.DoesNotContain("/approve wrong1234", adapter.SentConfirmationMessages[0].Text, StringComparison.Ordinal);
            Assert.Contains("/reject real5678", adapter.SentConfirmationMessages[0].Text, StringComparison.Ordinal);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Callback_approve_routes_to_confirmation_service_and_answers_callback()
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
                CreateCallbackUpdate(
                    updateId: 32,
                    callbackQueryId: "cb-1",
                    chatId: 200,
                    userId: 100,
                    callbackData: "confirm:approve:abc12345"),
                new TelegramOptions { AllowedUserIds = new long[] { 100 } },
                CancellationToken.None);

            Assert.Empty(runtime.Calls);
            Assert.Single(confirmationService.ApprovedConfirmations);
            Assert.Equal("abc12345", confirmationService.ApprovedConfirmations[0].ConfirmationId);
            Assert.Single(adapter.ClearedInlineKeyboards);
            Assert.Equal(200, adapter.ClearedInlineKeyboards[0].ChatId);
            Assert.Single(adapter.AnsweredCallbackQueries);
            Assert.Equal("cb-1", adapter.AnsweredCallbackQueries[0].CallbackQueryId);
            Assert.Contains("Подтверждаю", adapter.AnsweredCallbackQueries[0].Text ?? string.Empty, StringComparison.Ordinal);
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
        var boundedProvider = new BoundedChatHistoryProvider(
            repository,
            loggerFactory.CreateLogger<BoundedChatHistoryProvider>());
        var agentOptions = Options.Create(new AgentOptions());
        var projectCapsuleService = new ProjectCapsuleService(
            runtime,
            agentOptions,
            repository,
            loggerFactory.CreateLogger<ProjectCapsuleService>());
        var dialogueService = new DialogueService(
            runtime,
            agentOptions,
            boundedProvider,
            projectCapsuleService,
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

    private static Update CreateCallbackUpdate(
        int updateId,
        string callbackQueryId,
        long chatId,
        long userId,
        string callbackData) =>
        new()
        {
            Id = updateId,
            CallbackQuery = new CallbackQuery
            {
                Id = callbackQueryId,
                Data = callbackData,
                From = new User
                {
                    Id = userId,
                    IsBot = false,
                    FirstName = "Test",
                },
                Message = new Message
                {
                    Id = 1,
                    Date = DateTime.UtcNow,
                    Chat = new Chat
                    {
                        Id = chatId,
                        Type = ChatType.Private,
                    },
                },
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

        public List<string> ReasoningUpdatesToEmit { get; } = new();

        public int ReasoningUpdateDelayMs { get; set; }

        public int ResponseDelayMs { get; set; }

        public List<(string Message, AgentContext Context)> Calls { get; } = new();

        public AgentRuntimeHealth GetHealth() =>
            AgentRuntimeHealth.Configured(new LlmOptions());

        public Task<AgentRuntimeResponse> SendAsync(
            string message,
            AgentContext context,
            Func<AgentRuntimeReasoningUpdate, CancellationToken, Task>? onReasoningUpdate,
            CancellationToken cancellationToken)
        {
            if (ExceptionToThrow is not null)
            {
                throw ExceptionToThrow;
            }

            Calls.Add((message, context));

            return SendAsyncCore(
                context,
                onReasoningUpdate,
                cancellationToken);
        }

        private async Task<AgentRuntimeResponse> SendAsyncCore(
            AgentContext context,
            Func<AgentRuntimeReasoningUpdate, CancellationToken, Task>? onReasoningUpdate,
            CancellationToken cancellationToken)
        {
            if (onReasoningUpdate is not null)
            {
                foreach (var reasoningUpdate in ReasoningUpdatesToEmit)
                {
                    if (ReasoningUpdateDelayMs > 0)
                    {
                        await Task.Delay(ReasoningUpdateDelayMs, cancellationToken);
                    }

                    await onReasoningUpdate(
                        new AgentRuntimeReasoningUpdate(context.CorrelationId, reasoningUpdate),
                        cancellationToken);
                }
            }

            if (ResponseDelayMs > 0)
            {
                await Task.Delay(ResponseDelayMs, cancellationToken);
            }

            return new AgentRuntimeResponse(
                context.CorrelationId,
                IsConfigured: true,
                NextResponseText,
                GetHealth(),
                NextSummaryCandidate);
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

        public List<(DialogueConversation Conversation, string CorrelationId)> LatestPendingLookupRequests { get; } = new();

        public string? LatestPendingConfirmationId { get; set; }

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

        public Task<string?> GetLatestPendingConfirmationIdAsync(
            DialogueConversation conversation,
            string correlationId,
            CancellationToken cancellationToken)
        {
            LatestPendingLookupRequests.Add((conversation, correlationId));
            return Task.FromResult(LatestPendingConfirmationId);
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
        public List<(long ChatId, string Text, string ConfirmationId)> SentConfirmationMessages { get; } = new();
        public List<long> SentTypingActions { get; } = new();
        public List<(long ChatId, int MessageId, string Text)> SentMessagesWithIds { get; } = new();
        public List<(long ChatId, int MessageId, string Text)> EditedMessages { get; } = new();
        public List<(long ChatId, int MessageId)> DeletedMessages { get; } = new();
        public List<(long ChatId, int MessageId)> ClearedInlineKeyboards { get; } = new();
        public List<(string CallbackQueryId, string? Text, bool ShowAlert)> AnsweredCallbackQueries { get; } = new();
        public List<IReadOnlyList<(string Command, string Description)>> ConfiguredCommands { get; } = new();
        private int _nextMessageId = 10;

        public Task DeleteWebhookAsync(bool dropPendingUpdates, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<Update>> GetUpdatesAsync(
            int? offset,
            int limit,
            int timeoutSeconds,
            IReadOnlyList<UpdateType> allowedUpdates,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<Update>>(Array.Empty<Update>());

        public Task SendTypingAsync(long chatId, CancellationToken cancellationToken)
        {
            SentTypingActions.Add(chatId);
            return Task.CompletedTask;
        }

        public Task SendMessageAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            SentMessages.Add((chatId, text));
            return Task.CompletedTask;
        }

        public Task<int> SendMessageWithIdAsync(long chatId, string text, CancellationToken cancellationToken)
        {
            var messageId = Interlocked.Increment(ref _nextMessageId);
            SentMessagesWithIds.Add((chatId, messageId, text));
            return Task.FromResult(messageId);
        }

        public Task EditMessageTextAsync(
            long chatId,
            int messageId,
            string text,
            CancellationToken cancellationToken)
        {
            EditedMessages.Add((chatId, messageId, text));
            return Task.CompletedTask;
        }

        public Task DeleteMessageAsync(long chatId, int messageId, CancellationToken cancellationToken)
        {
            DeletedMessages.Add((chatId, messageId));
            return Task.CompletedTask;
        }

        public Task SetCommandsAsync(
            IReadOnlyList<(string Command, string Description)> commands,
            CancellationToken cancellationToken)
        {
            ConfiguredCommands.Add(commands);
            return Task.CompletedTask;
        }

        public Task SendConfirmationMessageAsync(
            long chatId,
            string text,
            string confirmationId,
            CancellationToken cancellationToken)
        {
            SentConfirmationMessages.Add((chatId, text, confirmationId));
            SentMessages.Add((chatId, text));
            return Task.CompletedTask;
        }

        public Task ClearInlineKeyboardAsync(
            long chatId,
            int messageId,
            CancellationToken cancellationToken)
        {
            ClearedInlineKeyboards.Add((chatId, messageId));
            return Task.CompletedTask;
        }

        public Task AnswerCallbackQueryAsync(
            string callbackQueryId,
            string? text,
            bool showAlert,
            CancellationToken cancellationToken)
        {
            AnsweredCallbackQueries.Add((callbackQueryId, text, showAlert));
            return Task.CompletedTask;
        }
    }
}
