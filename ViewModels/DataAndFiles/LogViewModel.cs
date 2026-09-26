// ============================================================
// LogViewModel.cs —— 「数据与档案 → 日志与诊断」页
//
// 【它显示的是"内存里那份"】
//   日志有两条去向（见 DebugLog.cs）：内存环形缓冲（总是记）+ 日志文件（只在调试模式）。
//   这一页显示的是**内存那份** —— 所以**不开调试模式也能看到日志**，
//   而"导出"和"打包诊断"才去碰文件。
//
// 【怎么取新日志：轮询，而不是逐条派发】
//   `DebugLog.Written` 事件是在**任意线程**上触发的（tosu 收包线程、WebView2 回调…）。
//   如果一条条派发给界面，每条都要切一次 UI 线程 —— 日志密集时反而更慢。
//   所以这里改成：View 用一个定时器（250ms）调 <see cref="RefreshFromBuffer"/>，
//   一次把攒下的新行全加进来。**顺带把跨线程问题也消掉了** ——
//   定时器在 UI 线程上跑，ViewModel 从头到尾只被 UI 线程碰。
//
// 【为什么每条日志带序号】（见 DebugLog.cs 的说明）
//   环形缓冲满了会挤掉最老的：那时"条数"不变、内容却变了。
//   光比对数量判断不出"有没有新的"，序号才可以。
//   它还让「清空显示」变得干净：记下当前最大序号，之后只显示比它新的。
// ============================================================
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OsuLive2dOverlay;

public partial class LogViewModel : ObservableObject
{
    /// <summary>界面上最多留多少行（跑一整晚也不会把内存吃光；更早的仍可从文件里查）。</summary>
    private const int MaxVisibleLines = 2000;

    /// <summary>写文本文件一律用"带 BOM 的 UTF-8" —— 否则记事本打开可能把中文显示成乱码。</summary>
    private static readonly Encoding Utf8WithBom = new UTF8Encoding(encoderShouldEmitUTF8Identifier: true);

    private readonly ConfigStore _store;

    /// <summary>界面上已经显示到哪一号了</summary>
    private long _lastSequence;

    private bool _loading;

    public LogViewModel(ConfigStore store)
    {
        _store = store;

        _loading = true;
        RetainMonths = store.Settings.Log.RetainMonths;
        Verbose = store.Settings.Log.Verbose;
        DebugMode = store.Settings.DebugMode;
        _loading = false;

        RefreshFromBuffer();
    }

    /// <summary>界面上显示的行</summary>
    public ObservableCollection<string> Lines { get; } = new();

    /// <summary>自动滚到底（默认开；用户手动往上翻时应该关掉，由 View 处理）</summary>
    [ObservableProperty] private bool _autoScroll = true;

    /// <summary>保留月数：启动时清掉多少个月之前的**月份文件夹**</summary>
    [ObservableProperty] private int _retainMonths = 5;

    /// <summary>详细输出：把开发期信息也写进日志</summary>
    [ObservableProperty] private bool _verbose;

    /// <summary>
    /// 调试模式：**决定写不写日志文件**（顺带决定画面上显不显示开发期信息）。
    ///
    /// 为什么这个开关放在日志页：它最直接的作用就是"日志要不要落盘"
    /// </summary>
    [ObservableProperty] private bool _debugMode;

    public IReadOnlyList<int> RetainMonthOptions { get; } = new[] { 1, 2, 3, 6, 12 };

    /// <summary>日志根目录（exe 旁边的 logs\，除非 `目录.日志` 改了）</summary>
    public string LogRootDirectory => DebugLog.RootDirectory;

    /// <summary>现在到底在不在写文件（没开调试模式就是"只留在内存里"）</summary>
    public bool IsWritingToFile => DebugLog.CurrentFile.Length > 0;

    /// <summary>给界面一句话说清"现在日志去哪了"</summary>
    public string FileStateText => IsWritingToFile
        ? $"正在写文件：{DebugLog.CurrentFile}"
        : "未写文件（打开「调试模式」才会写文件；现在只保留在内存里）";

    public event Action<string>? Notify;

    // ------------------------------------------------------------

    /// <summary>
    /// 把内存缓冲里的**新行**追加进界面列表。
    /// View 的定时器调它（250ms 一次）；构造函数也会调一次做初始化。
    /// </summary>
    public void RefreshFromBuffer()
    {
        var newest = _lastSequence;

        foreach (var line in DebugLog.RecentLines)
        {
            if (line.Sequence <= _lastSequence) continue;

            Lines.Add(line.Text);
            if (line.Sequence > newest) newest = line.Sequence;
        }

        _lastSequence = newest;

        // 界面不至于无限长 —— 更早的行仍可从日志文件里查
        while (Lines.Count > MaxVisibleLines) Lines.RemoveAt(0);

        // 文件状态可能变了（调试模式被打开/关掉），顺手刷新
        OnPropertyChanged(nameof(IsWritingToFile));
        OnPropertyChanged(nameof(FileStateText));
    }

    /// <summary>
    /// 清空**界面显示**。日志文件一个字节都不动 ——
    /// 那是留给事后排查的，不该被界面上一个按钮删掉。
    /// </summary>
    [RelayCommand]
    private void Clear()
    {
        DebugLog.ClearRecent();
        Lines.Clear();
        _lastSequence = DebugLog.LatestSequence;

        Notify?.Invoke("已清空显示（日志文件不受影响）");
    }

    [RelayCommand]
    private void OpenLogFolder()
    {
        try
        {
            var root = DebugLog.RootDirectory;
            if (string.IsNullOrEmpty(root)) return;

            Directory.CreateDirectory(root);        // 还没写过日志时目录可能不存在
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{root}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"打不开日志文件夹：{ex.Message}");
        }
    }

    /// <summary>
    /// 导出当前显示的行。
    /// **路径由 View 选**（VM 不该弹对话框）——所以这里是 public 方法而不是命令。
    /// </summary>
    public void ExportTo(string path)
    {
        try
        {
            File.WriteAllLines(path, Lines, Utf8WithBom);
            Notify?.Invoke($"已导出 {Lines.Count} 行到 {Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"导出失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 一键打包诊断：日志 + 两个配置文件 + 环境信息 → 一个 zip。
    ///
    /// 为什么值得做：出问题时让用户"描述现象"几乎问不出有效信息，
    /// 而"把这个 zip 发我"能一次拿到全部线索 —— 对他省事，对排查也省事。
    /// （同样，路径由 View 选。）
    /// </summary>
    public void PackDiagnosticsTo(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);

            using var archive = ZipFile.Open(path, ZipArchiveMode.Create);

            // ① 日志（当前显示的这些）
            WriteTextEntry(archive, "日志.txt", Lines);

            // ② 两个配置文件
            //    条目名带上档案名：多档案之后"包里这份是哪个角色的"必须一眼看得出来
            AddFileIfExists(archive, _store.ConfigPath, $"档案-{_store.ProfileName}.json");
            AddFileIfExists(archive, _store.SettingsPath, "settings.json");
            AddFileIfExists(archive, DebugLog.CurrentFile, "当天日志文件.log");

            // ③ 环境信息
            WriteTextEntry(archive, "环境信息.txt", BuildEnvironmentReport());

            Notify?.Invoke($"诊断包已生成：{Path.GetFileName(path)}");
        }
        catch (Exception ex)
        {
            Notify?.Invoke($"打包失败：{ex.Message}");
        }
    }

    private static void WriteTextEntry(ZipArchive archive, string entryName, IEnumerable<string> lines)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), Utf8WithBom);
        foreach (var line in lines) writer.WriteLine(line);
    }

    /// <summary>整段文本一次写进去的版本（环境信息是一整块字符串，不是逐行的列表）。</summary>
    private static void WriteTextEntry(ZipArchive archive, string entryName, string content)
    {
        var entry = archive.CreateEntry(entryName);
        using var writer = new StreamWriter(entry.Open(), Utf8WithBom);
        writer.Write(content);
    }

    private static void AddFileIfExists(ZipArchive archive, string? path, string entryName)
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) return;

        try
        {
            archive.CreateEntryFromFile(path, entryName);
        }
        catch
        {
            // 某个文件拷不进去不该让整个打包失败
        }
    }

    /// <summary>
    /// 环境信息。**只写"能帮上排查的"**，不写用户名、路径里的私人信息能省则省。
    /// </summary>
    private string BuildEnvironmentReport()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "未知";
        var config = _store.Config;

        return string.Join(Environment.NewLine, new[]
        {
            "== 程序 ==",
            $"版本：{version}",
            $"运行框架：{RuntimeInformation.FrameworkDescription}",
            $"进程架构：{RuntimeInformation.ProcessArchitecture}",
            $"编译时框架：{RuntimeInformation.OSArchitecture}",
            "",
            "== 系统 ==",
            RuntimeInformation.OSDescription,
            $"64 位系统：{Environment.Is64BitOperatingSystem}",
            $"系统区域：{System.Globalization.CultureInfo.CurrentCulture.Name}",
            "",
            "== 路径 ==",
            $"程序目录：{AppContext.BaseDirectory}",
            $"配置档案：{_store.ProfileName}（{_store.ConfigPath}）",
            $"档案目录：{_store.ProfilesDirectory}",
            $"软件设置：{_store.SettingsPath}",
            $"日志目录：{DebugLog.RootDirectory}",
            $"当前日志文件：{(DebugLog.CurrentFile.Length > 0 ? DebugLog.CurrentFile : "(未写文件)")}",
            "",
            "== 配置摘要（不含隐私内容）==",
            $"模型目录存在：{(!string.IsNullOrWhiteSpace(config.Model.Directory) && Directory.Exists(config.Model.Directory))}",
            $"模型入口：{config.Model.Entry}",
            // ★ 2026-09-26 补全：这一份是**导出给人/给 agent 排查用的**，少一项就多一轮来回。
            //   原来只有"常态区间 / 连击触发"两条 —— 连失误反应、常驻组件都没列，
            //   触摸和结算是后加的卡片，更没跟上。
            $"常态区间条数：{config.Ranges.Count}",
            $"连击触发条数：{config.ComboTriggers.Count}",
            $"失误反应：小额 {config.Miss.SmallThreshold} / 大额 {config.Miss.BigThreshold}",
            $"触摸反应条数：{config.Touch.Count}",
            $"结算反应条数：{config.ResultRanges.Count}",
            $"常驻组件数：{config.PersistentParts.Count}",
            $"语音启用：{config.Voice.Enabled}",
            $"语音情绪数：{config.Voice.Emotions.Count}",
            $"界面感知：{(config.Scenes is null ? "无" : "有")}",
            "",
            "== 日志设置 ==",
            $"保留月数：{RetainMonths}",
            $"详细输出：{Verbose}",
            $"调试模式：{_store.Settings.DebugMode}",
        });
    }

    // ------------------------------------------------------------
    // 两个设置项：改了就存（与体检页的"检查模式"同一套做法）
    //
    // 它们都只有少量取值、没有"改到一半"的状态，而且作用很直接 ——
    // 存了立刻算数才算即时。（其余设置项仍然走"标脏 → 点保存"那套，两套不要混。）
    // ------------------------------------------------------------
    partial void OnRetainMonthsChanged(int value)
    {
        if (_loading) return;
        _store.Settings.Log.RetainMonths = value;
        SaveSettingsQuietly();
    }

    partial void OnVerboseChanged(bool value)
    {
        if (_loading) return;
        _store.Settings.Log.Verbose = value;
        SaveSettingsQuietly();
    }

    /// <summary>
    /// 调试模式一改，**立刻**让日志设施跟上：
    /// 打开 → 马上开始写文件；关掉 → 立刻停止写（内存那份照旧）。
    /// `DebugLog.Start` 是幂等的（第二次调用只更新开关、不重置当天文件），所以这里可以放心调。
    /// </summary>
    partial void OnDebugModeChanged(bool value)
    {
        if (_loading) return;

        _store.Settings.DebugMode = value;
        DebugLog.Start(value, DebugLog.RootDirectory);

        SaveSettingsQuietly();

        // "日志去哪了"这句话要跟着变
        OnPropertyChanged(nameof(IsWritingToFile));
        OnPropertyChanged(nameof(FileStateText));
    }

    private void SaveSettingsQuietly()
    {
        _store.MarkSettingsDirty();

        try
        {
            _store.SaveSettings();
            Notify?.Invoke("设置已保存");
        }
        catch (Exception ex)
        {
            // 存不下也要说一声
            Notify?.Invoke($"保存设置失败：{ex.Message}");
        }
    }
}
