// ============================================================
// WindowPlacementResolver.cs —— 决定"某个界面该把窗口摆在哪"
//
// 【2026-9-19bug解决】
//   屏幕上只有一个悬浮窗，但"窗口摆在哪"是**每个界面一份**的配置。
//   改之前，档位里 X/Y/宽/高 为 null（= 没设过）被解释成"保持窗口现在待的地方"，
//   而"现在待的地方"正是**上一个界面**的位置 —— 于是用户看到：
//     · 只给主菜单设过位置时，选歌/打歌/结算全都显示在主菜单的位置上
//       （看着像"其他三个界面共用同一个位置"）
//     · 在选歌里随手拖一下，这个"借来的"位置就被记成选歌自己的位置，从此固化
//       （"退到主菜单再点到选歌，悬浮窗又被写成主界面的了"）
//   修法：把"没设过"解释成**程序默认摆放**，而不是"上一个界面的位置"。
//
// 【为什么抽成单独一个类】
//   和 PendingWindowAdjustments / ConfigFileWriter 一个道理：这段判断不依赖 WPF、不碰文件，
//   所以能搬出来用控制台测试；而且"哪一层说了算"从此只有这一个出处 ——
//   这类"多来源择优"的逻辑写在窗口类里，早晚会有人（包括我）在别处再写一遍不一样的分支。
//
// 【三层优先级】越靠前越优先：
//   ① pending  —— 运行期刚拖出来的（这一轮还没落盘，见 PendingWindowAdjustments）
//   ② window   —— 配置文件里设过的字段（null = 没设过）
//   ③ fallback —— 程序自己的默认摆放
//
//   逐字段判断（而不是整块挑一份）：X / Y / 宽 / 高 各自都可能"设过 / 没设过"，
//   本该按字段独立取值，没有理由让一个字段的缺失把另外三个也拖下水。
//
//   ① 比 ② 优先，不等于"改了配置也不生效"：落盘发生在退出程序时（用户确认之后），
//   而这一轮里用户眼睛看到的就应该是他自己刚拖的结果。反过来，如果这一轮里
//   切到别的界面再切回来，窗口也得回到他自己拖的那个位置，不能跳回配置文件里的旧值。
// ============================================================
namespace OsuLive2dOverlay;

public static class WindowPlacementResolver
{
    /// <summary>
    /// 算出这个界面最终该用的位置与尺寸。
    /// </summary>
    /// <param name="window">配置文件里这一份档位（字段为 null = 这个值没设过）</param>
    /// <param name="pending">运行期刚拖出来的这一份（这个界面没拖过就是 null）</param>
    /// <param name="fallback">程序默认摆放（四个值都必须是有效值）</param>
    public static WindowBounds Resolve(WindowPlacement? window, WindowBounds? pending, WindowBounds fallback)
    {
        return new WindowBounds(
            Pick(pending?.Left, window?.X, fallback.Left),
            Pick(pending?.Top, window?.Y, fallback.Top),
            Pick(pending?.Width, Positive(window?.Width), fallback.Width),
            Pick(pending?.Height, Positive(window?.Height), fallback.Height));
    }

    /// <summary>三个候选里挑第一个"真的设过"的</summary>
    private static double Pick(double? first, double? second, double fallback)
        => first ?? second ?? fallback;

    /// <summary>
    /// 宽高多一条规则：**小于等于 0 也算没设**。
    /// 0 宽的窗口没有意义（会变成看不见的一条线），与其照着配置做出来，不如当它没写。
    /// </summary>
    private static double? Positive(double? value) => value is { } v && v > 0 ? v : null;
}
