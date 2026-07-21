namespace HaPersonalAgent.Autonomous;

/// <summary>
/// Что: всё, что нужно для доставки результата одного завершённого прогона (HPA-039).
/// Зачем: чтобы отделить ДОСТАВКУ от исполнения — при батче (несколько агентов в одно окно) планировщик собирает
/// эти payload'ы и отправляет ОДИН сводный дайджест вместо N сообщений, сохраняя reply-якоря по-агентно.
/// Как: раннер возвращает его из RunAsync (когда индивидуальная доставка подавлена); null — если прогон не дал результата.
/// </summary>
public sealed record AutonomousRunDelivery(
    AutonomousAgentDefinition Definition,
    AutonomousAgentRun Run,
    AutonomousRunOutput Output,
    IReadOnlyList<AutonomousProposedAction> ProposedActions);
