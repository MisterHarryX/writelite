using WriteLite.Language.Core;
using WriteLite.Models;

namespace WriteLite.Services.Ai;

/// <summary>
/// Turns the model's rewritten sentence into corrections a person would recognise, by
/// aligning the two texts on words and punctuation rather than on characters.
/// </summary>
/// <remarks>
/// This used to be a character diff: common prefix and suffix trimmed first, tokens looked
/// for only in whatever was left. Measured on the frozen corpus, 22 of the local model's
/// 123 false positives (18 %) were the shapes that produces — <c>'' → 'ы'</c>,
/// <c>'' → 'ир'</c>, <c>'сторон' → 'нами'</c>. Those are not corrections the model got
/// wrong; they are slices this class made of corrections the model may well have got right.
///
/// <see cref="LinguisticDiff"/> now does the alignment, so spans land on word boundaries by
/// construction and an empty original span can only mean a real insertion. What is left
/// here is the mapping from a language edit to a product issue: category, severity, the
/// confidence prior, and the explanation the popup shows.
/// </remarks>
public sealed class TextCorrectionDiffService : ITextCorrectionDiffService
{
    private readonly AnalysisResultValidator _validator;

    public TextCorrectionDiffService(AnalysisResultValidator? validator = null)
    {
        _validator = validator ?? new AnalysisResultValidator();
    }

    public IReadOnlyList<TextIssue> BuildIssues(string original, string corrected)
    {
        if (string.IsNullOrEmpty(original) || corrected is null)
        {
            return [];
        }

        if (string.Equals(original, corrected, StringComparison.Ordinal))
        {
            return [];
        }

        if (!_validator.IsCorrectedTextSane(original, corrected))
        {
            return [];
        }

        var issues = new List<TextIssue>();
        foreach (var edit in LinguisticDiff.Compute(original, corrected))
        {
            if (!_validator.IsReplacementSane(edit.Original, edit.Replacement))
            {
                continue;
            }

            var (category, linguistic) = CategoryFor(edit);
            var overWide = edit.IsPhraseLevel && edit.WordCount > LinguisticDiff.OverWideWordCount;
            var confidence = ConfidenceFor(category) * (overWide ? 0.8 : 1.0);

            issues.Add(new TextIssue(
                edit.Start,
                edit.Length,
                edit.Original,
                edit.Replacement,
                TitleFor(category),
                ExplanationFor(edit),
                category,
                SeverityFor(confidence),
                CanApplyAutomatically: IsSafeAuto(edit),
                RuleId: "WL-AI-DIFF-" + edit.EditType.ToString().ToUpperInvariant(),
                LinguisticCategory: linguistic,
                Confidence: confidence));
        }

        return issues;
    }

    /// <summary>
    /// A category prior for a model-derived correction.
    /// </summary>
    /// <remarks>
    /// These findings previously carried no confidence at all, which meant they silently
    /// took <see cref="TextIssue"/>'s default of 1.0 — so every AI suggestion claimed
    /// perfect certainty. Measured on the frozen corpus: all 123 AI false positives scored
    /// exactly 1.000, which made any acceptance threshold below 1.0 a no-op and is why a
    /// confidence floor changed nothing.
    ///
    /// This is a category prior, not a model confidence — the local model emits no
    /// per-issue score, and nothing here can invent one. It is honest about what it is:
    /// punctuation edits from a diff are more often right than whole-word grammar rewrites.
    /// Values match the equivalent table in <c>TextDiffBuilder.ConfidenceFor</c> so the two
    /// AI paths agree.
    /// </remarks>
    private static double ConfidenceFor(IssueCategory category) => category switch
    {
        IssueCategory.Punctuation => 0.88,
        IssueCategory.Orthography => 0.85,
        IssueCategory.Grammar => 0.72,
        IssueCategory.Style => 0.60,
        _ => 0.65,
    };

    /// <summary>
    /// A model-derived suggestion is not an error the way a dictionary miss is.
    /// </summary>
    /// <remarks>
    /// Everything from this path used to be <see cref="IssueSeverity.Error"/>, which
    /// presented an 11 %-precision source with the same authority as a 96 %-precision one.
    /// </remarks>
    private static IssueSeverity SeverityFor(double confidence)
        => confidence >= 0.85 ? IssueSeverity.Warning : IssueSeverity.Suggestion;

    /// <summary>
    /// Which product category an edit belongs to.
    /// </summary>
    /// <remarks>
    /// The edit type answers most of this on its own, which is the point of having one: a
    /// punctuation insertion is punctuation, and no letter-counting heuristic is needed to
    /// establish that. Only word and phrase replacements still need the original and the
    /// replacement compared, to separate a misspelling from a grammatical change.
    /// </remarks>
    private static (IssueCategory, LinguisticIssueCategory) CategoryFor(TextEdit edit) => edit.EditType switch
    {
        TextEditType.PunctuationInsertion
            or TextEditType.PunctuationDeletion
            or TextEditType.PunctuationReplacement
            => (IssueCategory.Punctuation, LinguisticIssueCategory.PunctuationRecommendation),

        TextEditType.WhitespaceCorrection
            => (IssueCategory.Readability, LinguisticIssueCategory.ExtraSpace),

        TextEditType.WordInsertion or TextEditType.WordDeletion
            => (IssueCategory.Grammar, LinguisticIssueCategory.AgreementError),

        _ => InferReplacementCategory(edit.Original, edit.Replacement),
    };

    private static (IssueCategory, LinguisticIssueCategory) InferReplacementCategory(
        string original,
        string replacement)
    {
        var originalLetters = new string(original.Where(char.IsLetter).ToArray());
        var replacementLetters = new string(replacement.Where(char.IsLetter).ToArray());

        // Same letters, different string: the change is in case or in the marks around it.
        if (string.Equals(originalLetters, replacementLetters, StringComparison.Ordinal))
        {
            return (IssueCategory.Punctuation, LinguisticIssueCategory.PunctuationRecommendation);
        }

        if (string.Equals(originalLetters, replacementLetters, StringComparison.OrdinalIgnoreCase))
        {
            return (IssueCategory.Orthography, LinguisticIssueCategory.EndingError);
        }

        // A one- or two-letter change to a word of this length is a misspelling; anything
        // larger is the model choosing a different word, which is a grammatical claim.
        if (originalLetters.Length > 0
            && Math.Abs(original.Length - replacement.Length) <= 2
            && Levenshtein(originalLetters.ToLowerInvariant(), replacementLetters.ToLowerInvariant()) <= 2)
        {
            return (IssueCategory.Orthography, LinguisticIssueCategory.Typo);
        }

        return (IssueCategory.Grammar, LinguisticIssueCategory.AgreementError);
    }

    /// <summary>
    /// What the popup says. Generic text ("Нужна запятая") tells the user nothing they could
    /// not see; the edit type is known here, so the explanation names the change.
    /// </summary>
    private static string ExplanationFor(TextEdit edit) => edit.EditType switch
    {
        TextEditType.PunctuationInsertion => "Расширенный анализ предлагает добавить знак препинания.",
        TextEditType.PunctuationDeletion => "Расширенный анализ считает этот знак препинания лишним.",
        TextEditType.PunctuationReplacement => "Расширенный анализ предлагает другой знак препинания.",
        TextEditType.WhitespaceCorrection => "Исправлены пробелы между словами.",
        TextEditType.WordInsertion => "Расширенный анализ предлагает добавить слово.",
        TextEditType.WordDeletion => "Расширенный анализ считает это слово лишним.",
        TextEditType.PhraseReplacement => "Расширенный анализ предлагает переформулировать это сочетание слов.",
        _ => "Исправление по результатам расширенного анализа.",
    };

    private static string TitleFor(IssueCategory category) => category switch
    {
        IssueCategory.Punctuation => "Пунктуация",
        IssueCategory.Orthography => "Орфография",
        IssueCategory.Grammar => "Грамматика",
        IssueCategory.Style => "Стиль",
        IssueCategory.Readability => "Оформление",
        _ => "Замечание"
    };

    /// <summary>
    /// Which edits may be applied without the user reading them first.
    /// </summary>
    /// <remarks>
    /// A phrase replacement never qualifies, however short: it is the shape that means the
    /// alignment found no smaller unit, and silently rewriting several words at once is the
    /// worst thing an 11 %-precision source can be allowed to do.
    /// </remarks>
    private static bool IsSafeAuto(TextEdit edit)
    {
        if (edit.IsPhraseLevel || edit.EditType is TextEditType.WordInsertion or TextEditType.WordDeletion)
        {
            return false;
        }

        if (edit.Original.Length > 40 || edit.Replacement.Length > 48)
        {
            return false;
        }

        var letterDelta = Math.Abs(
            edit.Original.Count(char.IsLetter) - edit.Replacement.Count(char.IsLetter));
        return letterDelta <= 3;
    }

    private static int Levenshtein(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;
        var d = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) d[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) d[0, j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                d[i, j] = Math.Min(
                    Math.Min(d[i - 1, j] + 1, d[i, j - 1] + 1),
                    d[i - 1, j - 1] + cost);
            }
        }

        return d[a.Length, b.Length];
    }
}
