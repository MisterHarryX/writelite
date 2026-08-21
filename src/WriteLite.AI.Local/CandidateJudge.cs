using System.Text;
using System.Text.Json;
using WriteLite.AI.Contracts;

namespace WriteLite.AI.Local;

/// <summary>
/// Asks the local model to pick one option from a closed list, and refuses anything else.
/// </summary>
/// <remarks>
/// <para>An experimental path. Nothing in the shipping pipeline calls it; it is driven by
/// <c>tools/judgebench</c> so its value can be measured before it is given any authority.</para>
///
/// <para><b>Why this shape.</b> Phase 4 measured the free-generation path at 0.115 precision
/// against 0.958–0.972 for every deterministic layer, and the forensics showed the failures
/// were not near-misses: the model invented commas in the wrong places and rewrote words
/// that were already right. Selective routing did not help — with 13.2 % of sentences
/// consulted, precision was 0.115, identical. That is what a generation task costs here.</para>
///
/// <para>A judge is a different task with a different worst case. It never sees a blank to
/// fill in, only a menu, so it cannot introduce a token that no deterministic layer proposed.
/// Its errors are bounded by the candidate generator: if the right answer is on the menu it
/// can be chosen, and if it is not, that is recorded as a candidate-generation failure rather
/// than papered over by the model guessing something plausible.</para>
///
/// <para><b>What is enforced.</b> The reply must be JSON naming exactly one offered id.
/// Free-form text, an invented replacement, an unknown id, more than one id, or malformed
/// JSON all produce <see cref="CandidateJudgeOutcome.Rejected"/> and the caller falls back to
/// deterministic ranking. There is no partial credit and no repair of a bad answer — a model
/// that cannot follow a one-field schema is not a model to trust with the choice.</para>
/// </remarks>
public sealed class CandidateJudge
{
    /// <summary>The option that always exists: leave the text alone.</summary>
    /// <remarks>
    /// Never omitted, and deliberately given an id like any other option rather than being
    /// the absence of an answer. A judge that can only choose between replacements is a
    /// judge biased into changing something, and the largest single category in the frozen
    /// corpus — 277 of 841 items — is text that must come back untouched.
    /// </remarks>
    public const string NoChangeId = "NO_CHANGE";

    private const int MaxCandidates = 8;

    /// <summary>
    /// Enough for <c>{"candidateId":"NO_CHANGE","confidence":0.9}</c> and nothing else.
    /// A budget this tight is itself a guard: there is no room for prose.
    /// </summary>
    private const int MaxAnswerTokens = 24;

    private const string SystemPrompt =
        "Ты — строгий классификатор для проверки русского текста. "
        + "Тебе дают предложение, выделенный фрагмент и пронумерованный список вариантов. "
        + "Выбери ровно один вариант из списка. "
        + "Если фрагмент не нужно менять, выбери NO_CHANGE. "
        + "Ответь только JSON вида {\"candidateId\":\"A\"}. "
        + "Не объясняй, не переписывай предложение, не предлагай свой вариант.";

    private readonly QwenModelBackend _backend;

    public CandidateJudge(QwenModelBackend backend)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
    }

    /// <summary>Builds the option list for a span, with NO_CHANGE always last.</summary>
    /// <remarks>
    /// Candidates arrive already validated by the deterministic layer — every one of them is
    /// a real form from the lexicon. Duplicates and anything equal to the target are dropped,
    /// because offering the target twice under two ids is a way to make NO_CHANGE lose.
    /// </remarks>
    public static IReadOnlyList<JudgeCandidate> BuildCandidates(
        string target,
        IEnumerable<string> validatedCandidates)
    {
        var options = new List<JudgeCandidate>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { target };

        foreach (var candidate in validatedCandidates)
        {
            if (string.IsNullOrWhiteSpace(candidate) || !seen.Add(candidate))
            {
                continue;
            }

            options.Add(new JudgeCandidate(((char)('A' + options.Count)).ToString(), candidate));
            if (options.Count >= MaxCandidates)
            {
                break;
            }
        }

        options.Add(new JudgeCandidate(NoChangeId, string.Empty));
        return options;
    }

    public async Task<CandidateJudgeVerdict> JudgeAsync(
        CandidateJudgeRequest request,
        CancellationToken cancellationToken = default)
    {
        // Only NO_CHANGE on the menu means there is no question to ask.
        if (request.Candidates.Count <= 1)
        {
            return CandidateJudgeVerdict.NotConsulted("no-candidates");
        }

        var answer = await _backend.CompleteAsync(
            SystemPrompt,
            BuildPrompt(request),
            MaxAnswerTokens,

            // Deterministic. This is a classification, and a benchmark that moves between
            // identical runs cannot support a decision about whether to ship the path.
            temperature: 0.0,
            cancellationToken).ConfigureAwait(false);

        if (answer is null)
        {
            return CandidateJudgeVerdict.NotConsulted("backend-unavailable");
        }

        return Parse(answer, request.Candidates);
    }

    internal static string BuildPrompt(CandidateJudgeRequest request)
    {
        var builder = new StringBuilder();
        builder.Append("Предложение: ").AppendLine(request.Sentence);
        builder.Append("Фрагмент: ").AppendLine(request.Target);
        builder.AppendLine("Варианты:");
        foreach (var candidate in request.Candidates)
        {
            builder.Append(candidate.Id).Append(": ");
            builder.AppendLine(candidate.Id == NoChangeId ? "оставить без изменений" : candidate.Text);
        }

        builder.Append("Ответ:");
        return builder.ToString();
    }

    /// <summary>
    /// Parses the reply, accepting only a single offered id.
    /// </summary>
    /// <remarks>
    /// A fenced block or leading whitespace is tolerated because small models emit them
    /// habitually and the content inside is still a clean object. Nothing else is: a bare
    /// letter, a rewritten sentence, two ids, or an id that was not offered are all rejected
    /// with a reason, so the benchmark can report <em>how</em> the contract failed rather
    /// than only that it did.
    /// </remarks>
    internal static CandidateJudgeVerdict Parse(string answer, IReadOnlyList<JudgeCandidate> offered)
    {
        var json = ExtractJsonObject(answer);
        if (json is null)
        {
            return CandidateJudgeVerdict.Reject("not-json");
        }

        string? id;
        double? confidence = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return CandidateJudgeVerdict.Reject("not-an-object");
            }

            if (!document.RootElement.TryGetProperty("candidateId", out var idElement))
            {
                return CandidateJudgeVerdict.Reject("missing-candidateId");
            }

            // An array here is the model hedging between two options, which is exactly the
            // answer this path must not accept.
            if (idElement.ValueKind is JsonValueKind.Array)
            {
                return CandidateJudgeVerdict.Reject("multiple-candidates");
            }

            if (idElement.ValueKind is not JsonValueKind.String)
            {
                return CandidateJudgeVerdict.Reject("candidateId-not-a-string");
            }

            id = idElement.GetString();

            if (document.RootElement.TryGetProperty("confidence", out var confidenceElement)
                && confidenceElement.ValueKind is JsonValueKind.Number
                && confidenceElement.TryGetDouble(out var parsed))
            {
                confidence = Math.Clamp(parsed, 0.0, 1.0);
            }
        }
        catch (JsonException)
        {
            return CandidateJudgeVerdict.Reject("invalid-json");
        }

        if (string.IsNullOrWhiteSpace(id))
        {
            return CandidateJudgeVerdict.Reject("empty-candidateId");
        }

        id = id.Trim();
        var match = offered.FirstOrDefault(c => string.Equals(c.Id, id, StringComparison.OrdinalIgnoreCase));
        if (match is null)
        {
            // Covers both an id that was never offered and a model that answered with the
            // replacement text instead of the id — the invented-token case §24 forbids.
            return CandidateJudgeVerdict.Reject("unknown-candidate");
        }

        return match.Id == NoChangeId
            ? new CandidateJudgeVerdict(CandidateJudgeOutcome.NoChange, NoChangeId, string.Empty, confidence)
            : new CandidateJudgeVerdict(CandidateJudgeOutcome.Selected, match.Id, match.Text, confidence);
    }

    private static string? ExtractJsonObject(string answer)
    {
        var start = answer.IndexOf('{');
        var end = answer.LastIndexOf('}');
        return start >= 0 && end > start ? answer[start..(end + 1)] : null;
    }
}
