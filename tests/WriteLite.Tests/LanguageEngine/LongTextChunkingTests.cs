using WriteLite.Services.LanguageEngine;

namespace WriteLite.Tests.LanguageEngine;

[TestClass]
public sealed class LongTextChunkingTests
{
    [DataTestMethod]
    [DataRow(20_000)]
    [DataRow(50_000)]
    [DataRow(100_000)]
    public void Plan_CoversLargeDocumentExactly_WithOverlap(int length)
    {
        var text = BuildText(length);
        var chunks = SemanticTextChunker.CreatePlan(text, maxChunkLength: 4_096, overlapLength: 256);

        Assert.IsGreaterThan(1, chunks.Count);
        Assert.AreEqual(0, chunks[0].OwnedStart);
        Assert.AreEqual(length, chunks.Sum(chunk => chunk.OwnedLength));
        Assert.AreEqual(length, chunks[^1].OwnedEnd);
        Assert.IsTrue(chunks.All(chunk => chunk.Text.Length <= 4_096));

        for (var index = 1; index < chunks.Count; index++)
        {
            Assert.AreEqual(chunks[index - 1].OwnedEnd, chunks[index].OwnedStart);
            Assert.IsLessThan(chunks[index - 1].End, chunks[index].Start);
        }
    }

    [TestMethod]
    public async Task RunAndMerge_FindsTailIssue_WithGlobalUtf16Offset()
    {
        const string marker = "TAIL_ERROR";
        var text = BuildText(100_000 - marker.Length) + marker;
        var plan = SemanticTextChunker.CreatePlan(text, 4_096, 256);
        var results = await RunMarkerAnalyzerAsync(plan, marker);

        var merged = WriteLiteLanguageEngine.MergeChunkMatches(text, results);

        Assert.HasCount(1, merged);
        Assert.AreEqual(text.Length - marker.Length, merged[0].Offset);
        Assert.AreEqual(marker.Length, merged[0].Length);
    }

    [TestMethod]
    public async Task RunAndMerge_BoundaryEmojiIssue_IsNotSplitOrDuplicated()
    {
        const string marker = "ОШ😀ИБКА";
        var seed = new string('а', 24_000);
        var firstPlan = SemanticTextChunker.CreatePlan(seed, 2_048, 256);
        var expectedOffset = firstPlan[3].OwnedEnd - (marker.Length / 2);
        var text = seed.Remove(expectedOffset, marker.Length).Insert(expectedOffset, marker);
        var plan = SemanticTextChunker.CreatePlan(text, 2_048, 256);

        Assert.IsTrue(plan.Count(chunk => chunk.Text.Contains(marker, StringComparison.Ordinal)) >= 2,
            "The marker must be visible in both sides of the overlap for this regression test.");
        foreach (var chunk in plan)
        {
            Assert.IsFalse(chunk.Text.Length > 0 && char.IsLowSurrogate(chunk.Text[0]));
            Assert.IsFalse(chunk.Text.Length > 0 && char.IsHighSurrogate(chunk.Text[^1]));
        }

        var results = await RunMarkerAnalyzerAsync(plan, marker);
        var merged = WriteLiteLanguageEngine.MergeChunkMatches(text, results);

        Assert.HasCount(1, merged);
        Assert.AreEqual(expectedOffset, merged[0].Offset);
        Assert.AreEqual(marker.Length, merged[0].Length);
        Assert.AreEqual(marker, text.Substring(merged[0].Offset, merged[0].Length));
    }

    [TestMethod]
    public async Task RunAsync_BoundsConcurrency_AndReportsFullCoverage()
    {
        var text = BuildText(50_000);
        var plan = SemanticTextChunker.CreatePlan(text, 2_048, 128);
        var active = 0;
        var maximumActive = 0;
        var checkedCharacters = 0;

        _ = await SemanticTextChunker.RunAsync(
            plan,
            async (_, token) =>
            {
                var now = Interlocked.Increment(ref active);
                UpdateMaximum(ref maximumActive, now);
                try
                {
                    await Task.Delay(5, token);
                    return true;
                }
                finally
                {
                    Interlocked.Decrement(ref active);
                }
            },
            maxConcurrency: 2,
            onOwnedCharactersChecked: count => Interlocked.Add(ref checkedCharacters, count));

        Assert.IsGreaterThan(1, maximumActive);
        Assert.IsLessThanOrEqualTo(2, maximumActive);
        Assert.AreEqual(text.Length, checkedCharacters);
    }

    [TestMethod]
    public async Task RunAsync_Cancellation_StopsRemainingChunks()
    {
        var text = BuildText(100_000);
        var plan = SemanticTextChunker.CreatePlan(text, 1_024, 64);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromMilliseconds(30));
        var completed = 0;

        await Assert.ThrowsAsync<OperationCanceledException>(async () =>
            await SemanticTextChunker.RunAsync(
                plan,
                async (_, token) =>
                {
                    await Task.Delay(20, token);
                    Interlocked.Increment(ref completed);
                    return true;
                },
                maxConcurrency: 2,
                cancellationToken: cancellation.Token));

        Assert.IsLessThan(plan.Count, completed);
    }

    private static async Task<IReadOnlyList<SemanticChunkResult<List<WriteLiteLanguageMatchDto>>>> RunMarkerAnalyzerAsync(
        IReadOnlyList<SemanticTextChunk> plan,
        string marker)
        => await SemanticTextChunker.RunAsync(
            plan,
            (chunk, _) =>
            {
                var matches = new List<WriteLiteLanguageMatchDto>();
                var searchFrom = 0;
                while (searchFrom <= chunk.Text.Length - marker.Length)
                {
                    var offset = chunk.Text.IndexOf(marker, searchFrom, StringComparison.Ordinal);
                    if (offset < 0)
                    {
                        break;
                    }

                    matches.Add(new WriteLiteLanguageMatchDto
                    {
                        Offset = offset,
                        Length = marker.Length,
                        Message = "test",
                        Rule = new WriteLiteLanguageRuleDto { Id = "TEST_BOUNDARY" },
                        Replacements = [new WriteLiteLanguageReplacementDto { Value = "fixed" }]
                    });
                    searchFrom = offset + 1;
                }

                return Task.FromResult(matches);
            });

    private static string BuildText(int length)
    {
        const string paragraph = "Абзац с обычными словами и предложением. Вторая фраза!\r\n";
        var repeats = (length / paragraph.Length) + 1;
        return string.Concat(Enumerable.Repeat(paragraph, repeats))[..length];
    }

    private static void UpdateMaximum(ref int target, int candidate)
    {
        int current;
        do
        {
            current = Volatile.Read(ref target);
            if (candidate <= current)
            {
                return;
            }
        }
        while (Interlocked.CompareExchange(ref target, candidate, current) != current);
    }
}
