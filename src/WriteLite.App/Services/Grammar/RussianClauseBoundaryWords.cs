namespace WriteLite.Services.Grammar;

/// <summary>
/// The words that end one clause and open the next.
/// </summary>
/// <remarks>
/// <para><b>Why this is shared.</b> Two rules made the same mistake independently, and both
/// were found by pasting an ordinary 480-word report into the editor rather than by any
/// corpus. Each scanned rightwards for a subject and neither stopped at a clause boundary,
/// so each attached a subject from a following clause to a verb in this one:</para>
///
/// <list type="bullet">
/// <item><see cref="RussianAgreementAnalyzer"/> read «объём данных растёт быстрее чем мы
/// ожидали», crossed «чем», found «мы», and offered «растём» — at confidence 0.88 with the
/// auto-apply flag set.</item>
/// <item><see cref="RussianClauseCommaAnalyzer"/> read «реагировали на обращения пользователей
/// и во многих случаях находили причину до того как проблема становилась заметной», crossed
/// «до того как», found «проблема», concluded the second half had its own subject, and put a
/// comma between two homogeneous predicates.</item>
/// </list>
///
/// <para>A shared list is what keeps the third rule of this shape from having to rediscover
/// it. The membership is deliberately plain: these are closed-class function words, and the
/// question each rule asks — "may I keep scanning past this?" — has the same answer in every
/// one of them.</para>
/// </remarks>
internal static class RussianClauseBoundaryWords
{
    /// <summary>True when scanning rightwards must stop here.</summary>
    public static bool Contains(string word) => Words.Contains(word);

    private static readonly HashSet<string> Words = new(StringComparer.OrdinalIgnoreCase)
    {
        // Subordinators.
        "что", "чтобы", "чем", "как", "если", "когда", "пока", "хотя", "поскольку", "потому",
        "будто", "словно", "ибо", "раз", "лишь", "едва",

        // Relatives.
        "который", "которая", "которое", "которые", "которых", "котором", "которой", "которым",
        "кто", "чей", "чья", "чьё", "чьи",

        // Interrogative and relative adverbs.
        "где", "куда", "откуда", "зачем", "почему", "сколько",

        // Coordinators.
        "и", "а", "но", "или", "либо", "зато", "однако", "причём", "тогда", "поэтому", "значит",
    };
}
