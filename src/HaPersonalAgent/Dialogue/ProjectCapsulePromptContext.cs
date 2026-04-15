namespace HaPersonalAgent.Dialogue;

/// <summary>
/// Что: контекст project capsules для подмешивания в prompt текущего run.
/// Зачем: капсулы дают устойчивую долговременную память о проектах, которую нужно добавить отдельно от recent turns и summary.
/// Как: содержит готовый текстовый блок для System message и число использованных капсул.
/// </summary>
public sealed record ProjectCapsulePromptContext(
    string? PromptText,
    int CapsuleCount);
