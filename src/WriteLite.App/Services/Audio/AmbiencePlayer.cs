using System.IO;
using System.Windows.Media;
using System.Windows.Threading;

namespace WriteLite.Services.Audio;

/// <summary>One playable track.</summary>
/// <param name="Path">Absolute path on disk.</param>
/// <param name="Title">Display title, from the file name.</param>
/// <param name="Artist">Display artist, or null when the file name does not carry one.</param>
public sealed record AmbienceTrack(string Path, string Title, string? Artist);

/// <summary>
/// The ambience player behind the rail's transport bar.
/// </summary>
/// <remarks>
/// The website has one looping soundscape behind a single toggle. That track ships
/// here as the built-in one, so the desktop opens with the same sound the site plays,
/// and anything the user drops into their audio folder joins the same list — which is
/// what makes previous and next mean something rather than being decoration.
///
/// Built on WPF's <see cref="MediaPlayer"/>: it is already in the framework, decodes
/// off the UI thread, and costs nothing while stopped. No audio library is worth
/// adding to a writing tool for a background player.
///
/// Position is polled four times a second rather than watched per frame. The progress
/// bar is 2 px tall; nobody can see the difference, and a per-frame timer on a machine
/// that is simultaneously analysing text in another application is exactly the kind of
/// cost this app's responsiveness watchdog exists to catch.
/// </remarks>
public sealed class AmbiencePlayer : IDisposable
{
    private static readonly string[] SupportedExtensions = [".mp3", ".wav", ".m4a", ".wma", ".flac"];

    private readonly MediaPlayer _player = new();
    private readonly DispatcherTimer _ticker;
    private readonly List<AmbienceTrack> _tracks = [];

    private int _index = -1;
    private bool _isPlaying;
    private bool _isLooping;
    private bool _disposed;

    public AmbiencePlayer()
    {
        _ticker = new DispatcherTimer(DispatcherPriority.Background)
        {
            Interval = TimeSpan.FromMilliseconds(250)
        };
        _ticker.Tick += (_, _) => ProgressChanged?.Invoke();

        _player.MediaEnded += OnMediaEnded;
        _player.MediaFailed += OnMediaFailed;
        _player.MediaOpened += (_, _) => ProgressChanged?.Invoke();
    }

    /// <summary>Raised when the current track or the playing state changes.</summary>
    public event Action? StateChanged;

    /// <summary>Raised roughly four times a second while playing.</summary>
    public event Action? ProgressChanged;

    public IReadOnlyList<AmbienceTrack> Tracks => _tracks;

    public AmbienceTrack? Current => _index >= 0 && _index < _tracks.Count ? _tracks[_index] : null;

    public bool IsPlaying => _isPlaying;

    public bool HasTracks => _tracks.Count > 0;

    /// <summary>True when previous and next lead anywhere — a single track makes them inert.</summary>
    public bool CanSkip => _tracks.Count > 1;

    /// <summary>
    /// Whether the current track repeats instead of handing over to the next one.
    /// </summary>
    /// <remarks>
    /// Repeat-one rather than repeat-all: the list already wraps at the end, so
    /// "repeat everything" is what the player does anyway. What it could not do before
    /// is stay on one piece — which is the whole point of putting a particular track on
    /// while writing.
    /// </remarks>
    public bool IsLooping
    {
        get => _isLooping;
        set
        {
            if (_isLooping == value)
            {
                return;
            }

            _isLooping = value;
            StateChanged?.Invoke();
        }
    }

    public double Volume
    {
        get => _player.Volume;
        set => _player.Volume = Math.Clamp(value, 0, 1);
    }

    public TimeSpan Position => _player.Position;

    public TimeSpan Duration =>
        _player.NaturalDuration.HasTimeSpan ? _player.NaturalDuration.TimeSpan : TimeSpan.Zero;

    /// <summary>Fraction played, 0–1. Zero while the duration is still unknown.</summary>
    public double Progress
    {
        get
        {
            var duration = Duration;
            return duration > TimeSpan.Zero
                ? Math.Clamp(Position.TotalSeconds / duration.TotalSeconds, 0, 1)
                : 0;
        }
    }

    /// <summary>
    /// Builds the track list: the bundled soundscape first, then the user's own files.
    /// </summary>
    /// <remarks>
    /// Safe to call on a background thread — it only touches the file system — and safe
    /// to call when neither location exists, which simply leaves the player empty and
    /// the bar hidden.
    /// </remarks>
    public void LoadTracks(string? bundledDirectory = null, string? userDirectory = null)
    {
        bundledDirectory ??= Path.Combine(AppContext.BaseDirectory, "resources", "audio");
        userDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WriteLite", "audio");

        var found = new List<AmbienceTrack>();
        Collect(bundledDirectory, found);
        Collect(userDirectory, found);

        _tracks.Clear();
        _tracks.AddRange(found);

        if (_index >= _tracks.Count)
        {
            _index = _tracks.Count > 0 ? 0 : -1;
        }
        else if (_index < 0 && _tracks.Count > 0)
        {
            _index = 0;
        }

        CompatibilityLogger.Technical("ambience-tracks", $"count={_tracks.Count}");
        StateChanged?.Invoke();
    }

    private static void Collect(string directory, List<AmbienceTrack> into)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var files = Directory
                .EnumerateFiles(directory, "*", SearchOption.TopDirectoryOnly)
                .Where(path => SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
                .OrderBy(Path.GetFileName, StringComparer.CurrentCultureIgnoreCase);

            foreach (var file in files)
            {
                into.Add(Describe(file));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CompatibilityLogger.Technical("ambience-scan-failed", $"type={exception.GetType().Name}");
        }
    }

    /// <summary>
    /// Turns a file name into a title and, where the name says so, an artist.
    /// </summary>
    /// <remarks>
    /// File names, not embedded tags. Reading ID3 would mean a media library
    /// dependency, and the convention below — "Artist - Title" — is the one people
    /// already name music files with. A name that does not follow it is shown whole
    /// rather than being guessed at.
    /// </remarks>
    private static AmbienceTrack Describe(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        var separator = name.IndexOf(" - ", StringComparison.Ordinal);

        if (separator > 0 && separator < name.Length - 3)
        {
            return new AmbienceTrack(path, Humanize(name[(separator + 3)..]), Humanize(name[..separator]));
        }

        return new AmbienceTrack(path, Humanize(name), null);
    }

    private static string Humanize(string value)
    {
        var spaced = value.Replace('_', ' ').Replace('-', ' ').Trim();
        return spaced.Length == 0
            ? value
            : char.ToUpper(spaced[0], System.Globalization.CultureInfo.CurrentCulture) + spaced[1..];
    }

    public void TogglePlayPause()
    {
        if (_isPlaying)
        {
            Pause();
        }
        else
        {
            Play();
        }
    }

    public void Play()
    {
        if (_disposed || Current is null)
        {
            return;
        }

        try
        {
            // Opening is deferred until the first play so that launching WriteLite
            // never touches the audio device: most sessions never start the player.
            if (_player.Source is null)
            {
                _player.Open(new Uri(Current.Path, UriKind.Absolute));
            }

            _player.Play();
            _isPlaying = true;
            _ticker.Start();
            StateChanged?.Invoke();
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("ambience-play-failed", $"type={exception.GetType().Name}");
            Stop();
        }
    }

    public void Pause()
    {
        if (_disposed)
        {
            return;
        }

        _player.Pause();
        _isPlaying = false;
        _ticker.Stop();
        StateChanged?.Invoke();
    }

    public void Next() => Skip(1);

    public void Previous()
    {
        // Restart the current track when it is more than three seconds in, before
        // stepping back a track. Every music player behaves this way and hands are
        // trained on it.
        if (Position > TimeSpan.FromSeconds(3))
        {
            _player.Position = TimeSpan.Zero;
            ProgressChanged?.Invoke();
            return;
        }

        Skip(-1);
    }

    private void Skip(int delta)
    {
        if (_tracks.Count == 0)
        {
            return;
        }

        _index = ((_index + delta) % _tracks.Count + _tracks.Count) % _tracks.Count;
        SwitchToCurrent();
    }

    private void SwitchToCurrent()
    {
        if (Current is null)
        {
            return;
        }

        var wasPlaying = _isPlaying;
        _player.Stop();
        _player.Close();

        try
        {
            _player.Open(new Uri(Current.Path, UriKind.Absolute));
        }
        catch (Exception exception)
        {
            CompatibilityLogger.Technical("ambience-open-failed", $"type={exception.GetType().Name}");
            Stop();
            return;
        }

        if (wasPlaying)
        {
            _player.Play();
        }

        StateChanged?.Invoke();
        ProgressChanged?.Invoke();
    }

    private void Stop()
    {
        _isPlaying = false;
        _ticker.Stop();
        StateChanged?.Invoke();
    }

    /// <summary>
    /// A finished track repeats when repeat is on, otherwise hands over to the next one.
    /// </summary>
    /// <remarks>
    /// Ambience that stops after eight minutes is worse than no ambience: the silence
    /// arrives unannounced, in the middle of writing, and asks to be dealt with. So the
    /// player never simply stops — it either repeats the piece or moves on, and a lone
    /// track repeats regardless because there is nowhere to move on to.
    /// </remarks>
    private void OnMediaEnded(object? sender, EventArgs e)
    {
        if (_isLooping || _tracks.Count <= 1)
        {
            _player.Position = TimeSpan.Zero;
            _player.Play();
            return;
        }

        Skip(1);
    }

    private void OnMediaFailed(object? sender, ExceptionEventArgs e)
    {
        CompatibilityLogger.Technical("ambience-media-failed", $"type={e.ErrorException?.GetType().Name}");
        Stop();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ticker.Stop();
        _player.MediaEnded -= OnMediaEnded;
        _player.MediaFailed -= OnMediaFailed;

        try
        {
            _player.Stop();
            _player.Close();
        }
        catch (Exception exception) when (exception is InvalidOperationException or NullReferenceException)
        {
            // The media session may already be gone during shutdown.
        }
    }
}
