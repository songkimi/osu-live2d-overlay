// ============================================================
// Win32.cs —— 所有 Win32 互操作只在这一个文件里
//
// 为什么要集中（2026-09-20 整理）：
//   这些声明以前散在 OverlayWindow 里，跟"窗口该显示什么"混在一起。
//   平台细节（DllImport、常量、结构体）属于**基础设施**，界面类不该认识它们 ——
//   集中之后 OverlayWindow 里只剩"什么时候需要穿透"，"怎么设穿透"在这里。
//   附带好处：将来要判断哪些地方不可移植（跑不了 Windows 以外），看这一个文件就够。
//
// 边界：这里只放**操作系统的东西**。应用概念留在调用方 ——
//   比如热键编号（HOTKEY_*）、拖动区尺寸，都不是 Win32 的一部分。
// ============================================================
using System.Runtime.InteropServices;

namespace OsuLive2dOverlay;

internal static class Win32
{
    // ---- 窗口扩展样式（GWL_EXSTYLE 里的位）----
    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;

    // ---- 我们要处理的窗口消息 ----
    internal const int WM_HOTKEY = 0x0312;
    internal const int WM_NCHITTEST = 0x0084;
    internal const int WM_EXITSIZEMOVE = 0x0232;     // 系统原生拖动/缩放结束（= 用户松开鼠标）

    // ---- 命中测试（WM_NCHITTEST）的返回值 ----
    internal const int HTCAPTION = 2;
    internal const int HTBOTTOMRIGHT = 17;

    // ---- 热键的修饰键 / 虚拟键码 ----
    internal const uint MOD_ALT = 0x0001;
    internal const uint MOD_CONTROL = 0x0002;

    /// <summary>
    /// Q —— 「结束悬浮窗」默认热键 `Ctrl+Alt+Q` 里那个 Q。
    ///
    /// 只留了这一个：字母的虚拟键码就是它大写的 ASCII 码，别处要用字母直接写 `'E'` 即可。
    /// （原来还列着 T / R / V / 1~4 —— 那些是旧写死热键与开发期测试热键留下的，
    ///   ★ 2026-09-23 随测试热键一起删掉了。）
    /// </summary>
    internal const uint VK_Q = 0x51;

    /// <summary>
    /// 屏幕上的一个点。**它是物理像素，和 WPF 的 Point（DIP）不是一回事** ——
    /// 用之前必须过一遍 PointFromScreen / PointToScreen 换算，否则高 DPI 下会偏。
    /// </summary>
    [StructLayout(LayoutKind.Sequential)]
    internal struct POINT { public int X; public int Y; }

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hWnd, int nIndex);
    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);
    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);
    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")]
    private static extern bool EnumChildWindows(IntPtr hWndParent, EnumWindowsProc lpEnumFunc, IntPtr lParam);

    /// <summary>
    /// 设置（或清除）一个窗口的点击穿透，并顺手打上"不抢焦点"。
    /// 用位运算只改这两个样式位，其它位原样保留 —— 整份覆盖会把窗口别的行为一起改掉。
    /// </summary>
    internal static void SetClickThrough(IntPtr hwnd, bool clickThrough)
    {
        if (hwnd == IntPtr.Zero) return;

        var exStyle = GetWindowLong(hwnd, GWL_EXSTYLE) | WS_EX_NOACTIVATE;
        exStyle = clickThrough
            ? exStyle | WS_EX_TRANSPARENT
            : exStyle & ~WS_EX_TRANSPARENT;

        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle);
    }

    /// <summary>
    /// 对"顶层窗口 + 它的所有子窗口"依次执行（顶层排第一个）。
    /// 为什么要带上子窗口：WebView2 内部那些原生窗口的 WS_EX_TRANSPARENT 一旦被清除，
    /// WebGL 那层就停止上屏 —— 真机踩过，所以遍历这件事必须交给调用方看得到的地方做。
    /// </summary>
    internal static void ForEachInWindowTree(IntPtr root, Action<IntPtr> visit)
    {
        if (root == IntPtr.Zero) return;

        visit(root);
        EnumChildWindows(root, (h, _) => { visit(h); return true; }, IntPtr.Zero);
    }

    /// <summary>
    /// 读全局光标位置（物理像素）。窗口是点击穿透的，WebView2 收不到鼠标事件，只能这么问系统。
    /// </summary>
    internal static bool TryGetCursorPos(out POINT point) => GetCursorPos(out point);

    /// <summary>
    /// 注册全局热键。**同一个编号只能注册一次** —— 重复注册会返回 false，
    /// 而调用方通常不看返回值，于是症状是"这个热键没反应"，很难查。
    /// </summary>
    internal static bool RegisterHotkey(IntPtr hwnd, int id, uint modifiers, uint vk)
        => RegisterHotKey(hwnd, id, modifiers, vk);

    internal static void UnregisterHotkey(IntPtr hwnd, int id) => UnregisterHotKey(hwnd, id);
}
