using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using WriteLite.AI.Local;
using WriteLite.Services.Spelling;

namespace TrainExport;

/// <summary>One corrected/broken sentence pair from the error corpus.</summary>
internal sealed record ErrorPair(
    string PairId,
    string Correct,
    string Broken,
    string ErrorType,
    string Category);

internal static class ErrorCorpus
{
    /// <summary>
    /// Reassembles the corpus's <c>-pos</c>/<c>-neg</c> rows into pairs.
    /// </summary>
    /// <remarks>
    /// The corpus stores each example twice — once correct with <c>label: 1</c> and once
    /// broken with <c>label: 0</c> — sharing a <c>pair_id</c>. Both halves are needed here:
    /// the broken sentence supplies the target token and the correct one supplies the label.
    /// </remarks>
    public static IReadOnlyList<ErrorPair> LoadPairs(string path)
    {
        var positives = new Dictionary<string, Row>(StringComparer.Ordinal);
        var negatives = new Dictionary<string, Row>(StringComparer.Ordinal);

        foreach (var line in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            var row = JsonSerializer.Deserialize<Row>(line);
            if (row?.PairId is null || row.Sentence is null) continue;
            (row.Label == 1 ? positives : negatives)[row.PairId] = row;
        }

        var pairs = new List<ErrorPair>();
        foreach (var (pairId, positive) in positives)
        {
            if (!negatives.TryGetValue(pairId, out var negative)) continue;
            if (string.Equals(positive.Sentence, negative.Sentence, StringComparison.Ordinal)) continue;

            pairs.Add(new ErrorPair(
                pairId,
                positive.Sentence!,
                negative.Sentence!,
                negative.ErrorType ?? "",
                negative.Category ?? ""));
        }

        return pairs;
    }

    private sealed class Row
    {
        [JsonPropertyName("sentence")] public string? Sentence { get; set; }
        [JsonPropertyName("label")] public int Label { get; set; }
        [JsonPropertyName("pair_id")] public string? PairId { get; set; }
        [JsonPropertyName("error_type")] public string? ErrorType { get; set; }
        [JsonPropertyName("category")] public string? Category { get; set; }
    }
}

/// <summary>A candidate_ranking training example, in the §25 shape.</summary>
internal sealed class CandidateRankingExample
{
    [JsonPropertyName("task")] public string Task => "candidate_ranking";
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("split")] public required string Split { get; init; }
    [JsonPropertyName("sentence")] public required string Sentence { get; init; }
    [JsonPropertyName("target")] public required string Target { get; init; }
    [JsonPropertyName("target_start")] public required int TargetStart { get; init; }
    [JsonPropertyName("candidates")] public required IReadOnlyList<string> Candidates { get; init; }
    [JsonPropertyName("correct")] public required string Correct { get; init; }
    [JsonPropertyName("category")] public required string Category { get; init; }
    [JsonPropertyName("error_type")] public required string ErrorType { get; init; }

    /// <summary>
    /// True when the deterministic generator never offered the right answer.
    /// </summary>
    /// <remarks>
    /// Kept rather than dropped, and flagged rather than silently mixed in. A ranker trained
    /// only on solvable examples learns that the answer is always present, which is exactly
    /// the assumption that breaks in production — measured at 35 % of single-token targets on
    /// the frozen corpus. These examples are what teach a model to prefer NO_CHANGE over the
    /// best of a bad list.
    /// </remarks>
    [JsonPropertyName("candidate_generation_failure")] public required bool CandidateGenerationFailure { get; init; }
}

/// <summary>A punctuation_decision training example, in the §25 shape.</summary>
internal sealed class PunctuationDecisionExample
{
    [JsonPropertyName("task")] public string Task => "punctuation_decision";
    [JsonPropertyName("id")] public required string Id { get; init; }
    [JsonPropertyName("split")] public required string Split { get; init; }
    [JsonPropertyName("sentence")] public required string Sentence { get; init; }
    [JsonPropertyName("position")] public required int Position { get; init; }
    [JsonPropertyName("choices")] public IReadOnlyList<string> Choices => ["COMMA", "NO_CHANGE"];
    [JsonPropertyName("correct")] public required string Correct { get; init; }
}

internal static class CandidateRankingBuilder
{
    /// <summary>
    /// Turns one error pair into a ranking example, using the product's real candidate list.
    /// </summary>
    /// <remarks>
    /// Only single-token differences qualify. A pair differing in two places is not a
    /// candidate-ranking question — it is two of them, or a rewrite — and exporting it as one
    /// would teach a ranker to expect a menu that covers a whole phrase.
    /// </remarks>
    public static CandidateRankingExample? TryBuild(
        ErrorPair pair,
        string split,
        RussianFormIndexLexicon lexicon)
    {
        var difference = SingleTokenDifference(pair.Broken, pair.Correct);
        if (difference is null)
        {
            return null;
        }

        var (start, broken, correct) = difference.Value;
        var suggestions = lexicon.ContainsExact(broken) ? [] : lexicon.Suggest(broken, 6);
        var candidates = CandidateJudge.BuildCandidates(broken, suggestions)
            .Where(c => c.Text.Length > 0)
            .Select(c => c.Text)
            .ToList();

        var offered = candidates.Any(c => string.Equals(c, correct, StringComparison.OrdinalIgnoreCase));

        // NO_CHANGE is part of the label space, not an afterthought: a ranker has to be able
        // to say "none of these" and that has to be a trainable answer.
        candidates.Add(CandidateJudge.NoChangeId);

        return new CandidateRankingExample
        {
            Id = pair.PairId,
            Split = split,
            Sentence = pair.Broken,
            Target = broken,
            TargetStart = start,
            Candidates = candidates,
            Correct = correct,
            Category = pair.Category,
            ErrorType = pair.ErrorType,
            CandidateGenerationFailure = !offered,
        };
    }

    /// <summary>
    /// The one token that differs, or null when the sentences differ in more than one place.
    /// </summary>
    private static (int Start, string Broken, string Correct)? SingleTokenDifference(string broken, string correct)
    {
        var brokenTokens = Tokenize(broken);
        var correctTokens = Tokenize(correct);
        if (brokenTokens.Count != correctTokens.Count)
        {
            return null;
        }

        (int Start, string Broken, string Correct)? found = null;
        for (var i = 0; i < brokenTokens.Count; i++)
        {
            if (string.Equals(brokenTokens[i].Value, correctTokens[i].Value, StringComparison.Ordinal))
            {
                continue;
            }

            if (found is not null)
            {
                return null;
            }

            found = (brokenTokens[i].Start, brokenTokens[i].Value, correctTokens[i].Value);
        }

        return found;
    }

    private static List<(int Start, string Value)> Tokenize(string text)
    {
        var tokens = new List<(int, string)>();
        var i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetterOrDigit(text[i]))
            {
                i++;
                continue;
            }

            var start = i;
            while (i < text.Length && (char.IsLetterOrDigit(text[i]) || text[i] == '-')) i++;
            tokens.Add((start, text[start..i]));
        }

        return tokens;
    }
}

internal static class PunctuationDecisionBuilder
{
    /// <summary>
    /// Derives comma decisions from a correctly punctuated sentence.
    /// </summary>
    /// <remarks>
    /// <para>Supervision is free here and that is the point: any well-punctuated Russian text
    /// labels itself. A position where the author wrote a comma is a COMMA example; a word
    /// boundary where they did not is a NO_CHANGE example.</para>
    ///
    /// <para>Negatives are capped at the number of positives per sentence. Left uncapped the
    /// task is 95 % NO_CHANGE and a classifier learns to answer NO_CHANGE unconditionally,
    /// which is precisely the failure the deterministic layer already has.</para>
    ///
    /// <para>Only the corrected half of a pair is used. The broken half has an injected
    /// spelling error, and a punctuation classifier trained on misspelt input learns to
    /// answer a different question.</para>
    ///
    /// <para><b>Two defects in the first version of this builder, both found by auditing the
    /// export rather than by reading the code, and both fixed here.</b></para>
    ///
    /// <para><b>Negatives were taken with <c>Take(n)</c>.</b> That is the first n non-comma
    /// boundaries, which in Russian means the first few words of the sentence. The exported
    /// negatives had a mean relative position of 0.131 against 0.486 for the positives, and a
    /// classifier reading nothing but that one number scored <b>0.837 on validation</b>. Any
    /// model trained on it would have learned "commas come later in sentences" and reported a
    /// respectable accuracy for it. Negatives are now sampled uniformly from all eligible
    /// boundaries with a seeded shuffle, so position carries no signal and the model has to
    /// read the words.</para>
    ///
    /// <para><b>Comma-free sentences were dropped entirely.</b> <c>positives.Count == 0</c>
    /// ended the method, so every training sentence contained at least one comma. Most
    /// sentences a user writes contain none, and a model that has never seen one has never
    /// been asked the question it will mostly be asked in production. They are now sampled at
    /// a fixed rate and contribute negatives only.</para>
    /// </remarks>
    public static IEnumerable<PunctuationDecisionExample> Build(ErrorPair pair, string split)
    {
        var sentence = pair.Correct;
        if (sentence.Length < 20 || sentence.Length > 300)
        {
            yield break;
        }

        var boundaries = WordBoundaries(sentence);
        var positives = boundaries.Where(b => b.HasComma).ToList();
        var eligibleNegatives = boundaries.Where(b => !b.HasComma).ToList();
        if (eligibleNegatives.Count == 0)
        {
            yield break;
        }

        // Deterministic per sentence, so the export reproduces byte for byte, and different
        // per sentence, so the sampling is not correlated across the corpus.
        var random = new Random(StableSeed(pair.PairId));

        int negativeQuota;
        if (positives.Count > 0)
        {
            negativeQuota = positives.Count;
        }
        else
        {
            // A sentence whose author needed no comma is evidence too, and it is the shape
            // production sees most. Sampled rather than taken wholesale: at one negative per
            // comma-free sentence the class balance stays where the rest of this method puts
            // it, and the corpus does not fill up with the easy half of the task.
            if (random.NextDouble() >= CommaFreeSentenceSampleRate) yield break;
            negativeQuota = 1;
        }

        var stripped = sentence.Replace(",", "", StringComparison.Ordinal);
        var index = 0;
        foreach (var boundary in positives)
        {
            yield return new PunctuationDecisionExample
            {
                Id = $"{pair.PairId}-comma-{index++}",
                Split = split,
                Sentence = stripped,
                Position = CommaFreePosition(sentence, boundary.Position),
                Correct = "COMMA",
            };
        }

        index = 0;
        foreach (var boundary in SampleWithoutReplacement(eligibleNegatives, negativeQuota, random))
        {
            yield return new PunctuationDecisionExample
            {
                Id = $"{pair.PairId}-nochange-{index++}",
                Split = split,
                Sentence = stripped,
                Position = CommaFreePosition(sentence, boundary.Position),
                Correct = "NO_CHANGE",
            };
        }
    }

    /// <summary>
    /// How often a sentence that needed no comma at all contributes an example.
    /// </summary>
    /// <remarks>
    /// These sentences are roughly half the corpus and they carry one boundary each, so
    /// admitting all of them would swamp the positives and re-create by volume the
    /// unconditional-NO_CHANGE bias the per-sentence cap exists to prevent. A quarter keeps
    /// the shape present without letting it dominate; the resulting balance is written into
    /// the manifest rather than assumed.
    /// </remarks>
    private const double CommaFreeSentenceSampleRate = 0.25;

    private static IEnumerable<(int Position, bool HasComma)> SampleWithoutReplacement(
        List<(int Position, bool HasComma)> source,
        int count,
        Random random)
    {
        var pool = new List<(int Position, bool HasComma)>(source);
        for (var i = pool.Count - 1; i > 0; i--)
        {
            var j = random.Next(i + 1);
            (pool[i], pool[j]) = (pool[j], pool[i]);
        }

        return pool.Take(count).OrderBy(b => b.Position);
    }

    /// <summary>A seed derived from the id, so the sampling survives a re-run and a re-order.</summary>
    private static int StableSeed(string id)
    {
        unchecked
        {
            var hash = 17;
            foreach (var ch in id) hash = (hash * 31) + ch;
            return hash;
        }
    }

    /// <summary>Maps an offset in the original sentence onto the comma-stripped one.</summary>
    private static int CommaFreePosition(string sentence, int position)
    {
        var commasBefore = 0;
        for (var i = 0; i < position && i < sentence.Length; i++)
        {
            if (sentence[i] == ',') commasBefore++;
        }

        return position - commasBefore;
    }

    private static List<(int Position, bool HasComma)> WordBoundaries(string sentence)
    {
        var boundaries = new List<(int, bool)>();
        for (var i = 1; i < sentence.Length - 1; i++)
        {
            if (!char.IsLetter(sentence[i - 1]))
            {
                continue;
            }

            if (sentence[i] == ',')
            {
                boundaries.Add((i, true));
            }
            else if (sentence[i] == ' ' && char.IsLetter(sentence[i + 1]))
            {
                boundaries.Add((i, false));
            }
        }

        return boundaries;
    }
}

/// <summary>Per-split counts, so class balance and split sizes are auditable without re-reading the data.</summary>
internal sealed class SplitCounts
{
    [JsonPropertyName("total")] public required int Total { get; init; }
    [JsonPropertyName("comma")] public required int Comma { get; init; }
    [JsonPropertyName("noChange")] public required int NoChange { get; init; }
    [JsonPropertyName("sentences")] public required int Sentences { get; init; }
}

internal sealed class Manifest
{
    [JsonPropertyName("generatedUtc")] public required string GeneratedUtc { get; init; }
    [JsonPropertyName("purpose")] public string Purpose =>
        "Training data for the WriteLite language engine. Phase 6 trains punctuation_decision "
        + "from this export; candidate_ranking is exported for diagnosis and is not trained on.";
    [JsonPropertyName("sources")] public required IReadOnlyList<string> Sources { get; init; }
    [JsonPropertyName("goldenPolicy")] public string GoldenPolicy =>
        "The golden benchmark is evaluation-only. Every example is checked against golden ids "
        + "and against normalised content fingerprints of every golden source and target sentence.";
    [JsonPropertyName("goldenItemsGuarded")] public required int GoldenItemsGuarded { get; init; }
    [JsonPropertyName("goldenFingerprintsGuarded")] public required int GoldenFingerprintsGuarded { get; init; }
    [JsonPropertyName("blockedAsGolden")] public required int BlockedAsGolden { get; init; }
    [JsonPropertyName("candidateRankingExamples")] public required int CandidateRankingExamples { get; init; }
    [JsonPropertyName("candidateGenerationFailures")] public required int CandidateGenerationFailures { get; init; }
    [JsonPropertyName("candidateGenerationFailureRate")] public required double CandidateGenerationFailureRate { get; init; }
    [JsonPropertyName("punctuationExamples")] public required int PunctuationExamples { get; init; }
    [JsonPropertyName("punctuationCommaExamples")] public required int PunctuationCommaExamples { get; init; }
    [JsonPropertyName("punctuationNoChangeExamples")] public required int PunctuationNoChangeExamples { get; init; }
    [JsonPropertyName("punctuationBySplit")] public required IReadOnlyDictionary<string, SplitCounts> PunctuationBySplit { get; init; }
    [JsonPropertyName("candidateRankingBySplit")] public required IReadOnlyDictionary<string, int> CandidateRankingBySplit { get; init; }

    public static Manifest Build(
        GoldenGuard guard,
        IReadOnlyList<CandidateRankingExample> ranking,
        IReadOnlyList<PunctuationDecisionExample> punctuation)
    {
        var failures = ranking.Count(x => x.CandidateGenerationFailure);
        return new Manifest
        {
            GeneratedUtc = DateTime.UtcNow.ToString("O"),
            Sources =
            [
                "ai/data/errors/rerank_train.jsonl",
                "ai/data/errors/rerank_validation.jsonl",
                "ai/data/errors/rerank_test.jsonl",
            ],
            GoldenItemsGuarded = guard.GoldenItemCount,
            GoldenFingerprintsGuarded = guard.GoldenSentenceCount,
            BlockedAsGolden = guard.BlockedCount,
            CandidateRankingExamples = ranking.Count,
            CandidateGenerationFailures = failures,
            CandidateGenerationFailureRate = ranking.Count == 0
                ? 0
                : Math.Round((double)failures / ranking.Count, 4),
            PunctuationExamples = punctuation.Count,
            PunctuationCommaExamples = punctuation.Count(x => x.Correct == "COMMA"),
            PunctuationNoChangeExamples = punctuation.Count(x => x.Correct == "NO_CHANGE"),
            PunctuationBySplit = punctuation
                .GroupBy(x => x.Split, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(
                    g => g.Key,
                    g => new SplitCounts
                    {
                        Total = g.Count(),
                        Comma = g.Count(x => x.Correct == "COMMA"),
                        NoChange = g.Count(x => x.Correct == "NO_CHANGE"),
                        Sentences = g.Select(x => x.Sentence).Distinct(StringComparer.Ordinal).Count(),
                    },
                    StringComparer.Ordinal),
            CandidateRankingBySplit = ranking
                .GroupBy(x => x.Split, StringComparer.Ordinal)
                .OrderBy(g => g.Key, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.Count(), StringComparer.Ordinal),
        };
    }
}
