using System.IO;

namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// Resolves java/javaw without shelling out on every call. Result is cached per instance (Host lifetime).
/// </summary>
public sealed class WriteLiteJavaResolver : IWriteLiteJavaResolver
{
    private readonly object _gate = new();
    private string? _cached;
    private bool _resolved;

    public string? Resolve(WriteLiteLanguageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        lock (_gate)
        {
            if (_resolved)
            {
                return _cached;
            }

            _cached = FindJavaExecutable(options);
            _resolved = true;
            return _cached;
        }
    }

    /// <summary>Clears cache — used by tests only.</summary>
    public void ResetCache()
    {
        lock (_gate)
        {
            _resolved = false;
            _cached = null;
        }
    }

    public static IEnumerable<string> EnumerateCandidates(WriteLiteLanguageOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!string.IsNullOrWhiteSpace(options.ConfiguredJavaPath))
        {
            yield return options.ConfiguredJavaPath.Trim();
        }

        var baseDir = AppContext.BaseDirectory;
        yield return Path.Combine(baseDir, "Runtime", "Java", "bin", "javaw.exe");
        yield return Path.Combine(baseDir, "Runtime", "Java", "bin", "java.exe");

        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (!string.IsNullOrWhiteSpace(javaHome))
        {
            yield return Path.Combine(javaHome.Trim().Trim('"'), "bin", "javaw.exe");
            yield return Path.Combine(javaHome.Trim().Trim('"'), "bin", "java.exe");
        }

        if (options.PreferJavaw)
        {
            foreach (var path in FindOnPath("javaw.exe"))
            {
                yield return path;
            }

            foreach (var path in FindOnPath("java.exe"))
            {
                yield return path;
            }
        }
        else
        {
            foreach (var path in FindOnPath("java.exe"))
            {
                yield return path;
            }

            foreach (var path in FindOnPath("javaw.exe"))
            {
                yield return path;
            }
        }
    }

    public static bool IsUsableJavaExecutable(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return false;
        }

        try
        {
            var full = Path.GetFullPath(path.Trim().Trim('"'));
            if (!File.Exists(full))
            {
                return false;
            }

            var name = Path.GetFileName(full);
            return name.Equals("java.exe", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("javaw.exe", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("java", StringComparison.OrdinalIgnoreCase)
                   || name.Equals("javaw", StringComparison.OrdinalIgnoreCase);
        }
        catch
        {
            // A broken path segment must not abort discovery; keep scanning.
            return false;
        }
    }

    private static string? FindJavaExecutable(WriteLiteLanguageOptions options)
    {
        foreach (var candidate in EnumerateCandidates(options))
        {
            if (IsUsableJavaExecutable(candidate))
            {
                return Path.GetFullPath(candidate.Trim().Trim('"'));
            }
        }

        return null;
    }

    /// <summary>
    /// Scans PATH directories for the file — no shell process per request.
    /// </summary>
    private static IEnumerable<string> FindOnPath(string fileName)
    {
        var pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(pathEnv))
        {
            yield break;
        }

        foreach (var segment in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            string full;
            try
            {
                full = Path.Combine(segment.Trim().Trim('"'), fileName);
            }
            catch
            {
                // A malformed PATH segment must not stop discovery.
                continue;
            }

            yield return full;
        }
    }
}

// Backward-compatible static façade used by existing tests.
public static class WriteLiteJavaLocator
{
    public static string? FindJavaExecutable(WriteLiteLanguageOptions options)
        => new WriteLiteJavaResolver().Resolve(options);

    public static IEnumerable<string> EnumerateCandidates(WriteLiteLanguageOptions options)
        => WriteLiteJavaResolver.EnumerateCandidates(options);

    public static bool IsUsableJavaExecutable(string? path)
        => WriteLiteJavaResolver.IsUsableJavaExecutable(path);
}
