// ============================================================
// ResultReaction.cs —— 纯逻辑：结算了，按这一局的准确率挑一条反应
//
// 【为什么单独一个文件】
//   和 ComboTracker / GameStateTracker / TouchReaction / UpdateVersion 一个道理：
//   进一个列表 + 一个数，出一条反应 —— 不碰 WPF、不碰网络、不碰文件，
//   所以能进控制台测（《项目结构约定》§二）。
//
// 【★ 为什么调用方必须只在 state == 7 时调它】
//   抓包实测（2026-09-26，四个界面的包都看过）：
//     menu.state = 0 / 2 / 5  → resultsScreen.accuracy = **0**
//     menu.state = 7          → resultsScreen.accuracy = 91.90 / 94.69（真实值）
//   也就是说**非结算时它是 0，而不是 null** —— 而 0 会稳稳落进最低那一档。
//   这一层挡不住那种误用（0 是个合法值），所以那是**调用方的责任**：
//   只有场景真的变成 Result 的那一次才调这里。
//
//   （顺带：`gameplay.accuracy` 非结算时是 **100**，比 0 更糟 —— 会触发最高档。
//     这就是为什么不用它。）
//
// 【匹配规则】第一个满足 `最小 <= acc < 最大` 的。
//   · **顺序有意义**：多条都匹配时取靠前的（和「常态区间」一致）
//   · `最大` 为 null = 以上不封顶
//   · **acc 为 null → 返回 null**（字段缺失 / 这一包没有它）
//   · **一条都不匹配 → 返回 null**（什么都不做，不兜底、不猜）
// ============================================================
using System.Collections.Generic;

namespace OsuLive2dOverlay;

public static class ResultReaction
{
    /// <summary>
    /// 按准确率挑一条反应。挑不到就返回 <c>null</c>（调用方什么都不做）。
    /// </summary>
    /// <param name="ranges">配置里那一段 `结算反应`</param>
    /// <param name="accuracy">这一局的准确率，**0~100**（tosu 报的就是这个量纲，不是 0~1）</param>
    public static AccuracyRange? Pick(IReadOnlyList<AccuracyRange>? ranges, double? accuracy)
    {
        if (ranges == null || ranges.Count == 0 || accuracy is null) return null;
        var accReaction = ranges.FirstOrDefault(a => a.Min <= accuracy && (a.Max ?? double.PositiveInfinity) > accuracy);
        return accReaction;
    }
}
