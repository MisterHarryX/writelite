using WriteLite.Models;
using WriteLite.Services;

namespace WriteLite;

public partial class App
{
    /// <summary>Teaches the personal style library from a correction accepted in another application.</summary>
    private void LearnFromCorrection(TextIssue issue) => _personalStyle?.RecordAcceptedCorrection(issue);

    /// <summary>
    /// On the first launch after this feature exists, opens the window on «Мой стиль» so the
    /// calibration is offered at the start rather than discovered later.
    /// </summary>
    /// <remarks>
    /// Once only. WriteLite normally starts in the tray, and opening a window on every launch
    /// would be the kind of interruption the tray exists to avoid; after the first time the
    /// offer stays on the home page until the user takes it or turns it down.
    /// </remarks>
    private void OfferStyleCalibrationOnce()
    {
        if (_settings.StyleCalibrationCompleted || _settings.StyleCalibrationDismissed || _settings.StyleCalibrationOffered)
        {
            return;
        }

        _settings.StyleCalibrationOffered = true;
        SaveSettingsQuietly(_settings);
        CompatibilityLogger.State("style-calibration-offered");

        OpenMainWindow();
        _mainWindow?.OpenPersonalStyle(startCalibration: false);
    }
}
