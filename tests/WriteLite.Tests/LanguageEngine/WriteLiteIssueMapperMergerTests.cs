using WriteLite.Models;
using WriteLite.Services.LanguageEngine;

namespace WriteLite.Tests.LanguageEngine;

[TestClass]
public sealed class WriteLiteIssueMapperMergerTests
{
    private readonly WriteLiteIssueMapper _mapper = new();
    private readonly WriteLiteIssueMerger _merger = new();

    private static WriteLiteLanguageMatchDto Match(
        int offset,
        int length,
        string? message = "msg",
        string? shortMessage = "short",
        string? issueType = "misspelling",
        string? categoryId = "TYPOS",
        string? categoryName = "Typos",
        string? ruleId = "MORFOLOGIK_RULE_RU_RU",
        params string[] replacements)
    {
        return new WriteLiteLanguageMatchDto
        {
            Offset = offset,
            Length = length,
            Message = message,
            ShortMessage = shortMessage,
            Replacements = replacements.Select(r => new WriteLiteLanguageReplacementDto { Value = r }).ToList(),
            Rule = new WriteLiteLanguageRuleDto
            {
                Id = ruleId,
                Description = "desc",
                IssueType = issueType,
                Category = new WriteLiteLanguageCategoryDto { Id = categoryId, Name = categoryName }
            }
        };
    }

    // --- Mapper ---

    [TestMethod]
    public void Mapper_Orthography()
    {
        var text = "я хочю есть";
        var issue = _mapper.MapOne(text, Match(2, 4, issueType: "misspelling", categoryId: "TYPOS", replacements: "хочу"));
        Assert.IsNotNull(issue);
        Assert.AreEqual(IssueCategory.Orthography, issue!.Category);
        Assert.AreEqual("хочю", issue.Original);
        Assert.AreEqual("хочу", issue.Replacement);
        Assert.IsTrue(issue.CanApplyAutomatically);
        Assert.StartsWith("WL-SPELL-", issue.RuleId);
    }

    [TestMethod]
    public void Mapper_Punctuation()
    {
        var text = "Привет как дела";
        var issue = _mapper.MapOne(text, Match(0, 6, issueType: "punctuation", categoryId: "PUNCTUATION",
            ruleId: "COMMA_PARENTHESES", replacements: "Привет,"));
        Assert.IsNotNull(issue);
        Assert.AreEqual(IssueCategory.Punctuation, issue!.Category);
        Assert.StartsWith("WL-PUNCT-", issue.RuleId);
    }

    [TestMethod]
    public void Mapper_Grammar()
    {
        var text = "они идет домой";
        var issue = _mapper.MapOne(text, Match(4, 4, issueType: "grammar", categoryId: "GRAMMAR",
            ruleId: "AGREEMENT_RULE", replacements: "идут"));
        Assert.IsNotNull(issue);
        Assert.AreEqual(IssueCategory.Grammar, issue!.Category);
        Assert.StartsWith("WL-GRAMMAR-", issue.RuleId);
    }

    [TestMethod]
    public void Mapper_Style()
    {
        var text = "в связи с тем что";
        var issue = _mapper.MapOne(text, Match(0, text.Length, issueType: "style", categoryId: "STYLE",
            ruleId: "STYLE_FORMAL", replacements: "поскольку"));
        Assert.IsNotNull(issue);
        Assert.AreEqual(IssueCategory.Style, issue!.Category);
    }

    [TestMethod]
    public void Mapper_Typography()
    {
        var text = "слово  слово";
        var issue = _mapper.MapOne(text, Match(5, 2, issueType: "typography", categoryId: "TYPOGRAPHY",
            ruleId: "WHITESPACE_RULE", replacements: " "));
        Assert.IsNotNull(issue);
        Assert.AreEqual(IssueCategory.Readability, issue!.Category);
    }

    [TestMethod]
    public void Mapper_Repetition()
    {
        var text = "это это тест";
        var issue = _mapper.MapOne(text, Match(0, 7, issueType: "repetition", categoryId: "REDUNDANCY",
            ruleId: "WORD_REPEAT", replacements: "это"));
        Assert.IsNotNull(issue);
        Assert.AreEqual(LinguisticIssueCategory.RepeatedWord, issue!.LinguisticCategory);
    }

    [TestMethod]
    public void Mapper_UnknownCategory_Other()
    {
        var text = "abcdef";
        var issue = _mapper.MapOne(text, Match(0, 3, "msg", "short", "xyz", "QQQ", "Unknown", "UNKNOWN_RULE", "abc"));
        Assert.IsNotNull(issue);
        Assert.AreEqual(IssueCategory.Readability, issue!.Category);
        Assert.StartsWith("WL-OTHER-", issue.RuleId);
    }

    [TestMethod]
    public void Mapper_CyrillicOffset()
    {
        var text = "Привет мир";
        var issue = _mapper.MapOne(text, Match(7, 3, replacements: "мир!"));
        Assert.IsNotNull(issue);
        Assert.AreEqual("мир", issue!.Original);
        Assert.AreEqual(7, issue.Start);
    }

    [TestMethod]
    public void Mapper_EmojiBeforeError()
    {
        var text = "😀хочю";
        // 😀 is one char (or 2 UTF-16) — use actual indices
        var emojiLen = "😀".Length;
        var issue = _mapper.MapOne(text, Match(emojiLen, 4, replacements: "хочу"));
        Assert.IsNotNull(issue);
        Assert.AreEqual("хочю", issue!.Original);
    }

    [TestMethod]
    public void Mapper_EmojiInsideRange_SurrogateSafe()
    {
        var text = "ab😀cd";
        // If engine points into middle of surrogate, reject.
        var highIndex = text.IndexOf("😀", StringComparison.Ordinal);
        Assert.IsTrue(char.IsHighSurrogate(text[highIndex]));
        var bad = _mapper.MapOne(text, Match(highIndex + 1, 1, replacements: "x"));
        Assert.IsNull(bad);
    }

    [TestMethod]
    public void Mapper_Crlf_DoesNotSplit()
    {
        var text = "a\r\nb";
        // offset on \n after \r
        Assert.IsNull(_mapper.MapOne(text, Match(2, 1, replacements: "x")));
        // ending before \n while including \r only
        Assert.IsNull(_mapper.MapOne(text, Match(1, 1, replacements: "x")));
    }

    [TestMethod]
    public void Mapper_MultipleSameWords()
    {
        var text = "кот кот кот";
        var issue = _mapper.MapOne(text, Match(4, 3, replacements: "пёс"));
        Assert.IsNotNull(issue);
        Assert.AreEqual(4, issue!.Start);
        Assert.AreEqual("кот", issue.Original);
    }

    [TestMethod]
    public void Mapper_CombiningCharacter_RangeIntact()
    {
        // e + combining acute
        var text = "e\u0301rror";
        var issue = _mapper.MapOne(text, Match(0, 2, replacements: "é"));
        Assert.IsNotNull(issue);
        Assert.AreEqual(2, issue!.Length);
    }

    [TestMethod]
    public void Mapper_InvalidOffsetLength()
    {
        var text = "hello";
        Assert.IsNull(_mapper.MapOne(text, Match(-1, 1, replacements: "x")));
        Assert.IsNull(_mapper.MapOne(text, Match(0, 0, replacements: "x")));
        Assert.IsNull(_mapper.MapOne(text, Match(0, -1, replacements: "x")));
        Assert.IsNull(_mapper.MapOne(text, Match(4, 10, replacements: "x")));
    }

    [TestMethod]
    public void Mapper_EmptyReplacements_NoApply()
    {
        var text = "hello";
        var issue = _mapper.MapOne(text, Match(0, 5, replacements: Array.Empty<string>()));
        Assert.IsNotNull(issue);
        Assert.IsNull(issue!.Replacement);
        Assert.IsFalse(issue.CanApplyAutomatically);
    }

    [TestMethod]
    public void Mapper_SingleReplacement_Auto()
    {
        var text = "хочю";
        var issue = _mapper.MapOne(text, Match(0, 4, replacements: "хочу"));
        Assert.IsNotNull(issue);
        Assert.IsTrue(issue!.CanApplyAutomatically);
    }

    [TestMethod]
    public void Mapper_MultipleReplacements_NotAutoSafe()
    {
        var text = "хочю";
        var issue = _mapper.MapOne(text, Match(0, 4, "msg", "short", "misspelling", "TYPOS", "Typos", "MORFOLOGIK_RULE_RU_RU", "хочу", "хочешь"));
        Assert.IsNotNull(issue);
        Assert.AreEqual("хочу", issue!.Replacement);
        Assert.IsFalse(issue.CanApplyAutomatically);
    }

    [TestMethod]
    public void Mapper_ReplacementSameAsOriginal_RejectedAsApply()
    {
        var text = "hello";
        var issue = _mapper.MapOne(text, Match(0, 5, replacements: "hello"));
        Assert.IsNotNull(issue);
        Assert.IsNull(issue!.Replacement);
        Assert.IsFalse(issue.CanApplyAutomatically);
    }

    [TestMethod]
    public void Mapper_SafeWhitespaceDeletion()
    {
        var text = "a  b";
        var issue = _mapper.MapOne(text, Match(1, 2, issueType: "typography", categoryId: "TYPOGRAPHY",
            ruleId: "WHITESPACE", replacements: " "));
        Assert.IsNotNull(issue);
        Assert.IsTrue(issue!.CanApplyAutomatically);
    }

    [TestMethod]
    public void Mapper_SuspiciousLongDeletion_NotAuto()
    {
        var text = "длинныйфрагмент";
        var issue = _mapper.MapOne(text, Match(0, text.Length, "msg", "short", "grammar", "GRAMMAR", "Grammar", "X", ""));
        Assert.IsNotNull(issue);
        // long non-whitespace deletion should not be auto
        Assert.IsFalse(issue!.CanApplyAutomatically);
    }

    [TestMethod]
    public void Mapper_MissingOptionalFields()
    {
        var text = "тест";
        var match = new WriteLiteLanguageMatchDto
        {
            Offset = 0,
            Length = 4,
            Replacements = [new WriteLiteLanguageReplacementDto { Value = "Тест" }]
        };
        var issue = _mapper.MapOne(text, match);
        Assert.IsNotNull(issue);
        Assert.IsFalse(string.IsNullOrWhiteSpace(issue!.Title));
    }

    [TestMethod]
    public void Mapper_StripsThirdPartyBrand()
    {
        var text = "хочю";
        var issue = _mapper.MapOne(text, Match(0, 4,
            message: "LanguageTool suggests a fix",
            shortMessage: "languagetool typo",
            replacements: "хочу"));
        Assert.IsNotNull(issue);
        Assert.IsFalse(WriteLiteIssueNormalization.ContainsThirdPartyBrand(issue!.Title));
        Assert.IsFalse(WriteLiteIssueNormalization.ContainsThirdPartyBrand(issue.Explanation));
    }

    [TestMethod]
    public void Mapper_StripsHtmlAndLimitsLength()
    {
        var text = "abcd";
        var longMsg = "<b>error</b> " + new string('я', 400) + " http://example.com/x";
        var issue = _mapper.MapOne(text, Match(0, 4, message: longMsg, shortMessage: longMsg, replacements: "ab"));
        Assert.IsNotNull(issue);
        Assert.DoesNotContain("<", issue!.Explanation);
        Assert.DoesNotContain("http", issue.Explanation);
        Assert.IsLessThanOrEqualTo(280, issue.Explanation.Length);
    }

    [TestMethod]
    public void Mapper_RuleId_NoUserText()
    {
        var text = "секретноеслово";
        var issue = _mapper.MapOne(text, Match(0, text.Length, replacements: "x"));
        Assert.IsNotNull(issue);
        Assert.DoesNotContain("секрет", issue!.RuleId);
        Assert.StartsWith("WL-", issue.RuleId);
    }

    [TestMethod]
    public void Mapper_MapAll_Aggregates()
    {
        var text = "я хочю";
        var dto = new WriteLiteLanguageResponseDto
        {
            Matches =
            [
                Match(2, 4, replacements: "хочу"),
                Match(-1, 1, replacements: "x")
            ]
        };
        var result = _mapper.MapAll(text, dto);
        Assert.AreEqual(2, result.InputMatchCount);
        Assert.AreEqual(1, result.MappedCountEquivalent());
        Assert.AreEqual(1, result.RejectedCount);
        Assert.HasCount(1, result.Issues);
    }

    // --- Merger ---

    [TestMethod]
    public void Merger_ExactDuplicates()
    {
        var a = Issue(0, 3, "abc", "abd", IssueCategory.Orthography, "r1");
        var b = Issue(0, 3, "abc", "abd", IssueCategory.Orthography, "r1");
        var merged = _merger.Merge([a, b]);
        Assert.HasCount(1, merged.Issues);
        Assert.IsGreaterThanOrEqualTo(1, merged.DuplicateCount);
    }

    [TestMethod]
    public void Merger_SameRangeSameReplacement()
    {
        var a = Issue(0, 3, "abc", "x", IssueCategory.Orthography, "built-in");
        var b = Issue(0, 3, "abc", "x", IssueCategory.Orthography, "WL-SPELL-AAA");
        var merged = _merger.Merge([a], engineMappedIssues: [b]);
        Assert.HasCount(1, merged.Issues);
    }

    [TestMethod]
    public void Merger_SameRangeDifferentCategories_KeepsUseful()
    {
        var a = Issue(0, 3, "abc", "x", IssueCategory.Orthography, "r1");
        var b = Issue(0, 3, "abc", "y", IssueCategory.Grammar, "r2");
        var merged = _merger.Merge([a, b]);
        Assert.HasCount(2, merged.Issues);
    }

    [TestMethod]
    public void Merger_SameRangeSameCategoryDifferentReplacement_KeepsBoth()
    {
        var a = Issue(0, 3, "abc", "x", IssueCategory.Orthography, "r1");
        var b = Issue(0, 3, "abc", "y", IssueCategory.Orthography, "r2");

        var merged = _merger.Merge([a, b]);

        Assert.HasCount(2, merged.Issues);
        CollectionAssert.AreEquivalent(new[] { "x", "y" }, merged.Issues.Select(i => i.Replacement).ToArray());
    }

    [TestMethod]
    public void Merger_PrefersBuiltInWriteLiteRule()
    {
        var builtIn = Issue(0, 4, "хочю", "хочу", IssueCategory.Orthography, "local-spell");
        var engine = Issue(0, 4, "хочю", "хочу", IssueCategory.Orthography, "WL-SPELL-DEADBEEF");
        var merged = _merger.Merge([builtIn], engineMappedIssues: [engine]);
        Assert.HasCount(1, merged.Issues);
        Assert.AreEqual("local-spell", merged.Issues[0].RuleId);
    }

    [TestMethod]
    public void Merger_DuplicatePreservesStrongestMetadata()
    {
        var builtIn = Issue(0, 3, "abc", "abd", IssueCategory.Orthography, "local")
            with { Explanation = "Кратко.", Confidence = .8 };
        var supplementary = Issue(0, 3, "abc", "abd", IssueCategory.Orthography, "WL-SPELL")
            with { Explanation = "Более точное и полезное объяснение правила.", Confidence = .96 };

        var merged = _merger.Merge([builtIn], engineMappedIssues: [supplementary]);

        Assert.HasCount(1, merged.Issues);
        Assert.AreEqual("local", merged.Issues[0].RuleId);
        Assert.AreEqual(supplementary.Explanation, merged.Issues[0].Explanation);
        Assert.AreEqual(.96, merged.Issues[0].Confidence);
    }

    [TestMethod]
    public void Merger_PrefersSafeIssue()
    {
        var safe = Issue(0, 3, "abc", "abd", IssueCategory.Orthography, "a", auto: true);
        var unsafeIssue = Issue(0, 3, "abc", "abe", IssueCategory.Orthography, "b", auto: false);
        var merged = _merger.Merge([unsafeIssue, safe]);
        Assert.IsTrue(merged.Issues.Any(i => i.CanApplyAutomatically));
    }

    [TestMethod]
    public void Merger_OverlappingRanges_DemotesConflictForApplyAll()
    {
        var a = Issue(0, 5, "hello", "hallo", IssueCategory.Orthography, "a", auto: true);
        var b = Issue(3, 4, "lo!!", "lo", IssueCategory.Orthography, "b", auto: true);
        var merged = _merger.Merge([a, b]);
        var safe = WriteLiteIssueMerger.SelectSafeForApplyAll(merged.Issues);
        // No overlapping autos in safe set.
        for (var i = 0; i < safe.Count; i++)
        {
            for (var j = i + 1; j < safe.Count; j++)
            {
                var x = safe[i];
                var y = safe[j];
                Assert.IsFalse(x.Start < y.Start + y.Length && y.Start < x.Start + x.Length);
            }
        }
    }

    [TestMethod]
    public void Merger_NestedRanges()
    {
        var outer = Issue(0, 10, "0123456789", "x", IssueCategory.Style, "outer", auto: true);
        var inner = Issue(2, 3, "234", "y", IssueCategory.Orthography, "inner", auto: true);
        var merged = _merger.Merge([outer, inner]);
        Assert.IsGreaterThanOrEqualTo(1, merged.Issues.Count);
        var safe = WriteLiteIssueMerger.SelectSafeForApplyAll(merged.Issues);
        Assert.IsLessThanOrEqualTo(1, safe.Count); // nested conflict
    }

    [TestMethod]
    public void Merger_AdjacentNonOverlapping()
    {
        var a = Issue(0, 2, "ab", "AB", IssueCategory.Orthography, "a", auto: true);
        var b = Issue(2, 2, "cd", "CD", IssueCategory.Orthography, "b", auto: true);
        var merged = _merger.Merge([a, b]);
        Assert.HasCount(2, merged.Issues);
        Assert.HasCount(2, WriteLiteIssueMerger.SelectSafeForApplyAll(merged.Issues));
    }

    [TestMethod]
    public void Merger_DeterministicSort()
    {
        var a = Issue(5, 1, "b", "B", IssueCategory.Orthography, "2");
        var b = Issue(0, 1, "a", "A", IssueCategory.Orthography, "1");
        var r1 = _merger.Merge([a, b]).Issues.Select(i => i.Start).ToArray();
        var r2 = _merger.Merge([a, b]).Issues.Select(i => i.Start).ToArray();
        CollectionAssert.AreEqual(new[] { 0, 5 }, r1);
        CollectionAssert.AreEqual(r1, r2);
    }

    [TestMethod]
    public void Merger_EmptyCollections()
    {
        var merged = _merger.Merge(null, null, null);
        Assert.IsEmpty(merged.Issues);
    }

    [TestMethod]
    public void Merger_MaxIssues()
    {
        var many = Enumerable.Range(0, 50)
            .Select(i => Issue(i * 2, 1, "a", "b", IssueCategory.Orthography, "r" + i))
            .ToList();
        var merged = _merger.Merge(many, maxIssues: 10);
        Assert.HasCount(10, merged.Issues);
    }

    [TestMethod]
    public void Merger_DoesNotMutateInputs()
    {
        var list = new List<TextIssue>
        {
            Issue(0, 1, "a", "b", IssueCategory.Orthography, "r")
        };
        var copy = list.ToList();
        _ = _merger.Merge(list, engineMappedIssues: list);
        Assert.HasCount(1, list);
        Assert.AreEqual(copy[0], list[0]);
    }

    [TestMethod]
    public void Merger_RepeatedCall_Identical()
    {
        var a = Issue(0, 2, "ab", "AB", IssueCategory.Orthography, "a");
        var b = Issue(3, 2, "cd", "CD", IssueCategory.Punctuation, "b");
        var r1 = _merger.Merge([a], engineMappedIssues: [b]).Issues;
        var r2 = _merger.Merge([a], engineMappedIssues: [b]).Issues;
        CollectionAssert.AreEqual(r1.ToList(), r2.ToList());
    }

    [TestMethod]
    public void Merger_ApplyAllSafeSet_NonOverlapping()
    {
        var issues = new[]
        {
            Issue(0, 4, "хочю", "хочу", IssueCategory.Orthography, "1", auto: true),
            Issue(2, 4, "чюе", "чее", IssueCategory.Orthography, "2", auto: true),
            Issue(10, 1, "x", "y", IssueCategory.Orthography, "3", auto: true)
        };
        var merged = _merger.Merge(issues);
        var safe = WriteLiteIssueMerger.SelectSafeForApplyAll(merged.Issues);
        Assert.IsTrue(safe.All(s => s.CanApplyAutomatically));
    }

    private static TextIssue Issue(
        int start,
        int length,
        string original,
        string? replacement,
        IssueCategory category,
        string ruleId,
        bool auto = true)
        => new(
            start,
            length,
            original,
            replacement,
            "t",
            "e",
            category,
            IssueSeverity.Error,
            auto,
            ruleId);
}

internal static class MapResultExtensions
{
    public static int MappedCountEquivalent(this WriteLiteIssueMapResult r) => r.Issues.Count;
}
