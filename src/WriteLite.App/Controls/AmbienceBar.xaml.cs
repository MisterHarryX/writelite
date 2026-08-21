using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Animation;
using WriteLite.Services;
using WriteLite.Services.Audio;
using UserControl = System.Windows.Controls.UserControl;

namespace WriteLite.Controls;

/// <summary>
/// The rail's ambience player: transport, track, progress and volume.
/// </summary>
/// <remarks>
/// Deliberately small. It is a detail of the product's character, not a feature
/// competing with the editor, so it lives in space the rail was already wasting and
/// hides itself entirely when there is nothing to play.
/// </remarks>
public partial class AmbienceBar : UserControl
{
    /// <summary>Equalizer bar scale animations, held so they can be stopped rather than hidden.</summary>
    private readonly List<(ScaleTransform Transform, DoubleAnimation Animation)> _equalizer = [];

    private AmbiencePlayer? _player;
    private bool _suppressCallbacks;

    public AmbienceBar()
    {
        InitializeComponent();
        Unloaded += (_, _) => StopEqualizer();
    }

    /// <summary>Raised when the user changes volume, playback or repeat, so it can be persisted.</summary>
    public event Action<double, bool, bool>? StateChanged;

    public void Bind(AmbiencePlayer player, double volume, bool autoPlay, bool loop)
    {
        _player = player;
        _player.StateChanged += OnPlayerStateChanged;
        _player.ProgressChanged += OnProgressChanged;

        // Restored, not reported: setting these up is not the user changing them, and
        // raising StateChanged here would write the settings back over themselves.
        _suppressCallbacks = true;
        VolumeSlider.Value = Math.Clamp(volume, 0, 1);
        LoopButton.IsChecked = loop;
        _suppressCallbacks = false;

        _player.Volume = VolumeSlider.Value;
        _player.IsLooping = loop;

        OnPlayerStateChanged();

        // Resuming on launch is only reasonable when the user left it playing; nobody
        // wants sound to start on its own from a fresh install.
        if (autoPlay && _player.HasTracks)
        {
            _player.Play();
        }
    }

    private void OnPlayerStateChanged()
    {
        if (_player is null)
        {
            return;
        }

        // No tracks at all: the bar is not shown as an empty shell.
        Root.Visibility = _player.HasTracks ? Visibility.Visible : Visibility.Collapsed;
        if (!_player.HasTracks)
        {
            return;
        }

        var track = _player.Current;
        TitleText.Text = track?.Title ?? string.Empty;
        ArtistText.Text = track?.Artist ?? string.Empty;
        ArtistText.Visibility = string.IsNullOrEmpty(track?.Artist) ? Visibility.Collapsed : Visibility.Visible;

        PlayIcon.SetResourceReference(
            System.Windows.Shapes.Path.DataProperty,
            _player.IsPlaying ? "WlIconPause" : "WlIconPlay");
        PlayButton.ToolTip = _player.IsPlaying ? "Пауза" : "Включить атмосферу";

        // With one track, skipping has nowhere to go. Disabled rather than hidden, so
        // the transport does not change shape when a second file appears.
        PreviousButton.IsEnabled = _player.CanSkip;
        NextButton.IsEnabled = _player.CanSkip;

        // Kept in step with the player, which is the authority — repeat can be turned on
        // from here or restored from settings, and the button must show either.
        if (LoopButton.IsChecked != _player.IsLooping)
        {
            _suppressCallbacks = true;
            LoopButton.IsChecked = _player.IsLooping;
            _suppressCallbacks = false;
        }

        LoopButton.ToolTip = _player.IsLooping ? "Не повторять трек" : "Повторять трек";

        if (_player.IsPlaying)
        {
            StartEqualizer();
        }
        else
        {
            StopEqualizer();
        }
    }

    private void OnProgressChanged()
    {
        if (_player is null)
        {
            return;
        }

        // Written straight to the transform with no animation: the value already
        // arrives four times a second, and animating between samples would mean two
        // clocks fighting over the same property.
        ProgressScale.ScaleX = _player.Progress;
    }

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        _player?.TogglePlayPause();
        Report();
    }

    private void Previous_Click(object sender, RoutedEventArgs e) => _player?.Previous();

    private void Next_Click(object sender, RoutedEventArgs e) => _player?.Next();

    private void Volume_Changed(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressCallbacks || _player is null)
        {
            return;
        }

        _player.Volume = e.NewValue;
        Report();
    }

    private void Loop_Changed(object sender, RoutedEventArgs e)
    {
        if (_suppressCallbacks || _player is null)
        {
            return;
        }

        _player.IsLooping = LoopButton.IsChecked == true;
        Report();
    }

    /// <summary>Hands the current player state up to be persisted.</summary>
    private void Report() =>
        StateChanged?.Invoke(
            VolumeSlider.Value,
            _player?.IsPlaying == true,
            _player?.IsLooping == true);

    // ── Equalizer ────────────────────────────────────────────────────────────

    /// <summary>
    /// Four bars breathing at different rates.
    /// </summary>
    /// <remarks>
    /// The only animation in WriteLite that repeats. It is justified because it
    /// carries state that is otherwise invisible — sound is playing, from an
    /// application that may be minimised to the tray — and it is cheap: four scale
    /// transforms, composited, no layout.
    ///
    /// The periods are deliberately not multiples of each other, so the bars never
    /// fall into step and start reading as a single pulsing block.
    /// </remarks>
    private void StartEqualizer()
    {
        if (_equalizer.Count > 0)
        {
            return;
        }

        Equalizer.Visibility = Visibility.Visible;

        if (!Motion.IsEnabled)
        {
            // Reduced motion: the bars stay, at rest, as a static level meter.
            return;
        }

        var bars = new (ScaleTransform Transform, double From, double To, double Seconds)[]
        {
            (Bar1Scale, 0.25, 0.9, 0.72),
            (Bar2Scale, 0.45, 1.0, 0.53),
            (Bar3Scale, 0.3, 0.75, 0.89),
            (Bar4Scale, 0.5, 0.95, 0.64)
        };

        foreach (var (transform, from, to, seconds) in bars)
        {
            var animation = new DoubleAnimation(from, to, new Duration(TimeSpan.FromSeconds(seconds)))
            {
                AutoReverse = true,
                RepeatBehavior = RepeatBehavior.Forever,
                EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
            };

            transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation);
            _equalizer.Add((transform, animation));
        }
    }

    private void StopEqualizer()
    {
        foreach (var (transform, _) in _equalizer)
        {
            // Null clears the clock entirely rather than leaving it running against a
            // hidden element.
            transform.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }

        _equalizer.Clear();
        Equalizer.Visibility = Visibility.Collapsed;
    }
}
