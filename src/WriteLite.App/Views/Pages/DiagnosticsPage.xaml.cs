using System.Diagnostics;
using System.IO;
using System.Windows;
using WriteLite.Services.Diagnostics;
using UserControl = System.Windows.Controls.UserControl;
using MessageBox = System.Windows.MessageBox;
using Clipboard = System.Windows.Clipboard;

namespace WriteLite.Views.Pages;

public partial class DiagnosticsPage : UserControl
{
    private WriteLiteDiagnosticsService? _service;
    private Func<Task<bool>>? _restartEngine;
    private WriteLiteDiagnosticsSnapshot? _last;

    public DiagnosticsPage()
    {
        InitializeComponent();
    }

    public void Bind(WriteLiteDiagnosticsService service, Func<Task<bool>>? restartEngine = null)
    {
        _service = service;
        _restartEngine = restartEngine;
        Refresh();
    }

    public void Refresh()
    {
        if (_service is null)
        {
            return;
        }

        _last = _service.Capture();
        AppBlock.Text =
            $"WriteLite: запущен\nВерсия: {_last.AppVersion}\nСборка: {_last.BuildConfiguration}";
        FieldBlock.Text =
            $"Обнаружено: {_last.FieldDetected}\nПроцесс: {_last.FieldProcess}\nЭлемент: {_last.FieldControl}\n" +
            $"Чтение: {_last.FieldRead}\nЗапись: {_last.FieldWrite}\nValuePattern: {_last.FieldValuePattern}\n" +
            $"TextPattern: {_last.FieldTextPattern}\nГраницы: {_last.FieldBounds}\nЗамечаний: {_last.LastIssueCount}";
        EngineBlock.Text =
            $"Статус: {_last.EngineStatus}\nПоследний успех: {_last.LastSuccessUtc}\n" +
            $"Длительность: {_last.LastAnalysisDuration}\nЗамечания: {_last.LastIssueCount}\n" +
            $"Перезапусков: {_last.EngineRestarts}\nРасширенная проверка: {_last.ExtendedChecking}";
        SystemBlock.Text =
            $"Windows: {_last.WindowsVersion}\n.NET: {_last.DotNetVersion}\n" +
            $"DPI / масштаб: {_last.DpiScale}\nРабочая область: {_last.WorkArea}\n" +
            $"Память WriteLite: {_last.AppMemoryMb} МБ\nПамять дочернего процесса: {_last.ChildMemoryMb} МБ\n" +
            $"UI responsiveness: {_last.UiResponsiveness}\nОчереди анализа: {_last.AnalysisQueues}";
        EventsList.ItemsSource = _last.RecentEvents.Reverse().ToList();
    }

    private void Refresh_Click(object sender, RoutedEventArgs e) => Refresh();

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_service is null || _last is null)
        {
            return;
        }

        try
        {
            Clipboard.SetText(_service.BuildCopyableReport(_last));
            MessageBox.Show("Отчёт скопирован в буфер обмена.", "Диагностика");
        }
        catch (Exception ex)
        {
            MessageBox.Show("Не удалось скопировать отчёт.", "Диагностика");
            _ = ex;
        }
    }

    private void OpenLogs_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var dir = WriteLiteDiagnosticsService.LogsDirectory;
            Directory.CreateDirectory(dir);
            Process.Start(new ProcessStartInfo
            {
                FileName = dir,
                UseShellExecute = true
            });
        }
        catch
        {
            MessageBox.Show("Не удалось открыть папку логов.", "Диагностика");
        }
    }

    private async void RestartEngine_Click(object sender, RoutedEventArgs e)
    {
        if (_restartEngine is null)
        {
            return;
        }

        var ok = await _restartEngine();
        Refresh();
        MessageBox.Show(
            ok ? "Расширенная проверка перезапущена." : "Не удалось перезапустить. Работает базовая проверка WriteLite.",
            "Диагностика");
    }
}
