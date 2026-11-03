using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using WriteLite.Resources;
using WriteLite.Services.Rules;
using Button = System.Windows.Controls.Button;
using TextBox = System.Windows.Controls.TextBox;
using UserControl = System.Windows.Controls.UserControl;
using Panel = System.Windows.Controls.Panel;
using HorizontalAlignment = System.Windows.HorizontalAlignment;

namespace WriteLite.Views.Pages;

/// <summary>
/// The rules reference: every rule WriteLite checks, with its reasoning, examples and source.
/// </summary>
/// <remarks>
/// Built from <see cref="RuleReference"/>, which is built from the shipped rule pack, so a
/// rule cannot be checked without being explained here. Correction cards link to an entry by
/// rule id through <see cref="ShowRule"/>.
/// </remarks>
public partial class RulesPage : UserControl
{
    private readonly RuleReference _reference;
    private readonly Dictionary<string, (FrameworkElement Entry, FrameworkElement Details)> _entries =
        new(StringComparer.OrdinalIgnoreCase);

    public RulesPage() : this(RuleReference.Default)
    {
    }

    public RulesPage(RuleReference reference)
    {
        _reference = reference ?? throw new ArgumentNullException(nameof(reference));
        InitializeComponent();
        Render(_reference.Sections);
    }

    /// <summary>Opens the entry for <paramref name="ruleId"/> and scrolls it into view.</summary>
    /// <returns>False when the reference has no such rule; the page then shows the full list.</returns>
    public bool ShowRule(string ruleId)
    {
        if (!string.IsNullOrEmpty(SearchBox.Text)) SearchBox.Text = string.Empty;

        if (!_entries.TryGetValue(ruleId, out var target)) return false;

        target.Details.Visibility = Visibility.Visible;
        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Loaded,
            new Action(() =>
            {
                target.Entry.BringIntoView(new Rect(0, 0, target.Entry.ActualWidth, Math.Min(target.Entry.ActualHeight, 320)));
                (target.Entry as Panel)?.Children.OfType<Button>().FirstOrDefault()?.Focus();
            }));
        return true;
    }

    /// <summary>True when the entry for <paramref name="ruleId"/> is open. For tests.</summary>
    internal bool IsOpen(string ruleId)
        => _entries.TryGetValue(ruleId, out var target) && target.Details.Visibility == Visibility.Visible;

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
        => Render(_reference.Search(((TextBox)sender).Text));

    private void Render(IReadOnlyList<RuleReferenceSection> sections)
    {
        _entries.Clear();
        SectionsHost.Children.Clear();

        foreach (var section in sections)
        {
            var heading = new TextBlock { Margin = new Thickness(0, 26, 0, 6) };
            heading.SetResourceReference(StyleProperty, "WlMonoLabel");
            Controls.Type.SetTracked(heading, section.Title.ToUpperInvariant());
            SectionsHost.Children.Add(heading);

            foreach (var entry in section.Entries)
            {
                SectionsHost.Children.Add(BuildEntry(entry));
            }
        }

        EmptyText.Visibility = sections.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private FrameworkElement BuildEntry(RuleReferenceEntry rule)
    {
        var title = new TextBlock { Text = rule.Title, TextWrapping = TextWrapping.Wrap };
        title.SetResourceReference(StyleProperty, "WlCardTitle");
        title.SetResourceReference(TextBlock.ForegroundProperty, "WlText");

        var summary = new TextBlock { Text = rule.Summary, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) };
        summary.SetResourceReference(StyleProperty, "WlSecondary");
        summary.SetResourceReference(TextBlock.ForegroundProperty, "WlTextSecondary");

        var head = new StackPanel();
        head.Children.Add(title);
        if (!string.Equals(rule.Summary, rule.Title, StringComparison.CurrentCultureIgnoreCase)) head.Children.Add(summary);

        var toggle = new Button { Content = head, Style = (Style)FindResource("RuleRowButton") };
        System.Windows.Automation.AutomationProperties.SetName(toggle, rule.Title);

        var details = BuildDetails(rule);
        details.Visibility = Visibility.Collapsed;
        toggle.Click += (_, _) =>
            details.Visibility = details.Visibility == Visibility.Visible ? Visibility.Collapsed : Visibility.Visible;

        var entry = new StackPanel();
        entry.Children.Add(toggle);
        entry.Children.Add(details);

        var divider = new Border { Height = 1, Margin = new Thickness(0, 0, 0, 0) };
        divider.SetResourceReference(Border.BackgroundProperty, "WlLineSoft");
        entry.Children.Add(divider);

        _entries[rule.RuleId] = (entry, details);
        return entry;
    }

    private static FrameworkElement BuildDetails(RuleReferenceEntry rule)
    {
        var details = new StackPanel { Margin = new Thickness(0, 0, 0, 16) };

        if (!string.Equals(rule.Explanation, rule.Summary, StringComparison.CurrentCulture))
        {
            var explanation = new TextBlock { Text = rule.Explanation, TextWrapping = TextWrapping.Wrap, MaxWidth = 720, HorizontalAlignment = HorizontalAlignment.Left };
            explanation.SetResourceReference(StyleProperty, "WlBody");
            explanation.SetResourceReference(TextBlock.ForegroundProperty, "WlText");
            details.Children.Add(explanation);
        }

        if (rule.Examples.Count > 0)
        {
            details.Children.Add(Label(Strings.Rules_ExamplesLabel));
            foreach (var (bad, good) in rule.Examples)
            {
                var line = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
                line.SetResourceReference(StyleProperty, "WlBody");

                var wrong = new Run(bad) { TextDecorations = TextDecorations.Strikethrough };
                wrong.SetResourceReference(TextElement.ForegroundProperty, "WlTextMuted");
                var arrow = new Run("  →  ");
                arrow.SetResourceReference(TextElement.ForegroundProperty, "WlTextMuted");
                var right = new Run(good);
                right.SetResourceReference(TextElement.ForegroundProperty, "WlText");

                line.Inlines.Add(wrong);
                line.Inlines.Add(arrow);
                line.Inlines.Add(right);
                details.Children.Add(line);
            }
        }

        if (!string.IsNullOrWhiteSpace(rule.Source))
        {
            details.Children.Add(Label(Strings.Rules_SourceLabel));
            var source = new TextBlock { Text = rule.Source, TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 4, 0, 0) };
            source.SetResourceReference(StyleProperty, "WlCaption");
            details.Children.Add(source);
        }

        return details;

        static TextBlock Label(string text)
        {
            var label = new TextBlock { Margin = new Thickness(0, 14, 0, 2) };
            label.SetResourceReference(StyleProperty, "WlMonoLabel");
            Controls.Type.SetTracked(label, text.ToUpperInvariant());
            return label;
        }
    }
}
