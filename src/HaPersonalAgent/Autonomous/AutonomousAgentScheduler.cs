using HaPersonalAgent.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: фоновый планировщик автономных агентов — будит тех, у кого наступил срок.
/// Зачем: агент должен просыпаться сам по расписанию и переживать рестарт add-on, не теряя и не дублируя запуски.
/// Как: тикает раз в TickInterval и вызывает TickAsync с текущим временем; срок хранится в БД (next_run_utc), поэтому рестарт не сбивает график.
/// Гарантии: не запускает агента поверх незавершённого запуска, ограничивает параллельность и длительность, а пропущенные сроки обрабатывает по catch-up политике.
/// </summary>
public sealed class AutonomousAgentScheduler : BackgroundService
{
    private static readonly TimeSpan TickInterval = TimeSpan.FromSeconds(30);

    /// <summary>Опоздание в пределах этого окна — обычный джиттер тика, а не "пропущенный" запуск.</summary>
    private static readonly TimeSpan MissedRunGrace = TimeSpan.FromMinutes(15);

    private readonly IAutonomousAgentRepository _repository;
    private readonly IAutonomousAgentRunner _runner;
    private readonly IOptions<AutonomousAgentOptions> _options;
    private readonly ILogger<AutonomousAgentScheduler> _logger;

    public AutonomousAgentScheduler(
        IAutonomousAgentRepository repository,
        IAutonomousAgentRunner runner,
        IOptions<AutonomousAgentOptions> options,
        ILogger<AutonomousAgentScheduler> logger)
    {
        _repository = repository;
        _runner = runner;
        _options = options;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_options.Value.Enabled)
        {
            _logger.LogInformation("Autonomous agent scheduler is disabled by configuration.");
            return;
        }

        _logger.LogInformation(
            "Autonomous agent scheduler started: tick {TickSeconds}s, run timeout {RunTimeoutMinutes}m, max concurrent runs {MaxConcurrentRuns}, catch-up {CatchUpPolicy}.",
            TickInterval.TotalSeconds,
            _options.Value.RunTimeoutMinutes,
            _options.Value.MaxConcurrentRuns,
            _options.Value.CatchUpPolicy);

        try
        {
            await _repository.InitializeAsync(stoppingToken);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            _logger.LogError(exception, "Autonomous agent scheduler failed to initialize storage; scheduler is stopping.");
            return;
        }

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await TickAsync(DateTimeOffset.UtcNow, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception exception)
            {
                // Тик не должен убивать планировщик: логируем и ждём следующего.
                _logger.LogWarning(exception, "Autonomous agent scheduler tick failed; will retry on the next tick.");
            }

            try
            {
                await Task.Delay(TickInterval, stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    /// <summary>
    /// Один проход планировщика. Вынесен отдельно и принимает время аргументом,
    /// чтобы тесты могли прогонять сценарии с фейковыми часами без ожидания реального тика.
    /// </summary>
    internal async Task TickAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        var definitions = await _repository.ListDefinitionsAsync(cancellationToken);
        var dueAgents = new List<AutonomousAgentDefinition>();

        foreach (var definition in definitions)
        {
            if (definition.Status != AutonomousAgentStatus.Active
                || definition.ScheduleKind == AutonomousAgentScheduleKind.Manual)
            {
                continue;
            }

            // Новый агент ещё не имеет срока: ставим его "сейчас", чтобы первый бриф пришёл быстро,
            // а не через целый период ожидания.
            if (definition.NextRunUtc is null)
            {
                await _repository.UpdateScheduleStateAsync(
                    definition.Id,
                    nowUtc,
                    definition.LastRunUtc,
                    cancellationToken);
                dueAgents.Add(definition with { NextRunUtc = nowUtc });
                continue;
            }

            if (definition.NextRunUtc.Value <= nowUtc)
            {
                dueAgents.Add(definition);
            }
        }

        if (dueAgents.Count == 0)
        {
            return;
        }

        var maxConcurrent = Math.Clamp(_options.Value.MaxConcurrentRuns, 1, 8);
        using var concurrencyLimit = new SemaphoreSlim(maxConcurrent, maxConcurrent);
        var runTasks = new List<Task>();

        foreach (var definition in dueAgents)
        {
            // Не запускаем поверх незавершённого запуска: длинное исследование не должно множиться.
            if (await _repository.HasRunningRunAsync(definition.Id, cancellationToken))
            {
                _logger.LogInformation(
                    "Autonomous agent {AgentId} is skipped: a previous run is still in flight.",
                    definition.Id);
                continue;
            }

            var isMissed = definition.NextRunUtc is not null
                && definition.NextRunUtc.Value < nowUtc - MissedRunGrace;

            if (isMissed && AutonomousAgentOptions.IsSkipCatchUp(_options.Value.CatchUpPolicy))
            {
                var skippedNextRun = AutonomousAgentScheduleCalculator.ComputeNextRun(
                    definition.ScheduleKind,
                    definition.ScheduleExpression,
                    nowUtc);
                await _repository.UpdateScheduleStateAsync(
                    definition.Id,
                    skippedNextRun,
                    definition.LastRunUtc,
                    cancellationToken);
                _logger.LogInformation(
                    "Autonomous agent {AgentId} missed its slot ({MissedSlot:O}) and catch-up is 'skip'; rescheduled to {NextRunUtc:O} without running.",
                    definition.Id,
                    definition.NextRunUtc,
                    skippedNextRun);
                continue;
            }

            // Сдвигаем расписание ДО запуска: если запуск упадёт или зависнет, агент не будет
            // перезапускаться на каждом тике.
            var nextRunUtc = AutonomousAgentScheduleCalculator.ComputeNextRun(
                definition.ScheduleKind,
                definition.ScheduleExpression,
                nowUtc);
            await _repository.UpdateScheduleStateAsync(
                definition.Id,
                nextRunUtc,
                nowUtc,
                cancellationToken);

            runTasks.Add(RunWithLimitsAsync(definition, concurrencyLimit, isMissed, nextRunUtc, cancellationToken));
        }

        if (runTasks.Count > 0)
        {
            await Task.WhenAll(runTasks);
        }
    }

    private async Task RunWithLimitsAsync(
        AutonomousAgentDefinition definition,
        SemaphoreSlim concurrencyLimit,
        bool isMissed,
        DateTimeOffset? nextRunUtc,
        CancellationToken cancellationToken)
    {
        await concurrencyLimit.WaitAsync(cancellationToken);
        try
        {
            var timeout = TimeSpan.FromMinutes(Math.Clamp(_options.Value.RunTimeoutMinutes, 1, 120));
            using var runCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            runCancellation.CancelAfter(timeout);

            _logger.LogInformation(
                "Autonomous agent {AgentId} ('{AgentName}') run starting{MissedSuffix}; next run scheduled for {NextRunUtc:O}.",
                definition.Id,
                definition.Name,
                isMissed ? " (catch-up for a missed slot)" : string.Empty,
                nextRunUtc);

            await _runner.RunAsync(definition, runCancellation.Token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            _logger.LogInformation("Autonomous agent {AgentId} run cancelled because the host is stopping.", definition.Id);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                "Autonomous agent {AgentId} run exceeded the {RunTimeoutMinutes}-minute timeout and was cancelled.",
                definition.Id,
                _options.Value.RunTimeoutMinutes);
        }
        catch (Exception exception)
        {
            _logger.LogError(exception, "Autonomous agent {AgentId} run failed.", definition.Id);
        }
        finally
        {
            concurrencyLimit.Release();
        }
    }
}
