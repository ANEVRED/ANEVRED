using System.Text.RegularExpressions;

namespace ZestResourceOptimizer.Services;

public static class FallbackRussianTranslationService
{
    private static readonly (Regex Pattern, string Replacement)[] PhraseRules =
    [
        Rule("primary objectives", "\u043e\u0441\u043d\u043e\u0432\u043d\u044b\u0435 \u0446\u0435\u043b\u0438"),
        Rule("mission specs", "\u0434\u0430\u043d\u043d\u044b\u0435 \u043c\u0438\u0441\u0441\u0438\u0438"),
        Rule("area of operation", "\u0440\u0430\u0439\u043e\u043d \u043e\u043f\u0435\u0440\u0430\u0446\u0438\u0438"),
        Rule("hostile forces", "\u0432\u0440\u0430\u0436\u0434\u0435\u0431\u043d\u044b\u0435 \u0441\u0438\u043b\u044b"),
        Rule("equipment specs", "\u0441\u043d\u0430\u0440\u044f\u0436\u0435\u043d\u0438\u0435"),
        Rule("tactical strike group needed", "\u043d\u0443\u0436\u043d\u0430 \u0442\u0430\u043a\u0442\u0438\u0447\u0435\u0441\u043a\u0430\u044f \u0443\u0434\u0430\u0440\u043d\u0430\u044f \u0433\u0440\u0443\u043f\u043f\u0430"),
        Rule("we've received word", "\u043c\u044b \u043f\u043e\u043b\u0443\u0447\u0438\u043b\u0438 \u0441\u043e\u043e\u0431\u0449\u0435\u043d\u0438\u0435"),
        Rule("we have received word", "\u043c\u044b \u043f\u043e\u043b\u0443\u0447\u0438\u043b\u0438 \u0441\u043e\u043e\u0431\u0449\u0435\u043d\u0438\u0435"),
        Rule("go to", "\u043b\u0435\u0442\u0438\u0442\u0435 \u043a"),
        Rule("defend", "\u0437\u0430\u0449\u0438\u0442\u0438\u0442\u0435"),
        Rule("destroy", "\u0443\u043d\u0438\u0447\u0442\u043e\u0436\u044c\u0442\u0435"),
        Rule("target and destroy", "\u043d\u0430\u0439\u0434\u0438\u0442\u0435 \u0438 \u0443\u043d\u0438\u0447\u0442\u043e\u0436\u044c\u0442\u0435"),
        Rule("power relays", "\u0441\u0438\u043b\u043e\u0432\u044b\u0435 \u0440\u0435\u043b\u0435"),
        Rule("cooling units", "\u0431\u043b\u043e\u043a\u0438 \u043e\u0445\u043b\u0430\u0436\u0434\u0435\u043d\u0438\u044f"),
        Rule("station core", "\u044f\u0434\u0440\u043e \u0441\u0442\u0430\u043d\u0446\u0438\u0438"),
        Rule("exposed station core", "\u043e\u0442\u043a\u0440\u044b\u0442\u043e\u0435 \u044f\u0434\u0440\u043e \u0441\u0442\u0430\u043d\u0446\u0438\u0438"),
        Rule("gain access inside", "\u043f\u043e\u043b\u0443\u0447\u0438\u0442\u0435 \u0434\u043e\u0441\u0442\u0443\u043f \u0432\u043d\u0443\u0442\u0440\u044c"),
        Rule("enter the station", "\u0432\u043e\u0439\u0434\u0438\u0442\u0435 \u043d\u0430 \u0441\u0442\u0430\u043d\u0446\u0438\u044e"),
        Rule("return to base", "\u0432\u0435\u0440\u043d\u0438\u0442\u0435\u0441\u044c \u043d\u0430 \u0431\u0430\u0437\u0443"),
        Rule("contract deadline", "\u0441\u0440\u043e\u043a \u043a\u043e\u043d\u0442\u0440\u0430\u043a\u0442\u0430"),
        Rule("contracted by", "\u0437\u0430\u043a\u0430\u0437\u0447\u0438\u043a"),
        Rule("briefing", "\u0431\u0440\u0438\u0444\u0438\u043d\u0433"),
        Rule("details", "\u0434\u0435\u0442\u0430\u043b\u0438"),
        Rule("accepted", "\u043f\u0440\u0438\u043d\u044f\u0442\u043e"),
        Rule("offers", "\u043f\u0440\u0435\u0434\u043b\u043e\u0436\u0435\u043d\u0438\u044f"),
        Rule("mercenary", "\u043d\u0430\u0435\u043c\u043d\u0438\u043a"),
        Rule("salvage", "\u0443\u0442\u0438\u043b\u0438\u0437\u0430\u0446\u0438\u044f"),
        Rule("hostile territory", "\u0432\u0440\u0430\u0436\u0434\u0435\u0431\u043d\u0430\u044f \u0442\u0435\u0440\u0440\u0438\u0442\u043e\u0440\u0438\u044f"),
        Rule("reward", "\u043d\u0430\u0433\u0440\u0430\u0434\u0430")
    ];

    private static readonly Dictionary<string, string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        ["ship"] = "\u043a\u043e\u0440\u0430\u0431\u043b\u044c",
        ["ships"] = "\u043a\u043e\u0440\u0430\u0431\u043b\u0438",
        ["station"] = "\u0441\u0442\u0430\u043d\u0446\u0438\u044f",
        ["system"] = "\u0441\u0438\u0441\u0442\u0435\u043c\u0430",
        ["pilot"] = "\u043f\u0438\u043b\u043e\u0442",
        ["personnel"] = "\u043f\u0435\u0440\u0441\u043e\u043d\u0430\u043b",
        ["team"] = "\u043a\u043e\u043c\u0430\u043d\u0434\u0430",
        ["security"] = "\u043e\u0445\u0440\u0430\u043d\u0430",
        ["operation"] = "\u043e\u043f\u0435\u0440\u0430\u0446\u0438\u044f",
        ["forces"] = "\u0441\u0438\u043b\u044b",
        ["hostile"] = "\u0432\u0440\u0430\u0436\u0434\u0435\u0431\u043d\u044b\u0439",
        ["old"] = "\u0441\u0442\u0430\u0440\u044b\u0439",
        ["site"] = "\u043c\u0435\u0441\u0442\u043e",
        ["critical"] = "\u043a\u0440\u0438\u0442\u0438\u0447\u043d\u043e",
        ["needed"] = "\u043d\u0443\u0436\u043d\u043e",
        ["called"] = "\u043d\u0430\u0437\u044b\u0432\u0430\u0435\u0442\u0441\u044f",
        ["against"] = "\u043f\u0440\u043e\u0442\u0438\u0432",
        ["inside"] = "\u0432\u043d\u0443\u0442\u0440\u0438",
        ["outside"] = "\u0441\u043d\u0430\u0440\u0443\u0436\u0438",
        ["health"] = "\u043f\u0440\u043e\u0447\u043d\u043e\u0441\u0442\u044c",
        ["group"] = "\u0433\u0440\u0443\u043f\u043f\u0430",
        ["strike"] = "\u0443\u0434\u0430\u0440",
        ["mission"] = "\u043c\u0438\u0441\u0441\u0438\u044f",
        ["objective"] = "\u0446\u0435\u043b\u044c",
        ["objectives"] = "\u0446\u0435\u043b\u0438",
        ["received"] = "\u043f\u043e\u043b\u0443\u0447\u0435\u043d\u043e",
        ["word"] = "\u0441\u043e\u043e\u0431\u0449\u0435\u043d\u0438\u0435",
        ["protect"] = "\u0437\u0430\u0449\u0438\u0442\u0438\u0442\u044c",
        ["intervene"] = "\u0432\u043c\u0435\u0448\u0430\u0442\u044c\u0441\u044f",
        ["destroy"] = "\u0443\u043d\u0438\u0447\u0442\u043e\u0436\u0438\u0442\u044c",
        ["access"] = "\u0434\u043e\u0441\u0442\u0443\u043f",
        ["base"] = "\u0431\u0430\u0437\u0430",
        ["manager"] = "\u043c\u0435\u043d\u0435\u0434\u0436\u0435\u0440",
        ["operations"] = "\u043e\u043f\u0435\u0440\u0430\u0446\u0438\u0438",
        ["solution"] = "\u0440\u0435\u0448\u0435\u043d\u0438\u0435",
        ["solutions"] = "\u0440\u0435\u0448\u0435\u043d\u0438\u044f"
    };

    public static string Translate(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return string.Empty;
        }

        var lines = text
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n')
            .Select(TranslateLine);

        return string.Join(Environment.NewLine, lines).Trim();
    }

    private static string TranslateLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return string.Empty;
        }

        var translated = line.Trim();
        foreach (var (pattern, replacement) in PhraseRules)
        {
            translated = pattern.Replace(translated, replacement);
        }

        translated = Regex.Replace(translated, @"\b[A-Za-z][A-Za-z'-]*\b", match =>
        {
            var word = match.Value;
            return Words.TryGetValue(word, out var translatedWord) ? translatedWord : word;
        });

        return translated;
    }

    private static (Regex Pattern, string Replacement) Rule(string phrase, string replacement)
    {
        return (new Regex(@"\b" + Regex.Escape(phrase).Replace("\\ ", "\\s+") + @"\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant), replacement);
    }
}
