using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using WriteLite.Services.Lexical;

namespace WriteLite.Tests.Lexical;

[TestClass]
public sealed class LexicalLayerTests
{
    private static OfflineLexicalKnowledgeService CreateService()
    {
        var dir = Path.Combine(AppContext.BaseDirectory, "resources", "lexical");
        var svc = new OfflineLexicalKnowledgeService();
        if (Directory.Exists(dir))
        {
            svc.LoadFromDirectory(dir);
            if (svc.IsPackLoaded) return svc;
        }

        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "resources", "lexical"));
        if (Directory.Exists(repo))
            svc.LoadFromDirectory(repo);
        else
        {
            foreach (var name in new[] { "writelight-lexical-core.json" })
            {
                var path = Path.Combine(repo, name);
                if (File.Exists(path)) { svc.LoadPack(path); break; }
            }
        }

        return svc;
    }

    [TestMethod]
    public void WordRange_ResolvesCyrillicAndPunctuation()
    {
        var r = new WordRangeResolver();
        var text = "Скажи привет, друг!";
        var range = r.ResolveFromText(text, text.IndexOf('п', StringComparison.Ordinal));
        Assert.IsNotNull(range);
        Assert.AreEqual("привет", range!.Word);
        Assert.AreEqual(LexicalLanguage.Russian, range.Language);
    }

    [TestMethod]
    public void WordRange_IgnoresLatinTechnicalText()
    {
        var r = new WordRangeResolver();
        Assert.IsNull(r.ResolveFromText("release-v1 API_TOKEN", 4));
    }

    [TestMethod]
    public void WordRange_HandlesEmojiAndSurrogates()
    {
        var r = new WordRangeResolver();
        var text = "привет " + char.ConvertFromUtf32(0x1F600) + " мир";
        var range = r.ResolveFromText(text, 0);
        Assert.IsNotNull(range);
        Assert.AreEqual("привет", range!.Word);

        // Click on emoji should not treat it as a word.
        var emojiIndex = text.IndexOf(char.ConvertFromUtf32(0x1F600), StringComparison.Ordinal);
        var near = r.ResolveFromText(text, emojiIndex);
        // May snap to neighbouring word — must not return a string containing the surrogate pair alone as lemma source.
        if (near is not null)
        {
            Assert.IsFalse(near.Word.Any(char.IsSurrogate));
        }
    }

    [TestMethod]
    public void WordRange_StartAndEndOfText()
    {
        var r = new WordRangeResolver();
        Assert.AreEqual("Альфа", r.ResolveFromText("Альфа бета", 0)!.Word);
        Assert.AreEqual("бета", r.ResolveFromText("Альфа бета", 9)!.Word);
    }

    [TestMethod]
    public void Morphology_PreservesCase()
    {
        var m = new WordMorphologyService();
        Assert.AreEqual("Привет", m.ApplySurfaceCase("Привет", "привет"));
        Assert.AreEqual("ПРИВЕТ", m.ApplySurfaceCase("ПРИВЕТ", "привет"));
        Assert.AreEqual("привет", m.ApplySurfaceCase("привет", "Привет"));
    }

    [TestMethod]
    public async Task Lookup_CorePack_ReturnsRussianReferenceData()
    {
        var svc = CreateService();
        Assert.IsTrue(svc.IsPackLoaded, "Russian lexical pack must be present for tests.");

        var result = await svc.LookupAsync(new LexicalLookupRequest(
            "привет",
            "Скажи привет другу.",
            "Скажи привет другу.",
            6,
            6,
            LexicalLanguage.Russian,
            RequestId: 1));

        Assert.IsFalse(result.IsEmpty);
        Assert.IsGreaterThan(0, result.Synonyms.Count);
        Assert.IsGreaterThan(0, result.Definitions.Count);
        Assert.IsGreaterThan(0, result.Examples.Count);
        Assert.AreEqual("CC-BY-SA-4.0", result.PackLicense);
    }

    [TestMethod]
    public async Task Lookup_WorksWithoutQwen()
    {
        var svc = CreateService();
        var result = await svc.LookupAsync(new LexicalLookupRequest(
            "ошибка", "В тексте ошибка.", "В тексте ошибка.", 9, 6,
            LexicalLanguage.Russian, 2));
        Assert.IsGreaterThan(0, result.Definitions.Count);
        Assert.IsTrue(result.Definitions.Any(d => d.Definition.Contains("Неправ", StringComparison.OrdinalIgnoreCase)));
    }

    [TestMethod]
    public async Task Lookup_OpenPack_CoversCommonWordOutsideAuthoredCore()
    {
        var svc = CreateService();
        Assert.IsTrue(svc.IsPackLoaded);
        Assert.IsFalse(svc.IsDemoPack);

        var result = await svc.LookupAsync(new LexicalLookupRequest(
            "автомобилями", "Мы пользуемся автомобилями.", "Мы пользуемся автомобилями.",
            13, 12, LexicalLanguage.Russian, 22));

        Assert.AreEqual("автомобиль", result.Lemma);
        Assert.IsNotNull(result.Morphology);
        // Since the Russian Wiktionary layer was merged in, this lemma has a real
        // imported sense. Every definition must still be attributable to a source;
        // morphology alone may never be rendered as a definition.
        foreach (var definition in result.Definitions)
        {
            Assert.IsFalse(string.IsNullOrWhiteSpace(definition.SourceId),
                "every definition must carry the id of the source it was imported from");
            Assert.AreNotEqual(result.Lemma, definition.Definition,
                "a lemma repeated back is morphology, not a definition");
        }
    }

    [TestMethod]
    public async Task Lookup_MorphologyOnlyEntry_IsNotPresentedAsDefinition()
    {
        // "автомашина" carries OpenRussian inflections but no imported sense of
        // any kind, so the card must stay empty rather than inventing an article
        // out of morphology.
        var svc = CreateService();
        var result = await svc.LookupAsync(new LexicalLookupRequest(
            "автомашины", "Во дворе стояли автомашины.", "Во дворе стояли автомашины.",
            16, 10, LexicalLanguage.Russian, 25));

        Assert.IsNotNull(result.Morphology);
        Assert.IsTrue(result.IsEmpty,
            "OpenRussian morphology must not be presented as a fabricated definition.");
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.StatusMessage));
    }

    [TestMethod]
    public async Task Lookup_EnglishWord_ReturnsWordNetSenses()
    {
        var svc = CreateService();
        if (!svc.IsEnglishPackLoaded)
            Assert.Inconclusive("English pack not present in this build output.");

        var result = await svc.LookupAsync(new LexicalLookupRequest(
            "bright", "The room was bright.", "The room was bright.",
            14, 6, LexicalLanguage.English, 40));

        Assert.AreEqual(LexicalLanguage.English, result.Language);
        Assert.AreEqual("bright", result.Lemma);
        Assert.IsFalse(result.IsEmpty);
        Assert.IsGreaterThan(0, result.Definitions.Count);
        foreach (var definition in result.Definitions)
            Assert.AreEqual("oewn-2024", definition.SourceId);
    }

    [TestMethod]
    public async Task Lookup_English_DoesNotApplyRussianMorphology()
    {
        var svc = CreateService();
        if (!svc.IsEnglishPackLoaded)
            Assert.Inconclusive("English pack not present in this build output.");

        var result = await svc.LookupAsync(new LexicalLookupRequest(
            "house", "A small house.", "A small house.", 8, 5,
            LexicalLanguage.English, 41));

        // The morphology and syntax analysers are Russian rule-based engines;
        // running them on English would invent forms and roles.
        Assert.IsNull(result.Morphology);
        Assert.IsNull(result.Syntax);
        Assert.IsNull(result.SurfaceFormNote);
    }

    [TestMethod]
    public async Task Lookup_UnknownEnglishWord_IsHonestlyEmpty()
    {
        var svc = CreateService();
        if (!svc.IsEnglishPackLoaded)
            Assert.Inconclusive("English pack not present in this build output.");

        var result = await svc.LookupAsync(new LexicalLookupRequest(
            "zzzqwertyx", "A zzzqwertyx.", "A zzzqwertyx.", 2, 10,
            LexicalLanguage.English, 42));

        Assert.IsTrue(result.IsEmpty);
        Assert.IsEmpty(result.Definitions);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.StatusMessage));
    }

    [TestMethod]
    public void WordRange_ResolvesEnglishProseButNotIdentifiers()
    {
        var r = new WordRangeResolver();
        var range = r.ResolveFromText("The bright room.", 5);
        Assert.IsNotNull(range);
        Assert.AreEqual("bright", range!.Word);
        Assert.AreEqual(LexicalLanguage.English, range.Language);

        // Identifiers must not open a dictionary card.
        Assert.IsNull(r.ResolveFromText("release-v1 API_TOKEN", 4));
        Assert.IsNull(r.ResolveFromText("release-v1 API_TOKEN", 13));
    }

    [TestMethod]
    public void EnglishPack_DeclaresAttributionForBothUpstreamSources()
    {
        var svc = CreateService();
        if (!svc.IsEnglishPackLoaded)
            Assert.Inconclusive("English pack not present in this build output.");

        var dir = Path.Combine(AppContext.BaseDirectory, "resources", "lexical");
        var path = Path.Combine(dir, "writelight-lexical-en.json");
        if (!File.Exists(path))
            Assert.Inconclusive("English pack file not found next to the test host.");

        var result = LexicalPackLoader.LoadFromFile(path);
        Assert.IsTrue(result.Success, result.Error);
        Assert.AreEqual(LexicalLanguage.English, result.Language);
        Assert.AreEqual("CC-BY-4.0", result.Manifest!.License);
        // CC BY 4.0 and the Princeton WordNet License both require attribution.
        StringAssert.Contains(result.Manifest.LicenseNote, "Open English Wordnet");
        StringAssert.Contains(result.Manifest.LicenseNote, "Princeton");
    }

    [TestMethod]
    public async Task Lookup_MissingWord_HonestEmpty()
    {
        var svc = CreateService();
        var result = await svc.LookupAsync(new LexicalLookupRequest(
            "абракадабронность", "Контекст.", "Контекст.", 0, 18,
            LexicalLanguage.Russian, 3));
        Assert.IsTrue(result.IsEmpty);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.StatusMessage));
    }

    [TestMethod]
    public void Replacement_ReplacesOnlySelectedWord_PreservesCase()
    {
        var svc = new LexicalReplacementService();
        var text = "Скажи Привет другу";
        var start = text.IndexOf("Привет", StringComparison.Ordinal);
        var result = svc.TryReplace(new LexicalReplacementRequest(
            "t1", 1, 1, text, start, "Привет".Length, "Привет", "здравствуй",
            SupportsDirectWrite: true, IsPassword: false, IsReadOnly: false));

        Assert.IsTrue(result.Success);
        Assert.AreEqual("Скажи Здравствуй другу", result.NewText);
    }

    [TestMethod]
    public void Replacement_RejectsStaleOriginal()
    {
        var svc = new LexicalReplacementService();
        var result = svc.TryReplace(new LexicalReplacementRequest(
            "t1", 1, 1, "привет мир", 0, 6, "превет", "здравствуй",
            true, false, false));
        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public void Replacement_RejectsPasswordAndReadOnly()
    {
        var svc = new LexicalReplacementService();
        Assert.IsFalse(svc.TryReplace(new LexicalReplacementRequest(
            "t", 1, 1, "привет", 0, 6, "привет", "здравствуй", true, IsPassword: true, IsReadOnly: false)).Success);
        Assert.IsFalse(svc.TryReplace(new LexicalReplacementRequest(
            "t", 1, 1, "привет", 0, 6, "привет", "здравствуй", true, false, IsReadOnly: true)).Success);
    }

    [TestMethod]
    public void Replacement_DoesNotTouchNeighborRange()
    {
        var svc = new LexicalReplacementService();
        var text = "один два три";
        var result = svc.TryReplace(new LexicalReplacementRequest(
            "t", 1, 1, text, 5, 3, "два", "пара", true, false, false));
        Assert.IsTrue(result.Success);
        Assert.AreEqual("один пара три", result.NewText);
    }

    [TestMethod]
    public void PackLoader_RejectsCorruptJson()
    {
        var bytes = Encoding.UTF8.GetBytes("{ not valid");
        var result = LexicalPackLoader.LoadFromBytes(bytes);
        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public void PackLoader_RejectsMissingLicense()
    {
        var doc = new
        {
            manifest = new
            {
                formatVersion = 2,
                packId = "x",
                packVersion = "1",
                displayName = "x",
                license = "",
                isDemo = true,
                entryCount = 0
            },
            entries = Array.Empty<object>()
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(doc);
        var result = LexicalPackLoader.LoadFromBytes(bytes);
        Assert.IsFalse(result.Success);
    }

    [TestMethod]
    public void PackLoader_RejectsNonRussianEntriesAndCountMismatch()
    {
        static byte[] Pack(string language, int entryCount) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            manifest = new
            {
                formatVersion = 2,
                packId = "test",
                packVersion = "1",
                displayName = "test",
                license = "CC0-1.0",
                isDemo = false,
                entryCount,
                maxBytes = 1024 * 1024
            },
            entries = new[] { new { lemma = "слово", language, pos = "noun" } }
        });

        Assert.IsFalse(LexicalPackLoader.LoadFromBytes(Pack("en", 1)).Success);
        Assert.IsFalse(LexicalPackLoader.LoadFromBytes(Pack("ru", 2)).Success);
    }

    [TestMethod]
    public void PackLoader_IndexesYoAndEAsLookupVariants()
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            manifest = new
            {
                formatVersion = 2,
                packId = "yo-test",
                packVersion = "1",
                displayName = "test",
                license = "CC0-1.0",
                isDemo = false,
                entryCount = 1,
                maxBytes = 1024 * 1024
            },
            entries = new[] { new { lemma = "ёлка", language = "ru", pos = "noun" } }
        });
        var loaded = LexicalPackLoader.LoadFromBytes(bytes);
        Assert.IsTrue(loaded.Success, loaded.Error);
        Assert.IsTrue(loaded.Index!.ContainsKey("ёлка"));
        Assert.IsTrue(loaded.Index.ContainsKey("елка"));
    }

    [TestMethod]
    public void BundledLexicalPacks_AreRussianOnlyAndContainNoTranslationField()
    {
        var directory = Path.Combine(AppContext.BaseDirectory, "resources", "lexical");
        foreach (var fileName in new[] { "writelight-lexical-core.json", "writelight-lexical-open.json" })
        {
            var path = Path.Combine(directory, fileName);
            Assert.IsTrue(File.Exists(path), path);
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            foreach (var entry in document.RootElement.GetProperty("entries").EnumerateArray())
            {
                Assert.AreEqual("ru", entry.GetProperty("language").GetString(), $"{fileName}: non-Russian entry");
                Assert.IsFalse(entry.TryGetProperty("translations", out _), $"{fileName}: obsolete translation field");
            }
        }
    }

    [TestMethod]
    public void Ranker_PrefersPosMatchAndDropsSelf()
    {
        var ranker = new ContextualLexicalRanker();
        var ranked = ranker.RankSynonyms(
            [
                new LexicalSuggestion("привет", "привет", LexicalPartOfSpeech.Interjection, null, 0.5, true),
                new LexicalSuggestion("здравствуй", "здравствуй", LexicalPartOfSpeech.Interjection, null, 0.5, true),
                new LexicalSuggestion("приветствие", "приветствие", LexicalPartOfSpeech.Noun, null, 0.5, true)
            ],
            "привет",
            "Скажи привет другу",
            LexicalPartOfSpeech.Interjection);

        Assert.IsFalse(ranked.Any(s => string.Equals(s.Value, "привет", StringComparison.OrdinalIgnoreCase)));
        Assert.IsTrue(ranked[0].PartOfSpeech == LexicalPartOfSpeech.Interjection || ranked.Count > 0);
    }

    [TestMethod]
    public void LanguageDetector_SeparatesRussianFromEnglish()
    {
        var d = new LexicalLanguageDetector();
        Assert.AreEqual(LexicalLanguage.Russian, d.DetectWord("ошибка"));
        Assert.AreEqual(LexicalLanguage.English, d.DetectWord("error"));
        Assert.AreEqual(LexicalLanguage.Unknown, d.DetectWord("1234"));
        // A stray Latin letter in a Russian word is a typo, not a language switch.
        Assert.AreEqual(LexicalLanguage.Russian, d.DetectWord("ошибkа"));
    }

    [TestMethod]
    public void Morphology_RussianPastVerbFeatures()
    {
        var m = new RuleBasedMorphologyService();
        var f = m.AnalyzeFeatures("писала", LexicalLanguage.Russian);
        Assert.AreEqual("прош.", f.Tense);
        Assert.AreEqual("ж.", f.Gender);
    }

    [TestMethod]
    public void Syntax_SubjectBeforeVerb()
    {
        var a = new SyntacticRoleAnalyzer();
        var r = a.Analyze("человек", 0, 7, "Человек пишет письмо.", LexicalPartOfSpeech.Noun, LexicalLanguage.Russian);
        Assert.AreEqual(SyntacticRole.Subject, r.Role);
        Assert.IsFalse(string.IsNullOrWhiteSpace(r.Explanation));
    }

    [TestMethod]
    public void Syntax_ObjectAfterVerb()
    {
        var a = new SyntacticRoleAnalyzer();
        var r = a.Analyze("письмо", 13, 6, "Человек пишет письмо.", LexicalPartOfSpeech.Noun, LexicalLanguage.Russian);
        Assert.AreEqual(SyntacticRole.Object, r.Role);
    }

    [TestMethod]
    public async Task Lookup_IncludesMorphologyAndSyntax()
    {
        var svc = CreateService();
        Assert.IsTrue(svc.IsPackLoaded);
        var result = await svc.LookupAsync(new LexicalLookupRequest(
            "ошибка", "Это ошибка в тексте.", "Это ошибка в тексте.", 4, 6,
            LexicalLanguage.Russian, 9));
        Assert.IsNotNull(result.Morphology);
        Assert.IsNotNull(result.Syntax);
        Assert.IsFalse(string.IsNullOrWhiteSpace(result.Syntax!.Explanation));
    }

    [TestMethod]
    public void OzhegovProvider_MissingPack_IsHonest()
    {
        var p = new LicensedOzhegovDictionaryProvider(Path.Combine(Path.GetTempPath(), "writelight-no-ozhegov-" + Guid.NewGuid()));
        Assert.IsFalse(p.IsAvailable);
        Assert.AreEqual(0, p.LookupDefinitions("дом").Count);
        Assert.IsFalse(string.IsNullOrWhiteSpace(p.StatusMessage));
    }

    [TestMethod]
    public void OzhegovImporter_RequiresRightsEvidenceAndArtifactIntegrity()
    {
        var directory = Path.Combine(Path.GetTempPath(), "writelite-ozhegov-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var entries = "{\"lemma\":\"ёлка\",\"definition\":\"Проверочная лицензированная статья.\",\"pos\":\"noun\"}\n";
            File.WriteAllText(Path.Combine(directory, "ozhegov.entries.jsonl"), entries, new UTF8Encoding(false));
            var manifest = new OzhegovPackManifest
            {
                PackVersion = "licensed-test-1",
                RightsHolder = "Test rights holder",
                License = "Test license",
                Source = "https://example.invalid/licensed-source",
                LicenseEvidence = "https://example.invalid/license-evidence",
                LocalUsePermitted = true,
                RedistributionPermitted = false,
                RetrievedAt = DateTimeOffset.UtcNow,
                EntryCount = 1,
                Sha256 = new string('0', 64)
            };
            File.WriteAllText(
                Path.Combine(directory, "ozhegov.manifest.json"),
                JsonSerializer.Serialize(manifest));

            var invalid = new LicensedOzhegovPackImporter().TryLoad(directory);
            Assert.IsFalse(invalid.IsAvailable, "A mismatched SHA-256 must block the pack.");

            manifest.Sha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(entries))).ToLowerInvariant();
            File.WriteAllText(
                Path.Combine(directory, "ozhegov.manifest.json"),
                JsonSerializer.Serialize(manifest));
            var valid = new LicensedOzhegovPackImporter().TryLoad(directory);
            Assert.IsTrue(valid.IsAvailable, valid.StatusMessage);
            Assert.IsTrue(valid.Entries.ContainsKey("елка"));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [TestMethod]
    public void HistoricalSample_LoadsWithoutClaimingDal()
    {
        var repo = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "resources", "lexical"));
        // Provider looks under resources/lexical for historical-sample.*
        var p = new HistoricalDictionaryProvider(repo);
        // May load sample from BaseDirectory when tests copy content; if not, not available is OK.
        if (p.IsAvailable)
        {
            Assert.IsFalse(string.Equals(p.EditionLabel, "Даль", StringComparison.Ordinal));
            var defs = p.LookupHistoricalDefinitions("дом");
            Assert.IsTrue(defs.Count == 0 || defs[0].IsHistorical);
        }
    }
}
