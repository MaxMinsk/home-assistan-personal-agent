using System.Net;
using HaPersonalAgent.Agent;
using HaPersonalAgent.Autonomous;
using HaPersonalAgent.Configuration;
using HaPersonalAgent.Search;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты веб-поиска (HPA-034) и того, что галочки агента реально ограничивают инструменты.
/// Зачем: разбор ответа провайдера и границы доступа — контракт, который нельзя проверять руками на каждом релизе.
/// Как: разбор Brave-ответа без сети + проверка, что исполнитель выводит AgentToolPolicy из настроек конкретного агента.
/// </summary>
public class WebSearchTests
{
    [Fact]
    public void Brave_response_is_parsed_into_results()
    {
        const string payload = """
            {
              "web": {
                "results": [
                  {
                    "title": "Первый результат",
                    "url": "https://example.com/a",
                    "description": "Текст со <strong>подсветкой</strong> совпадения",
                    "page_age": "2026-07-01"
                  },
                  {
                    "title": "Второй результат",
                    "url": "https://example.com/b",
                    "description": "Обычное описание"
                  }
                ]
              }
            }
            """;

        var results = BraveWebSearchProvider.ParseResults(payload, limit: 10);

        Assert.Equal(2, results.Count);
        Assert.Equal("Первый результат", results[0].Title);
        Assert.Equal("https://example.com/a", results[0].Url);
        // Подсветку Brave отдаёт HTML-тегами — модели они не нужны.
        Assert.Equal("Текст со подсветкой совпадения", results[0].Description);
        Assert.Equal("2026-07-01", results[0].Age);
    }

    [Fact]
    public void Entries_without_url_or_title_are_skipped_and_the_limit_is_respected()
    {
        const string payload = """
            {
              "web": {
                "results": [
                  { "title": "ok", "url": "https://example.com/1", "description": "d" },
                  { "title": "no url", "description": "d" },
                  { "url": "https://example.com/3", "description": "no title" },
                  { "title": "ok2", "url": "https://example.com/4", "description": "d" }
                ]
              }
            }
            """;

        Assert.Equal(2, BraveWebSearchProvider.ParseResults(payload, limit: 10).Count);
        Assert.Single(BraveWebSearchProvider.ParseResults(payload, limit: 1));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json at all")]
    [InlineData("""{"unexpected":"shape"}""")]
    public void Malformed_payloads_yield_no_results_instead_of_throwing(string payload)
    {
        Assert.Empty(BraveWebSearchProvider.ParseResults(payload, limit: 5));
    }

    [Fact]
    public void Search_is_unconfigured_without_an_api_key()
    {
        Assert.False(new WebSearchOptions().IsConfigured);
        Assert.True(new WebSearchOptions { ApiKey = "key" }.IsConfigured);
        Assert.True(WebSearchOptions.IsBrave(null));
        Assert.True(WebSearchOptions.IsBrave("brave"));
        Assert.False(WebSearchOptions.IsBrave("tavily"));
    }

    [Fact]
    public async Task Brave_422_with_a_country_retries_once_without_the_country_filter()
    {
        // Регрессия из живого лога: q=Mestprom Minsk&country=BY -> 422 (BY не в списке стран Brave).
        var handler = new SequenceHandler(
            (HttpStatusCode.UnprocessableEntity, string.Empty),
            (HttpStatusCode.OK, """{"web":{"results":[{"title":"t","url":"https://example.com","description":"d"}]}}"""));
        var provider = new BraveWebSearchProvider(
            new SingleClientFactory(new HttpClient(handler)),
            Options.Create(new WebSearchOptions { ApiKey = "key", Country = "BY" }),
            NullLogger<BraveWebSearchProvider>.Instance);

        var response = await provider.SearchAsync("Mestprom Minsk", count: 5, CancellationToken.None);

        Assert.True(response.IsAvailable);
        Assert.Single(response.Results);
        Assert.Equal(2, handler.Requests.Count);
        Assert.Contains("country=BY", handler.Requests[0].Query, StringComparison.Ordinal);
        Assert.DoesNotContain("country=", handler.Requests[1].Query, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Non_422_failure_is_not_retried()
    {
        var handler = new SequenceHandler((HttpStatusCode.Unauthorized, string.Empty));
        var provider = new BraveWebSearchProvider(
            new SingleClientFactory(new HttpClient(handler)),
            Options.Create(new WebSearchOptions { ApiKey = "key", Country = "BY" }),
            NullLogger<BraveWebSearchProvider>.Instance);

        var response = await provider.SearchAsync("query", count: 5, CancellationToken.None);

        Assert.False(response.IsAvailable);
        Assert.Single(handler.Requests);
    }

    [Fact]
    public void Default_policy_allows_everything_and_research_policy_forbids_control_and_writes()
    {
        Assert.True(AgentToolPolicy.Default.AllowControlActions);
        Assert.True(AgentToolPolicy.Default.AllowMemoryWrite);
        Assert.True(AgentToolPolicy.Default.AllowWebSearch);

        var research = AgentToolPolicy.ReadOnlyResearch(
            allowWebSearch: true,
            allowHomeAssistantRead: true,
            allowMemoryRead: true);

        // Пользователя рядом нет — подтверждать управление и запись некому.
        Assert.False(research.AllowControlActions);
        Assert.False(research.AllowMemoryWrite);
        Assert.True(research.AllowWebSearch);
    }

    [Fact]
    public async Task Agent_tool_scope_is_carried_into_the_run_policy()
    {
        var repository = new InMemoryRepository();
        var runtime = new CapturingAgentRuntime();
        var runner = CreateRunner(repository, runtime);

        var definition = AutonomousAgentDefinition.Create(
            "агент",
            "миссия",
            AutonomousAgentScheduleKind.Manual,
            toolScope: AutonomousAgentToolScope.Create(
                allowHomeAssistantRead: false,
                allowWebSearch: false,
                allowMemoryRead: true,
                allowMemoryWrite: true,
                maxDurableFactsPerRun: 1));
        await repository.UpsertDefinitionAsync(definition, CancellationToken.None);

        await runner.RunAsync(definition, deliverIndividually: true, CancellationToken.None);

        var policy = runtime.LastContext!.EffectiveToolPolicy;
        Assert.False(policy.AllowWebSearch);
        Assert.False(policy.AllowHomeAssistantRead);
        Assert.True(policy.AllowMemoryRead);
        // Даже если владелец разрешил запись, фоновый запуск её не получает: подтверждать некому.
        Assert.False(policy.AllowMemoryWrite);
        Assert.False(policy.AllowControlActions);
    }

    private static AutonomousAgentRunner CreateRunner(
        IAutonomousAgentRepository repository,
        IAgentRuntime runtime) =>
        new(
            repository,
            runtime,
            new AutonomousAgentCapsuleWriter(
                new UnusedMemoryClient(),
                Microsoft.Extensions.Options.Options.Create(new MemoryMcpOptions()),
                NullLogger<AutonomousAgentCapsuleWriter>.Instance),
            NullLogger<AutonomousAgentRunner>.Instance);

    private sealed class SingleClientFactory : IHttpClientFactory
    {
        private readonly HttpClient _client;

        public SingleClientFactory(HttpClient client) => _client = client;

        public HttpClient CreateClient(string name) => _client;
    }

    /// <summary>Фейковый handler: отдаёт заранее заданную последовательность ответов и записывает URI запросов.</summary>
    private sealed class SequenceHandler : HttpMessageHandler
    {
        private readonly Queue<(HttpStatusCode Status, string Body)> _responses;

        public SequenceHandler(params (HttpStatusCode Status, string Body)[] responses) =>
            _responses = new Queue<(HttpStatusCode, string)>(responses);

        public List<Uri> Requests { get; } = new();

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request.RequestUri!);
            var (status, body) = _responses.Dequeue();
            return Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
        }
    }

    private sealed class CapturingAgentRuntime : IAgentRuntime
    {
        public AgentContext? LastContext { get; private set; }

        public AgentRuntimeHealth GetHealth() =>
            AgentRuntimeHealth.Configured(new LlmOptions());

        public Task<AgentRuntimeResponse> SendAsync(
            string message,
            AgentContext context,
            Func<AgentRuntimeReasoningUpdate, CancellationToken, Task>? onReasoningUpdate,
            CancellationToken cancellationToken)
        {
            LastContext = context;
            return Task.FromResult(new AgentRuntimeResponse(
                context.CorrelationId,
                IsConfigured: true,
                """{"summary":"ok","questions":[],"durableFacts":[]}""",
                GetHealth()));
        }
    }

    private sealed class UnusedMemoryClient : HaPersonalAgent.Memory.IMemoryMcpClient
    {
        public Task<HaPersonalAgent.Memory.MemoryMcpDiscoveryResult> DiscoverAsync(CancellationToken cancellationToken) =>
            throw new NotSupportedException();

        public Task<HaPersonalAgent.Memory.MemoryMcpToolResult> CallToolAsync(
            string toolName,
            IReadOnlyDictionary<string, object?>? arguments,
            CancellationToken cancellationToken) =>
            throw new NotSupportedException("Memory MCP is unconfigured in this test, so it must never be called.");
    }

    /// <summary>Минимальный in-memory репозиторий: тесту важен только контекст, доехавший до рантайма.</summary>
    private sealed class InMemoryRepository : IAutonomousAgentRepository
    {
        private readonly Dictionary<string, AutonomousAgentDefinition> _definitions = new(StringComparer.Ordinal);
        private readonly List<AutonomousAgentRun> _runs = new();

        public Task InitializeAsync(CancellationToken cancellationToken) => Task.CompletedTask;

        public Task UpsertDefinitionAsync(AutonomousAgentDefinition definition, CancellationToken cancellationToken)
        {
            _definitions[definition.Id] = definition;
            return Task.CompletedTask;
        }

        public Task<AutonomousAgentDefinition?> GetDefinitionAsync(string agentId, CancellationToken cancellationToken) =>
            Task.FromResult(_definitions.TryGetValue(agentId, out var value) ? value : null);

        public Task<IReadOnlyList<AutonomousAgentDefinition>> ListDefinitionsAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AutonomousAgentDefinition>>(_definitions.Values.ToList());

        public Task<bool> DeleteDefinitionAsync(string agentId, CancellationToken cancellationToken) =>
            Task.FromResult(_definitions.Remove(agentId));

        public Task UpdateScheduleStateAsync(
            string agentId,
            DateTimeOffset? nextRunUtc,
            DateTimeOffset? lastRunUtc,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task AppendRunAsync(AutonomousAgentRun run, CancellationToken cancellationToken)
        {
            _runs.Add(run);
            return Task.CompletedTask;
        }

        public Task UpdateRunAsync(AutonomousAgentRun run, CancellationToken cancellationToken)
        {
            var index = _runs.FindIndex(candidate => candidate.Id == run.Id);
            if (index >= 0)
            {
                _runs[index] = run;
            }

            return Task.CompletedTask;
        }

        public Task<AutonomousAgentRun?> GetRunAsync(string runId, CancellationToken cancellationToken) =>
            Task.FromResult(_runs.FirstOrDefault(run => run.Id == runId));

        public Task<IReadOnlyList<AutonomousAgentRun>> ListRunsAsync(
            string agentId,
            int limit,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AutonomousAgentRun>>(
                _runs.Where(run => run.AgentId == agentId).ToList());

        public Task<bool> HasRunningRunAsync(string agentId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<AutonomousAgentRun?> FindRunByDeliveredMessageAsync(
            string deliveredMessageId,
            CancellationToken cancellationToken) => Task.FromResult<AutonomousAgentRun?>(null);

        public Task EnqueueReplyAsync(AutonomousAgentInboxEntry entry, CancellationToken cancellationToken) =>
            Task.CompletedTask;

        public Task<IReadOnlyList<AutonomousAgentInboxEntry>> GetPendingRepliesAsync(
            string agentId,
            CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<AutonomousAgentInboxEntry>>(Array.Empty<AutonomousAgentInboxEntry>());

        public Task MarkRepliesConsumedAsync(
            IEnumerable<string> entryIds,
            string consumedByRunId,
            DateTimeOffset consumedUtc,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> DeletePendingReplyAsync(string agentId, string entryId, CancellationToken cancellationToken) =>
            Task.FromResult(false);

        public Task<AutonomousAgentContinuity?> GetContinuityAsync(string agentId, CancellationToken cancellationToken) =>
            Task.FromResult<AutonomousAgentContinuity?>(null);

        public Task UpsertContinuityAsync(AutonomousAgentContinuity continuity, CancellationToken cancellationToken) =>
            Task.CompletedTask;
    }
}
