using System.IO;

namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// Configuration for the offline WriteLite language engine host and client.
/// Значения по умолчанию берутся из единого конфига (engine-config.json) через
/// <see cref="WriteLiteDefaults"/>.
/// </summary>
public sealed class WriteLiteLanguageOptions
{
    public static string DefaultRelativeRuntimeDirectory => WriteLiteDefaults.Networking.LanguageEngineRuntimeRelativeDir;
    public static string DefaultHostAddress => WriteLiteDefaults.Networking.LanguageEngineHostAddress;
    public const string DefaultLanguage = "ru-RU";

    /// <summary>Absolute path to the engine runtime folder (contains server JAR and libs).</summary>
    public string RuntimeDirectory { get; set; } = ResolveDefaultRuntimeDirectory();

    /// <summary>Optional explicit path to java.exe / javaw.exe.</summary>
    public string? ConfiguredJavaPath { get; set; }

    /// <summary>Must remain 127.0.0.1 — external hosts are rejected by the client guard.</summary>
    public string HostAddress { get; set; } = DefaultHostAddress;

    /// <summary>Preferred port; if occupied, host searches upward within PortSearchRange.</summary>
    public int PreferredPort { get; set; } = WriteLiteDefaults.Networking.LanguageEnginePreferredPort;

    /// <summary>Alias used by host port binding; same as PreferredPort.</summary>
    public int Port
    {
        get => PreferredPort;
        set => PreferredPort = value;
    }

    /// <summary>How many ports after PreferredPort to try when binding.</summary>
    public int PortSearchRange { get; set; } = WriteLiteDefaults.Networking.LanguageEnginePortSearchRange;

    public TimeSpan StartupTimeout { get; set; } = WriteLiteDefaults.Networking.LanguageEngineStartupTimeout;

    public TimeSpan RequestTimeout { get; set; } = WriteLiteDefaults.Networking.LanguageEngineRequestTimeout;

    public TimeSpan ShutdownTimeout { get; set; } = WriteLiteDefaults.Networking.LanguageEngineShutdownTimeout;

    public TimeSpan ReadyPollInterval { get; set; } = WriteLiteDefaults.Networking.LanguageEngineReadyPollInterval;

    public string Language { get; set; } = DefaultLanguage;

    /// <summary>Reject client requests longer than this many characters.</summary>
    public int MaxTextLength { get; set; } = WriteLiteDefaults.Networking.LanguageEngineMaxTextLength;

    /// <summary>Feature flag — when false, host/client refuse to start/check.</summary>
    public bool EnableEngine { get; set; } = false;

    public bool PreferJavaw { get; set; } = true;

    /// <summary>Java heap for the child process (-Xmx), e.g. 512m.</summary>
    public string JavaMaxHeap { get; set; } = WriteLiteDefaults.Networking.LanguageEngineJavaMaxHeap;

    public string ServerJarFileName { get; set; } = "languagetool-server.jar";

    public int BoundPort { get; private set; }

    public void SetBoundPort(int port) => BoundPort = port;

    public string BaseUrl
    {
        get
        {
            var port = BoundPort > 0 ? BoundPort : PreferredPort;
            return $"http://{HostAddress}:{port}";
        }
    }

    public static string ResolveDefaultRuntimeDirectory()
    {
        return Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, DefaultRelativeRuntimeDirectory));
    }

    public string ResolveServerJarPath() => Path.Combine(RuntimeDirectory, ServerJarFileName);

    public string ResolveClasspath()
    {
        // Windows classpath: jar;libs/*
        var jar = ResolveServerJarPath();
        var libs = Path.Combine(RuntimeDirectory, "libs", "*");
        return $"{jar};{libs}";
    }
}
