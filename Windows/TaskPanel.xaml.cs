using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DeepSeekPet.Models;
using DeepSeekPet.Services;

namespace DeepSeekPet.Windows;

/// <summary>任务面板行的显示模型（每次刷新整体重建，任务量小、代价可忽略）</summary>
public sealed class TaskRow
{
    public TaskItem Item { get; init; } = null!;
    public string TimeText { get; init; } = "";
    public string TagText { get; init; } = "";
    public bool DoneToday { get; init; }
    public bool Highlight { get; init; }
}

/// <summary>聊天气泡（INotifyPropertyChanged：流式增量只更新该 TextBlock，局部重绘不重建列表）</summary>
public sealed class ChatBubble : INotifyPropertyChanged
{
    private string _text = "";

    public string Role { get; init; } = "assistant";
    public bool IsUser => Role == "user";

    public string Text
    {
        get => _text;
        set
        {
            if (_text == value) return;
            _text = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Text)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// 桌宠头顶面板：任务列表 + AI 对话区。
/// 独立的透明窗口，跟随桌宠；聊天消息增量追加，流式输出时只更新最后一条气泡。
/// </summary>
public partial class TaskPanel : Window
{
    private const int MaxBubbles = 60;

    private readonly TaskStore _store;
    private readonly App _app;
    private readonly DispatcherTimer _highlightTimer;
    private HashSet<string> _highlightIds = new();
    private bool _below;

    // ---- 聊天状态 ----
    private readonly ObservableCollection<ChatBubble> _bubbles = new();
    private ChatBubble? _streaming;
    private bool _chatReady;

    public bool IsOpen => IsVisible;

    public TaskPanel(TaskStore store, App app)
    {
        _store = store;
        _app = app;
        InitializeComponent();

        _highlightTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(12) };
        _highlightTimer.Tick += (_, _) =>
        {
            _highlightTimer.Stop();
            _highlightIds.Clear();
            Refresh();
        };

        ChatList.ItemsSource = _bubbles;
        SubscribeAgent();

        SizeChanged += (_, _) => Follow();
        Loaded += (_, _) =>
        {
            Refresh();
            LoadChatHistoryOnce();
        };
    }

    // ---------------- 与对话 Agent 的接线 ----------------

    private void SubscribeAgent()
    {
        try
        {
            var agent = _app.Agent;
            if (agent == null)
            {
                AppLogger.Warn("TaskPanel 创建时 Agent 尚未就绪，聊天事件未订阅");
                return;
            }
            agent.DeltaReceived += OnChatDelta;
            agent.ResponseCompleted += OnChatCompleted;
            agent.StatusChanged += OnChatStatus;
            agent.ErrorOccurred += OnChatError;
        }
        catch (Exception ex)
        {
            AppLogger.Error("订阅对话事件失败", ex);
        }
    }

    /// <summary>首次显示时加载最近对话（重启恢复）；无历史则显示人设问候语</summary>
    private void LoadChatHistoryOnce()
    {
        if (_chatReady) return;
        _chatReady = true;
        try
        {
            var recent = _app.Agent?.Session.Snapshot();
            if (recent is { Count: > 0 })
            {
                foreach (var m in recent.TakeLast(24))
                    _bubbles.Add(new ChatBubble { Role = m.Role, Text = m.Content });
            }
            else
            {
                var greeting = _app.Persona?.Greeting ?? "咕噜咕噜～我在这里哦。";
                _bubbles.Add(new ChatBubble { Role = "assistant", Text = greeting });
                if (_app.Agent is { HasApiKey: false })
                    _bubbles.Add(new ChatBubble
                    {
                        Role = "assistant",
                        Text = "（提示：还没有配置 API Key。编辑 appconfig.json 填入 apiKey 后重启，我就能聊天和查资料啦～\n" +
                               "现在也可以直接说「明天9点提醒我开会」这样的指令哦。）"
                    });
            }
            ScrollToEnd(force: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error("加载聊天历史失败", ex);
        }
    }

    private void OnChatDelta(string delta)
    {
        try
        {
            if (_streaming == null)
            {
                _streaming = new ChatBubble { Role = "assistant", Text = "" };
                _bubbles.Add(_streaming);
                TrimBubbles();
            }
            _streaming.Text += delta;   // 增量追加：只触发该气泡的局部重绘
            ScrollToEnd();
        }
        catch { }
    }

    private void OnChatCompleted(string full)
    {
        try
        {
            if (_streaming != null)
            {
                _streaming.Text = full;   // 定稿（与流式内容一致）
                _streaming = null;
            }
            else
            {
                _bubbles.Add(new ChatBubble { Role = "assistant", Text = full });
                TrimBubbles();
            }
            ScrollToEnd();

            // 面板未打开时：让桌宠顶着气泡"说话"，用户不看列表也能收到回复
            if (!IsVisible) _app.Pet?.Say(full);
        }
        catch { }
    }

    private void OnChatStatus(string? status)
    {
        try { ChatStatus.Text = status ?? ""; }
        catch { }
    }

    private void OnChatError(string msg)
    {
        try
        {
            _bubbles.Add(new ChatBubble { Role = "assistant", Text = msg });
            TrimBubbles();
            ScrollToEnd();
            if (!IsVisible) _app.Pet?.Say(Shorten(msg, 60));
        }
        catch { }
    }

    private void TrimBubbles()
    {
        while (_bubbles.Count > MaxBubbles)
            _bubbles.RemoveAt(0);
    }

    private void ScrollToEnd(bool force = false)
    {
        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                // 用户主动上翻查看历史时不强制拉到底
                if (force || ChatScroll.VerticalOffset >= ChatScroll.ScrollableHeight - 40)
                    ChatScroll.ScrollToVerticalOffset(double.MaxValue);
            }
            catch { }
        }), DispatcherPriority.Background);
    }

    // ---------------- 聊天输入 ----------------

    private void ChatInput_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            SendChat();
        }
    }

    private void BtnSend_Click(object sender, RoutedEventArgs e) => SendChat();

    private void SendChat()
    {
        try
        {
            var text = ChatInput.Text?.Trim() ?? "";
            if (text.Length == 0) return;
            ChatInput.Clear();

            _bubbles.Add(new ChatBubble { Role = "user", Text = text });
            TrimBubbles();
            ScrollToEnd(force: true);

            _app.Agent?.Send(text);
            _app.Pet?.KeepAwake();
        }
        catch (Exception ex)
        {
            AppLogger.Error("发送消息失败", ex);
        }
    }

    private void BtnClearChat_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            _app.Agent?.ClearMemory();
            _streaming = null;
            _bubbles.Clear();
            _bubbles.Add(new ChatBubble { Role = "assistant", Text = "咕噜～对话记忆清空啦，我们重新开始吧！" });
            ScrollToEnd(force: true);
        }
        catch (Exception ex)
        {
            AppLogger.Error("清空对话失败", ex);
        }
    }

    /// <summary>用户从桌宠气泡点进来时，把输入框聚焦</summary>
    public void FocusInput()
    {
        try
        {
            Dispatcher.BeginInvoke(new Action(() => ChatInput.Focus()), DispatcherPriority.Loaded);
        }
        catch { }
    }

    // ---------------- 显示 / 隐藏 ----------------

    public void ShowPanel(bool follow)
    {
        if (!IsVisible)
        {
            Opacity = 0;
            Show(); // ShowActivated=False，弹出不抢当前焦点
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                FillBehavior = FillBehavior.Stop
            };
            fade.Completed += (_, _) => Opacity = 1;
            BeginAnimation(OpacityProperty, fade);
        }
        Refresh();
        if (follow) Follow();
        _app.Pet?.KeepAwake();
    }

    public void HidePanel()
    {
        if (!IsVisible) return;
        var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(120))
        {
            FillBehavior = FillBehavior.Stop
        };
        fade.Completed += (_, _) =>
        {
            Hide();
            BeginAnimation(OpacityProperty, null);
            Opacity = 1;
            _app.Pet?.OnPanelClosed();

            // 面板收起 20 秒后做一次资源巡检：此时若有可回收的渲染缓存，回收最合适
            var t = new DispatcherTimer { Interval = TimeSpan.FromSeconds(20) };
            t.Tick += (s2, _) =>
            {
                t.Stop();
                try { MemoryGuard.Check(); } catch { }
            };
            t.Start();
        };
        BeginAnimation(OpacityProperty, fade);
    }

    public void ShowHighlight(List<string> ids)
    {
        _highlightIds = new HashSet<string>(ids);
        _highlightTimer.Stop();
        _highlightTimer.Start();
        ShowPanel(follow: true);
    }

    public void ForceClose()
    {
        _highlightTimer.Stop();
        Hide();
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_app.IsExiting)
        {
            e.Cancel = true; // 面板只隐藏不关闭
            HidePanel();
        }
        base.OnClosing(e);
    }

    // ---------------- 内容刷新 ----------------

    public void Refresh()
    {
        try
        {
            if (!IsVisible) return;
            var today = DateTime.Today.ToString("yyyy-MM-dd");
            var rows = new List<TaskRow>(_store.Tasks.Count);
            foreach (var t in _store.Tasks)
            {
                rows.Add(new TaskRow
                {
                    Item = t,
                    TimeText = $"{t.Hour:D2}:{t.Minute:D2}",
                    TagText = t.ScheduleText(),
                    DoneToday = t.DoneDate == today,
                    Highlight = _highlightIds.Contains(t.Id)
                });
            }
            rows.Sort((a, b) =>
            {
                if (a.DoneToday != b.DoneToday) return a.DoneToday ? 1 : -1;
                int c = a.Item.Hour * 60 + a.Item.Minute - (b.Item.Hour * 60 + b.Item.Minute);
                return c != 0 ? c : string.CompareOrdinal(a.Item.Title, b.Item.Title);
            });
            TaskList.ItemsSource = rows;
            HeaderText.Text = "今日任务 · " + DateTime.Now.ToString("M月d日 dddd", CultureInfo.GetCultureInfo("zh-CN"));
            EmptyHint.Visibility = _store.Tasks.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            FooterText.Text = BuildFooter();
        }
        catch (Exception ex)
        {
            AppLogger.Error("刷新任务面板失败", ex);
        }
    }

    private string BuildFooter()
    {
        DateTime? best = null;
        TaskItem? bestItem = null;
        var now = DateTime.Now;
        foreach (var t in _store.Tasks)
        {
            var n = t.NextOccurrence(now);
            if (n == null) continue;
            if (best == null || n.Value < best.Value)
            {
                best = n;
                bestItem = t;
            }
        }
        if (best == null || bestItem == null) return "暂无启用中的提醒";
        string when = best.Value.Date == DateTime.Today
            ? $"今天 {best.Value:HH:mm}"
            : best.Value.Date == DateTime.Today.AddDays(1)
                ? $"明天 {best.Value:HH:mm}"
                : $"{best.Value:M月d日 HH:mm}";
        return $"下一次提醒：{when} · {bestItem.Title}";
    }

    // ---------------- 跟随桌宠定位 ----------------

    public void Follow()
    {
        if (!IsVisible || _app.Pet == null) return;
        try
        {
            var (cx, topY, bottomY) = _app.Pet.GetPetAnchorPx();
            var dpi = VisualTreeHelper.GetDpi(this);
            int wpx = (int)Math.Ceiling(ActualWidth * dpi.DpiScaleX);
            int hpx = (int)Math.Ceiling(ActualHeight * dpi.DpiScaleY);
            if (wpx <= 0 || hpx <= 0) return;

            var work = Native.GetWorkAreaForPoint(cx, topY);
            int left = cx - wpx / 2;
            int top = topY - hpx - 2;
            _below = false;
            if (top < work.Top - 2)
            {
                _below = true;
                top = bottomY + 2;
            }
            left = Math.Clamp(left, work.Left + 2, Math.Max(work.Left + 2, work.Right - wpx - 2));
            if (_below)
                top = Math.Clamp(top, work.Top + 2, Math.Max(work.Top + 2, work.Bottom - hpx - 2));

            SetTail(cx - left);

            Native.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, left, top, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        catch
        {
            // 定位失败不影响主流程
        }
    }

    private void SetTail(int petCenterOffsetPx)
    {
        try
        {
            var dpi = VisualTreeHelper.GetDpi(this);
            double off = petCenterOffsetPx / dpi.DpiScaleX - 6; // 尾巴宽 12，居中
            off = Math.Clamp(off, 10, Math.Max(10, ActualWidth - 24));
            TailDown.Visibility = _below ? Visibility.Collapsed : Visibility.Visible;
            TailUp.Visibility = _below ? Visibility.Visible : Visibility.Collapsed;
            TailDown.Margin = new Thickness(off, 0, 0, 2);
            TailUp.Margin = new Thickness(off, 2, 0, 0);
        }
        catch { }
    }

    // ---------------- 行交互 ----------------

    private void BtnAdd_Click(object sender, RoutedEventArgs e)
    {
        new TaskEditorWindow(_store, null).ShowDialog();
    }

    private void BtnClose_Click(object sender, RoutedEventArgs e) => HidePanel();

    private void TaskDone_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is CheckBox cb && cb.DataContext is TaskRow row)
                _store.ToggleDone(row.Item); // 变更事件会驱动刷新与调度重排
        }
        catch (Exception ex)
        {
            AppLogger.Error("切换完成状态失败", ex);
        }
    }

    private void TaskEdit_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (sender is Button b && b.DataContext is TaskRow row)
                new TaskEditorWindow(_store, row.Item).ShowDialog();
        }
        catch (Exception ex)
        {
            AppLogger.Error("打开任务编辑失败", ex);
        }
    }

    private static string Shorten(string s, int n)
    {
        s = s.Replace("\r", " ").Replace("\n", " ").Trim();
        return s.Length <= n ? s : s[..n] + "…";
    }
}
