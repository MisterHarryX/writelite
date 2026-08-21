using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;
using DataObject = System.Windows.DataObject;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace WriteLite.Controls;

/// <summary>
/// The card grid, with drag-to-reorder.
/// </summary>
/// <remarks>
/// A <see cref="WrapPanel"/> rather than a <c>UniformGrid</c> or a fixed column count:
/// cards carry a min and max width and the panel decides how many fit, so the board
/// reflows from one column in a narrow window to four on a wide one without the page
/// having to know the breakpoints. That is also what keeps the last row reachable
/// instead of hanging past the viewport.
///
/// Reordering only reports intent. The panel works out where a card was dropped;
/// <c>NotesService</c> decides what that means for the stored order, and the board is
/// rebuilt from the store afterwards — so what is on screen is always what was saved.
/// </remarks>
public sealed class NoteBoard : WrapPanel
{
    /// <summary>Movement before a press becomes a drag. Below this it is a click on the card.</summary>
    private static readonly double DragThreshold = SystemParameters.MinimumHorizontalDragDistance;

    private const string CardFormat = "WriteLite.NoteCard";

    private Point _pressOrigin;
    private NoteCard? _pressed;
    private Border? _insertionMark;

    public NoteBoard()
    {
        Orientation = System.Windows.Controls.Orientation.Horizontal;
        AllowDrop = true;
        Background = Brushes.Transparent;

        PreviewMouseLeftButtonDown += OnPressed;
        PreviewMouseMove += OnMoved;
        PreviewMouseLeftButtonUp += (_, _) => _pressed = null;

        DragOver += OnDragOver;
        DragLeave += (_, _) => HideInsertionMark();
        Drop += OnDrop;
    }

    /// <summary>Raised when a card is dropped: the note that moved, and where it landed.</summary>
    public event Action<string, int>? ReorderRequested;

    /// <summary>True while a drag is in flight, so the page can hold off rebuilding.</summary>
    public bool IsDragging { get; private set; }

    private void OnPressed(object sender, MouseButtonEventArgs e)
    {
        _pressOrigin = e.GetPosition(this);
        _pressed = FindCard(e.OriginalSource as DependencyObject);
    }

    private void OnMoved(object sender, MouseEventArgs e)
    {
        if (_pressed is null || e.LeftButton != MouseButtonState.Pressed || _pressed.Note is null)
        {
            return;
        }

        var current = e.GetPosition(this);
        if (Math.Abs(current.X - _pressOrigin.X) < DragThreshold
            && Math.Abs(current.Y - _pressOrigin.Y) < DragThreshold)
        {
            return;
        }

        var card = _pressed;
        _pressed = null;
        IsDragging = true;

        // The card fades while it is being carried, so the board reads as "this one is
        // in your hand" rather than as a duplicate appearing under the cursor.
        card.Opacity = 0.45;

        try
        {
            DragDrop.DoDragDrop(card, new DataObject(CardFormat, card.Note.Id), DragDropEffects.Move);
        }
        finally
        {
            card.Opacity = 1;
            IsDragging = false;
            HideInsertionMark();
        }
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(CardFormat))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        e.Effects = DragDropEffects.Move;
        e.Handled = true;
        ShowInsertionMark(IndexAt(e.GetPosition(this)));
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        HideInsertionMark();

        if (e.Data.GetData(CardFormat) is not string noteId || string.IsNullOrEmpty(noteId))
        {
            return;
        }

        e.Handled = true;
        ReorderRequested?.Invoke(noteId, IndexAt(e.GetPosition(this)));
    }

    /// <summary>
    /// The slot a drop at this point belongs in.
    /// </summary>
    /// <remarks>
    /// Compared against each card's own centre rather than against a column grid: in a
    /// wrapping layout the rows do not line up, and "past the middle of this card"
    /// is the only rule that behaves the same on every row.
    /// </remarks>
    private int IndexAt(Point point)
    {
        var index = 0;

        foreach (var child in Children.OfType<NoteCard>())
        {
            var bounds = new Rect(child.TranslatePoint(new Point(0, 0), this), child.RenderSize);
            if (bounds.Height <= 0)
            {
                index++;
                continue;
            }

            var belowRow = point.Y > bounds.Bottom;
            var pastCentre = point.Y >= bounds.Top && point.X > bounds.Left + bounds.Width / 2;

            if (belowRow || pastCentre)
            {
                index++;
            }
        }

        return Math.Clamp(index, 0, Children.Count);
    }

    private void ShowInsertionMark(int index)
    {
        var cards = Children.OfType<NoteCard>().ToList();
        if (cards.Count == 0)
        {
            return;
        }

        _insertionMark ??= CreateInsertionMark();

        var target = index < cards.Count ? cards[index] : cards[^1];
        var bounds = new Rect(target.TranslatePoint(new Point(0, 0), this), target.RenderSize);

        _insertionMark.Height = Math.Max(24, bounds.Height - 20);
        _insertionMark.Margin = new Thickness(
            index < cards.Count ? bounds.Left - 7 : bounds.Right - 9,
            bounds.Top + 10,
            0,
            0);
        _insertionMark.Visibility = Visibility.Visible;
    }

    private Border CreateInsertionMark()
    {
        var mark = new Border
        {
            Width = 2,
            CornerRadius = new CornerRadius(1),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Left,
            VerticalAlignment = System.Windows.VerticalAlignment.Top,
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };

        mark.SetResourceReference(BackgroundProperty, "WlBrand");

        // Added as a child of the panel so it scrolls with the board; it is skipped by
        // every OfType<NoteCard> pass, so it never counts as a drop slot of its own.
        Children.Add(mark);
        return mark;
    }

    private void HideInsertionMark()
    {
        if (_insertionMark is not null)
        {
            _insertionMark.Visibility = Visibility.Collapsed;
        }
    }

    /// <summary>The cards currently on the board, in the order they are drawn.</summary>
    public List<NoteCard> Cards => [.. Children.OfType<NoteCard>()];

    /// <summary>Clears the board, keeping the insertion mark alive across rebuilds.</summary>
    public void ClearCards()
    {
        foreach (var card in Children.OfType<NoteCard>().ToList())
        {
            Children.Remove(card);
        }
    }

    /// <summary>Adds a card, keeping the insertion mark last so it draws above the grid.</summary>
    public void AddCard(NoteCard card) => InsertCard(card, int.MaxValue);

    /// <summary>
    /// Puts a card at a given position among the other cards.
    /// </summary>
    /// <remarks>
    /// Indexed against the cards rather than against <see cref="System.Windows.Controls.Panel.Children"/>,
    /// because the insertion mark is a child too and has to stay last so it draws over
    /// the grid rather than between two cards.
    /// </remarks>
    public void InsertCard(NoteCard card, int index)
    {
        var cards = Children.OfType<NoteCard>().ToList();
        index = Math.Clamp(index, 0, cards.Count);

        var target = index < cards.Count
            ? Children.IndexOf(cards[index])
            : _insertionMark is null ? Children.Count : Children.IndexOf(_insertionMark);

        Children.Insert(target, card);
    }

    /// <summary>Takes one card off the board, leaving every other card where it is.</summary>
    public void RemoveCard(NoteCard card) => Children.Remove(card);

    private static NoteCard? FindCard(DependencyObject? source)
    {
        while (source is not null)
        {
            if (source is NoteCard card)
            {
                return card;
            }

            source = source is Visual or System.Windows.Media.Media3D.Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }

        return null;
    }
}
