using System.Windows;
using DeepSeekPet.Models;
using DeepSeekPet.Services;

namespace DeepSeekPet.Windows;

/// <summary>新建 / 编辑任务的对话框</summary>
public partial class TaskEditorWindow : Window
{
    private readonly TaskStore _store;
    private readonly TaskItem _item;
    private readonly bool _isNew;

    public TaskEditorWindow(TaskStore store, TaskItem? existing)
    {
        _store = store;
        _isNew = existing == null;
        _item = existing ?? new TaskItem();
        InitializeComponent();

        TitleLabel.Text = _isNew ? "新建任务" : "编辑任务";
        Title = _isNew ? "新建任务" : "编辑任务";
        if (!_isNew) BtnDelete.Visibility = Visibility.Visible;

        TbTitle.Text = _item.Title;
        TbInterval.Text = _item.IntervalDays.ToString();
        RbOnce.IsChecked = _item.IsOnce;
        RbWeekly.IsChecked = !_item.IsOnce && _item.Mode == RepeatMode.Weekly;
        RbInterval.IsChecked = !_item.IsOnce && _item.Mode != RepeatMode.Weekly;
        TbOnceDate.Text = _item.IsOnce
            ? _item.OnceDate
            : DateTime.Today.AddDays(1).ToString("yyyy-MM-dd");

        var days = new[] { Wd1, Wd2, Wd3, Wd4, Wd5, Wd6, Wd7 };
        for (int i = 0; i < 7; i++)
            days[i].IsChecked = (_item.WeekMask & (1 << i)) != 0;

        for (int h = 0; h < 24; h++) CbHour.Items.Add(h.ToString("D2"));
        for (int m = 0; m < 60; m++) CbMinute.Items.Add(m.ToString("D2"));

        if (_isNew)
        {
            var t = DateTime.Now.AddMinutes(5);
            CbHour.SelectedIndex = t.Hour;
            CbMinute.SelectedIndex = t.Minute;
        }
        else
        {
            CbHour.SelectedIndex = Math.Clamp(_item.Hour, 0, 23);
            CbMinute.SelectedIndex = Math.Clamp(_item.Minute, 0, 59);
        }

        UpdatePanels();
        RbInterval.Checked += (_, _) => UpdatePanels();
        RbWeekly.Checked += (_, _) => UpdatePanels();
        RbOnce.Checked += (_, _) => UpdatePanels();
        Loaded += (_, _) =>
        {
            TbTitle.Focus();
            TbTitle.SelectAll();
        };
    }

    private void UpdatePanels()
    {
        bool weekly = RbWeekly.IsChecked == true;
        bool once = RbOnce.IsChecked == true;
        IntervalPanel.Visibility = !weekly && !once ? Visibility.Visible : Visibility.Collapsed;
        WeeklyPanel.Visibility = weekly ? Visibility.Visible : Visibility.Collapsed;
        OncePanel.Visibility = once ? Visibility.Visible : Visibility.Collapsed;
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var title = TbTitle.Text.Trim();
            if (title.Length == 0)
            {
                Error("请填写任务名称");
                return;
            }
            if (title.Length > 40) title = title[..40];

            bool weekly = RbWeekly.IsChecked == true;
            bool once = RbOnce.IsChecked == true;

            int interval = 1;
            if (!weekly && !once)
            {
                if (!int.TryParse(TbInterval.Text.Trim(), out interval) || interval < 1 || interval > 365)
                {
                    Error("间隔天数需要是 1 - 365 之间的整数");
                    return;
                }
            }

            int mask = 0;
            var days = new[] { Wd1, Wd2, Wd3, Wd4, Wd5, Wd6, Wd7 };
            for (int i = 0; i < 7; i++)
                if (days[i].IsChecked == true) mask |= 1 << i;
            if (weekly && mask == 0)
            {
                Error("请至少选择一个星期几");
                return;
            }

            if (CbHour.SelectedIndex < 0 || CbMinute.SelectedIndex < 0)
            {
                Error("请选择提醒时间");
                return;
            }

            string? onceDate = null;
            if (once)
            {
                var s = TbOnceDate.Text.Trim();
                if (!DateTime.TryParseExact(s, "yyyy-MM-dd",
                        System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.None, out var d))
                {
                    Error("日期格式应为 2026-10-15 这样");
                    return;
                }
                if (d.Date < DateTime.Today)
                {
                    Error("日期不能早于今天");
                    return;
                }
                var occ = d.Date.AddHours(CbHour.SelectedIndex).AddMinutes(CbMinute.SelectedIndex);
                if (occ <= DateTime.Now)
                {
                    Error("该时间已经过去了，请调整日期或时间");
                    return;
                }
                onceDate = d.Date.ToString("yyyy-MM-dd");
            }

            var candidate = _isNew ? _item : new TaskItem { Id = _item.Id };
            candidate.Title = title;
            candidate.Mode = weekly ? RepeatMode.Weekly : RepeatMode.Interval;
            candidate.IntervalDays = weekly ? 1 : interval;
            candidate.WeekMask = weekly ? mask : 0;
            candidate.Hour = CbHour.SelectedIndex;
            candidate.Minute = CbMinute.SelectedIndex;
            candidate.OnceDate = onceDate;

            var dup = _store.FindDuplicate(candidate, _isNew ? null : _item.Id);
            if (dup != null)
            {
                Error($"已存在相同任务「{dup}」，已自动去重");
                return;
            }

            _store.Upsert(candidate); // 变更事件驱动：面板刷新 + 调度重排
            Close();
        }
        catch (Exception ex)
        {
            AppLogger.Error("保存任务失败", ex);
            Error("保存失败，请重试（详见日志）");
        }
    }

    private void BtnDelete_Click(object sender, RoutedEventArgs e)
    {
        if (_isNew) return;
        var r = MessageBox.Show(this, $"确定删除任务「{_item.Title}」吗？", "删除任务",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (r != MessageBoxResult.OK) return;
        _store.Remove(_item);
        Close();
    }

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Close();

    private void Error(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }
}
