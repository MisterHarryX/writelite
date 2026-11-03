using WriteLite.Models;
using WriteLite.Services.Personal;
using WriteLite.Services.Settings;
using WriteLite.Services.Spelling;

namespace WriteLite.Tests.Personal;

/// <summary>
/// The personal style library: what it learns, what it refuses to learn, and the three
/// things «Мой стиль письма» does with it — predict the word, rank corrections, prefer «ё».
/// </summary>
[TestClass]
public sealed class PersonalStyleTests
{
    /// <summary>Every word is known except the ones a test marks as typos.</summary>
    private static Func<string, bool> KnownExcept(params string[] unknown)
    {
        var set = new HashSet<string>(unknown, StringComparer.OrdinalIgnoreCase);
        return word => !set.Contains(word);
    }

    private static PersonalStyleModel Learned(Func<string, bool> isKnown, params string[] texts)
    {
        var model = new PersonalStyleModel();
        foreach (var text in texts) model.Learn(text, isKnown, day: 20_000, includeTrailingSentence: true);
        return model;
    }

    // ── Learning ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void The_same_sentence_is_counted_once_however_often_it_is_offered()
    {
        var model = new PersonalStyleModel();
        var isKnown = KnownExcept();

        model.Learn("Сегодня отличная погода.", isKnown, 1, includeTrailingSentence: true);
        model.Learn("Сегодня отличная погода.", isKnown, 1, includeTrailingSentence: true);
        model.Learn("  сегодня   ОТЛИЧНАЯ погода. ", isKnown, 2, includeTrailingSentence: true);

        Assert.AreEqual(1, model.CountOf("погода"));
        Assert.AreEqual(3, model.TotalWords);
    }

    [TestMethod]
    public void An_unfinished_last_sentence_waits_until_it_is_finished()
    {
        var model = new PersonalStyleModel();
        var isKnown = KnownExcept();

        model.Learn("Первое предложение готово. Второе ещё пишет", isKnown, 1, includeTrailingSentence: false);
        Assert.AreEqual(1, model.CountOf("готово"));
        Assert.AreEqual(0, model.CountOf("пишет"));

        model.Learn("Первое предложение готово. Второе ещё пишется.", isKnown, 1, includeTrailingSentence: false);
        Assert.AreEqual(1, model.CountOf("готово"), "the finished sentence must not be counted twice");
        Assert.AreEqual(1, model.CountOf("пишется"));
    }

    [TestMethod]
    public void A_word_the_lexicon_rejects_is_never_vocabulary_but_is_listed_when_frequent()
    {
        var model = Learned(
            KnownExcept("извените"),
            "Извените, я опоздаю.",
            "Извените за задержку.",
            "Ещё раз извените.");

        Assert.AreEqual(0, model.CountOf("извените"));
        Assert.IsNull(model.Predict("изве"), "a misspelling must never be predicted back");

        var unknown = model.Snapshot().FrequentUnknown;
        Assert.AreEqual(1, unknown.Count);
        Assert.AreEqual(3, unknown[0].Count);
    }

    [TestMethod]
    public void Links_and_code_are_not_learned_as_the_users_phrasing()
    {
        var model = Learned(
            KnownExcept(),
            "Смотри https://example.com/страница там всё есть.",
            "var x = items[i]; if (x > 0) { return x; }");

        Assert.AreEqual(0, model.CountOf("смотри"));
        Assert.AreEqual(0, model.CountOf("return"));
    }

    // ── Prediction ────────────────────────────────────────────────────────────

    [TestMethod]
    public void A_word_written_twice_is_completed_from_its_prefix()
    {
        var model = Learned(
            KnownExcept(),
            "Спасибо за подробный ответ.",
            "Подробный план пришлю вечером.");

        var prediction = model.Predict("Нужен подр");

        Assert.IsNotNull(prediction);
        Assert.AreEqual("подробный", prediction.Word);
        Assert.AreEqual("обный", prediction.Completion);
        Assert.IsFalse(prediction.IsNextWord);
    }

    [TestMethod]
    public void A_word_seen_once_is_not_predicted()
    {
        var model = Learned(KnownExcept(), "Спасибо за подробный ответ.");
        Assert.IsNull(model.Predict("Нужен подр"));
    }

    [TestMethod]
    public void The_previous_words_choose_between_completions_of_the_same_prefix()
    {
        var model = Learned(
            KnownExcept(),
            "Хорошего вам вечера.",
            "Хорошего вам вечера и отдыха.",
            "Хорошего вам вечера, коллеги.",
            "Вечером созвонимся.",
            "Вечером пришлю.");

        var prediction = model.Predict("Хорошего вам веч");

        Assert.IsNotNull(prediction);
        Assert.AreEqual("вечера", prediction.Word);
    }

    [TestMethod]
    public void The_next_word_is_predicted_only_after_a_habit_is_established()
    {
        var model = Learned(
            KnownExcept(),
            "Заранее спасибо за помощь.",
            "Заранее спасибо, коллеги.",
            "Заранее спасибо всем.");

        var prediction = model.Predict("Заранее ");

        Assert.IsNotNull(prediction);
        Assert.IsTrue(prediction.IsNextWord);
        Assert.AreEqual("спасибо", prediction.Completion);

        var weak = Learned(KnownExcept(), "Заранее спасибо за помощь.");
        Assert.IsNull(weak.Predict("Заранее "), "one occurrence is not a habit");
    }

    [TestMethod]
    public void Nothing_is_predicted_at_the_start_of_a_sentence_or_inside_a_number()
    {
        var model = Learned(
            KnownExcept(),
            "Привет. Привет, как дела. Привет всем.",
            "Привет друзья.");

        Assert.IsNull(model.Predict("Готово. "));
        Assert.IsNull(model.Predict("Сумма 12"));
        Assert.IsNull(model.Predict(string.Empty));
    }

    [TestMethod]
    public void The_typed_letters_keep_their_case_and_names_keep_their_capital()
    {
        var model = Learned(
            KnownExcept(),
            "Летом поеду в Москву.",
            "В Москву приеду утром.",
            "Москву люблю.");

        var capitalised = model.Predict("Билеты в Мос");
        Assert.IsNotNull(capitalised);
        Assert.AreEqual("Москву", capitalised.Word);

        var shouted = model.Predict("ЛЕТИМ В МОС");
        Assert.IsNotNull(shouted);
        Assert.AreEqual("МОСКВУ", shouted.Word);
    }

    // ── Correction ranking ────────────────────────────────────────────────────

    [TestMethod]
    public void A_correction_the_user_made_before_is_ranked_first()
    {
        var model = new PersonalStyleModel();
        model.RecordCorrection("сдлать", "сделать");

        var ranking = model.Rank("сдлать", ["слать", "сделать", "сдать"]);

        Assert.AreEqual(PersonalRankReason.LearnedCorrection, ranking.Reason);
        Assert.AreEqual("сделать", ranking.Suggestions[0]);
        Assert.HasCount(3, ranking.Suggestions);
    }

    [TestMethod]
    public void A_correction_made_twice_is_offered_even_when_the_checker_missed_it()
    {
        var model = new PersonalStyleModel();
        model.RecordCorrection("шоб", "чтоб");
        model.RecordCorrection("шоб", "чтоб");

        var ranking = model.Rank("Шоб", ["шов", "шоу"], isKnownWord: _ => true);

        Assert.AreEqual(PersonalRankReason.LearnedCorrection, ranking.Reason);
        Assert.AreEqual("Чтоб", ranking.Suggestions[0], "the typed capital carries over");

        var once = new PersonalStyleModel();
        once.RecordCorrection("шоб", "чтоб");
        Assert.AreEqual(PersonalRankReason.None, once.Rank("шоб", ["шов", "шоу"], _ => true).Reason);
    }

    [TestMethod]
    public void A_word_the_user_writes_often_outranks_one_they_never_write()
    {
        var model = Learned(
            KnownExcept(),
            "Мыла много не бывает.",
            "Купи мыла.",
            "Без мыла никак.");

        var ranking = model.Rank("мла", ["мало", "мыла", "мгла"]);

        Assert.AreEqual(PersonalRankReason.FrequentWord, ranking.Reason);
        Assert.AreEqual("мыла", ranking.Suggestions[0]);

        Assert.AreEqual(PersonalRankReason.None, model.Rank("мла", ["мыла", "мало"]).Reason, "already first");
    }

    [TestMethod]
    public void A_correction_into_two_words_or_a_digit_is_not_a_spelling_habit()
    {
        using var service = PersonalStyleService.CreateInMemory();

        service.RecordCorrection("вообщем", "в общем");
        service.RecordCorrection("пять", "5");
        service.RecordCorrection("слово", "Слово");

        Assert.IsEmpty(service.Snapshot().Corrections);
    }

    // ── «ё» ──────────────────────────────────────────────────────────────────

    [TestMethod]
    public void A_demonstrated_yo_spelling_is_suggested_where_it_was_left_out()
    {
        var model = Learned(
            KnownExcept(),
            "Ещё раз спасибо.",
            "Напишу ещё вечером.",
            "Ещё не знаю.",
            "Всё готово.",
            "Всё хорошо.",
            "Всё получилось.");

        var found = model.FindYoPreferences("Еще немного, и все будет готово.");

        Assert.AreEqual(1, found.Count, "«все» is a different word from «всё» and must not be touched");
        Assert.AreEqual("Еще", found[0].Written);
        Assert.AreEqual("Ещё", found[0].Preferred);
    }

    [TestMethod]
    public void A_user_who_mixes_e_and_yo_in_a_word_is_left_alone()
    {
        var model = Learned(
            KnownExcept(),
            "Ещё раз.",
            "Ещё два.",
            "Ещё три.",
            "Еще четыре.");

        Assert.IsEmpty(model.FindYoPreferences("еще"));
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    [TestMethod]
    public void The_library_survives_a_save_and_reload()
    {
        var path = Path.Combine(Path.GetTempPath(), "wl-style-" + Guid.NewGuid().ToString("N"), "personal-style.json");
        try
        {
            using (var service = new PersonalStyleService(path, KnownExcept("извените")))
            {
                service.LearnNow("Спасибо за подробный ответ. Подробный план пришлю вечером. Извените.");
                service.RecordCorrection("сдлать", "сделать");
                service.RecordCalibrationText();
            }

            Assert.IsTrue(File.Exists(path));
            var json = File.ReadAllText(path);
            Assert.DoesNotContain("пришлю вечером", json, StringComparison.Ordinal, "sentences are never stored, only counts");

            using var reloaded = new PersonalStyleService(path, KnownExcept("извените"));
            var snapshot = reloaded.Snapshot();
            Assert.AreEqual(1, snapshot.CalibrationTexts);
            Assert.AreEqual("подробный", reloaded.Predict("Очень подр")?.Word);
            Assert.AreEqual("сделать", reloaded.RankSuggestions("сдлать", ["слать", "сделать"]).Suggestions[0]);

            // A sentence learned before the restart is still recognised as learned.
            Assert.AreEqual(0, reloaded.LearnNow("Спасибо за подробный ответ."));
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { }
        }
    }

    [TestMethod]
    public void Reset_forgets_everything_on_disk_too()
    {
        var path = Path.Combine(Path.GetTempPath(), "wl-style-" + Guid.NewGuid().ToString("N"), "personal-style.json");
        try
        {
            using (var service = new PersonalStyleService(path, KnownExcept()))
            {
                service.LearnNow("Подробный ответ. Подробный план.");
                service.Flush();
                service.Reset();
            }

            using var reloaded = new PersonalStyleService(path, KnownExcept());
            Assert.AreEqual(0, reloaded.Snapshot().TotalWords);
        }
        finally
        {
            try { Directory.Delete(Path.GetDirectoryName(path)!, recursive: true); } catch (IOException) { }
        }
    }

    // ── Switches ──────────────────────────────────────────────────────────────

    [TestMethod]
    public void With_the_mode_off_nothing_changes_but_learning_continues()
    {
        using var service = PersonalStyleService.CreateInMemory();
        service.ApplySettings(new WriteLiteAppSettings { PersonalStyleEnabled = false });

        Assert.IsGreaterThan(0, service.LearnNow("Подробный ответ. Подробный план. Ещё раз. Ещё два. Ещё три."));
        service.RecordCorrection("сдлать", "сделать");

        Assert.IsNull(service.Predict("Очень подр"));
        Assert.AreEqual(PersonalRankReason.None, service.RankSuggestions("сдлать", ["слать", "сделать"]).Reason);
        Assert.IsEmpty(service.FindStyleIssues("Еще"));

        service.ApplySettings(new WriteLiteAppSettings());
        Assert.IsNotNull(service.Predict("Очень подр"));
        Assert.AreEqual("сделать", service.RankSuggestions("сдлать", ["слать", "сделать"]).Suggestions[0]);
        Assert.HasCount(1, service.FindStyleIssues("Еще"));
    }

    [TestMethod]
    public void With_learning_off_nothing_is_learned()
    {
        using var service = PersonalStyleService.CreateInMemory();
        service.ApplySettings(new WriteLiteAppSettings { PersonalStyleLearning = false });

        Assert.AreEqual(0, service.LearnNow("Подробный ответ. Подробный план."));
        service.RecordCorrection("сдлать", "сделать");
        Assert.AreEqual(0, service.Snapshot().TotalWords);
        Assert.IsEmpty(service.Snapshot().Corrections);
    }

    [TestMethod]
    public async Task Other_applications_are_learned_from_only_when_allowed()
    {
        using var service = PersonalStyleService.CreateInMemory();
        service.ApplySettings(new WriteLiteAppSettings { PersonalStyleLearnInOtherApps = false });

        Assert.AreEqual(0, await service.ObserveAsync("Подробный ответ.", StyleLearningSource.OtherApp));
        Assert.AreEqual(2, await service.ObserveAsync("Подробный ответ.", StyleLearningSource.Editor));
    }

    [TestMethod]
    public void A_find_issue_is_a_style_suggestion_never_an_error()
    {
        using var service = PersonalStyleService.CreateInMemory();
        service.LearnNow("Ещё раз. Ещё два. Ещё три.");

        var issue = service.FindStyleIssues("Напишу еще.").Single();

        Assert.AreEqual(IssueClass.Style, issue.Class);
        Assert.AreEqual("ещё", issue.Replacement);
        Assert.IsFalse(issue.CanApplyAutomatically);
        Assert.AreEqual(PersonalStyleRuleIds.Yo, issue.RuleId);
    }

    // ── Field learning ────────────────────────────────────────────────────────

    [TestMethod]
    public void A_sent_message_is_learned_whole_even_without_a_full_stop()
    {
        var observed = new List<(string Text, bool Final)>();
        var tracker = new FieldLearningTracker((text, final) => observed.Add((text, final)));

        tracker.Update("chat", "привет как дела", isUserEditing: true);
        tracker.Update("chat", string.Empty, isUserEditing: false);

        CollectionAssert.Contains(observed, ("привет как дела", true));
    }

    [TestMethod]
    public void A_field_that_was_only_focused_teaches_nothing()
    {
        var observed = new List<(string Text, bool Final)>();
        var tracker = new FieldLearningTracker((text, final) => observed.Add((text, final)));

        tracker.Update("mail", "Текст письма, которое написал кто-то другой.", isUserEditing: false);
        tracker.Update("other", "Другое поле", isUserEditing: false);
        tracker.FlushDraft();

        Assert.IsEmpty(observed);
    }

    [TestMethod]
    public void Moving_to_another_field_finalises_the_draft_in_the_first()
    {
        var observed = new List<(string Text, bool Final)>();
        var tracker = new FieldLearningTracker((text, final) => observed.Add((text, final)));

        tracker.Update("a", "Черновик в первом поле", isUserEditing: true);
        tracker.Update("b", "Второе", isUserEditing: true);

        CollectionAssert.Contains(observed, ("Черновик в первом поле", true));
        CollectionAssert.Contains(observed, ("Второе", false));
    }

    // ── Spelling integration ──────────────────────────────────────────────────

    private sealed class FakeChecker(Dictionary<string, string[]> typos) : ISpellChecker
    {
        public bool IsFullDictionaryLoaded => true;

        public SpellDictionaryStats Stats { get; } = new(0, 0, TimeSpan.Zero, "test", "test", true);

        public SpellCheckResult CheckWord(string word, SpellingLanguage language, CancellationToken cancellationToken = default)
            => typos.TryGetValue(word.ToLowerInvariant(), out var suggestions)
                ? new SpellCheckResult(word, language, false, suggestions)
                : new SpellCheckResult(word, language, true, []);
    }

    [TestMethod]
    public void The_spelling_card_offers_the_correction_this_user_made_before()
    {
        var checker = new FakeChecker(new() { ["сдлать"] = ["слать", "сделать", "сдать"] });
        using var style = PersonalStyleService.CreateInMemory(word => !word.Equals("сдлать", StringComparison.OrdinalIgnoreCase));

        var analyzer = new SpellTextAnalyzer(checker) { ContextualRefinementEnabled = false, PersonalStyle = style };
        var before = analyzer.Analyze("Надо сдлать отчёт.").Single(i => i.Original == "сдлать");

        style.RecordAcceptedCorrection(before with { Replacement = "сделать" });
        var after = analyzer.Analyze("Надо сдлать отчёт.").Single(i => i.Original == "сдлать");

        Assert.AreEqual("сделать", after.Replacement);
        StringAssert.Contains(after.Explanation, "Раньше вы исправляли");
    }

    [TestMethod]
    public async Task The_orchestrated_lane_carries_the_yo_finding_through_its_filters()
    {
        using var style = PersonalStyleService.CreateInMemory();
        style.LearnNow("Ещё раз спасибо. Напишу ещё вечером. Ещё не знаю.");

        var checker = new LocalSpellChecker();
        using var orchestrator = new WriteLite.Services.LanguageEngine.WriteLiteOrchestratingAnalyzer(
            new WriteLite.Services.RuleBasedAnalyzer(WriteLite.Services.Rules.RuleCatalog.LoadDefault(), checker.RussianFormIndex),
            new SpellTextAnalyzer(checker) { ContextualRefinementEnabled = false, PersonalStyle = style },
            new WriteLite.Services.LanguageEngine.WriteLiteLanguageEngine(new WriteLite.Services.LanguageEngine.WriteLiteLanguageOptions { EnableEngine = false }),
            UserDictionaryService.CreateEmpty(),
            new WriteLite.Services.LanguageEngine.WriteLiteIgnoreService());
        orchestrator.ApplySettings(new WriteLiteAppSettings { ExtendedChecking = false });
        orchestrator.PersonalStyle = style;

        var issues = await orchestrator.AnalyzeAsync("Напишу еще раз вечером.");
        var yo = issues.SingleOrDefault(i => i.RuleId == PersonalStyleRuleIds.Yo);

        Assert.IsNotNull(yo, $"got: {string.Join(", ", issues.Select(i => i.RuleId))}");
        Assert.AreEqual("ещё", yo.Replacement);

        orchestrator.ApplySettings(new WriteLiteAppSettings { ExtendedChecking = false, EnableStyle = false });
        Assert.IsFalse((await orchestrator.AnalyzeAsync("Напишу еще раз вечером.")).Any(i => i.RuleId == PersonalStyleRuleIds.Yo),
            "turning style suggestions off turns these off too");
    }

    [TestMethod]
    public void A_keyboard_layout_fix_is_not_learned_as_a_spelling_habit()
    {
        using var style = PersonalStyleService.CreateInMemory();
        style.RecordAcceptedCorrection(new TextIssue(0, 6, "ghbdtn", "привет", "t", "e",
            IssueCategory.Orthography, IssueSeverity.Warning, RuleId: "ru.spelling.keyboard-layout"));

        Assert.IsEmpty(style.Snapshot().Corrections);
    }
}
