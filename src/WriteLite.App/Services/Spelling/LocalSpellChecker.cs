using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using WriteLite.Language.Core;

namespace WriteLite.Services.Spelling;

public sealed class LocalSpellChecker : ISpellChecker, IDisposable
{
    private static readonly IReadOnlyDictionary<string, string> StableCorrections =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // Keys are folded: lower case with ё collapsed to е (FoldSpelling).
            //
            // This table exists because Hunspell ranks by edit distance and its own
            // frequency data, which is not tuned for Russian typing errors. "дила"
            // is one edit from both "дела" and "дали", and Hunspell returned "дали"
            // — turning "Как твои дила" into "Как твои дали". A wrong correction is
            // worse than none, so every word here is one where the intended form is
            // unambiguous and the generic ranker is known to pick badly.
            ["тваи"] = "твои",
            ["хочю"] = "хочу",
            ["нужю"] = "нужно",
            ["вообщем"] = "в общем",
            ["вобщем"] = "в общем",
            ["пливет"] = "привет",
            ["славерное"] = "словарное",
            ["испарвление"] = "исправление",
            ["проверямое"] = "проверяемое",
            ["словареное"] = "словарное",

            // Vowel confusions where a competing real word outranks the intended one.
            ["дила"] = "дела",
            // Observed live: Hunspell offered "Роди" (imperative of "родить").
            ["вроди"] = "вроде",
            ["дилам"] = "делам",
            ["симпотичный"] = "симпатичный",
            ["экстримальный"] = "экстремальный",
            ["поситить"] = "посетить",
            ["интиресно"] = "интересно",

            // Doubled or dropped consonants.
            ["граматика"] = "грамматика",
            ["грамматный"] = "грамотный",
            ["професия"] = "профессия",
            ["колличество"] = "количество",
            ["руский"] = "русский",
            ["програма"] = "программа",
            ["расчитать"] = "рассчитать",
            ["агенство"] = "агентство",
            // NOTE: "длинна" deliberately absent — it is a real short-form
            // adjective ("дорога длинна"), so it is in the lexicon and correcting
            // it to "длина" would be wrong roughly as often as it is right.

            // Voiced/voiceless and silent consonants.
            ["сдесь"] = "здесь",
            ["зделать"] = "сделать",
            ["зделал"] = "сделал",
            ["зделали"] = "сделали",
            ["зделаю"] = "сделаю",
            ["извените"] = "извините",
            ["чуствовать"] = "чувствовать",
            ["учавствовать"] = "участвовать",

            // Word forms that are simply not standard Russian.
            //
            // NOTE: "ихний" and its forms are deliberately absent for the same
            // reason as "придти" below — Hunspell lists them as colloquial, so the
            // lexicon check short-circuits before this table is consulted. Marking
            // them is a register judgement, not a spelling one.
            // NOTE: "придти" deliberately absent. CheckCore consults the lexicon
            // before this table, and Hunspell knows "придти" as a dated variant, so
            // an entry here would never be reached. It is also not a misspelling —
            // flagging it is a style judgement and belongs to a style rule.
            ["будующий"] = "будущий",
            ["следущий"] = "следующий",
            ["следущей"] = "следующей",
            ["координально"] = "кардинально",
            ["пришол"] = "пришёл",
            ["ушол"] = "ушёл",
            ["нашол"] = "нашёл",

            // жи/ши, ча/ща, чу/щу.
            ["жызнь"] = "жизнь",
            ["машына"] = "машина",
            ["малышы"] = "малыши",

            // Hard sign before я/е/ю after a prefix.
            ["обьяснить"] = "объяснить",
            ["обьявление"] = "объявление",
            ["обьем"] = "объём",
            ["подьезд"] = "подъезд",

            // Written together but must be separate, and the reverse.
            ["врятли"] = "вряд ли",
            ["наврятли"] = "навряд ли",
            ["потомучто"] = "потому что",
            ["какбудто"] = "как будто",
            ["вкурсе"] = "в курсе",
            ["втечении"] = "в течение"
        };

    private static readonly IReadOnlyDictionary<char, char> EnglishToRussianLayout = new Dictionary<char, char>
    {
        ['q'] = 'й', ['w'] = 'ц', ['e'] = 'у', ['r'] = 'к', ['t'] = 'е', ['y'] = 'н', ['u'] = 'г', ['i'] = 'ш', ['o'] = 'щ', ['p'] = 'з',
        ['['] = 'х', [']'] = 'ъ', ['a'] = 'ф', ['s'] = 'ы', ['d'] = 'в', ['f'] = 'а', ['g'] = 'п', ['h'] = 'р', ['j'] = 'о', ['k'] = 'л',
        ['l'] = 'д', [';'] = 'ж', ['\''] = 'э', ['z'] = 'я', ['x'] = 'ч', ['c'] = 'с', ['v'] = 'м', ['b'] = 'и', ['n'] = 'т', ['m'] = 'ь',
        [','] = 'б', ['.'] = 'ю'
    };

    private readonly SeedSpellDictionary _seed;
    private readonly CompositeSpellingLexicon _russian;
    private readonly ConcurrentDictionary<string, SpellCheckResult> _cache = new(StringComparer.Ordinal);
    private readonly SpellDictionaryStats _stats;
    private readonly HunspellSpellingLexicon? _ruHunspell;

    public LocalSpellChecker()
        : this(SeedSpellDictionary.Load())
    {
    }

    public LocalSpellChecker(SeedSpellDictionary seed)
    {
        var sw = Stopwatch.StartNew();
        _seed = seed;
        _ruHunspell = HunspellSpellingLexicon.LoadRussian();
        _russian = new CompositeSpellingLexicon(_ruHunspell, seed, SpellingLanguage.Russian);
        sw.Stop();

        var ruCount = Math.Max(_russian.ApproximateWordCount, seed.Stats.RussianWordCount);
        var full = _russian.PrimaryReady;
        _stats = new SpellDictionaryStats(
            ruCount,
            0,
            sw.Elapsed + seed.Stats.InitialLoadTime,
            full
                ? "LibreOffice ru_RU Hunspell + WriteLite Russian seed"
                : "WriteLite seed (Hunspell not loaded)",
            full
                ? "BSD-like ru_RU license (Alexander I. Lebedev); project-local seed"
                : seed.Stats.License,
            IsFullDictionaryLoaded: full);

        CompatibilityLogger.State(
            $"spelling-dictionaries-loaded lang=ru words={ruCount} full={(full ? 1 : 0)} ms={_stats.InitialLoadTime.TotalMilliseconds:F0}");
    }

    public bool IsFullDictionaryLoaded => _stats.IsFullDictionaryLoaded;
    public SpellDictionaryStats Stats => _stats;

    public SpellCheckResult CheckWord(
        string word,
        SpellingLanguage language,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var key = $"{language}:{word}";
        if (_cache.TryGetValue(key, out var cached)) return cached;

        var result = CheckCore(word, language, cancellationToken);
        _cache[key] = result;
        return result;
    }

    private SpellCheckResult CheckCore(string word, SpellingLanguage language, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(word) || word.Length <= 1)
        {
            return new SpellCheckResult(word, language, true, []);
        }

        // Latin text is outside the spelling scope. The only exception is a
        // high-confidence Latin-keyboard -> Russian-layout candidate that is
        // itself present in the Russian lexicon (for example ghbdtn -> привет).
        if (word.All(IsLatinLayoutCharacter))
        {
            var layoutCandidate = ConvertLatinKeyboardToRussian(word.ToLowerInvariant());
            if (!string.IsNullOrEmpty(layoutCandidate) && _russian.ContainsExact(layoutCandidate))
            {
                var layoutSuggestions = CorrectionCandidateValidityPolicy.FilterSuggestions(
                    word,
                    [PreserveCase(word, layoutCandidate)],
                    1);
                if (layoutSuggestions.Count > 0)
                {
                    return new SpellCheckResult(word, SpellingLanguage.Russian, false, layoutSuggestions);
                }
            }

            return new SpellCheckResult(word, SpellingLanguage.Russian, true, []);
        }

        var folded = CorrectionCandidateValidityPolicy.FoldSpelling(word);
        // Stable high-confidence typos must win over the broad Hunspell lexicon:
        // some common typos are present there as rare names or variants.
        if (StableCorrections.TryGetValue(folded, out var stable))
        {
            var filteredStable = CorrectionCandidateValidityPolicy.FilterSuggestions(word, [PreserveCase(word, stable)], 1);
            if (filteredStable.Count > 0)
            {
                return new SpellCheckResult(word, language, false, filteredStable);
            }
        }

        var lexicon = _russian;
        if (lexicon.ContainsExact(word))
        {
            return new SpellCheckResult(word, language, true, []);
        }

        if (lexicon.ContainsExact(folded))
        {
            return new SpellCheckResult(word, language, true, []);
        }

        // Suggestions when full lexicon available (avoid tiny-seed false positives).
        IReadOnlyList<string> suggestions = [];
        if (lexicon.PrimaryReady)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var raw = lexicon.Suggest(word, 8);
            if (raw.Count == 0 && !string.Equals(word, folded, StringComparison.Ordinal))
            {
                raw = lexicon.Suggest(folded, 8)
                    .Select(s => PreserveCase(word, s))
                    .ToArray();
            }
            else
            {
                raw = raw.Select(s => PreserveCase(word, s)).ToArray();
            }

            suggestions = CorrectionCandidateValidityPolicy.FilterSuggestions(word, raw, 5);
        }
        else
        {
            var transposition = FindUnambiguousSeedTransposition(folded, language);
            if (transposition is not null)
            {
                suggestions = CorrectionCandidateValidityPolicy.FilterSuggestions(
                    word, [PreserveCase(word, transposition)], 1);
            }
        }

        // Unknown without reliable non-identical suggestion → not an orthography issue.
        var isKnown = false;
        return new SpellCheckResult(word, language, isKnown, suggestions);
    }

    private string? FindUnambiguousSeedTransposition(string normalized, SpellingLanguage language)
    {
        string? match = null;
        for (var i = 0; i < normalized.Length - 1; i++)
        {
            if (normalized[i] == normalized[i + 1]) continue;
            var chars = normalized.ToCharArray();
            (chars[i], chars[i + 1]) = (chars[i + 1], chars[i]);
            var candidate = new string(chars);
            if (!_seed.Contains(candidate, language)) continue;
            if (CorrectionCandidateValidityPolicy.IsMeaninglessSpellingCandidate(normalized, candidate))
                continue;
            if (match is not null) return null;
            match = candidate;
        }

        return match;
    }

    private static string? ConvertLatinKeyboardToRussian(string word)
    {
        var chars = new char[word.Length];
        for (var i = 0; i < word.Length; i++)
        {
            if (!EnglishToRussianLayout.TryGetValue(word[i], out chars[i]))
            {
                return null;
            }
        }

        return new string(chars);
    }

    private static bool IsLatinLayoutCharacter(char value)
        => value is >= 'a' and <= 'z' or >= 'A' and <= 'Z'
            || EnglishToRussianLayout.ContainsKey(char.ToLowerInvariant(value));

    private static string PreserveCase(string original, string suggestion)
    {
        if (string.IsNullOrEmpty(suggestion)) return suggestion;
        if (original.All(c => !char.IsLetter(c) || char.IsUpper(c)))
            return suggestion.ToUpperInvariant();
        if (char.IsUpper(original[0]) && original.Skip(1).All(c => !char.IsLetter(c) || char.IsLower(c)))
            return char.ToUpperInvariant(suggestion[0]) + suggestion[1..];
        return suggestion;
    }

    public void Dispose()
    {
        _ruHunspell?.Dispose();
    }
}
