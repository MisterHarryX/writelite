using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using WriteLite.Services;
using WriteLite.Services.FieldActions;
using Brush = System.Windows.Media.Brush;
using Button = System.Windows.Controls.Button;
using RadioButton = System.Windows.Controls.RadioButton;
using Size = System.Windows.Size;

namespace WriteLite.Views;

/// <summary>
/// The card the indicator's pen opens: one proposal for a fragment of another application's text.
/// </summary>
/// <remarks>
/// Like the correction card, it never takes the keyboard (<c>WS_EX_NOACTIVATE</c>): the field the
/// user is writing in keeps its caret and selection the whole time, so «Заменить» writes exactly
/// where they were. The window shows state; the controller in <c>App.FieldActions.cs</c> runs the
/// action and the write.
/// </remarks>
public partial class TextActionWindow : Window
{
    private FieldTextAction _action;
    private string _result = string.Empty;
    private bool _syncingChips;

    public TextActionWindow()
    {
        InitializeComponent();
        AppIconLoader.ApplyTo(this);
        NoActivateWindow.Attach(this);
        BuildChips();
    }

    public event Action<FieldTextAction>? ActionChosen;
    public event Action<string>? ReplaceRequested;
    public event Action? RetryRequested;
    public event Action? Closed2;

    public FieldTextAction CurrentAction => _action;

    private void BuildChips()
    {
        foreach (var action in new[]
                 {
                     FieldTextAction.Translate, FieldTextAction.Shorten, FieldTextAction.Improve,
                     FieldTextAction.Formal, FieldTextAction.Simplify,
                 })
        {
            var chip = new RadioButton
            {
                Content = action == FieldTextAction.Translate ? "Перевод" : FieldTextActionService.Title(action),
                Tag = action,
                GroupName = "TextActionChips",
                Focusable = false,
                Style = (Style)FindResource("WlTabButton"),
                Margin = new Thickness(0, 0, 4, 0),
            };
            chip.Checked += (_, _) =>
            {
                if (_syncingChips) return;
                ActionChosen?.Invoke(action);
            };
            ActionChips.Children.Add(chip);
        }
    }

    public void ShowBusy(FieldTextAction action, FieldTextScope scope, Rect physicalFieldBounds)
    {
        _action = action;
        _result = string.Empty;
        TitleText.Text = FieldTextActionService.Title(action, scope.Text);
        ScopeText.Text = scope.KindLabel.ToUpperInvariant();
        OriginalText.Text = scope.Text;
        ResultText.Text = string.Empty;
        BusyText.Text = action == FieldTextAction.Translate ? "Переводим…" : "Готовим вариант…";
        BusyPanel.Visibility = Visibility.Visible;
        NoticeText.Visibility = Visibility.Collapsed;
        FeedbackText.Visibility = Visibility.Collapsed;
        ReplaceButton.IsEnabled = false;
        CopyButton.IsEnabled = false;
        RetryButton.Visibility = action == FieldTextAction.Translate ? Visibility.Collapsed : Visibility.Visible;
        RetryButton.IsEnabled = false;
        SyncChips(action);

        if (!IsVisible)
        {
            PlaceNear(physicalFieldBounds);
            Show();
        }
    }

    public void ShowResult(FieldActionResult result)
    {
        _result = result.Suggestion;
        BusyPanel.Visibility = Visibility.Collapsed;
        ResultText.Text = result.Suggestion;
        var notice = result.Notice;
        if (result.IsNoOp)
        {
            notice = result.Action == FieldTextAction.Shorten
                ? "Текст уже достаточно короткий — менять нечего."
                : "Менять нечего: вариант совпал с исходным.";
        }

        NoticeText.Text = notice ?? string.Empty;
        NoticeText.Visibility = string.IsNullOrWhiteSpace(notice) ? Visibility.Collapsed : Visibility.Visible;
        ReplaceButton.IsEnabled = !result.IsNoOp && !string.IsNullOrWhiteSpace(result.Suggestion);
        CopyButton.IsEnabled = !string.IsNullOrWhiteSpace(result.Suggestion);
        RetryButton.IsEnabled = true;
    }

    public void ShowError(string message)
    {
        BusyPanel.Visibility = Visibility.Collapsed;
        ResultText.Text = string.Empty;
        ShowFeedback(message, isError: true);
        RetryButton.IsEnabled = true;
    }

    public void ShowFeedback(string message, bool isError)
    {
        FeedbackText.Text = message;
        FeedbackText.Foreground = (Brush)FindResource(isError ? "WlWarning" : "WlSuccessText");
        FeedbackText.Visibility = Visibility.Visible;
    }

    public void SetApplying(bool applying)
    {
        ReplaceButton.IsEnabled = !applying && !string.IsNullOrWhiteSpace(_result);
        ReplaceButton.Content = applying ? "Заменяем…" : "Заменить";
    }

    private void SyncChips(FieldTextAction action)
    {
        _syncingChips = true;
        foreach (var child in ActionChips.Children)
        {
            if (child is RadioButton { Tag: FieldTextAction a } chip) chip.IsChecked = a == action;
        }

        _syncingChips = false;
    }

    private void PlaceNear(Rect physicalFieldBounds)
    {
        var dpi = VisualTreeHelper.GetDpi(this);
        var field = OverlayPlacementService.Scale(physicalFieldBounds, dpi.DpiScaleX, dpi.DpiScaleY);
        var workArea = PopupWorkArea.GetCurrent(physicalFieldBounds, dpi.DpiScaleX, dpi.DpiScaleY);
        var placement = OverlayPlacementService.PlacePanel(field, workArea, new Size(Width, 420));
        Left = placement.Location.X;
        Top = Math.Min(placement.Location.Y, workArea.Bottom - 440);
    }

    private void Replace_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_result)) ReplaceRequested?.Invoke(_result);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (string.IsNullOrEmpty(_result)) return;
        try
        {
            System.Windows.Clipboard.SetText(_result);
            ShowFeedback("Скопировано — вставьте сочетанием Ctrl+V.", isError: false);
        }
        catch (Exception ex)
        {
            CompatibilityLogger.Technical("clipboard-copy-failed", $"type={ex.GetType().Name}");
        }
    }

    private void Retry_Click(object sender, RoutedEventArgs e) => RetryRequested?.Invoke();

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Hide();
        Closed2?.Invoke();
    }
}
