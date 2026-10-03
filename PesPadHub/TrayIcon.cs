using WinForms = System.Windows.Forms;

namespace PesPadHub;

/// <summary>系统托盘图标: 双击恢复主窗口, 右键菜单可退出。</summary>
sealed class TrayIcon : IDisposable
{
    readonly WinForms.NotifyIcon _icon;
    readonly WinForms.ToolStripItem _showItem, _exitItem;

    public TrayIcon(Action show, Action exit)
    {
        var menu = new WinForms.ContextMenuStrip();
        _showItem = menu.Items.Add("", null, (_, _) => show());
        _exitItem = menu.Items.Add("", null, (_, _) => exit());

        _icon = new WinForms.NotifyIcon
        {
            Icon = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!),
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => show();

        ApplyLanguage();
        Loc.Changed += ApplyLanguage;
    }

    void ApplyLanguage()
    {
        _icon.Text = Loc.T("AppTitle");
        _showItem.Text = Loc.T("TrayShow");
        _exitItem.Text = Loc.T("TrayExit");
    }

    public void Dispose()
    {
        Loc.Changed -= ApplyLanguage;
        _icon.Visible = false;
        _icon.Dispose();
    }
}
