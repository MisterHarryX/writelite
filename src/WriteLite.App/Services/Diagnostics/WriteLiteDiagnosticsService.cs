using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using WriteLite.Models;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Settings;

namespace WriteLite.Services.Diagnostics;

public sealed class WriteLiteDiagnosticsSnapshot
{
    public string AppVersion { get; init; } = "";
    public string BuildConfiguration { get; init; } = "";
    public string EngineStatus { get; init; } = "";
    public string EngineRestarts { get; init; } = "0";
    public string LastSuccessUtc { get; init; } = "—";
    public string FieldDetected { get; init; } = "Нет";
    public string FieldProcess { get; init; } = "—";
    public string FieldControl { get; init; } = "—";
    public string FieldRead { get; init; } = "—";
    public string FieldWrite { get; init; } = "—";
    public string FieldValuePattern { get; init; } = "—";
    public string FieldTextPattern { get; init; } = "—";
    public string FieldBounds { get; init; } = "—";
    public string LastAnalysisDuration { get; init; } = "—";
    public string LastIssueCount { get; init; } = "—";
    public string WindowsVersion { get; init; } = "";
    public string DotNetVersion { get; init; } = "";
    public string DpiScale { get; init; } = "—";
    public string WorkArea { get; init; } = "—";
    public string AppMemoryMb { get; init; } = "—";
    public string ChildMemoryMb { get; init; } = "—";
    public string CheckingEnabled { get; init; } = "—";
    public string ExtendedChecking { get; init; } = "—";
    public string UiResponsiveness { get; init; } = "—";
    public string AnalysisQueues { get; init; } = "—";
    public IReadOnlyList<string> RecentEvents { get; init; } = [];
}

public sealed class WriteLiteDiagnosticsService
{
    private readonly Func<TextSnapshot?> _latestSnapshot;
    private readonly Func<WriteLiteLanguageEngine?> _engine;
    private readonly Func<WriteLiteAppSettings> _settings;
    private readonly Func<TextFieldMonitor?> _monitor;
    private readonly Func<UiResponsivenessSnapshot?>? _uiResponsiveness;

    public WriteLiteDiagnosticsService(
        Func<TextSnapshot?> latestSnapshot,
        Func<WriteLiteLanguageEngine?> engine,
        Func<WriteLiteAppSettings> settings,
        Func<TextFieldMonitor?>? monitor = null,
        Func<UiResponsivenessSnapshot?>? uiResponsiveness = null)
    {
        _latestSnapshot = latestSnapshot;
        _engine = engine;
        _settings = settings;
        _monitor = monitor ?? (() => null);
        _uiResponsiveness = uiResponsiveness;
    }

    public WriteLiteDiagnosticsSnapshot Capture()
    {
        var snap = _latestSnapshot();
        var engine = _engine();
        var settings = _settings();
        var monitor = _monitor();
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0";
#if DEBUG
        const string config = "Debug";
#else
        const string config = "Release";
#endif

        string fieldDetected = "Нет";
        string process = "—";
        string control = "—";
        string read = "—";
        string write = "—";
        string valueP = "—";
        string textP = "—";
        string bounds = "—";
        string issues = "—";

        if (snap is not null)
        {
            fieldDetected = "Да";
            process = ResolveProcessName(snap.Target.ProcessId);
            control = snap.Target.ControlTypeName;
            read = "Да";
            write = snap.Target.SupportsDirectWrite ? "Да" : "Нет";
            var adapter = snap.Target.AdapterName ?? string.Empty;
            valueP = adapter.Contains("Value", StringComparison.OrdinalIgnoreCase) ? "Да" : "—";
            textP = adapter.Contains("Text", StringComparison.OrdinalIgnoreCase) ? "Да" : "—";
            // If adapter is composite/unknown, show adapter name neutrally.
            if (valueP == "—" && textP == "—")
            {
                valueP = adapter.Length > 0 ? adapter : "—";
                textP = snap.Target.SupportsRangeReplacement ? "частично" : "—";
            }

            var b = snap.Target.Bounds;
            bounds = $"{(int)b.X},{(int)b.Y} {(int)b.Width}×{(int)b.Height}";
            issues = snap.Issues.Count.ToString();
        }

        var lastDuration = "—";
        if (engine is not null && engine.LastAnalysisDuration > TimeSpan.Zero)
        {
            lastDuration = $"{engine.LastAnalysisDuration.TotalMilliseconds:F0} мс";
        }
        else if (monitor is not null && monitor.LastAnalysisDuration > TimeSpan.Zero)
        {
            lastDuration = $"{monitor.LastAnalysisDuration.TotalMilliseconds:F0} мс";
        }

        if (engine is not null && engine.LastIssueCount >= 0 && issues == "—")
        {
            issues = engine.LastIssueCount.ToString();
        }

        var appMb = Math.Round(Process.GetCurrentProcess().WorkingSet64 / 1024d / 1024d, 1);
        var childMb = "—";
        try
        {
            // Aggregate child memory only; do not surface PID in user UI.
            if (engine?.ProcessId is int id)
            {
                using var child = Process.GetProcessById(id);
                childMb = Math.Round(child.WorkingSet64 / 1024d / 1024d, 1).ToString("F1");
            }
        }
        catch
        {
            // ignore
        }

        string dpi = "—";
        string workArea = "—";
        try
        {
            var src = System.Windows.Media.VisualTreeHelper.GetDpi(
                System.Windows.Application.Current?.MainWindow
                ?? throw new InvalidOperationException());
            dpi = $"{src.DpiScaleX * 100:F0}%";
        }
        catch
        {
            try
            {
                using var g = System.Drawing.Graphics.FromHwnd(IntPtr.Zero);
                dpi = $"{g.DpiX / 96.0 * 100:F0}%";
            }
            catch
            {
                // ignore
            }
        }

        try
        {
            var wa = System.Windows.SystemParameters.WorkArea;
            workArea = $"{(int)wa.Width}×{(int)wa.Height}";
        }
        catch
        {
            // ignore
        }

        return new WriteLiteDiagnosticsSnapshot
        {
            AppVersion = version,
            BuildConfiguration = config,
            EngineStatus = engine?.UserFacingStatus ?? "Используется базовая проверка WriteLite",
            EngineRestarts = (engine?.RestartCount ?? 0).ToString(),
            LastSuccessUtc = engine?.LastSuccessUtc is DateTimeOffset t
                ? t.ToLocalTime().ToString("HH:mm:ss")
                : "—",
            FieldDetected = fieldDetected,
            FieldProcess = SanitizeToken(process),
            FieldControl = SanitizeToken(control),
            FieldRead = read,
            FieldWrite = write,
            FieldValuePattern = valueP,
            FieldTextPattern = textP,
            FieldBounds = bounds,
            LastAnalysisDuration = lastDuration,
            LastIssueCount = issues,
            WindowsVersion = Environment.OSVersion.VersionString,
            DotNetVersion = RuntimeInformation.FrameworkDescription,
            DpiScale = dpi,
            WorkArea = workArea,
            AppMemoryMb = appMb.ToString("F1"),
            ChildMemoryMb = childMb,
            CheckingEnabled = settings.CheckingEnabled ? "Да" : "Нет",
            ExtendedChecking = settings.ExtendedChecking ? "Да" : "Нет",
            UiResponsiveness = FormatUiResponsiveness(_uiResponsiveness?.Invoke()),
            AnalysisQueues = FormatAnalysisQueues(monitor),
            RecentEvents = ReadRecentSafeLogLines(80)
        };
    }

    private static string FormatUiResponsiveness(UiResponsivenessSnapshot? snap)
    {
        if (snap is null) return "—";
        var label = snap.Level switch
        {
            UiResponsivenessLevel.HangSuspected => "Обнаружено зависание",
            UiResponsivenessLevel.Delayed => "Задержка",
            _ => "Норма"
        };
        return $"{label} · last={snap.LastDelayMs:F0} мс · max={snap.MaxDelayMs:F0} мс · >250={snap.DelayOver250Count} · >2с={snap.DelayOver2000Count}";
    }

    private static string FormatAnalysisQueues(TextFieldMonitor? monitor)
    {
        if (monitor is null) return "—";
        var fast = monitor.FastAnalysisLatency;
        var deep = monitor.DeepAnalysisLatency;
        return $"fast samples={fast.Count} p95={fast.P95Milliseconds:F0} мс · deep samples={deep.Count} p95={deep.P95Milliseconds:F0} мс";
    }

    public string BuildCopyableReport(WriteLiteDiagnosticsSnapshot s)
    {
        var settings = _settings();
        var sb = new StringBuilder();
        sb.AppendLine("WriteLite diagnostic report");
        sb.AppendLine($"version={s.AppVersion}");
        sb.AppendLine($"build={s.BuildConfiguration}");
        sb.AppendLine($"windows={s.WindowsVersion}");
        sb.AppendLine($"dotnet={s.DotNetVersion}");
        sb.AppendLine($"dpi={s.DpiScale}");
        sb.AppendLine($"workArea={s.WorkArea}");
        sb.AppendLine($"engine={s.EngineStatus}");
        sb.AppendLine($"engineRestarts={s.EngineRestarts}");
        sb.AppendLine($"lastSuccess={s.LastSuccessUtc}");
        sb.AppendLine($"lastAnalysisMs={s.LastAnalysisDuration}");
        sb.AppendLine($"fieldDetected={s.FieldDetected}");
        sb.AppendLine($"fieldProcess={s.FieldProcess}");
        sb.AppendLine($"fieldControl={s.FieldControl}");
        sb.AppendLine($"read={s.FieldRead}");
        sb.AppendLine($"write={s.FieldWrite}");
        sb.AppendLine($"valuePattern={s.FieldValuePattern}");
        sb.AppendLine($"textPattern={s.FieldTextPattern}");
        sb.AppendLine($"bounds={s.FieldBounds}");
        sb.AppendLine($"lastIssues={s.LastIssueCount}");
        sb.AppendLine($"appMemoryMb={s.AppMemoryMb}");
        sb.AppendLine($"childMemoryMb={s.ChildMemoryMb}");
        sb.AppendLine($"checkingEnabled={settings.CheckingEnabled}");
        sb.AppendLine($"extendedChecking={settings.ExtendedChecking}");
        sb.AppendLine($"showIndicator={settings.ShowIndicator}");
        sb.AppendLine($"analysisDelayMs={settings.AnalysisDelayMs}");
        sb.AppendLine($"maxTextLength={settings.MaxTextLength}");
        sb.AppendLine("events:");
        foreach (var line in s.RecentEvents.TakeLast(40))
        {
            sb.AppendLine("  " + StripSensitive(line));
        }

        return sb.ToString();
    }

    public static string LogsDirectory
        => Path.Combine(AppContext.BaseDirectory, "logs");

    public static IReadOnlyList<string> ReadRecentSafeLogLines(int count)
    {
        try
        {
            var path = Path.Combine(LogsDirectory, "compatibility.log");
            if (!File.Exists(path))
            {
                return [];
            }

            string content;
            using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            using (var reader = new StreamReader(stream))
            {
                content = reader.ReadToEnd();
            }

            var lines = content
                .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
                .TakeLast(Math.Clamp(count, 10, 200))
                .ToList();
            return lines.Select(StripSensitive).ToList();
        }
        catch
        {
            return [];
        }
    }

    private static string ResolveProcessName(int processId)
    {
        try
        {
            using var p = Process.GetProcessById(processId);
            return SanitizeToken(p.ProcessName);
        }
        catch
        {
            return "—";
        }
    }

    private static string SanitizeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "—";
        // Never include path separators / user profile.
        return value.Replace('\\', '_').Replace('/', '_');
    }

    private static string StripSensitive(string line)
    {
        // Defense in depth: drop lines that look like free text payloads.
        if (line.Contains("text=", StringComparison.OrdinalIgnoreCase)
            && line.Contains("textLength=", StringComparison.OrdinalIgnoreCase) == false)
        {
            return "[redacted]";
        }

        // Strip absolute Windows paths.
        line = System.Text.RegularExpressions.Regex.Replace(
            line,
            @"[A-Za-z]:\\[^\s""']+",
            "[path]");

        return line
            .Replace("LanguageTool", "engine", StringComparison.OrdinalIgnoreCase)
            .Replace("languagetool", "engine", StringComparison.OrdinalIgnoreCase)
            .Replace("javaw", "runtime", StringComparison.OrdinalIgnoreCase)
            .Replace("java.exe", "runtime", StringComparison.OrdinalIgnoreCase);
    }
}
