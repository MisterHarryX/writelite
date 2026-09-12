using System.Net.Http;
using System.Text.Json;
using WriteLite.Language.Russian;

namespace WriteLite.Services.LanguageEngine;

public sealed class WriteLiteReadinessProbe : IWriteLiteReadinessProbe, IDisposable
{
    private readonly HttpClient _http;
    private readonly bool _ownsHttp;

    public WriteLiteReadinessProbe(HttpClient? httpClient = null)
    {
        if (httpClient is null)
        {
            _http = new HttpClient { Timeout = WriteLiteDefaults.Networking.LanguageEngineReadinessProbeTimeout };
            _ownsHttp = true;
        }
        else
        {
            _http = httpClient;
            _ownsHttp = false;
        }
    }

    public async Task<bool> IsReadyAsync(string baseAddress, CancellationToken cancellationToken = default)
    {
        WriteLiteLoopbackGuard.EnsureAllowed(baseAddress);

        var url = baseAddress.TrimEnd('/') + "/v2/languages";
        using var response = await _http.GetAsync(url, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            return false;
        }

        var body = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        return ContainsRussianLanguage(body);
    }

    /// <summary>
    /// Validates /v2/languages JSON contains a Russian language entry without logging the body.
    /// </summary>
    public static bool ContainsRussianLanguage(string json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array)
            {
                return false;
            }

            foreach (var item in doc.RootElement.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                if (item.TryGetProperty("code", out var codeEl))
                {
                    var code = codeEl.GetString();
                    if (!string.IsNullOrEmpty(code)
                        && code.StartsWith(RussianLanguageProfile.IsoCode, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }

                if (item.TryGetProperty("longCode", out var longCodeEl))
                {
                    var longCode = longCodeEl.GetString();
                    if (!string.IsNullOrEmpty(longCode)
                        && longCode.StartsWith(RussianLanguageProfile.IsoCode, StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }

            return false;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public void Dispose()
    {
        if (_ownsHttp)
        {
            _http.Dispose();
        }
    }
}
