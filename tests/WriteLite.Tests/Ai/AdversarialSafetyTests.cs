using WriteLite.Models;
using WriteLite.Services.Ai;
using WriteLite.Services.Lexical;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.Ai;

/// <summary>
/// The changes that must never happen, whatever any model proposes.
/// </summary>
/// <remarks>
/// These are correctness failures rather than quality ones. A missed comma is a bad
/// suggestion; inverting a negation, changing a percentage or renaming a product silently
/// alters what the user said, and the user has no reason to re-read a sentence the tool
/// claims it merely tidied.
///
/// The acceptance policy is the last gate before a model finding reaches the document, so
/// these assert against it directly rather than through the benchmark, where a single bad
/// item would be invisible inside an aggregate.
/// </remarks>
[TestClass]
public sealed class AdversarialSafetyTests
{
    private static LocalSpellChecker? _checker;
    private static LexicalSignalService? _signals;

    [ClassInitialize]
    public static void Load(TestContext _)
    {
        _checker = new LocalSpellChecker();
        _signals = LexicalSignalService.TryCreate(_checker);
    }

    [ClassCleanup]
    public static void Unload() => _checker?.Dispose();

    private static LocalAiRoutingPolicy Policy() => new(new LocalAiRoutingOptions(), _signals);

    private static TextIssue AiEdit(
        string sentence,
        string original,
        string replacement,
        IssueCategory category = IssueCategory.Grammar,
        double confidence = 0.99)
    {
        var start = sentence.IndexOf(original, StringComparison.Ordinal);
        Assert.IsGreaterThanOrEqualTo(0, start, $"test fixture: '{original}' not in the sentence");
        return new TextIssue(start, original.Length, original, replacement, "t", "e", category,
            IssueSeverity.Warning, false, "WL-AI-GRAMMAR", LinguisticIssueCategory.Typo, confidence);
    }

    /// <summary>
    /// Negation inversion — the single most damaging edit this product could make.
    /// </summary>
    [TestMethod]
    public void NegationIsNeverDroppedByAnUnsupportedAiEdit()
    {
        const string sentence = "Путин не подписал документ.";
        var decision = Policy().ShouldAccept(sentence, AiEdit(sentence, "не подписал", "подписал"), []);

        Assert.IsFalse(decision.Accept, "an unsupported AI edit was allowed to remove a negation");
    }

    [TestMethod]
    public void NumbersAndPercentagesAreNotRewritten()
    {
        const string sentence = "Стоимость выросла с 15% до 18%.";

        foreach (var (original, replacement) in new[] { ("15", "50"), ("18", "80") })
        {
            var decision = Policy().ShouldAccept(sentence, AiEdit(sentence, original, replacement), []);
            Assert.IsFalse(decision.Accept, $"an AI edit rewrote {original}% to {replacement}%");
        }
    }

    [TestMethod]
    public void ProductAndVersionIdentifiersSurvive()
    {
        const string sentence = "WriteLite 2.0 использует Qwen2.5.";

        foreach (var (original, replacement) in new[]
                 {
                     ("WriteLite", "Райтлайт"),
                     ("Qwen2.5", "Qwen 2,5"),
                 })
        {
            var decision = Policy().ShouldAccept(sentence, AiEdit(sentence, original, replacement), []);
            Assert.IsFalse(decision.Accept, $"an AI edit rewrote the identifier '{original}'");
        }
    }

    /// <summary>
    /// A file path is not prose, and the router should never have sent it to a model.
    /// Asserted at the routing gate rather than the acceptance gate because the cheapest
    /// protection is not asking.
    /// </summary>
    [TestMethod]
    public void FilePathsAreNeverRoutedToTheModel()
    {
        foreach (var text in new[]
                 {
                     @"C:\Users\Test\file.txt",
                     "https://example.com/api/v1/items?id=42",
                     "user@example.com",
                 })
        {
            var decision = Policy().ShouldConsult(text, []);
            Assert.IsFalse(decision.Consult, $"'{text}' was routed to the model ({decision.Reason})");
        }
    }

    /// <summary>
    /// Mixed Russian/English technical prose must keep its English terms.
    /// </summary>
    [TestMethod]
    public void EnglishTechnicalTermsInRussianProseAreNotTranslatedAway()
    {
        const string sentence = "Разработчик открыл pull request в репозитории.";
        var decision = Policy().ShouldAccept(
            sentence, AiEdit(sentence, "pull", "тянуть"), []);

        Assert.IsFalse(decision.Accept, "an AI edit translated a technical term");
    }

    /// <summary>
    /// The policy must not be defeated by simply asserting high confidence: an unsupported
    /// lexical edit is held to the strictest bar regardless of what the model claims.
    /// </summary>
    [TestMethod]
    public void MaximumConfidenceDoesNotBypassTheUnsupportedBar()
    {
        const string sentence = "Наша компания открыла новый офис в центре города.";
        var decision = Policy().ShouldAccept(
            sentence,
            AiEdit(sentence, "компания", "кампания", IssueCategory.Orthography, confidence: 1.0),
            []);

        Assert.IsFalse(
            decision.Accept,
            "confidence 1.0 bypassed the unsupported-edit policy; the model can always claim 1.0");
    }

    [TestMethod]
    public void FiniteVerbIsNotChangedToAnInfinitive()
    {
        const string sentence = "После проверки мы отправим сборку заказчику.";
        var decision = Policy().ShouldAccept(
            sentence,
            AiEdit(sentence, "отправим", "отправить", IssueCategory.Grammar, confidence: 0.99),
            []);

        Assert.IsFalse(decision.Accept);
        Assert.AreEqual("finite-verb-to-infinitive", decision.Reason);
    }

    [TestMethod]
    public void UnsupportedGrammarEditDoesNotChangeTheLemma()
    {
        const string sentence = "Команда быстро проверит готовую сборку.";
        var decision = Policy().ShouldAccept(
            sentence,
            AiEdit(sentence, "проверит", "отправит", IssueCategory.Grammar, confidence: 0.99),
            []);

        Assert.IsFalse(decision.Accept);
        Assert.AreEqual("unsupported-lemma-rewrite", decision.Reason);
    }
}
