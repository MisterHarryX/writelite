using System.IO;
using System.Windows;
using System.Windows.Threading;
using System.Windows.Automation;
using WriteLite.Models;
using WriteLite.Resources;
using WriteLite.Services;
using WriteLite.Services.Rules;
using WriteLite.Services.Ai;
using WriteLite.Services.Audio;
using WriteLite.Services.Diagnostics;
using WriteLite.Services.Grammar;
using WriteLite.Services.LanguageEngine;
using WriteLite.Services.Lexical;
using WriteLite.Services.Notes;
using WriteLite.Services.Reading;
using WriteLite.Language.Core;
using WriteLite.Language.Packs;
using WriteLite.Services.Settings;
using WriteLite.Services.Spelling;
using WriteLite.Views;
using MessageBox = System.Windows.MessageBox;

namespace WriteLite;

public partial class App : System.Windows.Application
{
    private WriteLiteOrchestratingAnalyzer? _orchestrator;

    private HybridTextAnalysisService? _hybrid;


    /// <summary>
    /// The native deterministic lane, shared by the field monitor and the editor.
    /// </summary>
    /// <remarks>
    /// One instance for both surfaces: it holds a rule catalogue and a reference to the form
    /// index, and a second copy would parse the catalogue again to answer the same questions.
    /// </remarks>
    private CompositeTextAnalyzer? _fastAnalyzer;

    private WritingAssistanceService? _writingAssistance;

    private WritingAssistanceCoordinator? _writingCoordinator;

    private LocalAiRoutingPolicy? _routingPolicy;

    private WriteLiteLanguageEngine? _languageEngine;

    private IAiTextProvider? _aiProvider;

    private LocalAiTextProvider? _localAiProvider;

    private string ResolveEngineStatusText()
    {
        var local = _languageEngine?.UserFacingStatus ?? Strings.App_EngineFallbackStatus;

        // Technical backend: Qwen / LanguageTool / Lite fallback
        var backend = ResolveActiveBackendLabel();
        if (!string.IsNullOrEmpty(backend))
        {
            local += " · Backend: " + backend;
        }

        return local;
    }


    /// <summary>
    /// The backend tag shown beside the engine status, in WriteAI vocabulary.
    /// </summary>
    /// <remarks>
    /// The branching this replaced produced six different strings from three transport
    /// identifiers and two booleans, and four of the six said «Qwen» to the user.
    /// <see cref="WriteAiStatus.BackendLabel"/> is now the single place that decides.
    /// </remarks>
    private string ResolveActiveBackendLabel()
    {
        if (_settings.WriteAiEnabled && _localAiProvider is { IsConfigured: true })
        {
            var last = _localAiProvider.LastBackend;
            var available = _localAiProvider.WriteAiAvailable;
            if (!string.IsNullOrEmpty(last))
            {
                return WriteAiStatus.BackendLabel(last, available);
            }

            if (available && _settings.PreferWriteAi
                && !_settings.LocalAiProfile.Equals("Lite", StringComparison.OrdinalIgnoreCase))
            {
                return WriteAiStatus.ProductName;
            }
        }

        if (_settings.ExtendedChecking
            && _languageEngine is not null
            && !string.IsNullOrWhiteSpace(_languageEngine.UserFacingStatus)
            && !_languageEngine.UserFacingStatus.Contains("базовая", StringComparison.OrdinalIgnoreCase))
        {
            return "LanguageTool";
        }

        return "Lite fallback";
    }


    private static int ResolveAnalysisDelayMs(WriteLiteAppSettings settings, IAiTextProvider? provider)
    {
        // Local Lite is fast — keep monitor debounce closer to normal typing delay.
        if (settings.LocalAiEnabled && (provider?.IsConfigured ?? false))
        {
            return Math.Max(settings.AnalysisDelayMs, settings.AiDebounceMs);
        }

        return settings.AnalysisDelayMs;
    }


    private static LocalAiTextProvider CreateLocalAiProvider(WriteLiteAppSettings settings)
    {
        var profile = ParseLocalProfile(settings.LocalAiProfile);
        var modelDir = Path.Combine(AppContext.BaseDirectory, "models", "writelight-qwen");
        var endpoint = string.IsNullOrWhiteSpace(settings.QwenEndpoint)
            ? "http://127.0.0.1:8742"
            : settings.QwenEndpoint.Trim().TrimEnd('/');

        CompatibilityLogger.Technical(
            "local-ai-provider-create",
            $"enabled={(settings.LocalAiEnabled ? 1 : 0)} profile={profile} preferQwen={(settings.PreferQwen ? 1 : 0)} " +
            $"endpoint={endpoint} modelDirExists={(Directory.Exists(modelDir) ? 1 : 0)}");

        return new LocalAiTextProvider(new LocalAiOptions
        {
            Enabled = settings.LocalAiEnabled,
            Profile = profile,
            ModelDirectory = modelDir,
            QwenEndpoint = endpoint,
            PreferQwen = settings.PreferQwen,
            TimeoutSeconds = 90,
            MaxInputChars = settings.AiMaxTextLength,
            // Allow AI on typical sentences; hybrid still has its own min length gate.
            MinInputChars = 1
        });
    }


    private static WriteLite.AI.Contracts.AiModelProfile ParseLocalProfile(string? value)
        => (value ?? "Auto").Trim().ToLowerInvariant() switch
        {
            "lite" => WriteLite.AI.Contracts.AiModelProfile.Lite,
            "standard" => WriteLite.AI.Contracts.AiModelProfile.Standard,
            "quality" => WriteLite.AI.Contracts.AiModelProfile.Quality,
            _ => WriteLite.AI.Contracts.AiModelProfile.Auto
        };


    private void RebuildAiProvider(WriteLiteAppSettings settings)
    {
        try
        {
            if (_aiProvider is IDisposable d)
            {
                d.Dispose();
            }
            else
            {
                _localAiProvider?.Dispose();
            }
        }
        catch
        {
            // ignore
        }

        _localAiProvider = CreateLocalAiProvider(settings);
        _aiProvider = _localAiProvider;
        _hybrid?.SetAiService(new AiTextAnalysisService(_aiProvider));
        _hybrid?.ApplySettings(settings);
        if (settings.LocalAiEnabled)
        {
            _ = WarmupLocalAiAndRefreshStatusAsync();
        }
    }


    private async Task WarmupLocalAiAndRefreshStatusAsync()
    {
        try
        {
            if (_localAiProvider is null)
            {
                return;
            }

            // Task.Run is load-bearing, not defensive. An async method runs
            // synchronously until its first real await, and this chain reaches
            // QwenModelBackend.RefreshAvailability — a fully synchronous method
            // that blocks on an HTTP health probe to 127.0.0.1:8742 — before any
            // await. Called directly from OnStartup, that executed the whole
            // probe sequence on the dispatcher: a dump taken inside the hang
            // shows OnStartup → WarmupAsync → ProbeHealth → GetResult parking
            // the UI thread ~10 s at every launch while llama-server was not up
            // yet. The worker thread eats that wait instead.
            // Availability only. Starting the model server here would hold ~481 MB
            // resident for every session, including the many in which no Smart Action is
            // ever invoked — §34. It starts on the first request that needs it.
            await Task.Run(() => _localAiProvider.WarmupAsync(startBackend: false))
                .ConfigureAwait(true);
            CompatibilityLogger.Technical(
                "local-ai-warmup-done",
                $"qwenAvailable={(_localAiProvider.QwenAvailable ? 1 : 0)} endpoint={_localAiProvider.QwenEndpoint}");
        }
        catch (Exception ex)
        {
            CompatibilityLogger.Technical("local-ai-warmup-failed", $"type={ex.GetType().Name}");
        }

        try
        {
            _ = Dispatcher.BeginInvoke(() => _mainWindow?.SetEngineStatus(ResolveEngineStatusText()));
        }
        catch
        {
            // ignore
        }
    }


    private async Task WarmupLexicalKnowledgeAsync(OfflineLexicalKnowledgeService service)
    {
        try
        {
            // The open dictionary contains tens of thousands of lemmas. Parsing it
            // off the dispatcher keeps tray startup and the first keystroke smooth.
            await Task.Run(
                () => service.LoadFromDirectory(),
                _applicationLifetimeCts.Token).ConfigureAwait(true);
            CompatibilityLogger.Technical(
                "lexical-warmup-done",
                $"loaded={(service.IsPackLoaded ? 1 : 0)} version={service.PackVersion ?? "none"}");
        }
        catch (OperationCanceledException)
        {
            // Normal application shutdown.
        }
        catch (Exception ex)
        {
            CompatibilityLogger.Technical("lexical-warmup-failed", $"type={ex.GetType().Name}");
        }
    }


    private void OnLanguageEngineStatusChanged(object? sender, EventArgs e)
    {
        // Engine shutdown and the WPF dispatcher can race.  Status updates are
        // optional UI work and must never turn a successful shutdown into a
        // dispatcher exception.
        if (Dispatcher.HasShutdownStarted || Dispatcher.HasShutdownFinished)
        {
            return;
        }

        try
        {
            _ = Dispatcher.BeginInvoke(() => _mainWindow?.SetEngineStatus(ResolveEngineStatusText()));
        }
        catch (Exception exception)
        {
            CompatibilityLogger.AccessError("engine-status-dispatch", null, exception);
        }
    }


}
