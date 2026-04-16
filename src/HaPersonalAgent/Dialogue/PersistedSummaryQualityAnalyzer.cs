using System.Text.RegularExpressions;

namespace HaPersonalAgent.Dialogue;

/// <summary>
/// Что: анализатор качества persisted summary по структурному markdown-контракту.
/// Зачем: HAAG-055 требует видеть в /status не только факт наличия summary, но и полезные метрики качества (facts/conflicts/структура).
/// Как: разбирает markdown-секции summary, считает содержательные bullets и возвращает детерминированный snapshot для диагностики.
/// </summary>
public static class PersistedSummaryQualityAnalyzer
{
    private const string SectionUserContext = "## Контекст пользователя";
    private const string SectionFacts = "## Факты и решения";
    private const string SectionOpenTasks = "## Открытые задачи";
    private const string SectionConstraints = "## Ограничения и предпочтения";
    private const string SectionConflicts = "## Конфликты и обновления";
    private const string SectionSourceAttribution = "## Source attribution";

    private static readonly string[] RequiredSections =
    [
        SectionUserContext,
        SectionFacts,
        SectionOpenTasks,
        SectionConstraints,
        SectionConflicts,
        SectionSourceAttribution,
    ];

    private static readonly Regex BulletRegex = new(@"^\s*-\s+(?<value>.+?)\s*$", RegexOptions.Compiled);

    public static PersistedSummaryQualitySnapshot Analyze(string? summaryText)
    {
        if (string.IsNullOrWhiteSpace(summaryText))
        {
            return PersistedSummaryQualitySnapshot.Empty;
        }

        var sections = ParseSections(summaryText);
        var hasStructuredContract = RequiredSections.All(section => sections.ContainsKey(section));
        var factsCount = CountMeaningfulBullets(sections, SectionFacts);
        var openTasksCount = CountMeaningfulBullets(sections, SectionOpenTasks);
        var constraintsCount = CountMeaningfulBullets(sections, SectionConstraints);
        var conflictsCount = CountMeaningfulBullets(sections, SectionConflicts);
        var totalBullets = sections.Sum(entry => CountMeaningfulBullets(entry.Value));

        return new PersistedSummaryQualitySnapshot(
            HasStructuredContract: hasStructuredContract,
            FactsCount: factsCount,
            OpenTasksCount: openTasksCount,
            ConstraintsCount: constraintsCount,
            ConflictsCount: conflictsCount,
            TotalMeaningfulBullets: totalBullets);
    }

    private static Dictionary<string, IReadOnlyList<string>> ParseSections(string summaryText)
    {
        var sections = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        var normalized = summaryText.Replace("\r\n", "\n", StringComparison.Ordinal);
        var lines = normalized.Split('\n');
        var currentSection = string.Empty;
        var buffer = new List<string>();

        foreach (var line in lines)
        {
            if (line.StartsWith("## ", StringComparison.Ordinal))
            {
                FlushSection(sections, currentSection, buffer);
                currentSection = line.Trim();
                buffer = [];
                continue;
            }

            if (string.IsNullOrWhiteSpace(currentSection))
            {
                continue;
            }

            buffer.Add(line);
        }

        FlushSection(sections, currentSection, buffer);
        return sections;
    }

    private static void FlushSection(
        IDictionary<string, IReadOnlyList<string>> sections,
        string sectionName,
        IReadOnlyList<string> lines)
    {
        if (string.IsNullOrWhiteSpace(sectionName))
        {
            return;
        }

        sections[sectionName] = lines;
    }

    private static int CountMeaningfulBullets(
        IReadOnlyDictionary<string, IReadOnlyList<string>> sections,
        string sectionName)
    {
        return sections.TryGetValue(sectionName, out var lines)
            ? CountMeaningfulBullets(lines)
            : 0;
    }

    private static int CountMeaningfulBullets(IReadOnlyList<string> sectionLines)
    {
        var count = 0;
        foreach (var line in sectionLines)
        {
            var match = BulletRegex.Match(line);
            if (!match.Success)
            {
                continue;
            }

            var value = match.Groups["value"].Value.Trim();
            if (value.Equals("нет данных", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            count++;
        }

        return count;
    }
}

/// <summary>
/// Что: snapshot качества persisted summary.
/// Зачем: /status должен показывать метрики в устойчивом machine-friendly виде без ad-hoc парсинга на уровне transport.
/// Как: формируется анализатором PersistedSummaryQualityAnalyzer на основе markdown-секций summary.
/// </summary>
public sealed record PersistedSummaryQualitySnapshot(
    bool HasStructuredContract,
    int FactsCount,
    int OpenTasksCount,
    int ConstraintsCount,
    int ConflictsCount,
    int TotalMeaningfulBullets)
{
    public static PersistedSummaryQualitySnapshot Empty { get; } = new(
        HasStructuredContract: false,
        FactsCount: 0,
        OpenTasksCount: 0,
        ConstraintsCount: 0,
        ConflictsCount: 0,
        TotalMeaningfulBullets: 0);
}
