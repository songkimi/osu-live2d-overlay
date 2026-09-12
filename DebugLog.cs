// ============================================================
// DebugLog.cs —— 开发期日志文件（只有「调试模式」为 true 时才写）
//
// 为什么需要它：这个插件的窗口是点击穿透、不抢焦点的，出了问题你没法在画面上点来点去排查；
// 而且"表情没播出来""语音被浏览器拦了"这类事在屏幕上只是一闪而过的字。
// 所以调试模式下顺手写一份日志，出问题时打开文件看就行。
//
// 面向玩家时（调试模式 = false）这个文件根本不会被创建 —— 不产生任何多余的东西。
// ============================================================
using System.IO;

namespace ComboOverlay;

public static class DebugLog
{
    private static readonly object Gate = new();
    private static bool _enabled;

    /// <summary>日志文件路径（未开启时为空串）</summary>
    public static string FilePath { get; private set; } = "";

    /// <summary>开启日志（调试模式）。每次启动重写一份，避免越堆越大</summary>
    public static void Start(bool enabled)
    {
        if (!enabled) return;

        try
        {
            FilePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "ComboOverlay", "debug.log");
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, $"[{DateTime.Now:HH:mm:ss}] 调试日志开始{Environment.NewLine}");
            _enabled = true;
        }
        catch
        {
            _enabled = false;   // 日志写不了不是什么大事，绝不能让插件因为日志挂掉
        }
    }

    public static void Write(string text)
    {
        if (!_enabled) return;

        try
        {
            lock (Gate)
                File.AppendAllText(FilePath, $"[{DateTime.Now:HH:mm:ss.fff}] {text}{Environment.NewLine}");
        }
        catch
        {
            // 同上：忽略
        }
    }
}
