using System.Windows;
using System.Windows.Input;
using WriteLite.Services.Lexical;
using Button = System.Windows.Controls.Button;
using Cursors = System.Windows.Input.Cursors;

namespace WriteLite.Controls;

/// <summary>
/// One clickable related word: a synonym, an antonym or an English translation.
/// </summary>
/// <remarks>
/// A button rather than a styled <c>TextBlock</c> with a mouse handler, so the whole
/// keyboard and accessibility story comes for free: chips are in the tab order, Enter
/// and Space activate them, and a screen reader announces each one as the button it is.
/// That matters more here than anywhere else on the page — the chips are how a reader
/// moves through the dictionary, so they cannot be mouse-only.
///
/// <see cref="HasArticle"/> distinguishes a word that opens an entry from one the
/// installed packs do not cover. Both stay readable; only the first looks like it
/// leads somewhere. Offering a click that lands on "nothing found" is worse than
/// showing plainly that the trail ends here.
/// </remarks>
public sealed class WordChip : Button
{
    public static readonly DependencyProperty WordProperty =
        DependencyProperty.Register(nameof(Word), typeof(string), typeof(WordChip),
            new PropertyMetadata(string.Empty, OnWordChanged));

    public static readonly DependencyProperty LanguageProperty =
        DependencyProperty.Register(nameof(Language), typeof(LexicalLanguage), typeof(WordChip),
            new PropertyMetadata(LexicalLanguage.Unknown));

    public static readonly DependencyProperty HasArticleProperty =
        DependencyProperty.Register(nameof(HasArticle), typeof(bool), typeof(WordChip),
            new PropertyMetadata(true, OnHasArticleChanged));

    static WordChip()
    {
        DefaultStyleKeyProperty.OverrideMetadata(
            typeof(WordChip),
            new FrameworkPropertyMetadata(typeof(WordChip)));
    }

    /// <summary>The word as it should be displayed and looked up.</summary>
    public string Word
    {
        get => (string)GetValue(WordProperty);
        set => SetValue(WordProperty, value);
    }

    /// <summary>Which dictionary the word belongs to, so a lookup targets the right pack.</summary>
    public LexicalLanguage Language
    {
        get => (LexicalLanguage)GetValue(LanguageProperty);
        set => SetValue(LanguageProperty, value);
    }

    /// <summary>False when the installed packs have no entry for this word.</summary>
    public bool HasArticle
    {
        get => (bool)GetValue(HasArticleProperty);
        set => SetValue(HasArticleProperty, value);
    }

    private static void OnWordChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chip = (WordChip)d;
        var word = e.NewValue as string ?? string.Empty;
        chip.Content = word;
        chip.SetValue(System.Windows.Automation.AutomationProperties.NameProperty, word);
    }

    private static void OnHasArticleChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var chip = (WordChip)d;
        var navigable = e.NewValue is true;

        // Not IsEnabled: a chip without an article is still legitimate dictionary
        // content that should be readable and selectable, it simply is not a link.
        chip.Cursor = navigable ? Cursors.Hand : Cursors.Arrow;
        chip.Focusable = navigable;
        chip.ToolTip = navigable ? null : "Нет статьи в установленных словарях";
    }
}
