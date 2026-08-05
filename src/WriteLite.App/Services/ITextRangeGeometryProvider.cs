using System.Windows;
using System.Windows.Automation;

namespace WriteLite.Services;

public interface ITextRangeGeometryProvider
{
    bool CanProvideGeometry(AutomationElement element);

    Task<IReadOnlyList<Rect>> GetRectanglesAsync(
        AutomationElement element,
        int start,
        int length,
        CancellationToken cancellationToken);
}
