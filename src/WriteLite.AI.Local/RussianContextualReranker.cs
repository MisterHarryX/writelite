using System.Diagnostics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace WriteLite.AI.Local;

/// <summary>
/// Scores whether a Russian sentence reads as correct, and uses that to choose
/// between spelling candidates in context.
/// </summary>
/// <remarks>
/// Edit distance and frequency cannot tell "компания проводит исследование"
/// from "кампания проводит исследование": both words exist, both are common,
/// and only the surrounding sentence decides. This is a small encoder
/// fine-tuned as a binary acceptability classifier — substitute each candidate
/// into the sentence, score all variants in one batch, and prefer the reading
/// the model finds most plausible.
///
/// It is deliberately a separate, tiny model rather than a prompt to the local
/// generative model: a 29M-parameter encoder answers in single-digit
/// milliseconds on CPU and cannot rewrite the user's text, which a generative
/// model asked the same question can and occasionally does.
///
/// Everything degrades cleanly: when the artefacts are absent the reranker
/// reports itself unavailable and the pipeline keeps its lexical ranking.
/// </remarks>
public sealed class RussianContextualReranker : IDisposable
{
    private readonly InferenceSession _session;
    private readonly WordPieceTokenizer _tokenizer;
    private readonly int _maxLength;
    private readonly string _inputIdsName;
    private readonly string _attentionMaskName;
    private readonly string? _tokenTypeName;
    private readonly bool _positiveIsSecondLogit;
    private bool _disposed;

    private RussianContextualReranker(
        InferenceSession session,
        WordPieceTokenizer tokenizer,
        int maxLength,
        string inputIdsName,
        string attentionMaskName,
        string? tokenTypeName,
        bool positiveIsSecondLogit,
        string modelDirectory,
        string modelVersion,
        TimeSpan loadTime)
    {
        _session = session;
        _tokenizer = tokenizer;
        _maxLength = maxLength;
        _inputIdsName = inputIdsName;
        _attentionMaskName = attentionMaskName;
        _tokenTypeName = tokenTypeName;
        _positiveIsSecondLogit = positiveIsSecondLogit;
        ModelDirectory = modelDirectory;
        ModelVersion = modelVersion;
        LoadTime = loadTime;
    }

    public string ModelDirectory { get; }
    public string ModelVersion { get; }
    public TimeSpan LoadTime { get; }
    public bool IsAvailable => !_disposed;

    /// <summary>
    /// The token window every sentence is padded or truncated to.
    /// </summary>
    /// <remarks>
    /// Exposed so a caller can tell whether the span it is asking about is inside the window.
    /// Beyond it the substituted variants are byte-identical after truncation, and the model
    /// returns the same score for every candidate — a tie that looks like an opinion.
    /// </remarks>
    public int MaxLength => _maxLength;

    /// <summary>Loads the reranker, or returns null when it is not deployed.</summary>
    public static RussianContextualReranker? TryLoad(string? modelDirectory = null)
    {
        var sw = Stopwatch.StartNew();
        var dir = modelDirectory
            ?? Path.Combine(AppContext.BaseDirectory, "models", "writelight-reranker");
        var modelPath = Path.Combine(dir, "model.onnx");
        var vocabPath = Path.Combine(dir, "vocab.txt");
        if (!File.Exists(modelPath) || !File.Exists(vocabPath)) return null;

        try
        {
            var maxLength = 64;
            var version = "reranker-unknown";
            var positiveIsSecondLogit = true;
            // rubert-tiny2 is cased. Defaulting to lowercase here would silently
            // resegment every capitalised word away from how training saw it.
            var lowercase = false;
            var configPath = Path.Combine(dir, "reranker.json");
            if (File.Exists(configPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(configPath));
                var root = document.RootElement;
                if (root.TryGetProperty("maxLength", out var m) && m.TryGetInt32(out var value)) maxLength = value;
                if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                    version = v.GetString() ?? version;
                if (root.TryGetProperty("positiveLabelIndex", out var p) && p.TryGetInt32(out var index))
                    positiveIsSecondLogit = index == 1;
                if (root.TryGetProperty("lowercase", out var l) && l.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    lowercase = l.GetBoolean();
            }

            var tokenizer = WordPieceTokenizer.LoadFromVocabFile(vocabPath, lowercase);
            if (tokenizer is null) return null;

            // Single-threaded: the caller is already off the UI thread and a
            // thread pool per session would fight the rest of the app for cores.
            var options = new SessionOptions
            {
                IntraOpNumThreads = 1,
                InterOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };

            var session = new InferenceSession(modelPath, options);
            var inputs = session.InputMetadata.Keys.ToArray();
            var inputIds = inputs.FirstOrDefault(n => n.Contains("input_ids", StringComparison.OrdinalIgnoreCase));
            var attention = inputs.FirstOrDefault(n => n.Contains("attention", StringComparison.OrdinalIgnoreCase));
            var tokenTypes = inputs.FirstOrDefault(n => n.Contains("token_type", StringComparison.OrdinalIgnoreCase));
            if (inputIds is null || attention is null)
            {
                session.Dispose();
                return null;
            }

            sw.Stop();
            return new RussianContextualReranker(
                session, tokenizer, maxLength, inputIds, attention, tokenTypes,
                positiveIsSecondLogit, dir, version, sw.Elapsed);
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or IOException or JsonException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    public readonly record struct RerankedCandidate(string Word, double Acceptability, int Rank);

    /// <summary>Probability that the sentence reads as correct Russian.</summary>
    public double ScoreSentence(string sentence) => ScoreBatch([sentence])[0];

    /// <summary>
    /// Scores the sentence once per candidate, with the candidate substituted
    /// over <paramref name="start"/>..<paramref name="start"/>+<paramref name="length"/>.
    /// </summary>
    public IReadOnlyList<RerankedCandidate> Rerank(
        string sentence,
        int start,
        int length,
        IReadOnlyList<string> candidates,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0) return [];
        if (start < 0 || length < 0 || start + length > sentence.Length) return [];

        var variants = new string[candidates.Count];
        for (var i = 0; i < candidates.Count; i++)
        {
            variants[i] = string.Concat(sentence.AsSpan(0, start), candidates[i], sentence.AsSpan(start + length));
        }

        cancellationToken.ThrowIfCancellationRequested();
        var scores = ScoreBatch(variants);

        return candidates
            .Select((word, i) => (word, score: scores[i]))
            .OrderByDescending(x => x.score)
            .Select((x, rank) => new RerankedCandidate(x.word, x.score, rank))
            .ToArray();
    }

    /// <summary>
    /// True when the text as written scores clearly worse than the best
    /// alternative — the signature of a real-word error, where the token is a
    /// perfectly good Russian word that does not belong in this sentence.
    /// </summary>
    public bool IndicatesRealWordError(
        string sentence,
        int start,
        int length,
        IReadOnlyList<string> candidates,
        double margin = 0.25,
        CancellationToken cancellationToken = default)
    {
        if (candidates.Count == 0) return false;
        var asWritten = ScoreSentence(sentence);
        var reranked = Rerank(sentence, start, length, candidates, cancellationToken);
        return reranked.Count > 0 && reranked[0].Acceptability - asWritten >= margin;
    }

    private double[] ScoreBatch(IReadOnlyList<string> sentences)
    {
        var batch = sentences.Count;
        var inputIds = new DenseTensor<long>([batch, _maxLength]);
        var attention = new DenseTensor<long>([batch, _maxLength]);
        var tokenTypes = _tokenTypeName is null ? null : new DenseTensor<long>([batch, _maxLength]);

        for (var row = 0; row < batch; row++)
        {
            var encoded = _tokenizer.Encode(sentences[row], _maxLength);
            for (var column = 0; column < _maxLength; column++)
            {
                inputIds[row, column] = encoded.InputIds[column];
                attention[row, column] = encoded.AttentionMask[column];
                if (tokenTypes is not null) tokenTypes[row, column] = 0;
            }
        }

        var inputs = new List<NamedOnnxValue>(3)
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName, inputIds),
            NamedOnnxValue.CreateFromTensor(_attentionMaskName, attention),
        };
        if (tokenTypes is not null && _tokenTypeName is not null)
        {
            inputs.Add(NamedOnnxValue.CreateFromTensor(_tokenTypeName, tokenTypes));
        }

        using var results = _session.Run(inputs);
        var logits = results.First().AsTensor<float>();
        var classes = logits.Dimensions.Length > 1 ? logits.Dimensions[^1] : 1;

        var scores = new double[batch];
        for (var row = 0; row < batch; row++)
        {
            if (classes >= 2)
            {
                var negative = logits[row, 0];
                var positive = logits[row, 1];
                if (!_positiveIsSecondLogit) (negative, positive) = (positive, negative);
                // Two-class softmax, written as a logistic on the difference to
                // avoid overflow on confident predictions.
                scores[row] = 1.0 / (1.0 + Math.Exp(negative - positive));
            }
            else
            {
                scores[row] = 1.0 / (1.0 + Math.Exp(-logits[row, 0]));
            }
        }

        return scores;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
