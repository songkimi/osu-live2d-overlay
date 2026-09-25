// ============================================================
// AutoStartRegistration.cs —— 开机自启动（写 HKCU 的 Run 键）
//
// 【它是什么】
//   勾上「开机自启动」之后的实际动作：在
//   `HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Run`
//   下面登记一个值，登录时由系统把本程序拉起来。
//
// 【为什么写 HKCU 而不是 HKLM】
//   ① **不需要管理员权限** —— HKLM 要提权，而"为了开机自启弹一次 UAC"是不可接受的；
//   ② 开机自启本来就是"这个用户想让它起来"，不是"这台机器的所有用户"。
//   （代价：换用户登录不会自启，那正是想要的行为。）
//
// 【★ 路径必须加引号】
//   这个程序的目录名里很可能有空格（`D:\My Programs\...`）。
//   Run 键的值是**命令行**，系统按空格切分 —— 不加引号会被拆成
//   "找不到 D:\My" 这种荒谬的错误，而且**登录时才会发现**，平时完全看不到。
//
// 【为什么每次启动都要重写一遍】
//   用户可能把程序挪到别的目录（解压到别处、换盘）。注册表里那条值还是旧路径，
//   于是"开机自启"在某次搬家之后就悄悄失效了。
//   所以启动时若开关是开着的，就**幂等地重写一次**（见 App.OnStartup）。
//
// 【为什么这个文件能进控制台测】
//   它不依赖 WPF、也不依赖任何界面概念，读写注册表是纯 IO —— 而且
//   `RunKeyPath` / `valueName` 都做成了参数，验证工程可以拿一个**临时子键**来试，
//   不必碰用户真正的那条开机项（见 `.build-verify/AutoStartCheck`）。
// ============================================================
using System;
using System.IO;
using Microsoft.Win32;

namespace OsuLive2dOverlay;

public static class AutoStartRegistration
{
    /// <summary>Run 键的路径。**只在这里写一份**（验证工程与界面都引它）</summary>
    public const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";

    /// <summary>登记的那个值的名字。用英文的固定名（不是界面文案），这样换语言也不会失效</summary>
    public const string ValueName = "osu-live2d-overlay";

    /// <summary>
    /// 最近一次失败的**原始原因**（成功时是 null）。
    ///
    /// 【为什么要它】这一层把异常全部吞掉换成 `false`，因为"注册表读不了"绝不能让程序崩 ——
    /// 但**吞掉不等于可以不知道**：用户看到"登记失败"时，真正该告诉他的是
    /// "拒绝访问"还是"被组策略拦了"。所以留一个可读的原因给调用方写进日志。
    /// （2026-09-24 加的：这条本来就是被一次调查逼出来的 —— 探针写注册表失败，
    ///   而代码只回了一个 false，查了半天才发现是权限。）
    /// </summary>
    public static string? LastError { get; private set; }

    /// <summary>
    /// 当前这个进程的 exe 完整路径 —— **开机自启要登记的就是它**。
    /// 用 <see cref="Environment.ProcessPath"/>（.NET 6+）而不是
    /// `Assembly.Location`：后者在单文件发布/裁剪过的程序里可能为空。
    /// </summary>
    public static string CurrentExePath =>
        Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "osu-live2d-overlay.exe");

    /// <summary>
    /// 程序路径 → Run 键里那一行命令。
    /// **一定要加引号**：路径含空格时，不加引号会让系统去启动一个根本不存在的程序。
    /// </summary>
    public static string BuildCommand(string exePath) => "\"" + exePath.Trim().Trim('"') + "\"";

    /// <summary>现在登记着没有（读失败一律当"没有"，绝不抛）</summary>
    public static bool IsEnabled(string keyPath = RunKeyPath, string valueName = ValueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            return key?.GetValue(valueName) is string value && !string.IsNullOrWhiteSpace(value);
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 登记的是不是**就是这个 exe**。
    ///
    /// 比 <see cref="IsEnabled"/> 多问一句"路径对不对"：
    /// 用户把程序挪到别处之后，注册表里那条命令仍然在（所以 IsEnabled 还是 true），
    /// 但它指的是一个**已经不存在的程序** —— 自启会在下次开机时静默失效。
    /// </summary>
    public static bool IsEnabledFor(string exePath,
                                    string keyPath = RunKeyPath, string valueName = ValueName)
        => ReadCommand(keyPath, valueName) == BuildCommand(exePath);

    /// <summary>读出现在登记的命令原文（验证工程与日志用；没有就是 null）</summary>
    public static string? ReadCommand(string keyPath = RunKeyPath, string valueName = ValueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: false);
            return key?.GetValue(valueName) as string;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 登记开机自启（幂等：重复调用只是把值刷成当前路径）。
    /// 返回是否成功 —— 失败的原因通常是安全软件或组策略拦了注册表，
    /// **必须让用户知道**，否则"勾了没反应"会变成最难查的那类问题。
    /// </summary>
    public static bool Enable(string exePath, string keyPath = RunKeyPath, string valueName = ValueName)
    {
        if (string.IsNullOrWhiteSpace(exePath))
        {
            LastError = "没有 exe 路径";
            return false;
        }

        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(keyPath, writable: true);
            if (key is null)
            {
                LastError = "打不开注册表项（CreateSubKey 返回了 null）";
                return false;
            }

            key.SetValue(valueName, BuildCommand(exePath), RegistryValueKind.String);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }

    /// <summary>
    /// 取消开机自启。
    /// 用 `DeleteValue(..., throwOnMissingValue: false)`：**取消一个本来就不存在的登记不算失败**
    /// （用户可能在别处清过了，或者第一次就没登记成功）。
    /// </summary>
    public static bool Disable(string keyPath = RunKeyPath, string valueName = ValueName)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(keyPath, writable: true);
            key?.DeleteValue(valueName, throwOnMissingValue: false);
            LastError = null;
            return true;
        }
        catch (Exception ex)
        {
            LastError = ex.Message;
            return false;
        }
    }
}
