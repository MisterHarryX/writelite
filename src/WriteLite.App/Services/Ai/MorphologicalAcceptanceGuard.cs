using WriteLite.Language.Russian;
using WriteLite.Models;
using WriteLite.Services.Grammar;

namespace WriteLite.Services.Ai;

/// <summary>
/// Rejects a model correction whose result is worse Russian than what it replaces.
/// </summary>
/// <remarks>
/// <para><b>Why the model's own confidence is not enough.</b> §29 of the sprint brief lists
/// what has actually come out of this pipeline: «более лучшее решение» → «лучше решение»,
/// «интерфейс более удобнее» → «более удобная», «отправим сборку» → «отправить сборку». Each
/// was returned with high confidence, and each is a construction no Russian speaker produces.
/// A generative model scores fluency of its own output, not grammaticality of the sentence it
/// leaves behind; something has to read the result.</para>
///
/// <para><b>Four checks, all on the corrected local context rather than on the edit.</b> The
/// edit alone cannot show the damage — «лучше» is a perfectly good word — so the guard
/// substitutes the replacement into the sentence and asks whether the phrase that comes out
/// still agrees, still has a finite predicate where it had one, and does not stack a
/// comparative on a comparative. That is why the fixes generalise instead of being a list of
/// the six strings above: nothing here mentions any of them.</para>
///
/// <para><b>Absent morphology means absent guard, not absent safety.</b> Without the form index
/// this type is not constructed and the AI acceptance policy keeps its other bars. It never
/// weakens a check; it only adds one.</para>
/// </remarks>
public sealed class MorphologicalAcceptanceGuard
{
    private readonly RussianMorphology _morphology;

    public MorphologicalAcceptanceGuard(RussianMorphology morphology)
        => _morphology = morphology ?? throw new ArgumentNullException(nameof(morphology));

    public static MorphologicalAcceptanceGuard? TryCreate(RussianFormIndex? index)
    {
        var morphology = RussianMorphology.TryCreate(index);
        return morphology is null ? null : new MorphologicalAcceptanceGuard(morphology);
    }

    /// <summary>Why a correction was rejected, or null when it was not.</summary>
    public string? Reject(string text, TextIssue candidate)
    {
        if (candidate.Replacement is null) return null;
        if (candidate.Start < 0 || candidate.Start + candidate.Length > text.Length) return null;

        var applied = IssueApplication.Apply(text, [candidate]);
        if (!applied.Ok) return "morph-guard: span does not apply";

        var before = Window(text, candidate.Start, candidate.Length);
        var after = Window(applied.Text, candidate.Start, candidate.Replacement.Length);

        if (StacksComparative(after) && !StacksComparative(before))
        {
            return "morph-guard: double comparative introduced";
        }

        if (BreaksAdjacentAgreement(after) && !BreaksAdjacentAgreement(before))
        {
            return "morph-guard: adjacent agreement broken";
        }

        if (RemovesFinitePredicate(before, after))
        {
            return "morph-guard: finite predicate replaced by an infinitive";
        }

        if (IntroducesAdverbBeforeNoun(before, after))
        {
            return "morph-guard: adverb left standing before a noun";
        }

        return null;
    }

    /// <summary>The sentence around an edit, which is the smallest context these checks need.</summary>
    private static string Window(string text, int start, int length)
    {
        var from = text.LastIndexOfAny(['.', '!', '?', '\n'], Math.Max(0, Math.Min(start, text.Length - 1)));
        from = from < 0 ? 0 : from + 1;
        var to = text.IndexOfAny(['.', '!', '?', '\n'], Math.Min(start + Math.Max(length, 1), text.Length - 1));
        to = to < 0 ? text.Length : to;
        return to > from ? text[from..to] : text;
    }

    /// <summary>«более удобнее», «более лучше» — an analytic and a synthetic comparative at once.</summary>
    private bool StacksComparative(string window)
    {
        var tokens = RussianTokens.Split(window);
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            var head = tokens[i].Value.ToLowerInvariant();
            if (head is not ("более" or "менее")) continue;
            if (IsSyntheticComparative(tokens[i + 1].Value)) return true;
        }

        return false;
    }

    /// <summary>
    /// «лучше решение» — a synthetic comparative standing where an attribute belongs.
    /// </summary>
    /// <remarks>
    /// A comparative does not decline, so it cannot be an attribute: «лучше решение» is not a
    /// noun phrase in any reading. This is the check that rejects «более лучшее решение» →
    /// «лучше решение», and it does so without knowing that either string exists.
    /// </remarks>
    private bool BreaksAdjacentAgreement(string window)
    {
        var tokens = RussianTokens.Split(window);
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (!IsSyntheticComparative(tokens[i].Value)) continue;
            if (!RussianTokens.OnlySpacesBetween(window, tokens[i], tokens[i + 1])) continue;

            var next = tokens[i + 1].Value;
            if (!_morphology.IsNoun(next)) continue;

            // A comparative followed by a genitive is the comparison itself — «лучше решения»,
            // "better than the solution" — and is correct.
            var analysis = _morphology.Nominal(next);
            if (analysis.IsEmpty || analysis.CanBe(RuCase.Gen)) continue;

            return true;
        }

        return false;
    }

    /// <summary>«отправим сборку» → «отправить сборку»: a predicate turned into an infinitive.</summary>
    /// <remarks>
    /// Only rejected when the sentence had a finite verb and no longer has one. A sentence that
    /// legitimately contains only an infinitive — «Отправить сборку до пятницы.» — never had a
    /// finite verb to lose, so the check does not fire on it, and neither does it fire when a
    /// modal like «нужно» or «можно» is present to license the infinitive.
    /// </remarks>
    private bool RemovesFinitePredicate(string before, string after)
        => HasFinitePredicate(before) && !HasFinitePredicate(after) && !HasModal(after);

    private bool HasFinitePredicate(string window)
    {
        foreach (var token in RussianTokens.Split(window))
        {
            var form = _morphology.Verb(token.Value);
            if (form is { IsInfinitive: false }) return true;
        }

        return false;
    }

    private static bool HasModal(string window)
    {
        foreach (var token in RussianTokens.Split(window))
        {
            if (Modals.Contains(token.Value.ToLowerInvariant())) return true;
        }

        return false;
    }

    /// <summary>«более удобнее» → «более тщательная проверить» and other part-of-speech salads.</summary>
    private bool IntroducesAdverbBeforeNoun(string before, string after)
        => AdverbBeforeNoun(after) && !AdverbBeforeNoun(before);

    private bool AdverbBeforeNoun(string window)
    {
        var tokens = RussianTokens.Split(window);
        for (var i = 0; i + 1 < tokens.Count; i++)
        {
            if (!_morphology.IsModifier(tokens[i].Value)) continue;
            if (!RussianTokens.OnlySpacesBetween(window, tokens[i], tokens[i + 1])) continue;

            var next = _morphology.Verb(tokens[i + 1].Value);
            if (next is { IsInfinitive: true }) return true;
        }

        return false;
    }

    /// <summary>True for «лучше», «удобнее», «быстрее», «тщательнее» and their kind.</summary>
    /// <remarks>
    /// The index has a part of speech for exactly this — COMP — so the shape heuristic is only
    /// a fallback for the handful of suppletive forms filed as particles. Deriving it from the
    /// «-ее» ending instead was wrong in the direction that matters: «лучшее» ends that way and
    /// is a declinable superlative adjective, so the sentence «более лучшее решение» looked as
    /// though it already contained the error the guard was checking for, and the rewrite that
    /// destroyed it was accepted.
    /// </remarks>
    private bool IsSyntheticComparative(string word)
    {
        var lower = word.ToLowerInvariant();
        if (_morphology.PartOfSpeech(lower) == RussianPartOfSpeech.Comparative) return true;
        if (_morphology.PartOfSpeech(lower) == RussianPartOfSpeech.AdjectiveFull) return false;
        return IrregularComparatives.Contains(lower);
    }

    private static readonly HashSet<string> IrregularComparatives = new(StringComparer.OrdinalIgnoreCase)
    {
        "лучше", "хуже", "больше", "меньше", "выше", "ниже", "дальше", "ближе", "старше",
        "младше", "тише", "громче", "чаще", "реже", "проще", "легче", "тяжелее", "дольше",
    };

    private static readonly HashSet<string> Modals = new(StringComparer.OrdinalIgnoreCase)
    {
        "нужно", "надо", "можно", "нельзя", "следует", "стоит", "необходимо", "требуется",
        "хочу", "хочет", "хотим", "хотят", "должен", "должна", "должны", "должно",
    };
}
