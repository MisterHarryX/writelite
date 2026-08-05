using System.Drawing;
using System.Windows.Forms;

namespace WriteLite.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _pauseItem;
    private bool _paused;

    public TrayIconService(
        Action openMainWindow,
        Action<bool> pauseChanged,
        Action exitRequested)
    {
        _pauseItem = new ToolStripMenuItem("Приостановить проверку");
        _pauseItem.Click += (_, _) =>
        {
            _paused = !_paused;
            _pauseItem.Text = _paused ? "Продолжить проверку" : "Приостановить проверку";
            pauseChanged(_paused);
        };

        var openItem = new ToolStripMenuItem("Открыть WriteLite");
        openItem.Click += (_, _) => openMainWindow();

        var exitItem = new ToolStripMenuItem("Выйти");
        exitItem.Click += (_, _) => exitRequested();

        var menu = new ContextMenuStrip();
        menu.Items.Add(openItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_pauseItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(exitItem);

        _notifyIcon = new NotifyIcon
        {
            Text = "WriteLite",
            Icon = AppIconLoader.CreateNotifyIcon(),
            Visible = true,
            ContextMenuStrip = menu
        };

        _notifyIcon.DoubleClick += (_, _) => openMainWindow();
    }

    public void Dispose()
    {
        _notifyIcon.Visible = false;
        _notifyIcon.Dispose();
    }
}
