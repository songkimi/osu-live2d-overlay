// ============================================================
// PreviewScale.cs —— 预览区里那个"悬浮窗边框"该画多大
//
// 【它解决什么】（定稿 §8.8 那几条要求里的前两条）
//   用户原话："此时要用某些明显的线来表示悬浮窗就是这么大，
//   人物在悬浮窗就是这么展示的。**没有这条线，用户不知道自己在调什么范围。**"
//   而窗口可能有 3000×2000 —— 面板里既画不下也看不清，
//   所以：**边框按窗口尺寸等比缩放**，并且**把比例标出来**（如 `25%`）。
//
// 【两条刻意的规则】
//   ① **只缩小，不放大。** 一个 200×150 的小窗口，如果放大到填满面板，
//      用户会以为窗口有那么大 —— 预览的意义是"如实反映"，不是"尽量占满"。
//   ② **面板还没布局时（宽或高 ≤ 0）不炸，也不算**：直接按 1:1 给出去，
//      让布局那一趟跑完再来问。面板尺寸是拿 `ActualWidth/Height` 来的，
//      而第一个 `Loaded` 之前它们就是 0 —— 这里必须容得下"还不知道"。
//
// 【它不引 WPF、不碰文件】纯算术 → 能进控制台测（《项目结构约定》§二）。
// ============================================================
namespace OsuLive2dOverlay;

/// <summary>
/// 边框画出来是多大：<paramref name="Scale"/> 是缩放倍数，
/// <paramref name="Width"/>/<paramref name="Height"/> 是缩放后的像素尺寸，
/// <paramref name="Label"/> 是直接显示给用户的比例文字（如 `25%`）。
/// </summary>
public readonly record struct PreviewFrame(double Scale, double Width, double Height, string Label);

public static class PreviewScale
{
    /// <summary>边框四周留的空白（DIP）——贴着面板边缘会看不清框在哪</summary>
    public const double Margin = 12;

    /// <summary>
    /// **适配显示**（默认）：把 `窗口尺寸` 等比缩进 `面板尺寸`（扣掉四周留白）。
    ///
    /// 宽和高各自算一个倍数，**取小的那个** —— 取大的会有一边溢出。
    /// 算出来大于 1 就压成 1（见上面规则 ①）。
    /// </summary>
    public static PreviewFrame Fit(double windowWidth, double windowHeight, double paneWidth, double paneHeight)
    {
        // 窗口尺寸不合法：画不出来，如实给个 0（调用方会显示"还不知道多大"）
        if (!IsUsableSize(windowWidth) || !IsUsableSize(windowHeight))
            return new PreviewFrame(1.0, 0, 0, "-");

        // 面板还没布局 → 按 1:1 给出去，等下一趟布局再问一次
        var usablePaneWidth = paneWidth - Margin * 2;
        var usablePaneHeight = paneHeight - Margin * 2;
        if (usablePaneWidth <= 0 || usablePaneHeight <= 0)
            return Actual(windowWidth, windowHeight);

        var scale = System.Math.Min(usablePaneWidth / windowWidth, usablePaneHeight / windowHeight);

        // 只缩小不放大 —— 这条是刻意的，别改成"填满"（见文件头规则 ①）
        if (scale > 1.0) scale = 1.0;

        return Build(scale, windowWidth, windowHeight);
    }

    /// <summary>
    /// **1:1 显示**：不缩放（超出的部分由外面给滚动条 / 平移看）。
    /// 用户要"看得清细节"时就切到它 —— 比如 1 像素的边框粗细问题。
    /// </summary>
    public static PreviewFrame Actual(double windowWidth, double windowHeight)
    {
        if (!IsUsableSize(windowWidth) || !IsUsableSize(windowHeight))
            return new PreviewFrame(1.0, 0, 0, "-");

        return Build(1.0, windowWidth, windowHeight);
    }

    /// <summary>
    /// 这个窗口尺寸**超过了给定上限**没有（上限通常是显示器工作区）。
    ///
    /// §8.8 第 4 条："超大的悬浮窗要有拦阻" —— 尺寸输入给上限 + 体检警告 +
    /// 提示"这个比例下角色几乎看不见，建议改用站位缩放而不是拉窗口"。
    /// 这里只回答"超没超"，文案归界面。
    /// </summary>
    public static bool Exceeds(double windowWidth, double windowHeight, double limitWidth, double limitHeight)
    {
        if (!IsUsableSize(windowWidth) || !IsUsableSize(windowHeight)) return false;
        if (!IsUsableSize(limitWidth) || !IsUsableSize(limitHeight)) return false;

        return windowWidth > limitWidth || windowHeight > limitHeight;
    }

    private static PreviewFrame Build(double scale, double windowWidth, double windowHeight)
    {
        var width = windowWidth * scale;
        var height = windowHeight * scale;

        // 比例文字：四舍五入到整数百分比。1:1 时正好是 "100%"。
        var label = $"{System.Math.Round(scale * 100)}%";

        return new PreviewFrame(scale, width, height, label);
    }

    /// <summary>正数才算"有效尺寸"（0 和负数都表示"还不知道 / 没设过"）</summary>
    private static bool IsUsableSize(double value) => value > 0;
}
