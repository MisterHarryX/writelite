using System.Globalization;
using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WriteLite.Services.Settings;
using Color = System.Windows.Media.Color;

namespace WriteLite.Services;

/// <summary>
/// Единственный источник инженерных констант и пределов WriteLite.
/// Все значения читаются из внедрённого <c>engine-config.json</c> (лежит рядом с этим
/// файлом) — правки конфигурации, таймингов, лимитов и порогов делаются в одном месте,
/// без перекомпиляции значений по всему коду.
///
/// Пользовательские настройки (то, что пользователь может менять) живут отдельно, в
/// <see cref="WriteLiteAppSettings"/>/<c>settings.json</c>. Здесь — только те константы,
/// которые в прошлом были «магическими числами», разбросанными по сервисам.
/// </summary>
public static class WriteLiteDefaults
{
    // ── Секции конфига ────────────────────────────────────────────────────
    public static NetworkingConfig Networking { get; } = new();
    public static AnalysisConfig Analysis { get; } = new();
    public static DebounceConfig Debounce { get; } = new();
    public static MemoryConfig Memory { get; } = new();
    public static ModelConfig Model { get; } = new();
    public static TextLimitsConfig TextLimits { get; } = new();
    public static UiConfig Ui { get; } = new();
    public static SmartPlacementScoringConfig SmartPlacementScoring { get; } = new();
    public static MonitorConfig Monitor { get; } = new();
    public static UiStateMachineConfig UiStateMachine { get; } = new();
    public static MotionConfig Motion { get; } = new();
    public static UnderlineThemeConfig UnderlineTheme { get; } = new();
    public static GrammarConfig Grammar { get; } = new();
    public static ScoringConfig Scoring { get; } = new();
    public static ApplicationConfig Application { get; } = new();
    public static AudioConfig Audio { get; } = new();

    // ── Загрузка embedded ресурса ─────────────────────────────────────────
    internal static ConfigRoot Root { get; } = Load();

    private static ConfigRoot Load()
    {
        try
        {
            var asm = typeof(WriteLiteDefaults).Assembly;
            using var stream = asm.GetManifestResourceStream("WriteLite.Services.engine-config.json")
                               ?? throw new InvalidOperationException("Embedded engine-config.json missing.");
            using var reader = new StreamReader(stream);
            var json = reader.ReadToEnd();
            return JsonSerializer.Deserialize<ConfigRoot>(json, JsonOptions)
                   ?? throw new InvalidOperationException("Failed to parse engine-config.json.");
        }
        catch (Exception ex)
        {
            // Конфиг — инженерная деталь; при его отсутствии падать нельзя, но и работать
            // молча с неверными значениями тоже. Аварийный выход с осмысленной схемой —
            // единственный честный вариант: без конфига продукт не поддерживается.
            throw new InvalidOperationException("WriteLiteDefaults: не удалось загрузить engine-config.json.", ex);
        }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true
    };

    // ── Типобезопасные обёртки для TimeSpan/типизации JSON ──────────────

    public static TimeSpan Seconds(int s) => TimeSpan.FromSeconds(s);
    public static TimeSpan Milliseconds(int ms) => TimeSpan.FromMilliseconds(ms);

    /// <summary>Разбирает цвет вида "#AARRGGBB" из конфига в WPF Color.</summary>
    public static Color ParseColor(string hex)
    {
        var s = hex.TrimStart('#');
        if (s.Length != 8)
        {
            throw new FormatException($"engine-config: неверный цвет '{hex}', ожидается #AARRGGBB.");
        }

        return Color.FromArgb(
            byte.Parse(s.Substring(0, 2), NumberStyles.HexNumber),
            byte.Parse(s.Substring(2, 2), NumberStyles.HexNumber),
            byte.Parse(s.Substring(4, 2), NumberStyles.HexNumber),
            byte.Parse(s.Substring(6, 2), NumberStyles.HexNumber));
    }

    /// <summary>Все значения по ключу, для отладки/диагностики.</summary>
    public static string Describe()
        => JsonSerializer.Serialize(Root, new JsonSerializerOptions { WriteIndented = true });
}

// NOTE: классы ниже отражают структуру engine-config.json. Поля с типом long/int/double
// берутся как есть; TimeSpan-значения (секунды/мс) представлены числами в JSON, а удобные
// обёртки публикуются свойствами типа TimeSpan.

public sealed class NetworkingConfig
{
    private static ConfigRoot D => WriteLiteDefaults.Root;

    public int LanguageEnginePreferredPort => D.Networking.LanguageEnginePreferredPort;
    public int LanguageEnginePortSearchRange => D.Networking.LanguageEnginePortSearchRange;
    public string LanguageEngineHostAddress => D.Networking.LanguageEngineHostAddress;
    public TimeSpan LanguageEngineStartupTimeout => WriteLiteDefaults.Seconds(D.Networking.LanguageEngineStartupTimeoutSeconds);
    public TimeSpan LanguageEngineRequestTimeout => WriteLiteDefaults.Seconds(D.Networking.LanguageEngineRequestTimeoutSeconds);
    public TimeSpan LanguageEngineShutdownTimeout => WriteLiteDefaults.Seconds(D.Networking.LanguageEngineShutdownTimeoutSeconds);
    public TimeSpan LanguageEngineReadyPollInterval => WriteLiteDefaults.Milliseconds(D.Networking.LanguageEngineReadyPollIntervalMs);
    public TimeSpan LanguageEngineRestartPollInterval => WriteLiteDefaults.Milliseconds(D.Networking.LanguageEngineRestartPollIntervalMs);
    public TimeSpan LanguageEngineRecoveryBaseDelay => WriteLiteDefaults.Seconds(D.Networking.LanguageEngineRecoveryBaseDelaySeconds);
    public TimeSpan LanguageEnginePortBindSettle => WriteLiteDefaults.Milliseconds(D.Networking.LanguageEnginePortBindSettleMs);
    public TimeSpan LanguageEngineReadinessProbeTimeout => WriteLiteDefaults.Seconds(D.Networking.LanguageEngineReadinessProbeTimeoutSeconds);
    public int EngineMaxStartAttempts => D.Networking.EngineMaxStartAttempts;
    public int EngineAutoStartMaxAttempts => D.Networking.EngineAutoStartMaxAttempts;
    public int EngineAutoStartFailureLimit => D.Networking.EngineAutoStartFailureLimit;
    public TimeSpan EngineRecoveryBackoffBase => WriteLiteDefaults.Seconds(D.Networking.EngineRecoveryBackoffBaseSeconds);
    public int EngineRecoveryBackoffFactor => D.Networking.EngineRecoveryBackoffFactor;
    public TimeSpan EngineRecoveryBackoffMax => WriteLiteDefaults.Seconds(D.Networking.EngineRecoveryBackoffMaxSeconds);
    public int LanguageEngineMaxTextLength => D.Networking.LanguageEngineMaxTextLength;
    public string LanguageEngineJavaMaxHeap => D.Networking.LanguageEngineJavaMaxHeap;
    public string LanguageEngineRuntimeRelativeDir => D.Networking.LanguageEngineRuntimeRelativeDir;
}

public sealed class AnalysisConfig
{
    private static ConfigRoot D => WriteLiteDefaults.Root;

    public int MaxTextLengthDefault => D.Analysis.MaxTextLengthDefault;
    public int EditingIdleGraceSecondsDefault => D.Analysis.EditingIdleGraceSecondsDefault;
    public TimeSpan TextFieldPollInterval => WriteLiteDefaults.Milliseconds(D.Analysis.TextFieldPollIntervalMs);
    public TimeSpan TextFieldFastDebounce => WriteLiteDefaults.Milliseconds(D.Analysis.TextFieldFastDebounceMs);
    public TimeSpan TextFieldFastDebounceClampMin => WriteLiteDefaults.Milliseconds(D.Analysis.TextFieldFastDebounceClampMinMs);
    public TimeSpan TextFieldFastDebounceClampMax => WriteLiteDefaults.Milliseconds(D.Analysis.TextFieldFastDebounceClampMaxMs);
    public TimeSpan TextFieldDeepDebounceDefault => WriteLiteDefaults.Milliseconds(D.Analysis.TextFieldDeepDebounceDefaultMs);
    public TimeSpan TextFieldDeepDebounceClampMin => WriteLiteDefaults.Milliseconds(D.Analysis.TextFieldDeepDebounceClampMinMs);
    public TimeSpan TextFieldDeepDebounceClampMax => WriteLiteDefaults.Milliseconds(D.Analysis.TextFieldDeepDebounceClampMaxMs);
    public TimeSpan TextFieldCorrectionSuppression => WriteLiteDefaults.Milliseconds(D.Analysis.TextFieldCorrectionSuppressionMs);
    public TimeSpan TextFieldIdleTickInterval => WriteLiteDefaults.Milliseconds(D.Analysis.TextFieldIdleTickIntervalMs);
    public TimeSpan FocusSubscriptionTimeout => WriteLiteDefaults.Seconds(D.Analysis.FocusSubscriptionTimeoutSeconds);
    public TimeSpan PostWriteSettleDelay => WriteLiteDefaults.Milliseconds(D.Analysis.PostWriteSettleDelayMs);
    public int DirtyTextRangeMaxLength => D.Analysis.DirtyTextRangeMaxLength;
    public int LiveAnalysisCharacterCeiling => D.Analysis.LiveAnalysisCharacterCeiling;
    public TimeSpan RegexMatchTimeout => WriteLiteDefaults.Milliseconds(D.Analysis.RegexMatchTimeoutMs);
    public TimeSpan StyleRegexMatchTimeout => WriteLiteDefaults.Milliseconds(D.Analysis.StyleRegexMatchTimeoutMs);
    public TimeSpan UiaResolveTimeout => WriteLiteDefaults.Milliseconds(D.Analysis.UiaResolveTimeoutMs);
    public TimeSpan UiaReadTimeout => WriteLiteDefaults.Milliseconds(D.Analysis.UiaReadTimeoutMs);
    public TimeSpan UiaOperationTimeout => WriteLiteDefaults.Milliseconds(D.Analysis.UiaOperationTimeoutMs);
    public TimeSpan UiaCircuitBreakerCooldown => WriteLiteDefaults.Seconds(D.Analysis.UiaCircuitBreakerCooldownSeconds);
    public int[] ExternalWriteVerifyBackoffMs => D.Analysis.ExternalWriteVerifyBackoffMs;
}

public sealed class DebounceConfig
{
    private static ConfigRoot D => WriteLiteDefaults.Root;

    public int AnalysisDelayMsDefault => D.Debounce.AnalysisDelayMsDefault;
    public int AnalysisDelayMsClampMin => D.Debounce.AnalysisDelayMsClampMin;
    public int AnalysisDelayMsClampMax => D.Debounce.AnalysisDelayMsClampMax;
    public int MaxTextLengthClampMin => D.Debounce.MaxTextLengthClampMin;
    public int MaxTextLengthClampMax => D.Debounce.MaxTextLengthClampMax;
    public int AiDebounceMsDefault => D.Debounce.AiDebounceMsDefault;
    public int AiDebounceMsClampMin => D.Debounce.AiDebounceMsClampMin;
    public int AiDebounceMsClampMax => D.Debounce.AiDebounceMsClampMax;
    public int AiMinTextLengthDefault => D.Debounce.AiMinTextLengthDefault;
    public int AiMinTextLengthClampMin => D.Debounce.AiMinTextLengthClampMin;
    public int AiMinTextLengthClampMax => D.Debounce.AiMinTextLengthClampMax;
    public int AiMaxTextLengthDefault => D.Debounce.AiMaxTextLengthDefault;
    public int AiMaxTextLengthClampMin => D.Debounce.AiMaxTextLengthClampMin;
    public int AiMaxTextLengthClampMax => D.Debounce.AiMaxTextLengthClampMax;
    public int AiDebounceClampMinMs => D.Debounce.AiDebounceClampMinMs;
    public int AiDebounceClampMaxMs => D.Debounce.AiDebounceClampMaxMs;
    public TimeSpan NotesSaveDelay => WriteLiteDefaults.Milliseconds(D.Debounce.NotesSaveDelayMs);
    public TimeSpan ReadingLibraryWriteDelay => WriteLiteDefaults.Milliseconds(D.Debounce.ReadingLibraryWriteDelayMs);
    public TimeSpan DocumentRecoveryInterval => WriteLiteDefaults.Seconds(D.Debounce.DocumentRecoveryIntervalSeconds);
    public TimeSpan ReaderPositionSaveDelay => WriteLiteDefaults.Milliseconds(D.Debounce.ReaderPositionSaveDelayMs);
    public TimeSpan DictionarySkeletonDelay => WriteLiteDefaults.Milliseconds(D.Debounce.DictionarySkeletonDelayMs);
    public TimeSpan EditingWritingDelay => WriteLiteDefaults.Milliseconds(D.Debounce.EditingWritingDelayMs);
    public TimeSpan StudyCardDraftTimeout => WriteLiteDefaults.Seconds(D.Debounce.StudyCardDraftTimeoutSeconds);
    public TimeSpan EditorKeystrokeAnalysisDelay => WriteLiteDefaults.Milliseconds(D.Debounce.EditorKeystrokeAnalysisDelayMs);
    public TimeSpan EditorFullRescanDelay => WriteLiteDefaults.Milliseconds(D.Debounce.EditorFullRescanDelayMs);
    public TimeSpan EditorStructuralRescanDelay => WriteLiteDefaults.Milliseconds(D.Debounce.EditorStructuralRescanDelayMs);
    public TimeSpan EditorPostEditRescanDelay => WriteLiteDefaults.Milliseconds(D.Debounce.EditorPostEditRescanDelayMs);
    public TimeSpan EditorPostRewriteRescanDelay => WriteLiteDefaults.Milliseconds(D.Debounce.EditorPostRewriteRescanDelayMs);
    public TimeSpan DebounceSetClampMin => WriteLiteDefaults.Milliseconds(D.Debounce.DebounceSetClampMinMs);
    public TimeSpan DebounceSetClampMax => WriteLiteDefaults.Milliseconds(D.Debounce.DebounceSetClampMaxMs);
}

public sealed class MemoryConfig
{
    private static ConfigRoot D => WriteLiteDefaults.Root;

    public int OfflineLexicalMaxCacheEntries => D.Memory.OfflineLexicalMaxCacheEntries;
    public int TranslationIndexMaxCacheEntries => D.Memory.TranslationIndexMaxCacheEntries;
    public int SqliteLexicalMaxCacheEntries => D.Memory.SqliteLexicalMaxCacheEntries;
    public int InlineGeometryCacheCapacity => D.Memory.InlineGeometryCacheCapacity;
    public int InputLatencyCapacity => D.Memory.InputLatencyCapacity;
    public int RecentDocumentsMaxEntries => D.Memory.RecentDocumentsMaxEntries;
    public int DocumentFingerprintPrefixBytes => D.Memory.DocumentFingerprintPrefixBytes;
    public int DocumentFingerprintBufferBytes => D.Memory.DocumentFingerprintBufferBytes;
    public int MorphologyCacheLimit => D.Memory.MorphologyCacheLimit;
    public int CandidateEditDistanceOneCap => D.Memory.CandidateEditDistanceOneCap;
    public int CandidateEditDistanceTwoCap => D.Memory.CandidateEditDistanceTwoCap;
    public int MaxAlignableTokens => D.Memory.MaxAlignableTokens;
    public int WordPieceMaxCharsPerWord => D.Memory.WordPieceMaxCharsPerWord;
    public int CandidateJudgeMaxCandidates => D.Memory.CandidateJudgeMaxCandidates;
    public int CandidateJudgeMaxAnswerTokens => D.Memory.CandidateJudgeMaxAnswerTokens;
}

public sealed class ModelConfig
{
    private static ConfigRoot D => WriteLiteDefaults.Root;

    public string QwenDefaultModelVersion => D.Model.QwenDefaultModelVersion;
    public string QwenDefaultEndpoint => D.Model.QwenDefaultEndpoint;
    public int QwenDefaultServerContextTokens => D.Model.QwenDefaultServerContextTokens;
    public int QwenDefaultServerBatchTokens => D.Model.QwenDefaultServerBatchTokens;
    public int QwenDefaultServerMicroBatchTokens => D.Model.QwenDefaultServerMicroBatchTokens;
    public int QwenDefaultServerParallelSlots => D.Model.QwenDefaultServerParallelSlots;
    public long QwenMaximumLocalModelBytes => D.Model.QwenMaximumLocalModelBytes;
    public TimeSpan QwenTimeout => WriteLiteDefaults.Seconds(D.Model.QwenTimeoutSeconds);
    public TimeSpan QwenInstructionTimeout => WriteLiteDefaults.Seconds(D.Model.QwenInstructionTimeoutSeconds);
    public int QwenMaxNewTokens => D.Model.QwenMaxNewTokens;
    public int QwenMaxCompletionTokens => D.Model.QwenMaxCompletionTokens;
    public int QwenWedgeThreshold => D.Model.QwenWedgeThreshold;
    public TimeSpan QwenRecoveryCooldown => WriteLiteDefaults.Seconds(D.Model.QwenRecoveryCooldownSeconds);
    public int QwenMaxRecoveryAttempts => D.Model.QwenMaxRecoveryAttempts;
    public TimeSpan QwenHealthProbeTimeout => WriteLiteDefaults.Seconds(D.Model.QwenHealthProbeTimeoutSeconds);
    public TimeSpan QwenServerStartupPollInterval => WriteLiteDefaults.Milliseconds(D.Model.QwenServerStartupPollMs);
    public int QwenServerStartupPollAttempts => D.Model.QwenServerStartupPollAttempts;
    public TimeSpan QwenProcessKillWait => WriteLiteDefaults.Seconds(D.Model.QwenProcessKillWaitSeconds);
    public int QwenWorkerThreadsMin => D.Model.QwenWorkerThreadsMin;
    public int QwenWorkerThreadsMax => D.Model.QwenWorkerThreadsMax;
    public TimeSpan LocalAiCacheTtl => TimeSpan.FromMinutes(D.Model.LocalAiCacheTtlMinutes);
    public int LocalAiMaxInputChars => D.Model.LocalAiMaxInputChars;
    public int LocalAiMinInputChars => D.Model.LocalAiMinInputChars;
    public double CandidateScorerAutomaticThreshold => D.Model.CandidateScorerAutomaticThreshold;
    public double CandidateScorerAutomaticMargin => D.Model.CandidateScorerAutomaticMargin;
    public double CandidateScorerSuggestionThreshold => D.Model.CandidateScorerSuggestionThreshold;
    public int MorphologyAlternativesMax => D.Model.MorphologyAlternativesMax;
    public int EditDistanceSuggestCap => D.Model.EditDistanceSuggestCap;
    public int EditDistanceFilterCap => D.Model.EditDistanceFilterCap;
    public double RewriteTemperature => D.Model.RewriteTemperature;
    public double RewriteShortenTemperature => D.Model.RewriteShortenTemperature;
    public double WritingCompletionTemperature => D.Model.WritingCompletionTemperature;
}

public sealed class TextLimitsConfig
{
    private static ConfigRoot D => WriteLiteDefaults.Root;

    public int AiMaxTextLengthForEngine => D.TextLimits.AiMaxTextLengthForEngine;
    public int TextRewriteMaxContextChars => D.TextLimits.TextRewriteMaxContextChars;
    public int TextRewriteMaxSelectionChars => D.TextLimits.TextRewriteMaxSelectionChars;
    public int WritingAssistanceContextBudget => D.TextLimits.WritingAssistanceContextBudget;
    public int WritingAssistanceMaxCompletionChars => D.TextLimits.WritingAssistanceMaxCompletionChars;
    public int WritingAssistanceMinimumContextChars => D.TextLimits.WritingAssistanceMinimumContextChars;
    public int RewriteDiffMaxTokens => D.TextLimits.RewriteDiffMaxTokens;
    public int CorrectionCardMaxFragmentLength => D.TextLimits.CorrectionCardMaxFragmentLength;
    public int StudyCardMaxPassage => D.TextLimits.StudyCardMaxPassage;
    public int PunctuationModelMaxSentenceLength => D.TextLimits.PunctuationModelMaxSentenceLength;
    public int AiCallRouterMinChars => D.TextLimits.AiCallRouterMinChars;
    public int AiCallRouterMaxChars => D.TextLimits.AiCallRouterMaxChars;
    public int PromptTextBoxMaxLength => D.TextLimits.PromptTextBoxMaxLength;
    public int DocumentIssueMaxRenderedIssues => D.TextLimits.DocumentIssueMaxRenderedIssues;
    public int ReadingAnchorNearWindow => D.TextLimits.ReadingAnchorNearWindow;
}

public sealed class UiConfig
{
    private static ConfigRoot D => WriteLiteDefaults.Root;

    public double EditorPanelCollapseThresholdPx => D.Ui.EditorPanelCollapseThresholdPx;
    public double EditorCompactToolbarThresholdPx => D.Ui.EditorCompactToolbarThresholdPx;
    public double MainWindowCompactRailThresholdPx => D.Ui.MainWindowCompactRailThresholdPx;
    public double AutoCorrectConfidence => D.Ui.AutoCorrectConfidence;
    public double SafeApplyConfidence => D.Ui.SafeApplyConfidence;
    public double ReaderMarksPanelWidthPx => D.Ui.ReaderMarksPanelWidthPx;
    public double ReaderMinimumTextWidthPx => D.Ui.ReaderMinimumTextWidthPx;
    public double IndentStepPoints => D.Ui.IndentStepPoints;
    public double OverlayPlacementGapPx => D.Ui.OverlayPlacementGapPx;
    public double SmartPlacementGapPx => D.Ui.SmartPlacementGapPx;
    public double SmartPlacementMinEdgeMarginPx => D.Ui.SmartPlacementMinEdgeMarginPx;
    public double SmartPlacementBaseScore => D.Ui.SmartPlacementBaseScore;
    public double SmartPlacementPreferenceRankStep => D.Ui.SmartPlacementPreferenceRankStep;
}

public sealed class SmartPlacementScoringConfig
{
    private static ConfigRoot D => WriteLiteDefaults.Root;

    public double FieldOverlapPenalty => D.SmartPlacementScoring.FieldOverlapPenalty;
    public double FieldDistancePenalty => D.SmartPlacementScoring.FieldDistancePenalty;
    public double AnchorOverlapPenalty => D.SmartPlacementScoring.AnchorOverlapPenalty;
    public double AnchorDistancePenalty => D.SmartPlacementScoring.AnchorDistancePenalty;
    public double CaretIntersectionPenalty => D.SmartPlacementScoring.CaretIntersectionPenalty;
    public double HostOverlapBonus => D.SmartPlacementScoring.HostOverlapBonus;
    public double InsideBottomRightPenalty => D.SmartPlacementScoring.InsideBottomRightPenalty;
    public double OutsideFieldFallbackRankOffset => D.SmartPlacementScoring.OutsideFieldFallbackRankOffset;
    public double MaxFieldOverlapRatio => D.SmartPlacementScoring.MaxFieldOverlapRatio;
}

public sealed class MonitorConfig
{
    private static ConfigRoot D => WriteLiteDefaults.Root;

    public TimeSpan UiResponsivenessDelayThreshold => WriteLiteDefaults.Milliseconds(D.Monitor.UiResponsivenessDelayThresholdMs);
    public TimeSpan UiResponsivenessHangThreshold => WriteLiteDefaults.Milliseconds(D.Monitor.UiResponsivenessHangThresholdMs);
    public TimeSpan UiResponsivenessWatchdogInterval => WriteLiteDefaults.Milliseconds(D.Monitor.UiResponsivenessWatchdogIntervalMs);
}

public sealed class UiStateMachineConfig
{
    private static ConfigRoot D => WriteLiteDefaults.Root;

    public TimeSpan DefaultIdleGrace => TimeSpan.FromSeconds(D.UiStateMachine.DefaultIdleGraceSeconds);
    public TimeSpan MinIdleGrace => TimeSpan.FromSeconds(D.UiStateMachine.MinIdleGraceSeconds);
    public TimeSpan MaxIdleGrace => TimeSpan.FromSeconds(D.UiStateMachine.MaxIdleGraceSeconds);
    public TimeSpan DefaultFocusGrace => WriteLiteDefaults.Milliseconds(D.UiStateMachine.DefaultFocusGraceMs);
}

public sealed class MotionConfig
{
    private static ConfigRoot D => WriteLiteDefaults.Root;

    public Duration Press => WriteLiteDefaults.Milliseconds(D.Motion.PressMs);
    public Duration Release => WriteLiteDefaults.Milliseconds(D.Motion.ReleaseMs);
    public Duration Hover => WriteLiteDefaults.Milliseconds(D.Motion.HoverMs);
    public Duration Popover => WriteLiteDefaults.Milliseconds(D.Motion.PopoverMs);
    public Duration Micro => WriteLiteDefaults.Milliseconds(D.Motion.MicroMs);
    public TimeSpan StaggerStep => WriteLiteDefaults.Milliseconds(D.Motion.StaggerStepMs);
    public double RevealOffset => D.Motion.RevealOffsetPx;
    public double PressScale => D.Motion.PressScale;
    public TimeSpan PressDipKeyFrame => WriteLiteDefaults.Milliseconds(D.Motion.PressDipKeyFrameMs);
    public TimeSpan PressReleaseKeyFrame => WriteLiteDefaults.Milliseconds(D.Motion.PressReleaseKeyFrameMs);
    public TimeSpan SpinnerRotationDuration => WriteLiteDefaults.Milliseconds(D.Motion.SpinnerRotationDurationMs);
}

public sealed class UnderlineThemeConfig
{
    private static ConfigRoot D => WriteLiteDefaults.Root;

    public double DefaultThickness => D.UnderlineTheme.DefaultThickness;
    public double DefaultWaveHeight => D.UnderlineTheme.DefaultWaveHeight;
    public double DefaultOpacity => D.UnderlineTheme.DefaultOpacity;
    public double SuggestionOpacity => D.UnderlineTheme.SuggestionOpacity;
    public double WarningOpacity => D.UnderlineTheme.WarningOpacity;
    public double WaveStep => D.UnderlineTheme.WaveStep;
    public double MinimizeDuplicationThickness => D.UnderlineTheme.MinimizeDuplicationThickness;
    public double MinimizeDuplicationWaveHeight => D.UnderlineTheme.MinimizeDuplicationWaveHeight;
    public double MinimizeDuplicationOpacityFactor => D.UnderlineTheme.MinimizeDuplicationOpacityFactor;
    public double BaselineOffset => D.UnderlineTheme.BaselineOffset;
    public string OrthographyColor => D.UnderlineTheme.OrthographyColor;
    public string GrammarColor => D.UnderlineTheme.GrammarColor;
    public string PunctuationColor => D.UnderlineTheme.PunctuationColor;
    public string StyleColor => D.UnderlineTheme.StyleColor;
}

public sealed class GrammarConfig
{
    private static ConfigRoot D => WriteLiteDefaults.Root;

    public int AgreementMaxModifiers => D.Grammar.AgreementMaxModifiers;
    public int AgreementSubjectWindow => D.Grammar.AgreementSubjectWindow;
    public int ClauseBoundaryMinSubordinateTokens => D.Grammar.ClauseBoundaryMinSubordinateTokens;
    public int ClauseBoundaryPredicateWindow => D.Grammar.ClauseBoundaryPredicateWindow;
    public int CaseGovernmentMaxPhraseTokens => D.Grammar.CaseGovernmentMaxPhraseTokens;
    public int VocativeMaxModifiers => D.Grammar.VocativeMaxModifiers;
}

public sealed class ScoringConfig
{
    private static ConfigRoot D => WriteLiteDefaults.Root;

    public int MinimumSplitPartLength => D.Scoring.MinimumSplitPartLength;
}

public sealed class ApplicationConfig
{
    private static ConfigRoot D => WriteLiteDefaults.Root;

    public TimeSpan SingleInstanceMutexWait => WriteLiteDefaults.Milliseconds(D.Application.SingleInstanceMutexWaitMs);
    public TimeSpan SingleInstanceSignalTimeout => WriteLiteDefaults.Milliseconds(D.Application.SingleInstanceSignalTimeoutMs);
    public TimeSpan SingleInstanceServerRetryDelay => WriteLiteDefaults.Milliseconds(D.Application.SingleInstanceServerRetryDelayMs);
    public TimeSpan SingleInstanceDisposeWait => WriteLiteDefaults.Milliseconds(D.Application.SingleInstanceDisposeWaitMs);
    public TimeSpan LifecycleSmokeShutdownDelay => WriteLiteDefaults.Milliseconds(D.Application.LifecycleSmokeShutdownDelayMs);
    public TimeSpan ReaderAnnounceDelay => WriteLiteDefaults.Milliseconds(D.Application.ReaderAnnounceDelayMs);
}

public sealed class AudioConfig
{
    private static ConfigRoot D => WriteLiteDefaults.Root;

    public TimeSpan AmbienceTickerInterval => WriteLiteDefaults.Milliseconds(D.Audio.AmbienceTickerIntervalMs);
    public TimeSpan AmbienceRestartThreshold => WriteLiteDefaults.Seconds(D.Audio.AmbienceRestartThresholdSeconds);
}

// ── Модель JSON (зеркало engine-config.json) ──────────────────────────────

internal sealed class ConfigRoot
{
    public NetworkingJson Networking { get; set; } = new();
    [JsonPropertyName("analysis")] public AnalysisJson Analysis { get; set; } = new();
    public DebounceJson Debounce { get; set; } = new();
    public MemoryJson Memory { get; set; } = new();
    public ModelJson Model { get; set; } = new();
    [JsonPropertyName("textLimits")] public TextLimitsJson TextLimits { get; set; } = new();
    public UiJson Ui { get; set; } = new();
    [JsonPropertyName("smartPlacementScoring")] public SmartPlacementScoringJson SmartPlacementScoring { get; set; } = new();
    public MonitorJson Monitor { get; set; } = new();
    [JsonPropertyName("uiStateMachine")] public UiStateMachineJson UiStateMachine { get; set; } = new();
    public MotionJson Motion { get; set; } = new();
    [JsonPropertyName("underlineTheme")] public UnderlineThemeJson UnderlineTheme { get; set; } = new();
    public GrammarJson Grammar { get; set; } = new();
    public ScoringJson Scoring { get; set; } = new();
    public ApplicationJson Application { get; set; } = new();
    public AudioJson Audio { get; set; } = new();
}

public sealed class NetworkingJson
{
    public int LanguageEnginePreferredPort { get; set; } = 18081;
    public int LanguageEnginePortSearchRange { get; set; } = 40;
    public string LanguageEngineHostAddress { get; set; } = "127.0.0.1";
    public int LanguageEngineStartupTimeoutSeconds { get; set; } = 15;
    public int LanguageEngineRequestTimeoutSeconds { get; set; } = 30;
    public int LanguageEngineShutdownTimeoutSeconds { get; set; } = 5;
    public int LanguageEngineReadyPollIntervalMs { get; set; } = 400;
    public int LanguageEngineRestartPollIntervalMs { get; set; } = 200;
    public int LanguageEngineRecoveryBaseDelaySeconds { get; set; } = 3;
    public int LanguageEnginePortBindSettleMs { get; set; } = 150;
    public int LanguageEngineReadinessProbeTimeoutSeconds { get; set; } = 3;
    public int EngineMaxStartAttempts { get; set; } = 8;
    public int EngineAutoStartMaxAttempts { get; set; } = 3;
    public int EngineAutoStartFailureLimit { get; set; } = 5;
    public int EngineRecoveryBackoffBaseSeconds { get; set; } = 3;
    public int EngineRecoveryBackoffFactor { get; set; } = 3;
    public int EngineRecoveryBackoffMaxSeconds { get; set; } = 60;
    public int LanguageEngineMaxTextLength { get; set; } = 20000;
    public string LanguageEngineJavaMaxHeap { get; set; } = "512m";
    public string LanguageEngineRuntimeRelativeDir { get; set; } = "ThirdParty\\LanguageEngine\\6.4";
}

public sealed class AnalysisJson
{
    public int MaxTextLengthDefault { get; set; } = 20000;
    public int EditingIdleGraceSecondsDefault { get; set; } = 4;
    public int TextFieldPollIntervalMs { get; set; } = 800;
    public int TextFieldFastDebounceMs { get; set; } = 180;
    public int TextFieldFastDebounceClampMinMs { get; set; } = 120;
    public int TextFieldFastDebounceClampMaxMs { get; set; } = 220;
    public int TextFieldDeepDebounceDefaultMs { get; set; } = 900;
    public int TextFieldDeepDebounceClampMinMs { get; set; } = 700;
    public int TextFieldDeepDebounceClampMaxMs { get; set; } = 1200;
    public int TextFieldCorrectionSuppressionMs { get; set; } = 450;
    public int TextFieldIdleTickIntervalMs { get; set; } = 500;
    public int FocusSubscriptionTimeoutSeconds { get; set; } = 5;
    public int PostWriteSettleDelayMs { get; set; } = 350;
    public int DirtyTextRangeMaxLength { get; set; } = 2400;
    public int LiveAnalysisCharacterCeiling { get; set; } = 120000;
    public int RegexMatchTimeoutMs { get; set; } = 100;
    public int StyleRegexMatchTimeoutMs { get; set; } = 200;
    public int UiaResolveTimeoutMs { get; set; } = 700;
    public int UiaReadTimeoutMs { get; set; } = 1200;
    public int UiaOperationTimeoutMs { get; set; } = 750;
    public int UiaCircuitBreakerCooldownSeconds { get; set; } = 8;
    public int[] ExternalWriteVerifyBackoffMs { get; set; } = [0, 25, 70, 150];
}

public sealed class DebounceJson
{
    public int AnalysisDelayMsDefault { get; set; } = 400;
    public int AnalysisDelayMsClampMin { get; set; } = 100;
    public int AnalysisDelayMsClampMax { get; set; } = 5000;
    public int MaxTextLengthClampMin { get; set; } = 500;
    public int MaxTextLengthClampMax { get; set; } = 100000;
    public int AiDebounceMsDefault { get; set; } = 1500;
    public int AiDebounceMsClampMin { get; set; } = 1500;
    public int AiDebounceMsClampMax { get; set; } = 2000;
    public int AiMinTextLengthDefault { get; set; } = 12;
    public int AiMinTextLengthClampMin { get; set; } = 4;
    public int AiMinTextLengthClampMax { get; set; } = 200;
    public int AiMaxTextLengthDefault { get; set; } = 12000;
    public int AiMaxTextLengthClampMin { get; set; } = 200;
    public int AiMaxTextLengthClampMax { get; set; } = 20000;
    public int AiDebounceClampMinMs { get; set; } = 200;
    public int AiDebounceClampMaxMs { get; set; } = 2000;
    public int NotesSaveDelayMs { get; set; } = 600;
    public int ReadingLibraryWriteDelayMs { get; set; } = 400;
    public int DocumentRecoveryIntervalSeconds { get; set; } = 20;
    public int ReaderPositionSaveDelayMs { get; set; } = 400;
    public int DictionarySkeletonDelayMs { get; set; } = 140;
    public int EditingWritingDelayMs { get; set; } = 900;
    public int StudyCardDraftTimeoutSeconds { get; set; } = 60;
    public int EditorKeystrokeAnalysisDelayMs { get; set; } = 220;
    public int EditorFullRescanDelayMs { get; set; } = 80;
    public int EditorStructuralRescanDelayMs { get; set; } = 250;
    public int EditorPostEditRescanDelayMs { get; set; } = 120;
    public int EditorPostRewriteRescanDelayMs { get; set; } = 150;
    public int DebounceSetClampMinMs { get; set; } = 50;
    public int DebounceSetClampMaxMs { get; set; } = 10000;
}

public sealed class MemoryJson
{
    public int OfflineLexicalMaxCacheEntries { get; set; } = 384;
    public int TranslationIndexMaxCacheEntries { get; set; } = 256;
    public int SqliteLexicalMaxCacheEntries { get; set; } = 512;
    public int InlineGeometryCacheCapacity { get; set; } = 512;
    public int InputLatencyCapacity { get; set; } = 1024;
    public int RecentDocumentsMaxEntries { get; set; } = 12;
    public int DocumentFingerprintPrefixBytes { get; set; } = 4194304;
    public int DocumentFingerprintBufferBytes { get; set; } = 65536;
    public int MorphologyCacheLimit { get; set; } = 40000;
    public int CandidateEditDistanceOneCap { get; set; } = 1024;
    public int CandidateEditDistanceTwoCap { get; set; } = 4096;
    public int MaxAlignableTokens { get; set; } = 1200;
    public int WordPieceMaxCharsPerWord { get; set; } = 100;
    public int CandidateJudgeMaxCandidates { get; set; } = 8;
    public int CandidateJudgeMaxAnswerTokens { get; set; } = 24;
}

public sealed class ModelJson
{
    public string QwenDefaultModelVersion { get; set; } = "WriteLite-Qwen-0.6B-GEC-1.0.0-dev";
    public string QwenDefaultEndpoint { get; set; } = "http://127.0.0.1:8742";
    public int QwenDefaultServerContextTokens { get; set; } = 768;
    public int QwenDefaultServerBatchTokens { get; set; } = 256;
    public int QwenDefaultServerMicroBatchTokens { get; set; } = 128;
    public int QwenDefaultServerParallelSlots { get; set; } = 1;
    public long QwenMaximumLocalModelBytes { get; set; } = 5368709120;
    public int QwenTimeoutSeconds { get; set; } = 90;
    public int QwenInstructionTimeoutSeconds { get; set; } = 30;
    public int QwenMaxNewTokens { get; set; } = 96;
    public int QwenMaxCompletionTokens { get; set; } = 288;
    public int QwenWedgeThreshold { get; set; } = 2;
    public int QwenRecoveryCooldownSeconds { get; set; } = 15;
    public int QwenMaxRecoveryAttempts { get; set; } = 3;
    public int QwenHealthProbeTimeoutSeconds { get; set; } = 2;
    public int QwenServerStartupPollMs { get; set; } = 250;
    public int QwenServerStartupPollAttempts { get; set; } = 20;
    public int QwenProcessKillWaitSeconds { get; set; } = 5;
    public int QwenWorkerThreadsMin { get; set; } = 2;
    public int QwenWorkerThreadsMax { get; set; } = 4;
    public int LocalAiCacheTtlMinutes { get; set; } = 10;
    public int LocalAiMaxInputChars { get; set; } = 12000;
    public int LocalAiMinInputChars { get; set; } = 1;
    public double CandidateScorerAutomaticThreshold { get; set; } = 0.80;
    public double CandidateScorerAutomaticMargin { get; set; } = 0.18;
    public double CandidateScorerSuggestionThreshold { get; set; } = 0.28;
    public int MorphologyAlternativesMax { get; set; } = 4;
    public int EditDistanceSuggestCap { get; set; } = 8;
    public int EditDistanceFilterCap { get; set; } = 5;
    public double RewriteTemperature { get; set; } = 0.4;
    public double RewriteShortenTemperature { get; set; } = 0.2;
    public double WritingCompletionTemperature { get; set; } = 0.3;
}

public sealed class TextLimitsJson
{
    public int AiMaxTextLengthForEngine { get; set; } = 12000;
    public int TextRewriteMaxContextChars { get; set; } = 400;
    public int TextRewriteMaxSelectionChars { get; set; } = 4000;
    public int WritingAssistanceContextBudget { get; set; } = 320;
    public int WritingAssistanceMaxCompletionChars { get; set; } = 120;
    public int WritingAssistanceMinimumContextChars { get; set; } = 24;
    public int RewriteDiffMaxTokens { get; set; } = 2000;
    public int CorrectionCardMaxFragmentLength { get; set; } = 80;
    public int StudyCardMaxPassage { get; set; } = 1200;
    public int PunctuationModelMaxSentenceLength { get; set; } = 300;
    public int AiCallRouterMinChars { get; set; } = 12;
    public int AiCallRouterMaxChars { get; set; } = 12000;
    public int PromptTextBoxMaxLength { get; set; } = 200;
    public int DocumentIssueMaxRenderedIssues { get; set; } = 400;
    public int ReadingAnchorNearWindow { get; set; } = 20000;
}

public sealed class UiJson
{
    public double EditorPanelCollapseThresholdPx { get; set; } = 1080;
    public double EditorCompactToolbarThresholdPx { get; set; } = 860;
    public double MainWindowCompactRailThresholdPx { get; set; } = 1000;
    public double AutoCorrectConfidence { get; set; } = 0.95;
    public double SafeApplyConfidence { get; set; } = 0.85;
    public double ReaderMarksPanelWidthPx { get; set; } = 340;
    public double ReaderMinimumTextWidthPx { get; set; } = 460;
    public double IndentStepPoints { get; set; } = 35.4;
    public double OverlayPlacementGapPx { get; set; } = 8;
    public double SmartPlacementGapPx { get; set; } = 8;
    public double SmartPlacementMinEdgeMarginPx { get; set; } = 4;
    public double SmartPlacementBaseScore { get; set; } = 1000;
    public double SmartPlacementPreferenceRankStep { get; set; } = 10;
}

public sealed class SmartPlacementScoringJson
{
    public double FieldOverlapPenalty { get; set; } = 2.5;
    public double FieldDistancePenalty { get; set; } = 0.15;
    public double AnchorOverlapPenalty { get; set; } = 8.0;
    public double AnchorDistancePenalty { get; set; } = 0.35;
    public double CaretIntersectionPenalty { get; set; } = 500;
    public double HostOverlapBonus { get; set; } = 0.002;
    public double InsideBottomRightPenalty { get; set; } = 400;
    public double OutsideFieldFallbackRankOffset { get; set; } = 1000000;
    public double MaxFieldOverlapRatio { get; set; } = 0.28;
}

public sealed class MonitorJson
{
    public int UiResponsivenessDelayThresholdMs { get; set; } = 250;
    public int UiResponsivenessHangThresholdMs { get; set; } = 2000;
    public int UiResponsivenessWatchdogIntervalMs { get; set; } = 400;
}

public sealed class UiStateMachineJson
{
    public int DefaultIdleGraceSeconds { get; set; } = 4;
    public double MinIdleGraceSeconds { get; set; } = 1.5;
    public double MaxIdleGraceSeconds { get; set; } = 30;
    public int DefaultFocusGraceMs { get; set; } = 250;
}

public sealed class MotionJson
{
    public int PressMs { get; set; } = 90;
    public int ReleaseMs { get; set; } = 150;
    public int HoverMs { get; set; } = 140;
    public int PopoverMs { get; set; } = 160;
    public int MicroMs { get; set; } = 200;
    public int StaggerStepMs { get; set; } = 40;
    public double RevealOffsetPx { get; set; } = 6;
    public double PressScale { get; set; } = 0.985;
    public int PressDipKeyFrameMs { get; set; } = 80;
    public int PressReleaseKeyFrameMs { get; set; } = 230;
    public int SpinnerRotationDurationMs { get; set; } = 900;
}

public sealed class UnderlineThemeJson
{
    public double DefaultThickness { get; set; } = 1.25;
    public double DefaultWaveHeight { get; set; } = 1.35;
    public double DefaultOpacity { get; set; } = 0.88;
    public double SuggestionOpacity { get; set; } = 0.62;
    public double WarningOpacity { get; set; } = 0.78;
    public double WaveStep { get; set; } = 3.25;
    public double MinimizeDuplicationThickness { get; set; } = 1.0;
    public double MinimizeDuplicationWaveHeight { get; set; } = 1.1;
    public double MinimizeDuplicationOpacityFactor { get; set; } = 0.85;
    public double BaselineOffset { get; set; } = 1.25;
    public string OrthographyColor { get; set; } = "#FFEC6C08";
    public string GrammarColor { get; set; } = "#FFE0A64B";
    public string PunctuationColor { get; set; } = "#FFE89645";
    public string StyleColor { get; set; } = "#FFA48447";
}

public sealed class GrammarJson
{
    public int AgreementMaxModifiers { get; set; } = 3;
    public int AgreementSubjectWindow { get; set; } = 3;
    public int ClauseBoundaryMinSubordinateTokens { get; set; } = 2;
    public int ClauseBoundaryPredicateWindow { get; set; } = 3;
    public int CaseGovernmentMaxPhraseTokens { get; set; } = 3;
    public int VocativeMaxModifiers { get; set; } = 2;
}

public sealed class ScoringJson
{
    public int MinimumSplitPartLength { get; set; } = 3;
}

public sealed class ApplicationJson
{
    public int SingleInstanceMutexWaitMs { get; set; } = 100;
    public int SingleInstanceSignalTimeoutMs { get; set; } = 500;
    public int SingleInstanceServerRetryDelayMs { get; set; } = 150;
    public int SingleInstanceDisposeWaitMs { get; set; } = 500;
    public int LifecycleSmokeShutdownDelayMs { get; set; } = 1500;
    public int ReaderAnnounceDelayMs { get; set; } = 1000;
}

public sealed class AudioJson
{
    public int AmbienceTickerIntervalMs { get; set; } = 250;
    public int AmbienceRestartThresholdSeconds { get; set; } = 3;
}
