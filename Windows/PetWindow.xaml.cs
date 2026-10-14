using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using DeepSeekPet.Models;
using DeepSeekPet.Services;

namespace DeepSeekPet.Windows;

/// <summary>
/// 桌宠主窗口：透明置顶、可拖拽、贴边自动隐藏、低频动画。
/// 几何换算全部走物理像素（Win32），DPI / 多显示器 / RDP 均按显示器自适应。
/// 不使用任何全局钩子。
/// </summary>
public partial class PetWindow : Window
{
    private enum DockEdge { None, Left, Right, Top }
    private enum AnimMode { Off, Calm, Active }

    private const double BasePetDip = 180;
    private const double SideMarginDip = 12;
    private const double BottomMarginDip = 8;
    private const double HeadroomDip = 64;
    private const double DockTabThicknessDip = 14;
    private const double DockTabLengthDip = 58;
    private const double DockThresholdDip = 32;
    private const double ClickThresholdDip = 6;

    private readonly TaskStore _store;
    private readonly App _app;

    private IntPtr _hwnd;
    private double _zoom = 1.0;

    // 拖拽状态
    private bool _dragging;
    private int _dragMoved;
    private (int X, int Y) _dragCursorStart;
    private (int X, int Y) _dragWinStart;

    // 窗口位置（物理像素）
    private int _px, _py;
    private int _animFromX, _animFromY, _animToX, _animToY;

    // 贴边状态
    private DockEdge _edge = DockEdge.None;
    private bool _edgeHidden;
    private bool _revealed;

    // 动画状态（直接作用于 Transform 的 BeginAnimation，最省资源且最可靠）
    private AnimMode _animMode = AnimMode.Off;
    private DateTime _lastInteraction = DateTime.Now;
    private DoubleAnimationUsingKeyFrames _floatActiveAnim = null!;
    private DoubleAnimation _breathActiveX = null!, _breathActiveY = null!;
    private DoubleAnimation _breathCalmX = null!, _breathCalmY = null!;
    private DoubleAnimationUsingKeyFrames _popAnimX = null!, _popAnimY = null!;
    private DoubleAnimationUsingKeyFrames _jumpAnim = null!, _wiggleAnim = null!;

    private readonly DispatcherTimer _hideCheckTimer;
    private readonly DispatcherTimer _modeTimer;
    private readonly DispatcherTimer _alertMarkTimer;

    public static readonly DependencyProperty AnimTProperty =
        DependencyProperty.Register(nameof(AnimT), typeof(double), typeof(PetWindow),
            new PropertyMetadata(0d, (d, e) => ((PetWindow)d).OnAnimTChanged((double)e.NewValue)));

    public double AnimT
    {
        get => (double)GetValue(AnimTProperty);
        set => SetValue(AnimTProperty, value);
    }

    public PetWindow(TaskStore store)
    {
        _store = store;
        _app = App.Current;
        InitializeComponent();

        _zoom = Math.Clamp(store.Settings.Zoom, 0.6, 1.8);
        ApplyMetrics();

        var img = LoadPetImage(store.DataDir);
        if (img != null) PetImage.Source = img;

        BuildAnimations();

        _hideCheckTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(380) };
        _hideCheckTimer.Tick += (_, _) => HideCheck();
        _modeTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _modeTimer.Tick += (_, _) => ModeCheck();
        _alertMarkTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3.2) };
        _alertMarkTimer.Tick += (_, _) =>
        {
            _alertMarkTimer.Stop();
            FadeAlertMark(0);
        };

        SourceInitialized += OnSourceInitialized;
        Loaded += OnWindowLoaded;
        Closed += OnWindowClosed;
        DpiChanged += (_, _) => Dispatcher.BeginInvoke(new Action(ReapplyAfterDpi), DispatcherPriority.Loaded);
        PetArea.LostMouseCapture += (_, _) => EndDragSafely();
    }

    private double CurrentScale
    {
        get
        {
            try { return VisualTreeHelper.GetDpi(this).DpiScaleX; }
            catch { return 1.0; }
        }
    }

    // ==================================================================
    //  初始化
    // ==================================================================

    private static BitmapImage? LoadPetImage(string dataDir)
    {
        try
        {
            Uri uri;
            var custom = Path.Combine(dataDir, "pet.png"); // 用户可替换自己的形象
            if (File.Exists(custom)) uri = new Uri(custom);
            else uri = new Uri("pack://application:,,,/Assets/pet.png", UriKind.Absolute);

            var bmp = new BitmapImage();
            bmp.BeginInit();
            bmp.UriSource = uri;
            bmp.CacheOption = BitmapCacheOption.OnLoad; // 加载后释放文件句柄
            bmp.EndInit();
            bmp.Freeze();
            return bmp;
        }
        catch (Exception ex)
        {
            AppLogger.Error("形象图片加载失败", ex);
            return null;
        }
    }

    private void ApplyMetrics()
    {
        double s = BasePetDip * _zoom;
        PetArea.Width = s;
        PetArea.Height = s;
        DockTab.Width = DockTabThicknessDip;
        DockTab.Height = DockTabLengthDip;
        Width = s + SideMarginDip * 2;
        Height = s + BottomMarginDip + HeadroomDip;
    }

    private void OnSourceInitialized(object? sender, EventArgs e)
    {
        try
        {
            _hwnd = new WindowInteropHelper(this).Handle;
            int ex = Native.GetWindowLong(_hwnd, Native.GWL_EXSTYLE);
            ex |= Native.WS_EX_TOOLWINDOW | Native.WS_EX_NOACTIVATE; // 不出现在 Alt-Tab、点击不抢焦点
            Native.SetWindowLong(_hwnd, Native.GWL_EXSTYLE, ex);
            Topmost = _store.Settings.TopMost;
        }
        catch (Exception ex)
        {
            AppLogger.Error("初始化窗口样式失败", ex);
        }
        RestorePosition();
    }

    private void OnWindowLoaded(object? sender, EventArgs e)
    {
        try
        {
            if (ParseEdge(_store.Settings.DockEdge) is { } saved && saved != DockEdge.None)
            {
                _edge = saved;
                AlignDockTab();
                var (x, y) = ComputeEdgePosition(_edge, hidden: true);
                MoveTo(x, y);
                _edgeHidden = true;
                DockTab.Visibility = Visibility.Visible;
                SetAnimMode(AnimMode.Off);
            }
            else
            {
                PlayPop();
            }

            _modeTimer.Start();

            if (!_store.Settings.FirstRunDone)
            {
                _store.Settings.FirstRunDone = true;
                _store.TouchSettings();
                Dispatcher.BeginInvoke(new Action(() => _app.EnsurePanel().ShowPanel(follow: true)),
                    DispatcherPriority.ApplicationIdle);
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("初始化桌宠失败", ex);
        }
    }

    private void OnWindowClosed(object? sender, EventArgs e)
    {
        _modeTimer.Stop();
        _hideCheckTimer.Stop();
        _alertMarkTimer.Stop();
        StopAllMotion();
        try { _app.PanelOrNull?.ForceClose(); } catch { }
        if (!_app.IsExiting) _app.ExitApp();
    }

    /// <summary>退出前停止一切回调，避免拆窗口期间定时器仍触发</summary>
    public void ReleaseForExit()
    {
        _modeTimer.Stop();
        _hideCheckTimer.Stop();
        _alertMarkTimer.Stop();
        StopAllMotion();
    }

    private static DockEdge ParseEdge(string s) => s switch
    {
        "left" => DockEdge.Left,
        "right" => DockEdge.Right,
        "top" => DockEdge.Top,
        _ => DockEdge.None
    };

    // ==================================================================
    //  位置与贴边
    // ==================================================================

    private void RestorePosition()
    {
        try
        {
            int wpx = (int)Math.Round(Width * CurrentScale);
            int hpx = (int)Math.Round(Height * CurrentScale);
            int x, y;

            if (_store.Settings.WinX != int.MinValue)
            {
                x = _store.Settings.WinX;
                y = _store.Settings.WinY;
                if (!IntersectsAnyMonitor(x, y, wpx, hpx))
                {
                    var work = Native.PrimaryWorkArea();
                    x = work.Right - wpx - 28;
                    y = work.Bottom - hpx - 12;
                }
            }
            else
            {
                var work = Native.PrimaryWorkArea();
                x = work.Right - wpx - 28;
                y = work.Bottom - hpx - 12;
            }
            MoveTo(x, y);
        }
        catch (Exception ex)
        {
            AppLogger.Error("恢复位置失败", ex);
        }
    }

    private static bool IntersectsAnyMonitor(int x, int y, int w, int h)
    {
        foreach (var (b, _) in Native.GetMonitors())
        {
            if (x < b.Right && x + w > b.Left && y < b.Bottom && y + h > b.Top) return true;
        }
        return false;
    }

    private void MoveTo(int x, int y)
    {
        _px = x;
        _py = y;
        if (_hwnd != IntPtr.Zero)
            Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
                Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        try { _app.PanelOrNull?.Follow(); } catch { }
        try { _app.SayBubble?.Follow(); } catch { }
    }

    private void SavePos()
    {
        _store.Settings.WinX = _px;
        _store.Settings.WinY = _py;
        _store.TouchSettings();
    }

    private (int X, int Y) ComputeEdgePosition(DockEdge edge, bool hidden)
    {
        var rect = Native.GetWindowRectPx(_hwnd);
        if (rect.Width <= 0)
        {
            rect = new Native.RECT
            {
                Left = _px,
                Top = _py,
                Right = _px + (int)Math.Round(Width * CurrentScale),
                Bottom = _py + (int)Math.Round(Height * CurrentScale)
            };
        }
        int wpx = rect.Width, hpx = rect.Height;
        var work = Native.GetWorkAreaForPoint((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
        int tab = (int)Math.Round(DockTabThicknessDip * CurrentScale);

        int x = rect.Left, y = rect.Top;
        switch (edge)
        {
            case DockEdge.Left:
                x = hidden ? work.Left - (wpx - tab) : work.Left;
                y = Math.Clamp(rect.Top, work.Top, Math.Max(work.Top, work.Bottom - hpx));
                break;
            case DockEdge.Right:
                x = hidden ? work.Right - tab : work.Right - wpx;
                y = Math.Clamp(rect.Top, work.Top, Math.Max(work.Top, work.Bottom - hpx));
                break;
            case DockEdge.Top:
                y = hidden ? work.Top - (hpx - tab) : work.Top;
                x = Math.Clamp(rect.Left, work.Left, Math.Max(work.Left, work.Right - wpx));
                break;
        }
        return (x, y);
    }

    private void AlignDockTab()
    {
        DockTab.Width = DockTabThicknessDip;
        DockTab.Height = DockTabLengthDip;
        switch (_edge)
        {
            case DockEdge.Left:
                DockTab.HorizontalAlignment = HorizontalAlignment.Right;
                DockTab.VerticalAlignment = VerticalAlignment.Center;
                break;
            case DockEdge.Right:
                DockTab.HorizontalAlignment = HorizontalAlignment.Left;
                DockTab.VerticalAlignment = VerticalAlignment.Center;
                break;
            case DockEdge.Top:
                DockTab.Width = DockTabLengthDip;
                DockTab.Height = DockTabThicknessDip;
                DockTab.HorizontalAlignment = HorizontalAlignment.Center;
                DockTab.VerticalAlignment = VerticalAlignment.Bottom;
                break;
        }
    }

    private void DockTo(DockEdge edge)
    {
        SavePos(); // 记住隐藏前的可见位置，便于取消隐藏
        _edge = edge;
        _store.Settings.DockEdge = edge.ToString().ToLowerInvariant();
        _store.TouchSettings();
        AlignDockTab();

        // 面板开着时保持"已展开"状态停靠（收起面板后再自动隐藏），避免面板悬在空处
        if (_app.PanelOrNull?.IsOpen == true)
        {
            DockTab.Visibility = Visibility.Collapsed;
            var (rx, ry) = ComputeEdgePosition(edge, hidden: false);
            AnimatePosition(rx, ry, 200, () =>
            {
                _edgeHidden = false;
                _revealed = true;
                SetAnimMode(AnimMode.Active);
                _hideCheckTimer.Stop();
                _hideCheckTimer.Start();
            });
            return;
        }

        DockTab.Visibility = Visibility.Visible;
        var (hx, hy) = ComputeEdgePosition(edge, hidden: true);
        AnimatePosition(hx, hy, 200, () =>
        {
            _edgeHidden = true;
            _revealed = false;
            SetAnimMode(AnimMode.Off); // 隐藏即停动画：空闲零帧消耗
        });
    }

    public void Reveal()
    {
        if (_edge == DockEdge.None) return;
        MarkInteraction();
        var (rx, ry) = ComputeEdgePosition(_edge, hidden: false);
        DockTab.Visibility = Visibility.Collapsed;
        AnimatePosition(rx, ry, 170, () =>
        {
            _edgeHidden = false;
            _revealed = true;
            SetAnimMode(AnimMode.Active);
            _hideCheckTimer.Stop();
            _hideCheckTimer.Start();
        });
    }

    private void Rehide()
    {
        _hideCheckTimer.Stop();
        if (!_revealed || _edge == DockEdge.None) return;
        AlignDockTab();
        DockTab.Visibility = Visibility.Visible;
        var (hx, hy) = ComputeEdgePosition(_edge, hidden: true);
        AnimatePosition(hx, hy, 190, () =>
        {
            _edgeHidden = true;
            _revealed = false;
            SetAnimMode(AnimMode.Off);
        });
    }

    private void HideCheck()
    {
        if (_edge == DockEdge.None || !_revealed || _dragging) return;
        if (_app.PanelOrNull?.IsOpen == true) return; // 面板打开时不收起
        var (cx, cy) = Native.GetCursorPx();
        var rect = Native.GetWindowRectPx(_hwnd);
        bool inside = cx >= rect.Left - 4 && cx < rect.Right + 4 && cy >= rect.Top - 4 && cy < rect.Bottom + 4;
        if (!inside) Rehide();
    }

    /// <summary>面板收起后重新评估是否要贴边收起</summary>
    public void OnPanelClosed()
    {
        if (_edge != DockEdge.None && _revealed) HideCheck();
    }

    public void Undock()
    {
        _hideCheckTimer.Stop();
        _edge = DockEdge.None;
        _edgeHidden = false;
        _revealed = false;
        _store.Settings.DockEdge = "none";
        _store.TouchSettings();
        DockTab.Visibility = Visibility.Collapsed;

        var rect = Native.GetWindowRectPx(_hwnd);
        var work = Native.GetWorkAreaForPoint((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
        int nx = Math.Clamp(rect.Left, work.Left, Math.Max(work.Left, work.Right - rect.Width));
        int ny = Math.Clamp(rect.Top, work.Top, Math.Max(work.Top, work.Bottom - rect.Height));
        MoveTo(nx, ny);
        SetAnimMode(AnimMode.Active);
        SavePos();
    }

    private void DockTab_MouseEnter(object sender, MouseEventArgs e)
    {
        // 仅在隐藏动画完成后响应悬停：
        // 滑出动画期间把手会从静止的鼠标下方经过，若立即响应会造成“藏不住”的抖动。
        if (!_edgeHidden || _revealed) return;
        Reveal();
    }

    // ==================================================================
    //  拖拽 / 点击
    // ==================================================================

    private void PetArea_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (Mouse.LeftButton != MouseButtonState.Pressed) return;
        _dragging = true;
        _dragMoved = 0;
        _dragCursorStart = Native.GetCursorPx();
        _dragWinStart = (_px, _py);
        Mouse.Capture(PetArea);
        MarkInteraction();
        e.Handled = true;
    }

    private void PetArea_MouseMove(object sender, MouseEventArgs e)
    {
        MarkInteraction();
        if (!_dragging) return;
        var cur = Native.GetCursorPx();
        int dx = cur.X - _dragCursorStart.X;
        int dy = cur.Y - _dragCursorStart.Y;
        int dist = Math.Abs(dx) + Math.Abs(dy);
        if (dist > _dragMoved) _dragMoved = dist;

        int nx = _dragWinStart.X + dx;
        int ny = _dragWinStart.Y + dy;
        var work = Native.GetWorkAreaForPoint(cur.X, cur.Y);
        int wpx = (int)Math.Round(Width * CurrentScale);
        int hpx = (int)Math.Round(Height * CurrentScale);
        nx = Math.Clamp(nx, work.Left, Math.Max(work.Left, work.Right - wpx));
        ny = Math.Clamp(ny, work.Top, Math.Max(work.Top, work.Bottom - hpx));
        MoveTo(nx, ny);

        TiltT.Angle = Math.Clamp(dx * 0.12, -9, 9); // 拖拽倾斜反馈
    }

    private void PetArea_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Mouse.Capture(null);
        TiltReset();

        if (_dragMoved < ClickThresholdDip * CurrentScale)
        {
            // 单击：展开 / 收起任务列表
            _app.TogglePanel();
            return;
        }

        // 拖拽结束：判断是否贴边自动隐藏
        var rect = Native.GetWindowRectPx(_hwnd);
        var work = Native.GetWorkAreaForPoint((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
        int dl = rect.Left - work.Left;
        int dr = work.Right - rect.Right;
        int dt = rect.Top - work.Top;
        int min = Math.Min(dl, Math.Min(dr, dt));
        double th = DockThresholdDip * CurrentScale;

        if (min <= th)
        {
            DockEdge edge = min == dl ? DockEdge.Left : (min == dr ? DockEdge.Right : DockEdge.Top);
            DockTo(edge);
        }
        else
        {
            _edge = DockEdge.None;
            _store.Settings.DockEdge = "none";
            _store.TouchSettings();
            SavePos();
        }
    }

    private void EndDragSafely()
    {
        if (!_dragging) return;
        _dragging = false;
        TiltReset();
    }

    private void PetArea_MouseEnter(object sender, MouseEventArgs e) => MarkInteraction();

    // ==================================================================
    //  位置动画（60fps 封顶，180~200ms 的短动画）
    // ==================================================================

    private void AnimatePosition(int toX, int toY, int durationMs, Action? done)
    {
        _animFromX = _px;
        _animFromY = _py;
        _animToX = toX;
        _animToY = toY;

        if (durationMs <= 0)
        {
            MoveTo(toX, toY);
            done?.Invoke();
            return;
        }

        var anim = new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(durationMs))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        anim.SetValue(Timeline.DesiredFrameRateProperty, 60);
        anim.Completed += (_, _) =>
        {
            MoveTo(_animToX, _animToY);
            done?.Invoke();
        };
        BeginAnimation(AnimTProperty, anim);
    }

    private void OnAnimTChanged(double t)
    {
        if (_hwnd == IntPtr.Zero) return;
        int x = (int)Math.Round(_animFromX + (_animToX - _animFromX) * t);
        int y = (int)Math.Round(_animFromY + (_animToY - _animFromY) * t);
        _px = x;
        _py = y;
        Native.SetWindowPos(_hwnd, IntPtr.Zero, x, y, 0, 0,
            Native.SWP_NOSIZE | Native.SWP_NOZORDER | Native.SWP_NOACTIVATE);
        try { _app.PanelOrNull?.Follow(); } catch { }
        try { _app.SayBubble?.Follow(); } catch { }
    }

    // ==================================================================
    //  桌宠动画（全部为 GPU 合成友好的变换动画；帧率设上限）
    // ==================================================================

    private void BuildAnimations()
    {
        // ---- 待机漂浮（活跃模式，20fps 封顶）----
        _floatActiveAnim = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromSeconds(6),
            RepeatBehavior = RepeatBehavior.Forever
        };
        _floatActiveAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
        _floatActiveAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-6, KeyTime.FromPercent(0.5),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        _floatActiveAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1),
            new SineEase { EasingMode = EasingMode.EaseInOut }));
        _floatActiveAnim.SetValue(Timeline.DesiredFrameRateProperty, 20);

        // ---- 呼吸（活跃 20fps / 安静 10fps）----
        _breathActiveX = MakeBreath(1.012, 3.6, 20);
        _breathActiveY = MakeBreath(1.024, 3.6, 20);
        _breathCalmX = MakeBreath(1.014, 5.4, 10);
        _breathCalmY = MakeBreath(1.026, 5.4, 10);

        // ---- 出场弹跳 ----
        _popAnimX = MakePopScale();
        _popAnimY = MakePopScale();
        _popAnimY.Completed += (_, _) => SetAnimMode(AnimMode.Active);

        // ---- 提醒跳动（跳跃 + 小幅摆动，60fps 短暂爆发）----
        _jumpAnim = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(920),
            FillBehavior = FillBehavior.Stop
        };
        _jumpAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        _jumpAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-34, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(190)),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        _jumpAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(430)),
            new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        _jumpAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-18, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600)),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        _jumpAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(850)),
            new QuadraticEase { EasingMode = EasingMode.EaseIn }));
        _jumpAnim.SetValue(Timeline.DesiredFrameRateProperty, 60);

        _wiggleAnim = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(920),
            FillBehavior = FillBehavior.Stop
        };
        _wiggleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.Zero)));
        _wiggleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-7, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(190))));
        _wiggleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(6, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(430))));
        _wiggleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(-3, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(600))));
        _wiggleAnim.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(850))));
        _wiggleAnim.SetValue(Timeline.DesiredFrameRateProperty, 60);
    }

    private static DoubleAnimation MakeBreath(double to, double seconds, int fps)
    {
        var a = new DoubleAnimation(1.0, to, TimeSpan.FromSeconds(seconds))
        {
            AutoReverse = true,
            RepeatBehavior = RepeatBehavior.Forever,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut }
        };
        a.SetValue(Timeline.DesiredFrameRateProperty, fps);
        return a;
    }

    private static DoubleAnimationUsingKeyFrames MakePopScale()
    {
        var a = new DoubleAnimationUsingKeyFrames
        {
            Duration = TimeSpan.FromMilliseconds(420),
            FillBehavior = FillBehavior.Stop
        };
        a.KeyFrames.Add(new EasingDoubleKeyFrame(0.82, KeyTime.FromPercent(0),
            new QuadraticEase { EasingMode = EasingMode.EaseOut }));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.05, KeyTime.FromPercent(0.62)));
        a.KeyFrames.Add(new EasingDoubleKeyFrame(1.0, KeyTime.FromPercent(1)));
        return a;
    }

    private void PlayPop()
    {
        try
        {
            ScaleT.BeginAnimation(ScaleTransform.ScaleXProperty, _popAnimX);
            ScaleT.BeginAnimation(ScaleTransform.ScaleYProperty, _popAnimY);
        }
        catch { }
    }

    private void SetAnimMode(AnimMode mode)
    {
        if (_animMode == mode) return;
        _animMode = mode;
        StopAllMotion();
        try
        {
            switch (mode)
            {
                case AnimMode.Active:
                    FloatT.BeginAnimation(TranslateTransform.YProperty, _floatActiveAnim);
                    ScaleT.BeginAnimation(ScaleTransform.ScaleXProperty, _breathActiveX);
                    ScaleT.BeginAnimation(ScaleTransform.ScaleYProperty, _breathActiveY);
                    break;
                case AnimMode.Calm:
                    ScaleT.BeginAnimation(ScaleTransform.ScaleXProperty, _breathCalmX);
                    ScaleT.BeginAnimation(ScaleTransform.ScaleYProperty, _breathCalmY);
                    break;
                case AnimMode.Off:
                    break;
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("动画启动失败", ex);
        }
    }

    private void StopAllMotion()
    {
        try
        {
            FloatT.BeginAnimation(TranslateTransform.YProperty, null);
            ScaleT.BeginAnimation(ScaleTransform.ScaleXProperty, null);
            ScaleT.BeginAnimation(ScaleTransform.ScaleYProperty, null);
        }
        catch { }
    }

    private void MarkInteraction()
    {
        _lastInteraction = DateTime.Now;
        if (_animMode == AnimMode.Calm) SetAnimMode(AnimMode.Active);
    }

    /// <summary>供任务面板调用：面板开着时保持活跃状态</summary>
    public void KeepAwake() => MarkInteraction();

    private void ModeCheck()
    {
        if (_animMode == AnimMode.Active && (DateTime.Now - _lastInteraction).TotalSeconds > 25)
            SetAnimMode(AnimMode.Calm); // 25 秒无交互自动降频
    }

    private void TiltReset()
    {
        var a = new DoubleAnimation(0, TimeSpan.FromMilliseconds(260))
        {
            EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut },
            FillBehavior = FillBehavior.Stop
        };
        a.SetValue(Timeline.DesiredFrameRateProperty, 40);
        TiltT.BeginAnimation(RotateTransform.AngleProperty, a);
    }

    // ==================================================================
    //  提醒 / 唤醒
    // ==================================================================

    public void Alert(List<TaskItem> tasks)
    {
        try
        {
            MarkInteraction();
            if (_edge != DockEdge.None) Reveal();
            FadeAlertMark(1);
            _alertMarkTimer.Stop();
            _alertMarkTimer.Start();
            JumpT.BeginAnimation(TranslateTransform.YProperty, _jumpAnim);
            TiltT.BeginAnimation(RotateTransform.AngleProperty, _wiggleAnim);
            if (_animMode != AnimMode.Active) SetAnimMode(AnimMode.Active);
        }
        catch (Exception ex)
        {
            AppLogger.Error("提醒动画失败", ex);
        }
    }

    /// <summary>桌宠"说话"：头顶气泡显示一段文本（AI 回复 / 提醒语）</summary>
    public void Say(string text)
    {
        try
        {
            _app.SayBubble?.ShowText(text);
        }
        catch (Exception ex)
        {
            AppLogger.Warn($"显示说话气泡失败：{ex.Message}");
        }
    }

    private void FadeAlertMark(double to)
    {
        var a = new DoubleAnimation(to, TimeSpan.FromMilliseconds(to > 0 ? 150 : 400))
        {
            FillBehavior = FillBehavior.Stop
        };
        a.SetValue(Timeline.DesiredFrameRateProperty, 30);
        a.Completed += (_, _) => AlertMark.Opacity = to; // 动画结束定稿到目标值
        AlertMark.BeginAnimation(OpacityProperty, a);
    }

    /// <summary>第二个实例启动时唤醒本实例</summary>
    public void Nudge()
    {
        try
        {
            MarkInteraction();
            if (_edge != DockEdge.None) Reveal();
            var n = new DoubleAnimationUsingKeyFrames
            {
                Duration = TimeSpan.FromMilliseconds(380),
                FillBehavior = FillBehavior.Stop
            };
            n.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(0)));
            n.KeyFrames.Add(new EasingDoubleKeyFrame(-10, KeyTime.FromPercent(0.45)));
            n.KeyFrames.Add(new EasingDoubleKeyFrame(0, KeyTime.FromPercent(1)));
            n.SetValue(Timeline.DesiredFrameRateProperty, 60);
            JumpT.BeginAnimation(TranslateTransform.YProperty, n);
            if (_edge == DockEdge.None && _animMode == AnimMode.Off) SetAnimMode(AnimMode.Active);
        }
        catch { }
    }

    // ==================================================================
    //  周期巡检 / DPI
    // ==================================================================

    /// <summary>调度器捎带调用（约 2 分钟一次）：显示器/分辨率变化后自愈</summary>
    public void HousekeepingTick()
    {
        try
        {
            if (_hwnd == IntPtr.Zero) return;
            var rect = Native.GetWindowRectPx(_hwnd);

            if (_edge != DockEdge.None)
            {
                var (x, y) = ComputeEdgePosition(_edge, _edgeHidden);
                if (x != _px || y != _py) MoveTo(x, y);
                return;
            }

            if (!IntersectsAnyMonitor(rect.Left, rect.Top, rect.Width, rect.Height))
            {
                var work = Native.PrimaryWorkArea();
                int nx = work.Right - rect.Width - 28;
                int ny = work.Bottom - rect.Height - 12;
                MoveTo(nx, ny);
                SavePos();
                AppLogger.Warn("窗口超出可视区域（显示器变化），已自动复位");
                return;
            }

            var w = Native.GetWorkAreaForPoint((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
            if (rect.Left < w.Left - 2 || rect.Right > w.Right + 2 ||
                rect.Top < w.Top - 2 || rect.Bottom > w.Bottom + 2)
            {
                int nx = Math.Clamp(rect.Left, w.Left, Math.Max(w.Left, w.Right - rect.Width));
                int ny = Math.Clamp(rect.Top, w.Top, Math.Max(w.Top, w.Bottom - rect.Height));
                MoveTo(nx, ny);
                SavePos();
            }
        }
        catch (Exception ex)
        {
            AppLogger.Error("位置巡检失败", ex);
        }
    }

    private void ReapplyAfterDpi()
    {
        try
        {
            if (_hwnd == IntPtr.Zero) return;
            if (_edge != DockEdge.None)
            {
                AlignDockTab();
                var (x, y) = ComputeEdgePosition(_edge, _edgeHidden);
                MoveTo(x, y);
            }
            else
            {
                var rect = Native.GetWindowRectPx(_hwnd);
                var work = Native.GetWorkAreaForPoint((rect.Left + rect.Right) / 2, (rect.Top + rect.Bottom) / 2);
                int nx = Math.Clamp(rect.Left, work.Left, Math.Max(work.Left, work.Right - rect.Width));
                int ny = Math.Clamp(rect.Top, work.Top, Math.Max(work.Top, work.Bottom - rect.Height));
                MoveTo(nx, ny);
                SavePos();
            }
        }
        catch { }
    }

    /// <summary>面板定位用锚点（物理像素）：桌宠中心 X、头顶 Y、脚下 Y</summary>
    public (int CenterX, int TopY, int BottomY) GetPetAnchorPx()
    {
        var r = Native.GetWindowRectPx(_hwnd);
        double scale = CurrentScale;
        int headroomPx = (int)Math.Round(HeadroomDip * scale);
        int bottomPx = (int)Math.Round(BottomMarginDip * scale);
        return (r.Left + r.Width / 2, r.Top + headroomPx, r.Bottom - bottomPx);
    }

    // ==================================================================
    //  右键菜单（与托盘菜单共用同一份定义：AppMenu）
    // ==================================================================

    private void PetArea_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;   // 阻止左键拖拽逻辑被右键干扰
        ShowUnifiedMenu();
    }

    private void DockTab_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        e.Handled = true;
        ShowUnifiedMenu();
    }

    /// <summary>弹出统一右键菜单（每次重建菜单对象，勾选态实时刷新）</summary>
    private void ShowUnifiedMenu()
    {
        try
        {
            MarkInteraction();
            var menu = AppMenu.BuildWpf(_app);
            menu.IsOpen = true;
        }
        catch (Exception ex)
        {
            AppLogger.Error("弹出右键菜单失败", ex);
        }
    }

    /// <summary>应用“窗口置顶”设置（统一菜单调用；桌宠与面板保持一致）</summary>
    public void ApplyTopMost()
    {
        try
        {
            Topmost = _store.Settings.TopMost;
            if (_app.SayBubble != null) _app.SayBubble.Topmost = _store.Settings.TopMost;
        }
        catch { }
    }

    /// <summary>是否处于贴边状态（统一菜单“取消边缘隐藏”的可用性依据）</summary>
    public bool IsDocked => _edge != DockEdge.None;

    public void SetZoom(double z)
    {
        z = Math.Clamp(z, 0.6, 1.8);
        if (Math.Abs(z - _zoom) < 0.001) return;

        var rect = Native.GetWindowRectPx(_hwnd);
        int anchorCx = rect.Left + rect.Width / 2;
        int anchorBottom = rect.Bottom;

        _zoom = z;
        ApplyMetrics();
        _store.Settings.Zoom = _zoom;
        _store.TouchSettings();

        Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                if (_edge != DockEdge.None)
                {
                    AlignDockTab();
                    var (x, y) = ComputeEdgePosition(_edge, _edgeHidden);
                    MoveTo(x, y);
                    return;
                }
                var work = Native.GetWorkAreaForPoint(anchorCx, anchorBottom);
                int nw = (int)Math.Round(Width * CurrentScale);
                int nh = (int)Math.Round(Height * CurrentScale);
                int nx = Math.Clamp(anchorCx - nw / 2, work.Left, Math.Max(work.Left, work.Right - nw));
                int ny = Math.Clamp(anchorBottom - nh, work.Top, Math.Max(work.Top, work.Bottom - nh));
                MoveTo(nx, ny);
                SavePos();
                MarkInteraction();
            }
            catch { }
        }), DispatcherPriority.Loaded);
    }

}
