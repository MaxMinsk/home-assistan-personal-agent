using HaPersonalAgent.Agent;
using HaPersonalAgent.Autonomous;

namespace HaPersonalAgent.Tests;

/// <summary>
/// Что: тесты границ инструментов для HPA-035 (approve-later, стадия A — создание предложений).
/// Зачем: включение галочки «может предлагать» должно поднимать ИМЕННО propose-оси (control + запись в память) и
/// не открывать лишнего; выключенная галочка обязана оставлять прежний research-only профиль без регресса.
/// Как: проверяем фабрики AgentToolPolicy и дефолт AutonomousAgentToolScope напрямую.
/// </summary>
public class AutonomousProposeActionsPolicyTests
{
    [Fact]
    public void Research_default_scope_does_not_allow_proposing_actions()
    {
        Assert.False(AutonomousAgentToolScope.ResearchDefault.AllowProposeActions);
        // Явный Create без флага — тоже безопасный дефолт.
        Assert.False(AutonomousAgentToolScope.Create(true, true, true, true, 3).AllowProposeActions);
    }

    [Fact]
    public void Read_only_research_policy_never_allows_control_or_writes()
    {
        var policy = AgentToolPolicy.ReadOnlyResearch(
            allowWebSearch: true,
            allowHomeAssistantRead: true,
            allowMemoryRead: true);

        Assert.False(policy.AllowControlActions);
        Assert.False(policy.AllowMemoryWrite);
        Assert.False(policy.AllowScheduledAgentRouting);
    }

    [Fact]
    public void Proposals_policy_enables_control_and_memory_write_proposals_but_not_routing()
    {
        var policy = AgentToolPolicy.ReadOnlyResearchWithProposals(
            allowWebSearch: true,
            allowHomeAssistantRead: true,
            allowMemoryRead: true);

        // Control-инструмент (propose_home_assistant_mcp_action) и propose_memory_save становятся доступны.
        Assert.True(policy.AllowControlActions);
        Assert.True(policy.AllowMemoryWrite);
        // Роутинг в плановые агенты фоновому прогону по-прежнему запрещён (во избежание петель).
        Assert.False(policy.AllowScheduledAgentRouting);
    }

    [Fact]
    public void Memory_write_proposals_require_memory_read()
    {
        // propose_memory_save вложен в ветку memory-read: без чтения предлагать записи нельзя.
        var policy = AgentToolPolicy.ReadOnlyResearchWithProposals(
            allowWebSearch: true,
            allowHomeAssistantRead: true,
            allowMemoryRead: false);

        Assert.True(policy.AllowControlActions);
        Assert.False(policy.AllowMemoryWrite);
    }
}
