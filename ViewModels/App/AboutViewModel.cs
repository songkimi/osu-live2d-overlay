// ============================================================
// AboutViewModel.cs —— 「应用 → 关于」页
//
// 【这一页为什么不做成纯静态 XAML】
//   它要显示的东西**都是算出来的**：程序版本、系统、.NET 运行时、程序目录、配置目录 ——
//   把这些写死在 XAML 里，换个机器就全是错的。
//
// 【刻意不显示的东西】
//   · **不显示路径**（用户 2026-09-23 指出"已经有一处显示了"）：配置/档案/程序目录
//     在「数据与档案 → 程序与数据源」页都有，那儿还能直接打开文件夹。
//     同一份信息摆两处，迟早出现"两边不一样"。
//   · **不显示 GitHub 链接**：仓库地址会变，而写在界面上的死链比没有链接更让用户困惑。
//     要给人看仓库就去 README（那里是唯一该维护地址的地方）。
//   · **不显示"最新版本"**：那要联网查（检查更新是另一页的事，且还没做）。
// ============================================================
using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows;

namespace OsuLive2dOverlay;

public sealed class AboutViewModel
{
    public AboutViewModel(ConfigStore store)
    {
        // 版本：从程序集拿 —— 免得"关于"页写的版本和打包出来的对不上
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        Version = string.IsNullOrWhiteSpace(info)
            ? (asm.GetName().Version?.ToString() ?? "未知")
            : info;

        // 系统与运行时：排查问题时最常被问到的两项
        SystemText = $"{RuntimeInformation.OSDescription}（{(Environment.Is64BitProcess ? "64 位" : "32 位")}进程）";
        RuntimeText = RuntimeInformation.FrameworkDescription;

        DpiText = "按 96 DPI 基准自动适配（字号可在「通用」里调）";
    }

    public string Version { get; }
    public string SystemText { get; }
    public string RuntimeText { get; }
    public string DpiText { get; }

    /// <summary>许可证那一句：写清"这个程序本身"和"它引用的东西"是两码事</summary>
    public string LicenseText =>
        "本程序的开源许可见仓库里的 LICENSE。注意它 不覆盖 你使用的 Live2D 模型、" +
        "语音素材与 Cubism Core —— 那些各有各的授权，得由你自己确认。";
}
