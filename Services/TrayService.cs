using System.Diagnostics;
using System.Drawing;
using System.IO;
using Forms = System.Windows.Forms;

namespace DeepSeekPet.Services;

/// <summary>
/// 系统托盘图标（开机自启后 1-2 秒即可从托盘看到程序，无需等待界面完全就绪）。
/// - 左键/双击：打开任务+聊天面板
/// - 右键菜单：显示面板 / 新建任务 / 打开配置文件 / 打开数据目录 / 退出
/// 所有菜单动作统一 marshal 到 WPF UI 线程执行。
/// </summary>
public sealed class TrayService : IDisposable
{
    private readonly App _app;
    private readonly Forms.NotifyIcon _icon;
    private bool _disposed;

    public TrayService(App app)
    {
        _app = app;

        _icon = new Forms.NotifyIcon
        {
            Text = "DeepSeek 鲸鱼娘",
            Icon = LoadIcon(),
            Visible = true
        };

        _icon.MouseClick += (_, e) =>
        {
            if (e.Button == Forms.MouseButtons.Left) OnUi(ShowPanel);
        };
        _icon.DoubleClick += (_, _) => OnUi(ShowPanel);
        _icon.ContextMenuStrip = BuildMenu();
    }

    private void OnUi(Action action)
    {
        try
        {
            if (_app.Dispatcher.CheckAccess()) action();
            else _app.Dispatcher.BeginInvoke(action);
        }
        catch { }
    }

    private static Icon LoadIcon()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (!string.IsNullOrEmpty(exe))
            {
                var ico = Icon.ExtractAssociatedIcon(exe);
                if (ico != null) return ico;
            }
        }
        catch { }
        try { return SystemIcons.Application; }
        catch { return SystemIcons.WinLogo; }
    }

    private Forms.ContextMenuStrip BuildMenu()
    {
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        try { menu.Font = new Font("Microsoft YaHei UI", 9f); } catch { }

        // 与桌宠右键菜单共用同一份定义（AppMenu）：每次展开前重建，勾选态实时
        menu.Opening += (_, _) => AppMenu.FillWinForms(menu, _app);
        AppMenu.FillWinForms(menu, _app);
        return menu;
    }

    private void ShowPanel()
    {
        try { _app.ShowPanelAndFocus(); }
        catch { }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _icon.Visible = false;
            _icon.Dispose();
        }
        catch { }
    }
}
