using System.ComponentModel;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DeepSeekPet.Config;
using DeepSeekPet.Models;
using DeepSeekPet.Services;

namespace DeepSeekPet.Windows;

/// <summary>
/// 设置窗口（单例，隐藏复用）：可视化编辑 API Key / 搜索 / 人设卡。
/// - 保存即写入 appconfig.json 与 persona.json（既有的热更新机制会让改动立即生效，无需重启）；
/// - 「测试连接」用输入框中的实时参数做一次最小请求，验证密钥与端点；
/// - 关闭按钮只隐藏，再次打开不丢单例状态。
/// </summary>
public partial class SettingsWindow : Window
{
    private static readonly Brush OkBrush = new SolidColorBrush(Color.FromRgb(0x2E, 0x9E, 0x5B));
    private static readonly Brush FailBrush = new SolidColorBrush(Color.FromRgb(0xD6, 0x45, 0x45));
    private static readonly Brush MutedBrush = new SolidColorBrush(Color.FromRgb(0x8A, 0x93, 0xA6));

    private readonly App _app;
    private System.Windows.Threading.DispatcherTimer? _hintTimer;

    public SettingsWindow(App app)
    {
        _app = app;
        InitializeComponent();

        try
        {
            var v = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version;
            AboutVersion.Text = v != null ? $"版本 v{v.Major}.{v.Minor}.{v.Build} · .NET 8 · WPF" : "版本 v1.2";
        }
        catch { AboutVersion.Text = "版本 v1.2"; }

        try { AboutDataDir.Text = $"• 当前数据目录：{_app.Store.DataDir}"; }
        catch { AboutDataDir.Text = "• 当前数据目录：—"; }

        TabAi.Checked += (_, _) => SwitchPanels("ai");
        TabSearch.Checked += (_, _) => SwitchPanels("search");
        TabPersona.Checked += (_, _) => SwitchPanels("persona");
        TabAbout.Checked += (_, _) => SwitchPanels("about");

        CkShowKey.Checked += (_, _) => ApplyKeyVisibility();
        CkShowKey.Unchecked += (_, _) => ApplyKeyVisibility();

        LoadFromStore();
    }

    /// <summary>供 App.ShowSettings(tab) 调用：刷新内容并切到指定页签（null=上次页签）</summary>
    public void ShowTab(string? tab)
    {
        LoadFromStore();
        if (!string.IsNullOrEmpty(tab)) SwitchPanels(tab!);
        HideError();
        SavedHint.Text = "";
    }

    // ---------------- 数据装载 / 保存 ----------------

    private void LoadFromStore()
    {
        try
        {
            var c = _app.Config.Current;
            var key = c.ApiKey ?? "";
            PbApiKey.Password = key;
            TbApiKeyPlain.Text = key;
            CkShowKey.IsChecked = false;
            PbApiKey.Visibility = Visibility.Visible;
            TbApiKeyPlain.Visibility = Visibility.Collapsed;

            TbBaseUrl.Text = c.BaseUrl;
            TbModel.Text = c.Model;
            TbTimeout.Text = c.TimeoutSeconds.ToString(CultureInfo.InvariantCulture);
            TbTemperature.Text = c.Temperature.ToString(CultureInfo.InvariantCulture);
            TbMemoryRounds.Text = c.MemoryRounds.ToString(CultureInfo.InvariantCulture);

            TbSearchKey.Text = c.SearchApiKey ?? "";
            TbSearxng.Text = c.SearxngUrl ?? "";
            TbCacheMinutes.Text = c.SearchCacheMinutes.ToString(CultureInfo.InvariantCulture);
            TbMaxResults.Text = c.SearchMaxResults.ToString(CultureInfo.InvariantCulture);
            CkEnableSearch.IsChecked = c.EnableWebSearch;
            CbProvider.SelectedIndex = c.SearchProvider switch
            {
                "searxng" => 1,
                "none" => 2,
                _ => 0
            };

            var p = _app.Persona.Current;
            TbPersonaName.Text = p.Name;
            TbCreature.Text = p.Creature;
            TbPersonality.Text = p.Personality;
            TbSpeechStyle.Text = p.SpeechStyle;
            TbBackground.Text = p.Background;
            TbRules.Text = string.Join("\n", p.Rules);
            TbCatchphrases.Text = string.Join("\n", p.Catchphrases);
            TbGreeting.Text = p.Greeting;
            TbSummary.Text = p.Summary;
        }
        catch (Exception ex)
        {
            AppLogger.Error("装载设置失败", ex);
        }
    }

    private void BtnSave_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            HideError();
            SavedHint.Text = "";

            // ---- 校验 ----
            if (!int.TryParse(TbTimeout.Text.Trim(), out var timeout) || timeout < 5 || timeout > 300)
            { Error("超时需要是 5 – 300 之间的整数"); return; }
            if (!double.TryParse(TbTemperature.Text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out var temp)
                || temp < 0 || temp > 2)
            { Error("采样温度需要是 0 – 2 之间的数字"); return; }
            if (!int.TryParse(TbMemoryRounds.Text.Trim(), out var rounds) || rounds < 4 || rounds > 60)
            { Error("记忆轮数需要是 4 – 60 之间的整数"); return; }
            if (!int.TryParse(TbCacheMinutes.Text.Trim(), out var cacheMin) || cacheMin < 0 || cacheMin > 1440)
            { Error("搜索缓存需要是 0 – 1440 分钟"); return; }
            if (!int.TryParse(TbMaxResults.Text.Trim(), out var maxRes) || maxRes < 1 || maxRes > 10)
            { Error("搜索条数需要是 1 – 10 之间的整数"); return; }
            if (TbBaseUrl.Text.Trim().Length == 0) { Error("接口地址不能为空"); return; }
            if (TbModel.Text.Trim().Length == 0) { Error("模型名不能为空"); return; }
            if (TbPersonaName.Text.Trim().Length == 0) { Error("人设卡「名字」不能为空"); return; }

            // ---- 写 appconfig.json（ConfigService.Save 内部 Normalize + 热更新通知） ----
            var cfg = AppConfig.FromJson(_app.Config.Current.ToJson()) ?? new AppConfig();
            cfg.ApiKey = (CkShowKey.IsChecked == true ? TbApiKeyPlain.Text : PbApiKey.Password).Trim();
            cfg.BaseUrl = TbBaseUrl.Text.Trim();
            cfg.Model = TbModel.Text.Trim();
            cfg.TimeoutSeconds = timeout;
            cfg.Temperature = temp;
            cfg.MemoryRounds = rounds;
            cfg.SearchProvider = SelectedProvider();
            cfg.SearchApiKey = TbSearchKey.Text.Trim();
            cfg.SearxngUrl = TbSearxng.Text.Trim();
            cfg.SearchCacheMinutes = cacheMin;
            cfg.SearchMaxResults = maxRes;
            cfg.EnableWebSearch = CkEnableSearch.IsChecked == true;
            _app.Config.Save(cfg);

            // ---- 写 persona.json ----
            var p = Persona.FromJson(_app.Persona.Current.ToJson()) ?? new Persona();
            p.Name = TbPersonaName.Text.Trim();
            p.Creature = TbCreature.Text.Trim();
            p.Personality = TbPersonality.Text.Trim();
            p.SpeechStyle = TbSpeechStyle.Text.Trim();
            p.Background = TbBackground.Text.Trim();
            p.Rules = SplitLines(TbRules.Text);
            p.Catchphrases = SplitLines(TbCatchphrases.Text);
            p.Greeting = TbGreeting.Text.Trim();
            p.Summary = TbSummary.Text.Trim();
            if (!_app.Persona.Save(p))
            {
                Error("设置已保存，但人设卡写入失败（详见日志）");
                return;
            }

            SavedHint.Text = "✓ 已保存并立即生效";
            _hintTimer?.Stop();
            _hintTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(3.5) };
            _hintTimer.Tick += (s2, _) =>
            {
                ((System.Windows.Threading.DispatcherTimer)s2!).Stop();
                SavedHint.Text = "";
            };
            _hintTimer.Start();
            _app.Pet?.Say("设置保存好了，本鲸记住啦～");
        }
        catch (Exception ex)
        {
            AppLogger.Error("保存设置失败", ex);
            Error($"保存失败：{ex.Message}");
        }
    }

    private string SelectedProvider()
    {
        if (CbProvider.SelectedItem is ComboBoxItem item && item.Tag is string tag) return tag;
        return "tavily";
    }

    private static List<string> SplitLines(string text) =>
        (text ?? "").Replace("\r", "")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();

    // ---------------- 连接测试 ----------------

    private async void BtnTestLlm_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (_app.Agent == null)
            {
                SetTestResult(false, "对话模块未初始化");
                return;
            }

            BtnTestLlm.IsEnabled = false;
            TestLlmResult.Foreground = MutedBrush;
            TestLlmResult.Text = "测试中…";

            var probe = AppConfig.FromJson(_app.Config.Current.ToJson()) ?? new AppConfig();
            probe.ApiKey = (CkShowKey.IsChecked == true ? TbApiKeyPlain.Text : PbApiKey.Password).Trim();
            probe.BaseUrl = TbBaseUrl.Text.Trim();
            probe.Model = TbModel.Text.Trim();
            probe.TimeoutSeconds = 20;
            probe.Normalize();

            var (ok, msg) = await _app.Agent.TestConnectionAsync(probe);
            SetTestResult(ok, msg);
        }
        catch (Exception ex)
        {
            AppLogger.Error("连接测试异常", ex);
            SetTestResult(false, ex.Message);
        }
        finally
        {
            BtnTestLlm.IsEnabled = true;
        }
    }

    private void SetTestResult(bool ok, string msg)
    {
        TestLlmResult.Foreground = ok ? OkBrush : FailBrush;
        TestLlmResult.Text = msg.Length > 42 ? msg[..42] + "…" : msg;
        TestLlmResult.ToolTip = msg;
    }

    // ---------------- 交互细节 ----------------

    private void SwitchPanels(string tab)
    {
        AiPanel.Visibility = tab == "ai" ? Visibility.Visible : Visibility.Collapsed;
        SearchPanel.Visibility = tab == "search" ? Visibility.Visible : Visibility.Collapsed;
        PersonaPanel.Visibility = tab == "persona" ? Visibility.Visible : Visibility.Collapsed;
        AboutPanel.Visibility = tab == "about" ? Visibility.Visible : Visibility.Collapsed;
    }

    private void ApplyKeyVisibility()
    {
        try
        {
            if (CkShowKey.IsChecked == true)
            {
                TbApiKeyPlain.Text = PbApiKey.Password;
                TbApiKeyPlain.Visibility = Visibility.Visible;
                PbApiKey.Visibility = Visibility.Collapsed;
            }
            else
            {
                PbApiKey.Password = TbApiKeyPlain.Text;
                PbApiKey.Visibility = Visibility.Visible;
                TbApiKeyPlain.Visibility = Visibility.Collapsed;
            }
        }
        catch { }
    }

    private void BtnResetPersona_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var def = Persona.DefaultWhale();
            TbPersonaName.Text = def.Name;
            TbCreature.Text = def.Creature;
            TbPersonality.Text = def.Personality;
            TbSpeechStyle.Text = def.SpeechStyle;
            TbBackground.Text = def.Background;
            TbRules.Text = string.Join("\n", def.Rules);
            TbCatchphrases.Text = string.Join("\n", def.Catchphrases);
            TbGreeting.Text = def.Greeting;
            TbSummary.Text = def.Summary;
            SavedHint.Text = "已载入默认预设，点「保存」后生效";
        }
        catch (Exception ex)
        {
            AppLogger.Error("恢复默认人设失败", ex);
        }
    }

    private void BtnOpenData_Click(object sender, RoutedEventArgs e) => AppMenu.OpenPath(_app.Store.DataDir);

    private void BtnOpenLogs_Click(object sender, RoutedEventArgs e)
        => AppMenu.OpenPath(System.IO.Path.Combine(_app.Store.DataDir, "logs"));

    private void BtnCancel_Click(object sender, RoutedEventArgs e) => Hide();

    private void Error(string msg)
    {
        ErrorText.Text = msg;
        ErrorText.Visibility = Visibility.Visible;
    }

    private void HideError()
    {
        ErrorText.Text = "";
        ErrorText.Visibility = Visibility.Collapsed;
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (!_app.IsExiting)
        {
            e.Cancel = true;   // 关闭按钮只隐藏（单例复用）
            Hide();
        }
        base.OnClosing(e);
    }
}
