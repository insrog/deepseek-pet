using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using DeepSeekPet.Services;

namespace DeepSeekPet.Windows;

/// <summary>
/// 桌宠"说话气泡"：AI 回复/提醒语在桌宠头顶短暂悬浮显示。
/// - 透明无边框、置顶、不抢焦点；
/// - 显示 6.5 秒后自动淡出（鼠标悬停时保持）；
/// - 点击气泡打开任务面板并聚焦输入框；
/// - 轻量：一个窗口复用，不重复创建。
/// </summary>
public partial class SpeechBubble : Window
{
    private readonly App _app;
    private readonly DispatcherTimer _hideTimer;
    private bool _hover;

    public SpeechBubble(App app)
    {
        _app = app;
        InitializeComponent();

        _hideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(6.5) };
        _hideTimer.Tick += (_, _) =>
        {
            _hideTimer.Stop();
            if (!_hover) FadeOut();
        };

        MouseEnter += (_, _) => _hover = true;
        MouseLeave += (_, _) => _hover = false;
        MouseLeftButtonDown += (_, _) =>
        {
            HideBubble();
            try { _app.ShowPanelAndFocus(); } catch { }
        };

        Loaded += (_, _) => Follow();
        SizeChanged += (_, _) => Follow();

        // 虚拟桌面/DPI 变化后重算位置
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(Follow), DispatcherPriority.Loaded);
    }

    /// <summary>显示一段话（超长自动截断，避免气泡过大）</summary>
    public void ShowText(string text)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(text)) return;
            text = text.Trim();
            MsgText.Text = text.Length > 240 ? text[..240] + "…" : text;

            if (!IsVisible) Show();
            Opacity = 0;
            var fade = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(150))
            {
                FillBehavior = FillBehavior.Stop
            };
            fade.Completed += (_, _) => Opacity = 1;
            BeginAnimation(OpacityProperty, fade);

            _hover = false;
            Follow();
            _hideTimer.Stop();
            _hideTimer.Start();
        }
        catch (Exception ex)
        {
            AppLogger.Error("显示说话气泡失败", ex);
        }
    }

    public void HideBubble()
    {
        try
        {
            _hideTimer.Stop();
            Hide();
        }
        catch { }
    }

    /// <summary>跟随桌宠位置（头顶优先，上方放不下则放下方并翻转尾巴）</summary>
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
            int top = topY - hpx - 4;
            bool below = false;
            if (top < work.Top + 2)
            {
                below = true;
                top = bottomY + 4;   // 下方空间也有限，直接贴着桌宠
            }
            left = Math.Clamp(left, work.Left + 2, Math.Max(work.Left + 2, work.Right - wpx - 2));
            top = Math.Clamp(top, work.Top + 2, Math.Max(work.Top + 2, work.Bottom - hpx - 2));

            TailDown.Visibility = below ? Visibility.Collapsed : Visibility.Visible;
            TailUp.Visibility = below ? Visibility.Visible : Visibility.Collapsed;

            Native.SetWindowPos(new WindowInteropHelper(this).Handle, IntPtr.Zero, left, top, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        }
        catch
        {
            // 定位失败不影响主流程
        }
    }

    private void FadeOut()
    {
        try
        {
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(320))
            {
                FillBehavior = FillBehavior.Stop
            };
            fade.Completed += (_, _) =>
            {
                Hide();
                Opacity = 1;
            };
            BeginAnimation(OpacityProperty, fade);
        }
        catch { }
    }

    protected override void OnClosing(System.ComponentModel.CancelEventArgs e)
    {
        if (!_app.IsExiting)
        {
            e.Cancel = true;   // 气泡只隐藏不关闭
            HideBubble();
        }
        base.OnClosing(e);
    }
}
