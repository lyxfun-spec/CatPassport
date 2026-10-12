using System.Drawing;
using System.Windows.Forms;

namespace CatAssistant.App.Services;

public sealed class TrayIconService : IDisposable
{
    private readonly NotifyIcon _icon;

    public TrayIconService(string title, Action show, Action exit)
    {
        var menu = new ContextMenuStrip();
        menu.Items.Add("打开", null, (_, _) => show());
        menu.Items.Add("退出", null, (_, _) => exit());
        _icon = new NotifyIcon { Text = title, Icon = SystemIcons.Application, Visible = true, ContextMenuStrip = menu };
        _icon.DoubleClick += (_, _) => show();
    }

    public void Dispose() { _icon.Visible = false; _icon.Dispose(); }
}
