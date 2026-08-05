using System.Net.Http;
using System.Text;
using System.Text.Json;

namespace WriteLite.Services.LanguageEngine;

/// <summary>
/// HTTP client for the local offline language engine (127.0.0.1 only).
/// Reuses a single HttpClient instance for the client lifetime.
/// </summary>
public sealed class WriteLiteLanguageClient : IWriteLiteLanguageClient, IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        // Unknown JSON fields are ignored by default System.Text.Json behavior.
    };

    private readonly WriteLiteLanguageOptions _options;
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;
    private readonly string _baseAddress;

    public WriteLiteLanguageClient(WriteLiteLanguageOptions? options = null, HttpClient? httpClient = null)
        : this(options, httpClient, baseAddress: null)
    {
    }

    public WriteLiteLanguageClient(
        WriteLiteLanguageOptions options,
        string baseAddress,
        HttpClient? httpClient = null)
        : this(options, httpClient, baseAddress)
    {
    }

    private WriteLiteLanguageClient(
        WriteLiteLanguageOptions? options,
        HttpClient? httpClient,
        string? baseAddress)
    {
        _options = options ?? new WriteLiteLanguageOptions();

        if (!string.IsNullOrWhiteSpace(baseAddress))
        {
            _baseAddress = baseAddress.TrimEnd('/');
        }
        else if (_options.BoundPort > 0)
        {
            _baseAddress = $"http://{_options.HostAddress}:{_options.BoundPort}";
        }
        else
        {
            _baseAddress = $"http://{_options.HostAddress}:{_options.PreferredPort}";
        }

        WriteLiteLoopbackGuard.EnsureAllowed(_baseAddress);

        if (httpClient is null)
        {
            _http = new HttpClient { BaseAddress = new Uri(_baseAddress + "/") };
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
            _ownsHttp = false;
        }
    }

    /// <summary>Exposed for tests — same HttpClient instance across CheckAsync calls.</summary>
    public HttpClient SharedHttpClient => _http;

    public async Task<WriteLiteLanguageResponseDto> CheckAsync(
        string text,
        string? language = null,
        CancellationToken cancellationToken = default)
    {
        if (!_options.EnableEngine)
        {
            CompatibilityLogger.State("language-engine-unavailable");
            throw new WriteLiteLanguageEngineException(
                "engine-disabled",
                "Local language engine is disabled.");
        }

        WriteLiteLoopbackGuard.EnsureAllowed(_baseAddress);

        if (string.IsNullOrEmpty(text))
        {
            return new WriteLiteLanguageResponseDto();
        }

        if (text.Length > _options.MaxTextLength)
        {
            throw new WriteLiteLanguageEngineException(
                "text-too-long",
                "Text exceeds the configured maximum length for local analysis.");
        }

        var lang = string.IsNullOrWhiteSpace(language) ? _options.Language : language.Trim();
        var url = _baseAddress.TrimEnd('/') + "/v2/check";

        CompatibilityLogger.State("language-engine-request-started");
        CompatibilityLogger.Technical(
            "language-engine-request-started",
            $"port={ExtractPort(_baseAddress)} textLength={text.Length} language={SanitizeToken(lang)}");

        var form = new Dictionary<string, string>
        {
            ["language"] = lang,
            ["text"] = text
        };

        using var content = new FormUrlEncodedContent(form);
        // Ensure form encoding Content-Type
        content.Headers.ContentType =
            new System.Net.Http.Headers.MediaTypeHeaderValue("application/x-www-form-urlencoded");

        using var request = new HttpRequestMessage(HttpMethod.Post, url) { Content = content };
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(_options.RequestTimeout);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            using var response = await _http
                .SendAsync(request, HttpCompletionOption.ResponseContentRead, timeoutCts.Token)
                .ConfigureAwait(false);

            var body = await response.Content.ReadAsStringAsync(timeoutCts.Token).ConfigureAwait(false);
            sw.Stop();

            if (!response.IsSuccessStatusCode)
            {
                CompatibilityLogger.State("language-engine-request-failed");
                CompatibilityLogger.Technical(
                    "language-engine-request-failed",
                    $"port={ExtractPort(_baseAddress)} status={(int)response.StatusCode} elapsedMs={sw.ElapsedMilliseconds} errorCode=http-error");
                throw new WriteLiteLanguageEngineException(
                    "http-error",
                    $"Local language engine returned status {(int)response.StatusCode}.");
            }

            WriteLiteLanguageResponseDto dto;
            try
            {
                dto = ParseResponseJson(body);
            }
            catch (WriteLiteLanguageEngineException)
            {
                CompatibilityLogger.State("language-engine-request-failed");
                throw;
            }

            var matchCount = dto.Matches?.Count ?? 0;
            CompatibilityLogger.State("language-engine-request-completed");
            CompatibilityLogger.Technical(
                "language-engine-request-completed",
                $"port={ExtractPort(_baseAddress)} textLength={text.Length} matchCount={matchCount} elapsedMs={sw.ElapsedMilliseconds}");

            return dto;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            CompatibilityLogger.State("language-engine-timeout");
            CompatibilityLogger.Technical(
                "language-engine-timeout",
                $"port={ExtractPort(_baseAddress)} textLength={text.Length} elapsedMs={sw.ElapsedMilliseconds}");
            throw new WriteLiteLanguageEngineException(
                "request-timeout",
                "Local language engine request timed out.");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (HttpRequestException ex)
        {
            CompatibilityLogger.State("language-engine-unavailable");
            CompatibilityLogger.Technical(
                "language-engine-request-failed",
                $"port={ExtractPort(_baseAddress)} errorCode=http-unavailable elapsedMs={sw.ElapsedMilliseconds}");
            throw new WriteLiteLanguageEngineException(
                "http-unavailable",
                "Local language engine is unavailable.",
                ex);
        }
        catch (WriteLiteLanguageEngineException)
        {
            throw;
        }
        catch (Exception ex)
        {
            CompatibilityLogger.State("language-engine-request-failed");
            CompatibilityLogger.AccessError("language-engine-request", null, ex);
            throw new WriteLiteLanguageEngineException(
                "request-failed",
                "Local language engine request failed.",
                ex);
        }
    }

    public static WriteLiteLanguageResponseDto ParseResponseJson(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            throw new WriteLiteLanguageEngineException("invalid-json", "Empty language engine response.");
        }

        try
        {
            return JsonSerializer.Deserialize<WriteLiteLanguageResponseDto>(json, JsonOptions)
                   ?? new WriteLiteLanguageResponseDto();
        }
        catch (JsonException ex)
        {
            throw new WriteLiteLanguageEngineException(
                "invalid-json",
                "Local language engine returned an unreadable response.",
                ex);
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }

    private static int ExtractPort(string baseAddress)
    {
        return Uri.TryCreate(baseAddress, UriKind.Absolute, out var uri) ? uri.Port : 0;
    }

    private static string SanitizeToken(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return "unknown";
        }

        var sb = new StringBuilder(value.Length);
        foreach (var ch in value)
        {
            sb.Append(char.IsLetterOrDigit(ch) || ch is '-' or '_' ? ch : '_');
        }

        return sb.ToString();
    }
}
