// ============================================================
// FontScale.cs —— pt 与 DIP 的换算，以及字号的层级阶梯
//
// 【为什么需要这个文件】
//   用户习惯用 pt（1/72 英寸）说字号，设计稿也是 pt；
//   但 **WPF 的 FontSize 单位是设备无关像素（DIP，1/96 英寸）**，它不认 pt。
//
//   于是有个很安静的坑：把配置里的 10.5 直接塞进 FontSize，
//   得到的不是"10.5pt"，而是 10.5 DIP ≈ 7.9pt —— **比预期小一大截，而且不报错**。
//
//   所以：**配置里存 pt，绑定时过这里换算**。
//
// 【为什么是"层级"而不是一个数字】
//   用户要求"字号按文字层级分别给"：标题、正文、说明、小标签不是同一个大小。
//   所以这里定义一条阶梯，**用户只调基准值**（通用.字号），其余按比例跟着走 ——
//   否则界面上要摆五个字号输入框，用户还得自己保证它们的大小关系不乱。
//
// 【它为什么在 Infrastructure/】
//   纯静态数学，不依赖 WPF、不做 IO —— 谁都可以用，它不依赖任何人（见《项目结构约定》§二）。
//   注意：**不要在这里 using System.Windows** —— 一旦引入 WPF 类型，它就不能在控制台测了。
// ============================================================
// 【为什么它显式写着 using System;】
//   主工程开了 ImplicitUsings，所以这里不写也能编译。
//   但《项目结构约定》§五 规矩一是"**链接主工程的真文件**"——
//   验证工程（如 .build-verify/UiProbe）有自己的 csproj，**没开 ImplicitUsings**，
//   链接进来就会报 CS0103「当前上下文中不存在名称 Math」。
//   → 被链接的源文件必须**自包含**：需要什么命名空间就自己 using，别指望宿主工程的开关。
//   （2026-09-20 加 UiProbe 时踩到）
using System;

namespace OsuLive2dOverlay;

/// <summary>字号层级。界面上每一处文字都该属于其中一层，不允许"随手写个大小"。</summary>
public enum FontLevel
{
    /// <summary>页面标题（每页顶部）</summary>
    PageTitle,

    /// <summary>卡片标题（分组卡片的抬头）</summary>
    CardTitle,

    /// <summary>正文 —— **基准层**，用户调的就是它</summary>
    Body,

    /// <summary>说明文字（卡片标题下的灰字、设置行的说明）</summary>
    Caption,

    /// <summary>小标签（「重启后生效」这类标记）</summary>
    Label
}

public static class FontScale
{
    /// <summary>pt → DIP 的比例：1pt = 96/72 DIP = 1.3333…</summary>
    public const double DipPerPoint = 96.0 / 72.0;

    /// <summary>基准字号的默认值（pt）—— 与配置 <c>通用.字号</c> 的默认值必须一致</summary>
    public const double DefaultBodyPoints = 10.5;

    /// <summary>基准字号允许的范围（pt）</summary>
    public const double MinBodyPoints = 9.0;
    public const double MaxBodyPoints = 14.0;

    /// <summary>每一层相对基准的倍数。改这里就是改整套界面的字号关系。</summary>
    private static double RatioOf(FontLevel level) => level switch
    {
        FontLevel.PageTitle => 1.35,
        FontLevel.CardTitle => 1.15,
        FontLevel.Body => 1.00,
        FontLevel.Caption => 0.90,
        FontLevel.Label => 0.85,
        _ => 1.00
    };

    /// <summary>
    /// pt → DIP。**所有要交给 FontSize 的值都必须过这个方法**。
    /// </summary>
    public static double PtToDip(double points) => points * DipPerPoint;

    /// <summary>
    /// 给定基准字号（pt），算出某一层该用的 DIP 值。
    ///
    /// **结果会被"吸附"到整数 DIP**（见 <see cref="Snap"/>）—— 这不是洁癖，是渲染质量要求。
    /// </summary>
    /// <param name="basePoints">基准字号（pt），来自配置 <c>通用.字号</c></param>
    /// <param name="level">要算的层级</param>
    public static double Dip(FontLevel level, double basePoints = DefaultBodyPoints)
        => Snap(PtToDip(Clamp(basePoints) * RatioOf(level)));

    /// <summary>
    /// 把算出来的 DIP 吸附到**整数**。
    ///
    /// 【为什么必须做】（2026-09-20 用户反馈"个别小字看起来像错别字"后查出来的）
    ///   层级的乘法必然算出小数：10.5pt 基准下是 18.9 / 16.1 / 14 / 12.6 / 11.9 DIP。
    ///   而 **ClearType 是按像素网格渲染字形的** —— 字号不是整数时，
    ///   字形的竖笔画落不到整像素上，会被抗锯齿"抹平"或与相邻笔画粘连，
    ///   小字号下尤其明显：用户看到的就是"处""置灰"这类字**长得像错别字**。
    ///
    ///   出问题的正好是**最小的两级**（12.6 与 11.9），而正文 14 是整数所以没事 ——
    ///   这个分布本身就说明了原因。
    ///
    /// 【为什么是整数而不是 0.5 的倍数】
    ///   整数能让字形最彻底地落在像素网格上。代价是层级比例会有 ±0.5 DIP 的失真
    ///   （19 / 16 / 14 / 13 / 12，顺序与间距仍然正确），远比"字看起来是错的"划算。
    ///
    /// 连带：`Tokens.Light/Dark.xaml` 里那五个初始值也必须写成整数，
    /// 否则启动瞬间（代码还没重算之前）仍是小数。
    /// </summary>
    private static double Snap(double dip)
        => Math.Round(dip, MidpointRounding.AwayFromZero);

    /// <summary>
    /// 把配置里读来的基准字号夹到合法区间。
    ///
    /// 为什么必须夹：配置是用户拿记事本改的，写个 80 进去界面就废了；
    /// 而解析层是"宽松加载、永不崩"，它不会帮你拦这个。
    /// 非数字（NaN）也在这里兜住 —— 否则 NaN 传进 FontSize 会让整个布局算不出来。
    /// </summary>
    public static double Clamp(double basePoints)
    {
        if (double.IsNaN(basePoints) || double.IsInfinity(basePoints)) return DefaultBodyPoints;
        if (basePoints < MinBodyPoints) return MinBodyPoints;
        if (basePoints > MaxBodyPoints) return MaxBodyPoints;
        return basePoints;
    }
}
