using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace WriteLite.Services;

public static class CompatibilityLogger
{
    private static readonly object SyncRoot = new();
    private static readonly string LogDirectory = Path.Combine(AppContext.BaseDirectory, "logs");
    private static readonly string LogFile = Path.Combine(LogDirectory, "compatibility.log");

    /// <summary>Rotate when active log exceeds this size (default 2 MB).</summary>
    public static long MaxLogBytes { get; set; } = 2 * 1024 * 1024;

    /// <summary>How many rotated archives to keep (compatibility.1.log …).</summary>
    public static int MaxArchiveCount { get; set; } = 3;

    private static readonly Regex AbsolutePathRegex = new(
        @"[A-Za-z]:\\[^\s""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex UncPathRegex = new(
        @"\\\\[^\s""']+",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static void State(string state)
    {
        Write("state", $"value={Sanitize(state)}");
    }

    public static void Technical(string eventName, string detail)
    {
        Write(eventName, Sanitize(detail));
    }

    public static void Element(
        int processId,
        string controlType,
        bool valuePattern,
        bool textPattern,
        bool isPassword,
        bool isEnabled,
        bool isOffscreen)
    {
        Write(
            "element",
            $"process={GetProcessName(processId)} pid={processId} " +
            $"control={Sanitize(controlType)} valuePattern={valuePattern} textPattern={textPattern} " +
            $"isPassword={isPassword} isEnabled={isEnabled} isOffscreen={isOffscreen}");
    }

    public static void Operation(string operation, int processId, bool succeeded, string detail)
    {
        Write(
            "operation",
            $"name={Sanitize(operation)} process={GetProcessName(processId)} pid={processId} " +
            $"succeeded={succeeded} detail={Sanitize(detail)}");
    }

    public static void AccessError(string operation, int? processId, Exception exception)
    {
        var process = processId is int id
            ? $"process={GetProcessName(id)} pid={id} "
            : string.Empty;

        // Exception messages can contain a text fragment supplied by an
        // automation host.  Keep diagnostics useful without ever persisting
        // that fragment: operation, exception type and HRESULT are enough to
        // group failures and are safe to include in support bundles.
        Write(
            "access-error",
            $"operation={Sanitize(operation)} {process}" +
            $"exception={Sanitize(exception.GetType().Name)} hresult=0x{exception.HResult:X8}");
    }

    /// <summary>
    /// Records a failure nobody handled, with enough detail to act on it.
    /// </summary>
    /// <remarks>
    /// Deliberately more than <see cref="AccessError"/> writes. That one is for the
    /// automation surface, where an exception message can carry a fragment of whatever
    /// the user was typing in another application, so it records only the shape of the
    /// failure. An exception that reached a global handler is a defect in WriteLite, and
    /// "exception=InvalidOperationException" on its own does not locate one.
    ///
    /// The stack trace is code — type and method names, and file paths that
    /// <see cref="Sanitize"/> replaces — so it can be recorded. The exception *message*
    /// still cannot: it is the one field that routinely quotes the user's own text back.
    /// The result is a line naming the operation, the type, where it was thrown and how
    /// it got there, which is what makes a report actionable without making it personal.
    /// </remarks>
    public static void Unhandled(string source, Exception exception, string? context = null)
    {
        ArgumentNullException.ThrowIfNull(exception);

        var inner = exception.InnerException;

        Write(
            "unhandled",
            $"source={Sanitize(source)} " +
            (context is null ? string.Empty : $"context={Sanitize(context)} ") +
            $"exception={Sanitize(exception.GetType().FullName ?? "unknown")} " +
            $"hresult=0x{exception.HResult:X8} " +
            $"site={Sanitize(exception.TargetSite?.DeclaringType?.FullName + "." + exception.TargetSite?.Name)} " +
            (inner is null ? string.Empty : $"inner={Sanitize(inner.GetType().Name)} ") +
            $"stack={Sanitize(Frames(exception))}");
    }

    /// <summary>The stack, trimmed to the frames that identify the fault.</summary>
    /// <remarks>
    /// Bounded so one failure cannot fill the log: the top of a stack is what says where
    /// the defect is, and forty frames of dispatcher plumbing underneath say nothing that
    /// the source field has not already said.
    /// </remarks>
    private static string Frames(Exception exception, int max = 12)
    {
        var stack = exception.StackTrace;
        if (string.IsNullOrWhiteSpace(stack))
        {
            return "none";
        }

        var frames = stack
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Take(max);

        return string.Join(" | ", frames);
    }

    private static void Write(string eventName, string fields)
    {
        try
        {
            var line = string.Create(
                CultureInfo.InvariantCulture,
                $"{DateTimeOffset.Now:O} event={eventName} {fields}{Environment.NewLine}");

            lock (SyncRoot)
            {
                Directory.CreateDirectory(LogDirectory);
                RotateIfNeeded(LogFile, MaxLogBytes, MaxArchiveCount);
                using var stream = new FileStream(
                    LogFile,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite);
                using var writer = new StreamWriter(stream, Encoding.UTF8);
                writer.Write(line);
            }
        }
        catch
        {
            // Диагностика не должна влиять на работу корректора.
        }
    }

    /// <summary>
    /// Rotates <paramref name="logPath"/> when it exceeds <paramref name="maxBytes"/>.
    /// Archives: compatibility.1.log … up to maxArchives. Safe to call on missing file.
    /// </summary>
    public static void RotateIfNeeded(string logPath, long maxBytes, int maxArchives)
    {
        if (string.IsNullOrWhiteSpace(logPath) || maxBytes <= 0 || maxArchives <= 0)
        {
            return;
        }

        try
        {
            if (!File.Exists(logPath))
            {
                return;
            }

            var info = new FileInfo(logPath);
            if (info.Length < maxBytes)
            {
                return;
            }

            // Shift archives upward: .2 -> .3, .1 -> .2, then active -> .1
            for (var i = maxArchives; i >= 1; i--)
            {
                var src = i == 1 ? logPath : ArchivePath(logPath, i - 1);
                var dst = ArchivePath(logPath, i);
                if (i == maxArchives && File.Exists(dst))
                {
                    File.Delete(dst);
                }

                if (File.Exists(src))
                {
                    if (File.Exists(dst))
                    {
                        File.Delete(dst);
                    }

                    File.Move(src, dst);
                }
            }
        }
        catch
        {
            // rotation is best-effort
        }
    }

    public static string ArchivePath(string logPath, int index)
    {
        var dir = Path.GetDirectoryName(logPath) ?? ".";
        var name = Path.GetFileNameWithoutExtension(logPath);
        var ext = Path.GetExtension(logPath);
        return Path.Combine(dir, $"{name}.{index}{ext}");
    }

    private static string GetProcessName(int processId)
    {
        try
        {
            using var process = Process.GetProcessById(processId);
            return Sanitize(process.ProcessName);
        }
        catch
        {
            return "unavailable";
        }
    }

    /// <summary>Public for unit tests — strips user paths and brand tokens from free-form fields.</summary>
    public static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "unknown";

        // Order matters: strip paths while whitespace still delimits tokens,
        // then neutralize brand names, then flatten whitespace.
        var cleaned = AbsolutePathRegex.Replace(value, "[path]");
        cleaned = UncPathRegex.Replace(cleaned, "[path]");
        cleaned = cleaned
            .Replace("LanguageTool", "engine", StringComparison.OrdinalIgnoreCase)
            .Replace("languagetool", "engine", StringComparison.OrdinalIgnoreCase)
            .Replace("javaw", "runtime", StringComparison.OrdinalIgnoreCase)
            .Replace("java.exe", "runtime", StringComparison.OrdinalIgnoreCase)
            .Replace('\r', '_')
            .Replace('\n', '_')
            .Replace(' ', '_');

        // Bound length so a single line cannot balloon the log.
        if (cleaned.Length > 500)
        {
            cleaned = cleaned[..500] + "…";
        }

        return cleaned;
    }
}
