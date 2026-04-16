using HaPersonalAgent.Agent;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Dialogue;
using HaPersonalAgent.Storage;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты transport-agnostic dialogue слоя.
/// Зачем: память диалога должна работать одинаково для Telegram, будущего Web UI и системных notifications.
/// Как: использует временную SQLite базу и fake IAgentRuntime без реальных Telegram или LLM сетевых вызовов.
/// </summary>
public class DialogueServiceTests
{
    [Fact]
    public async Task User_message_is_stored_under_transport_agnostic_conversation_key()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var runtime = new FakeAgentRuntime("hello back");
            var service = CreateService(repository, runtime);
            var conversation = DialogueConversation.Create("web-ui", "session-1", "user-1");

            await service.SendUserMessageAsync(
                DialogueRequest.Create(conversation, "hello", "web-test"),
                CancellationToken.None);

            var stored = await repository.GetConversationMessagesAsync(
                DialogueConversationKey.Create(conversation),
                limit: 10,
                CancellationToken.None);
            var rawEvents = await repository.GetRawEventsAsync(
                DialogueConversationKey.Create(conversation),
                limit: 10,
                CancellationToken.None);

            Assert.Equal("web-test", runtime.Calls.Single().Context.CorrelationId);
            Assert.Equal("web-ui:session-1:user-1", runtime.Calls.Single().Context.ConversationKey);
            Assert.Equal("user-1", runtime.Calls.Single().Context.ParticipantId);
            Assert.Equal(new[] { "hello", "hello back" }, stored.Select(message => message.Text));
            Assert.Equal(
                new[] { DialogueRawEventKinds.UserMessage, DialogueRawEventKinds.AssistantMessage },
                rawEvents.Select(rawEvent => rawEvent.EventKind));
            Assert.Equal(new[] { "hello", "hello back" }, rawEvents.Select(rawEvent => rawEvent.Payload));
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Reset_clears_context_through_conversation_abstraction()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var service = CreateService(repository, new FakeAgentRuntime("unused"));
            var conversation = DialogueConversation.Create("web-ui", "session-1", "user-1");
            await repository.AppendConversationMessagesAsync(
                DialogueConversationKey.Create(conversation),
                new[] { new AgentConversationMessage(AgentConversationRole.User, "clear me", DateTimeOffset.UtcNow) },
                CancellationToken.None);
            var latestMessageId = await repository.GetLatestConversationMessageIdAsync(
                DialogueConversationKey.Create(conversation),
                CancellationToken.None);
            Assert.True(latestMessageId.HasValue);
            await repository.UpsertConversationVectorMemoryAsync(
                new[]
                {
                    new ConversationVectorMemoryEntry(
                        DialogueConversationKey.Create(conversation),
                        latestMessageId!.Value,
                        AgentConversationRole.User,
                        "legacy memory",
                        string.Join(",", Enumerable.Repeat("1", 128)),
                        DateTimeOffset.UtcNow),
                },
                CancellationToken.None);
            await repository.UpsertConversationSummaryAsync(
                new ConversationSummaryMemory(
                    DialogueConversationKey.Create(conversation),
                    "summary-to-clear",
                    DateTimeOffset.UtcNow,
                    latestMessageId!.Value,
                    SummaryVersion: 1),
                CancellationToken.None);
            await repository.UpsertProjectCapsulesAsync(
                new[]
                {
                    new ProjectCapsuleMemory(
                        DialogueConversationKey.Create(conversation),
                        "dog",
                        "Щенок",
                        "## Факты\n- Адаптация идет хорошо.",
                        "conversation",
                        0.77d,
                        SourceEventId: 1,
                        DateTimeOffset.UtcNow,
                        Version: 1),
                },
                CancellationToken.None);
            await repository.UpsertProjectCapsuleExtractionStateAsync(
                new ProjectCapsuleExtractionState(
                    DialogueConversationKey.Create(conversation),
                    LastRawEventId: 2,
                    DateTimeOffset.UtcNow,
                    RunsCount: 1),
                CancellationToken.None);

            await service.ResetAsync(conversation, CancellationToken.None);

            var stored = await repository.GetConversationMessagesAsync(
                DialogueConversationKey.Create(conversation),
                limit: 10,
                CancellationToken.None);
            var summary = await repository.GetConversationSummaryAsync(
                DialogueConversationKey.Create(conversation),
                CancellationToken.None);
            var vectorMemoryCount = await repository.GetConversationVectorMemoryCountAsync(
                DialogueConversationKey.Create(conversation),
                CancellationToken.None);
            var projectCapsuleCount = await repository.GetProjectCapsuleCountAsync(
                DialogueConversationKey.Create(conversation),
                CancellationToken.None);
            var projectCapsuleExtractionState = await repository.GetProjectCapsuleExtractionStateAsync(
                DialogueConversationKey.Create(conversation),
                CancellationToken.None);
            var rawEvents = await repository.GetRawEventsAsync(
                DialogueConversationKey.Create(conversation),
                limit: 10,
                CancellationToken.None);

            Assert.Empty(stored);
            Assert.Null(summary);
            Assert.Equal(0, vectorMemoryCount);
            Assert.Equal(0, projectCapsuleCount);
            Assert.Null(projectCapsuleExtractionState);
            Assert.Single(rawEvents);
            Assert.Equal(DialogueRawEventKinds.ContextReset, rawEvents[0].EventKind);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task System_notification_is_not_stored_as_dialogue_turn()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var runtime = new FakeAgentRuntime("unused");
            var service = CreateService(repository, runtime);
            var conversation = DialogueConversation.Create("telegram", "200", "100");

            await service.RecordSystemNotificationAsync(
                DialogueSystemNotification.Create(
                    conversation,
                    kind: "camera-alert",
                    text: "Motion detected near the garage.",
                    sourceId: "camera.garage"),
                CancellationToken.None);

            var stored = await repository.GetConversationMessagesAsync(
                DialogueConversationKey.Create(conversation),
                limit: 10,
                CancellationToken.None);
            var rawEvents = await repository.GetRawEventsAsync(
                DialogueConversationKey.Create(conversation),
                limit: 10,
                CancellationToken.None);

            Assert.Empty(stored);
            Assert.Empty(runtime.Calls);
            Assert.Single(rawEvents);
            Assert.Equal(DialogueRawEventKinds.SystemNotification, rawEvents[0].EventKind);
            Assert.Equal("Motion detected near the garage.", rawEvents[0].Payload);
            Assert.Equal("camera.garage", rawEvents[0].SourceId);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Compaction_notice_is_visible_to_user_but_not_persisted_in_sql_history()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var runtime = new FakeAgentRuntime(new[]
            {
                "[context-summary] Чтобы удержать бюджет контекста, я сжал раннюю часть диалога (1 summarize step)." + Environment.NewLine + Environment.NewLine + "Текущий ответ.",
                "Продолжаем диалог.",
            });
            var service = CreateService(repository, runtime);
            var conversation = DialogueConversation.Create("telegram", "200", "100");

            var firstResponse = await service.SendUserMessageAsync(
                DialogueRequest.Create(conversation, "Привет", "run-1"),
                CancellationToken.None);

            await service.SendUserMessageAsync(
                DialogueRequest.Create(conversation, "Что дальше?", "run-2"),
                CancellationToken.None);

            var stored = await repository.GetConversationMessagesAsync(
                DialogueConversationKey.Create(conversation),
                limit: 10,
                CancellationToken.None);

            Assert.Equal(4, stored.Count);
            Assert.Equal("Привет", stored[0].Text);
            Assert.Equal(AgentConversationRole.User, stored[0].Role);
            Assert.Equal("Текущий ответ.", stored[1].Text);
            Assert.Equal(AgentConversationRole.Assistant, stored[1].Role);
            Assert.Equal("Что дальше?", stored[2].Text);
            Assert.Equal(AgentConversationRole.User, stored[2].Role);
            Assert.Equal("Продолжаем диалог.", stored[3].Text);
            Assert.Equal(AgentConversationRole.Assistant, stored[3].Role);
            Assert.StartsWith("[context-summary]", firstResponse.Text, StringComparison.Ordinal);

            Assert.Equal(2, runtime.Calls.Count);
            Assert.Equal(2, runtime.Calls[1].Context.ConversationMessages.Count);
            Assert.Equal("Привет", runtime.Calls[1].Context.ConversationMessages[0].Text);
            Assert.Equal("Текущий ответ.", runtime.Calls[1].Context.ConversationMessages[1].Text);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Persisted_summary_candidate_is_saved_and_reused_in_next_runtime_context()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var runtime = new FakeAgentRuntime(new (string Text, string? SummaryCandidate)[]
            {
                ("Первый ответ.", "Persisted summary v1"),
                ("Второй ответ.", null),
            });
            var service = CreateService(repository, runtime);
            var conversation = DialogueConversation.Create("telegram", "200", "100");
            var conversationKey = DialogueConversationKey.Create(conversation);

            await service.SendUserMessageAsync(
                DialogueRequest.Create(conversation, "Первое сообщение", "run-1"),
                CancellationToken.None);

            var summaryAfterFirstRun = await repository.GetConversationSummaryAsync(
                conversationKey,
                CancellationToken.None);
            Assert.NotNull(summaryAfterFirstRun);
            Assert.Equal("Persisted summary v1", summaryAfterFirstRun.Summary);
            Assert.Equal(1, summaryAfterFirstRun.SummaryVersion);

            await service.SendUserMessageAsync(
                DialogueRequest.Create(conversation, "Второе сообщение", "run-2"),
                CancellationToken.None);

            Assert.Equal(2, runtime.Calls.Count);
            Assert.Equal("Persisted summary v1", runtime.Calls[1].Context.PersistedSummary);
            Assert.False(runtime.Calls[1].Context.ShouldRefreshPersistedSummary);
            Assert.Equal(PersistedSummaryRefreshReasons.None, runtime.Calls[1].Context.PersistedSummaryRefreshReason);
            Assert.Equal(0, runtime.Calls[1].Context.MessagesSincePersistedSummary);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Persisted_summary_candidate_replaces_previous_summary_snapshot()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var runtime = new FakeAgentRuntime(new (string Text, string? SummaryCandidate)[]
            {
                ("Первый ответ.", "Собака быстро адаптировалась к дому."),
                ("Второй ответ.", "Пользователь тренирует выдержку на прогулке."),
            });
            var service = CreateService(repository, runtime);
            var conversation = DialogueConversation.Create("telegram", "200", "100");
            var conversationKey = DialogueConversationKey.Create(conversation);

            await service.SendUserMessageAsync(
                DialogueRequest.Create(conversation, "Первое сообщение", "run-1"),
                CancellationToken.None);
            await service.SendUserMessageAsync(
                DialogueRequest.Create(conversation, "Второе сообщение", "run-2"),
                CancellationToken.None);

            var summary = await repository.GetConversationSummaryAsync(
                conversationKey,
                CancellationToken.None);
            Assert.NotNull(summary);
            Assert.Equal(2, summary.SummaryVersion);
            Assert.Equal("Пользователь тренирует выдержку на прогулке.", summary.Summary);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Bounded_history_archives_overflow_and_recalls_relevant_vector_memory()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var runtime = new FakeAgentRuntime(new[]
            {
                "noted one",
                "noted two",
                "noted three",
            });
            var service = CreateService(
                repository,
                runtime,
                new AgentOptions
                {
                    StateDatabasePath = databasePath,
                    ConversationContextMaxTurns = 1,
                });
            var conversation = DialogueConversation.Create("telegram", "200", "100");
            var conversationKey = DialogueConversationKey.Create(conversation);

            await service.SendUserMessageAsync(
                DialogueRequest.Create(conversation, "alpha likes tea", "run-1"),
                CancellationToken.None);
            await service.SendUserMessageAsync(
                DialogueRequest.Create(conversation, "beta likes coffee", "run-2"),
                CancellationToken.None);
            await service.SendUserMessageAsync(
                DialogueRequest.Create(conversation, "remind me: alpha likes tea?", "run-3"),
                CancellationToken.None);

            Assert.Equal(3, runtime.Calls.Count);
            Assert.Equal(2, runtime.Calls[2].Context.ConversationMessages.Count);
            Assert.True(runtime.Calls[2].Context.RetrievedMemoryCount > 0);
            Assert.Contains("alpha likes tea", runtime.Calls[2].Context.RetrievedMemoryContext, StringComparison.OrdinalIgnoreCase);

            var storedMessages = await repository.GetConversationMessagesAsync(
                conversationKey,
                10,
                CancellationToken.None);
            var vectorMemoryCount = await repository.GetConversationVectorMemoryCountAsync(
                conversationKey,
                CancellationToken.None);

            Assert.Equal(2, storedMessages.Count);
            Assert.Equal(new[] { "remind me: alpha likes tea?", "noted three" }, storedMessages.Select(message => message.Text));
            Assert.True(vectorMemoryCount >= 2);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task On_demand_retrieval_mode_does_not_auto_inject_vector_memories_into_runtime_context()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var runtime = new FakeAgentRuntime(new[]
            {
                "noted one",
                "noted two",
                "noted three",
            });
            var service = CreateService(
                repository,
                runtime,
                new AgentOptions
                {
                    StateDatabasePath = databasePath,
                    ConversationContextMaxTurns = 1,
                    MemoryRetrievalMode = AgentOptions.MemoryRetrievalModeOnDemandTool,
                });
            var conversation = DialogueConversation.Create("telegram", "200", "100");

            await service.SendUserMessageAsync(
                DialogueRequest.Create(conversation, "alpha likes tea", "run-1"),
                CancellationToken.None);
            await service.SendUserMessageAsync(
                DialogueRequest.Create(conversation, "beta likes coffee", "run-2"),
                CancellationToken.None);
            await service.SendUserMessageAsync(
                DialogueRequest.Create(conversation, "remind me: alpha likes tea?", "run-3"),
                CancellationToken.None);

            Assert.Equal(3, runtime.Calls.Count);
            Assert.Equal(2, runtime.Calls[2].Context.ConversationMessages.Count);
            Assert.Equal(AgentOptions.MemoryRetrievalModeOnDemandTool, runtime.Calls[2].Context.MemoryRetrievalMode);
            Assert.DoesNotContain("alpha likes tea", runtime.Calls[2].Context.RetrievedMemoryContext ?? string.Empty, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(0, runtime.Calls[2].Context.RetrievedMemoryCount);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Project_capsules_are_injected_into_runtime_memory_context()
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
                        "construction",
                        "Стройка",
                        "## Статус\n- Выбираем доски для каркаса.",
                        "conversation",
                        0.84d,
                        SourceEventId: 3,
                        DateTimeOffset.UtcNow,
                        Version: 1),
                },
                CancellationToken.None);
            var runtime = new FakeAgentRuntime("ok");
            var service = CreateService(repository, runtime);
            var conversation = DialogueConversation.Create("telegram", "200", "100");

            await service.SendUserMessageAsync(
                DialogueRequest.Create(conversation, "что по стройке?", "run-capsule"),
                CancellationToken.None);

            Assert.Single(runtime.Calls);
            Assert.Contains("project capsules", runtime.Calls[0].Context.RetrievedMemoryContext, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("Стройка", runtime.Calls[0].Context.RetrievedMemoryContext, StringComparison.Ordinal);
            Assert.True(runtime.Calls[0].Context.RetrievedMemoryCount > 0);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Context_snapshot_reports_message_counts_and_summary_numbers()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var service = CreateService(repository, new FakeAgentRuntime("unused"));
            var conversation = DialogueConversation.Create("telegram", "200", "100");
            var key = DialogueConversationKey.Create(conversation);
            var now = DateTimeOffset.UtcNow;

            await repository.AppendConversationMessagesAsync(
                key,
                new[]
                {
                    new AgentConversationMessage(AgentConversationRole.User, "m1", now),
                    new AgentConversationMessage(AgentConversationRole.Assistant, "m2", now),
                    new AgentConversationMessage(AgentConversationRole.User, "m3", now),
                },
                CancellationToken.None);

            var latestMessageId = await repository.GetLatestConversationMessageIdAsync(
                key,
                CancellationToken.None);
            Assert.True(latestMessageId.HasValue);
            await repository.UpsertConversationSummaryAsync(
                new ConversationSummaryMemory(
                    key,
                    "summary-1",
                    now,
                    SourceLastMessageId: 1,
                    SummaryVersion: 2),
                CancellationToken.None);

            var snapshot = await service.GetContextSnapshotAsync(conversation, CancellationToken.None);

            Assert.Equal(key, snapshot.ConversationKey);
            Assert.Equal(3, snapshot.StoredMessageCount);
            Assert.Equal(0, snapshot.RawEventCount);
            Assert.Equal(0, snapshot.VectorMemoryCount);
            Assert.Equal(0, snapshot.ProjectCapsuleCount);
            Assert.Equal(0, snapshot.ProjectCapsuleLatestSourceEventId);
            Assert.Null(snapshot.ProjectCapsuleLastUpdatedAtUtc);
            Assert.Equal(0, snapshot.ProjectCapsuleLastProcessedRawEventId);
            Assert.Null(snapshot.ProjectCapsuleLastExtractionAtUtc);
            Assert.Equal(0, snapshot.ProjectCapsuleExtractionRunsCount);
            Assert.Equal(AgentOptions.MemoryRetrievalModeBeforeInvoke, snapshot.MemoryRetrievalMode);
            Assert.True(snapshot.MemoryRetrievalBeforeInvokeEnabled);
            Assert.False(snapshot.MemoryRetrievalOnDemandToolEnabled);
            Assert.Equal(24, snapshot.MaxContextMessages);
            Assert.Equal(3, snapshot.LoadedHistoryMessageCount);
            Assert.False(snapshot.PersistedSummaryRefreshSuggested);
            Assert.Equal(PersistedSummaryRefreshReasons.None, snapshot.PersistedSummaryRefreshReason);
            Assert.Equal(12, snapshot.PersistedSummaryRefreshThreshold);
            Assert.True(snapshot.PersistedSummaryPresent);
            Assert.Equal("summary-1".Length, snapshot.PersistedSummaryLength);
            Assert.Equal(2, snapshot.PersistedSummaryVersion);
            Assert.Equal(1, snapshot.PersistedSummarySourceLastMessageId);
            Assert.False(snapshot.PersistedSummaryStructuredContract);
            Assert.Equal(0, snapshot.PersistedSummaryFactsCount);
            Assert.Equal(0, snapshot.PersistedSummaryConflictsCount);
            Assert.Equal(1d, snapshot.PersistedSummaryHistoryToSummaryCompressionRatio);
            Assert.Equal(latestMessageId.Value - 1, snapshot.MessagesSincePersistedSummary);
            Assert.Equal(30, snapshot.EstimatedContextTokenCount);
            Assert.Equal(3, snapshot.EstimatedHistoryTokenCount);
            Assert.Equal(3, snapshot.EstimatedPersistedSummaryTokenCount);
            Assert.Equal(0, snapshot.EstimatedProjectCapsuleTokenCount);
            Assert.Equal(24, snapshot.EstimatedMessageScaffoldingTokenCount);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Persisted_summary_refresh_is_requested_after_enough_new_messages()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var runtime = new FakeAgentRuntime("ok");
            var service = CreateService(repository, runtime);
            var conversation = DialogueConversation.Create("telegram", "200", "100");
            var key = DialogueConversationKey.Create(conversation);
            var now = DateTimeOffset.UtcNow;

            var seedMessages = Enumerable.Range(1, 14)
                .Select(index => new AgentConversationMessage(
                    index % 2 == 0 ? AgentConversationRole.Assistant : AgentConversationRole.User,
                    $"m{index}",
                    now))
                .ToArray();
            await repository.AppendConversationMessagesAsync(
                key,
                seedMessages,
                CancellationToken.None);
            await repository.UpsertConversationSummaryAsync(
                new ConversationSummaryMemory(
                    key,
                    "stale-summary",
                    now,
                    SourceLastMessageId: 1,
                    SummaryVersion: 1),
                CancellationToken.None);

            await service.SendUserMessageAsync(
                DialogueRequest.Create(conversation, "новый вопрос", "run-refresh"),
                CancellationToken.None);

            Assert.Single(runtime.Calls);
            Assert.True(runtime.Calls[0].Context.ShouldRefreshPersistedSummary);
            Assert.Equal(PersistedSummaryRefreshReasons.Threshold, runtime.Calls[0].Context.PersistedSummaryRefreshReason);
            Assert.True(runtime.Calls[0].Context.MessagesSincePersistedSummary >= 12);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Refresh_persisted_summary_forces_runtime_refresh_without_storing_new_turns()
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
                    new AgentConversationMessage(AgentConversationRole.User, "Привет", now),
                    new AgentConversationMessage(AgentConversationRole.Assistant, "Здравствуйте", now),
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

            var runtime = new FakeAgentRuntime(new (string Text, string? SummaryCandidate)[]
            {
                ("service response", "новый summary"),
            });
            var service = CreateService(repository, runtime);
            var conversation = DialogueConversation.Create("telegram", "200", "100");

            var result = await service.RefreshPersistedSummaryAsync(
                conversation,
                correlationId: "refresh-1",
                CancellationToken.None);

            var stored = await repository.GetConversationMessagesAsync(
                "telegram:200:100",
                10,
                CancellationToken.None);
            var summary = await repository.GetConversationSummaryAsync(
                "telegram:200:100",
                CancellationToken.None);

            Assert.Single(runtime.Calls);
            Assert.Equal(LlmExecutionProfile.Summarization, runtime.Calls[0].Context.ExecutionProfile);
            Assert.True(runtime.Calls[0].Context.ShouldRefreshPersistedSummary);
            Assert.Equal(PersistedSummaryRefreshReasons.Manual, runtime.Calls[0].Context.PersistedSummaryRefreshReason);
            Assert.True(runtime.Calls[0].Context.ForcePersistedSummaryRefresh);
            Assert.Equal(2, stored.Count);
            Assert.NotNull(summary);
            Assert.Equal("новый summary", summary.Summary);
            Assert.Equal(2, summary.SummaryVersion);
            Assert.True(result.IsConfigured);
            Assert.True(result.IsUpdated);
            Assert.NotNull(result.Summary);
            Assert.Equal("новый summary", result.Summary!.Summary);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    [Fact]
    public async Task Refresh_persisted_summary_returns_noop_when_history_is_empty()
    {
        var databasePath = CreateTemporaryDatabasePath();

        try
        {
            var repository = CreateRepository(databasePath);
            var runtime = new FakeAgentRuntime("unused");
            var service = CreateService(repository, runtime);
            var conversation = DialogueConversation.Create("telegram", "200", "100");

            var result = await service.RefreshPersistedSummaryAsync(
                conversation,
                correlationId: "refresh-empty",
                CancellationToken.None);

            Assert.Empty(runtime.Calls);
            Assert.True(result.IsConfigured);
            Assert.False(result.IsUpdated);
            Assert.Contains("нет истории", result.Message, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            DeleteTemporaryDatabaseDirectory(databasePath);
        }
    }

    private static DialogueService CreateService(
        AgentStateRepository repository,
        FakeAgentRuntime runtime,
        AgentOptions? agentOptions = null)
    {
        var loggerFactory = LoggerFactory.Create(_ => { });
        var boundedProvider = new BoundedChatHistoryProvider(
            repository,
            loggerFactory.CreateLogger<BoundedChatHistoryProvider>());
        var projectCapsuleService = new ProjectCapsuleService(
            runtime,
            Options.Create(agentOptions ?? new AgentOptions()),
            repository,
            loggerFactory.CreateLogger<ProjectCapsuleService>());

        return new DialogueService(
            runtime,
            Options.Create(agentOptions ?? new AgentOptions()),
            boundedProvider,
            projectCapsuleService,
            repository,
            loggerFactory.CreateLogger<DialogueService>());
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
    /// Что: fake agent runtime для тестов DialogueService.
    /// Зачем: тесты проверяют memory orchestration без сетевых вызовов к LLM provider.
    /// Как: сохраняет вызовы SendAsync и возвращает заранее заданный ответ.
    /// </summary>
    private sealed class FakeAgentRuntime : IAgentRuntime
    {
        private readonly Queue<(string Text, string? SummaryCandidate)> _responseQueue;

        public FakeAgentRuntime(string responseText)
            : this(new[] { responseText })
        {
        }

        public FakeAgentRuntime(IEnumerable<string> responseTexts)
            : this(responseTexts.Select(text => (Text: text, SummaryCandidate: (string?)null)))
        {
        }

        public FakeAgentRuntime(IEnumerable<(string Text, string? SummaryCandidate)> responseEntries)
        {
            _responseQueue = new Queue<(string Text, string? SummaryCandidate)>(responseEntries);
        }

        public List<(string Message, AgentContext Context)> Calls { get; } = new();

        public AgentRuntimeHealth GetHealth() =>
            AgentRuntimeHealth.Configured(new LlmOptions());

        public Task<AgentRuntimeResponse> SendAsync(
            string message,
            AgentContext context,
            Func<AgentRuntimeReasoningUpdate, CancellationToken, Task>? onReasoningUpdate,
            CancellationToken cancellationToken)
        {
            Calls.Add((message, context));
            if (_responseQueue.Count == 0)
            {
                throw new InvalidOperationException("No fake runtime responses left in queue.");
            }
            var responseEntry = _responseQueue.Dequeue();

            return Task.FromResult(new AgentRuntimeResponse(
                context.CorrelationId,
                IsConfigured: true,
                responseEntry.Text,
                GetHealth(),
                responseEntry.SummaryCandidate));
        }
    }
}
