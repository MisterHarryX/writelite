using System.IO;

namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// Configuration for the offline WriteLite language engine host and client.
/// </summary>
public sealed class WriteLiteLanguageOptions
{
    public const string DefaultRelativeRuntimeDirectory = @"ThirdParty\LanguageEngine\6.4";
    public const string DefaultHostAddress = "127.0.0.1";
    public const string DefaultLanguage = "ru-RU";

    /// <summary>Absolute path to the engine runtime folder (contains server JAR and libs).</summary>
    public string RuntimeDirectory { get; set; } = ResolveDefaultRuntimeDirectory();

    /// <summary>Optional explicit path to java.exe / javaw.exe.</summary>
    public string? ConfiguredJavaPath { get; set; }

    /// <summary>Must remain 127.0.0.1 — external hosts are rejected by the client guard.</summary>
    public string HostAddress { get; set; } = DefaultHostAddress;

    /// <summary>Preferred port; if occupied, host searches upward within PortSearchRange.</summary>
    public int PreferredPort { get; set; } = 18081;

    /// <summary>Alias used by host port binding; same as PreferredPort.</summary>
    public int Port
    {
        get => PreferredPort;
        set => PreferredPort = value;
    }

    /// <summary>How many ports after PreferredPort to try when binding.</summary>
    public int PortSearchRange { get; set; } = 40;

    public TimeSpan StartupTimeout { get; set; } = TimeSpan.FromSeconds(45);

    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(30);

    public TimeSpan ShutdownTimeout { get; set; } = TimeSpan.FromSeconds(5);

    public TimeSpan ReadyPollInterval { get; set; } = TimeSpan.FromMilliseconds(400);

    public string Language { get; set; } = DefaultLanguage;

    /// <summary>Reject client requests longer than this many characters.</summary>
    public int MaxTextLength { get; set; } = 20_000;

    /// <summary>Feature flag — when false, host/client refuse to start/check.</summary>
    public bool EnableEngine { get; set; } = false;

    public bool PreferJavaw { get; set; } = true;

    /// <summary>Java heap for the child process (-Xmx), e.g. 512m.</summary>
    public string JavaMaxHeap { get; set; } = "512m";

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
