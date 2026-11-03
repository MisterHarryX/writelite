using System.IO;
using WriteLite.Services.FieldActions;
using WriteLite.Services.Translation;

namespace WriteLite.Tests.FieldActions;

[TestClass]
public sealed class TranslatorTests
{
    private static string? ModelRoot()
    {
        var root = Path.Combine(AppContext.BaseDirectory, "models", "writelite-translate");
        if (Directory.Exists(root)) return root;
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "models", "writelite-translate"));
        return Directory.Exists(repo) ? repo : null;
    }

    [TestMethod]
    public async Task RussianToEnglishMatchesPythonReference()
    {
        var root = ModelRoot();
        if (root is null) Assert.Inconclusive("translation models not present");
        using var translator = new MarianTranslator(root);
        // Python with the same int8 graphs (ORT 1.28) produced these; ORT 1.20 may round a close
        // second-best token differently, so the check is on content words, not exact strings.
        var cases = new (string Ru, string[] Expected)[]
        {
            ("Я вчера не был дома, потому что задержался на работе.", ["home", "yesterday", "work"]),
            ("Не могли бы вы перенести встречу на четверг?", ["meeting", "Thursday"]),
            ("Привет, как дела?", ["Hey", "how"]),
        };
        foreach (var (ru, expected) in cases)
        {
            var result = await translator.TranslateAsync(ru, "ru", "en");
            Console.WriteLine($"{ru} -> {result}");
            foreach (var word in expected) StringAssert.Contains(result, word);
        }
    }

    [TestMethod]
    public async Task EnglishToRussianKeepsLinesAndSentences()
    {
        var root = ModelRoot();
        if (root is null) Assert.Inconclusive("translation models not present");
        using var translator = new MarianTranslator(root);
        var result = await translator.TranslateAsync("Hello, how are you?\nThe meeting is postponed until Thursday.", "en", "ru");
        var lines = result.Split('\n');
        Assert.AreEqual(2, lines.Length, result);
        StringAssert.Contains(lines[0], "Привет");
        StringAssert.Contains(lines[1], "четверг");
    }

    [TestMethod]
    public void DirectionFollowsScript()
    {
        Assert.IsTrue(LanguageDetector.IsMostlyLatin("Hello there, мир"));
        Assert.IsFalse(LanguageDetector.IsMostlyLatin("Привет, world"));
        Assert.AreEqual("Перевести на английский", FieldTextActionService.Title(FieldTextAction.Translate, "Привет"));
        Assert.AreEqual("Перевести на русский", FieldTextActionService.Title(FieldTextAction.Translate, "Hello"));
    }

    [TestMethod]
    public void SentenceAroundCaret()
    {
        const string text = "Первое предложение. Второе, где курсор. Третье!";
        var caret = text.IndexOf("курсор", StringComparison.Ordinal);
        var (start, length) = FieldTextScope.SentenceAround(text, caret);
        Assert.AreEqual("Второе, где курсор.", text.Substring(start, length));
    }
}
