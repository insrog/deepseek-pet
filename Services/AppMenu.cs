using System.Diagnostics;
using System.IO;
using System.Windows.Controls.Primitives;
using DeepSeekPet.Windows;
using Forms = System.Windows.Forms;
using Wpf = System.Windows.Controls;

namespace DeepSeekPet.Services;

/// <summary>菜单项类型</summary>
public enum MenuEntryKind
{
    Item,
    Separator,
    CheckItem,
    SubMenu
}

/// <summary>一个菜单项的定义（与具体 UI 框架无关）</summary>
public sealed class MenuEntry
{
    public MenuEntryKind Kind { get; init; } = MenuEntryKind.Item;
    public string Text { get; init; } = "";
    public bool IsChecked { get; init; }
    public bool IsEnabled { get; init; } = true;
    public Action? Action { get; init; }
    public List<MenuEntry> Children { get; } = new();
}

/// <summary>
/// 统一右键菜单：桌宠右键 / 贴边把手右键 / 托盘图标右键 三处共用同一份定义与同一套动作。
/// - 单一数据源：每次展开时调用 Build() 重新求值，勾选态（置顶/声音/自启/缩放）实时刷新；
/// - 渲染各随平台：WPF ContextMenu（桌宠）与 WinForms ContextMenuStrip（托盘）分别适配，
///   但菜单项、顺序、文案、行为完全一致。
/// </summary>
public static class AppMenu
{
    public static List<MenuEntry> Build(App app)
    {
        var s = app.Store.Settings;
        var pet = app.Pet;

        var zoomMenu = new MenuEntry { Kind = MenuEntryKind.SubMenu, Text = "缩放", IsEnabled = pet != null };
        foreach (var (z, label) in new[] { (0.75, "75%"), (1.0, "100%"), (1.25, "125%"), (1.5, "150%") })
        {
            double zz = z;
            zoomMenu.Children.Add(new MenuEntry
            {
                Kind = MenuEntryKind.CheckItem,
                Text = label,
                IsChecked = Math.Abs(s.Zoom - zz) < 0.01,
                Action = () => pet?.SetZoom(zz)
            });
        }

        return new List<MenuEntry>
        {
            new()
            {
                Text = app.PanelOrNull?.IsOpen == true ? "收起任务面板" : "显示任务面板",
                Action = () => app.TogglePanel()
            },
            new() { Text = "新建任务…", Action = () => SafeRun(() => new TaskEditorWindow(app.Store, null).ShowDialog()) },
            new() { Text = "设置…", Action = () => app.ShowSettings() },
            new() { Kind = MenuEntryKind.Separator },

            new() { Text = "清空对话记忆", Action = () => ClearMemory(app) },
            new() { Kind = MenuEntryKind.Separator },

            new()
            {
                Kind = MenuEntryKind.CheckItem, Text = "窗口置顶", IsChecked = s.TopMost, IsEnabled = pet != null,
                Action = () =>
                {
                    s.TopMost = !s.TopMost;
                    app.Store.TouchSettings();
                    pet?.ApplyTopMost();
                }
            },
            new()
            {
                Kind = MenuEntryKind.CheckItem, Text = "提醒声音", IsChecked = s.Sound,
                Action = () =>
                {
                    s.Sound = !s.Sound;
                    app.Store.TouchSettings();
                }
            },
            new()
            {
                Kind = MenuEntryKind.CheckItem, Text = "开机自动启动", IsChecked = AutoStart.IsEnabled(),
                Action = () => SafeRun(() => AutoStart.Set(!AutoStart.IsEnabled()))
            },
            zoomMenu,
            new()
            {
                Text = "取消边缘隐藏", IsEnabled = pet?.IsDocked == true,
                Action = () => pet?.Undock()
            },
            new() { Kind = MenuEntryKind.Separator },

            new() { Text = "打开数据目录", Action = () => OpenPath(app.Store.DataDir) },
            new() { Text = "打开日志目录", Action = () => OpenPath(Path.Combine(app.Store.DataDir, "logs")) },
            new() { Kind = MenuEntryKind.Separator },

            new() { Text = "退出 DeepSeekPet", Action = () => app.ExitApp() },
        };
    }

    // ---------------- 共享动作 ----------------

    private static void ClearMemory(App app)
    {
        app.Agent?.ClearMemory();
        app.Pet?.Say("咕噜～对话记忆已清空，我们重新开始吧。");
    }

    public static void OpenPath(string path)
    {
        SafeRun(() =>
        {
            if (File.Exists(path)) Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
            else Process.Start("explorer.exe", path);
        });
    }

    private static void SafeRun(Action action)
    {
        try { action(); }
        catch (Exception ex) { AppLogger.Error("菜单动作执行失败", ex); }
    }

    // ---------------- 渲染：WPF（桌宠 / 贴边把手） ----------------

    public static Wpf.ContextMenu BuildWpf(App app)
    {
        var menu = new Wpf.ContextMenu { Placement = PlacementMode.MousePoint };
        try
        {
            foreach (var e in Build(app)) menu.Items.Add(ToWpf(e));
        }
        catch (Exception ex)
        {
            AppLogger.Error("构建 WPF 菜单失败", ex);
        }
        return menu;
    }

    private static object ToWpf(MenuEntry e)
    {
        if (e.Kind == MenuEntryKind.Separator) return new Wpf.Separator();

        var mi = new Wpf.MenuItem
        {
            Header = e.Text,
            IsEnabled = e.IsEnabled,
            IsCheckable = e.Kind == MenuEntryKind.CheckItem,
            IsChecked = e.IsChecked
        };

        if (e.Kind == MenuEntryKind.SubMenu)
        {
            foreach (var c in e.Children) mi.Items.Add(ToWpf(c));
            return mi;
        }

        if (e.Action is { } action)
            mi.Click += (_, _) => SafeRun(action);
        return mi;
    }

    // ---------------- 渲染：WinForms（托盘） ----------------

    /// <summary>构建托盘菜单（每次展开前调用可拿到最新勾选态；由调用方负责替换旧菜单）</summary>
    public static Forms.ContextMenuStrip BuildWinForms(App app)
    {
        var menu = new Forms.ContextMenuStrip { ShowImageMargin = false };
        try { menu.Font = new System.Drawing.Font("Microsoft YaHei UI", 9f); } catch { }
        try
        {
            foreach (var e in Build(app)) menu.Items.Add(ToWinForms(e));
        }
        catch (Exception ex)
        {
            AppLogger.Error("构建托盘菜单失败", ex);
        }
        return menu;
    }

    /// <summary>把最新菜单填入已有 ContextMenuStrip（托盘 Opening 时调用，保证勾选态实时）</summary>
    public static void FillWinForms(Forms.ContextMenuStrip menu, App app)
    {
        try
        {
            menu.Items.Clear();
            foreach (var e in Build(app)) menu.Items.Add(ToWinForms(e));
        }
        catch (Exception ex)
        {
            AppLogger.Error("刷新托盘菜单失败", ex);
        }
    }

    private static Forms.ToolStripItem ToWinForms(MenuEntry e)
    {
        if (e.Kind == MenuEntryKind.Separator) return new Forms.ToolStripSeparator();

        if (e.Kind == MenuEntryKind.SubMenu)
        {
            var sub = new Forms.ToolStripMenuItem(e.Text) { Enabled = e.IsEnabled };
            foreach (var c in e.Children) sub.DropDownItems.Add(ToWinForms(c));
            return sub;
        }

        var mi = new Forms.ToolStripMenuItem(e.Text)
        {
            Enabled = e.IsEnabled,
            Checked = e.Kind == MenuEntryKind.CheckItem && e.IsChecked,
            CheckOnClick = false
        };
        if (e.Action is { } action)
            mi.Click += (_, _) => SafeRun(action);
        return mi;
    }
}
