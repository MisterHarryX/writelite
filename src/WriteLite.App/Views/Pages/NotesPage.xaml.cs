using System.Windows;
using System.Windows.Controls;
using WriteLite.Controls;
using WriteLite.Services;
using WriteLite.Services.Notes;
using ContextMenu = System.Windows.Controls.ContextMenu;
using MenuItem = System.Windows.Controls.MenuItem;
using MessageBox = System.Windows.MessageBox;
using TextChangedEventArgs = System.Windows.Controls.TextChangedEventArgs;
using UserControl = System.Windows.Controls.UserControl;

namespace WriteLite.Views.Pages;

/// <summary>
/// The notes board.
/// </summary>
/// <remarks>
/// A board rather than a list, because the things people put here — a task, a shopping
/// list, a goal for the quarter — are glanceable objects rather than rows of a table.
/// The page owns no state beyond the current filter, search and sort: every card is
/// rendered from <see cref="NotesService"/> and every gesture goes straight back to
/// it, so what is on screen and what is on disk cannot drift apart.
/// </remarks>
public partial class NotesPage : UserControl
{
    private readonly Dictionary<string, NoteCard> _cards = [];
    private readonly HashSet<string> _leaving = [];
    private readonly Dictionary<string, int> _dismissalTokens = [];
    private int _dismissals;

    private NotesService? _notes;
    private Action? _changedHandler;
    private NotesFilter _filter = NotesFilter.All;
    private NotesSort _sort = NotesSort.Modified;
    private string _search = string.Empty;
    private bool _loaded;
    private bool _refreshQueued;

    public NotesPage()
    {
        InitializeComponent();
        Board.ReorderRequested += OnReorderRequested;
    }

    /// <summary>Raised when a note's text should be looked up in the dictionary.</summary>
    public event Action<string>? WordNavigationRequested;

    public void Bind(NotesService notes)
    {
        // Unsubscribed first. Binding twice used to leave the old lambda attached, so a
        // second bind meant every change refreshed the board twice, a third meant three
        // times, and the page could never be collected because the service held it.
        if (_notes is not null && _changedHandler is not null)
        {
            _notes.Changed -= _changedHandler;
        }

        _notes = notes;
        _changedHandler = QueueRefresh;
        _notes.Changed += _changedHandler;

        Reload();
    }

    /// <summary>Called when the page comes into view.</summary>
    public void Reload()
    {
        Refresh();

        // The board arrives as a short cascade the first time it is opened, and
        // silently on every visit after that — a page re-animating on every return
        // reads as a reload rather than as remembered state.
        if (!_loaded && _notes is not null)
        {
            _loaded = true;
            Motion.RevealSequence(Board.Cards.Cast<FrameworkElement>());
        }
    }

    // ── Rendering ────────────────────────────────────────────────────────────

    /// <summary>
    /// Asks for one refresh, however many changes arrive before it runs.
    /// </summary>
    /// <remarks>
    /// <see cref="NotesService.Changed"/> fires once per mutation and every mutation
    /// used to post its own redraw. Ticking fifty tasks quickly queued fifty full
    /// rebuilds of the board behind the fifty clicks, which is what the freeze was.
    /// </remarks>
    private void QueueRefresh()
    {
        if (_refreshQueued)
        {
            return;
        }

        _refreshQueued = true;

        Dispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() =>
            {
                _refreshQueued = false;
                Refresh();
            }));
    }

    /// <summary>
    /// Brings the board in line with the store, moving as little as possible.
    /// </summary>
    /// <remarks>
    /// Reconciled rather than rebuilt. Clearing the board and constructing a fresh
    /// <see cref="NoteCard"/> per note — each one a BAML parse and a template
    /// realisation — ran on every change and every keystroke in the search box, and it
    /// destroyed the very card the user was interacting with: the checkmark could not
    /// appear because the checkbox drawing it no longer existed a frame later.
    ///
    /// A card that is no longer wanted leaves through a fade rather than vanishing, and
    /// is only detached once that fade has finished — so completing a task reads as the
    /// card acknowledging the tick and then stepping off the board, which is what the
    /// gesture is.
    /// </remarks>
    private void Refresh()
    {
        if (_notes is null || Board.IsDragging)
        {
            return;
        }

        var notes = _notes.Query(_filter, _search, _sort);
        var wanted = new HashSet<string>(notes.Count);

        for (var index = 0; index < notes.Count; index++)
        {
            var note = notes[index];
            wanted.Add(note.Id);

            if (_cards.TryGetValue(note.Id, out var existing))
            {
                // A card on its way out that has come back — unticked before the fade
                // finished — is caught here and restored rather than left half-faded.
                // Forgetting the token is what stops the cancelled fade's callback from
                // detaching the card the reader has just asked to keep.
                if (_leaving.Remove(note.Id))
                {
                    _dismissalTokens.Remove(note.Id);
                    existing.BeginAnimation(OpacityProperty, null);
                    existing.Opacity = 1;
                }

                existing.Bind(note);
            }
            else
            {
                existing = BuildCard(note);
                _cards[note.Id] = existing;
                Board.InsertCard(existing, index);
            }
        }

        foreach (var id in _cards.Keys.Where(id => !wanted.Contains(id)).ToList())
        {
            Dismiss(id);
        }

        SweepOrphans();
        Reorder(notes);

        // The empty state waits for the last card to finish leaving. Showing "Пока
        // пусто" over a card that is still fading says the board is empty while the
        // reader can plainly see that it is not.
        var empty = notes.Count == 0;
        var settled = empty && _leaving.Count == 0;

        EmptyState.Visibility = settled ? Visibility.Visible : Visibility.Collapsed;
        Board.Visibility = settled ? Visibility.Collapsed : Visibility.Visible;

        if (empty)
        {
            RenderEmptyState();
        }

        RenderStatus(notes.Count);
    }

    /// <summary>
    /// Fades a card off the board and detaches it once that fade has actually finished.
    /// </summary>
    /// <remarks>
    /// The card stays in <c>_cards</c> while it fades, so a note that comes back before
    /// the fade ends — the reader unticking a task they ticked by mistake — is found
    /// and restored rather than drawn a second time underneath its own ghost.
    ///
    /// The token is what makes that safe. <see cref="Motion.FadeOut"/>'s callback is the
    /// clock's <c>Completed</c> event, which WPF also raises when the clock is replaced
    /// or removed — so a callback firing is not proof that this fade ran to the end. Two
    /// dismissals in quick succession would otherwise have the second one's
    /// <c>BeginAnimation</c> cancel the first clock, whose callback then detached a card
    /// that was mid-fade and still a child of the board. The board ended up holding a
    /// pile of invisible ghosts, one per burst, and drew a fresh card beside them every
    /// time the note came back.
    /// </remarks>
    private void Dismiss(string noteId)
    {
        if (!_cards.TryGetValue(noteId, out var card) || _leaving.Contains(noteId))
        {
            return;
        }

        if (!Motion.IsEnabled)
        {
            Detach(noteId, card);
            return;
        }

        _leaving.Add(noteId);

        var token = ++_dismissals;
        _dismissalTokens[noteId] = token;

        Motion.FadeOut(card, () =>
        {
            // Only this dismissal's own clock reaching the end may detach the card.
            if (!_dismissalTokens.TryGetValue(noteId, out var current) || current != token)
            {
                return;
            }

            Detach(noteId, card);
        });
    }

    /// <summary>
    /// Removes any card on the board that this page no longer accounts for.
    /// </summary>
    /// <remarks>
    /// The invariant is that every card drawn is the card registered for its note —
    /// including while it fades, which is why a leaving card stays in the map. Anything
    /// else on the board cannot be found, reused or brought back by anything, so leaving
    /// it there only lets the board accumulate invisible layers. Cheap enough to run on
    /// every refresh, and self-healing, which is worth more than trusting that no future
    /// edit can break the invariant again.
    /// </remarks>
    private void SweepOrphans()
    {
        foreach (var card in Board.Cards)
        {
            var registered = card.Note is { } note
                             && _cards.TryGetValue(note.Id, out var current)
                             && ReferenceEquals(current, card);

            if (!registered)
            {
                Board.RemoveCard(card);
            }
        }
    }

    /// <summary>Takes a card off the board and forgets it, whatever state it was in.</summary>
    private void Detach(string noteId, NoteCard card)
    {
        _leaving.Remove(noteId);
        _dismissalTokens.Remove(noteId);

        // Only if this is still the card registered for that note: a rebuilt card must
        // not be dropped from the map by its predecessor's callback.
        if (_cards.TryGetValue(noteId, out var registered) && ReferenceEquals(registered, card))
        {
            _cards.Remove(noteId);
        }

        Board.RemoveCard(card);

        // The board is now settled, so the empty state — held back while the card was
        // still on screen — gets its chance to be right.
        if (_leaving.Count == 0)
        {
            QueueRefresh();
        }
    }

    /// <summary>
    /// Puts the cards into the order the query asked for, moving only what is out of place.
    /// </summary>
    /// <remarks>
    /// Positions are counted among the cards that are staying. A card that is fading out
    /// still occupies a slot in the panel, and using raw child indices while one is in
    /// flight would shuffle the cards either side of it for no reason.
    /// </remarks>
    private void Reorder(IReadOnlyList<Note> notes)
    {
        for (var index = 0; index < notes.Count; index++)
        {
            if (!_cards.TryGetValue(notes[index].Id, out var card))
            {
                continue;
            }

            var cards = Board.Cards;
            var at = cards.IndexOf(card);
            if (at < 0)
            {
                continue;
            }

            // Where this card currently sits, ignoring the ones on their way out.
            var settled = 0;
            for (var scan = 0; scan < at; scan++)
            {
                if (cards[scan].Note is { } note && !_leaving.Contains(note.Id))
                {
                    settled++;
                }
            }

            if (settled == index)
            {
                continue;
            }

            Board.RemoveCard(card);

            // Translate the wanted position among staying cards back into a board index.
            var remaining = Board.Cards;
            var target = remaining.Count;
            var seen = 0;

            for (var scan = 0; scan < remaining.Count; scan++)
            {
                if (seen == index)
                {
                    target = scan;
                    break;
                }

                if (remaining[scan].Note is { } note && !_leaving.Contains(note.Id))
                {
                    seen++;
                }
            }

            Board.InsertCard(card, target);
        }
    }

    private NoteCard BuildCard(Note note)
    {
        var card = new NoteCard();
        card.Bind(note);

        card.Opened += Open;
        card.CompletionChanged += OnCompletionChanged;
        card.PinChanged += (target, pinned) => _notes?.SetPinned(target.Id, pinned);
        card.ItemChanged += (target, item, done) => _notes?.SetItemDone(target.Id, item.Id, done);
        card.DuplicateRequested += target => _notes?.Duplicate(target.Id);
        card.DeleteRequested += Delete;

        return card;
    }

    /// <summary>
    /// A card was ticked or unticked.
    /// </summary>
    /// <remarks>
    /// The card has already restyled itself and started its tick before this runs; all
    /// that is left is to tell the store, which updates memory immediately and lets its
    /// own debounce carry the write to disk. Nothing on this path touches a file, so
    /// the dispatcher is free for the animation that is already running.
    /// </remarks>
    private void OnCompletionChanged(Note note, bool completed) => _notes?.SetCompleted(note.Id, completed);

    private void RenderEmptyState()
    {
        var searching = !string.IsNullOrWhiteSpace(_search);

        (EmptyTitle.Text, EmptyHint.Text) = (searching, _filter) switch
        {
            (true, _) => ("Ничего не найдено",
                $"По запросу «{_search.Trim()}» нет ни одной заметки. Попробуйте другое слово или другой фильтр."),
            (false, NotesFilter.Tasks) => ("Задач пока нет",
                "Создайте задачу или список — их можно отмечать выполненными прямо на карточке."),
            (false, NotesFilter.Goals) => ("Целей пока нет",
                "Цель — это крупная задача с подпунктами. Подойдёт для того, что не делается за один раз."),
            (false, NotesFilter.Completed) => ("Здесь пока пусто",
                "Выполненные задачи и цели собираются на этой вкладке. Любую из них можно вернуть в работу."),
            _ => ("Пока пусто",
                "Создайте первую заметку, задачу или цель — всё останется на этом компьютере.")
        };
    }

    private void RenderStatus(int shown)
    {
        if (_notes is null)
        {
            return;
        }

        var counts = _notes.Counts();
        var parts = new List<string>
        {
            $"{shown} {RussianPlural.Form(shown, "КАРТОЧКА", "КАРТОЧКИ", "КАРТОЧЕК")}"
        };

        if (counts.Tasks > 0)
        {
            parts.Add($"{counts.Tasks} В РАБОТЕ");
        }

        if (counts.Completed > 0)
        {
            parts.Add($"{counts.Completed} ВЫПОЛНЕНО");
        }

        Controls.Type.SetTracked(StatusText, string.Join(" · ", parts));
    }

    // ── Commands ─────────────────────────────────────────────────────────────

    private void NewNote_Click(object sender, RoutedEventArgs e)
    {
        // A menu rather than four buttons in the header: the kinds are a choice made
        // once per card, and four primary buttons would compete with the board itself.
        var menu = new ContextMenu
        {
            Style = TryFindResource("WlContextMenu") as Style,
            PlacementTarget = NewNoteButton,
            Placement = System.Windows.Controls.Primitives.PlacementMode.Bottom
        };

        menu.Items.Add(KindItem("Заметка", NoteKind.Note));
        menu.Items.Add(KindItem("Задача", NoteKind.Task));
        menu.Items.Add(KindItem("Список", NoteKind.Checklist));
        menu.Items.Add(KindItem("Цель", NoteKind.Goal));

        menu.IsOpen = true;
    }

    private MenuItem KindItem(string header, NoteKind kind)
    {
        var item = new MenuItem
        {
            Header = header,
            Style = TryFindResource("WlMenuItem") as Style
        };

        item.Click += (_, _) => Create(kind);
        return item;
    }

    private void Create(NoteKind kind)
    {
        if (_notes is null)
        {
            return;
        }

        // A new task starts on the tab that will show it, so the card the user just
        // asked for is never created into a filter that hides it.
        if (_filter == NotesFilter.Completed)
        {
            TabAll.IsChecked = true;
        }
        else if (kind == NoteKind.Goal && _filter == NotesFilter.Tasks)
        {
            TabGoals.IsChecked = true;
        }
        else if (kind is NoteKind.Task or NoteKind.Checklist && _filter == NotesFilter.Goals)
        {
            TabTasks.IsChecked = true;
        }

        var note = _notes.Create(kind);
        Open(note);
    }

    private void Open(Note note)
    {
        if (_notes is null)
        {
            return;
        }

        var editor = new NoteEditorWindow(note)
        {
            Owner = Window.GetWindow(this)
        };

        editor.WordNavigationRequested += word => WordNavigationRequested?.Invoke(word);

        var result = editor.ShowDialog();

        if (result == true)
        {
            _notes.Update(editor.Result);
        }
        else if (editor.WasDeleted)
        {
            _notes.Delete(note.Id);
        }
        else if (editor.IsAbandoned)
        {
            // A card created a moment ago and closed without a word in it was a
            // mis-click; leaving an empty card on the board is worse than not
            // creating one. Anything that was typed is kept.
            _notes.Delete(note.Id);
        }
        else
        {
            // Cancelled: the note object was edited in place by the dialog, so the
            // board is redrawn from the store to discard whatever it changed.
            Refresh();
        }
    }

    private void Delete(Note note)
    {
        if (_notes is null)
        {
            return;
        }

        var confirm = MessageBox.Show(
            $"Удалить «{note.DisplayTitle}»?",
            "Заметки",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);

        if (confirm == MessageBoxResult.Yes)
        {
            _notes.Delete(note.Id);
        }
    }

    private void OnReorderRequested(string noteId, int index)
    {
        if (_notes is null)
        {
            return;
        }

        // Dragging is a statement about order, so it switches the board to the manual
        // sort rather than being silently overruled by "newest first" on the next redraw.
        if (_sort != NotesSort.Manual)
        {
            _sort = NotesSort.Manual;
            SortBox.SelectedIndex = 4;
        }

        _notes.Reorder(noteId, index);
        Refresh();
    }

    // ── Filter, search, sort ─────────────────────────────────────────────────

    private void Filter_Changed(object sender, RoutedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        _filter = sender switch
        {
            _ when ReferenceEquals(sender, TabTasks) => NotesFilter.Tasks,
            _ when ReferenceEquals(sender, TabGoals) => NotesFilter.Goals,
            _ when ReferenceEquals(sender, TabCompleted) => NotesFilter.Completed,
            _ => NotesFilter.All
        };

        Refresh();
        BoardScroll.ScrollToHome();
    }

    private void Search_Changed(object sender, TextChangedEventArgs e)
    {
        _search = SearchBox.Text ?? string.Empty;
        Refresh();
    }

    private void Sort_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!IsInitialized)
        {
            return;
        }

        _sort = SortBox.SelectedIndex switch
        {
            1 => NotesSort.Created,
            2 => NotesSort.DueDate,
            3 => NotesSort.Title,
            4 => NotesSort.Manual,
            _ => NotesSort.Modified
        };

        Refresh();
    }
}
