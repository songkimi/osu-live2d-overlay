// ============================================================
// DebugLog.cs —— 运行日志（内存里总是留一份；写文件只在调试模式）
//
// 【为什么需要它】
//   这个插件的窗口是点击穿透、不抢焦点的，出了问题没法在画面上点来点去排查；
//   而且"表情没播出来""语音被浏览器拦了"这类事在屏幕上只是一闪而过的字。
//
// 【两条去向，不要混】
//   · **内存环形缓冲**：总是记，保留最近若干条 —— 给设置界面的「日志」页实时显示用。
//     不写盘，所以玩家机器上不会凭空多出文件。
//   · **日志文件**：只在**调试模式**下写 —— 给"事后排查、发给别人看"用。
//
// 【日志文件怎么摆】（2026-09-20 用户提出，改掉了原来"一个文件写到底"的做法）
//
//     <日志目录>/2026-09/2026-09-20.log
//                └─ 按月一个文件夹   └─ 一天一个文件
//
//   为什么改成这样，有两个理由，第二个是原来设计里的一个**矛盾**：
//     ① 一个文件写到底太乱，而且**删不掉"几个月前"这一档** —— 粒度根本对不上；
//     ② 原来写在 `%TEMP%` 下，而 **`%TEMP%` 会被系统清理** ——
//        这与"自动清除 5 个月前的日志"直接打架：用户真想查三个月前的记录时，文件早没了。
//
//   改成"按月一个文件夹"之后，**清理就是删整个文件夹**：
//   不用逐文件读时间戳，也不用担心边界情况。
//
// 【为什么每条带一个递增序号】
//   界面要**增量刷新**（不重建整个列表，否则滚动位置会跳）。
//   但环形缓冲满了之后会把最老的挤出去 —— 那时"条数"不变、内容却变了，
//   光看数量判断不出"有没有新的"。序号能：
//     · 让界面只取"比上次见过的序号更大"的那些
//     · 让"清空显示"变成"记下当前最大序号"，之后新的照样进来
//
// 【线程安全】
//   日志会从多个线程写（tosu 的收包线程、UI 线程、WebView2 的回调）。
//   所以内部有锁；而**通知界面的事件在锁外触发** —— 否则订阅者一旦卡住，
//   会把所有写日志的线程一起拖住。
// ============================================================
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;

namespace OsuLive2dOverlay;

/// <summary>一条日志：序号（给界面做增量用）+ 已经格式化好的整行文本</summary>
public sealed record LogLine(long Sequence, string Text);

public static class DebugLog
{
    /// <summary>内存里保留最近多少条（日志页显示用）。</summary>
    private const int RecentCapacity = 500;

    /// <summary>月份文件夹的命名格式 —— 清理时只认这个格式，别的一律不碰。</summary>
    private const string MonthFormat = "yyyy-MM";

    private static readonly object Gate = new();
    private static readonly Queue<LogLine> Recent = new();

    private static long _sequence;
    private static bool _started;
    private static bool _enabled;
    private static string _root = "";

    private static string _currentDate = "";     // 当前正在写哪一天（yyyy-MM-dd）
    private static string _currentFile = "";     // 当前日志文件的完整路径

    /// <summary>日志根目录（没配置时是 exe 旁边的 logs\\）</summary>
    public static string RootDirectory => _root;

    /// <summary>当前正在写的日志文件；没开调试模式时是空串。</summary>
    public static string CurrentFile
    {
        get { lock (Gate) return _currentFile; }
    }

    /// <summary>
    /// 每写一行就抛一次（**可能在非 UI 线程上**）。
    /// 订阅方自己负责切回 UI 线程 —— 或者干脆不订阅，改用轮询式的
    /// <see cref="RecentLines"/>（日志页就是这么做的：定时拉增量，比一条条派发更省）。
    /// </summary>
    public static event Action<LogLine>? Written;

    /// <summary>内存里最近的日志（副本；每条带序号，供界面做增量刷新）。</summary>
    public static IReadOnlyList<LogLine> RecentLines
    {
        get { lock (Gate) return Recent.ToList(); }
    }

    /// <summary>当前最大序号 —— 界面"清空显示"时记下它，之后只显示比它新的。</summary>
    public static long LatestSequence
    {
        get { lock (Gate) return _sequence; }
    }

    /// <summary>
    /// 初始化。**整个程序只调一次**（`App.OnStartup` 里调）。
    ///
    /// 再调用时只更新"开不开文件日志"这一项，不会重置当前文件 ——
    /// 这样"调试模式"中途被改也不会把当天已经写下的内容冲掉。
    /// </summary>
    /// <param name="enabled">是否写日志文件（= 调试模式）</param>
    /// <param name="logRoot">日志根目录；传空就用 exe 旁边的 logs\\</param>
    public static void Start(bool enabled, string? logRoot = null)
    {
        lock (Gate)
        {
            if (_started)
            {
                // 已经初始化过了：只跟随开关，不重新开文件
                _enabled = enabled;
                if (_enabled)
                {
                    try { RollFile(force: false); }
                    catch { _enabled = false; }
                }
                return;
            }

            _root = string.IsNullOrWhiteSpace(logRoot)
                ? Path.Combine(AppContext.BaseDirectory, DirectoryResolver.LogsFolderName)
                : logRoot!;

            _enabled = enabled;
            _started = true;

            if (_enabled)
            {
                try { RollFile(force: true); }
                catch { _enabled = false; }   // 写不了日志不是什么大事，绝不能让程序因为日志挂掉
            }
        }

        Write(_enabled
            ? $"调试日志开始（目录：{_root}）"
            : "日志开始（仅保留在内存里，未写文件 —— 打开「调试模式」才会写文件）");
    }

    /// <summary>
    /// 写一行。任何线程都可以调。
    /// </summary>
    public static void Write(string text)
    {
        LogLine line;

        lock (Gate)
        {
            // ① 内存缓冲：总是记（序号在这里补上，保证单调递增）
            line = new LogLine(++_sequence, $"[{DateTime.Now:HH:mm:ss.fff}] {text}");
            Recent.Enqueue(line);
            while (Recent.Count > RecentCapacity) Recent.Dequeue();

            // ② 文件：只在调试模式
            if (_enabled)
            {
                try
                {
                    RollFile(force: false);          // 跨天了就换文件（程序可能连着跑过午夜）
                    File.AppendAllText(_currentFile, line.Text + Environment.NewLine);
                }
                catch
                {
                    _enabled = false;                // 写不动就别再试了，但内存那份还在
                }
            }
        }

        // ③ 通知订阅者 —— **在锁外**：订阅者卡住不能把写日志的线程一起拖住
        Written?.Invoke(line);
    }

    /// <summary>
    /// 清空**内存里的显示缓冲**（日志页的「清空」按钮）。
    /// **不动日志文件** —— 那个是留给人事后查的，不该被界面上的一个按钮删掉。
    /// 清空后新日志照常进来（序号继续递增）。
    /// </summary>
    public static void ClearRecent()
    {
        lock (Gate) Recent.Clear();
    }

    /// <summary>
    /// 换文件（如果需要）。调用方必须持锁。
    /// 一天一个文件：日期变了就换；同一个文件当天多次启动是**追加**，不覆盖。
    /// </summary>
    private static void RollFile(bool force)
    {
        var now = DateTime.Now;
        var today = now.ToString("yyyy-MM-dd");

        if (!force && today == _currentDate && _currentFile.Length > 0) return;

        _currentDate = today;

        var monthDirectory = Path.Combine(_root, now.ToString(MonthFormat));
        Directory.CreateDirectory(monthDirectory);
        _currentFile = Path.Combine(monthDirectory, today + ".log");
    }

    /// <summary>
    /// 清掉太久以前的日志（按**整个月份文件夹**删）。启动时调一次就够，不用定时器。
    ///
    /// 三条安全线，都是为了"绝不误删":
    ///   ① 只认 `yyyy-MM` 这种名字的文件夹 —— 用户自己往 logs 里放的别的东西一律不碰
    ///   ② **本月永不删**（哪怕 keepMonths 传了 0）
    ///   ③ 删不掉就留着（文件被占用、权限不足），下次启动再试 —— 不因为清理失败而报错
    /// </summary>
    /// <param name="keepMonths">保留最近几个月（配置项 `日志.保留月数`）</param>
    /// <returns>删掉了几个月份文件夹</returns>
    public static int Cleanup(int keepMonths)
    {
        var root = _root;
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root)) return 0;

        var months = Math.Max(1, keepMonths);
        var thisMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
        var cutoff = thisMonth.AddMonths(-(months - 1));   // 保留最近 N 个月（含本月）

        var removed = 0;

        foreach (var directory in Directory.GetDirectories(root))
        {
            var name = Path.GetFileName(directory);
            if (!DateTime.TryParseExact(name, MonthFormat, CultureInfo.InvariantCulture,
                                        DateTimeStyles.None, out var month))
                continue;                                   // ① 不是月份文件夹，不碰

            if (month >= cutoff) continue;                  // ② 在保留范围内（含本月），不碰

            try
            {
                Directory.Delete(directory, recursive: true);
                removed++;
            }
            catch
            {
                // ③ 删不掉就算了 —— 下次启动再试
            }
        }

        if (removed > 0) Write($"已清理 {removed} 个过期的日志文件夹（保留最近 {months} 个月）");
        return removed;
    }
}
