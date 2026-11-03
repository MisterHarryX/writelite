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
    public static readonly string[] SupportedExtensions = [".mp3", ".wav", ".m4a", ".wma", ".flac", ".aac", ".ogg", ".opus"];

    private const int MaxLibraryTracks = 5000;
    private readonly Random _random = new();
    private readonly List<int> _history = [];
    private bool _shuffle;

    /// <summary>The user's library (folders, files, favourites, where playback stopped).</summary>
    public MusicLibrary Library { get; private set; } = new();

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
            Interval = WriteLiteDefaults.Audio.AmbienceTickerInterval
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
    public void LoadTracks(string? bundledDirectory = null, string? userDirectory = null, MusicLibrary? library = null)
    {
        bundledDirectory ??= Path.Combine(AppContext.BaseDirectory, "resources", "audio");
        userDirectory ??= Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "WriteLite", "audio");
        Library = library ?? Library;

        var found = new List<AmbienceTrack>();
        Collect(bundledDirectory, found);
        Collect(userDirectory, found);
        foreach (var folder in Library.Folders)
        {
            Collect(folder, found, recursive: true);
        }

        foreach (var file in Library.Files)
        {
            if (File.Exists(file) && IsSupported(file)) found.Add(Describe(file));
        }

        var unique = found
            .GroupBy(t => t.Path, StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .Take(MaxLibraryTracks)
            .ToList();

        var currentPath = Current?.Path ?? Library.LastTrack;
        _tracks.Clear();
        _tracks.AddRange(unique);
        _index = currentPath is null ? -1 : _tracks.FindIndex(t => string.Equals(t.Path, currentPath, StringComparison.OrdinalIgnoreCase));
        if (_index < 0 && _tracks.Count > 0) _index = 0;
        _shuffle = Library.Shuffle;

        CompatibilityLogger.Technical("ambience-tracks", $"count={_tracks.Count}");
        StateChanged?.Invoke();
    }

    public static bool IsSupported(string path) =>
        SupportedExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    // ── Library editing ────────────────────────────────────────────────────────

    /// <summary>Adds files (and folders dropped among them); returns how many tracks were new.</summary>
    public int AddFiles(IEnumerable<string> paths)
    {
        var added = 0;
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                added += AddFolder(path);
                continue;
            }

            if (!File.Exists(path) || !IsSupported(path)) continue;
            if (_tracks.Any(t => string.Equals(t.Path, path, StringComparison.OrdinalIgnoreCase))) continue;
            Library.Files.Add(path);
            _tracks.Add(Describe(path));
            added++;
        }

        if (_index < 0 && _tracks.Count > 0) _index = 0;
        if (added > 0) SaveLibrary();
        return added;
    }

    /// <summary>Adds a folder (with its subfolders); returns how many tracks it brought.</summary>
    public int AddFolder(string folder)
    {
        if (!Directory.Exists(folder)) return 0;
        if (!Library.Folders.Contains(folder, StringComparer.OrdinalIgnoreCase)) Library.Folders.Add(folder);
        var found = new List<AmbienceTrack>();
        Collect(folder, found, recursive: true);
        var added = 0;
        foreach (var track in found)
        {
            if (_tracks.Count >= MaxLibraryTracks) break;
            if (_tracks.Any(t => string.Equals(t.Path, track.Path, StringComparison.OrdinalIgnoreCase))) continue;
            _tracks.Add(track);
            added++;
        }

        if (_index < 0 && _tracks.Count > 0) _index = 0;
        SaveLibrary();
        return added;
    }

    /// <summary>Takes a track off the list (the file itself is not touched).</summary>
    public void Remove(AmbienceTrack track)
    {
        var at = _tracks.IndexOf(track);
        if (at < 0) return;
        Library.Files.RemoveAll(f => string.Equals(f, track.Path, StringComparison.OrdinalIgnoreCase));
        Library.Favorites.RemoveAll(f => string.Equals(f, track.Path, StringComparison.OrdinalIgnoreCase));
        var wasCurrent = at == _index;
        _tracks.RemoveAt(at);
        if (at < _index) _index--;
        if (wasCurrent)
        {
            _index = Math.Min(_index, _tracks.Count - 1);
            if (_index >= 0) SwitchToCurrent();
            else Stop();
        }

        SaveLibrary();
    }

    public bool IsFavorite(AmbienceTrack track) =>
        Library.Favorites.Contains(track.Path, StringComparer.OrdinalIgnoreCase);

    public void ToggleFavorite(AmbienceTrack track)
    {
        if (IsFavorite(track)) Library.Favorites.RemoveAll(f => string.Equals(f, track.Path, StringComparison.OrdinalIgnoreCase));
        else Library.Favorites.Add(track.Path);
        SaveLibrary();
    }

    /// <summary>Remembers the library and where playback is, so the next start continues from there.</summary>
    public void SaveLibrary()
    {
        Library.LastTrack = Current?.Path;
        Library.LastPositionSeconds = _player.Source is null ? Library.LastPositionSeconds : Position.TotalSeconds;
        Library.Shuffle = _shuffle;
        Library.Save();
        StateChanged?.Invoke();
    }

    // ── Playback control ───────────────────────────────────────────────────────

    public bool IsShuffle
    {
        get => _shuffle;
        set
        {
            if (_shuffle == value) return;
            _shuffle = value;
            Library.Shuffle = value;
            StateChanged?.Invoke();
        }
    }

    /// <summary>Plays a particular track from the list.</summary>
    public void PlayTrack(AmbienceTrack track)
    {
        var at = _tracks.IndexOf(track);
        if (at < 0) return;
        if (at == _index && _player.Source is not null)
        {
            if (!_isPlaying) Play();
            return;
        }

        _history.Add(_index);
        _index = at;
        _isPlaying = true;
        SwitchToCurrent();
        _ticker.Start();
    }

    /// <summary>Jumps to a fraction (0–1) of the current track.</summary>
    public void Seek(double fraction)
    {
        var duration = Duration;
        if (duration <= TimeSpan.Zero) return;
        _player.Position = TimeSpan.FromSeconds(duration.TotalSeconds * Math.Clamp(fraction, 0, 1));
        ProgressChanged?.Invoke();
    }

    /// <summary>Puts the remembered track at the remembered second, without starting it.</summary>
    public void RestoreLastPosition()
    {
        if (Current is null || Library.LastTrack is null
            || !string.Equals(Current.Path, Library.LastTrack, StringComparison.OrdinalIgnoreCase)
            || Library.LastPositionSeconds < 1)
        {
            return;
        }

        try
        {
            if (_player.Source is null) _player.Open(new Uri(Current.Path, UriKind.Absolute));
            _player.Position = TimeSpan.FromSeconds(Library.LastPositionSeconds);
            ProgressChanged?.Invoke();
        }
        catch (Exception ex)
        {
            CompatibilityLogger.Technical("ambience-restore-failed", ex);
        }
    }

    private static void Collect(string directory, List<AmbienceTrack> into, bool recursive = false)
    {
        try
        {
            if (!Directory.Exists(directory))
            {
                return;
            }

            var files = Directory
                .EnumerateFiles(directory, "*", new EnumerationOptions
                {
                    RecurseSubdirectories = recursive,
                    IgnoreInaccessible = true,
                    MaxRecursionDepth = 6,
                })
                .Where(IsSupported)
                .Take(MaxLibraryTracks)
                .OrderBy(path => path, StringComparer.CurrentCultureIgnoreCase);

            foreach (var file in files)
            {
                into.Add(Describe(file));
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            CompatibilityLogger.Technical("ambience-scan-failed", exception);
        }
    }

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
            CompatibilityLogger.Technical("ambience-play-failed", exception);
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
        if (Position > WriteLiteDefaults.Audio.AmbienceRestartThreshold)
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

        if (_shuffle && _tracks.Count > 2)
        {
            if (delta < 0 && _history.Count > 0)
            {
                _index = Math.Clamp(_history[^1], 0, _tracks.Count - 1);
                _history.RemoveAt(_history.Count - 1);
            }
            else
            {
                _history.Add(_index);
                if (_history.Count > 50) _history.RemoveAt(0);
                int next;
                do
                {
                    next = _random.Next(_tracks.Count);
                }
                while (next == _index);
                _index = next;
            }
        }
        else
        {
            _index = ((_index + delta) % _tracks.Count + _tracks.Count) % _tracks.Count;
        }

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
            CompatibilityLogger.Technical("ambience-open-failed", exception);
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
        CompatibilityLogger.Technical("ambience-media-failed", e.ErrorException);
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
