using System.Windows;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DataObject = System.Windows.DataObject;
using IDataObject = System.Windows.IDataObject;

namespace WriteLite.Services.Writing;

/// <summary>
/// Borrows the clipboard for one paste and gives it back.
/// </summary>
/// <remarks>
/// <para><b>Why this is not just <c>Clipboard.SetText</c>.</b> The clipboard belongs to the
/// user. Correcting one word must not cost them whatever they had copied — §27 of the brief
/// says so explicitly, and it is the difference between a tool that helps and a tool that has
/// to be watched. Everything the clipboard held is captured before the correction goes on it
/// and put back afterwards, including formats WriteLite knows nothing about, because
/// <see cref="IDataObject"/> carries them all.</para>
///
/// <para><b>Why the STA thread.</b> OLE clipboard access requires a single-threaded
/// apartment, and the write path runs on background workers. Marshalling to the application
/// dispatcher instead would put a clipboard round-trip — which can block on whichever
/// application currently owns the clipboard — on the UI thread, which is exactly the class of
/// stall the rest of this monitor is written to avoid. A short-lived STA thread costs
/// microseconds and cannot freeze the window.</para>
///
/// <para><b>What it cannot promise.</b> A delayed-rendering owner (a spreadsheet that
/// promises to produce a range only when someone asks for it) hands over a promise, not data;
/// capturing it materialises what it offers now, and an application that would have rendered
/// something richer later will not. There is no way to preserve that from outside the owning
/// process, so the capture is best-effort and says so.</para>
/// </remarks>
public sealed class ClipboardTransfer
{
    private readonly IDataObject? _previous;
    private readonly bool _hadContent;

    private ClipboardTransfer(IDataObject? previous, bool hadContent)
    {
        _previous = previous;
        _hadContent = hadContent;
    }

    /// <summary>Snapshots whatever the clipboard currently holds.</summary>
    public static ClipboardTransfer Capture()
    {
        IDataObject? captured = null;
        var hadContent = false;
        RunOnSta(() =>
        {
            captured = Clipboard.GetDataObject();
            hadContent = captured is not null && captured.GetFormats().Length > 0;
        });

        return new ClipboardTransfer(captured, hadContent);
    }

    /// <summary>Puts <paramref name="text"/> on the clipboard as the only content.</summary>
    public static bool SetText(string text)
    {
        var succeeded = false;
        RunOnSta(() =>
        {
            Clipboard.SetDataObject(new DataObject(DataFormats.UnicodeText, text ?? string.Empty), true);
            succeeded = true;
        });

        return succeeded;
    }

    /// <summary>Returns the clipboard to what <see cref="Capture"/> found.</summary>
    /// <remarks>
    /// An empty clipboard is restored by clearing rather than by writing an empty string: the
    /// two are different states, and a paste target can tell them apart.
    /// </remarks>
    public void Restore()
    {
        var previous = _previous;
        var hadContent = _hadContent;
        RunOnSta(() =>
        {
            if (hadContent && previous is not null)
            {
                Clipboard.SetDataObject(previous, true);
            }
            else
            {
                Clipboard.Clear();
            }
        });
    }

    /// <summary>
    /// Runs one clipboard operation on a fresh STA thread, swallowing the transient failures
    /// the clipboard is prone to.
    /// </summary>
    /// <remarks>
    /// <c>Clipboard</c> throws <see cref="System.Runtime.InteropServices.COMException"/> when
    /// another process holds the clipboard open — routinely, and for a few milliseconds at a
    /// time. Failing the whole correction over that would be wrong; the caller finds out
    /// through verification instead, and gets to try nothing worse than the next strategy.
    /// </remarks>
    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception exception) { failure = exception; }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.IsBackground = true;
        thread.Start();
        if (!thread.Join(TimeSpan.FromMilliseconds(700)))
        {
            CompatibilityLogger.Technical("clipboard-timeout", "op=sta-join");
            return;
        }

        if (failure is not null)
        {
            CompatibilityLogger.Technical("clipboard-failed", $"type={failure.GetType().Name}");
        }
    }
}
