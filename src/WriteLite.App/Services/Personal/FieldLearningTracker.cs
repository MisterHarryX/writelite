namespace WriteLite.Services.Personal;

/// <summary>
/// Decides when text typed in another application's field is finished enough to learn.
/// </summary>
/// <remarks>
/// <para>Finished sentences are learned as they appear. The last one usually is not finished
/// — the user is still typing it — and in a messenger it never will be: a chat message is sent
/// without a full stop and the field empties. So the whole previous text, last sentence
/// included, is learned at the moments a field's text stops being a draft: the field is
/// cleared or replaced with something shorter, or the user moves to another field.</para>
///
/// <para>Repeats cost nothing — the library ignores a sentence it has already learned — so
/// this errs toward offering text rather than toward tracking state precisely.</para>
/// </remarks>
public sealed class FieldLearningTracker(Action<string, bool> observe)
{
    private string? _target;
    private string _text = string.Empty;

    /// <summary>A new reading of the focused field, or null/empty when there is none.</summary>
    /// <param name="target">Identity of the field (UIA runtime id).</param>
    /// <param name="text">Its current text.</param>
    /// <param name="isUserEditing">True when the monitor has confirmed the user is typing here.</param>
    public void Update(string? target, string? text, bool isUserEditing)
    {
        if (string.IsNullOrEmpty(target) || string.IsNullOrEmpty(text))
        {
            FlushDraft();
            _target = null;
            return;
        }

        if (!string.Equals(target, _target, StringComparison.Ordinal))
        {
            FlushDraft();
            _target = target;
            _text = string.Empty;
        }
        else if (_text.Length > 0 && !text.StartsWith(_text[..Math.Min(_text.Length, 24)], StringComparison.Ordinal)
                 && text.Length < _text.Length / 2)
        {
            // Sent, or cleared and started over: the old text is final.
            FlushDraft();
        }

        // A field the user only focused may hold someone else's text — a quoted reply, a
        // prefilled form. Nothing is learned until they actually type in it.
        if (!isUserEditing)
        {
            return;
        }

        _text = text;
        observe(text, false);
    }

    /// <summary>Learns whatever draft is held, last sentence included. Call on shutdown too.</summary>
    public void FlushDraft()
    {
        if (_text.Length > 0)
        {
            observe(_text, true);
        }

        _text = string.Empty;
    }
}
