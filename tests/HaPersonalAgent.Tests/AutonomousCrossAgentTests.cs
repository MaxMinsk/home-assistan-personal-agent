using HaPersonalAgent.Autonomous;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты кросс-агентного контекста и правил промпта (HPA-039, часть A + правка HPA-035).
/// Зачем: агент должен ВИДЕТЬ находки других (только с opt-in) для замечания связей, но с заземлением;
/// а строка «read-only» обязана честно меняться, когда агенту разрешено предлагать действия.
/// Как: чистые проверки построителя входного сообщения.
/// </summary>
public class AutonomousCrossAgentTests
{
    private static AutonomousAgentDefinition Agent(bool allowProposeActions = false) =>
        AutonomousAgentDefinition.Create(
            "Бизнес-агент",
            "Ищи ниши.",
            AutonomousAgentScheduleKind.Weekly,
            toolScope: AutonomousAgentToolScope.Create(true, true, true, true, 3, allowProposeActions));

    [Fact]
    public void Cross_agent_notes_render_a_grounded_context_section()
    {
        var input = AutonomousAgentPromptBuilder.BuildRunInput(
            Agent(),
            continuity: null,
            pendingReplies: Array.Empty<AutonomousAgentInboxEntry>(),
            previousSummary: null,
            crossAgentNotes: new[]
            {
                new AutonomousCrossAgentNote("Сканер активов", "нашёл дешёвый офис в центре", "офисы"),
            });

        Assert.Contains("What your other agents are working on", input, StringComparison.Ordinal);
        Assert.Contains("Сканер активов", input, StringComparison.Ordinal);
        Assert.Contains("нашёл дешёвый офис", input, StringComparison.Ordinal);
        Assert.Contains("сейчас в фокусе: офисы", input, StringComparison.Ordinal);
        // Заземление: связь только по факту.
        Assert.Contains("never invent a link", input, StringComparison.Ordinal);
    }

    [Fact]
    public void No_cross_agent_section_when_there_are_no_notes()
    {
        var input = AutonomousAgentPromptBuilder.BuildRunInput(
            Agent(),
            continuity: null,
            pendingReplies: Array.Empty<AutonomousAgentInboxEntry>(),
            previousSummary: null,
            crossAgentNotes: Array.Empty<AutonomousCrossAgentNote>());

        Assert.DoesNotContain("What your other agents are working on", input, StringComparison.Ordinal);
    }

    [Fact]
    public void Read_only_rule_flips_when_the_agent_may_propose_actions()
    {
        var proposing = AutonomousAgentPromptBuilder.BuildRunInput(
            Agent(allowProposeActions: true),
            null,
            Array.Empty<AutonomousAgentInboxEntry>(),
            null);
        Assert.Contains("You may PROPOSE", proposing, StringComparison.Ordinal);
        Assert.DoesNotContain("read-only run: you cannot control", proposing, StringComparison.Ordinal);

        var readOnly = AutonomousAgentPromptBuilder.BuildRunInput(
            Agent(allowProposeActions: false),
            null,
            Array.Empty<AutonomousAgentInboxEntry>(),
            null);
        Assert.Contains("read-only run: you cannot control", readOnly, StringComparison.Ordinal);
    }
}
