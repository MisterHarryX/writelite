using System.IO;
using WriteLite.Models;
using WriteLite.Services;
using WriteLite.Services.Grammar;

namespace WriteLite.Tests.Language;

/// <summary>
/// WriteLite-Punctuation-v1 as a shipped component: packaged, discoverable beside the
/// executable, and able to see more than one sentence.
/// </summary>
/// <remarks>
/// Phase 6 accepted the model behind a benchmark flag. Everything here is about the
/// difference between that and a product: the artefacts have to be in the build output rather
/// than only in the repository, and the analyzer has to work on a document rather than only
/// on a corpus item.
/// </remarks>
[TestClass]
public sealed class PunctuationModelIntegrationTests
{
    private static string DeployedModelDirectory()
        => Path.Combine(AppContext.BaseDirectory, "models", "writelite-punctuation");

    /// <summary>
    /// A deliberately loose threshold, for the tests that are about plumbing.
    /// </summary>
    /// <remarks>
    /// The shipping threshold is 0.996 and fires on roughly a quarter of true commas, by
    /// design — it was chosen against a false-positive budget, not for recall. Tests that
    /// check offset mapping, protected spans and cancellation need a finding to exist
    /// before they can check where it landed, and pinning them to the operating point
    /// would make them assertions about the model's confidence rather than about the code
    /// under test. The operating point itself is pinned once, in
    /// <see cref="TheDeployedArtefactLoadsFromItsPackagedLocation"/>.
    /// </remarks>
    private const double ProbeThreshold = 0.5;

    private static PunctuationModelAnalyzer LoadForProbing()
    {
        var analyzer = PunctuationModelAnalyzer.TryLoad(DeployedModelDirectory(), ProbeThreshold);
        Assert.IsNotNull(analyzer);
        return analyzer;
    }

    [TestMethod]
    public void TheModelIsPackagedBesideTheExecutable()
    {
        // Discovery falls back to walking up to WriteLite.sln, which exists in a working copy
        // and never in an install. Without this test a missing Content item would look like a
        // working model everywhere it was tried and load nothing for a user.
        var directory = DeployedModelDirectory();

        Assert.IsTrue(File.Exists(Path.Combine(directory, "model.onnx")), directory);
        Assert.IsTrue(File.Exists(Path.Combine(directory, "vocab.txt")), directory);
        Assert.IsTrue(File.Exists(Path.Combine(directory, "punctuation.json")), directory);
    }

    [TestMethod]
    public void TrainingCheckpointsAreNotPackaged()
    {
        // The PUNC-A/B/C checkpoints live in the same source directory and are 342 MB
        // between them. §57: no training artefacts in the release build.
        foreach (var experiment in new[] { "PUNC-A", "PUNC-B", "PUNC-C" })
        {
            Assert.IsFalse(
                Directory.Exists(Path.Combine(DeployedModelDirectory(), experiment)),
                experiment);
        }
    }

    [TestMethod]
    public void TheDeployedArtefactLoadsFromItsPackagedLocation()
    {
        using var analyzer = PunctuationModelAnalyzer.TryLoad(DeployedModelDirectory());

        Assert.IsNotNull(analyzer);
        Assert.AreEqual(0.996, analyzer.Threshold, 1e-9, "the frozen Phase 6 operating point");
        StringAssert.Contains(analyzer.ModelVersion, "PUNC-B");
    }

    [TestMethod]
    public void ADocumentIsScoredSentenceBySentence()
    {
        using var analyzer = LoadForProbing();

        // Phase 6 returned nothing at all once the text exceeded one sentence's worth of
        // characters, so this is the case that used to be silently empty. The assertion is
        // about offsets, not about how many commas the model wants: every finding must land
        // on the word it claims, wherever in the document that word is.
        var sentence = "Ну ладно тогда до встречи. ";
        var document = string.Concat(Enumerable.Repeat(sentence, 30));

        var findings = analyzer.Analyze(document, [], []);

        Assert.IsNotEmpty(findings, "a 30-sentence document produced no findings at all");
        foreach (var issue in findings)
        {
            Assert.AreEqual(
                issue.Original,
                document.Substring(issue.Start, issue.Length),
                $"offset {issue.Start} does not hold «{issue.Original}»");
            Assert.AreEqual(issue.Original + ",", issue.Replacement);
            Assert.IsFalse(issue.CanApplyAutomatically, "suggestion-only");
        }
    }

    [TestMethod]
    public void ASentenceTooLongForTheEncoderIsSkippedRatherThanTruncated()
    {
        using var analyzer = LoadForProbing();

        // Past 300 characters the boundaries fall outside the 64-token window and the model
        // would be scoring a truncated string.
        var long1 = string.Join(" ", Enumerable.Repeat("слово", 120)) + ".";
        Assert.IsGreaterThan(300, long1.Length);

        Assert.IsEmpty(analyzer.Analyze(long1, [], []));
    }

    [TestMethod]
    public void ProtectedSpansAndExistingFindingsAreRespectedAtDocumentOffsets()
    {
        using var analyzer = LoadForProbing();

        const string prefix = "Первое предложение здесь. ";
        const string target = "Ну ладно тогда до встречи.";
        var document = prefix + target;

        var withoutMask = analyzer.Analyze(document, [], []);
        Assert.IsNotEmpty(withoutMask, "precondition: the model has an opinion about this text");

        // Masking the whole second sentence must silence it — and the offsets used for the
        // mask are document offsets, which is what the sentence loop has to translate.
        var masked = analyzer.Analyze(document, [], [(prefix.Length, document.Length)]);
        Assert.IsEmpty(masked);
    }

    [TestMethod]
    public void CancellationIsObservedRatherThanIgnored()
    {
        using var analyzer = LoadForProbing();

        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var document = string.Concat(Enumerable.Repeat("Ну ладно тогда до встречи. ", 30));

        Assert.ThrowsExactly<OperationCanceledException>(
            () => analyzer.Analyze(document, [], [], cts.Token));
    }

    [TestMethod]
    public void AnAbsentModelDirectoryDegradesToNothing()
    {
        // §6: never a silent fallback to unsafe behaviour, and never a throw either.
        var missing = Path.Combine(Path.GetTempPath(), "writelite-no-punctuation-model", Guid.NewGuid().ToString("N"));

        Assert.IsNull(PunctuationModelAnalyzer.TryLoad(missing));
    }

    [TestMethod]
    public void ModelFindingsCarryTheFullCardContract()
    {
        using var analyzer = LoadForProbing();

        var findings = analyzer.Analyze("Ну ладно тогда до встречи.", [], []);
        Assert.IsNotEmpty(findings);

        foreach (var issue in findings)
        {
            Assert.IsNotEmpty(issue.Title);
            Assert.IsNotEmpty(issue.Explanation);
            Assert.AreEqual(IssueCategory.Punctuation, issue.Category);
            Assert.AreEqual(IssueSeverity.Suggestion, issue.Severity);
            Assert.AreEqual("ru.punctuation.model-comma", issue.RuleId);
            Assert.IsGreaterThanOrEqualTo(analyzer.Threshold, issue.Confidence);
        }
    }
}
