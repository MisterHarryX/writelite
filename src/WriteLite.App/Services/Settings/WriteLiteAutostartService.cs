using System.Diagnostics;
using System.IO;
using Microsoft.Win32;

namespace WriteLite.Services.Settings;

public interface IRegistryKey : IDisposable
{
    void SetValue(string name, object value);
    void DeleteValue(string name, bool throwOnMissing);
    object? GetValue(string name);
}

public interface IRegistryRoot
{
    IRegistryKey? OpenSubKey(string name, bool writable);
}

/// <summary>
/// HKCU Run autostart without admin rights. Testable via registry abstraction.
/// </summary>
public sealed class WriteLiteAutostartService : IWriteLiteAutostartService
{
    public const string RunValueName = "WriteLite";
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    private readonly Func<string> _exePathProvider;
    private readonly IRegistryRoot _registry;

    public WriteLiteAutostartService(
        Func<string>? exePathProvider = null,
        IRegistryRoot? registry = null)
    {
        _exePathProvider = exePathProvider ?? (() => Environment.ProcessPath ?? string.Empty);
        _registry = registry ?? new WindowsRegistryRoot();
    }

    public bool CanEnableForCurrentBinary
    {
        get
        {
            var path = _exePathProvider();
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
            {
                return false;
            }

            var name = Path.GetFileName(path);
            // Avoid registering `dotnet.exe` as production autostart during `dotnet run`.
            if (name.Equals("dotnet.exe", StringComparison.OrdinalIgnoreCase)
                || name.Equals("dotnet", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            return name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase);
        }
    }

    public string StatusMessage
    {
        get
        {
            if (!CanEnableForCurrentBinary)
            {
                return "Автозапуск доступен после публикации (не для dotnet run).";
            }

            return IsEnabled ? "Автозапуск включён" : "Автозапуск выключен";
        }
    }

    public bool IsEnabled
    {
        get
        {
            try
            {
                using var key = _registry.OpenSubKey(RunKeyPath, writable: false);
                var value = key?.GetValue(RunValueName) as string;
                return !string.IsNullOrWhiteSpace(value);
            }
            catch
            {
                return false;
            }
        }
    }

    public void SetEnabled(bool enabled)
    {
        if (enabled)
        {
            if (!CanEnableForCurrentBinary)
            {
                return;
            }

            var path = _exePathProvider();
            var command = "\"" + path.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
            using var key = _registry.OpenSubKey(RunKeyPath, writable: true)
                           ?? throw new InvalidOperationException("Cannot open Run key.");
            key.SetValue(RunValueName, command);
        }
        else
        {
            try
            {
                using var key = _registry.OpenSubKey(RunKeyPath, writable: true);
                key?.DeleteValue(RunValueName, throwOnMissing: false);
            }
            catch
            {
                // ignore
            }
        }
    }

    private sealed class WindowsRegistryRoot : IRegistryRoot
    {
        public IRegistryKey? OpenSubKey(string name, bool writable)
        {
            var key = Registry.CurrentUser.OpenSubKey(name, writable);
            return key is null ? null : new WindowsRegistryKey(key);
        }
    }

    private sealed class WindowsRegistryKey(RegistryKey key) : IRegistryKey, IDisposable
    {
        public void SetValue(string name, object value) => key.SetValue(name, value);

        public void DeleteValue(string name, bool throwOnMissing)
        {
            try
            {
                key.DeleteValue(name, throwOnMissing);
            }
            catch (ArgumentException) when (!throwOnMissing)
            {
                // missing
            }
        }

        public object? GetValue(string name) => key.GetValue(name);

        public void Dispose() => key.Dispose();
    }
}
