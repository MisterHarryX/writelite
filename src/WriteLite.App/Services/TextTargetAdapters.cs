using System.Windows;
using System.Windows.Automation;

namespace WriteLite.Services;

/// <summary>Reads a control's text through <c>ValuePattern</c>.</summary>
/// <remarks>
/// Preferred when available because it is one call and returns the control's own value
/// verbatim, with no document-range walk.
/// </remarks>
public sealed class ValuePatternTextAdapter : ITextTargetAdapter
{
    public string Name => "ValuePattern";

    public bool CanHandle(AutomationElement element)
        => element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject)
           && valueObject is ValuePattern valuePattern
           && !valuePattern.Current.IsReadOnly;

    public Task<(bool Succeeded, string Text)> ReadTextAsync(AutomationElement element, CancellationToken cancellationToken = default)
    {
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) ||
            valueObject is not ValuePattern valuePattern)
        {
            return Task.FromResult((false, string.Empty));
        }

        return Task.FromResult((true, valuePattern.Current.Value ?? string.Empty));
    }

    public Task<Rect> GetAnchorRectangleAsync(AutomationElement element, CancellationToken cancellationToken = default)
        => Task.FromResult(element.Current.BoundingRectangle);
}

/// <summary>Reads a control's text through <c>TextPattern</c>.</summary>
/// <remarks>
/// The shape every browser input, <c>contenteditable</c> region, Electron composer and WPF
/// <c>RichTextBox</c> presents. It used to be called the read-only adapter and to refuse
/// every write on principle; the refusal was never about the control, only about the fact
/// that <c>ValuePattern.SetValue</c> is not available here. Corrections to these controls now
/// go through <see cref="Writing.SelectionPasteWriteStrategy"/>.
/// </remarks>
public sealed class TextPatternTextAdapter : ITextTargetAdapter
{
    public string Name => "TextPattern";

    public bool CanHandle(AutomationElement element)
    {
        // Prefer ValuePattern when both exist (handled by Composite order).
        return element.TryGetCurrentPattern(TextPattern.Pattern, out _)
               && !(element.TryGetCurrentPattern(ValuePattern.Pattern, out var v)
                    && v is ValuePattern vp
                    && !vp.Current.IsReadOnly);
    }

    public Task<(bool Succeeded, string Text)> ReadTextAsync(AutomationElement element, CancellationToken cancellationToken = default)
    {
        if (!element.TryGetCurrentPattern(TextPattern.Pattern, out var textObject) ||
            textObject is not TextPattern textPattern)
        {
            return Task.FromResult((false, string.Empty));
        }

        return Task.FromResult((true, textPattern.DocumentRange.GetText(-1) ?? string.Empty));
    }

    public Task<Rect> GetAnchorRectangleAsync(AutomationElement element, CancellationToken cancellationToken = default)
        => Task.FromResult(element.Current.BoundingRectangle);
}

/// <summary>Picks the first adapter that can read a given control.</summary>
public sealed class CompositeTextTargetAdapter : ITextTargetAdapter
{
    private readonly ITextTargetAdapter[] _adapters =
    [
        new ValuePatternTextAdapter(),
        new TextPatternTextAdapter(),
    ];

    public string Name => "Composite";

    public bool CanHandle(AutomationElement element) => Select(element) is not null;

    public ITextTargetAdapter? Select(AutomationElement element)
        => _adapters.FirstOrDefault(adapter => adapter.CanHandle(element));

    public Task<(bool Succeeded, string Text)> ReadTextAsync(AutomationElement element, CancellationToken cancellationToken = default)
    {
        var adapter = Select(element);
        return adapter is null
            ? Task.FromResult((false, string.Empty))
            : adapter.ReadTextAsync(element, cancellationToken);
    }

    public Task<Rect> GetAnchorRectangleAsync(AutomationElement element, CancellationToken cancellationToken = default)
    {
        var adapter = Select(element);
        return adapter is null
            ? Task.FromResult(Rect.Empty)
            : adapter.GetAnchorRectangleAsync(element, cancellationToken);
    }
}
