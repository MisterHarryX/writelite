namespace WriteLite.Language.Russian;

/// <summary>
/// Combines the signals that separate a correct Russian suggestion from a
/// merely-close one.
/// </summary>
/// <remarks>
/// Every term is a multiplier on a base derived from edit distance, so no single
/// signal can carry a bad candidate: a very frequent word that is two edits away
/// and shares no first letter still loses to a one-edit neighbour that matches a
/// known Russian confusion pattern. The absolute value matters, not just the
/// order — <see cref="RussianCorrectionConfidence"/> turns it into the
/// automatic/manual decision.
/// </remarks>
public static class RussianCandidateScorer
{
    // Vowels Russians actually confuse: unstressed о/а and е/и/я neutralise in
    // speech, which is where "севодня" and "интиресный" come from.
    private static readonly (char A, char B)[] VowelConfusions =
    [
        ('о', 'а'), ('е', 'и'), ('е', 'я'), ('и', 'я'), ('о', 'ы'), ('у', 'о'), ('э', 'е'),
    ];

    // Voiced/voiceless pairs, which neutralise at word end and before consonants
    // ("сдесь", "зделать", "фторник").
    private static readonly (char A, char B)[] ConsonantConfusions =
    [
        ('б', 'п'), ('в', 'ф'), ('г', 'к'), ('д', 'т'), ('ж', 'ш'), ('з', 'с'),
        ('с', 'ц'), ('ч', 'щ'), ('ш', 'щ'), ('г', 'х'),
    ];

    /// <param name="original">
    /// The typed word, lowercased. Every factor derived from it folds case anyway.
    /// </param>
    /// <param name="typedCapitalised">
    /// Whether the word as the user typed it began with a capital.
    /// </param>
    /// <remarks>
    /// <paramref name="typedCapitalised"/> is separate because <paramref name="original"/>
    /// arrives lowercased, and <see cref="RegisterFactor"/> needs the real capitalisation.
    /// Without it the proper-name penalty fired on every candidate unconditionally: measured
    /// on the frozen corpus, «Шолохава» scored «шолохова» at 0.264 and «Гагарен» scored
    /// «гагарин» at 0.287, losing to unrelated common nouns, because a capitalised surname
    /// was being treated as an attempt to rewrite a lowercase word into a name.
    /// </remarks>
    public static double Score(
        string original,
        string candidate,
        int distance,
        RussianCandidateOrigin origin,
        RussianFormIndex.FormInfo info,
        bool technicalTerm = false,
        bool typedCapitalised = false)
    {
        if (string.IsNullOrEmpty(candidate)) return 0.0;

        // A layout or homoglyph rewrite is not an edit-distance guess: the
        // characters the user pressed map one-to-one onto a real word, which is
        // about as certain as spelling correction ever gets.
        var score = origin switch
        {
            RussianCandidateOrigin.KeyboardLayout => 0.96,
            RussianCandidateOrigin.KeyboardLayoutLatin => LatinLayoutBase(candidate, technicalTerm),
            RussianCandidateOrigin.Homoglyph => 0.94,
            RussianCandidateOrigin.WordSplit => 0.72,
            _ => distance <= 1 ? 0.62 : 0.34,
        };

        if (origin is RussianCandidateOrigin.EditDistance)
        {
            score *= FirstLetterFactor(original, candidate);
            score *= LengthFactor(original, candidate);
            score *= 1.0 + 0.22 * RussianKeyboard.AdjacencyScore(original, candidate);
            score *= ConfusionFactor(original, candidate);
        }

        // The Russian form index has nothing to say about an English word, and asking it
        // would apply a "not in the frequency list" penalty to every one of them.
        if (origin is not RussianCandidateOrigin.KeyboardLayoutLatin)
        {
            score *= FrequencyFactor(info);

            // A homoglyph or layout rewrite is exempt from the proper-name penalty. That
            // penalty guards against a guess that turns an ordinary word into a name; these
            // two origins are not guesses. The characters the user pressed map one-to-one
            // onto the candidate, so «мoре» with a Latin o is «море» whether or not «море»
            // also happens to be a surname — and being treated as a guess cost it a factor
            // of three and the top slot.
            var isCharacterLevelRepair = origin
                is RussianCandidateOrigin.Homoglyph or RussianCandidateOrigin.KeyboardLayout;
            score *= RegisterFactor(typedCapitalised || isCharacterLevelRepair, info);
        }

        return Math.Clamp(score, 0.0, 0.999);
    }

    /// <summary>
    /// How far to trust an English word recovered from Cyrillic keystrokes.
    /// </summary>
    /// <remarks>
    /// Deliberately below the Russian-target layout score of 0.96, because the two are not
    /// equally safe. Latin keystrokes that map onto a Russian word are almost always a layout
    /// slip — nobody types "ghbdtn" on purpose. Cyrillic keystrokes that map onto an English
    /// word can also be a Russian typo that happens to collide, so the evidence has to be
    /// stronger before it wins:
    ///
    /// <list type="bullet">
    /// <item>Technical and product vocabulary scores highest. Someone writing Russian prose
    /// about "github" is a documented WriteLite use case; someone who meant an obscure
    /// English noun is not.</item>
    /// <item>Longer words score higher, because a long Cyrillic string whose layout image is
    /// English by coincidence is vanishingly unlikely, while a three-letter one is not.</item>
    /// </list>
    ///
    /// Even at its highest this stays under the automatic-replacement threshold's margin
    /// requirement unless nothing else was found, so a Russian candidate that is genuinely
    /// close still competes.
    /// </remarks>
    private static double LatinLayoutBase(string candidate, bool technicalTerm)
    {
        var length = candidate.Length;
        if (technicalTerm)
        {
            return length >= 4 ? 0.94 : 0.88;
        }

        return length switch
        {
            >= 6 => 0.90,
            >= 4 => 0.84,
            _ => 0.74,
        };
    }

    /// <summary>
    /// Typists rarely get the first letter wrong, so a candidate that changes it
    /// is usually the ranker reaching. The exception is a first letter that is
    /// itself a known confusion: "зделать" and "сдесь" both start with the
    /// wrong voicing, and penalising them as hard as an arbitrary substitution
    /// hands the suggestion to whatever real word merely inserts a letter.
    /// </summary>
    private static double FirstLetterFactor(string original, string candidate)
    {
        var a = RussianFormIndex.Fold(original[0]);
        var b = RussianFormIndex.Fold(candidate[0]);
        if (a == b) return 1.0;
        if (IsPair(ConsonantConfusions, a, b) || IsPair(VowelConfusions, a, b)) return 0.95;
        return 0.55;
    }

    private static double LengthFactor(string original, string candidate)
    {
        var delta = Math.Abs(original.Length - candidate.Length);
        return delta switch { 0 => 1.0, 1 => 0.97, 2 => 0.85, _ => 0.6 };
    }

    /// <summary>
    /// Boosts candidates whose difference from the typed word is one of the
    /// mistakes Russian writers demonstrably make, rather than an arbitrary edit.
    /// </summary>
    private static double ConfusionFactor(string original, string candidate)
    {
        if (Math.Abs(original.Length - candidate.Length) > 1) return 1.0;

        var boost = 1.0;
        if (original.Length == candidate.Length)
        {
            for (var i = 0; i < original.Length; i++)
            {
                var a = RussianFormIndex.Fold(original[i]);
                var b = RussianFormIndex.Fold(candidate[i]);
                if (a == b) continue;
                if (IsPair(VowelConfusions, a, b)) boost *= 1.35;
                else if (IsPair(ConsonantConfusions, a, b)) boost *= 1.3;
            }
        }

        // тся/ться and doubled consonants are the two orthographic classes that
        // dominate Russian error corpora.
        if (DiffersByReflexiveSoftSign(original, candidate)) boost *= 1.4;
        if (DiffersByDoubledConsonant(original, candidate)) boost *= 1.35;

        return Math.Min(boost, 1.8);
    }

    private static bool IsPair((char A, char B)[] pairs, char a, char b)
    {
        foreach (var (x, y) in pairs)
        {
            if ((a == x && b == y) || (a == y && b == x)) return true;
        }

        return false;
    }

    private static bool DiffersByReflexiveSoftSign(string original, string candidate)
    {
        var (shorter, longer) = original.Length < candidate.Length ? (original, candidate) : (candidate, original);
        if (longer.Length - shorter.Length != 1) return false;
        return shorter.EndsWith("тся", StringComparison.Ordinal)
            && longer.EndsWith("ться", StringComparison.Ordinal)
            && shorter[..^3] == longer[..^4];
    }

    private static bool DiffersByDoubledConsonant(string original, string candidate)
    {
        var (shorter, longer) = original.Length < candidate.Length ? (original, candidate) : (candidate, original);
        if (longer.Length - shorter.Length != 1) return false;
        for (var i = 0; i < longer.Length - 1; i++)
        {
            if (longer[i] != longer[i + 1]) continue;
            if (string.CompareOrdinal(longer.Remove(i, 1), shorter) == 0) return true;
        }

        return false;
    }

    /// <summary>
    /// Frequency breaks ties, but must not decide them: a very common word is
    /// preferred over an obscure one at the same distance, never over a closer
    /// match. Rank 0 (absent from the frequency list) is treated as uncommon
    /// rather than unknown-and-therefore-bad.
    /// </summary>
    private static double FrequencyFactor(RussianFormIndex.FormInfo info)
    {
        if (!info.HasFrequency) return 0.9;
        var rank = info.FrequencyRank;
        if (rank <= 1000) return 1.25;
        if (rank <= 10000) return 1.15;
        if (rank <= 50000) return 1.05;
        return 0.98;
    }

    /// <summary>
    /// Never quietly rewrite an ordinary word into a proper name, an
    /// abbreviation, an archaism or an obscenity — the classic way a
    /// spellchecker produces an embarrassing "correction".
    /// </summary>
    /// <remarks>
    /// The proper-name penalty is conditional on how the user typed the word, and that
    /// condition is the whole point of it: rewriting «мере» into «Море» is the embarrassing
    /// case, while rewriting «Шолохава» into «Шолохова» is the correction someone actually
    /// wants. Feeding it a lowercased word collapsed the two and penalised both.
    /// </remarks>
    private static double RegisterFactor(bool typedCapitalised, RussianFormIndex.FormInfo info)
    {
        var factor = 1.0;
        var flags = info.Flags;
        if (flags.HasFlag(RussianFormFlags.ProperName) && !typedCapitalised) factor *= 0.35;
        if (flags.HasFlag(RussianFormFlags.Abbreviation)) factor *= 0.5;
        if (flags.HasFlag(RussianFormFlags.Archaic)) factor *= 0.45;
        if (flags.HasFlag(RussianFormFlags.Obscene)) factor *= 0.2;
        if (flags.HasFlag(RussianFormFlags.NonCyrillic)) factor *= 0.7;
        return factor;
    }
}

/// <summary>
/// Turns a ranked candidate list into the automatic-versus-manual decision.
/// </summary>
/// <remarks>
/// The two modes share all the intelligence and differ only here. Automatic
/// replacement is held to a deliberately harsh standard: a wrong silent edit to
/// text the user already wrote is worse than a missed correction, so it needs
/// both a high absolute score and a clear margin over the runner-up.
/// </remarks>
public static class RussianCorrectionConfidence
{
    public const double AutomaticThreshold = 0.80;
    public const double AutomaticMargin = 0.18;
    public const double SuggestionThreshold = 0.28;

    public static bool QualifiesForAutomaticReplacement(IReadOnlyList<RussianCandidate> ranked)
    {
        if (ranked.Count == 0) return false;
        var best = ranked[0];
        if (best.Score < AutomaticThreshold) return false;
        if (best.Info.Flags.HasFlag(RussianFormFlags.Obscene)) return false;
        if (ranked.Count == 1) return true;
        return best.Score - ranked[1].Score >= AutomaticMargin;
    }

    public static IReadOnlyList<RussianCandidate> Suggestions(IReadOnlyList<RussianCandidate> ranked, int take = 5)
        => ranked.Where(c => c.Score >= SuggestionThreshold).Take(take).ToArray();
}
