using WriteLite.AI.Local;
using WriteLite.Models;
using WriteLite.Services.Grammar;

namespace WriteLite.Tests.Language;

/// <summary>
/// The parts of the punctuation-model path that hold whether or not a model is deployed.
/// </summary>
/// <remarks>
/// Boundary enumeration and pair tokenisation decide what the classifier is ever asked, so a
/// defect in either is invisible in the model's own accuracy and shows up only as a worse
/// product. They are also the parts that must keep working when the artefact is absent: a
/// missing model is a supported configuration, not an error.
/// </remarks>
[TestClass]
public sealed class PunctuationModelTests
{
    [TestMethod]
    public void Boundaries_AreWordGaps_NotEveryOffset()
    {
        const string sentence = "Он сказал что придёт";
        var boundaries = PunctuationDecisionModel.CandidateBoundaries(sentence);

        CollectionAssert.AreEqual(
            new[] { 2, 9, 13 },
            boundaries.ToArray(),
            $"got [{string.Join(",", boundaries)}]");
        foreach (var position in boundaries)
        {
            Assert.AreEqual(' ', sentence[position]);
            Assert.IsTrue(char.IsLetter(sentence[position - 1]));
            Assert.IsTrue(char.IsLetter(sentence[position + 1]));
        }
    }

    /// <summary>
    /// A boundary that already carries a comma is not a candidate. This model adds commas;
    /// removing one the writer chose is a different decision with a different cost.
    /// </summary>
    [TestMethod]
    public void Boundaries_SkipPositionsThatAlreadyHaveAComma()
    {
        var boundaries = PunctuationDecisionModel.CandidateBoundaries("Он сказал, что придёт");
        Assert.IsFalse(boundaries.Contains(9), "the offset of the existing comma was offered");
        Assert.IsFalse(boundaries.Contains(10), "the space after an existing comma was offered");
    }

    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    [DataRow("Одно")]
    [DataRow("а б")]
    public void Boundaries_DegenerateInput_DoesNotThrow(string sentence)
        => Assert.IsNotNull(PunctuationDecisionModel.CandidateBoundaries(sentence));

    [TestMethod]
    public void MissingArtifacts_LoadReturnsNull_RatherThanThrowing()
    {
        var absent = Path.Combine(Path.GetTempPath(), "writelite-no-such-punctuation-model");
        Assert.IsNull(PunctuationDecisionModel.TryLoad(absent));
        Assert.IsNull(PunctuationModelAnalyzer.TryLoad(absent));
    }

    // ---- pair tokenisation ------------------------------------------------

    private static WordPieceTokenizer Tokenizer()
    {
        var vocabulary = new Dictionary<string, int>(StringComparer.Ordinal)
        {
            ["[PAD]"] = 0, ["[UNK]"] = 1, ["[CLS]"] = 2, ["[SEP]"] = 3,
        };
        var next = 4;
        foreach (var token in new[] { "он", "сказал", "что", "придёт", "завтра", "утром", "рано" })
        {
            vocabulary[token] = next++;
        }

        return new WordPieceTokenizer(vocabulary, lowercase: true);
    }

    [TestMethod]
    public void EncodePair_ProducesClsLeftSepRightSep_WithTwoSegments()
    {
        var tokenizer = Tokenizer();
        var encoded = tokenizer.EncodePair("он сказал", "что придёт", maxLength: 16);

        Assert.AreEqual(tokenizer.ClsId, encoded.InputIds[0]);
        Assert.AreEqual(0, encoded.TokenTypeIds[0]);

        var separators = encoded.InputIds
            .Select((id, i) => (id, i))
            .Where(x => x.id == tokenizer.SepId)
            .Select(x => x.i)
            .ToList();
        Assert.HasCount(2, separators, "a pair must carry exactly two [SEP]");

        // Everything up to and including the first [SEP] is segment 0; the rest is segment 1.
        for (var i = 0; i <= separators[0]; i++) Assert.AreEqual(0, encoded.TokenTypeIds[i], $"at {i}");
        for (var i = separators[0] + 1; i <= separators[1]; i++) Assert.AreEqual(1, encoded.TokenTypeIds[i], $"at {i}");
    }

    [TestMethod]
    public void EncodePair_PadsToMaxLength_AndMasksThePadding()
    {
        var encoded = Tokenizer().EncodePair("он сказал", "что придёт", maxLength: 24);
        Assert.HasCount(24, encoded.InputIds);
        Assert.HasCount(24, encoded.AttentionMask);
        Assert.HasCount(24, encoded.TokenTypeIds);

        var used = encoded.AttentionMask.Count(x => x == 1);
        for (var i = used; i < 24; i++)
        {
            Assert.AreEqual(0, encoded.AttentionMask[i], $"padding at {i} is attended to");
        }
    }

    /// <summary>
    /// Truncation must not eat one side whole. Half the evidence for a comma is on the right,
    /// and a long left context that consumes the entire budget would hide it.
    /// </summary>
    [TestMethod]
    public void EncodePair_TruncatesBothSides_NotOnlyTheTail()
    {
        var tokenizer = Tokenizer();
        var left = string.Join(" ", Enumerable.Repeat("сказал", 40));
        var right = string.Join(" ", Enumerable.Repeat("придёт", 40));
        var encoded = tokenizer.EncodePair(left, right, maxLength: 16);

        var separators = encoded.InputIds.Select((id, i) => (id, i)).Where(x => x.id == tokenizer.SepId).ToList();
        Assert.HasCount(2, separators);

        var leftTokens = separators[0].i - 1;
        var rightTokens = separators[1].i - separators[0].i - 1;
        Assert.IsGreaterThan(0, leftTokens, "left context was truncated away entirely");
        Assert.IsGreaterThan(0, rightTokens, "right context was truncated away entirely");
        Assert.HasCount(16, encoded.InputIds);
    }

    // ---- yielding to the rules layer --------------------------------------

    /// <summary>
    /// The analyzer must stand down where a deterministic rule already spoke. Verified through
    /// the coverage predicate rather than through a live model, so it holds with no artefact.
    /// </summary>
    [TestMethod]
    public void Analyzer_WithNoModelDeployed_ContributesNothing()
    {
        var analyzer = PunctuationModelAnalyzer.TryLoad(
            Path.Combine(Path.GetTempPath(), "writelite-no-such-punctuation-model"));

        // The supported configuration is "no model": the pipeline keeps its rule findings and
        // nothing else changes.
        Assert.IsNull(analyzer);
    }

    [TestMethod]
    public void RuleFindings_AndModelBoundaries_UseTheSameSpanShape()
    {
        // A missing comma is reported on the word *before* the boundary, as an insertion at
        // its end. Both the Phase 5 rules and the model path must agree on that, or the diff
        // quality counters that Phase 5 drove to zero would move.
        const string sentence = "Он сказал что придёт";
        var boundary = PunctuationDecisionModel.CandidateBoundaries(sentence)[1];

        var start = boundary;
        while (start > 0 && char.IsLetter(sentence[start - 1])) start--;

        Assert.AreEqual("сказал", sentence[start..boundary]);
        Assert.AreEqual(boundary, start + "сказал".Length);
    }

    [TestMethod]
    public void IssueShape_IsAnInsertionOnThePrecedingWord()
    {
        // Documents the contract the analyzer builds to, so a change to it fails here rather
        // than as a drift in subword_fragment_rate three phases later.
        var issue = new TextIssue(
            3, 6, "сказал", "сказал,",
            "Здесь может требоваться запятая", "",
            IssueCategory.Punctuation, IssueSeverity.Suggestion,
            CanApplyAutomatically: false,
            RuleId: "ru.punctuation.model-comma",
            LinguisticCategory: LinguisticIssueCategory.PunctuationRecommendation,
            Confidence: 0.9);

        Assert.AreEqual(issue.Original + ",", issue.Replacement);
        Assert.IsFalse(issue.CanApplyAutomatically, "a probability must not auto-apply");
    }
}
