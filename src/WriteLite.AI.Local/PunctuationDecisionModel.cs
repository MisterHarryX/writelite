using System.Diagnostics;
using System.Text.Json;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace WriteLite.AI.Local;

/// <summary>
/// Answers one question: does a comma belong at this boundary of this sentence?
/// </summary>
/// <remarks>
/// <para>The narrowness is the design. Phases 4 and 5 measured a generative model rewriting
/// Russian at 0.115 and then 0.211 precision against 0.958–0.972 for every deterministic
/// layer, and after the diff defect was removed three-quarters of what remained were commas
/// placed in the wrong position. This model cannot place a comma in the wrong position,
/// because it is not the one choosing positions: the caller enumerates boundaries and the
/// model returns a probability for each. Its entire output surface is one number per
/// boundary.</para>
///
/// <para>Rules already take the part of Russian comma placement that a closed list can
/// settle — introductory words and adversative conjunctions, added in Phase 5 with zero false
/// positives across 841 items. What is left needs to know where a clause ends, whether a
/// token is a gerund with dependents, and whether «и» joins predicates or clauses. Those are
/// the 17 of 24 misses the rule analysis explicitly declined to guess at.</para>
///
/// <para>Everything degrades cleanly. Absent artefacts mean <see cref="TryLoad"/> returns
/// null and the pipeline keeps its rule-based punctuation exactly as before.</para>
/// </remarks>
public sealed class PunctuationDecisionModel : IDisposable
{
    private readonly InferenceSession _session;
    private readonly WordPieceTokenizer _tokenizer;
    private readonly int _maxLength;
    private readonly bool _segmentEncoding;
    private readonly string _inputIdsName;
    private readonly string _attentionMaskName;
    private readonly string? _tokenTypeName;
    private readonly int _commaLabelIndex;
    private bool _disposed;

    private PunctuationDecisionModel(
        InferenceSession session,
        WordPieceTokenizer tokenizer,
        int maxLength,
        bool segmentEncoding,
        string inputIdsName,
        string attentionMaskName,
        string? tokenTypeName,
        int commaLabelIndex,
        double acceptanceThreshold,
        string modelDirectory,
        string modelVersion,
        TimeSpan loadTime)
    {
        _session = session;
        _tokenizer = tokenizer;
        _maxLength = maxLength;
        _segmentEncoding = segmentEncoding;
        _inputIdsName = inputIdsName;
        _attentionMaskName = attentionMaskName;
        _tokenTypeName = tokenTypeName;
        _commaLabelIndex = commaLabelIndex;
        AcceptanceThreshold = acceptanceThreshold;
        ModelDirectory = modelDirectory;
        ModelVersion = modelVersion;
        LoadTime = loadTime;
    }

    public string ModelDirectory { get; }
    public string ModelVersion { get; }
    public TimeSpan LoadTime { get; }
    public bool IsAvailable => !_disposed;

    /// <summary>
    /// The COMMA probability at or above which a finding is worth offering.
    /// </summary>
    /// <remarks>
    /// Read from the artefact rather than hardcoded. Training is balanced roughly 50/50
    /// because an uncapped corpus is 95 % NO_CHANGE and teaches a classifier to answer
    /// NO_CHANGE unconditionally; production is the uncapped distribution. The threshold is
    /// what reconciles the two, it is chosen on the validation split, and it is frozen before
    /// the golden benchmark is evaluated — so re-tuning it is a re-export, not a rebuild.
    /// </remarks>
    public double AcceptanceThreshold { get; }

    /// <summary>Loads the model, or returns null when it is not deployed.</summary>
    public static PunctuationDecisionModel? TryLoad(string? modelDirectory = null)
    {
        var sw = Stopwatch.StartNew();
        var dir = modelDirectory ?? Resolve();
        if (dir is null) return null;

        var modelPath = Path.Combine(dir, "model.onnx");
        var vocabPath = Path.Combine(dir, "vocab.txt");
        if (!File.Exists(modelPath) || !File.Exists(vocabPath)) return null;

        try
        {
            var maxLength = 64;
            var version = "punctuation-unknown";
            var commaLabelIndex = 1;
            var threshold = 0.5;
            var segmentEncoding = true;
            // rubert-tiny2 is cased; defaulting to lowercase would silently resegment every
            // capitalised word away from how training saw it.
            var lowercase = false;

            var configPath = Path.Combine(dir, "punctuation.json");
            if (File.Exists(configPath))
            {
                using var document = JsonDocument.Parse(File.ReadAllText(configPath));
                var root = document.RootElement;
                if (root.TryGetProperty("maxLength", out var m) && m.TryGetInt32(out var value)) maxLength = value;
                if (root.TryGetProperty("version", out var v) && v.ValueKind == JsonValueKind.String)
                    version = v.GetString() ?? version;
                if (root.TryGetProperty("commaLabelIndex", out var c) && c.TryGetInt32(out var index))
                    commaLabelIndex = index;
                if (root.TryGetProperty("acceptanceThreshold", out var t) && t.TryGetDouble(out var value2))
                    threshold = value2;
                if (root.TryGetProperty("encoding", out var e) && e.ValueKind == JsonValueKind.String)
                    segmentEncoding = e.GetString() != "marker";
                if (root.TryGetProperty("lowercase", out var l) && l.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    lowercase = l.GetBoolean();
            }

            var tokenizer = WordPieceTokenizer.LoadFromVocabFile(vocabPath, lowercase);
            if (tokenizer is null) return null;

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
            return new PunctuationDecisionModel(
                session, tokenizer, maxLength, segmentEncoding, inputIds, attention, tokenTypes,
                commaLabelIndex, threshold, dir, version, sw.Elapsed);
        }
        catch (Exception ex) when (ex is OnnxRuntimeException or IOException or JsonException or PlatformNotSupportedException)
        {
            return null;
        }
    }

    /// <summary>
    /// Where the artefacts live: beside the executable when deployed, or at the repository
    /// root when a harness is running from <c>bin/Release/…</c>.
    /// </summary>
    /// <remarks>
    /// The second case is not a convenience. Every benchmark in this project runs from a build
    /// output directory, and a model that silently fails to load there would score as "the
    /// model did not help" rather than as "the model was not there" — two results that look
    /// identical in a report and mean opposite things.
    /// </remarks>
    private static string? Resolve()
    {
        var candidates = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "models", "writelite-punctuation"),
        };

        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "WriteLite.sln")))
            {
                candidates.Add(Path.Combine(dir.FullName, "models", "writelite-punctuation"));
                break;
            }

            dir = dir.Parent;
        }

        return candidates.FirstOrDefault(c => File.Exists(Path.Combine(c, "model.onnx")));
    }

    public readonly record struct CommaDecision(int Position, double Probability)
    {
        public bool Accepted(double threshold) => Probability >= threshold;
    }

    /// <summary>Marks the candidate boundary when the model was trained with marker encoding.</summary>
    private const string Marker = "¦";

    /// <summary>
    /// Word boundaries in a sentence where a comma could go.
    /// </summary>
    /// <remarks>
    /// A boundary is a space with a letter on each side. Positions that already carry a comma
    /// are not offered: this model adds commas, and removing a comma the writer chose is a
    /// different decision with a very different cost. Everything else the caller wants to
    /// exclude — inside quotes, inside a URL, inside a protected span — is the caller's to
    /// exclude, because only the caller knows what it protected.
    /// </remarks>
    public static IReadOnlyList<int> CandidateBoundaries(string sentence)
    {
        var boundaries = new List<int>();
        if (string.IsNullOrEmpty(sentence)) return boundaries;

        for (var i = 1; i < sentence.Length - 1; i++)
        {
            if (sentence[i] != ' ') continue;
            if (!char.IsLetter(sentence[i - 1])) continue;
            if (!char.IsLetter(sentence[i + 1])) continue;
            boundaries.Add(i);
        }

        return boundaries;
    }

    /// <summary>COMMA probability for each requested boundary, scored in one batch.</summary>
    public IReadOnlyList<CommaDecision> Score(
        string sentence,
        IReadOnlyList<int> positions,
        CancellationToken cancellationToken = default)
    {
        if (_disposed || positions.Count == 0 || string.IsNullOrEmpty(sentence)) return [];

        var batch = positions.Count;
        var inputIds = new DenseTensor<long>([batch, _maxLength]);
        var attention = new DenseTensor<long>([batch, _maxLength]);
        var tokenTypes = _tokenTypeName is null ? null : new DenseTensor<long>([batch, _maxLength]);

        for (var row = 0; row < batch; row++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var position = Math.Clamp(positions[row], 0, sentence.Length);
            var encoded = _segmentEncoding
                ? _tokenizer.EncodePair(sentence[..position].Trim(), sentence[position..].Trim(), _maxLength)
                : _tokenizer.Encode(
                    $"{sentence[..position]} {Marker} {sentence[position..]}".Trim(), _maxLength);

            for (var column = 0; column < _maxLength; column++)
            {
                inputIds[row, column] = encoded.InputIds[column];
                attention[row, column] = encoded.AttentionMask[column];
                if (tokenTypes is not null) tokenTypes[row, column] = encoded.TokenTypeIds[column];
            }
        }

        var feeds = new List<NamedOnnxValue>
        {
            NamedOnnxValue.CreateFromTensor(_inputIdsName, inputIds),
            NamedOnnxValue.CreateFromTensor(_attentionMaskName, attention),
        };
        if (_tokenTypeName is not null && tokenTypes is not null)
        {
            feeds.Add(NamedOnnxValue.CreateFromTensor(_tokenTypeName, tokenTypes));
        }

        using var results = _session.Run(feeds);
        var logits = results.First().AsTensor<float>();
        var classes = logits.Dimensions[^1];

        var decisions = new CommaDecision[batch];
        for (var row = 0; row < batch; row++)
        {
            var negative = logits[row, _commaLabelIndex == 1 ? 0 : 1];
            var positive = logits[row, _commaLabelIndex];
            decisions[row] = new CommaDecision(positions[row], Sigmoid(positive - negative));
        }

        _ = classes;
        return decisions;
    }

    /// <summary>Two-class softmax, expressed as a sigmoid over the logit difference.</summary>
    private static double Sigmoid(double x) => 1.0 / (1.0 + Math.Exp(-x));

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _session.Dispose();
    }
}
