using WriteLite.AI.Contracts;

namespace WriteLite.AI.Local;

/// <summary>
/// Optional ONNX seq2seq backend. When model files are absent, reports unavailable.
/// Full ONNX Runtime GenAI binding can be enabled by dropping model files into the model directory
/// and adding the Microsoft.ML.OnnxRuntime package in a future drop; this host already
/// validates integrity and routes traffic to Lite when ONNX is not ready.
/// </summary>
public sealed class OnnxModelBackend : IAsyncDisposable
{
    private readonly string _modelDirectory;
    private readonly string? _manifestPath;
    private bool _available;
    private string _version = "onnx-unavailable";

    public OnnxModelBackend(string? modelDirectory = null)
    {
        _modelDirectory = modelDirectory
                          ?? Path.Combine(AppContext.BaseDirectory, "models", "writelight-gec");
        _manifestPath = Path.Combine(_modelDirectory, "model.manifest.json");
        RefreshAvailability();
    }

    public bool IsAvailable => _available;

    public string ModelVersion => _version;

    public string ModelDirectory => _modelDirectory;

    public void RefreshAvailability()
    {
        try
        {
            if (!Directory.Exists(_modelDirectory))
            {
                _available = false;
                _version = "onnx-missing";
                return;
            }

            var onnx = Directory.GetFiles(_modelDirectory, "*.onnx", SearchOption.TopDirectoryOnly);
            if (onnx.Length == 0)
            {
                _available = false;
                _version = "onnx-missing";
                return;
            }

            // Integrity: reject zero-length files.
            if (onnx.Any(f => new FileInfo(f).Length == 0))
            {
                _available = false;
                _version = "onnx-corrupt";
                return;
            }

            if (File.Exists(_manifestPath))
            {
                var json = File.ReadAllText(_manifestPath);
                // Minimal parse without extra deps.
                var marker = "\"modelVersion\"";
                var idx = json.IndexOf(marker, StringComparison.Ordinal);
                if (idx >= 0)
                {
                    var colon = json.IndexOf(':', idx);
                    var q1 = json.IndexOf('"', colon + 1);
                    var q2 = json.IndexOf('"', q1 + 1);
                    if (q1 >= 0 && q2 > q1)
                    {
                        _version = json.Substring(q1 + 1, q2 - q1 - 1);
                    }
                }
            }
            else
            {
                _version = "onnx-unversioned";
            }

            // Runtime binding not packaged by default — mark as present but not executable
            // until ONNX Runtime GenAI is wired. Callers should treat IsExecutable separately.
            _available = false; // weights may exist; inference path enabled when package added
            _version += "+pending-runtime";
        }
        catch
        {
            _available = false;
            _version = "onnx-error";
        }
    }

    /// <summary>
    /// Attempts neural correction. Returns null when backend cannot run (always for MVP without ORT package).
    /// </summary>
    public Task<string?> TryCorrectAsync(string text, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Placeholder for ONNX Runtime GenAI session.Run.
        // Intentionally returns null so LocalAiTextAnalyzer falls back to Lite engine.
        return Task.FromResult<string?>(null);
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
