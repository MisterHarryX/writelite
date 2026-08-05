using System.Windows;
using System.Windows.Automation;

namespace WriteLite.Services;

public interface ITextTargetAdapter
{
    bool SupportsDirectWrite { get; }
    bool SupportsRangeReplacement { get; }
    string Name { get; }

    bool CanHandle(AutomationElement element);
    Task<(bool Succeeded, string Text)> ReadTextAsync(AutomationElement element, CancellationToken cancellationToken = default);
    Task<(bool Succeeded, string Error)> ReplaceTextAsync(AutomationElement element, string newText, CancellationToken cancellationToken = default);
    Task<(bool Succeeded, string Error)> ReplaceRangeAsync(AutomationElement element, int start, int length, string replacement, CancellationToken cancellationToken = default);
    Task<Rect> GetAnchorRectangleAsync(AutomationElement element, CancellationToken cancellationToken = default);
}
