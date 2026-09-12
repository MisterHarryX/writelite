using System.Drawing;
using System.Windows.Forms;
using WriteLite.Resources;

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
        _pauseItem = new ToolStripMenuItem(Strings.App_PauseCheck);
        _pauseItem.Click += (_, _) =>
        {
            _paused = !_paused;
            _pauseItem.Text = _paused ? Strings.App_ResumeCheck : Strings.App_PauseCheck;
            pauseChanged(_paused);
        };

        var openItem = new ToolStripMenuItem(Strings.App_OpenFromTray);
        openItem.Click += (_, _) => openMainWindow();

        var exitItem = new ToolStripMenuItem(Strings.App_Exit);
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
