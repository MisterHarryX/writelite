using WriteLite.Models;

namespace WriteLite.Services;

/// <summary>
/// Why a document changed. Analysis scheduling reads this; nothing else should.
/// </summary>
public enum TextEditOrigin
{
    /// <summary>A person typing. Short debounce, incremental range.</summary>
    UserTyping,

    /// <summary>A large insertion in one event. Full pass, but still off the dispatcher.</summary>
    Paste,

    /// <summary>One correction the user clicked. Localized revalidation.</summary>
    CorrectionApply,

    /// <summary>Several corrections in one transaction. One localized revalidation for all of them.</summary>
    SafeBatchApply,

    /// <summary>Undo or redo. Full pass: the delta is not knowable from the event.</summary>
    Undo,

    /// <summary>A rewrite or Smart Action. Full pass.</summary>
    SmartAction,

    /// <summary>Loading or restoring a document. Full pass.</summary>
    ProgrammaticRestore,
}

/// <summary>
/// The corrections this document has already accepted, so a slow lane cannot offer one back.
/// </summary>
/// <remarks>
/// <para><b>The bug this closes.</b> A correction is published, the user applies it, and the
/// same correction appears again a moment later. The mechanism is timing, not logic: the deep
/// lane started against revision <i>n</i>, the apply produced revision <i>n+1</i>, and the
/// lane's answer — computed honestly against text that no longer exists — arrived afterwards
/// carrying the finding the user had just consumed. A revision guard on publication catches
/// the common case; it does not catch a lane that recomputes an identical finding from a
/// segment that happens to still match.</para>
///
/// <para><b>What is recorded.</b> The rule, the exact original text and the exact replacement —
/// not the offset, which is meaningless across a revision. A finding is suppressed only when
/// all three match something already applied <i>and</i> the text at its span is still the
/// original. Once the user types over that region, or genuinely reintroduces the same mistake
/// somewhere else, nothing here suppresses anything: the entry is scoped to the applied span
/// and released the moment its text stops being what was applied over.</para>
///
/// <para><b>Why it is bounded.</b> Entries are dropped when their span no longer holds the
/// original text, and the ledger is cleared outright when a document is loaded or replaced.
/// A long editing session cannot grow it without bound, because each entry needs its own
/// stale span to survive.</para>
/// </remarks>
public sealed class AppliedCorrectionLedger
{
    private readonly record struct Entry(string RuleId, string Original, string? Replacement, int Revision);

    private readonly List<Entry> _entries = [];
    private readonly object _gate = new();

    /// <summary>Corrections currently remembered. Diagnostics and tests only.</summary>
    public int Count
    {
        get { lock (_gate) return _entries.Count; }
    }

    /// <summary>Records a correction the user accepted at <paramref name="revision"/>.</summary>
    public void Record(TextIssue issue, int revision)
    {
        if (issue is null) return;

        lock (_gate)
        {
            var entry = new Entry(issue.RuleId, issue.Original, issue.Replacement, revision);
            if (!_entries.Contains(entry))
            {
                _entries.Add(entry);
            }
        }
    }

    /// <summary>Forgets everything. Call when the document is replaced, not when it is edited.</summary>
    public void Clear()
    {
        lock (_gate) _entries.Clear();
    }

    /// <summary>
    /// True when <paramref name="issue"/> is a correction already applied at or before
    /// <paramref name="revision"/> and therefore must not be offered again.
    /// </summary>
    /// <remarks>
    /// The revision comparison is what keeps an honest re-detection alive: a finding produced
    /// by a lane that started <i>after</i> the apply describes text the user has seen since,
    /// and if it still says the same thing then the correction genuinely did not take. Only a
    /// lane that started at or before the applying revision is silenced.
    /// </remarks>
    public bool IsConsumed(TextIssue issue, int revision)
    {
        if (issue is null) return false;

        lock (_gate)
        {
            foreach (var entry in _entries)
            {
                if (entry.Revision >= revision
                    && string.Equals(entry.RuleId, issue.RuleId, StringComparison.Ordinal)
                    && string.Equals(entry.Original, issue.Original, StringComparison.Ordinal)
                    && string.Equals(entry.Replacement, issue.Replacement, StringComparison.Ordinal))
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// Drops entries whose original text no longer occurs in <paramref name="text"/>: the
    /// user has moved past them and re-typing the same mistake deserves the same correction.
    /// </summary>
    public void Prune(string text)
    {
        text ??= string.Empty;
        lock (_gate)
        {
            _entries.RemoveAll(entry =>
                entry.Original.Length > 0 && !text.Contains(entry.Original, StringComparison.Ordinal));
        }
    }
}
