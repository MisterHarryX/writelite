using System.Windows;
using System.Windows.Automation;

namespace WriteLite.Services;

public sealed class ValuePatternTextAdapter : ITextTargetAdapter
{
    public bool SupportsDirectWrite => true;
    public bool SupportsRangeReplacement => true;
    public string Name => "ValuePattern";

    public bool CanHandle(AutomationElement element)
    {
        return element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) &&
               valueObject is ValuePattern valuePattern &&
               !valuePattern.Current.IsReadOnly;
    }

    public Task<(bool Succeeded, string Text)> ReadTextAsync(AutomationElement element, CancellationToken cancellationToken = default)
    {
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) ||
            valueObject is not ValuePattern valuePattern)
        {
            return Task.FromResult((false, string.Empty));
        }

        return Task.FromResult((true, valuePattern.Current.Value ?? string.Empty));
    }

    public Task<(bool Succeeded, string Error)> ReplaceTextAsync(AutomationElement element, string newText, CancellationToken cancellationToken = default)
        => ReplaceTextAsync(element, newText, caretIndex: newText?.Length ?? 0, cancellationToken);

    public Task<(bool Succeeded, string Error)> ReplaceTextAsync(
        AutomationElement element,
        string newText,
        int caretIndex,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!element.TryGetCurrentPattern(ValuePattern.Pattern, out var valueObject) ||
            valueObject is not ValuePattern valuePattern)
        {
            return Task.FromResult((false, "ValuePattern is unavailable."));
        }

        if (valuePattern.Current.IsReadOnly)
        {
            return Task.FromResult((false, "The target field is read-only."));
        }

        // Prefer Win32 EM_REPLACESEL on classic Edit/RichEdit (caret + often Ctrl+Z).
        // On RichEditD2DPT (Win11 Notepad) undo is unreliable — still used for caret/range fidelity.
        if (Win32TextEdit.TryGetHwnd(element, out var hwnd)
            && Win32TextEdit.TryReplaceAll(hwnd, newText ?? string.Empty, caretIndex))
        {
            if (Win32TextEdit.IsUndoUnreliable(hwnd))
            {
                CompatibilityLogger.Technical("write-path", "win32-richedit-d2d undo=unreliable");
            }
            else
            {
                CompatibilityLogger.Technical("write-path", "win32-range undo=likely");
            }

            TextWriteTelemetry.RecordWrite();
            return Task.FromResult((true, string.Empty));
        }

        // Fallback: ValuePattern.SetValue — Ctrl+Z typically unavailable after this path.
        CompatibilityLogger.Technical("write-path", "valuepattern-fallback undo=unavailable");
        cancellationToken.ThrowIfCancellationRequested();
        valuePattern.SetValue(newText ?? string.Empty);
        TextWriteTelemetry.RecordWrite();
        return Task.FromResult((true, string.Empty));
    }

    public async Task<(bool Succeeded, string Error)> ReplaceRangeAsync(
        AutomationElement element,
        int start,
        int length,
        string replacement,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Prefer range-level Win32 replace (caret + scroll restore).
        if (Win32TextEdit.TryGetHwnd(element, out var hwnd)
            && Win32TextEdit.TryReplaceRange(hwnd, start, length, replacement ?? string.Empty))
        {
            CompatibilityLogger.Technical(
                "write-path",
                Win32TextEdit.IsUndoUnreliable(hwnd)
                    ? "win32-range undo=unreliable"
                    : "win32-range undo=likely");
            TextWriteTelemetry.RecordWrite();
            return (true, string.Empty);
        }

        var read = await ReadTextAsync(element, cancellationToken).ConfigureAwait(false);
        if (!read.Succeeded)
        {
            return (false, "Could not read the target field.");
        }

        if (!TextCorrectionService.IsRangeValid(read.Text, start, length))
        {
            return (false, "The correction range is stale.");
        }

        CompatibilityLogger.Technical("write-path", "valuepattern-fallback undo=unavailable");
        var newText = read.Text.Remove(start, length).Insert(start, replacement ?? string.Empty);
        return await ReplaceTextAsync(element, newText, start + (replacement?.Length ?? 0), cancellationToken)
            .ConfigureAwait(false);
    }

    public Task<Rect> GetAnchorRectangleAsync(AutomationElement element, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(element.Current.BoundingRectangle);
    }
}

public sealed class TextPatternReadOnlyAdapter : ITextTargetAdapter
{
    public bool SupportsDirectWrite => false;
    public bool SupportsRangeReplacement => false;
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

    public Task<(bool Succeeded, string Error)> ReplaceTextAsync(AutomationElement element, string newText, CancellationToken cancellationToken = default)
    {
        return Task.FromResult((false, "The target field is available for analysis only."));
    }

    public Task<(bool Succeeded, string Error)> ReplaceRangeAsync(AutomationElement element, int start, int length, string replacement, CancellationToken cancellationToken = default)
    {
        return Task.FromResult((false, "The target field does not support safe direct replacement."));
    }

    public Task<Rect> GetAnchorRectangleAsync(AutomationElement element, CancellationToken cancellationToken = default)
    {
        return Task.FromResult(element.Current.BoundingRectangle);
    }
}

public sealed class LegacyAccessibleTextAdapter : ITextTargetAdapter
{
    public bool SupportsDirectWrite => false;
    public bool SupportsRangeReplacement => false;
    public string Name => "LegacyIAccessible";

    public bool CanHandle(AutomationElement element) => false;

    public Task<(bool Succeeded, string Text)> ReadTextAsync(AutomationElement element, CancellationToken cancellationToken = default)
        => Task.FromResult((false, string.Empty));

    public Task<(bool Succeeded, string Error)> ReplaceTextAsync(AutomationElement element, string newText, CancellationToken cancellationToken = default)
        => Task.FromResult((false, "Legacy accessible fields are analysis-only in WriteLite."));

    public Task<(bool Succeeded, string Error)> ReplaceRangeAsync(AutomationElement element, int start, int length, string replacement, CancellationToken cancellationToken = default)
        => Task.FromResult((false, "Legacy accessible fields are analysis-only in WriteLite."));

    public Task<Rect> GetAnchorRectangleAsync(AutomationElement element, CancellationToken cancellationToken = default)
        => Task.FromResult(element.Current.BoundingRectangle);
}

public sealed class CompositeTextTargetAdapter : ITextTargetAdapter
{
    private readonly ITextTargetAdapter[] _adapters =
    [
        new ValuePatternTextAdapter(),
        new TextPatternReadOnlyAdapter(),
        new LegacyAccessibleTextAdapter()
    ];

    public bool SupportsDirectWrite => false;
    public bool SupportsRangeReplacement => false;
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

    public Task<(bool Succeeded, string Error)> ReplaceTextAsync(AutomationElement element, string newText, CancellationToken cancellationToken = default)
    {
        var adapter = Select(element);
        return adapter is null
            ? Task.FromResult((false, "No supported text adapter."))
            : adapter.ReplaceTextAsync(element, newText, cancellationToken);
    }

    public Task<(bool Succeeded, string Error)> ReplaceRangeAsync(AutomationElement element, int start, int length, string replacement, CancellationToken cancellationToken = default)
    {
        var adapter = Select(element);
        return adapter is null
            ? Task.FromResult((false, "No supported text adapter."))
            : adapter.ReplaceRangeAsync(element, start, length, replacement, cancellationToken);
    }

    public Task<Rect> GetAnchorRectangleAsync(AutomationElement element, CancellationToken cancellationToken = default)
    {
        var adapter = Select(element);
        return adapter is null
            ? Task.FromResult(Rect.Empty)
            : adapter.GetAnchorRectangleAsync(element, cancellationToken);
    }
}
