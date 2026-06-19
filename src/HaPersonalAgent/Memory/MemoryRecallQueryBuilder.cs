namespace HaPersonalAgent.Memory;

/// <summary>
/// What: turns a raw user message into an effective Memory MCP full-text query.
/// Why: notes_search matches query tokens with AND semantics, so a natural-language question
/// ("сколько у меня перцев?") requires every word — including function words like "сколько"/"у"/"меня"
/// that never appear in notes — and returns nothing. The retired vector store used to paper over this;
/// lexical recall needs the query cleaned. Verified against the live server: bare "перцев" → hits,
/// the full phrase → 0, and prefix "перц*" is honored (covers Russian morphology).
/// How: drop punctuation, stop words, and very short tokens, then prefix-match each remaining content
/// token ("перцев" → "перцев*"). Removing tokens and prefixing can only broaden an AND query, never
/// narrow it, so this never makes a previously-working query return fewer results. Falls back to the
/// trimmed original when nothing meaningful remains.
/// </summary>
public static class MemoryRecallQueryBuilder
{
    private const int MinTokenLength = 3;

    private static readonly char[] Separators =
    {
        ' ', '\t', '\n', '\r', '.', ',', '!', '?', ';', ':', '(', ')', '[', ']', '{', '}',
        '"', '\'', '«', '»', '—', '–', '-', '/', '\\', '|', '*', '@', '#', '%', '&', '=', '+', '~', '`',
    };

    // Russian + English question/function words that carry no note content and would force an empty
    // AND match. Intentionally conservative: only high-frequency words unlikely to be a search subject.
    private static readonly HashSet<string> StopWords = new(StringComparer.OrdinalIgnoreCase)
    {
        // RU question words
        "сколько", "какие", "какой", "какая", "какое", "что", "чё", "как", "где", "когда", "почему",
        "зачем", "кто", "чей", "чья", "чьи", "куда", "откуда",
        // RU pronouns / particles / prepositions
        "меня", "мне", "мой", "моя", "мои", "моё", "тебя", "тебе", "твой", "вас", "нас", "его", "её",
        "это", "этот", "эта", "эти", "там", "тут", "вот", "уже", "ещё", "еще", "так", "тоже", "или",
        "для", "без", "над", "под", "при", "про", "что-то", "кое",
        // RU common verbs
        "есть", "было", "была", "были", "будет", "быть", "хочу", "хочешь", "можешь", "покажи", "скажи",
        "назови", "перечисли", "вызови", "вижу", "знаю",
        // EN
        "how", "many", "much", "what", "which", "who", "whose", "where", "when", "why", "does", "did",
        "have", "has", "the", "and", "for", "you", "your", "are", "can", "could", "would", "please",
        "show", "tell", "list", "count", "with", "about", "from",
    };

    public static string Build(string? message)
    {
        if (string.IsNullOrWhiteSpace(message))
        {
            return string.Empty;
        }

        var tokens = message
            .Split(Separators, StringSplitOptions.RemoveEmptyEntries)
            .Where(token => token.Length >= MinTokenLength && !StopWords.Contains(token))
            .Select(token => token + "*")
            .ToArray();

        return tokens.Length == 0 ? message.Trim() : string.Join(' ', tokens);
    }
}
