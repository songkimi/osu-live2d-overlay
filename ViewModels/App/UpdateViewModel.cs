// ============================================================
// UpdateViewModel.cs —— 「应用 → 更新」页
//
// 【这一页和别的页有三处不同】
//   ① **不存配置** —— 它只读"当前版本" + 联网查"最新版本"，
//      所以没有标脏、没有保存栏、没有「● 未保存」。像「概览」页，不像「通用」页。
//
//   ② **有异步** —— 全项目第一个"用户点一下、要等网络"的页面。
//      FetchLatestAsync 一般几百毫秒（实测 899ms），但网络差的时候会拖到十几秒（超时值）。
//
//   ③ **结果有四种**：还没查 / 已是最新 / 有新版本 / 查不到。
//      界面要能表达这四种，不能"点了没反应"（定稿 §1.2 明令禁止）。
//
// 【★ 为什么这一页不用自己维护 IsChecking 这类状态】
//   `[RelayCommand]` 用在 `async Task` 方法上，生成的是 **AsyncRelayCommand** ——
//   它在执行期间**自动把 CanExecute 变成 false**。所以"检查中按钮变灰、连点不会发出
//   第二个请求"这件事是白送的：不用写 CanExecute，也不用自己维护一个 bool 去和按钮同步。
//   （不知道这一条，就会写出一堆互相打架的状态。）
//
// 【版本号：一个给机器、一个给人】
//   程序集里的真值是 `0.7.0+65dd21fe32e928c639cb7d702dddad96dd61c974`
//   （csproj 只写了 0.7.0，"+git commit" 是 .NET 自动拼的）。
//     · 喂给 IsNewer 用**完整的**（它自己会切掉 + 后面那截）
//     · 显示给用户要**截断成 0.7.0** —— 没人想读那串 sha
// ============================================================
using System;
using System.Diagnostics;
using System.Reflection;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace OsuLive2dOverlay;

public partial class UpdateViewModel : ObservableObject
{
    /// <summary>本地版本**原样**（含 "+sha"）。喂 IsNewer 用这个</summary>
    private readonly string _currentVersionRaw;

    /// <summary>检查成功后记住那个 Release 页面的地址 —— 「打开发布页」用它</summary>
    private string _releaseUrl = "";

    public UpdateViewModel(ConfigStore store)
    {
        _currentVersionRaw = ReadVersion();
        CurrentVersionText = ShortVersion(_currentVersionRaw);
    }

    /// <summary>显示给用户的当前版本（截断过），如 "0.7.0"</summary>
    public string CurrentVersionText { get; }

    /// <summary>给用户看的那一句话 —— 四种结果都靠它表达</summary>
    [ObservableProperty] private string _statusText = "还没检查过。";

    /// <summary>查到有新版本 —— 控制「最新」那一行和「打开发布页」按钮出不出现</summary>
    [ObservableProperty] private bool _hasUpdate;

    /// <summary>新版本号（只有 HasUpdate 为真时才有意义）</summary>
    [ObservableProperty] private string _latestVersionText = "";

    /// <summary>回执（查不到、打不开浏览器）。沿用别的页的做法：VM 只喊一声，怎么显示归 View</summary>
    public event Action<string>? Notify;

    /// <summary>
    /// 检查更新。**整个过程不会抛** —— FetchLatestAsync 内部已经把异常全接住、落成 null 了。
    /// </summary>
    [RelayCommand]
    private async Task CheckAsync()
    {
        _releaseUrl = "";
        LatestVersionText = "";
        StatusText = "正在检查...";
        HasUpdate = false;
        var info = await GitHubReleaseClient.FetchLatestAsync();
        if (info == null)
        {
            StatusText = "找不到最新版本，详情见日志";
            return;
        }
        HasUpdate = UpdateVersion.IsNewer(ReadVersion(),info.TagName);
        if (HasUpdate)
        {
            
            LatestVersionText = ShortVersion(info.TagName);
            _releaseUrl = info.HtmlUrl;
            StatusText = "发现了最新版本";
        }
        else
        {
            StatusText = "当前是最新版本！";
        }
    }

    /// <summary>用系统默认浏览器打开那个 Release 页</summary>
    [RelayCommand]
    private void OpenReleasePage()
    {
        if (string.IsNullOrWhiteSpace(_releaseUrl))
            return;
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = _releaseUrl,
                UseShellExecute = true
            };
            Process.Start(psi);
        }
        catch(Exception ex)
        {
            var inner = ex.InnerException?.Message;
            Notify?.Invoke("无法打开发布页,详情见日志");
            DebugLog.Write($"尝试打开发布页的未知错误： {ex.Message}" + (inner is null ? "" : $" → {inner}"));
        }
    }

    /// <summary>从程序集读版本 —— 和「关于」页同一套做法（免得界面写的版本和打包出来的对不上）</summary>
    private static string ReadVersion()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        return string.IsNullOrWhiteSpace(info)
            ? (asm.GetName().Version?.ToString() ?? "未知")
            : info;
    }

    /// <summary>把 "0.7.0+65dd21fe..." 截成 "0.7.0"（只给界面显示看；比较要用完整的那个）</summary>
    private static string ShortVersion(string raw) => raw.Split('+')[0];
}
