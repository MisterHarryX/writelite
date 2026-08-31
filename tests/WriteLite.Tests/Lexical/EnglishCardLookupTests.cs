using System.IO;
using WriteLite.Services.Lexical;

namespace WriteLite.Tests.Lexical;

/// <summary>
/// The English half of the double-click dictionary card, against the shipped packs.
/// </summary>
/// <remarks>
/// The card is the only English dictionary most people will ever reach — it is what opens
/// when a word is double-clicked in a browser or a chat client. Its English path had no
/// lemma resolution, so a word in any inflected form reported that it was not in the
/// dictionary while its article sat one lookup away: "boxes" and "carried" are absent from
/// the pack, "box" and "carry" are in it. Double-clicking running prose lands on an
/// inflected form most of the time, so most of the English dictionary was unreachable.
/// </remarks>
[TestClass]
public sealed class EnglishCardLookupTests
{
    private static OfflineLexicalKnowledgeService Service()
    {
        var service = new OfflineLexicalKnowledgeService();
        service.LoadFromDirectory(Path.Combine(AppContext.BaseDirectory, "resources", "lexical"));
        return service;
    }

    private static async Task<LexicalLookupResult> Lookup(OfflineLexicalKnowledgeService service, string word)
        => await service.LookupAsync(new LexicalLookupRequest(
            Word: word,
            Sentence: word,
            FullText: word,
            Start: 0,
            Length: word.Length,
            SourceLanguage: LexicalLanguage.English));

    [TestMethod]
    public async Task ALemmaInThePackAnswersDirectly()
    {
        using var service = Service();
        var result = await Lookup(service, "box");

        Assert.AreEqual(LexicalLanguage.English, result.Language);
        Assert.IsFalse(result.IsEmpty, "the shipped English pack has an article for 'box'");
    }

    [TestMethod]
    public async Task APluralResolvesToItsLemma()
    {
        using var service = Service();

        // Absent from the pack in this form; present as "box".
        var result = await Lookup(service, "boxes");

        Assert.AreEqual(LexicalLanguage.English, result.Language);
        Assert.AreEqual("box", result.Lemma);
        Assert.IsFalse(result.IsEmpty);
    }

    [TestMethod]
    public async Task APastTenseResolvesToItsLemma()
    {
        using var service = Service();
        var result = await Lookup(service, "carried");

        Assert.AreEqual("carry", result.Lemma);
        Assert.IsFalse(result.IsEmpty);
    }

    [TestMethod]
    public async Task AResolvedFormSaysWhichLemmaItCameFrom()
    {
        using var service = Service();
        var result = await Lookup(service, "boxes");

        // Otherwise the card shows an article headed "box" for a word the user selected as
        // "boxes", with nothing on it explaining the difference.
        Assert.IsNotNull(result.SurfaceFormNote);
        Assert.Contains("box", result.SurfaceFormNote!);
    }

    [TestMethod]
    public async Task AWordInItsOwnRightIsNotReducedPastItself()
    {
        using var service = Service();

        // "running" has its own article. A stemmer would have handed back "run".
        var result = await Lookup(service, "running");

        Assert.AreEqual("running", result.Lemma);
        Assert.IsNull(result.SurfaceFormNote, "the selected word is the lemma; there is nothing to explain");
    }

    [TestMethod]
    public async Task AnInventedWordStaysAMiss()
    {
        using var service = Service();
        var result = await Lookup(service, "zzzqwertys");

        // The reduction must not turn a miss into a wrong article: no candidate of
        // "zzzqwertys" is in the pack, so the answer is still that the word is unknown.
        Assert.IsTrue(result.IsEmpty);
    }

    [TestMethod]
    public async Task AShortWordIsNeverReduced()
    {
        using var service = Service();

        // "bus" must not be served the article for "bu"; below four characters no rule runs.
        var result = await Lookup(service, "bus");

        Assert.IsTrue(
            result.IsEmpty || string.Equals(result.Lemma, "bus", StringComparison.OrdinalIgnoreCase),
            $"expected 'bus' or a miss, got '{result.Lemma}'");
    }
}
