namespace DeepSeekPet.Models;

/// <summary>用户设置（随 settings.json 持久化）</summary>
public sealed class AppSettings
{
    /// <summary>桌宠缩放倍率</summary>
    public double Zoom { get; set; } = 1.0;

    /// <summary>窗口置顶</summary>
    public bool TopMost { get; set; } = true;

    /// <summary>提醒声音</summary>
    public bool Sound { get; set; } = true;

    /// <summary>边缘隐藏（none / left / right / top）</summary>
    public string DockEdge { get; set; } = "none";

    /// <summary>上次窗口位置（物理像素；int.MinValue = 未初始化）</summary>
    public int WinX { get; set; } = int.MinValue;
    public int WinY { get; set; } = int.MinValue;

    /// <summary>是否已展示过首次引导（首次运行自动打开任务面板）</summary>
    public bool FirstRunDone { get; set; }
}
