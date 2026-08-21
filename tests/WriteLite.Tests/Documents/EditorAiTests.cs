using System.Text.RegularExpressions;
using WriteLite.AI.Local;
using WriteLite.Services.Ai;

namespace WriteLite.Tests.Documents;

/// <summary>
/// The editor's AI layer: rewriting, the preview diff, and the privacy guarantees.
/// </summary>
/// <remarks>
/// These run without a model server. That is deliberate — the behaviour that most
/// needs pinning down is what happens when the neural path is unavailable, because
/// that is when a lesser implementation starts inventing text.
/// </remarks>
[TestClass]
public sealed class EditorAiTests
{
    private static TextRewriteService BuildService() =>
        // An empty model directory and a loopback endpoint nothing is listening on:
        // the service must degrade rather than fail.
        new(new QwenModelBackend(
            Path.Combine(Path.GetTempPath(), "wl-no-model-" + Guid.NewGuid().ToString("N")[..8]),
            "http://127.0.0.1:9"));

    // ── Scope ────────────────────────────────────────────────────────────────

    [TestMethod]
    public async Task Rewrite_returns_the_selection_untouched_when_there_is_nothing_to_do()
    {
        var result = await BuildService().RewriteAsync(
            new RewriteRequest(RewriteOperation.Rewrite, "   "));

        Assert.AreEqual("   ", result.Suggestion);
    }

    [TestMethod]
    public async Task Rewrite_refuses_a_selection_beyond_the_documented_limit()
    {
        var oversized = new string('а', TextRewriteService.MaxSelectionChars + 1);

        var exception = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => BuildService().RewriteAsync(new RewriteRequest(RewriteOperation.Rewrite, oversized)));

        Assert.Contains("слишком много", exception.Message);
    }

    /// <summary>
    /// Without the model, offline rules make only checkable edits and never invent prose.
    /// </summary>
    [TestMethod]
    public async Task Offline_fallback_fixes_punctuation_spacing_without_inventing_text()
    {
        var result = await BuildService().RewriteAsync(
            new RewriteRequest(RewriteOperation.Grammar, "Привет ,мир .Как дела ?"));

        Assert.AreEqual("offline-rules", result.Backend);
        Assert.AreEqual("Привет, мир. Как дела?", result.Suggestion);
    }

    [TestMethod]
    public async Task Offline_fallback_removes_a_duplicated_word()
    {
        var result = await BuildService().RewriteAsync(
            new RewriteRequest(RewriteOperation.Shorten, "это это очень важный текст"));

        Assert.AreEqual("это очень важный текст", result.Suggestion);
    }

    /// <summary>
    /// The operations rules genuinely cannot perform must come back unchanged.
    /// </summary>
    /// <remarks>
    /// This is the most important test in the file. Returning fabricated text under
    /// the label "AI" would be the worst failure mode for a writing tool, so the
    /// contract is that an unavailable model yields the original text and an honest
    /// backend label — which the preview then explains to the user.
    /// </remarks>
    [TestMethod]
    public async Task Offline_fallback_does_not_fabricate_for_operations_it_cannot_perform()
    {
        var service = BuildService();
        const string original = "Исходный текст, который нельзя переписать без модели.";

        foreach (var operation in new[]
                 {
                     RewriteOperation.Rewrite,
                     RewriteOperation.Formal,
                     RewriteOperation.Casual,
                     RewriteOperation.Expand,
                     RewriteOperation.Explain,
                     RewriteOperation.Translate
                 })
        {
            var result = await service.RewriteAsync(new RewriteRequest(operation, original));

            Assert.AreEqual(original, result.Suggestion, $"{operation} fabricated a rewrite without a model.");
            Assert.IsTrue(result.IsNoOp);
            Assert.AreEqual("offline-rules", result.Backend);
        }
    }

    // ── Privacy ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Everything the editor's AI touches must be loopback-only.
    /// </summary>
    [TestMethod]
    public void Editor_ai_sources_contain_no_non_loopback_urls()
    {
        var root = FindRepositoryRoot();

        var files = new[]
        {
            "IEditorAiService.cs",
            "TextRewriteService.cs",
            "RewriteDiff.cs"
        }.Select(name => Path.Combine(root, "src", "WriteLite.App", "Services", "Ai", name));

        foreach (var file in files)
        {
            Assert.IsTrue(File.Exists(file), $"Expected {file} to exist.");

            foreach (Match match in Regex.Matches(File.ReadAllText(file), @"https?://[^\s""'<>]+"))
            {
                var value = match.Value.TrimEnd('.', ',', ';', ')');
                Assert.IsTrue(Uri.TryCreate(value, UriKind.Absolute, out var uri), $"Invalid URL: {value}");
                Assert.IsTrue(
                    uri!.Host is "127.0.0.1" or "localhost" or "::1",
                    $"Non-loopback URL in the editor AI layer: {value}");
            }
        }
    }

    [TestMethod]
    public void Rewrite_prompts_instruct_the_model_to_return_only_the_text()
    {
        Assert.Contains("ТОЛЬКО итоговым текстом", RewritePrompts.System);
        Assert.Contains("Сохраняй язык оригинала", RewritePrompts.System);
    }

    // ── Response cleaning ────────────────────────────────────────────────────

    [TestMethod]
    public void Cleaner_strips_a_code_fence()
    {
        Assert.AreEqual(
            "Готовый текст.",
            ResponseCleaner.Clean("```\nГотовый текст.\n```", "исходный"));
    }

    [TestMethod]
    public void Cleaner_strips_a_preamble()
    {
        Assert.AreEqual(
            "Готовый текст.",
            ResponseCleaner.Clean("Вот исправленный вариант: Готовый текст.", "исходный"));
    }

    [TestMethod]
    public void Cleaner_unwraps_quotes_the_model_added()
    {
        Assert.AreEqual("Готовый текст.", ResponseCleaner.Clean("«Готовый текст.»", "исходный"));
    }

    /// <summary>Quotation the author wrote is theirs and must survive.</summary>
    [TestMethod]
    public void Cleaner_keeps_quotes_the_author_already_had()
    {
        Assert.AreEqual(
            "«Готовый текст.»",
            ResponseCleaner.Clean("«Готовый текст.»", "«исходный текст»"));
    }

    [TestMethod]
    public void Cleaner_rejects_an_empty_answer()
    {
        Assert.IsNull(ResponseCleaner.Clean("   ", "исходный"));
        Assert.IsNull(ResponseCleaner.Clean(null, "исходный"));
    }

    // ── Diff ─────────────────────────────────────────────────────────────────

    [TestMethod]
    public void Diff_marks_only_what_changed()
    {
        var segments = RewriteDiff.Compute(
            "быстрая коричневая лиса прыгает",
            "быстрая рыжая лиса прыгает");

        Assert.IsTrue(segments.Any(segment => segment.Kind == DiffKind.Removed && segment.Text.Contains("коричневая")));
        Assert.IsTrue(segments.Any(segment => segment.Kind == DiffKind.Added && segment.Text.Contains("рыжая")));
        Assert.IsTrue(segments.Any(segment => segment.Kind == DiffKind.Unchanged && segment.Text.Contains("быстрая")));
    }

    [TestMethod]
    public void Diff_of_identical_text_is_entirely_unchanged()
    {
        var segments = RewriteDiff.Compute("одинаковый текст", "одинаковый текст");

        Assert.IsTrue(segments.All(segment => segment.Kind == DiffKind.Unchanged));
    }

    /// <summary>Reassembling the kept and added segments must reproduce the suggestion exactly.</summary>
    [TestMethod]
    public void Diff_segments_reassemble_into_the_original_and_the_suggestion()
    {
        const string original = "Первое предложение. Второе предложение здесь.";
        const string suggestion = "Первое предложение. Второе предложение изменилось.";

        var segments = RewriteDiff.Compute(original, suggestion);

        var rebuiltOriginal = string.Concat(segments
            .Where(segment => segment.Kind is DiffKind.Unchanged or DiffKind.Removed)
            .Select(segment => segment.Text));

        var rebuiltSuggestion = string.Concat(segments
            .Where(segment => segment.Kind is DiffKind.Unchanged or DiffKind.Added)
            .Select(segment => segment.Text));

        Assert.AreEqual(original, rebuiltOriginal);
        Assert.AreEqual(suggestion, rebuiltSuggestion);
    }

    private static string FindRepositoryRoot()
    {
        for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
        {
            if (File.Exists(Path.Combine(current.FullName, "WriteLite.sln")))
            {
                return current.FullName;
            }
        }

        throw new DirectoryNotFoundException("WriteLite repository root was not found.");
    }
}
