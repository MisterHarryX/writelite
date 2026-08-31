using System.Text;
using System.Windows;
using System.Windows.Automation;

namespace CardProbe;

/// <summary>One observation of WriteLite's correction card, as the desktop exposes it.</summary>
internal sealed record CardObservation(
    bool Present,
    IReadOnlyList<string> Texts,
    IReadOnlyList<string> Buttons)
{
    /// <summary>True while this reading still looks like it caught a re-render in progress.</summary>
    public bool Torn { get; init; } = true;

    public static readonly CardObservation Absent = new(false, [], []);

    public bool HasApplyCta => Buttons.Any(b => b is "Исправить" || b.StartsWith("Добавить", StringComparison.Ordinal));
    public bool HasRetry => Buttons.Contains("Повторить");
    public bool HasCopy => Buttons.Contains("Скопировать");
    public bool HasDictionary => Buttons.Contains("В словарь");
    public bool HasIgnore => Buttons.Contains("Игнорировать");
    public bool HasStaleWarning => Texts.Any(t => t.Contains("Текст изменился", StringComparison.Ordinal));
    public bool HasProgress => Texts.Any(t => t.Contains("Проверяем текст", StringComparison.Ordinal));
    public bool HasChangeArrow => Texts.Contains("→");
    public bool HasExplanation => Texts.Any(t => t.Contains("словар", StringComparison.OrdinalIgnoreCase)
                                                 || t.Contains("Проверьте", StringComparison.Ordinal));

    /// <summary>
    /// The forbidden combinations, named. Empty means the card is asserting one thing.
    /// </summary>
    /// <remarks>
    /// Written as the screenshot's own contradictions rather than as a generic invariant, so
    /// a failure here names which half of the reported card came back.
    /// </remarks>
    public IReadOnlyList<string> Contradictions()
    {
        if (!Present) return [];
        var found = new List<string>();

        if (HasApplyCta && HasStaleWarning) found.Add("apply CTA + «Текст изменился»");
        if (HasApplyCta && HasRetry) found.Add("apply CTA + «Повторить»");
        if (HasProgress && HasApplyCta) found.Add("progress + apply CTA");
        if (HasProgress && HasRetry) found.Add("progress + «Повторить»");
        if (HasProgress && HasStaleWarning) found.Add("progress + «Текст изменился»");
        if (HasProgress && HasExplanation) found.Add("progress + finished explanation");
        if (HasProgress && HasChangeArrow) found.Add("progress + stale change row");
        if (HasStaleWarning) found.Add("«Текст изменился» shown at all (a text change is not a failure)");

        // «X → X»: the arrow is present and the two mono fragments either side are equal.
        var arrow = -1;
        for (var i = 0; i < Texts.Count; i++)
        {
            if (Texts[i] == "→") { arrow = i; break; }
        }

        if (arrow > 0 && arrow < Texts.Count - 1
            && string.Equals(Texts[arrow - 1], Texts[arrow + 1], StringComparison.Ordinal))
        {
            found.Add($"«{Texts[arrow - 1]} → {Texts[arrow + 1]}» — a correction that changes nothing");
        }

        return found;
    }

    public string Describe()
    {
        if (!Present) return "no card";
        var builder = new StringBuilder();
        builder.Append(HasProgress
            ? (HasChangeArrow ? "APPLYING" : "CHECKING")
            : HasRetry ? "FAILED"
            : HasChangeArrow ? "RESULT"
            : HasIgnore ? "NO-SUGGESTION"
            : "TORN-READ");
        builder.Append(" [");
        builder.Append(string.Join(' ', Buttons));
        builder.Append("] ");
        builder.Append(string.Join(" | ", Texts.Where(t => t.Length > 1).Take(6)));
        return builder.ToString();
    }
}

/// <summary>Reads WriteLite's correction card through UI Automation, as a screen reader would.</summary>
internal static class Card
{
    /// <summary>
    /// Reads the card, retrying once if the read caught the tree mid-update.
    /// </summary>
    /// <remarks>
    /// <c>FindAll</c> walks a live visual tree, so a read that lands inside WPF's re-render
    /// can see half of one phase and half of the next. That is a property of the observer,
    /// not of the card, and it is distinguishable: no real phase has a card on screen with no
    /// action at all and no progress. One re-read after the frame settles tells the two
    /// apart — and if the state persists, it is reported as observed rather than smoothed.
    /// </remarks>
    public static CardObservation Read(int writeLiteProcessId)
    {
        var first = ReadOnce(writeLiteProcessId);
        if (!first.Present || !IsIncoherent(first)) return first;

        Thread.Sleep(90);
        var second = ReadOnce(writeLiteProcessId);
        return second.Present && IsIncoherent(second) ? second with { Torn = false } : second;
    }

    /// <summary>No phase shows a card with neither an action nor a progress indicator.</summary>
    private static bool IsIncoherent(CardObservation card)
        => !card.HasProgress && !card.HasApplyCta && !card.HasIgnore && !card.HasRetry && !card.HasCopy;

    private static CardObservation ReadOnce(int writeLiteProcessId)
    {
        var window = Find(writeLiteProcessId);
        if (window is null) return CardObservation.Absent;

        try
        {
            var texts = new List<string>();
            var buttons = new List<string>();

            foreach (AutomationElement element in window.FindAll(TreeScope.Descendants, System.Windows.Automation.Condition.TrueCondition))
            {
                if (!IsVisible(element)) continue;

                var type = element.Current.ControlType;
                var name = element.Current.Name ?? string.Empty;
                if (name.Length == 0) continue;

                if (type == ControlType.Button) buttons.Add(name);
                else if (type == ControlType.Text) texts.Add(name);
            }

            return new CardObservation(true, texts, buttons);
        }
        catch (ElementNotAvailableException)
        {
            // The card closed between finding it and reading it. That is an answer too.
            return CardObservation.Absent;
        }
    }

    public static AutomationElement? Find(int writeLiteProcessId)
    {
        try
        {
            return AutomationElement.RootElement.FindFirst(
                TreeScope.Children,
                new AndCondition(
                    new PropertyCondition(AutomationElement.ProcessIdProperty, writeLiteProcessId),
                    new PropertyCondition(
                        AutomationElement.AutomationIdProperty,
                        "WriteLiteCorrectionCard")));
        }
        catch (ElementNotAvailableException)
        {
            return null;
        }
    }

    /// <summary>
    /// Whether the user can see this element.
    /// </summary>
    /// <remarks>
    /// A WPF element with <c>Visibility.Collapsed</c> stays in the visual tree and keeps an
    /// automation peer, so "is it in the tree" answers nothing. It measures zero, which is
    /// what is tested here. <c>IsOffscreen</c> is deliberately not the test: it is unreliable
    /// on layered windows, and on this machine it is already known to lie for other providers.
    /// </remarks>
    private static bool IsVisible(AutomationElement element)
    {
        try
        {
            var rect = element.Current.BoundingRectangle;
            return rect != Rect.Empty && rect.Width > 0.5 && rect.Height > 0.5;
        }
        catch (ElementNotAvailableException)
        {
            return false;
        }
    }
}
