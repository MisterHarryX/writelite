using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using WriteLite.Services.Audio;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;
using DataFormats = System.Windows.DataFormats;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace WriteLite.Views.Pages;

/// <summary>
/// «Музыка»: the full player behind the rail's mini player — now playing, seek, shuffle, repeat,
/// volume, and the library with search and favourites.
/// </summary>
/// <remarks>
/// The player itself is the one <see cref="AmbiencePlayer"/> of the process, so music keeps
/// playing on every page and while the window is hidden in the tray; this page only shows it.
/// The library (folders and files the user added) is remembered between runs, and so are the
/// track and the second where playback stopped.
/// </remarks>
public partial class MusicPage : UserControl
{
    private readonly ObservableCollection<TrackRow> _rows = [];
    private AmbiencePlayer? _player;
    private bool _seeking;
    private bool _syncing;
    private string _search = string.Empty;

    public MusicPage()
    {
        InitializeComponent();
        TrackList.ItemsSource = _rows;
    }

    /// <summary>Volume changes, so the shell can persist them with the other ambience settings.</summary>
    public event Action<double>? VolumeChanged;

    public void Bind(AmbiencePlayer player)
    {
        if (_player is not null)
        {
            _player.StateChanged -= OnStateChanged;
            _player.ProgressChanged -= OnProgress;
        }

        _player = player;
        _player.StateChanged += OnStateChanged;
        _player.ProgressChanged += OnProgress;
        Reload();
    }

    public void Reload()
    {
        RenderRows();
        RenderNowPlaying();
        OnProgress();
    }

    private void OnStateChanged() => Dispatcher.BeginInvoke(() =>
    {
        RenderNowPlaying();
        RefreshMarkers();
    });

    private void RenderRows()
    {
        _rows.Clear();
        if (_player is null) return;
        var favorites = TabFavorites.IsChecked == true;
        foreach (var track in _player.Tracks)
        {
            if (favorites && !_player.IsFavorite(track)) continue;
            if (_search.Length > 0
                && !(track.Title.Contains(_search, StringComparison.CurrentCultureIgnoreCase)
                     || (track.Artist?.Contains(_search, StringComparison.CurrentCultureIgnoreCase) ?? false)))
            {
                continue;
            }

            _rows.Add(new TrackRow(track, this));
        }

        EmptyState.Visibility = _player.Tracks.Count <= 2 && _search.Length == 0 && !favorites
            ? Visibility.Visible
            : Visibility.Collapsed;
        RefreshMarkers();
    }

    private void RefreshMarkers()
    {
        foreach (var row in _rows) row.Refresh();
    }

    private void RenderNowPlaying()
    {
        if (_player is null) return;
        _syncing = true;
        var current = _player.Current;
        NowTitle.Text = current?.Title ?? "Ничего не выбрано";
        NowArtist.Text = current?.Artist ?? (current is null ? "Добавьте музыку ниже" : "Неизвестный исполнитель");
        PlayIcon.Data = (Geometry)FindResource(_player.IsPlaying ? "WlIconPause" : "WlIconPlay");
        PlayButton.ToolTip = _player.IsPlaying ? "Пауза" : "Воспроизвести";
        System.Windows.Automation.AutomationProperties.SetName(PlayButton, (string)PlayButton.ToolTip);
        ShuffleButton.IsChecked = _player.IsShuffle;
        RepeatButton.IsChecked = _player.IsLooping;
        VolumeSlider.Value = _player.Volume;
        PaintArtwork(current);
        _syncing = false;
    }

    /// <summary>A colour per track, so the artwork changes with the music even without covers.</summary>
    private void PaintArtwork(AmbienceTrack? track)
    {
        var hash = (track?.Title ?? string.Empty).Aggregate(17, (h, c) => h * 31 + c);
        var hue = Math.Abs(hash % 360);
        ArtStop1.Color = FromHsl(hue, 0.78, 0.52);
        ArtStop2.Color = FromHsl((hue + 40) % 360, 0.6, 0.14);
    }

    private static Color FromHsl(double h, double s, double l)
    {
        var c = (1 - Math.Abs(2 * l - 1)) * s;
        var x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        var m = l - c / 2;
        var (r, g, b) = h switch
        {
            < 60 => (c, x, 0.0),
            < 120 => (x, c, 0.0),
            < 180 => (0.0, c, x),
            < 240 => (0.0, x, c),
            < 300 => (x, 0.0, c),
            _ => (c, 0.0, x),
        };
        return Color.FromRgb((byte)((r + m) * 255), (byte)((g + m) * 255), (byte)((b + m) * 255));
    }

    private void OnProgress()
    {
        if (_player is null) return;
        Dispatcher.BeginInvoke(() =>
        {
            if (!_seeking) SeekSlider.Value = _player.Progress;
            ElapsedText.Text = Format(_player.Position);
            DurationText.Text = Format(_player.Duration);
        });
    }

    private static string Format(TimeSpan time) =>
        time.TotalHours >= 1 ? time.ToString(@"h\:mm\:ss") : time.ToString(@"m\:ss");

    // ── Transport ──────────────────────────────────────────────────────────────

    private void Play_Click(object sender, RoutedEventArgs e) => _player?.TogglePlayPause();

    private void Next_Click(object sender, RoutedEventArgs e) => _player?.Next();

    private void Previous_Click(object sender, RoutedEventArgs e) => _player?.Previous();

    private void Shuffle_Click(object sender, RoutedEventArgs e)
    {
        if (_player is null) return;
        _player.IsShuffle = ShuffleButton.IsChecked == true;
        _player.SaveLibrary();
    }

    private void Repeat_Click(object sender, RoutedEventArgs e)
    {
        if (_player is not null) _player.IsLooping = RepeatButton.IsChecked == true;
    }

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_syncing || _player is null) return;
        _player.Volume = VolumeSlider.Value;
        VolumeChanged?.Invoke(VolumeSlider.Value);
    }

    private void Seek_MouseDown(object sender, MouseButtonEventArgs e) => _seeking = true;

    private void Seek_MouseUp(object sender, MouseButtonEventArgs e)
    {
        _seeking = false;
        _player?.Seek(SeekSlider.Value);
    }

    // ── Library ────────────────────────────────────────────────────────────────

    private void TrackList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (TrackList.SelectedItem is TrackRow row) _player?.PlayTrack(row.Track);
    }

    private void TrackList_KeyDown(object sender, KeyEventArgs e)
    {
        if (TrackList.SelectedItem is not TrackRow row || _player is null) return;
        if (e.Key == Key.Enter)
        {
            _player.PlayTrack(row.Track);
            e.Handled = true;
        }
        else if (e.Key == Key.Delete)
        {
            _player.Remove(row.Track);
            RenderRows();
            e.Handled = true;
        }
    }

    private void Favorite_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: TrackRow row } && _player is not null)
        {
            _player.ToggleFavorite(row.Track);
            if (TabFavorites.IsChecked == true) RenderRows();
            else row.Refresh();
        }
    }

    private void Tab_Checked(object sender, RoutedEventArgs e)
    {
        if (IsInitialized) RenderRows();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        _search = (SearchBox.Text ?? string.Empty).Trim();
        RenderRows();
    }

    private void AddFiles_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Multiselect = true,
            Title = "Добавить музыку",
            Filter = "Музыка|" + string.Join(";", AmbiencePlayer.SupportedExtensions.Select(x => "*" + x)),
        };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) Add(dialog.FileNames);
    }

    private void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "Папка с музыкой" };
        if (dialog.ShowDialog(Window.GetWindow(this)) == true) Add([dialog.FolderName]);
    }

    private void AddMyMusic_Click(object sender, RoutedEventArgs e) =>
        Add([Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)]);

    private void Page_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Page_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] paths) Add(paths);
    }

    private void Add(IReadOnlyList<string> paths)
    {
        if (_player is null) return;
        var added = _player.AddFiles(paths);
        RenderRows();
        WriteLite.Services.CompatibilityLogger.Technical("music-library-added", $"tracks={added}");
    }

    /// <summary>One row of the track list.</summary>
    public sealed class TrackRow(AmbienceTrack track, MusicPage page) : INotifyPropertyChanged
    {
        public AmbienceTrack Track { get; } = track;

        public string Title => Track.Title;

        public string Artist => Track.Artist ?? string.Empty;

        private bool IsCurrent => page._player?.Current == Track;

        public string Marker => IsCurrent ? (page._player!.IsPlaying ? "▶" : "❚❚") : string.Empty;

        public Brush MarkerBrush => (Brush)page.FindResource("WlBrand");

        public FontWeight Weight => IsCurrent ? FontWeights.SemiBold : FontWeights.Normal;

        public string Star => page._player?.IsFavorite(Track) == true ? "★" : "☆";

        public Brush StarBrush => (Brush)page.FindResource(page._player?.IsFavorite(Track) == true ? "WlBrand" : "WlTextSubtle");

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(string.Empty));
    }
}
