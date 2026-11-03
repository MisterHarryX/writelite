using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace WriteLite.Services.Translation;

/// <summary>Translates text between two languages on this machine.</summary>
public interface ITextTranslator
{
    bool Supports(string from, string to);

    Task<string> TranslateAsync(string text, string from, string to, CancellationToken cancellationToken = default);
}

/// <summary>Cheap script detection, enough to choose a translation direction.</summary>
public static class LanguageDetector
{
    public static bool IsMostlyLatin(string text)
    {
        int latin = 0, cyrillic = 0;
        foreach (var c in text)
        {
            if (c is >= 'a' and <= 'z' or >= 'A' and <= 'Z') latin++;
            else if (c is >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё') cyrillic++;
        }

        return latin > cyrillic;
    }
}

/// <summary>
/// Offline translator over Marian (opus-mt) models exported to ONNX.
/// </summary>
/// <remarks>
/// <para>One <see cref="MarianModel"/> per direction under <c>models/writelite-translate/&lt;from&gt;-&lt;to&gt;</c>,
/// loaded on first use (about a second) and kept. Text is split into lines and sentences, each
/// sentence is translated on its own (greedy decoding, ~30–120 ms on a laptop CPU) and the
/// original line breaks and spacing between sentences are kept, so a translated chat message
/// keeps its shape.</para>
///
/// <para>Nothing here touches the network; the models are files that ship with WriteLite.</para>
/// </remarks>
public sealed class MarianTranslator : ITextTranslator, IDisposable
{
    private readonly string _root;
    private readonly Dictionary<string, Lazy<MarianModel?>> _models = new(StringComparer.Ordinal);

    public MarianTranslator(string? root = null)
    {
        _root = root ?? Path.Combine(AppContext.BaseDirectory, "models", "writelite-translate");
        foreach (var pair in new[] { "ru-en", "en-ru" })
        {
            var dir = Path.Combine(_root, pair);
            _models[pair] = new Lazy<MarianModel?>(() => MarianModel.TryLoad(dir), LazyThreadSafetyMode.ExecutionAndPublication);
        }
    }

    public bool Supports(string from, string to)
    {
        var key = $"{from}-{to}";
        return _models.ContainsKey(key) && File.Exists(Path.Combine(_root, key, "decoder.onnx"));
    }

    public Task<string> TranslateAsync(string text, string from, string to, CancellationToken cancellationToken = default)
    {
        var key = $"{from}-{to}";
        if (!_models.TryGetValue(key, out var lazy))
        {
            throw new NotSupportedException($"Нет модели перевода {key}.");
        }

        return Task.Run(() =>
        {
            var model = lazy.Value ?? throw new InvalidOperationException("Модель перевода не загрузилась.");
            return TranslateText(model, text, cancellationToken);
        }, cancellationToken);
    }

    private static readonly Regex SentenceSplit = new(@"(?<=[.!?…])\s+(?=\S)", RegexOptions.Compiled);

    private static string TranslateText(MarianModel model, string text, CancellationToken cancellationToken)
    {
        var result = new StringBuilder(text.Length + 16);
        var lines = text.Split('\n');
        for (var li = 0; li < lines.Length; li++)
        {
            if (li > 0) result.Append('\n');
            var line = lines[li];
            var trailingCr = line.EndsWith('\r');
            if (trailingCr) line = line[..^1];
            if (string.IsNullOrWhiteSpace(line))
            {
                result.Append(line);
            }
            else
            {
                var lead = line.Length - line.TrimStart().Length;
                result.Append(line, 0, lead);
                var parts = SentenceSplit.Split(line.Trim());
                for (var i = 0; i < parts.Length; i++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (i > 0) result.Append(' ');
                    result.Append(model.Translate(parts[i]));
                }
            }

            if (trailingCr) result.Append('\r');
        }

        return result.ToString();
    }

    public void Dispose()
    {
        foreach (var lazy in _models.Values)
        {
            if (lazy.IsValueCreated) lazy.Value?.Dispose();
        }
    }
}

/// <summary>One translation direction: tokenizers, vocabulary and the two ONNX graphs.</summary>
internal sealed class MarianModel : IDisposable
{
    private readonly InferenceSession _encoder;
    private readonly InferenceSession _decoder;
    private readonly UnigramTokenizer _source;
    private readonly Dictionary<string, int> _vocab;
    private readonly string[] _pieces;
    private readonly int _eos;
    private readonly int _pad;
    private readonly int _unk;
    private readonly int _decoderStart;

    private MarianModel(InferenceSession encoder, InferenceSession decoder, UnigramTokenizer source,
        Dictionary<string, int> vocab, string[] pieces, int eos, int pad, int unk, int decoderStart)
    {
        _encoder = encoder;
        _decoder = decoder;
        _source = source;
        _vocab = vocab;
        _pieces = pieces;
        _eos = eos;
        _pad = pad;
        _unk = unk;
        _decoderStart = decoderStart;
    }

    public static MarianModel? TryLoad(string dir)
    {
        try
        {
            var configPath = Path.Combine(dir, "config.json");
            if (!File.Exists(configPath)) return null;
            using var config = JsonDocument.Parse(File.ReadAllText(configPath));
            var root = config.RootElement;
            var pieces = File.ReadAllLines(Path.Combine(dir, "vocab.tsv"), Encoding.UTF8);
            var vocab = new Dictionary<string, int>(pieces.Length, StringComparer.Ordinal);
            for (var i = 0; i < pieces.Length; i++) vocab.TryAdd(pieces[i], i);
            var options = new SessionOptions
            {
                IntraOpNumThreads = Math.Clamp(Environment.ProcessorCount / 2, 1, 4),
                InterOpNumThreads = 1,
                GraphOptimizationLevel = GraphOptimizationLevel.ORT_ENABLE_ALL,
            };
            var model = new MarianModel(
                new InferenceSession(Path.Combine(dir, "encoder.onnx"), options),
                new InferenceSession(Path.Combine(dir, "decoder.onnx"), options),
                UnigramTokenizer.Load(Path.Combine(dir, "source.tsv")),
                vocab,
                pieces,
                root.GetProperty("eos_id").GetInt32(),
                root.GetProperty("pad_id").GetInt32(),
                root.GetProperty("unk_id").GetInt32(),
                root.GetProperty("decoder_start_id").GetInt32());
            CompatibilityLogger.Technical("translator-loaded", $"dir={Path.GetFileName(dir)} vocab={pieces.Length}");
            return model;
        }
        catch (Exception ex)
        {
            CompatibilityLogger.Technical("translator-load-failed", $"type={ex.GetType().Name} message={ex.Message}");
            return null;
        }
    }

    public string Translate(string sentence)
    {
        if (string.IsNullOrWhiteSpace(sentence)) return sentence;
        var ids = new List<long>();
        foreach (var piece in _source.Encode(sentence))
        {
            ids.Add(_vocab.TryGetValue(piece, out var id) ? id : _unk);
        }

        ids.Add(_eos);
        if (ids.Count > 510) ids = [.. ids.Take(509), _eos];

        var length = ids.Count;
        var inputIds = new DenseTensor<long>(ids.ToArray(), [1, length]);
        var mask = new DenseTensor<long>(Enumerable.Repeat(1L, length).ToArray(), [1, length]);
        using var encoded = _encoder.Run(
        [
            NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
            NamedOnnxValue.CreateFromTensor("attention_mask", mask),
        ]);
        var hidden = encoded.First().AsTensor<float>().ToDenseTensor();

        var output = new List<long> { _decoderStart };
        var maxNew = Math.Min(400, length * 2 + 16);
        var lastRepeat = 0;
        for (var step = 0; step < maxNew; step++)
        {
            var decoderIds = new DenseTensor<long>(output.ToArray(), [1, output.Count]);
            using var decoded = _decoder.Run(
            [
                NamedOnnxValue.CreateFromTensor("input_ids", decoderIds),
                NamedOnnxValue.CreateFromTensor("encoder_hidden_states", hidden),
                NamedOnnxValue.CreateFromTensor("encoder_attention_mask", mask),
            ]);
            var logits = decoded.First().AsTensor<float>();
            var vocabSize = logits.Dimensions[^1];
            var best = -1;
            var bestScore = float.NegativeInfinity;
            for (var v = 0; v < vocabSize; v++)
            {
                if (v == _pad) continue;
                var score = logits[0, v];
                if (score > bestScore)
                {
                    bestScore = score;
                    best = v;
                }
            }

            if (best < 0 || best == _eos) break;

            // A greedy decoder can loop on a phrase; three identical tokens in a row is the tell.
            lastRepeat = output[^1] == best ? lastRepeat + 1 : 0;
            if (lastRepeat >= 3) break;
            output.Add(best);
        }

        var text = new StringBuilder();
        foreach (var id in output.Skip(1))
        {
            if (id == _eos || id == _pad || id == _unk || id < 0 || id >= _pieces.Length) continue;
            var piece = _pieces[id];
            if (piece.StartsWith('<') && piece.EndsWith('>')) continue;
            text.Append(piece);
        }

        return text.ToString().Replace('▁', ' ').Trim();
    }

    public void Dispose()
    {
        _encoder.Dispose();
        _decoder.Dispose();
    }
}

/// <summary>
/// SentencePiece unigram segmentation (Viterbi over piece log-probabilities).
/// </summary>
/// <remarks>
/// A small reimplementation rather than a native dependency: the pieces and scores are exported
/// from the model's <c>.spm</c> by the build script, and segmentation is the one part of
/// SentencePiece Marian needs. Normalisation is NFKC plus whitespace folding, which is what the
/// opus-mt models were trained with (<c>nmt_nfkc</c>).
/// </remarks>
internal sealed class UnigramTokenizer
{
    private const char Space = '▁';
    private readonly Dictionary<string, float> _scores;
    private readonly int _maxPieceLength;
    private readonly float _unkScore;

    private UnigramTokenizer(Dictionary<string, float> scores, int maxPieceLength, float minScore)
    {
        _scores = scores;
        _maxPieceLength = maxPieceLength;
        _unkScore = minScore - 10f;
    }

    public static UnigramTokenizer Load(string path)
    {
        var scores = new Dictionary<string, float>(StringComparer.Ordinal);
        var max = 1;
        var min = 0f;
        foreach (var line in File.ReadLines(path, Encoding.UTF8))
        {
            var parts = line.Split('\t');
            if (parts.Length < 3 || parts[2] == "1") continue;
            var score = float.Parse(parts[1], CultureInfo.InvariantCulture);
            scores[parts[0]] = score;
            max = Math.Max(max, parts[0].Length);
            min = Math.Min(min, score);
        }

        return new UnigramTokenizer(scores, max, min);
    }

    public IReadOnlyList<string> Encode(string text)
    {
        var normalized = Normalize(text);
        var n = normalized.Length;
        var best = new float[n + 1];
        var from = new int[n + 1];
        Array.Fill(best, float.NegativeInfinity);
        best[0] = 0;
        for (var end = 1; end <= n; end++)
        {
            for (var len = 1; len <= _maxPieceLength && len <= end; len++)
            {
                var start = end - len;
                if (float.IsNegativeInfinity(best[start])) continue;
                if (!_scores.TryGetValue(normalized.Substring(start, len), out var score))
                {
                    if (len != 1) continue;
                    score = _unkScore;
                }

                var total = best[start] + score;
                if (total > best[end])
                {
                    best[end] = total;
                    from[end] = start;
                }
            }
        }

        var pieces = new List<string>();
        for (var end = n; end > 0; end = from[end])
        {
            pieces.Add(normalized.Substring(from[end], end - from[end]));
        }

        pieces.Reverse();
        return pieces;
    }

    private static string Normalize(string text)
    {
        var nfkc = text.Normalize(NormalizationForm.FormKC);
        var builder = new StringBuilder(nfkc.Length + 1);
        builder.Append(Space);
        var lastSpace = true;
        foreach (var c in nfkc.Trim())
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastSpace) builder.Append(Space);
                lastSpace = true;
                continue;
            }

            builder.Append(c);
            lastSpace = false;
        }

        return builder.ToString();
    }
}
