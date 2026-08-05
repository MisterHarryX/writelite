namespace WriteLite.Services;

/// <summary>Pure presentation rules for the fixed-size indicator; safe to unit test without WPF.</summary>
public static class IndicatorPresentation
{
    public static string BadgeText(int issueCount) => issueCount switch
    {
        <= 0 => string.Empty,
        <= 99 => issueCount.ToString(),
        _ => "99+"
    };

    public static bool ShouldUpdatePosition(double oldX, double oldY, double newX, double newY)
        => Math.Abs(oldX - newX) > 1 || Math.Abs(oldY - newY) > 1;
}
