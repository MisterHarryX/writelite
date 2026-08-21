using WriteLite.Language.Russian;
using WriteLite.Services;
using WriteLite.Services.Rules;

// Forensics for the morphology layer.
//
//   morphprobe <word> ...             what the index records vs what the paradigms derive
//   morphprobe --check "<sentence>"   the findings a sentence produces, with the analysis
//                                     of every token that took part in one
//
// The two modes answer the two questions that come up when an agreement rule misfires: is the
// analysis of this word wrong, or is the analysis right and the relation the rule claimed
// wrong. Guessing between them from a benchmark's false-positive list does not work.
Console.OutputEncoding = System.Text.Encoding.UTF8;

var index = RussianFormIndex.Load();
if (index is null)
{
    Console.WriteLine("form index not deployed");
    return 1;
}

var morphology = new RussianMorphology(index);

if (args.Length >= 2 && args[0] == "--check")
{
    var analyzer = new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), index);
    foreach (var sentence in args[1..])
    {
        Console.WriteLine($"» {sentence}");
        var issues = analyzer.Analyze(sentence);
        if (issues.Count == 0) Console.WriteLine("  (no findings)");

        foreach (var issue in issues)
        {
            Console.WriteLine(
                $"  [{issue.RuleId}] {issue.Original.r()} -> {issue.Replacement.r()} "
                + $"conf={issue.Confidence:F2} safe={issue.CanApplyAutomatically} :: {issue.Title}");
        }

        foreach (var token in RussianTokensProbe.Split(sentence))
        {
            var analysis = morphology.Nominal(token);
            var verb = morphology.Verb(token);
            var info = index.GetInfo(token.ToLowerInvariant());
            Console.WriteLine(
                $"    {token,-16} pos={info.PartOfSpeech,-14} mod={(morphology.IsModifier(token) ? "Y" : "-")} "
                + $"noun={(morphology.IsNoun(token) ? "Y" : "-")} "
                + $"{(analysis.IsEmpty ? "-" : string.Join(" ", analysis.Readings))}"
                + $"{(verb is null ? string.Empty : $"  VERB num={verb.Value.Number} gen={verb.Value.Gender} per={verb.Value.Person} past={verb.Value.IsPast} inf={verb.Value.IsInfinitive}")}");
        }

        Console.WriteLine();
    }

    return 0;
}

if (args.Length >= 1 && args[0] == "--perf")
{
    // Deterministic-pipeline latency against document size. The unit is a whole analysis
    // pass, which is what the editor schedules, so the numbers are comparable with the
    // debounce intervals rather than with a per-sentence cost.
    var analyzer = new RuleBasedAnalyzer(RuleCatalog.LoadDefault(), index);
    const string paragraph =
        "Согласно нового плана мы отправим сборку заказчику в пятницу. "
        + "Прочитав письмо он надолго задумался и решил ответить позже. "
        + "Все студенты сдал экзамен успешно, хотя было очень трудно. "
        + "Дождь закончился и выглянуло солнце над мокрым городом. "
        + "Мама приготовила вкусную обед для всей семьи в воскресенье. ";

    Console.WriteLine($"{"words",8} {"chars",9} {"p50 ms",9} {"p95 ms",9} {"issues",8}");
    foreach (var repeats in new[] { 1, 10, 100, 400, 1200 })
    {
        var text = string.Concat(Enumerable.Repeat(paragraph, repeats));
        var wordCount = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries).Length;

        analyzer.Analyze(text);   // warm the caches, as a running editor would be
        var samples = new List<double>();
        var issues = 0;
        var rounds = repeats >= 400 ? 3 : 10;
        for (var i = 0; i < rounds; i++)
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            issues = analyzer.Analyze(text).Count;
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        var p50 = samples[samples.Count / 2];
        var p95 = samples[Math.Min(samples.Count - 1, (int)(samples.Count * 0.95))];
        Console.WriteLine($"{wordCount,8} {text.Length,9} {p50,9:F1} {p95,9:F1} {issues,8}");
    }

    return 0;
}

string[] words = args.Length > 0
    ? args
    :
    [
        "новым", "правилам", "доме", "новом", "интерфейс", "удобная", "задачу", "сложное",
        "тема", "новую", "историю", "интересная", "Германии", "Германия", "документов",
        "документы", "конца", "конце", "поездке", "поездку", "неделе", "прошлом", "прошлой",
        "кровати", "моём", "моей", "эта", "этот", "фильм", "задача", "туфли", "новое",
        "цветы", "красивую", "обед", "вкусную", "студенты", "сдал", "сдали", "живут",
        "живёт", "находятся", "библиотека", "побежал", "дети",
    ];

foreach (var word in words)
{
    var lower = word.ToLowerInvariant();
    var info = index.GetInfo(lower);
    var analysis = morphology.Nominal(lower);
    var verb = morphology.Verb(lower);

    Console.WriteLine($"{word}");
    Console.WriteLine($"  index   : pos={info.PartOfSpeech} lemma={info.Lemma} tag={info.Tag}");
    Console.WriteLine(
        analysis.IsEmpty
            ? "  readings: (none)"
            : $"  readings: {string.Join("  ", analysis.Readings)}  gender={analysis.LexicalGender} anim={analysis.Animate}");
    if (verb is not null)
    {
        Console.WriteLine(
            $"  verb    : num={verb.Value.Number} gen={verb.Value.Gender} per={verb.Value.Person} "
            + $"past={verb.Value.IsPast} inf={verb.Value.IsInfinitive}");
    }
}

return 0;

internal static class RussianTokensProbe
{
    /// <summary>Letter runs with an internal hyphen, matching the analyzers' own tokenizer.</summary>
    public static List<string> Split(string text)
    {
        var tokens = new List<string>();
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetter(text[i])) { i++; continue; }
            var start = i;
            while (i < text.Length
                   && (char.IsLetter(text[i])
                       || (text[i] == '-' && i + 1 < text.Length && char.IsLetter(text[i + 1]))))
            {
                i++;
            }

            tokens.Add(text[start..i]);
        }

        return tokens;
    }
}

internal static class ProbeFormatting
{
    /// <summary>Quotes a value the way a diff would show it, including null.</summary>
    public static string r(this string? value) => value is null ? "(null)" : $"«{value}»";
}
