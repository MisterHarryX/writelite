using System.Threading.Channels;
using WriteLite.Models;
using WriteLite.Services.Settings;

namespace WriteLite.Services.Personal;

/// <summary>
/// The user's personal style library: learns from what they write and answers the
/// «Мой стиль письма» questions — which word comes next, which correction they meant, which
/// spelling they prefer.
/// </summary>
/// <remarks>
/// <para><b>One instance per process.</b> The editor, the field monitor and both analyzer
/// graphs share it, so a word learned in Telegram is predicted in the editor and a typo
/// corrected in the editor is ranked first in Word. Constructed once in <c>App.OnStartup</c>.</para>
///
/// <para><b>Learning never runs on the caller's thread.</b> <see cref="Observe"/> queues the
/// text and returns; a single background reader learns it sentence by sentence, taking the lock
/// once per sentence so a large import cannot hold up a prediction on the dispatcher. Answers
/// (<see cref="Predict"/>, <see cref="RankSuggestions"/>) take the same lock for microseconds.</para>
///
/// <para><b>Saving is coalesced.</b> The library is written a few seconds after the last change
/// and on <see cref="Dispose"/>, through <c>AtomicJsonFile</c>, so a crash costs at most the
/// last few sentences and never the library.</para>
///
/// <para><b>Two switches, deliberately separate.</b> Learning is whether WriteLite watches what
/// the user writes; the mode is whether what it learned changes what it suggests. A user can
/// keep teaching it with the mode off, or freeze what it knows with learning off.</para>
/// </remarks>
public sealed class PersonalStyleService : IDisposable
{
    /// <summary>How long after the last change the library is written.</summary>
    public static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(8);

    /// <summary>Longest text accepted for one observation. Imports are split by the caller.</summary>
    public const int MaxObservedChars = 2_000_000;

    private readonly object _gate = new();

    /// <summary>One writer at a time: the save timer and Flush/Dispose can race.</summary>
    private readonly object _saveGate = new();
    private readonly string? _path;
    private readonly Func<string, bool> _isKnownWord;
    private readonly Func<DateTime> _clock;
    private readonly Channel<Observation> _queue = Channel.CreateUnbounded<Observation>(new UnboundedChannelOptions { SingleReader = true });
    private readonly Task _reader;
    private readonly System.Threading.Timer _saveTimer;
    private PersonalStyleModel _model;
    private long _savedRevision;
    private bool _disposed;

    private volatile bool _modeEnabled = true;
    private volatile bool _learningEnabled = true;
    private volatile bool _learnInOtherApps = true;
    private volatile bool _predictionEnabled = true;

    private sealed record Observation(string Text, StyleLearningSource Source, bool IncludeTrailing, TaskCompletionSource<int>? Done);

    /// <param name="path">Library file; null keeps everything in memory (tests).</param>
    /// <param name="isKnownWord">The spelling lexicon. A word it rejects never becomes vocabulary.</param>
    /// <param name="clock">Local time, for recency. Defaults to <see cref="DateTime.Now"/>.</param>
    public PersonalStyleService(string? path, Func<string, bool> isKnownWord, Func<DateTime>? clock = null)
    {
        _path = path;
        _isKnownWord = isKnownWord;
        _clock = clock ?? (() => DateTime.Now);
        _model = path is null ? new PersonalStyleModel() : SafeLoad(path);
        _savedRevision = _model.Revision;
        _saveTimer = new System.Threading.Timer(_ => SaveNow(), null, Timeout.Infinite, Timeout.Infinite);
        _reader = Task.Run(ReadQueueAsync);
    }

    /// <summary>An in-memory library for tests.</summary>
    public static PersonalStyleService CreateInMemory(Func<string, bool>? isKnownWord = null, Func<DateTime>? clock = null)
        => new(null, isKnownWord ?? (_ => true), clock);

    /// <summary>Raised after the library changed, at most once per learning batch. Any thread.</summary>
    public event EventHandler? Changed;

    /// <summary>Whether «Мой стиль письма» changes suggestions.</summary>
    public bool IsModeEnabled => _modeEnabled;

    /// <summary>Whether word prediction is on (it also requires the mode).</summary>
    public bool IsPredictionEnabled => _modeEnabled && _predictionEnabled;

    public bool IsLearningEnabled => _learningEnabled;

    public bool LearnsInOtherApps => _learningEnabled && _learnInOtherApps;

    public void ApplySettings(WriteLiteAppSettings settings)
    {
        _modeEnabled = settings.PersonalStyleEnabled;
        _learningEnabled = settings.PersonalStyleLearning;
        _learnInOtherApps = settings.PersonalStyleLearnInOtherApps;
        _predictionEnabled = settings.PersonalWordPrediction;
    }

    /// <summary>Distinct words in the library. Cheap; for status lines.</summary>
    public int DistinctWords
    {
        get { lock (_gate) return _model.DistinctWords; }
    }

    // ── Learning ──────────────────────────────────────────────────────────────

    /// <summary>
    /// Queues a text the user wrote for learning. Returns immediately.
    /// </summary>
    /// <param name="includeTrailingSentence">False while the user may still be typing the last sentence.</param>
    /// <remarks>
    /// Ignored when learning is off, and for <see cref="StyleLearningSource.OtherApp"/> when
    /// learning in other applications is off. Calibration and import are explicit acts and are
    /// accepted whenever learning is on.
    /// </remarks>
    public void Observe(string text, StyleLearningSource source, bool includeTrailingSentence = false)
    {
        if (!Accepts(source, text)) return;
        _queue.Writer.TryWrite(new Observation(Clip(text), source, includeTrailingSentence, null));
    }

    /// <summary>Learns a text and completes when it has been learned. Returns the words learned.</summary>
    public Task<int> ObserveAsync(string text, StyleLearningSource source, bool includeTrailingSentence = true)
    {
        if (!Accepts(source, text)) return Task.FromResult(0);

        var done = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_queue.Writer.TryWrite(new Observation(Clip(text), source, includeTrailingSentence, done)))
        {
            return Task.FromResult(0);
        }

        return done.Task;
    }

    /// <summary>Learns a text on the calling thread. Tests and tools.</summary>
    public int LearnNow(string text, StyleLearningSource source = StyleLearningSource.Editor, bool includeTrailingSentence = true)
    {
        if (!Accepts(source, text)) return 0;
        var learned = LearnCore(Clip(text), includeTrailingSentence);
        if (learned > 0) OnChanged();
        return learned;
    }

    /// <summary>Remembers a spelling correction the user accepted.</summary>
    public void RecordCorrection(string typed, string intended)
    {
        if (!_learningEnabled || string.IsNullOrWhiteSpace(typed) || string.IsNullOrWhiteSpace(intended)) return;

        // Only a correction to a real word teaches anything; a layout fix or a split into two
        // words is a different kind of mistake and is not a habit of spelling.
        if (intended.Contains(' ') || !_isKnownWord(intended.Trim())) return;

        bool changed;
        lock (_gate) changed = _model.RecordCorrection(typed, intended);
        if (changed) OnChanged();
    }

    /// <summary>
    /// Learns from a finding the user applied, if it is the kind that says something about
    /// their spelling.
    /// </summary>
    /// <remarks>
    /// Spelling only, and not a keyboard-layout fix: «руддщ» → «hello» is the wrong layout, not
    /// a habit of spelling, and remembering it would rank an English word first for a Russian
    /// typo. Both correction surfaces — the editor and the field card — report through here.
    /// </remarks>
    public void RecordAcceptedCorrection(TextIssue issue)
    {
        if (issue.Category != IssueCategory.Orthography || string.IsNullOrWhiteSpace(issue.Replacement)) return;
        if (issue.RuleId.Contains("keyboard-layout", StringComparison.Ordinal)) return;

        RecordCorrection(issue.Original, issue.Replacement);
    }

    /// <summary>Records that the user accepted a predicted word.</summary>
    public void RecordPredictionAccepted()
    {
        lock (_gate) _model.RecordPredictionAccepted();
        OnChanged();
    }

    /// <summary>Counts one finished calibration text.</summary>
    public void RecordCalibrationText()
    {
        lock (_gate) _model.RecordCalibrationText();
        OnChanged();
    }

    public void ForgetCorrection(string typed, string intended)
    {
        bool changed;
        lock (_gate) changed = _model.ForgetCorrection(typed, intended);
        if (changed) OnChanged();
    }

    public void ForgetUnknown(string word)
    {
        bool changed;
        lock (_gate) changed = _model.ForgetUnknown(word);
        if (changed) OnChanged();
    }

    /// <summary>Forgets everything the library has learned, on disk too.</summary>
    public void Reset()
    {
        lock (_gate) _model.Reset();
        OnChanged();
        SaveNow();
    }

    // ── Answers ───────────────────────────────────────────────────────────────

    /// <summary>The word the user is probably typing, or null. Null whenever prediction is off.</summary>
    public WordPrediction? Predict(string textBeforeCaret)
    {
        if (!IsPredictionEnabled || string.IsNullOrEmpty(textBeforeCaret)) return null;

        // Only the tail matters, and the tail is all that should be scanned on a keystroke.
        var tail = textBeforeCaret.Length <= 400 ? textBeforeCaret : textBeforeCaret[^400..];
        lock (_gate) return _model.Predict(tail);
    }

    /// <summary>A spelling checker's candidates, reordered by this user's history. Unchanged when the mode is off.</summary>
    public SuggestionRanking RankSuggestions(string typed, IReadOnlyList<string> suggestions)
    {
        if (!_modeEnabled || string.IsNullOrEmpty(typed)) return SuggestionRanking.Unchanged(suggestions);
        lock (_gate) return _model.Rank(typed, suggestions, _isKnownWord);
    }

    /// <summary>
    /// «Мой стиль» findings: places where the text departs from a spelling this user has
    /// demonstrably chosen. Empty when the mode is off.
    /// </summary>
    public IReadOnlyList<TextIssue> FindStyleIssues(string text)
    {
        if (!_modeEnabled || string.IsNullOrWhiteSpace(text)) return [];

        IReadOnlyList<YoPreference> preferences;
        lock (_gate) preferences = _model.FindYoPreferences(text);

        return preferences
            .Select(p => new TextIssue(
                p.Start,
                p.Length,
                p.Written,
                p.Preferred,
                $"В вашем стиле — «{p.Preferred}»",
                $"Вы обычно пишете это слово через «ё» ({p.YoCount} из {p.YoCount + p.ECount}). "
                + "Буква «е» здесь не ошибка — это подсказка режима «Мой стиль письма».",
                IssueCategory.Style,
                IssueSeverity.Suggestion,
                CanApplyAutomatically: false,
                RuleId: PersonalStyleRuleIds.Yo,
                LinguisticCategory: LinguisticIssueCategory.UncheckedVowel,
                // Above IssueConfidencePolicy.StyleThreshold (0.84), or the rendering pipeline
                // drops it from the orchestrated lane and it flickers out after the fast one.
                // The evidence earns it: three «ё» spellings and nine in ten of the total.
                Confidence: 0.85))
            .ToArray();
    }

    public PersonalStyleSnapshot Snapshot()
    {
        lock (_gate) return _model.Snapshot();
    }

    // ── Persistence ───────────────────────────────────────────────────────────

    /// <summary>Writes the library now if anything changed. Safe from any thread.</summary>
    public void Flush() => SaveNow();

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _queue.Writer.TryComplete();
        try
        {
            // Whatever was queued before exit is the user's last sentences; give the reader a
            // moment to take them in, but never hold shutdown hostage to it.
            _reader.Wait(TimeSpan.FromSeconds(2));
        }
        catch (AggregateException)
        {
            // The reader logs its own failures.
        }

        _saveTimer.Dispose();
        SaveNow();
    }

    // ── Internals ─────────────────────────────────────────────────────────────

    private bool Accepts(StyleLearningSource source, string? text)
    {
        if (_disposed || !_learningEnabled || string.IsNullOrWhiteSpace(text)) return false;
        return source != StyleLearningSource.OtherApp || _learnInOtherApps;
    }

    private static string Clip(string text) => text.Length <= MaxObservedChars ? text : text[..MaxObservedChars];

    private async Task ReadQueueAsync()
    {
        await foreach (var observation in _queue.Reader.ReadAllAsync().ConfigureAwait(false))
        {
            var learned = 0;
            try
            {
                learned = LearnCore(observation.Text, observation.IncludeTrailing);
            }
            catch (Exception exception)
            {
                CompatibilityLogger.Technical("personal-style-learn-failed", exception);
            }

            if (learned > 0)
            {
                CompatibilityLogger.Technical("personal-style-learned", $"source={observation.Source} words={learned}");
                OnChanged();
            }

            observation.Done?.TrySetResult(learned);
        }
    }

    private int LearnCore(string text, bool includeTrailing)
    {
        var day = (int)(_clock().Date - DateTime.UnixEpoch.Date).TotalDays;
        var learned = 0;

        // The lexicon is consulted outside the lock: it is the slow part, and a prediction on
        // the dispatcher must never wait behind a Hunspell lookup.
        foreach (var sentence in PersonalStyleModel.SentencesToLearn(text, includeTrailing))
        {
            var known = new Dictionary<string, bool>(StringComparer.Ordinal);
            foreach (var token in PersonalStyleModel.Tokenize(sentence))
            {
                if (!known.ContainsKey(token)) known[token] = SafeIsKnown(token);
            }

            lock (_gate)
            {
                learned += _model.LearnSentence(sentence, token => known.TryGetValue(token, out var k) && k, day);
            }
        }

        return learned;
    }

    private bool SafeIsKnown(string token)
    {
        try
        {
            return _isKnownWord(token);
        }
        catch (Exception)
        {
            // A lexicon that cannot answer is treated as "not known": the word is then counted
            // as unknown, which is the conservative side — it is never predicted.
            return false;
        }
    }

    private void OnChanged()
    {
        if (_path is not null && !_disposed)
        {
            try
            {
                _saveTimer.Change(SaveDelay, Timeout.InfiniteTimeSpan);
            }
            catch (ObjectDisposedException)
            {
                // Shutting down; Dispose saves.
            }
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private void SaveNow()
    {
        if (_path is null) return;

        // The whole save is one critical section: two saves racing could otherwise write an
        // older snapshot over a newer one and still record the newer revision as saved. The
        // model lock is held only for the copy, so learning and prediction never wait on disk.
        lock (_saveGate)
        {
            PersonalStyleDocument document;
            long revision;
            lock (_gate)
            {
                revision = _model.Revision;
                if (revision == _savedRevision) return;
                document = PersonalStyleDocument.From(_model);
            }

            try
            {
                Storage.AtomicJsonFile.WriteSerialized(_path, document.Serialize());
                lock (_gate) _savedRevision = revision;
            }
            catch (Exception exception)
            {
                CompatibilityLogger.Technical("personal-style-save-failed", $"type={exception.GetType().Name}");
            }
        }
    }

    private static PersonalStyleModel SafeLoad(string path)
    {
        try
        {
            return PersonalStyleDocument.Load(path);
        }
        catch (Exception exception)
        {
            // A damaged library must not stop the application; the backup is tried inside Load.
            CompatibilityLogger.Technical("personal-style-load-failed", $"type={exception.GetType().Name}");
            return new PersonalStyleModel();
        }
    }
}

/// <summary>Rule identifiers of «Мой стиль» findings.</summary>
public static class PersonalStyleRuleIds
{
    public const string Yo = "personal.style.yo";
}
