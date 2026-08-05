using System.Windows;
using WinFormsTextBox = System.Windows.Forms.TextBox;

namespace WriteLite.TestHost;

public partial class TestHostWindow : Window
{
    private bool _alternate;

    public TestHostWindow()
    {
        InitializeComponent();
        WinFormsHost.Child = new WinFormsTextBox
        {
            Name = "WinFormsInput",
            AccessibleName = "WinFormsInput",
            Text = "Мы с другом пошёл в магазин",
            Dock = System.Windows.Forms.DockStyle.Fill
        };
        LongInput.Text = string.Join(Environment.NewLine, Enumerable.Range(1, 60).Select(i => $"Строка {i}: тестовый длинный текст для прокрутки."));
    }

    private void ChangeText_Click(object sender, RoutedEventArgs e)
    {
        _alternate = !_alternate;
        WpfInput.Text = _alternate ? "Модель глупая нужю до" : "Привет как тваи дела";
    }

    private void CloseHost_Click(object sender, RoutedEventArgs e) => Close();
}
