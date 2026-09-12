using System.Diagnostics;
using System.IO;
using System.Windows;
using WriteLite.Resources;
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
        AppBlock.Text = string.Join("\n",
            Strings.Diag_AppRunning,
            string.Format(Strings.Diag_AppVersion, _last.AppVersion),
            string.Format(Strings.Diag_AppBuild, _last.BuildConfiguration));
        FieldBlock.Text = string.Join("\n",
            string.Format(Strings.Diag_FieldDetected, _last.FieldDetected),
            string.Format(Strings.Diag_FieldProcess, _last.FieldProcess),
            string.Format(Strings.Diag_FieldControl, _last.FieldControl),
            string.Format(Strings.Diag_FieldRead, _last.FieldRead),
            string.Format(Strings.Diag_FieldWrite, _last.FieldWrite),
            $"ValuePattern: {_last.FieldValuePattern}",
            $"TextPattern: {_last.FieldTextPattern}",
            string.Format(Strings.Diag_FieldBounds, _last.FieldBounds),
            string.Format(Strings.Diag_FieldIssueCount, _last.LastIssueCount));
        EngineBlock.Text = string.Join("\n",
            string.Format(Strings.Diag_EngineStatus, _last.EngineStatus),
            string.Format(Strings.Diag_EngineLastSuccess, _last.LastSuccessUtc),
            string.Format(Strings.Diag_EngineDuration, _last.LastAnalysisDuration),
            string.Format(Strings.Diag_EngineIssueCount, _last.LastIssueCount),
            string.Format(Strings.Diag_EngineRestarts, _last.EngineRestarts),
            string.Format(Strings.Diag_EngineExtendedChecking, _last.ExtendedChecking));
        SystemBlock.Text = string.Join("\n",
            $"Windows: {_last.WindowsVersion}",
            $".NET: {_last.DotNetVersion}",
            string.Format(Strings.Diag_SystemDpi, _last.DpiScale),
            string.Format(Strings.Diag_SystemWorkArea, _last.WorkArea),
            string.Format(Strings.Diag_SystemAppMemory, _last.AppMemoryMb),
            string.Format(Strings.Diag_SystemChildMemory, _last.ChildMemoryMb),
            $"UI responsiveness: {_last.UiResponsiveness}",
            string.Format(Strings.Diag_SystemAnalysisQueues, _last.AnalysisQueues));
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
            MessageBox.Show(Strings.Diag_ReportCopied, Strings.Nav_Diagnostics);
        }
        catch (Exception ex)
        {
            MessageBox.Show(Strings.Diag_CopyFailed, Strings.Nav_Diagnostics);
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
            MessageBox.Show(Strings.Diag_OpenLogsFailed, Strings.Nav_Diagnostics);
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
            ok ? Strings.Diag_RestartSucceeded : Strings.Diag_RestartFailed,
            Strings.Nav_Diagnostics);
    }
}
